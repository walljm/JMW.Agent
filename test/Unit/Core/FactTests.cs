using System.Net;
using System.Text.Json;

using JMW.Discovery.Core;

namespace JMW.Discovery.Tests;

public sealed class FactTests
{
    // ── DimKey / Attribute derivation ─────────────────────────────────────────
    // The projection router indexes on (DimKey, Attribute), where DimKey is ALL
    // list segments in path order and Attribute is the bare tail after the LAST
    // list segment. Dimensions separated by bare grouping segments (e.g.
    // Service[x].DNS.Zone[y].Type) must still contribute every list segment —
    // a regression here silently unroutes every multi-dimension projection.

    [Theory]
    [InlineData("Device[r1].Interface[eth0].Speed", "Device|Interface", "Speed")]
    [InlineData("Device[r1].OS.Hostname", "Device", "OS.Hostname")]
    [InlineData("Service[s1].DNS.Stats.TotalQueries", "Service", "DNS.Stats.TotalQueries")]
    [InlineData("Service[s1].DNS.Zone[home].Type", "Service|Zone", "Type")]
    [InlineData("Service[s1].DNS.Zone[home].Record[web.home].IP", "Service|Zone|Record", "IP")]
    [InlineData("Service[s1].DHCP.Scope[lan].Lease[aa:bb].Hostname", "Service|Scope|Lease", "Hostname")]
    [InlineData("Device[d1].Modbus.Holding[40001].Value", "Device|Holding", "Value")]
    [InlineData("Hostname", "", "Hostname")]
    [InlineData("Device[r1].Interface[eth0]", "Device|Interface", "")]
    public void Create_DerivesDimKeyAndAttribute(string id, string expectedDimKey, string expectedAttribute)
    {
        Fact f = Fact.Create(id, "v");
        Assert.Equal(expectedDimKey, f.DimKey);
        Assert.Equal(expectedAttribute, f.Attribute);
    }

    // ── Create factory methods ────────────────────────────────────────────────

    [Fact]
    public void Create_String_SetsValueKindAndId()
    {
        Fact f = Fact.Create("Device[r1].Hostname", "router-1");
        Assert.Equal("Device[r1].Hostname", f.Id);
        Assert.Equal(FactValueKind.String, f.Value.Kind);
        Assert.Equal("router-1", f.Value.AsString());
    }

    [Fact]
    public void Create_Long_SetsValueKind()
    {
        Fact f = Fact.Create("Device[r1].Interface[eth0].Speed", 1_000_000_000L);
        Assert.Equal(FactValueKind.Long, f.Value.Kind);
        Assert.Equal(1_000_000_000L, f.Value.AsLong());
    }

    [Fact]
    public void Create_Bool_SetsValueKind()
    {
        Fact f = Fact.Create("Device[r1].Interface[eth0].Enabled", true);
        Assert.Equal(FactValueKind.Bool, f.Value.Kind);
        Assert.True(f.Value.AsBool());
    }

    [Fact]
    public void Create_WithExplicitCollectedAt_UsesProvidedTime()
    {
        DateTimeOffset ts = new(2026, 6, 4, 0, 0, 0, TimeSpan.Zero);
        Fact f = Fact.Create("Device[r1].Hostname", "router-1", ts);
        Assert.Equal(ts, f.CollectedAt);
    }

