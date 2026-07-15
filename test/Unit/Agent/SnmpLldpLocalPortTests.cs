using System.Reflection;

using JMW.Discovery.Agent.Collection.Device;

namespace JMW.Discovery.UnitTests.Agent;

/// <summary>
/// <see cref="SnmpCollector" />'s private BuildLocalPortLabels resolves lldpLocPortTable's
/// per-port label: prefer lldpLocPortDesc (per RFC, equal to ifDescr when the port has an
/// ifIndex); fall back to lldpLocPortId only when its subtype is interfaceName (5) — any other
/// subtype's raw encoding (MAC bytes, locally-assigned string, etc.) isn't a meaningful label
/// and should be skipped rather than surfaced as a confusing local port name.
/// </summary>
public sealed class SnmpLldpLocalPortTests
{
    private static Dictionary<string, string> BuildLocalPortLabels(
        Dictionary<string, string> descByPortNum,
        Dictionary<string, string> idByPortNum,
        Dictionary<string, int> idSubtypeByPortNum
    )
    {
        MethodInfo m = typeof(SnmpCollector).GetMethod(
                "BuildLocalPortLabels",
                BindingFlags.NonPublic | BindingFlags.Static
            )
         ?? throw new InvalidOperationException("SnmpCollector.BuildLocalPortLabels not found.");
        return (Dictionary<string, string>)m.Invoke(
            null,
            [descByPortNum, idByPortNum, idSubtypeByPortNum]
        )!;
    }

    [Fact]
    public void BuildLocalPortLabels_PrefersDescOverId()
    {
        Dictionary<string, string> desc = new() { ["1"] = "GigabitEthernet0/1" };
        Dictionary<string, string> id = new() { ["1"] = "00:11:22:33:44:55" };
        Dictionary<string, int> subtype = new() { ["1"] = 3 }; // portComponent, not interfaceName

        Dictionary<string, string> result = BuildLocalPortLabels(desc, id, subtype);

        Assert.Equal("GigabitEthernet0/1", result["1"]);
    }

    [Fact]
    public void BuildLocalPortLabels_FallsBackToIdWhenSubtypeIsInterfaceName()
    {
        Dictionary<string, string> desc = new();
        Dictionary<string, string> id = new() { ["2"] = "eth0" };
        Dictionary<string, int> subtype = new() { ["2"] = 5 }; // interfaceName

        Dictionary<string, string> result = BuildLocalPortLabels(desc, id, subtype);

        Assert.Equal("eth0", result["2"]);
    }

    [Fact]
    public void BuildLocalPortLabels_SkipsIdWhenSubtypeIsNotInterfaceName()
    {
        Dictionary<string, string> desc = new();
        Dictionary<string, string> id = new() { ["3"] = "\x00\x11\x22\x33\x44\x55" }; // raw MAC bytes, not a label
        Dictionary<string, int> subtype = new() { ["3"] = 3 }; // portComponent

        Dictionary<string, string> result = BuildLocalPortLabels(desc, id, subtype);

        Assert.False(result.ContainsKey("3"));
    }

    [Fact]
    public void BuildLocalPortLabels_SkipsPortWithNoUsableData()
    {
        Dictionary<string, string> desc = new() { ["4"] = "" }; // blank desc, not whitespace-only skip bug
        Dictionary<string, string> id = new();
        Dictionary<string, int> subtype = new();

        Dictionary<string, string> result = BuildLocalPortLabels(desc, id, subtype);

        Assert.False(result.ContainsKey("4"));
    }

    [Fact]
    public void BuildLocalPortLabels_MultiplePortsResolvedIndependently()
    {
        Dictionary<string, string> desc = new() { ["1"] = "Gi0/1" };
        Dictionary<string, string> id = new() { ["2"] = "eth1" };
        Dictionary<string, int> subtype = new() { ["2"] = 5 };

        Dictionary<string, string> result = BuildLocalPortLabels(desc, id, subtype);

        Assert.Equal(2, result.Count);
        Assert.Equal("Gi0/1", result["1"]);
        Assert.Equal("eth1", result["2"]);
    }
}