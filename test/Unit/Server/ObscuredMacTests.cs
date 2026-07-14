using JMW.Discovery.Server;

namespace JMW.Discovery.UnitTests.Server;

public sealed class ObscuredMacTests
{
    [Theory]
    [InlineData("00e0bf1fc40*", true)]
    [InlineData("00:e0:bf:1f:c4:0*", true)]
    [InlineData("00e0bf400073", false)] // full MAC, not obscured
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsObscured(string? mac, bool expected) =>
        Assert.Equal(expected, ObscuredMac.IsObscured(mac));

    [Theory]
    [InlineData("00e0bf1fc40*", true, "00e0bf")] // only the OUI is trustworthy
    [InlineData("00:e0:bf:1f:c4:0*", true, "00e0bf")] // separators stripped
    [InlineData("00E0BF1FC40*", true, "00e0bf")] // lowercased
    [InlineData("ac67844409e*", true, "ac6784")]
    [InlineData("00e0*", false, "")] // fewer than 6 hex
    [InlineData("**", false, "")]
    public void TryGetOui(string mac, bool expectedOk, string expectedOui)
    {
        bool ok = ObscuredMac.TryGetOui(mac, out string oui);
        Assert.Equal(expectedOk, ok);
        Assert.Equal(expectedOui, oui);
    }

    [Fact]
    public void Pick_UniqueIpMac_MatchingOui_Resolves()
    {
        // The IP is attested for one real MAC whose OUI matches → reconstruct it.
        string? result = ObscuredMac.Pick(["00e0bf400073"], "00e0bf");
        Assert.Equal("00e0bf400073", result);
    }

    [Fact]
    public void Pick_NoIpMacs_ReturnsNull() =>
        Assert.Null(ObscuredMac.Pick([], "00e0bf"));

    [Fact]
    public void Pick_OuiMismatch_ReturnsNull()
    {
        // IP maps to a MAC of a different vendor — stale ARP / reassigned IP. Reject.
        string? result = ObscuredMac.Pick(["5c475edf3e42"], "ccf411");
        Assert.Null(result);
    }

    [Fact]
    public void Pick_MultipleMatchingOui_Ambiguous_ReturnsNull()
    {
        string? result = ObscuredMac.Pick(["00e0bf400073", "00e0bf990011"], "00e0bf");
        Assert.Null(result);
    }

    [Fact]
    public void Pick_FiltersToMatchingOui()
    {
        // IP has two MACs; only one matches the obscured OUI → unique, resolves.
        string? result = ObscuredMac.Pick(["5c475edf3e42", "00e0bf400073"], "00e0bf");
        Assert.Equal("00e0bf400073", result);
    }

    [Fact]
    public void Pick_IgnoresMalformedCandidates()
    {
        string? result = ObscuredMac.Pick([null, "00e0bf4000", "00e0bf400073"], "00e0bf");
        Assert.Equal("00e0bf400073", result);
    }
}