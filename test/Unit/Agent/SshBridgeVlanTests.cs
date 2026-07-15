using System.Collections;
using System.Reflection;

using JMW.Discovery.Agent.Collection.Device;

namespace JMW.Discovery.UnitTests.Agent;

/// <summary>
/// <see cref="SshCollector" />'s private bridge/VLAN/STP parsers: <c>ip -d link show</c> (bridge
/// master, 802.1Q sub-interface VLAN ID, per-port STP state) and <c>bridge vlan show</c>
/// (PVID/tagged VLAN membership). Both are pure text parsers with no SSH session involved, tested
/// via reflection the same way as the SNMP collector's pure helpers (the returned records are
/// private nested types, so each row is read back as a property-name → value map).
/// </summary>
public sealed class SshBridgeVlanTests
{
    private static List<Dictionary<string, object?>> Invoke(string methodName, string output)
    {
        MethodInfo m = typeof(SshCollector).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
         ?? throw new InvalidOperationException($"SshCollector.{methodName} not found.");
        object result = m.Invoke(null, [output])!;

        List<Dictionary<string, object?>> rows = [];
        foreach (object item in (IEnumerable)result)
        {
            Dictionary<string, object?> row = new(StringComparer.Ordinal);
            foreach (PropertyInfo p in item.GetType().GetProperties())
            {
                row[p.Name] = p.GetValue(item);
            }

            rows.Add(row);
        }

        return rows;
    }

    // ── ParseIpDLinkShow ──────────────────────────────────────────────────────

    private const string IpDLinkShowOutput =
        "1: lo: <LOOPBACK,UP,LOWER_UP> mtu 65536 qdisc noqueue state UNKNOWN mode DEFAULT group default qlen 1000\n"
      + "    link/loopback 00:00:00:00:00:00 brd 00:00:00:00:00:00 promiscuity 0\n"
      + "2: eth0: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 qdisc pfifo_fast master br0 state UP mode DEFAULT group default qlen 1000\n"
      + "    link/ether 00:11:22:33:44:55 brd ff:ff:ff:ff:ff:ff promiscuity 1\n"
      + "    bridge_slave state forwarding priority 32 cost 4\n"
      + "3: br0: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 qdisc noqueue state UP mode DEFAULT group default qlen 1000\n"
      + "    link/ether 00:11:22:33:44:55 brd ff:ff:ff:ff:ff:ff\n"
      + "    bridge forward_delay 1500 hello_time 200\n"
      + "4: eth0.10@eth0: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 qdisc noqueue state UP mode DEFAULT group default qlen 1000\n"
      + "    link/ether 00:11:22:33:44:55 brd ff:ff:ff:ff:ff:ff\n"
      + "    vlan protocol 802.1Q id 10 <REORDER_HDR>\n";

    [Fact]
    public void ParseIpDLinkShow_MasterOnHeaderLine_SetsBridgeMaster()
    {
        List<Dictionary<string, object?>> rows = Invoke("ParseIpDLinkShow", IpDLinkShowOutput);

        Dictionary<string, object?> eth0 = rows.Single(r => (string)r["Interface"]! == "eth0");
        Assert.Equal("br0", eth0["BridgeMaster"]);
    }

    [Fact]
    public void ParseIpDLinkShow_BridgeSlaveDetailLine_SetsStpState()
    {
        List<Dictionary<string, object?>> rows = Invoke("ParseIpDLinkShow", IpDLinkShowOutput);

        Dictionary<string, object?> eth0 = rows.Single(r => (string)r["Interface"]! == "eth0");
        Assert.Equal("forwarding", eth0["StpState"]);
    }

    [Fact]
    public void ParseIpDLinkShow_VlanSubInterface_StripsParentAndSetsVlanId()
    {
        List<Dictionary<string, object?>> rows = Invoke("ParseIpDLinkShow", IpDLinkShowOutput);

        Dictionary<string, object?> vlanIface = rows.Single(r => (string)r["Interface"]! == "eth0.10");
        Assert.Equal(10, vlanIface["VlanId"]);
        Assert.Null(vlanIface["BridgeMaster"]);
    }

    [Fact]
    public void ParseIpDLinkShow_InterfaceWithNoBridgeOrVlan_HasNullFields()
    {
        List<Dictionary<string, object?>> rows = Invoke("ParseIpDLinkShow", IpDLinkShowOutput);

        Dictionary<string, object?> lo = rows.Single(r => (string)r["Interface"]! == "lo");
        Assert.Null(lo["BridgeMaster"]);
        Assert.Null(lo["VlanId"]);
        Assert.Null(lo["StpState"]);
    }

    [Fact]
    public void ParseIpDLinkShow_EmptyOutput_ReturnsNothing()
    {
        Assert.Empty(Invoke("ParseIpDLinkShow", ""));
    }

    // ── ParseBridgeVlanShow ───────────────────────────────────────────────────

    private const string BridgeVlanShowOutput =
        "port              vlan-id\n"
      + "eth0              1 PVID Egress Untagged\n"
      + "                  10\n"
      + "                  20\n"
      + "br0               1 PVID Egress Untagged\n";

    [Fact]
    public void ParseBridgeVlanShow_PvidRow_IsMarkedPvid()
    {
        List<Dictionary<string, object?>> rows = Invoke("ParseBridgeVlanShow", BridgeVlanShowOutput);

        Dictionary<string, object?> pvidRow = rows.Single(r => (string)r["Port"]! == "eth0" && (int)r["VlanId"]! == 1);
        Assert.True((bool)pvidRow["IsPvid"]!);
    }

    [Fact]
    public void ParseBridgeVlanShow_ContinuationRows_AttachToSamePortAndAreNotPvid()
    {
        List<Dictionary<string, object?>> rows = Invoke("ParseBridgeVlanShow", BridgeVlanShowOutput);

        List<Dictionary<string, object?>> eth0Rows = rows.Where(r => (string)r["Port"]! == "eth0").ToList();
        Assert.Equal(3, eth0Rows.Count); // vlan 1 (PVID), 10, 20
        Assert.Equal(
            new[] { 10, 20 },
            eth0Rows.Where(r => !(bool)r["IsPvid"]!).Select(r => (int)r["VlanId"]!).OrderBy(v => v)
        );
    }

    [Fact]
    public void ParseBridgeVlanShow_HeaderRow_IsNotTreatedAsAPort()
    {
        List<Dictionary<string, object?>> rows = Invoke("ParseBridgeVlanShow", BridgeVlanShowOutput);

        Assert.DoesNotContain(rows, r => (string)r["Port"]! == "port");
    }

    [Fact]
    public void ParseBridgeVlanShow_EmptyOutput_ReturnsNothing()
    {
        Assert.Empty(Invoke("ParseBridgeVlanShow", ""));
    }
}