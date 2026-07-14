using JMW.Discovery.Agent.Collection.Device.OnHub;
using JMW.Discovery.Agent.Collection.Device.OnHub.Proto;

namespace JMW.Discovery.UnitTests.Agent.OnHub;

/// <summary>
/// Covers parsing the AP's own interface inventory from <c>ip -s -d addr</c> output.
/// The fixture is drawn from a real Google Wifi diagnostic report (MACs obscured by
/// the firmware) plus a couple of synthesized rows for scope/edge coverage.
/// </summary>
public sealed class OnHubApInterfacesTests
{
    // Real report excerpt: loopback, a DOWN virtual iface, WAN (UP + IPv4), a
    // no-carrier bridge slave, the LAN bridge master (management IP), and a GRE
    // tunnel whose name carries an "@parent" suffix. One synthesized global IPv6.
    private const string IpAddrOutput =
        """
        1: lo: <LOOPBACK,UP,LOWER_UP> mtu 65536 qdisc noqueue state UNKNOWN group default qlen 1000
            link/loopback 00000079357*      brd 00000079357*      promiscuity 0 numtxqueues 1 numrxqueues 1
            inet 127.0.0.1/8 scope host lo
               valid_lft forever preferred_lft forever
            inet6 ::1/128 scope host
               valid_lft forever preferred_lft forever
            RX: bytes  packets  errors  dropped overrun mcast
            23080543   46531    0       0       0       0
            TX: bytes  packets  errors  dropped carrier collsns
            23080543   46531    0       0       0       0
        2: ifb0: <BROADCAST,NOARP> mtu 1500 qdisc noop state DOWN group default qlen 32
            link/ether 9280074082b*      brd ffffff551b5*      promiscuity 0
            ifb numtxqueues 1 numrxqueues 1
        4: wan0: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 qdisc mq state UP group default qlen 1000
            link/ether 703acb1f8f8*      brd ffffff551b5*      promiscuity 0 numtxqueues 4 numrxqueues 4
            inet 173.67.196.15/24 brd 173.67.196.255 scope global wan0
               valid_lft forever preferred_lft forever
            inet6 fe80::0000:0000:0000:a758/64 scope link
               valid_lft forever preferred_lft forever
        5: lan0: <NO-CARRIER,BROADCAST,MULTICAST,UP> mtu 1500 qdisc mq master br-lan state DOWN group default qlen 1000
            link/ether 703acb70d06*      brd ffffff551b5*      promiscuity 1
            bridge_slave state disabled priority 32 cost 100 hairpin off guard off numtxqueues 4 numrxqueues 4
        8: br-lan: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 qdisc noqueue state UP group default qlen 1000
            link/ether 703acb70d06*      brd ffffff551b5*      promiscuity 0
            bridge forward_delay 300 hello_time 200 max_age 600 numtxqueues 1 numrxqueues 1
            inet 192.168.1.1/24 scope global br-lan
               valid_lft forever preferred_lft forever
            inet6 2001:db8::1/64 scope global
               valid_lft forever preferred_lft forever
        22: gre-guest0@br-lan: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1462 qdisc pfifo_fast master br-guest state UNKNOWN group default qlen 1000
            link/ether aac050855b1*      brd ffffff551b5*      promiscuity 1
            gretap remote 192.168.1.215 local 192.168.1.1 dev br-lan ttl inherit
            inet6 fe80::0000:0000:0000:5dbf/64 scope link
        """;

    private static OnHubInterface Find(IReadOnlyList<OnHubInterface> ifaces, string name) =>
        ifaces.Single(i => i.Name == name);

    [Fact]
    public void Parse_ExtractsEveryInterfaceBlock()
    {
        IReadOnlyList<OnHubInterface> ifaces = OnHubApInterfaces.Parse(IpAddrOutput);

        Assert.Equal(
            new[]
            {
                "lo",
                "ifb0",
                "wan0",
                "lan0",
                "br-lan",
                "gre-guest0",
            },
            ifaces.Select(i => i.Name).ToArray()
        );
    }

