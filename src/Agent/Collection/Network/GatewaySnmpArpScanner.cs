using System.Net;
using System.Runtime.Versioning;

using JMW.Discovery.Core;

using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers devices by walking the default gateway's SNMP ARP table
/// (ipNetToMediaTable). Useful in multi-VLAN environments where the agent
/// cannot see L2 traffic from remote subnets directly.
/// Uses the SNMPv2c community string "public" by default. For gateways
/// that require a different community, configure an explicit SNMP target
/// in agent.json so that SnmpCollector picks it up instead.
/// Source tag: "gateway-arp".
/// </summary>
public sealed class GatewaySnmpArpScanner : INetworkScanner
{
    private static readonly ObjectIdentifier IpNetToMediaPhysAddr = new("1.3.6.1.2.1.4.22.1.2");
    private static readonly ObjectIdentifier IpNetToMediaType = new("1.3.6.1.2.1.4.22.1.4");

    public string Name => "gateway-arp";
    public bool IsSupported => true;

    public async Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(NetworkScanTarget target, CancellationToken ct)
    {
        string? gatewayIp = await DetectGatewayAsync(target, ct);
        if (gatewayIp is null)
        {
            return [];
        }

        return await WalkGatewayArpAsync(gatewayIp, target.LocalAddress.ToString(), ct);
    }

    // ── Gateway detection ─────────────────────────────────────────────────────

    private static async Task<string?> DetectGatewayAsync(NetworkScanTarget target, CancellationToken ct)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return await DetectLinuxGatewayAsync(target, ct);
            }

            if (OperatingSystem.IsMacOS())
            {
                return await DetectMacOsGatewayAsync(ct);
            }

            if (OperatingSystem.IsWindows())
            {
                return await DetectWindowsGatewayAsync(ct);
            }
        }
        catch { }

        return null;
    }

    private static async Task<string?> DetectLinuxGatewayAsync(NetworkScanTarget target, CancellationToken ct)
    {
        // Parse "ip route show" for the default route on the matching interface.
        // Example: "default via 192.168.1.1 dev eth0 proto dhcp ..."
        string output = await CollectorHelper.RunAsync("ip", "route show", ct);
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] tokens = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3)
            {
                continue;
            }

            if (!tokens[0].Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? via = null;
            string? dev = null;
            for (int i = 1; i < tokens.Length - 1; i++)
            {
                if (tokens[i] == "via")
                {
                    via = tokens[i + 1];
                }
                else if (tokens[i] == "dev")
                {
                    dev = tokens[i + 1];
                }
            }

            if (via is null)
            {
                continue;
            }

            // Prefer the route on the same interface as the scan target.
            if (target.InterfaceName is not null
             && dev is not null
             && !dev.Equals(target.InterfaceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IPAddress.TryParse(via, out _))
            {
                return via;
            }
        }

        return null;
    }

    private static async Task<string?> DetectMacOsGatewayAsync(CancellationToken ct)
    {
        // "route -n get default" emits "gateway: X.X.X.X"
        string output = await CollectorHelper.RunAsync("route", "-n get default", ct);
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("gateway:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string candidate = trimmed["gateway:".Length..].Trim();
            if (IPAddress.TryParse(candidate, out _))
            {
                return candidate;
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<string?> DetectWindowsGatewayAsync(CancellationToken ct)
    {
        const string script =
            "Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | Select-Object -First 1 -ExpandProperty NextHop";
        string output = (await CollectorHelper.RunPsAsync(script, ct)).Trim();
        return IPAddress.TryParse(output, out _) ? output : null;
    }

    // ── SNMP ARP walk ─────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<DiscoveredDevice>> WalkGatewayArpAsync(
        string gatewayIp,
        string localIp,
        CancellationToken ct
    )
    {
        if (!IPAddress.TryParse(gatewayIp, out IPAddress? gwAddr))
        {
            return [];
        }

        IPEndPoint endpoint = new(gwAddr, 161);
        OctetString community = new("public");
        int timeout = 5000;

        List<Variable> physResult = [];
        List<Variable> typeResult = [];

        try
        {
            await Task.Run(
                () => Messenger.Walk(
                    VersionCode.V2,
                    endpoint,
                    community,
                    IpNetToMediaPhysAddr,
                    physResult,
                    timeout,
                    WalkMode.WithinSubtree
                ),
                ct
            );
            await Task.Run(
                () => Messenger.Walk(
                    VersionCode.V2,
                    endpoint,
                    community,
                    IpNetToMediaType,
                    typeResult,
                    timeout,
                    WalkMode.WithinSubtree
                ),
                ct
            );
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return [];
        }

        // Build type lookup by OID suffix (ifIndex.a.b.c.d)
        string typePrefix = IpNetToMediaType + ".";
        Dictionary<string, int> typeByKey = new(StringComparer.Ordinal);
        foreach (Variable v in typeResult)
        {
            string oid = v.Id.ToString();
            if (oid.StartsWith(typePrefix, StringComparison.Ordinal) && int.TryParse(v.Data.ToString(), out int t))
            {
                typeByKey[oid[typePrefix.Length..]] = t;
            }
        }

        string physPrefix = IpNetToMediaPhysAddr + ".";
        List<DiscoveredDevice> devices = [];

        foreach (Variable v in physResult)
        {
            string oid = v.Id.ToString();
            if (!oid.StartsWith(physPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string suffix = oid[physPrefix.Length..]; // "{ifIndex}.{a}.{b}.{c}.{d}"

            // Type 2 = invalid; skip stale entries.
            if (typeByKey.TryGetValue(suffix, out int entryType) && entryType == 2)
            {
                continue;
            }

            // Extract IP from last 4 components of suffix.
            int firstDot = suffix.IndexOf('.', StringComparison.Ordinal);
            if (firstDot < 0 || firstDot + 1 >= suffix.Length)
            {
                continue;
            }

            string ipStr = suffix[(firstDot + 1)..];
            if (!IPAddress.TryParse(ipStr, out _))
            {
                continue;
            }

            // Skip the gateway itself and the local agent.
            if (ipStr == gatewayIp || ipStr == localIp)
            {
                continue;
            }

            if (v.Data is not OctetString octet)
            {
                continue;
            }

            byte[] bytes = octet.GetRaw();
            if (bytes.Length != 6 || bytes.All(b => b == 0))
            {
                continue;
            }

            devices.Add(
                new DiscoveredDevice
                {
                    IpAddress = ipStr,
                    MacAddress = MacFormat.FromBytes(bytes),
                    Source = "gateway-arp",
                }
            );
        }

        return devices;
    }
}