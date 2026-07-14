using JMW.Discovery.Agent.Collection.Device.OnHub.Proto;

namespace JMW.Discovery.Agent.Collection.Device.OnHub;

/// <summary>The AP's own network interface, distilled from <c>ip -s -d addr</c>.</summary>
public sealed record OnHubInterface(
    string Name,
    string? ObscuredMac, // link/ether, obscured (real OUI, obfuscated device bytes, trailing '*')
    long? Mtu,
    bool? Up,
    string? Ipv4,
    string? Ipv6, // first global-scope address; link-local (fe80) is dropped as noise
    string? Type, // "loopback" | "bridge" | "wireless" | null
    long? SpeedBps = null, // from ethtool (Mbps × 1_000_000)
    string? Duplex = null, // from ethtool ("Full" | "Half")
    long? Ipv4PrefixLength = null, // CIDR prefix length parsed off Ipv4 before it was stripped
    long? Ipv6PrefixLength = null // CIDR prefix length parsed off Ipv6 before it was stripped
);

/// <summary>
/// Parses the router's own interface inventory from the diagnostic report's
/// <c>/bin/ip -s -d addr</c> command output. Each interface block looks like:
/// <code>
/// 8: br-lan: &lt;BROADCAST,MULTICAST,UP,LOWER_UP&gt; mtu 1500 qdisc noqueue state UP group default qlen 1000
///     link/ether 703acb70d06*      brd ffffff551b5*      promiscuity 0
///     bridge forward_delay 300 hello_time 200 max_age 600 numtxqueues 1 numrxqueues 1
///     inet 192.168.1.1/24 scope global br-lan
///     inet6 fe80::.../64 scope link
/// </code>
/// MACs are obscured by the firmware (real OUI, obfuscated device bytes); they are
/// captured raw so server-side reconstruction can resolve the real MAC by IP + OUI.
/// </summary>
public static class OnHubApInterfaces
{
    private const string IpAddrCommand = "/bin/ip -s -d addr";
    private const string EthtoolCommand = "/usr/sbin/ethtool";
    private const string IwDevCommand = "/usr/sbin/iw dev";

    public static IReadOnlyList<OnHubInterface> Extract(DiagnosticReport report)
    {
        string? ipAddr = null;
        Dictionary<string, (long? SpeedBps, string? Duplex)> ethtool = new(StringComparer.Ordinal);
        HashSet<string> wireless = new(StringComparer.Ordinal);

        foreach (CommandOutput cmd in report.CommandOutputs)
        {
            if (string.Equals(cmd.Command, IpAddrCommand, StringComparison.Ordinal))
            {
                ipAddr = cmd.Output;
            }
            else if (string.Equals(cmd.Command, IwDevCommand, StringComparison.Ordinal))
            {
                // Only the bare `iw dev` lists interfaces; the per-iface dumps
                // ("iw dev <x> station dump") carry a longer command string.
                foreach (string name in ParseIwInterfaceNames(cmd.Output))
                {
                    wireless.Add(name);
                }
            }
            else if (EthtoolInterface(cmd.Command) is { } iface)
            {
                ethtool[iface] = ParseEthtool(cmd.Output);
            }
        }

        if (ipAddr is null)
        {
            return [];
        }

        List<OnHubInterface> interfaces = [.. Parse(ipAddr)];

        // Merge link speed / duplex (ethtool) and wireless typing (iw dev) onto the
        // matching interface. iw wins on Type: an 802.11 interface shows up in ip addr
        // as an untyped bridge slave, so "wireless" is the more useful label.
        for (int i = 0; i < interfaces.Count; i++)
        {
            OnHubInterface iface = interfaces[i];
            if (ethtool.TryGetValue(iface.Name, out (long? SpeedBps, string? Duplex) e))
            {
                iface = iface with
                {
                    SpeedBps = e.SpeedBps,
                    Duplex = e.Duplex,
                };
            }

            if (wireless.Contains(iface.Name))
            {
                iface = iface with
                {
                    Type = "wireless",
                };
            }

            interfaces[i] = iface;
        }

        return interfaces;
    }