    [Fact]
    public void Parse_Loopback_TypedAndUp_NoEther()
    {
        OnHubInterface lo = Find(OnHubApInterfaces.Parse(IpAddrOutput), "lo");

        Assert.Equal("loopback", lo.Type);
        Assert.Equal(65536, lo.Mtu);
        Assert.True(lo.Up); // UNKNOWN state, but LOWER_UP flag set
        Assert.Null(lo.ObscuredMac); // link/loopback is not an Ethernet MAC
        Assert.Equal("127.0.0.1", lo.Ipv4); // first inet captured regardless of scope
        Assert.Equal(8, lo.Ipv4PrefixLength); // "127.0.0.1/8" — prefix captured, not discarded
    }

    [Fact]
    public void Parse_Wan_UpWithIpv4_LinkLocalIpv6Dropped()
    {
        OnHubInterface wan = Find(OnHubApInterfaces.Parse(IpAddrOutput), "wan0");

        Assert.True(wan.Up);
        Assert.Equal(1500, wan.Mtu);
        Assert.Equal("173.67.196.15", wan.Ipv4); // prefix stripped from Ipv4 itself
        Assert.Equal(24, wan.Ipv4PrefixLength); // ... but captured separately
        Assert.Equal("703acb1f8f8*", wan.ObscuredMac);
        Assert.Null(wan.Ipv6); // fe80 link-local is noise, not captured
        Assert.Null(wan.Ipv6PrefixLength);
    }

    [Fact]
    public void Parse_NoCarrierSlave_IsDown()
    {
        OnHubInterface lan = Find(OnHubApInterfaces.Parse(IpAddrOutput), "lan0");

        Assert.False(lan.Up); // state DOWN
        Assert.Equal("703acb70d06*", lan.ObscuredMac);
        Assert.Null(lan.Type); // bridge_slave, not a bridge master
    }

    [Fact]
    public void Parse_BridgeMaster_TypedWithManagementIpAndGlobalIpv6()
    {
        OnHubInterface br = Find(OnHubApInterfaces.Parse(IpAddrOutput), "br-lan");

        Assert.Equal("bridge", br.Type);
        Assert.True(br.Up);
        Assert.Equal("192.168.1.1", br.Ipv4);
        Assert.Equal(24, br.Ipv4PrefixLength);
        Assert.Equal("2001:db8::1", br.Ipv6); // scope global is captured
        Assert.Equal(64, br.Ipv6PrefixLength);
    }

