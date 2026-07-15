using JMW.Discovery.Agent.Collection.Device.OnHub.Proto;

namespace JMW.Discovery.Agent.Collection.Device.OnHub;

/// <summary>One bridge's membership, distilled from <c>brctl show</c>.</summary>
public sealed record OnHubBridgeMembership(
    string BridgeName,
    string? BridgeId,
    bool? StpEnabled,
    IReadOnlyList<string> MemberInterfaces
);

/// <summary>One bridge port's STP state, distilled from <c>brctl showstp &lt;bridge&gt;</c>.</summary>
public sealed record OnHubBridgePortStp(
    string Interface,
    int PortNumber,
    string? State, // forwarding | blocking | listening | learning | disabled
    int? PathCost,
    string? DesignatedBridge
);

/// <summary>A bridge's STP root-election summary, from <c>brctl showstp &lt;bridge&gt;</c>.</summary>
public sealed record OnHubBridgeStp(
    string BridgeName,
    string? BridgeId,
    string? RootId,
    int? RootPathCost,
    string? RootPortInterface, // resolved via the matching port-number entry in Ports; null if this bridge IS the root
    IReadOnlyList<OnHubBridgePortStp> Ports
);

/// <summary>One hardware switch port's VLAN membership, from <c>swconfig dev switch0 show</c>.</summary>
public sealed record OnHubSwitchPort(int Port, int? Pvid, IReadOnlyList<int> TaggedVlans);

/// <summary>
/// Parses bridge/VLAN/STP topology already captured (unparsed) in the diagnostic report's
/// <c>commandOutputs</c> — no new HTTP round-trip to the AP. Three independent commands, each
/// optional (a report may be missing any of them):
/// <list type="bullet">
/// <item><c>/sbin/brctl show</c> — bridge → member-interface table, STP enabled flag.</item>
/// <item><c>/sbin/brctl showstp &lt;bridge&gt;</c> — per bridge found above: root election
/// summary + per-port state/cost/designated-bridge.</item>
/// <item><c>/usr/sbin/swconfig dev switch0 show</c> — hardware switch VLAN config: per-port
/// PVID and per-VLAN tagged/untagged port membership.</item>
/// </list>
/// Deliberately lenient: unrecognized lines are skipped rather than throwing, matching
/// <see cref="OnHubApInterfaces" />/<see cref="OnHubApStorage" />'s convention for
/// machine-generated device output we only mine a handful of fields from.
/// </summary>
public static class OnHubApBridgeVlan
{
    private const string BrctlShowCommand = "/sbin/brctl show";
    private const string BrctlShowStpPrefix = "/sbin/brctl showstp ";
    private const string SwconfigShowCommand = "/usr/sbin/swconfig dev switch0 show";

    public static (
        IReadOnlyList<OnHubBridgeMembership> Bridges,
        IReadOnlyList<OnHubBridgeStp> BridgeStp,
        IReadOnlyList<OnHubSwitchPort> SwitchPorts
    ) Extract(DiagnosticReport report)
    {
        string? brctlShowOutput = null;
        string? swconfigOutput = null;
        Dictionary<string, string> brctlShowStpByBridge = new(StringComparer.Ordinal);

        foreach (CommandOutput cmd in report.CommandOutputs)
        {
            if (string.Equals(cmd.Command, BrctlShowCommand, StringComparison.Ordinal))
            {
                brctlShowOutput = cmd.Output;
            }
            else if (string.Equals(cmd.Command, SwconfigShowCommand, StringComparison.Ordinal))
            {
                swconfigOutput = cmd.Output;
            }
            else if (cmd.Command.StartsWith(BrctlShowStpPrefix, StringComparison.Ordinal))
            {
                string bridgeName = cmd.Command[BrctlShowStpPrefix.Length..].Trim();
                if (bridgeName.Length > 0)
                {
                    brctlShowStpByBridge[bridgeName] = cmd.Output;
                }
            }
        }

        IReadOnlyList<OnHubBridgeMembership> bridges = brctlShowOutput is null
            ? []
            : ParseBrctlShow(brctlShowOutput);

        List<OnHubBridgeStp> bridgeStp = [];
        foreach (OnHubBridgeMembership bridge in bridges)
        {
            if (brctlShowStpByBridge.TryGetValue(bridge.BridgeName, out string? stpOutput))
            {
                bridgeStp.Add(ParseBrctlShowStp(bridge.BridgeName, stpOutput));
            }
        }

        IReadOnlyList<OnHubSwitchPort> switchPorts = swconfigOutput is null
            ? []
            : ParseSwconfigShow(swconfigOutput);

        return (bridges, bridgeStp, switchPorts);
    }