    [Fact]
    public void Create_WithoutCollectedAt_UsesUtcNow()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;
        Fact f = Fact.Create("Device[r1].Hostname", "router-1");
        DateTimeOffset after = DateTimeOffset.UtcNow;
        Assert.InRange(f.CollectedAt, before, after);
    }

    [Fact]
    public void Create_IPAddress_SetsCorrectKind()
    {
        Fact f = Fact.Create("Device[r1].Interface[eth0].IPAddr", IPAddress.Parse("10.0.0.1"));
        Assert.Equal(FactValueKind.IPv4Address, f.Value.Kind);
    }

    [Fact]
    public void Create_IPNetwork_SetsCorrectKind()
    {
        Fact f = Fact.Create("Network[10.0.0.0/24].Origin", IPNetwork.Parse("10.0.0.0/24"));
        Assert.Equal(FactValueKind.IPPrefix, f.Value.Kind);
    }

    // ── ParseId ───────────────────────────────────────────────────────────────

    [Fact]
    public void ParseId_ReturnsCorrectSegments()
    {
        Fact f = Fact.Create("Device[r1].Interface[eth0].Speed", 1000L);
        FactSegment[] segs = f.ParseId();

        Assert.Equal(3, segs.Length);
        Assert.Equal("Device", segs[0].Name);
        Assert.Equal("r1", segs[0].Key);
        Assert.Equal("Interface", segs[1].Name);
        Assert.Equal("eth0", segs[1].Key);
        Assert.Equal("Speed", segs[2].Name);
        Assert.Null(segs[2].Key);
    }

    // ── JSON round-trip ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("string", "Device[r1].Name")]
    [InlineData("long", "Device[r1].Speed")]
    [InlineData("bool", "Device[r1].Enabled")]
    [InlineData("ipv4", "Device[r1].Addr")]
    [InlineData("ipv6", "Device[r1].AddrV6")]
    [InlineData("dateTimeOffset", "Device[r1].LastSeen")]
    [InlineData("timeSpan", "Device[r1].Uptime")]
    public void JsonRoundTrip_PreservesValueAndId(string valueType, string id)
    {
        Fact original = valueType switch
        {
            "string" => Fact.Create(id, "test-value"),
            "long" => Fact.Create(id, 12345L),
            "bool" => Fact.Create(id, true),
            "ipv4" => Fact.Create(id, IPAddress.Parse("10.0.0.1")),
            "ipv6" => Fact.Create(id, IPAddress.Parse("2001:db8::1")),
            "dateTimeOffset" => Fact.Create(id, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            "timeSpan" => Fact.Create(id, TimeSpan.FromHours(24)),
            _ => throw new InvalidOperationException(),
        };

        string json = JsonSerializer.Serialize(original);
        Fact back = JsonSerializer.Deserialize<Fact>(json);

        Assert.Equal(original.Id, back.Id);
        Assert.Equal(original.Value, back.Value);
    }

    // ── Source (provenance) ────────────────────────────────────────────────────
    // FactSource ordinals are persisted (facts_history.source) so the wire format sends
    // the ordinal, not the name -- cheaper across thousands of facts per batch, and
    // stable even if a member were ever renamed (the enum's doc comment forbids
    // renumbering, but a rename wouldn't affect an ordinal-based wire format either way).

    [Fact]
    public void Create_DefaultsSourceToUnknown() =>
        Assert.Equal(FactSource.Unknown, Fact.Create("Device[r1].Hostname", "router-1").Source);

    [Fact]
    public void JsonRoundTrip_PreservesSource()
    {
        Fact original = Fact.Create("Device[r1].Hostname", "router-1") with { Source = FactSource.HttpBanner };

        string json = JsonSerializer.Serialize(original);
        Fact back = JsonSerializer.Deserialize<Fact>(json);

        Assert.Equal(FactSource.HttpBanner, back.Source);
    }

    [Fact]
    public void JsonRoundTrip_SendsSourceAsOrdinal_NotName()
    {
        // Locks the wire format: "source" must be a JSON number, not a string, so an
        // accidental revert to name-based serialization is caught here rather than only
        // showing up as a bandwidth regression in production.
        Fact fact = Fact.Create("Device[r1].Hostname", "router-1") with { Source = FactSource.HttpBanner };
        string json = JsonSerializer.Serialize(fact);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement sourceElement = doc.RootElement.GetProperty("source");
        Assert.Equal(JsonValueKind.Number, sourceElement.ValueKind);
        Assert.Equal((int)FactSource.HttpBanner, sourceElement.GetInt32());
    }

    [Fact]
    public void JsonDeserialize_MissingSource_DefaultsToUnknown()
    {
        // Backward compatibility: an older agent build that doesn't send "source" at all
        // must not fail deserialization -- it should just come back as Unknown.
        Fact back = JsonSerializer.Deserialize<Fact>(
            """{"id":"Device[r1].Hostname","value":{"kind":"String","value":"router-1"}}"""
        );

        Assert.Equal(FactSource.Unknown, back.Source);
    }

    [Fact]
    public void Equals_IgnoresSource()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Fact a = Fact.Create("Device[r1].Hostname", "router-1", now) with { Source = FactSource.HttpBanner };
        Fact b = Fact.Create("Device[r1].Hostname", "router-1", now) with { Source = FactSource.Nbns };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    // ── Value equality across Create overloads ────────────────────────────────

    [Fact]
    public void Create_FactValue_SameAsTypedOverload()
    {
        Fact a = Fact.Create("Device[r1].Speed", 1000L);
        Fact b = Fact.Create("Device[r1].Speed", FactValue.FromLong(1000L));
        Assert.Equal(a.Value, b.Value);
    }

    // ── AttributePath ─────────────────────────────────────────────────────────
    // Empty brackets mark list positions; bare segments have no brackets.

    [Theory]
    [InlineData("Device[router-1].Interface[eth0].Speed", "Device[].Interface[].Speed")]
    [InlineData("Device[router-1].Hostname", "Device[].Hostname")]
    [InlineData("CollectedAt", "CollectedAt")]
    [InlineData("Device[r1].Inventory.Modules[0].Name", "Device[].Inventory.Modules[].Name")]
    [InlineData("Network[10.0.0.0/24].Origin", "Network[].Origin")]
    [InlineData(
        "Device[r1].Vrf[default].BgpNeighbor[10.0.0.1].State",
        "Device[].Vrf[].BgpNeighbor[].State"
    )]
    public void AttributePath_EmptyBracketsForListSegments(string id, string expected)
    {
        Fact f = Fact.Create(id, 0L);
        Assert.Equal(expected, f.AttributePath);
    }

    [Fact]
    public void AttributePath_BareAttribute_NoChange()
    {
        // A bare attribute (no list segments anywhere) has no brackets
        Assert.Equal("CollectedAt", Fact.Create("CollectedAt", "now").AttributePath);
    }

    [Fact]
    public void AttributePath_InterleavedBareSegments_OnlyListsGetBrackets()
    {
        // "Inventory" is a bare grouping segment — no brackets
        Fact f = Fact.Create("Device[r1].Inventory.Modules[card0].SerialNumber", "SN1234");
        Assert.Equal("Device[].Inventory.Modules[].SerialNumber", f.AttributePath);
    }

    // ── NUL sanitization ───────────────────────────────────────────────────────
    // A raw NUL surviving into a list-segment key gets JSON-serialized into
    // KeyValuesJson as a six-character escape sequence -- by the time a later pass
    // looks for a literal NUL character to strip, it's gone (replaced by ordinary
    // ASCII), and Postgres's json/jsonb parser rejects that escape outright
    // (22P05). The only place that reliably catches it is before Create() parses
    // id into segments. Found deploying against a real NBNS name containing a
    // trailing NUL from the raw 15-byte NetBIOS name field.

    [Fact]
    public void Create_NulInListSegmentKey_StripsFromIdAndKeyValuesJson()
    {
        Fact f = Fact.Create("Discovered[192.168.1.71].NbnsName[-NAS-40\0\0].Ip", "192.168.1.71");

        Assert.DoesNotContain('\0', f.Id);
        Assert.DoesNotContain('\0', f.KeyValuesJson);
        Assert.DoesNotContain("\\u0000", f.KeyValuesJson, StringComparison.Ordinal);

        // The sanitized JSON must still parse cleanly and round-trip the surviving text.
        using JsonDocument doc = JsonDocument.Parse(f.KeyValuesJson);
        Assert.Equal("-NAS-40", doc.RootElement.GetProperty("NbnsName").GetString());
    }

    [Fact]
    public void Create_NulInStringValue_StripsFromValue()
    {
        Fact f = Fact.Create("Device[r1].Hostname", "router-1\0\0");
        Assert.DoesNotContain('\0', f.Value.AsString() ?? "");
        Assert.Equal("router-1", f.Value.AsString());
    }

    [Fact]
    public void Create_NoNul_LeavesTextUnchanged()
    {
        // Sanity check: the sanitizer must not alter clean input.
        Fact f = Fact.Create("Discovered[192.168.1.71].NbnsName[NAS-40].Ip", "192.168.1.71");
        Assert.Equal("NAS-40", JsonDocument.Parse(f.KeyValuesJson).RootElement.GetProperty("NbnsName").GetString());
    }
}