    [Fact]
    public void Parse_TunnelName_StripsAtParentSuffix()
    {
        IReadOnlyList<OnHubInterface> ifaces = OnHubApInterfaces.Parse(IpAddrOutput);

        Assert.Contains(ifaces, i => i.Name == "gre-guest0");
        Assert.DoesNotContain(ifaces, i => i.Name.Contains('@'));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n   \n")]
    [InlineData("garbage without any interface header\nmore noise")]
    public void Parse_MalformedOrEmpty_ReturnsEmpty(string input) =>
        Assert.Empty(OnHubApInterfaces.Parse(input));

    [Fact]
    public void Parse_InetWithNoSlash_PrefixLengthIsNull()
    {
        // Defensive: a malformed/truncated capture with no "/" in the inet line must not
        // throw, and must leave the prefix length null rather than a bogus value.
        const string noSlash =
            """
            9: eth10: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 qdisc mq state UP group default
                inet 10.0.0.5 scope global eth10
            """;

        OnHubInterface eth10 = Assert.Single(OnHubApInterfaces.Parse(noSlash));

        Assert.Equal("10.0.0.5", eth10.Ipv4);
        Assert.Null(eth10.Ipv4PrefixLength);
    }

    [Fact]
    public void ParseEthtool_UpLink_ReturnsSpeedAndDuplex()
    {
        const string ethtool =
            """
            Settings for wan0:
            	Supported ports: [ ]
            	Speed: 1000Mb/s
            	Duplex: Full
            	Port: Twisted Pair
            	Link detected: yes
            """;

        (long? speedBps, string? duplex) = OnHubApInterfaces.ParseEthtool(ethtool);

        Assert.Equal(1000L * 1_000_000, speedBps);
        Assert.Equal("Full", duplex);
    }

    [Fact]
    public void ParseEthtool_DownLink_UnknownYieldsNulls()
    {
        const string ethtool =
            """
            Settings for lan0:
            	Speed: Unknown!
            	Duplex: Unknown! (255)
            	Link detected: no
            """;

        (long? speedBps, string? duplex) = OnHubApInterfaces.ParseEthtool(ethtool);

        Assert.Null(speedBps);
        Assert.Null(duplex);
    }

    [Fact]
    public void Extract_MergesEthtoolSpeedOntoMatchingInterface()
    {
        DiagnosticReport report = new();
        report.CommandOutputs.Add(
            new CommandOutput
            {
                Command = "/bin/ip -s -d addr",
                Output = IpAddrOutput,
            }
        );
        // The plain `ethtool wan0` (settings) must be used; `ethtool -S wan0` (stats) ignored.
        report.CommandOutputs.Add(
            new CommandOutput
            {
                Command = "/usr/sbin/ethtool -S wan0",
                Output = "NIC statistics:\n     rx_packets: 42\n",
            }
        );
        report.CommandOutputs.Add(
            new CommandOutput
            {
                Command = "/usr/sbin/ethtool wan0",
                Output = "Settings for wan0:\n\tSpeed: 1000Mb/s\n\tDuplex: Full\n\tLink detected: yes\n",
            }
        );

        IReadOnlyList<OnHubInterface> ifaces = OnHubApInterfaces.Extract(report);
        OnHubInterface wan = Find(ifaces, "wan0");

        Assert.Equal(1000L * 1_000_000, wan.SpeedBps);
        Assert.Equal("Full", wan.Duplex);
        // An interface with no ethtool output keeps null speed/duplex.
        Assert.Null(Find(ifaces, "br-lan").SpeedBps);
    }

    [Fact]
    public void ParseIwInterfaceNames_ExtractsEveryWirelessInterface()
    {
        const string iw =
            """
            phy#1
            	Interface guest-5000mhz
            		ifindex 16
            		type AP
            	Interface wlan-5000mhz
            		ifindex 7
            		type AP
            phy#0
            	Interface wlan-2400mhz
            		ifindex 6
            		type AP
            """;

        Assert.Equal(
            new[]
            {
                "guest-5000mhz",
                "wlan-5000mhz",
                "wlan-2400mhz",
            },
            OnHubApInterfaces.ParseIwInterfaceNames(iw).ToArray()
        );
    }

    [Fact]
    public void Extract_MergesWirelessTypeFromIwDev()
    {
        const string ipAddr =
            """
            6: wlan-2400mhz: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 qdisc noqueue master br-lan state UP group default qlen 1000
                link/ether 703acb32619*      brd ffffff551b5*      promiscuity 1
            8: br-lan: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 qdisc noqueue state UP group default qlen 1000
                link/ether 703acb70d06*      brd ffffff551b5*      promiscuity 0
                bridge forward_delay 300
                inet 192.168.1.1/24 scope global br-lan
            """;
        const string iw = "phy#0\n\tInterface wlan-2400mhz\n\t\ttype AP\n";

        DiagnosticReport report = new();
        report.CommandOutputs.Add(
            new CommandOutput
            {
                Command = "/bin/ip -s -d addr",
                Output = ipAddr,
            }
        );
        report.CommandOutputs.Add(
            new CommandOutput
            {
                Command = "/usr/sbin/iw dev",
                Output = iw,
            }
        );
        // A per-interface dump must NOT be treated as the interface listing.
        report.CommandOutputs.Add(
            new CommandOutput
            {
                Command = "/usr/sbin/iw dev wlan-2400mhz station dump",
                Output = "Station aabb (on wlan-2400mhz)\n",
            }
        );

        IReadOnlyList<OnHubInterface> ifaces = OnHubApInterfaces.Extract(report);

        Assert.Equal("wireless", Find(ifaces, "wlan-2400mhz").Type); // was untyped bridge slave
        Assert.Equal("bridge", Find(ifaces, "br-lan").Type); // not in iw set, keeps bridge
    }

    [Fact]
    public void Parse_HeaderWithoutDetailLines_StillEmitsInterface()
    {
        // Truncated capture: a header with no following link/inet lines.
        const string truncated = "3: eth9: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1400 qdisc mq state UP group default";
        IReadOnlyList<OnHubInterface> ifaces = OnHubApInterfaces.Parse(truncated);

        OnHubInterface eth9 = Assert.Single(ifaces);
        Assert.Equal("eth9", eth9.Name);
        Assert.Equal(1400, eth9.Mtu);
        Assert.True(eth9.Up);
        Assert.Null(eth9.Ipv4);
        Assert.Null(eth9.ObscuredMac);
    }
}