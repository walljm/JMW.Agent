using JMW.Discovery.Agent.Collection.Device.OnHub;

namespace JMW.Discovery.UnitTests.Agent.OnHub;

/// <summary>
/// Covers parsing bridge membership (<c>brctl show</c>), spanning-tree state
/// (<c>brctl showstp &lt;bridge&gt;</c>), and switch VLAN config (<c>swconfig dev switch0
/// show</c>) — see docs/plans/d3-l2-l3.md. No real captured fixture exists for these three
/// commands (unlike <c>ip -s -d addr</c>), so this sample text is hand-written to match the
/// documented real-world bridge-utils/OpenWrt swconfig output shapes.
/// </summary>
public sealed class OnHubApBridgeVlanTests
{
    private const string BrctlShowOutput =
        "bridge name\tbridge id\t\tSTP enabled\tinterfaces\n"
      + "br-lan\t\t8000.703acb70d064\tyes\t\teth0\n"
      + "\t\t\t\t\t\teth1\n"
      + "\t\t\t\t\t\twlan0\n"
      + "br-guest\t8000.703acb70d065\tno\t\twlan1\n";

    [Fact]
    public void ParseBrctlShow_FirstRowCarriesNameIdStpAndFirstInterface()
    {
        IReadOnlyList<OnHubBridgeMembership> bridges = OnHubApBridgeVlan.ParseBrctlShow(BrctlShowOutput);

        OnHubBridgeMembership lan = bridges.Single(b => b.BridgeName == "br-lan");
        Assert.Equal("8000.703acb70d064", lan.BridgeId);
        Assert.True(lan.StpEnabled);
        Assert.Equal(["eth0", "eth1", "wlan0"], lan.MemberInterfaces);
    }