    /// <summary>
    /// Parses <c>brctl show</c>:
    /// <code>
    /// bridge name	bridge id		STP enabled	interfaces
    /// br-lan		8000.703acb70d064	yes		eth0
    /// 							eth1
    /// br-guest	8000.703acb70d065	no		wlan1
    /// </code>
    /// A bridge's own row (unindented) carries name/id/stp-enabled/first-interface; any
    /// further member interfaces are continuation rows (indented) with only the interface name.
    /// </summary>
    public static IReadOnlyList<OnHubBridgeMembership> ParseBrctlShow(string output)
    {
        List<OnHubBridgeMembership> result = [];

        string? name = null;
        string? bridgeId = null;
        bool? stpEnabled = null;
        List<string> members = [];

        void Flush()
        {
            if (name is not null)
            {
                result.Add(new OnHubBridgeMembership(name, bridgeId, stpEnabled, [.. members]));
            }

            name = null;
            bridgeId = null;
            stpEnabled = null;
            members = [];
        }

        foreach (string rawLine in output.Split('\n'))
        {
            if (rawLine.TrimEnd().Length == 0)
            {
                continue;
            }

            if (rawLine.StartsWith("bridge name", StringComparison.Ordinal))
            {
                continue; // header row
            }

            string[] tokens = rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                continue;
            }

            bool isContinuation = char.IsWhiteSpace(rawLine[0]);
            if (isContinuation)
            {
                if (name is not null && tokens.Length >= 1)
                {
                    members.Add(tokens[0]);
                }

                continue;
            }

            // New bridge row — flush the previous one first.
            Flush();

            name = tokens[0];
            if (tokens.Length >= 2)
            {
                bridgeId = tokens[1];
            }

            if (tokens.Length >= 3)
            {
                stpEnabled = string.Equals(tokens[2], "yes", StringComparison.OrdinalIgnoreCase);
            }

            if (tokens.Length >= 4)
            {
                members.Add(tokens[3]);
            }
        }

