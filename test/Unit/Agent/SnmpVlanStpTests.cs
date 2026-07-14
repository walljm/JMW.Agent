using System.Reflection;

using JMW.Discovery.Agent.Collection.Device;

namespace JMW.Discovery.UnitTests.Agent;

/// <summary>
/// <see cref="SnmpCollector" />'s private VLAN/STP helpers: Q-BRIDGE-MIB PortList bitmap
/// decoding (RFC 2674), tagged-VLAN derivation (egress minus untagged), BRIDGE-MIB bridge ID
/// formatting, and STP role computation. All pure logic, tested via reflection the same way as
/// <see cref="SnmpVendorResolutionTests" /> and <see cref="SnmpLldpLocalPortTests" />.
/// </summary>
public sealed class SnmpVlanStpTests
{
    private static T Invoke<T>(string methodName, params object?[] args)
    {
        MethodInfo m = typeof(SnmpCollector).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
         ?? throw new InvalidOperationException($"SnmpCollector.{methodName} not found.");
        return (T)m.Invoke(null, args)!;
    }

    // ── DecodePortList ────────────────────────────────────────────────────────

    [Fact]
    public void DecodePortList_FirstOctetBitsMapToPorts1Through8()
    {
        // 0xFF = all 8 bits set = ports 1..8, MSB (0x80) = port 1.
        HashSet<int> ports = Invoke<HashSet<int>>("DecodePortList", new byte[] { 0xFF });
        Assert.Equal(new HashSet<int> { 1, 2, 3, 4, 5, 6, 7, 8 }, ports);
    }

    [Fact]
    public void DecodePortList_SecondOctetMapsToPorts9Through16()
    {
        // 0x80 in the second octet = port 9 (MSB of octet index 1 = 8*1 + 0 + 1 = 9).
        HashSet<int> ports = Invoke<HashSet<int>>("DecodePortList", new byte[] { 0x00, 0x80 });
        Assert.Equal(new HashSet<int> { 9 }, ports);
    }

    [Fact]
    public void DecodePortList_SingleBitInMiddle()
    {
        // 0x04 = bit index 5 (0-based from MSB) = port 6.
        HashSet<int> ports = Invoke<HashSet<int>>("DecodePortList", new byte[] { 0x04 });
        Assert.Equal(new HashSet<int> { 6 }, ports);
    }

    [Fact]
    public void DecodePortList_EmptyBytesYieldsNoPorts()
    {
        HashSet<int> ports = Invoke<HashSet<int>>("DecodePortList", Array.Empty<byte>());
        Assert.Empty(ports);
    }

    [Fact]
    public void DecodePortList_AllZeroYieldsNoPorts()
    {
        HashSet<int> ports = Invoke<HashSet<int>>("DecodePortList", new byte[] { 0x00, 0x00 });
        Assert.Empty(ports);
    }

    // ── BuildTaggedVlanMembership ─────────────────────────────────────────────

    [Fact]
    public void BuildTaggedVlanMembership_EgressMinusUntagged_IsTagged()
    {
        Dictionary<int, HashSet<int>> egress = new() { [10] = [1, 2, 3] };
        Dictionary<int, HashSet<int>> untagged = new() { [10] = [1] }; // port 1 is access/untagged on VLAN 10

        Dictionary<int, List<int>> result = Invoke<Dictionary<int, List<int>>>(
            "BuildTaggedVlanMembership",
            egress,
            untagged
        );

        Assert.False(result.ContainsKey(1)); // untagged, not reported as tagged
        Assert.Equal([10], result[2]);
        Assert.Equal([10], result[3]);
    }

    [Fact]
    public void BuildTaggedVlanMembership_PortTaggedOnMultipleVlans()
    {
        Dictionary<int, HashSet<int>> egress = new() { [10] = [5], [20] = [5] };
        Dictionary<int, HashSet<int>> untagged = new();

        Dictionary<int, List<int>> result = Invoke<Dictionary<int, List<int>>>(
            "BuildTaggedVlanMembership",
            egress,
            untagged
        );

        Assert.Equal(new HashSet<int> { 10, 20 }, result[5].ToHashSet());
    }

    [Fact]
    public void BuildTaggedVlanMembership_VlanWithNoUntaggedEntry_TreatsAllEgressAsTagged()
    {
        Dictionary<int, HashSet<int>> egress = new() { [30] = [7] };
        Dictionary<int, HashSet<int>> untagged = new(); // no entry at all for VLAN 30

        Dictionary<int, List<int>> result = Invoke<Dictionary<int, List<int>>>(
            "BuildTaggedVlanMembership",
            egress,
            untagged
        );

        Assert.Equal([30], result[7]);
    }

    // ── FormatBridgeId ────────────────────────────────────────────────────────

    [Fact]
    public void FormatBridgeId_CombinesPriorityAndMac()
    {
        byte[] mac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
        string id = Invoke<string>("FormatBridgeId", 32768, mac);
        Assert.Equal("32768:00:11:22:33:44:55", id);
    }

    // ── ComputeStpRole ────────────────────────────────────────────────────────

    [Fact]
    public void ComputeStpRole_MatchingRootPort_ReturnsRoot()
    {
        string? role = Invoke<string?>("ComputeStpRole", 3, 3, "other-bridge", "our-bridge", 5 /* forwarding */);
        Assert.Equal("root", role);
    }

    [Fact]
    public void ComputeStpRole_DesignatedByOurOwnBridge_ReturnsDesignated()
    {
        string? role = Invoke<string?>("ComputeStpRole", 3, 7, "our-bridge", "our-bridge", 5 /* forwarding */);
        Assert.Equal("designated", role);
    }

    [Fact]
    public void ComputeStpRole_BlockingAndNotRootOrDesignated_ReturnsAlternate()
    {
        string? role = Invoke<string?>("ComputeStpRole", 3, 7, "other-bridge", "our-bridge", 2 /* blocking */);
        Assert.Equal("alternate", role);
    }

    [Fact]
    public void ComputeStpRole_DisabledState_ReturnsDisabled()
    {
        string? role = Invoke<string?>("ComputeStpRole", 3, 7, "other-bridge", "our-bridge", 1 /* disabled */);
        Assert.Equal("disabled", role);
    }

    [Fact]
    public void ComputeStpRole_RootPortTakesPriorityOverDesignatedMatch()
    {
        // Same port is both the root port AND (degenerately) matches our own bridge id —
        // root should win since it's checked first.
        string? role = Invoke<string?>("ComputeStpRole", 5, 5, "our-bridge", "our-bridge", 5 /* forwarding */);
        Assert.Equal("root", role);
    }
}
