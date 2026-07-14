using System.Net.Sockets;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

using Microsoft.Extensions.Logging;

using Renci.SshNet;
using Renci.SshNet.Common;

namespace JMW.Discovery.Agent.Collection.Device;

public sealed class SshCollector : IDeviceCollector, IDisposable
{
    private readonly ILogger<SshCollector> _logger = AgentLog.CreateLogger<SshCollector>();
    private SshClient? _client;

    public string CollectorType => "ssh";

    public bool CanCollect(Target target) =>
        target.CollectorType == null || target.CollectorType.Equals("ssh", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<Fact>> CollectAsync(
        Target target,
        ICollectionContext context,
        CancellationToken ct
    )
    {
        int port = target.Properties.GetInt("port", 22);

        AuthenticationMethod[] authMethods;
        string username;

        if (target.Credentials is SshCredentials ssh)
        {
            username = ssh.Username;
            if (ssh.Password != null)
            {
                authMethods = [new PasswordAuthenticationMethod(username, ssh.Password)];
            }
            else if (ssh.KeyFile != null)
            {
                authMethods = [new PrivateKeyAuthenticationMethod(username, new PrivateKeyFile(ssh.KeyFile))];
            }
            else
            {
                authMethods = [new PasswordAuthenticationMethod(username, "")];
            }
        }
        else if (target.Credentials == null)
        {
            username = Environment.UserName;
            authMethods = [new PasswordAuthenticationMethod(username, "")];
        }
        else
        {
            string credType = target.Credentials.GetType().Name;
            SshCollectorLog.UnsupportedCredentials(_logger, credType, target.Endpoint);
            return [];
        }

        ConnectionInfo connInfo = new(target.Endpoint, port, username, authMethods);
        _client = new SshClient(connInfo);

        try
        {
            await _client.ConnectAsync(ct);
        }
        catch (Exception ex) when (ex is SshAuthenticationException or SshConnectionException or SocketException)
        {
            SshCollectorLog.ConnectionFailed(_logger, target.Endpoint, port, ex);
            return [];
        }

        List<Fingerprint> fingerprints = [];

        string hostname = Run("hostname").Trim();
        if (hostname.Length > 0)
        {
            fingerprints.Add(
                new Fingerprint(
                    FingerprintType.SshHostKey,
                    $"{connInfo.CurrentKeyExchangeAlgorithm}:{_client.ConnectionInfo.ServerVersion}".ToLowerInvariant()
                )
            );
            fingerprints.Add(new Fingerprint(FingerprintType.MachineId, hostname));
        }

        string sshHostKeyRaw = _client.ConnectionInfo.ServerVersion ?? "";
        if (sshHostKeyRaw.Length > 0 && fingerprints.All(f => f.Type != FingerprintType.SshHostKey))
        {
            fingerprints.Add(new Fingerprint(FingerprintType.SshHostKey, sshHostKeyRaw.ToLowerInvariant()));
        }

        string machineId = Run(
                "cat /etc/machine-id 2>/dev/null || cat /var/lib/dbus/machine-id 2>/dev/null"
            )
            .Trim();
        if (machineId.Length > 0)
        {
            fingerprints.Add(new Fingerprint(FingerprintType.MachineId, machineId.ToLowerInvariant()));
        }

        bool isLinux = Run("uname -a").Contains("Linux", StringComparison.OrdinalIgnoreCase);

        DeviceIdentity identity = new(
            Fingerprints: fingerprints,
            Kind: "host",
            Vendor: null,
            OsFamily: isLinux ? "linux" : null,
            OsVersion: null
        );

        string deviceId = await context.RegisterProbeAsync(identity, ct);

        List<Fact> facts = [];

        string kernel = Run("uname -r").Trim();
        if (kernel.Length > 0)
        {
            facts.Add(Fact.Create(FactPaths.SystemKernel, [deviceId], kernel));
        }

        if (isLinux)
        {
            CollectLinuxOsFacts(deviceId, facts);
        }

        string fqdn = Run("hostname -f 2>/dev/null || hostname").Trim();
        if (fqdn.Length > 0)
        {
            facts.Add(Fact.Create(FactPaths.SystemHostname, [deviceId], fqdn));
        }

        if (isLinux)
        {
            CollectNetworkFacts(deviceId, facts);
            CollectFilesystemFacts(deviceId, facts);
            CollectMemoryFacts(deviceId, facts);
            CollectBridgeVlanFacts(deviceId, facts);
        }

        string ncpuStr = Run("nproc 2>/dev/null").Trim();
        if (long.TryParse(ncpuStr, out long ncpu) && ncpu > 0)
        {
            facts.Add(Fact.Create(FactPaths.SshCpuCount, [deviceId], ncpu));
        }

        _client.Disconnect();

        return facts;
    }

    private void CollectLinuxOsFacts(string deviceId, List<Fact> facts)
    {
        string osRelease = Run("cat /etc/os-release 2>/dev/null").Trim();
        if (osRelease.Length == 0)
        {
            return;
        }

        Dictionary<string, string> kv = KeyValueParser.ParseEqualsKeyValue(osRelease.Split('\n'));
        if (kv.TryGetValue("NAME", out string? distro))
        {
            facts.Add(Fact.Create(FactPaths.SystemOsDistro, [deviceId], distro));
        }

        if (kv.TryGetValue("VERSION", out string? version))
        {
            facts.Add(Fact.Create(FactPaths.SystemOsVersion, [deviceId], version));
        }
        else if (kv.TryGetValue("VERSION_ID", out string? versionId))
        {
            facts.Add(Fact.Create(FactPaths.SystemOsVersion, [deviceId], versionId));
        }
    }

    private void CollectNetworkFacts(string deviceId, List<Fact> facts)
    {
        string ipAddrOutput = Run("ip addr show 2>/dev/null").Trim();
        if (ipAddrOutput.Length == 0)
        {
            return;
        }

        string? currentIface = null;
        foreach (string rawLine in ipAddrOutput.Split('\n'))
        {
            string line = rawLine.TrimEnd();

            // Interface header line: "2: eth0: <flags>"
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                int colon = line.IndexOf(':');
                if (colon >= 0)
                {
                    int secondColon = line.IndexOf(':', colon + 1);
                    if (secondColon > colon)
                    {
                        currentIface = line[(colon + 1)..secondColon].Trim();
                    }
                }

                continue;
            }

            if (currentIface == null)
            {
                continue;
            }

            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("link/ether ", StringComparison.Ordinal))
            {
                string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    string mac = parts[1].Replace(":", "").ToLowerInvariant();
                    facts.Add(Fact.Create(FactPaths.InterfaceMAC, [deviceId, currentIface], mac));
                }
            }
            else if (trimmed.StartsWith("inet ", StringComparison.Ordinal))
            {
                string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    string ip = parts[1].Split('/')[0];
                    facts.Add(Fact.Create(FactPaths.SshInterfaceIP, [deviceId, currentIface], ip));
                }
            }
        }
    }

    private void CollectFilesystemFacts(string deviceId, List<Fact> facts)
    {
        string dfOutput = Run(
                "df -h --output=source,size,used,pcent,target 2>/dev/null | tail -n +2"
            )
            .Trim();

        if (dfOutput.Length == 0)
        {
            return;
        }

        foreach (string rawLine in dfOutput.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
            {
                continue;
            }

            string mountTarget = parts[4];
            facts.Add(Fact.Create(FactPaths.SshFsSize, [deviceId, mountTarget], parts[1]));
            facts.Add(Fact.Create(FactPaths.SshFsUsed, [deviceId, mountTarget], parts[2]));
            facts.Add(Fact.Create(FactPaths.SshFsUsePercent, [deviceId, mountTarget], parts[3]));
        }
    }

    private void CollectMemoryFacts(string deviceId, List<Fact> facts)
    {
        string freeOutput = Run("free -m | awk 'NR==2{print $2,$3}'").Trim();
        if (freeOutput.Length == 0)
        {
            return;
        }

        string[] parts = freeOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 1 && long.TryParse(parts[0], out long totalMb))
        {
            facts.Add(Fact.Create(FactPaths.SshMemTotalMB, [deviceId], totalMb));
        }

        if (parts.Length >= 2 && long.TryParse(parts[1], out long usedMb))
        {
            facts.Add(Fact.Create(FactPaths.SshMemUsedMB, [deviceId], usedMb));
        }
    }

    // ── Bridge/VLAN/STP (docs/plans/d3-l2-l3.md) ──────────────────────────────

    private void CollectBridgeVlanFacts(string deviceId, List<Fact> facts)
    {
        string ipLinkOutput = Run("ip -d link show 2>/dev/null").Trim();
        if (ipLinkOutput.Length > 0)
        {
            foreach (IpLinkBridgeVlanInfo info in ParseIpDLinkShow(ipLinkOutput))
            {
                facts.AddIfPresent(FactPaths.InterfaceBridgeMaster, [deviceId, info.Interface], info.BridgeMaster);
                if (info.VlanId is { } vlanId)
                {
                    facts.Add(Fact.Create(FactPaths.InterfaceVlanId, [deviceId, info.Interface], vlanId));
                }

                facts.AddIfPresent(FactPaths.InterfaceStpState, [deviceId, info.Interface], info.StpState);
            }
        }

        // Only present when the kernel bridge has VLAN filtering enabled (bridge vlan add ...);
        // degrades to "" per the Run() convention on hosts without it.
        string bridgeVlanOutput = Run("bridge vlan show 2>/dev/null").Trim();
        if (bridgeVlanOutput.Length == 0)
        {
            return;
        }

        Dictionary<string, List<int>> taggedByPort = new(StringComparer.Ordinal);
        foreach (BridgeVlanEntry entry in ParseBridgeVlanShow(bridgeVlanOutput))
        {
            if (entry.IsPvid)
            {
                // Reconciled with (may supersede) any 802.1Q sub-interface VlanId already
                // emitted above — both represent the same InterfaceVlanId concept per the
                // shared fact-path convention (see FactPaths.cs L2 section).
                facts.Add(Fact.Create(FactPaths.InterfaceVlanId, [deviceId, entry.Port], entry.VlanId));
                continue;
            }

            if (!taggedByPort.TryGetValue(entry.Port, out List<int>? vlans))
            {
                vlans = [];
                taggedByPort[entry.Port] = vlans;
            }

            vlans.Add(entry.VlanId);
        }

        foreach ((string port, List<int> vlans) in taggedByPort)
        {
            facts.Add(
                Fact.Create(FactPaths.InterfaceTaggedVlans, [deviceId, port], string.Join(",", vlans.OrderBy(v => v)))
            );
        }
    }

    private sealed record IpLinkBridgeVlanInfo(string Interface, string? BridgeMaster, int? VlanId, string? StpState);

    /// <summary>
    /// Parses <c>ip -d link show</c>:
    /// <code>
    /// 2: eth0: &lt;BROADCAST,MULTICAST,UP,LOWER_UP&gt; mtu 1500 qdisc pfifo_fast master br0 state UP ...
    ///     link/ether 00:11:22:33:44:55 brd ff:ff:ff:ff:ff:ff promiscuity 1 ...
    ///     bridge_slave state forwarding priority 32 cost 4 ...
    /// 4: eth0.10@eth0: &lt;BROADCAST,MULTICAST,UP,LOWER_UP&gt; mtu 1500 qdisc noqueue state UP ...
    ///     link/ether 00:11:22:33:44:55 brd ff:ff:ff:ff:ff:ff
    ///     vlan protocol 802.1Q id 10 &lt;REORDER_HDR&gt;
    /// </code>
    /// "master BRIDGE" on the header line names the bridge this port belongs to; a
    /// "bridge_slave state X" detail line carries per-port STP state directly (no separate
    /// sysfs read needed); a "vlan ... id N" detail line is an 802.1Q sub-interface's VLAN ID.
    /// </summary>
    private static List<IpLinkBridgeVlanInfo> ParseIpDLinkShow(string output)
    {
        List<IpLinkBridgeVlanInfo> result = [];

        string? name = null;
        string? master = null;
        int? vlanId = null;
        string? stpState = null;

        void Flush()
        {
            if (name is not null)
            {
                result.Add(new IpLinkBridgeVlanInfo(name, master, vlanId, stpState));
            }

            name = null;
            master = null;
            vlanId = null;
            stpState = null;
        }

        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd();
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (line.Length > 0 && char.IsDigit(line[0]) && IsIpLinkHeaderLine(trimmed))
            {
                Flush();
                ParseIpLinkHeader(trimmed, out name, out master);
                continue;
            }

            if (name is null)
            {
                continue;
            }

            string[] tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                continue;
            }

            if (string.Equals(tokens[0], "vlan", StringComparison.Ordinal))
            {
                int idIdx = Array.IndexOf(tokens, "id");
                if (idIdx >= 0 && idIdx + 1 < tokens.Length && int.TryParse(tokens[idIdx + 1], out int id))
                {
                    vlanId = id;
                }
            }
            else if (string.Equals(tokens[0], "bridge_slave", StringComparison.Ordinal))
            {
                int stateIdx = Array.IndexOf(tokens, "state");
                if (stateIdx >= 0 && stateIdx + 1 < tokens.Length)
                {
                    stpState = tokens[stateIdx + 1];
                }
            }
        }

        Flush();
        return result;
    }

    // "N: name[@parent]: <FLAGS> ..." — same shape as OnHubApInterfaces' header parsing.
    private static bool IsIpLinkHeaderLine(string trimmed)
    {
        int colon = trimmed.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        for (int i = 0; i < colon; i++)
        {
            if (!char.IsDigit(trimmed[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static void ParseIpLinkHeader(string line, out string? name, out string? master)
    {
        name = null;
        master = null;

        int firstColon = line.IndexOf(':');
        int secondColon = line.IndexOf(':', firstColon + 1);
        if (secondColon < 0)
        {
            return;
        }

        string rawName = line[(firstColon + 1)..secondColon].Trim();
        int at = rawName.IndexOf('@');
        name = at >= 0 ? rawName[..at] : rawName;
        if (name.Length == 0)
        {
            name = null;
            return;
        }

        string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length - 1; i++)
        {
            if (string.Equals(tokens[i], "master", StringComparison.Ordinal))
            {
                master = tokens[i + 1];
                break;
            }
        }
    }

    private sealed record BridgeVlanEntry(string Port, int VlanId, bool IsPvid);

    /// <summary>
    /// Parses <c>bridge vlan show</c>:
    /// <code>
    /// port              vlan-id
    /// eth0              1 PVID Egress Untagged
    ///                   10
    ///                   20
    /// br0               1 PVID Egress Untagged
    /// </code>
    /// A port's first row carries its name; further VLAN rows for the same port are indented
    /// continuations with a blank port column. The "PVID" flag marks the port's native/access
    /// VLAN (→ InterfaceVlanId); every other row is trunk/tagged membership (→ InterfaceTaggedVlans).
    /// </summary>
    private static List<BridgeVlanEntry> ParseBridgeVlanShow(string output)
    {
        List<BridgeVlanEntry> result = [];
        string? currentPort = null;

        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd();
            if (line.Trim().Length == 0)
            {
                continue;
            }

            bool continuation = char.IsWhiteSpace(line[0]);
            string[] tokens = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                continue;
            }

            int vlanTokenIdx = 0;
            if (!continuation)
            {
                // Header row: "port  vlan-id" — never a real port name (second token names a column).
                if (string.Equals(tokens[0], "port", StringComparison.Ordinal)
                 && tokens.Length >= 2
                 && tokens[1].Contains("vlan", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                currentPort = tokens[0];
                vlanTokenIdx = 1;
            }

            if (currentPort is null || vlanTokenIdx >= tokens.Length)
            {
                continue;
            }

            if (!int.TryParse(tokens[vlanTokenIdx], out int vlanId))
            {
                continue;
            }

            bool isPvid = tokens.Skip(vlanTokenIdx + 1).Any(t => string.Equals(t, "PVID", StringComparison.Ordinal));
            result.Add(new BridgeVlanEntry(currentPort, vlanId, isPvid));
        }

        return result;
    }

    private string Run(string command)
    {
        if (_client == null || !_client.IsConnected)
        {
            return "";
        }

        try
        {
            using SshCommand cmd = _client.RunCommand(command);
            return cmd.Result;
        }
        catch (Exception ex)
        {
            SshCollectorLog.CommandFailed(_logger, command, ex);
            return "";
        }
    }


    public void Dispose()
    {
        if (_client != null)
        {
            if (_client.IsConnected)
            {
                _client.Disconnect();
            }

            _client.Dispose();
            _client = null;
        }
    }
}

internal static partial class SshCollectorLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "SSH unsupported credentials type {CredType} for {Address}.")]
    internal static partial void UnsupportedCredentials(ILogger logger, string credType, string address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SSH connection to {Address}:{Port} failed.")]
    internal static partial void ConnectionFailed(ILogger logger, string address, int port, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SSH command '{Command}' failed.")]
    internal static partial void CommandFailed(ILogger logger, string command, Exception ex);
}