        Flush();
        return result;
    }

    /// <summary>
    /// Parses <c>brctl showstp &lt;bridge&gt;</c>:
    /// <code>
    /// br-lan
    ///  bridge id		8000.703acb70d064
    ///  designated root	8000.703acb70d064
    ///  root port		   0			path cost		    0
    /// ...
    /// eth0 (1)
    ///  port id		8001			state			forwarding
    ///  designated root	8000.703acb70d064	path cost		    4
    ///  designated bridge	8000.703acb70d064	message age timer	  0.00
    /// </code>
    /// Bridge-summary lines (before the first "ifname (N)" header) are unindented-adjacent
    /// key/value pairs; per-port blocks start with an unindented "ifname (N)" header followed
    /// by indented key/value lines, two pairs per line.
    /// </summary>
    public static OnHubBridgeStp ParseBrctlShowStp(string bridgeName, string output)
    {
        string? bridgeId = null;
        string? rootId = null;
        int? rootPathCost = null;
        int? rootPortNum = null;

        List<OnHubBridgePortStp> ports = [];
        string? currentPort = null;
        int currentPortNum = 0;
        string? currentState = null;
        int? currentCost = null;
        string? currentDesignatedBridge = null;

        void FlushPort()
        {
            if (currentPort is not null)
            {
                ports.Add(
                    new OnHubBridgePortStp(currentPort, currentPortNum, currentState, currentCost, currentDesignatedBridge)
                );
            }

            currentPort = null;
            currentPortNum = 0;
            currentState = null;
            currentCost = null;
            currentDesignatedBridge = null;
        }

        foreach (string rawLine in output.Split('\n'))
        {
            string trimmed = rawLine.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            bool unindented = rawLine.Length > 0 && !char.IsWhiteSpace(rawLine[0]);

            if (unindented)
            {
                // "eth0 (1)" — a per-port header. The bare bridge-name line (first line of the
                // whole dump) also fits "unindented", but never carries "(N)"; skip it.
                if (TryParsePortHeader(trimmed, out string? ifName, out int portNum))
                {
                    FlushPort();
                    currentPort = ifName;
                    currentPortNum = portNum;
                }

                continue;
            }

            string[] tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            bool inPortBlock = currentPort is not null;

            for (int i = 0; i < tokens.Length - 1;)
            {
                if (Matches(tokens, i, "bridge", "id") && !inPortBlock)
                {
                    bridgeId = tokens[i + 2];
                    i += 3;
                }
                else if (Matches(tokens, i, "designated", "root") && !inPortBlock)
                {
                    rootId = tokens[i + 2];
                    i += 3;
                }
                else if (Matches(tokens, i, "root", "port") && !inPortBlock)
                {
                    if (int.TryParse(tokens[i + 2], out int parsedRootPort))
                    {
                        rootPortNum = parsedRootPort;
                    }

                    i += 3;
                }
                else if (Matches(tokens, i, "path", "cost"))
                {
                    if (int.TryParse(tokens[i + 2], out int cost))
                    {
                        if (inPortBlock)
                        {
                            currentCost = cost;
                        }
                        else
                        {
                            rootPathCost = cost;
                        }
                    }

                    i += 3;
                }
                else if (Matches(tokens, i, "designated", "bridge") && inPortBlock)
                {
                    currentDesignatedBridge = tokens[i + 2];
                    i += 3;
                }
                else if (string.Equals(tokens[i], "state", StringComparison.Ordinal) && inPortBlock)
                {
                    currentState = tokens[i + 1];
                    i += 2;
                }
                else
                {
                    i++;
                }
            }
        }

        FlushPort();

        string? rootPortInterface = rootPortNum is > 0
            ? ports.FirstOrDefault(p => p.PortNumber == rootPortNum.Value)?.Interface
            : null;

        return new OnHubBridgeStp(bridgeName, bridgeId, rootId, rootPathCost, rootPortInterface, ports);
    }

    private static bool Matches(string[] tokens, int i, string a, string b) =>
        i + 2 < tokens.Length
     && string.Equals(tokens[i], a, StringComparison.Ordinal)
     && string.Equals(tokens[i + 1], b, StringComparison.Ordinal);

    // "eth0 (1)" → ifName="eth0", portNum=1. Rejects the bare "br-lan" first line (no parens).
    private static bool TryParsePortHeader(string trimmed, out string? ifName, out int portNum)
    {
        ifName = null;
        portNum = 0;

        int open = trimmed.IndexOf('(', StringComparison.Ordinal);
        if (open <= 0 || !trimmed.EndsWith(')'))
        {
            return false;
        }

        string name = trimmed[..open].Trim();
        string numStr = trimmed[(open + 1)..^1].Trim();
        if (name.Length == 0 || !int.TryParse(numStr, out portNum))
        {
            return false;
        }

        ifName = name;
        return true;
    }

    /// <summary>
    /// Parses <c>swconfig dev switch0 show</c>:
    /// <code>
    /// Global attributes:
    /// 	enable_vlan: 1
    /// Port 0:
    /// 	pvid: 1
    /// 	link: port:0 link:up speed:1000baseT full-duplex
    /// VLAN 1:
    /// 	vid: 1
    /// 	ports: 0t 1 2 3 5t
    /// </code>
    /// A trailing 't' on a port token in a VLAN's "ports:" line marks tagged (trunk) membership;
    /// untagged/native membership for a port is already captured by that port's own "pvid:" line.
    /// </summary>
    public static IReadOnlyList<OnHubSwitchPort> ParseSwconfigShow(string output)
    {
        Dictionary<int, int> pvidByPort = new();
        Dictionary<int, List<int>> taggedVlansByPort = new();

        int? currentPort = null;
        int? currentVlan = null;

        foreach (string rawLine in output.Split('\n'))
        {
            string trimmed = rawLine.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith("Port ", StringComparison.Ordinal) && trimmed.EndsWith(':'))
            {
                currentVlan = null;
                currentPort = ParsePortOrVlanNumber(trimmed, "Port ");
                continue;
            }

            if (trimmed.StartsWith("VLAN ", StringComparison.Ordinal) && trimmed.EndsWith(':'))
            {
                currentPort = null;
                currentVlan = ParsePortOrVlanNumber(trimmed, "VLAN ");
                continue;
            }

            if (trimmed.StartsWith("Global attributes", StringComparison.Ordinal))
            {
                currentPort = null;
                currentVlan = null;
                continue;
            }

            if (currentPort is { } port && trimmed.StartsWith("pvid:", StringComparison.Ordinal))
            {
                if (int.TryParse(trimmed["pvid:".Length..].Trim(), out int pvid))
                {
                    pvidByPort[port] = pvid;
                }

                continue;
            }

            if (currentVlan is { } vlan && trimmed.StartsWith("ports:", StringComparison.Ordinal))
            {
                foreach (string token in trimmed["ports:".Length..].Split(
                             (char[]?)null,
                             StringSplitOptions.RemoveEmptyEntries
                         ))
                {
                    if (!token.EndsWith('t'))
                    {
                        continue; // untagged/native membership comes from that port's own pvid, not here
                    }

                    if (int.TryParse(token[..^1], out int taggedPort))
                    {
                        if (!taggedVlansByPort.TryGetValue(taggedPort, out List<int>? vlans))
                        {
                            vlans = [];
                            taggedVlansByPort[taggedPort] = vlans;
                        }

                        vlans.Add(vlan);
                    }
                }
            }
        }

        HashSet<int> allPorts = [.. pvidByPort.Keys, .. taggedVlansByPort.Keys];
        return [.. allPorts
            .OrderBy(p => p)
            .Select(
                p => new OnHubSwitchPort(
                    p,
                    pvidByPort.TryGetValue(p, out int pvid) ? pvid : null,
                    taggedVlansByPort.TryGetValue(p, out List<int>? tagged) ? tagged : []
                )
            )];
    }

    private static int? ParsePortOrVlanNumber(string headerLine, string prefix)
    {
        string rest = headerLine[prefix.Length..].TrimEnd(':').Trim();
        return int.TryParse(rest, out int n) ? n : null;
    }
}