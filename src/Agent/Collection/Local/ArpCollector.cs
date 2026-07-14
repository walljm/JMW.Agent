using System.Runtime.Versioning;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.Agent.Collection.Local;

/// <summary>
/// Collects the ARP / neighbor cache from the local host.
/// Linux  : parses `ip neigh`
/// macOS  : parses `arp -a`
/// Windows: uses PowerShell Get-NetNeighbor via CollectorHelper.RunPsJsonAsync.
/// MACs are normalized to lowercase colon-separated (00:11:22:33:44:55).
/// Fact keys: Device[{deviceId}].ARP[{ip}].{MAC|Interface|State}
/// Warms the OS neighbor cache (NeighborCacheWarmer) before every read — otherwise the cache
/// only holds entries for hosts the OS happened to talk to recently, so most hosts on the LAN
/// read back with no MAC.
/// </summary>
public sealed class ArpCollector : OsDispatchLocalCollector
{
    public override string Name => "arp";

    // ── Linux ─────────────────────────────────────────────────────────────────

    protected override async Task CollectLinuxAsync(string deviceId, List<Fact> facts, CancellationToken ct)
    {
        await NeighborCacheWarmer.WarmAsync(ct);

        // Example: 192.168.1.1 dev eth0 lladdr 00:11:22:33:44:55 REACHABLE
        string output = await CollectorHelper.RunAsync("ip", "neigh", ct);
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] tokens = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 5)
            {
                continue;
            }

            string ip = tokens[0];

            // Find lladdr and dev via token scanning
            string? mac = null, iface = null, state = null;
            for (int i = 1; i < tokens.Length - 1; i++)
            {
                switch (tokens[i])
                {
                    case "lladdr": mac = tokens[++i]; break;
                    case "dev": iface = tokens[++i]; break;
                }
            }

            // Skip incomplete entries (no MAC)
            if (mac is null)
            {
                continue;
            }

            // State is typically the last token
            state = tokens[^1];

            // Validate state is a known neighbor state, not some other trailing token
            state = state switch
            {
                "REACHABLE" or "STALE" or "DELAY" or "PROBE" or "FAILED" or "PERMANENT" or "NOARP" => state,
                _ => "UNKNOWN",
            };

            EmitArpFacts(deviceId, ip, mac, iface, state, facts);
        }
    }

    // ── macOS ─────────────────────────────────────────────────────────────────

    protected override async Task CollectMacOsAsync(string deviceId, List<Fact> facts, CancellationToken ct)
    {
        await NeighborCacheWarmer.WarmAsync(ct);

        // Example: ? (192.168.1.1) at 0:11:22:33:44:55 on en0 ifscope [ethernet]
        string output = await CollectorHelper.RunAsync("arp", "-a", ct);
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();

            // Skip incomplete entries
            if (trimmed.Contains("incomplete", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Extract IP from between parentheses
            int ipStart = trimmed.IndexOf('(');
            int ipEnd = trimmed.IndexOf(')');
            if (ipStart < 0 || ipEnd < 0 || ipEnd <= ipStart + 1)
            {
                continue;
            }

            string ip = trimmed[(ipStart + 1)..ipEnd];

            // Extract MAC after " at "
            int atIdx = trimmed.IndexOf(" at ", StringComparison.OrdinalIgnoreCase);
            if (atIdx < 0)
            {
                continue;
            }

            string rest = trimmed[(atIdx + 4)..].Trim();
            string[] parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1)
            {
                continue;
            }

            string mac = parts[0];
            string? iface = null;

            // Find interface after "on"
            int onIdx = Array.IndexOf(parts, "on");
            if (onIdx >= 0 && onIdx + 1 < parts.Length)
            {
                iface = parts[onIdx + 1];
            }

            EmitArpFacts(deviceId, ip, mac, iface, "REACHABLE", facts);
        }
    }

    // ── Windows ───────────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    protected override async Task CollectWindowsAsync(string deviceId, List<Fact> facts, CancellationToken ct)
    {
        await NeighborCacheWarmer.WarmAsync(ct);

        const string script = """
            Get-NetNeighbor -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Select-Object IPAddress,LinkLayerAddress,InterfaceAlias,State |
            ConvertTo-Json -Compress
            """;

        List<WinNeighbor> entries = await CollectorHelper.RunPsJsonAsync<WinNeighbor>(script, ct);
        foreach (WinNeighbor e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.IpAddress) || string.IsNullOrWhiteSpace(e.LinkLayerAddress))
            {
                continue;
            }

            EmitArpFacts(
                deviceId,
                e.IpAddress,
                e.LinkLayerAddress,
                e.InterfaceAlias,
                e.State ?? "UNKNOWN",
                facts
            );
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void EmitArpFacts(
        string deviceId,
        string ip,
        string mac,
        string? iface,
        string state,
        List<Fact> facts
    )
    {
        string[] keys = [deviceId, ip];
        facts.Add(Fact.Create(FactPaths.ArpMac, keys, mac));
        facts.Add(Fact.Create(FactPaths.ArpState, keys, state));
        facts.AddIfPresent(FactPaths.ArpInterface, keys, iface);
    }

    private sealed class WinNeighbor
    {
        public string? IpAddress { get; set; }
        public string? LinkLayerAddress { get; set; }
        public string? InterfaceAlias { get; set; }
        public string? State { get; set; }
    }
}