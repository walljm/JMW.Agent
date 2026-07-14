using JMW.Discovery.Core;
using JMW.Discovery.Server.Reporting;

namespace JMW.Discovery.Tests;

/// <summary>
/// Unit tests for the reporting cursor helper and the change-feed value renderer.
/// These cover logic the schema validators do not exercise: parts-based keyset
/// cursor round-tripping and kind-aware rendering of facts_history values.
/// </summary>
public sealed class ReportingHelpersTests
{
    // ── KeysetCursor parts-based round-trip ───────────────────────────────────

    [Theory]
    [InlineData("dev-1", "tcp:0.0.0.0:22")]
    [InlineData("router-7", "/var/lib/data")]
    [InlineData("", "")]
    public void EncodeParts_RoundTrips_TwoParts(string a, string b)
    {
        string cursor = KeysetCursor.EncodeParts(a, b);

        Assert.True(KeysetCursor.TryDecodeParts(cursor, 2, out string[] parts));
        Assert.Equal(a, parts[0]);
        Assert.Equal(b, parts[1]);
    }

    [Fact]
    public void EncodeParts_RoundTrips_SinglePart()
    {
        string cursor = KeysetCursor.EncodeParts("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

        Assert.True(KeysetCursor.TryDecodeParts(cursor, 1, out string[] parts));
        Assert.Single(parts);
        Assert.Equal("3f2504e0-4f89-41d3-9a0c-0305e82c3301", parts[0]);
    }

    [Theory]
    [InlineData("", 2)]
    [InlineData("notbase64!!!", 2)]
    public void TryDecodeParts_RejectsMalformedInput(string cursor, int count)
    {
        Assert.False(KeysetCursor.TryDecodeParts(cursor, count, out _));
    }

    [Fact]
    public void TryDecodeParts_RejectsWrongPartCount()
    {
        string cursor = KeysetCursor.EncodeParts("a", "b");

        Assert.False(KeysetCursor.TryDecodeParts(cursor, 1, out _));
        Assert.False(KeysetCursor.TryDecodeParts(cursor, 3, out _));
    }

    [Fact]
    public void EncodeParts_DoesNotCollideWithGuidCursor()
    {
        // The GUID-based decoder must still reject a parts cursor whose tail is not a GUID.
        string cursor = KeysetCursor.EncodeParts("hostname", "not-a-guid");
        Assert.False(KeysetCursor.TryDecode(cursor, out _, out _));
    }

    // ── Change-feed value rendering (kind-aware) ──────────────────────────────

    [Fact]
    public void RenderValue_String_FromValueStr()
    {
        string result = ChangesApi.RenderValue((short)FactValueKind.String, "eth0", null, null);
        Assert.Equal("eth0", result);
    }

    [Fact]
    public void RenderValue_IpAddress_FromValueStr()
    {
        // IPv4/IPv6/IPPrefix/Mac all land in value_str as human-readable text.
        string result = ChangesApi.RenderValue((short)FactValueKind.IPv4Address, "10.0.0.5", null, null);
        Assert.Equal("10.0.0.5", result);
    }

    [Fact]
    public void RenderValue_Long_FromValueLong()
    {
        string result = ChangesApi.RenderValue((short)FactValueKind.Long, null, 4096, null);
        Assert.Equal("4096", result);
    }

    [Fact]
    public void RenderValue_Double_FromValueDouble()
    {
        string result = ChangesApi.RenderValue((short)FactValueKind.Double, null, null, 42.5);
        Assert.Equal("42.5", result);
    }

    [Theory]
    [InlineData(1, "true")]
    [InlineData(0, "false")]
    public void RenderValue_Bool_RendersTrueFalse_NotOneZero(long stored, string expected)
    {
        string result = ChangesApi.RenderValue((short)FactValueKind.Bool, null, stored, null);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void RenderValue_DateTimeOffset_RendersIsoTimestamp_NotTicks()
    {
        DateTimeOffset moment = new(2026, 6, 4, 13, 5, 9, TimeSpan.Zero);
        string result = ChangesApi.RenderValue((short)FactValueKind.DateTimeOffset, null, moment.UtcTicks, null);
        Assert.Equal("2026-06-04 13:05:09 UTC", result);
    }

    [Fact]
    public void RenderValue_TimeSpan_RendersDuration_NotTicks()
    {
        TimeSpan span = TimeSpan.FromHours(2) + TimeSpan.FromMinutes(30);
        string result = ChangesApi.RenderValue((short)FactValueKind.TimeSpan, null, span.Ticks, null);
        Assert.Equal(span.ToString(), result);
    }

    [Fact]
    public void RenderValue_NullValues_RendersDash()
    {
        string result = ChangesApi.RenderValue((short)FactValueKind.String, null, null, null);
        Assert.Equal("—", result);
    }

    // ── since-window translation ──────────────────────────────────────────────

    [Theory]
    [InlineData("1h")]
    [InlineData("6h")]
    [InlineData("24h")]
    [InlineData("7d")]
    public void ResolveSince_KnownWindows_ReturnPastTimestamp(string token)
    {
        DateTimeOffset? result = ChangesApi.ResolveSince(token);
        Assert.NotNull(result);
        Assert.True(result.Value < DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("bogus")]
    public void ResolveSince_UnknownOrEmpty_ReturnsNull(string? token)
    {
        Assert.Null(ChangesApi.ResolveSince(token));
    }
}