    /// <summary>
    /// Extracts the interface names from bare <c>iw dev</c> output. The listing groups
    /// interfaces under <c>phy#N</c> headers, one per <c>Interface &lt;name&gt;</c> line.
    /// </summary>
    public static IReadOnlyList<string> ParseIwInterfaceNames(string output)
    {
        List<string> names = [];
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("Interface ", StringComparison.Ordinal))
            {
                string name = line["Interface ".Length..].Trim();
                if (name.Length > 0)
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    // Returns the interface name for a plain `ethtool <iface>` command, or null for
    // anything else (notably `ethtool -S <iface>`, which is per-NIC statistics).
    private static string? EthtoolInterface(string command)
    {
        string[] tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 2
         && string.Equals(tokens[0], EthtoolCommand, StringComparison.Ordinal)
         && !tokens[1].StartsWith('-')
                ? tokens[1]
                : null;
    }

    /// <summary>
    /// Parses link speed + duplex from <c>ethtool &lt;iface&gt;</c> output. A down link
    /// reports "Unknown!" for both, which yields nulls.
    /// </summary>
    public static (long? SpeedBps, string? Duplex) ParseEthtool(string output)
    {
        long? speedBps = null;
        string? duplex = null;

        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("Speed:", StringComparison.Ordinal))
            {
                // "Speed: 1000Mb/s" → 1000 Mbps × 1_000_000. "Unknown!" → null.
                string value = line["Speed:".Length..].Trim();
                if (ParseLeadingLong(value) is { } mbps)
                {
                    speedBps = mbps * 1_000_000;
                }
            }
            else if (line.StartsWith("Duplex:", StringComparison.Ordinal))
            {
                string value = line["Duplex:".Length..].Trim();
                if (string.Equals(value, "Full", StringComparison.Ordinal)
                 || string.Equals(value, "Half", StringComparison.Ordinal))
                {
                    duplex = value;
                }
            }
        }