    [Fact]
    public void ParseBrctlShow_ContinuationLinesAppendToPriorBridge()
    {
        IReadOnlyList<OnHubBridgeMembership> bridges = OnHubApBridgeVlan.ParseBrctlShow(BrctlShowOutput);

        Assert.Equal(2, bridges.Count);
        OnHubBridgeMembership guest = bridges.Single(b => b.BridgeName == "br-guest");
        Assert.False(guest.StpEnabled);
        Assert.Equal(["wlan1"], guest.MemberInterfaces);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bridge name\tbridge id\t\tSTP enabled\tinterfaces\n")]
    public void ParseBrctlShow_EmptyOrHeaderOnly_ReturnsNothing(string input) =>
        Assert.Empty(OnHubApBridgeVlan.ParseBrctlShow(input));

    // This bridge IS the root (root port 0 = no upstream port). One port designated
    // (matches our own bridge id, forwarding); one port blocking (alternate).
    private const string ShowStpRootBridge =
        "br-lan\n"
      + " bridge id\t\t8000.703acb70d064\n"
      + " designated root\t8000.703acb70d064\n"
      + " root port\t\t   0\t\t\tpath cost\t\t    0\n"
      + " max age\t\t 20.00\t\t\tbridge max age\t\t 20.00\n"
      + " flags\t\t\t\n"
      + "\n"
      + "eth0 (1)\n"
      + " port id\t\t8001\t\t\tstate\t\t\tforwarding\n"
      + " designated root\t8000.703acb70d064\tpath cost\t\t    4\n"
      + " designated bridge\t8000.703acb70d064\tmessage age timer\t  0.00\n"
      + " designated port\t8001\t\tforward delay timer\t  0.00\n"
      + " flags\t\t\t\n"
      + "\n"
      + "eth1 (2)\n"
      + " port id\t\t8002\t\t\tstate\t\t\tblocking\n"
      + " designated root\t8000.703acb70d064\tpath cost\t\t    4\n"
      + " designated bridge\t8000.703acb70d065\tmessage age timer\t  0.00\n"
      + " designated port\t8002\t\tforward delay timer\t  0.00\n"
      + " flags\t\t\t\n";

    [Fact]
    public void ParseBrctlShowStp_RootBridge_RootPortIsNullAndIdsResolve()
    {
        OnHubBridgeStp stp = OnHubApBridgeVlan.ParseBrctlShowStp("br-lan", ShowStpRootBridge);

        Assert.Equal("8000.703acb70d064", stp.BridgeId);
        Assert.Equal("8000.703acb70d064", stp.RootId);
        Assert.Equal(0, stp.RootPathCost);
        Assert.Null(stp.RootPortInterface); // root port 0 = this bridge is the root
        Assert.Equal(2, stp.Ports.Count);
    }

    [Fact]
    public void ParseBrctlShowStp_PortMatchingOwnBridgeId_IsDesignated()
    {
        OnHubBridgeStp stp = OnHubApBridgeVlan.ParseBrctlShowStp("br-lan", ShowStpRootBridge);

        OnHubBridgePortStp eth0 = stp.Ports.Single(p => p.Interface == "eth0");
        Assert.Equal("forwarding", eth0.State);
        Assert.Equal(4, eth0.PathCost);
        Assert.Equal("8000.703acb70d064", eth0.DesignatedBridge);
    }

    [Fact]
    public void ParseBrctlShowStp_PortNotMatchingOwnBridgeId_IsBlocking()
    {
        OnHubBridgeStp stp = OnHubApBridgeVlan.ParseBrctlShowStp("br-lan", ShowStpRootBridge);

        OnHubBridgePortStp eth1 = stp.Ports.Single(p => p.Interface == "eth1");
        Assert.Equal("blocking", eth1.State);
        Assert.Equal("8000.703acb70d065", eth1.DesignatedBridge);
    }

    // A downstream (non-root) bridge: root port 1 resolves to eth0's interface name.
    private const string ShowStpDownstreamBridge =
        "br-lan\n"
      + " bridge id\t\t8000.aabbccddeeff\n"
      + " designated root\t8000.703acb70d064\n"
      + " root port\t\t   1\t\t\tpath cost\t\t    4\n"
      + "\n"
      + "eth0 (1)\n"
      + " port id\t\t8001\t\t\tstate\t\t\tforwarding\n"
      + " designated root\t8000.703acb70d064\tpath cost\t\t    0\n"
      + " designated bridge\t8000.703acb70d064\tmessage age timer\t  0.00\n";

    [Fact]
    public void ParseBrctlShowStp_NonZeroRootPort_ResolvesToInterfaceName()
    {
        OnHubBridgeStp stp = OnHubApBridgeVlan.ParseBrctlShowStp("br-lan", ShowStpDownstreamBridge);

        Assert.Equal("eth0", stp.RootPortInterface);
        Assert.Equal("8000.703acb70d064", stp.RootId);
    }

    private const string SwconfigOutput =
        "Global attributes:\n"
      + "\tenable_vlan: 1\n"
      + "Port 0:\n"
      + "\tpvid: 1\n"
      + "\tlink: port:0 link:up speed:1000baseT full-duplex\n"
      + "Port 1:\n"
      + "\tpvid: 1\n"
      + "\tlink: port:1 link:down\n"
      + "Port 5:\n"
      + "\tpvid: 1\n"
      + "\tlink: port:5 link:up speed:1000baseT full-duplex\n"
      + "VLAN 1:\n"
      + "\tvid: 1\n"
      + "\tports: 0t 1 2 3 5t\n"
      + "VLAN 10:\n"
      + "\tvid: 10\n"
      + "\tports: 0t 5t\n";

    [Fact]
    public void ParseSwconfigShow_ParsesPvidPerPort()
    {
        IReadOnlyList<OnHubSwitchPort> ports = OnHubApBridgeVlan.ParseSwconfigShow(SwconfigOutput);

        Assert.All(ports, p => Assert.Equal(1, p.Pvid));
    }

    [Fact]
    public void ParseSwconfigShow_TaggedSuffix_MarksTrunkMembership()
    {
        IReadOnlyList<OnHubSwitchPort> ports = OnHubApBridgeVlan.ParseSwconfigShow(SwconfigOutput);

        OnHubSwitchPort port0 = ports.Single(p => p.Port == 0);
        Assert.Equal([1, 10], port0.TaggedVlans.OrderBy(v => v));

        OnHubSwitchPort port1 = ports.Single(p => p.Port == 1);
        Assert.Empty(port1.TaggedVlans); // untagged/native on VLAN 1 only, matches its pvid
    }

    [Theory]
    [InlineData("")]
    [InlineData("Global attributes:\n\tenable_vlan: 0\n")]
    public void ParseSwconfigShow_NoPortsOrVlans_ReturnsEmpty(string input) =>
        Assert.Empty(OnHubApBridgeVlan.ParseSwconfigShow(input));
}