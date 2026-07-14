using System.Runtime.Versioning;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.Agent.Collection.Local;

/// <summary>
/// Collects IPv4 and IPv6 routing table entries from the local host.
/// Linux  : parses `ip -4 route` and `ip -6 route`.
/// macOS  : parses `netstat -rn -f inet` and `netstat -rn -f inet6`.
/// Windows: uses PowerShell Get-NetRoute via CollectorHelper.RunPsJsonAsync.
/// Fact keys: Device[{deviceId}].Route[{destination}].{Gateway|Interface|Metric|Family}
/// </summary>
public sealed class RouteCollector : OsDispatchLocalCollector
{
    public override string Name => "routes";

    // ── Linux ─────────────────────────────────────────────────────────────────

    protected override async Task CollectLinuxAsync(string deviceId, List<Fact> facts, CancellationToken ct)
    {
        string ipv4 = await CollectorHelper.RunAsync("ip", "-4 route", ct);
        ParseIpRoute(deviceId, ipv4, "inet", facts);

        string ipv6 = await CollectorHelper.RunAsync("ip", "-6 route", ct);
        ParseIpRoute(deviceId, ipv6, "inet6", facts);
    }

    /// <summary>
    /// Parses `ip -4 route` / `ip -6 route` output.
    /// Example lines:
    /// default via 192.168.1.1 dev eth0 proto dhcp metric 100
    /// 192.168.1.0/24 dev eth0 proto kernel scope link src 192.168.1.50
    /// </summary>
    private static void ParseIpRoute(string deviceId, string output, string family, List<Fact> facts)
    {
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] tokens = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 1)
            {
                continue;
            }

            // Normalize "default" to the appropriate default route CIDR
            string destination = tokens[0] switch
            {
                "default" when family == "inet" => "0.0.0.0/0",
                "default" when family == "inet6" => "::/0",
                _ => tokens[0],
            };

            // Skip local/broadcast non-routing entries without a slash (pure addresses)
            // but allow valid CIDRs including host routes (e.g. 192.168.1.1/32)
            if (destination != "0.0.0.0/0"
             && destination != "::/0"
             && !destination.Contains('/')
             && !destination.Contains(':'))
            {
                continue;
            }

            string? gateway = null, iface = null, proto = null, src = null, scope = null;
            int metric = 0;

            for (int i = 1; i < tokens.Length - 1; i++)
            {
                switch (tokens[i])
                {
                    case "via": gateway = tokens[++i]; break;
                    case "dev": iface = tokens[++i]; break;
                    case "metric": _ = int.TryParse(tokens[++i], out metric); break;
                    case "proto": proto = tokens[++i]; break;
                    case "src": src = tokens[++i]; break;
                    case "scope": scope = tokens[++i]; break;
                }
            }

            string[] keys = [deviceId, destination];
            facts.Add(Fact.Create(FactPaths.RouteFamily, keys, family == "inet" ? "IPv4" : "IPv6"));
            facts.AddIfPresent(FactPaths.RouteGateway, keys, gateway);
            facts.AddIfPresent(FactPaths.RouteInterface, keys, iface);
            facts.Add(Fact.Create(FactPaths.RouteMetric, keys, metric));
            facts.AddIfPresent(FactPaths.RouteProto, keys, proto);
            facts.AddIfPresent(FactPaths.RouteSource, keys, src);
            facts.AddIfPresent(FactPaths.RouteScope, keys, scope);
        }
    }

    // ── macOS ─────────────────────────────────────────────────────────────────

    protected override async Task CollectMacOsAsync(string deviceId, List<Fact> facts, CancellationToken ct)
    {
        string ipv4 = await CollectorHelper.RunAsync("netstat", "-rn -f inet", ct);
        ParseNetstat(deviceId, ipv4, "IPv4", facts);

        string ipv6 = await CollectorHelper.RunAsync("netstat", "-rn -f inet6", ct);
        ParseNetstat(deviceId, ipv6, "IPv6", facts);
    }

    /// <summary>
    /// Parses `netstat -rn -f inet[6]` output.
    /// Header looks like: Destination  Gateway  Flags  Refs  Use  Netif  Expire
    /// Data lines start after the header separator.
    /// </summary>
    private static void ParseNetstat(string deviceId, string output, string family, List<Fact> facts)
    {
        bool pastHeader = false;
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();

            // Skip header lines
            if (!pastHeader)
            {
                if (trimmed.StartsWith("Destination", StringComparison.OrdinalIgnoreCase))
                {
                    pastHeader = true;
                }

                continue;
            }

            string[] tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                continue;
            }

            string destination = tokens[0];

            // Skip loopback-only entries (lo0 as sole interface with no useful gateway)
            if (tokens.Length >= 6 && tokens[5] == "lo0" && !tokens[2].Contains('G'))
            {
                continue;
            }

            // Normalize default
            if (destination.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                destination = family == "IPv4" ? "0.0.0.0/0" : "::/0";
            }

            string gateway = tokens[1];

            // Skip link-layer gateway entries (they show the MAC, not a usable gateway)
            if (gateway.StartsWith("link#", StringComparison.OrdinalIgnoreCase))
            {
                gateway = "";
            }

            string iface = tokens.Length >= 6 ? tokens[5] : "";

            string[] keys = [deviceId, destination];
            facts.Add(Fact.Create(FactPaths.RouteFamily, keys, family));
            if (gateway.Length > 0)
            {
                facts.Add(Fact.Create(FactPaths.RouteGateway, keys, gateway));
            }

            if (iface.Length > 0)
            {
                facts.Add(Fact.Create(FactPaths.RouteInterface, keys, iface));
            }

            facts.Add(Fact.Create(FactPaths.RouteMetric, keys, 0L));
        }
    }

    // ── Windows ───────────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    protected override async Task CollectWindowsAsync(string deviceId, List<Fact> facts, CancellationToken ct)
    {
        const string script = """
            Get-NetRoute -ErrorAction SilentlyContinue |
            Select-Object @{n='Family';e={$_.AddressFamily.ToString()}},DestinationPrefix,NextHop,InterfaceAlias,RouteMetric |
            ConvertTo-Json -Compress
            """;

        List<WinRoute> routes = await CollectorHelper.RunPsJsonAsync<WinRoute>(script, ct);
        foreach (WinRoute r in routes)
        {
            if (string.IsNullOrWhiteSpace(r.DestinationPrefix))
            {
                continue;
            }

            string family = r.Family switch
            {
                "InterNetwork" => "IPv4",
                "InterNetworkV6" => "IPv6",
                _ => r.Family ?? "Unknown",
            };

            string[] keys = [deviceId, r.DestinationPrefix];
            facts.Add(Fact.Create(FactPaths.RouteFamily, keys, family));
            facts.AddIfPresent(FactPaths.RouteGateway, keys, r.NextHop);
            facts.AddIfPresent(FactPaths.RouteInterface, keys, r.InterfaceAlias);
            facts.Add(Fact.Create(FactPaths.RouteMetric, keys, r.RouteMetric ?? 0));
        }
    }

    private sealed class WinRoute
    {
        public string? Family { get; set; }
        public string? DestinationPrefix { get; set; }
        public string? NextHop { get; set; }
        public string? InterfaceAlias { get; set; }
        public int? RouteMetric { get; set; }
    }
}