        return (speedBps, duplex);
    }

    // Leading integer of a string like "1000Mb/s"; null when there is none ("Unknown!").
    private static long? ParseLeadingLong(string s)
    {
        int end = 0;
        while (end < s.Length && char.IsDigit(s[end]))
        {
            end++;
        }

        return end > 0 ? OnHubTextFormat.ParseLong(s[..end]) : null;
    }

    public static IReadOnlyList<OnHubInterface> Parse(string output)
    {
        List<OnHubInterface> result = [];

        // Accumulator for the interface block currently being read.
        string? name = null;
        string? obscuredMac = null;
        long? mtu = null;
        bool? up = null;
        string? ipv4 = null;
        string? ipv6 = null;
        string? type = null;
        long? ipv4PrefixLength = null;
        long? ipv6PrefixLength = null;

        void Flush()
        {
            if (name is not null)
            {
                result.Add(
                    new OnHubInterface(
                        name,
                        obscuredMac,
                        mtu,
                        up,
                        ipv4,
                        ipv6,
                        type,
                        Ipv4PrefixLength: ipv4PrefixLength,
                        Ipv6PrefixLength: ipv6PrefixLength
                    )
                );
            }

            name = null;
            obscuredMac = null;
            mtu = null;
            up = null;
            ipv4 = null;
            ipv6 = null;
            type = null;
            ipv4PrefixLength = null;
            ipv6PrefixLength = null;
        }

        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd();
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                continue;
            }

            // A header line ("8: br-lan: <FLAGS> mtu 1500 ... state UP ...") starts at
            // column 0 with "<index>: ". Detail lines are indented.
            if (line.Length > 0 && char.IsDigit(line[0]) && IsHeaderLine(trimmed))
            {
                Flush();
                ParseHeader(trimmed, out name, out mtu, out up, out type);
            }
            else if (name is not null)
            {
                ParseDetail(trimmed, ref obscuredMac, ref ipv4, ref ipv6, ref type, ref ipv4PrefixLength, ref ipv6PrefixLength);
            }
        }

        Flush();
        return result;
    }

    private static bool IsHeaderLine(string trimmed)
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

    private static void ParseHeader(string line, out string? name, out long? mtu, out bool? up, out string? type)
    {
        name = null;
        mtu = null;
        up = null;
        type = null;

        // "8: br-lan: <FLAGS> mtu 1500 qdisc ... state UP ..."
        int firstColon = line.IndexOf(':');
        int secondColon = line.IndexOf(':', firstColon + 1);
        if (secondColon < 0)
        {
            return;
        }

        // Interface name; strip a "@parent" suffix (e.g. "gre-guest0@br-lan").
        string rawName = line[(firstColon + 1)..secondColon].Trim();
        int at = rawName.IndexOf('@');
        name = at >= 0 ? rawName[..at] : rawName;
        if (name.Length == 0)
        {
            name = null;
            return;
        }

        string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string flags = "";
        string? state = null;
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].StartsWith('<') && tokens[i].EndsWith('>'))
            {
                flags = tokens[i][1..^1];
            }
            else if (string.Equals(tokens[i], "mtu", StringComparison.Ordinal) && i + 1 < tokens.Length)
            {
                mtu = OnHubTextFormat.ParseLong(tokens[i + 1]);
            }
            else if (string.Equals(tokens[i], "state", StringComparison.Ordinal) && i + 1 < tokens.Length)
            {
                state = tokens[i + 1];
            }
        }

        up = OperationalUp(state, flags);
    }

    // Operational status: an explicit UP/DOWN state wins; for UNKNOWN (loopback,
    // tunnels) fall back to the carrier flag (LOWER_UP).
    private static bool? OperationalUp(string? state, string flags)
    {
        if (string.Equals(state, "UP", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(state, "DOWN", StringComparison.Ordinal))
        {
            return false;
        }

        if (flags.Length == 0)
        {
            return null;
        }

        return flags.Split(',').Contains("LOWER_UP") ? true : null;
    }

    private static void ParseDetail(
        string trimmed,
        ref string? obscuredMac,
        ref string? ipv4,
        ref string? ipv6,
        ref string? type,
        ref long? ipv4PrefixLength,
        ref long? ipv6PrefixLength
    )
    {
        string[] tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return;
        }

        switch (tokens[0])
        {
            case "link/ether" when tokens.Length >= 2 && obscuredMac is null:
                obscuredMac = tokens[1];
                break;

            case "link/loopback" when type is null:
                type = "loopback";
                break;

            case "inet" when tokens.Length >= 2 && ipv4 is null:
                ipv4 = StripPrefix(tokens[1], out ipv4PrefixLength);
                break;

            case "inet6" when tokens.Length >= 2 && ipv6 is null && IsGlobalScope(tokens):
                ipv6 = StripPrefix(tokens[1], out ipv6PrefixLength);
                break;

            // A bridge master carries a "bridge forward_delay ..." line; a slave carries
            // "bridge_slave ...". Only the master is the bridge device.
            case "bridge" when type is null:
                type = "bridge";
                break;
        }
    }

    private static bool IsGlobalScope(string[] tokens)
    {
        for (int i = 0; i < tokens.Length - 1; i++)
        {
            if (string.Equals(tokens[i], "scope", StringComparison.Ordinal))
            {
                return string.Equals(tokens[i + 1], "global", StringComparison.Ordinal);
            }
        }

        return false;
    }

    // Splits an "ip/prefix" CIDR string, returning the bare IP and (via out) the parsed
    // prefix length. The bare IP is what OnHubInterface.Ipv4/Ipv6 carry — DiscoveryMaterializer's
    // MAC-reconstruction join keys on the exact bare-IP string, so that meaning must not change
    // (see OnHubApInterfaces class remarks). The prefix length is captured separately so it isn't
    // silently discarded — the Subnets page needs it to synthesize a CIDR for interfaces that
    // have no peer already covering their subnet (e.g. an isolated guest network).
    private static string StripPrefix(string cidr, out long? prefixLength)
    {
        int slash = cidr.IndexOf('/');
        if (slash < 0)
        {
            prefixLength = null;
            return cidr;
        }

        prefixLength = OnHubTextFormat.ParseLong(cidr[(slash + 1)..]);
        return cidr[..slash];
    }
}