using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.Agent.Collection.Local;

public sealed class DhcpLeaseCollector : ILocalCollector
{
    private static readonly (string Path, string Source)[] LeaseFiles =
    [
        ("/var/lib/misc/dnsmasq.leases", "dnsmasq"),
        ("/var/lib/dnsmasq/dnsmasq.leases", "dnsmasq"),
        ("/var/lib/dhcpd/dhcpd.leases", "isc-dhcpd"),
        ("/var/lib/dhcp/dhcpd.leases", "isc-dhcpd"),
        ("/var/lib/kea/dhcp4.leases", "kea"),
        ("/tmp/dhcp.leases", "openwrt"),
    ];

    public string Name => "dhcp-leases";

    public bool IsSupported => LeaseFiles.Any(f => File.Exists(f.Path));

    public async Task<IReadOnlyList<Fact>> CollectAsync(string deviceId, CancellationToken ct)
    {
        List<Fact> facts = new();

        foreach ((string path, string source) in LeaseFiles)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                string[] lines = await File.ReadAllLinesAsync(path, ct);

                switch (source)
                {
                    case "dnsmasq":
                    case "openwrt":
                        ParseDnsmasq(deviceId, lines, source, facts);
                        break;
                    case "isc-dhcpd":
                        ParseIscDhcpd(deviceId, lines, facts);
                        break;
                    case "kea":
                        ParseKea(deviceId, lines, facts);
                        break;
                }
            }
            catch
            {
                // best-effort
            }
        }

        return facts;
    }

    private static void ParseDnsmasq(string deviceId, string[] lines, string source, List<Fact> facts)
    {
        foreach (string line in lines)
        {
            try
            {
                string[] tokens = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 3)
                {
                    continue;
                }

                string mac = tokens[1];
                string ip = tokens[2];
                string[] keys = [deviceId, mac];

                facts.Add(Fact.Create(FactPaths.DhcpLocalLeaseIP, keys, ip));
                facts.Add(Fact.Create(FactPaths.DhcpLocalLeaseSource, keys, source));

                if (tokens.Length >= 4 && tokens[3] != "*")
                {
                    facts.Add(Fact.Create(FactPaths.DhcpLocalLeaseHostname, keys, tokens[3]));
                }

                if (long.TryParse(tokens[0], out long epoch) && epoch > 0)
                {
                    string expires = DateTimeOffset.FromUnixTimeSeconds(epoch).ToString("o");
                    facts.Add(Fact.Create(FactPaths.DhcpLocalLeaseExpires, keys, expires));
                }
            }
            catch
            {
                // skip malformed line
            }
        }
    }

    private static void ParseIscDhcpd(string deviceId, string[] lines, List<Fact> facts)
    {
        // Accumulate all stanzas keyed by IP; last one wins.
        Dictionary<string, IscLease> leases = new(StringComparer.OrdinalIgnoreCase);

        IscLease? current = null;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            try
            {
                if (trimmed.StartsWith("lease ", StringComparison.OrdinalIgnoreCase))
                {
                    string ip = trimmed.Split(' ')[1].TrimEnd('{').Trim();
                    current = new IscLease
                    {
                        IP = ip,
                    };
                    leases[ip] = current;
                }
                else if (trimmed == "}")
                {
                    current = null;
                }
                else if (current is not null)
                {
                    if (trimmed.StartsWith("hardware ethernet ", StringComparison.OrdinalIgnoreCase))
                    {
                        string mac = trimmed["hardware ethernet ".Length..].TrimEnd(';').Trim();
                        current.Mac = mac;
                    }
                    else if (trimmed.StartsWith("ends ", StringComparison.OrdinalIgnoreCase))
                    {
                        string rest = trimmed["ends ".Length..].TrimEnd(';').Trim();
                        if (!rest.Equals("never", StringComparison.OrdinalIgnoreCase))
                        {
                            // Format: "N yyyy/MM/dd HH:mm:ss" (day-of-week index, then date/time in UTC)
                            int spaceIdx = rest.IndexOf(' ');
                            if (spaceIdx >= 0)
                            {
                                string datePart = rest[(spaceIdx + 1)..].Trim().Replace('/', '-');
                                if (DateTimeOffset.TryParse(datePart + "Z", out DateTimeOffset expires))
                                {
                                    current.Expires = expires;
                                }
                            }
                        }
                    }
                    else if (trimmed.StartsWith("client-hostname ", StringComparison.OrdinalIgnoreCase))
                    {
                        string hostname = trimmed["client-hostname ".Length..].TrimEnd(';').Trim().Trim('"');
                        current.Hostname = hostname;
                    }
                }
            }
            catch
            {
                // skip malformed line
            }
        }

        foreach (IscLease lease in leases.Values)
        {
            if (lease.Mac is null)
            {
                continue;
            }

            string[] keys = [deviceId, lease.Mac];
            facts.Add(Fact.Create(FactPaths.DhcpLocalLeaseIP, keys, lease.IP));
            facts.Add(Fact.Create(FactPaths.DhcpLocalLeaseSource, keys, "isc-dhcpd"));

            facts.AddIfPresent(FactPaths.DhcpLocalLeaseHostname, keys, lease.Hostname);

            if (lease.Expires.HasValue)
            {
                facts.Add(Fact.Create(FactPaths.DhcpLocalLeaseExpires, keys, lease.Expires.Value.ToString("o")));
            }
        }
    }

    private static void ParseKea(string deviceId, string[] lines, List<Fact> facts)
    {
        if (lines.Length == 0)
        {
            return;
        }

        string[]? headers = null;
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (headers is null)
            {
                headers = trimmed.Split(',');
                continue;
            }

            try
            {
                string[] cols = trimmed.Split(',');
                if (cols.Length < headers.Length)
                {
                    continue;
                }

                string Get(string name)
                {
                    int idx = Array.IndexOf(headers, name);
                    return idx >= 0 ? cols[idx].Trim() : string.Empty;
                }

                string mac = Get("hwaddr");
                string ip = Get("address");

                if (mac.Length == 0 || ip.Length == 0)
                {
                    continue;
                }

                string[] keys = [deviceId, mac];
                facts.Add(Fact.Create(FactPaths.DhcpLocalLeaseIP, keys, ip));
                facts.Add(Fact.Create(FactPaths.DhcpLocalLeaseSource, keys, "kea"));

                string hostname = Get("hostname");
                if (hostname.Length > 0)
                {
                    facts.Add(Fact.Create(FactPaths.DhcpLocalLeaseHostname, keys, hostname));
                }

                string expireRaw = Get("expire");
                if (long.TryParse(expireRaw, out long epoch) && epoch > 0)
                {
                    facts.Add(
                        Fact.Create(
                            FactPaths.DhcpLocalLeaseExpires,
                            keys,
                            DateTimeOffset.FromUnixTimeSeconds(epoch).ToString("o")
                        )
                    );
                }
            }
            catch
            {
                // skip malformed line
            }
        }
    }


    private sealed class IscLease
    {
        public string IP { get; set; } = string.Empty;
        public string? Mac { get; set; }
        public string? Hostname { get; set; }
        public DateTimeOffset? Expires { get; set; }
    }
}