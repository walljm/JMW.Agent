using JMW.Discovery.Core;

namespace JMW.Discovery.Tests;

public sealed class FactSegmentTests
{
    // ── ParsePath ─────────────────────────────────────────────────────────────

    [Fact]
    public void ParsePath_DeviceInterfaceAttribute_ThreeSegments()
    {
        FactSegment[] segs = FactSegment.ParsePath("Device[router-1].Interface[eth0].Speed");

        Assert.Equal(3, segs.Length);

        Assert.Equal("Device", segs[0].Name);
        Assert.Equal("router-1", segs[0].Key);
        Assert.True(segs[0].IsList);

        Assert.Equal("Interface", segs[1].Name);
        Assert.Equal("eth0", segs[1].Key);
        Assert.True(segs[1].IsList);

        Assert.Equal("Speed", segs[2].Name);
        Assert.Null(segs[2].Key);
        Assert.False(segs[2].IsList);
    }

    [Fact]
    public void ParsePath_BareAttribute_OneSegment()
    {
        FactSegment[] segs = FactSegment.ParsePath("CollectedAt");

        Assert.Single(segs);
        Assert.Equal("CollectedAt", segs[0].Name);
        Assert.Null(segs[0].Key);
    }

    [Fact]
    public void ParsePath_DeepNesting_FourSegments()
    {
        FactSegment[] segs = FactSegment.ParsePath("Device[r1].Vrf[default].BgpNeighbor[10.0.0.1].State");

        Assert.Equal(4, segs.Length);
        Assert.Equal("Device", segs[0].Name);
        Assert.Equal("r1", segs[0].Key);
        Assert.Equal("Vrf", segs[1].Name);
        Assert.Equal("default", segs[1].Key);
        Assert.Equal("BgpNeighbor", segs[2].Name);
        Assert.Equal("10.0.0.1", segs[2].Key);
        Assert.Equal("State", segs[3].Name);
        Assert.Null(segs[3].Key);
    }

    [Fact]
    public void ParsePath_NetworkPrefix_HandlesSlashInKey()
    {
        FactSegment[] segs = FactSegment.ParsePath("Network[10.0.0.0/24].Origin");

        Assert.Equal(2, segs.Length);
        Assert.Equal("Network", segs[0].Name);
        Assert.Equal("10.0.0.0/24", segs[0].Key);
        Assert.Equal("Origin", segs[1].Name);
    }

    [Fact]
    public void ParsePath_EmptyString_ReturnsEmpty() =>
        Assert.Empty(FactSegment.ParsePath(""));

    [Fact]
    public void ParsePath_Nullish_ReturnsEmpty() =>
        Assert.Empty(FactSegment.ParsePath(""));

    // ── ToString ──────────────────────────────────────────────────────────────

    [Fact]
    public void ToString_ListSegment_IncludesBrackets() =>
        Assert.Equal("Device[router-1]", new FactSegment("Device", "router-1").ToString());

    [Fact]
    public void ToString_BareSegment_NoBrackets() =>
        Assert.Equal("Speed", new FactSegment("Speed", null).ToString());

    // ── IsList ────────────────────────────────────────────────────────────────

    [Fact]
    public void IsList_WithKey_True() =>
        Assert.True(new FactSegment("Device", "r1").IsList);

    [Fact]
    public void IsList_WithoutKey_False() =>
        Assert.False(new FactSegment("Speed", null).IsList);

    // ── Empty brackets (structural / attribute_path form) ─────────────────────
    //
    // "Device[]" means "this is a list position" without specifying the key.
    // Used in attribute_path to mark list positions: "Device[].Interface[].Speed"

    [Fact]
    public void ParsePath_EmptyBrackets_ParsesAsListWithEmptyKey()
    {
        FactSegment[] segs = FactSegment.ParsePath("Device[].Inventory.Modules[].Name");

        Assert.Equal(4, segs.Length);

        Assert.Equal("Device", segs[0].Name);
        Assert.Equal("", segs[0].Key); // empty string, not null
        Assert.True(segs[0].IsList);

        Assert.Equal("Inventory", segs[1].Name);
        Assert.Null(segs[1].Key);
        Assert.False(segs[1].IsList); // bare segment — no brackets

        Assert.Equal("Modules", segs[2].Name);
        Assert.Equal("", segs[2].Key);
        Assert.True(segs[2].IsList);

        Assert.Equal("Name", segs[3].Name);
        Assert.Null(segs[3].Key);
        Assert.False(segs[3].IsList);
    }

    [Fact]
    public void ToString_EmptyKey_ProducesEmptyBrackets() =>
        Assert.Equal("Device[]", new FactSegment("Device", "").ToString());

    [Fact]
    public void IsList_EmptyStringKey_True() =>
        Assert.True(new FactSegment("Device", "").IsList); // "" is not null

    [Fact]
    public void IsList_NullKey_False() =>
        Assert.False(new FactSegment("Device", null).IsList);

    // Round-trip: full ID → AttributePath → re-parse preserves structure
    [Fact]
    public void AttributePath_ReParsePreservesListPositions()
    {
        // Start with a full ID, derive the structural path, re-parse it
        string fullId = "Device[router-1].Inventory.Modules[card0].SerialNumber";
        FactSegment[] fullSegs = FactSegment.ParsePath(fullId);

        string attrPath = string.Join(".", fullSegs.Select(s => s.IsList ? $"{s.Name}[]" : s.Name));
        FactSegment[] attrSegs = FactSegment.ParsePath(attrPath);

        Assert.Equal(fullSegs.Length, attrSegs.Length);
        for (int i = 0; i < fullSegs.Length; i++)
        {
            Assert.Equal(fullSegs[i].Name, attrSegs[i].Name);
            Assert.Equal(fullSegs[i].IsList, attrSegs[i].IsList);
            // Keys differ (full has "router-1", structural has ""), names and IsList match
        }
    }

    // ── Equality (record struct) ───────────────────────────────────────────────

    [Fact]
    public void EqualSegments_AreEqual() =>
        Assert.Equal(new FactSegment("Device", "r1"), new FactSegment("Device", "r1"));

    [Fact]
    public void DifferentSegments_AreNotEqual() =>
        Assert.NotEqual(new FactSegment("Device", "r1"), new FactSegment("Device", "r2"));
}