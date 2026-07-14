using System.Net;
using System.Net.NetworkInformation;

using JMW.Discovery.Core;

namespace JMW.Discovery.Tests;

public sealed class FactValueTests
{
    // ── Kind and null ─────────────────────────────────────────────────────────

    [Fact]
    public void Null_HasNullKind() =>
        Assert.Equal(FactValueKind.Null, FactValue.Null.Kind);

    [Fact]
    public void Null_AllAccessorsReturnNull()
    {
        Assert.Null(FactValue.Null.AsString());
        Assert.Null(FactValue.Null.AsLong());
        Assert.Null(FactValue.Null.AsDouble());
        Assert.Null(FactValue.Null.AsBool());
        Assert.Null(FactValue.Null.AsDateTimeOffset());
        Assert.Null(FactValue.Null.AsTimeSpan());
    }

    // ── Primitive round-trips ─────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("Device[r1].Interface[eth0]")]
    public void String_RoundTrips(string value)
    {
        FactValue fv = FactValue.FromString(value);
        Assert.Equal(FactValueKind.String, fv.Kind);
        Assert.Equal(value, fv.AsString());
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(1_000_000_000L)]
    public void Long_RoundTrips(long value)
    {
        FactValue fv = FactValue.FromLong(value);
        Assert.Equal(FactValueKind.Long, fv.Kind);
        Assert.Equal(value, fv.AsLong());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(3.14)]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    public void Double_RoundTrips(double value)
    {
        FactValue fv = FactValue.FromDouble(value);
        Assert.Equal(FactValueKind.Double, fv.Kind);
        Assert.Equal(value, fv.AsDouble());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Bool_RoundTrips(bool value)
    {
        FactValue fv = FactValue.FromBool(value);
        Assert.Equal(FactValueKind.Bool, fv.Kind);
        Assert.Equal(value, fv.AsBool());
    }

    [Fact]
    public void DateTimeOffset_RoundTrips()
    {
        DateTimeOffset ts = new(2026, 6, 4, 12, 0, 0, TimeSpan.Zero);
        FactValue fv = FactValue.FromDateTimeOffset(ts);
        Assert.Equal(FactValueKind.DateTimeOffset, fv.Kind);
        Assert.Equal(ts, fv.AsDateTimeOffset());
    }

    [Fact]
    public void DateTimeOffset_AlwaysUtc()
    {
        DateTimeOffset local = new(2026, 6, 4, 12, 0, 0, TimeSpan.FromHours(5));
        FactValue fv = FactValue.FromDateTimeOffset(local);
        Assert.Equal(TimeSpan.Zero, fv.AsDateTimeOffset()!.Value.Offset);
        Assert.Equal(local.UtcDateTime, fv.AsDateTimeOffset()!.Value.UtcDateTime);
    }

    [Fact]
    public void TimeSpan_RoundTrips()
    {
        TimeSpan ts = TimeSpan.FromHours(42.5);
        FactValue fv = FactValue.FromTimeSpan(ts);
        Assert.Equal(FactValueKind.TimeSpan, fv.Kind);
        Assert.Equal(ts, fv.AsTimeSpan());
    }

    // ── Network types ─────────────────────────────────────────────────────────

    [Fact]
    public void IPv4_RoundTripsViaIPAddress()
    {
        IPAddress addr = IPAddress.Parse("192.168.1.1");
        FactValue fv = FactValue.FromIPAddress(addr);
        Assert.Equal(FactValueKind.IPv4Address, fv.Kind);
        Assert.Equal(addr, fv.ToIPAddress());
        Assert.Equal("192.168.1.1", fv.ToString());
    }

    [Fact]
    public void IPv6_RoundTripsViaIPAddress()
    {
        IPAddress addr = IPAddress.Parse("2001:db8::1");
        FactValue fv = FactValue.FromIPAddress(addr);
        Assert.Equal(FactValueKind.IPv6Address, fv.Kind);
        Assert.Equal(addr, fv.ToIPAddress());
    }

    [Fact]
    public void IPv4Prefix_RoundTrips()
    {
        IPNetwork net = IPNetwork.Parse("10.0.0.0/8");
        FactValue fv = FactValue.FromIPNetwork(net);
        Assert.Equal(FactValueKind.IPPrefix, fv.Kind);
        IPNetwork? back = fv.ToIPNetwork();
        Assert.NotNull(back);
        Assert.Equal(net.BaseAddress, back.Value.BaseAddress);
        Assert.Equal(net.PrefixLength, back.Value.PrefixLength);
        Assert.Equal("10.0.0.0/8", fv.ToString());
    }

    [Fact]
    public void IPv6Prefix_RoundTrips()
    {
        IPNetwork net = IPNetwork.Parse("2001:db8::/32");
        FactValue fv = FactValue.FromIPNetwork(net);
        IPNetwork? back = fv.ToIPNetwork();
        Assert.NotNull(back);
        Assert.Equal(net.BaseAddress, back.Value.BaseAddress);
        Assert.Equal(net.PrefixLength, back.Value.PrefixLength);
    }

    [Fact]
    public void MacAddress_RoundTrips()
    {
        long mac = 0x001A_2B3C_4D5EL;
        FactValue fv = FactValue.FromMacAddress(mac);
        Assert.Equal(FactValueKind.MacAddress, fv.Kind);
        Assert.Equal("00:1A:2B:3C:4D:5E", fv.ToString());
    }

    [Fact]
    public void PhysicalAddress_RoundTrips()
    {
        PhysicalAddress pa = PhysicalAddress.Parse("00-1A-2B-3C-4D-5E");
        FactValue fv = FactValue.FromPhysicalAddress(pa);
        Assert.Equal(FactValueKind.MacAddress, fv.Kind);
        Assert.Equal("00:1A:2B:3C:4D:5E", fv.ToString());
    }

    // ── Cross-accessor isolation ──────────────────────────────────────────────
    // A value of one kind must not bleed into another accessor.

    [Fact]
    public void LongValue_DoesNotAppearAsString()
    {
        FactValue fv = FactValue.FromLong(42);
        Assert.Null(fv.AsString());
        Assert.Null(fv.AsDouble());
        Assert.Null(fv.AsBool());
    }

    [Fact]
    public void StringValue_DoesNotAppearAsLong()
    {
        FactValue fv = FactValue.FromString("hello");
        Assert.Null(fv.AsLong());
        Assert.Null(fv.AsDouble());
        Assert.Null(fv.AsBool());
    }

    // ── Equality ──────────────────────────────────────────────────────────────

    [Fact]
    public void EqualValues_AreEqual()
    {
        Assert.Equal(FactValue.FromLong(42), FactValue.FromLong(42));
        Assert.Equal(FactValue.FromString("hello"), FactValue.FromString("hello"));
        Assert.Equal(FactValue.FromBool(true), FactValue.FromBool(true));
        Assert.Equal(FactValue.Null, FactValue.Null);
    }

    [Fact]
    public void DifferentValues_AreNotEqual()
    {
        Assert.NotEqual(FactValue.FromLong(42), FactValue.FromLong(43));
        Assert.NotEqual(FactValue.FromString("a"), FactValue.FromString("b"));
        Assert.NotEqual(FactValue.FromLong(1), FactValue.FromBool(true)); // different kind
        Assert.NotEqual(FactValue.FromLong(0), FactValue.Null);
    }

    [Fact]
    public void OperatorEquality_MatchesEquals()
    {
        FactValue a = FactValue.FromLong(42);
        FactValue b = FactValue.FromLong(42);
        FactValue c = FactValue.FromLong(99);
        Assert.True(a == b);
        Assert.False(a == c);
        Assert.True(a != c);
    }
}