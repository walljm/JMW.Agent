using JMW.Discovery.Core;

namespace JMW.Discovery.UnitTests.Agent;

/// <summary>
/// Locks the contract of the shared <see cref="MacFormat.Normalize" /> (review item F7 — merged
/// from three previously-copied implementations, one of which was the zero-alloc scanner variant).
/// The three originals had no direct tests; this covers the merged behavior, including the macOS
/// leading-zero-omitted padding the fast path can't recover on its own.
/// </summary>
public sealed class MacFormatTests
{
    [Theory]
    // Full MAC, every separator style → canonical lowercase colon form.
    [InlineData("aa:bb:cc:dd:ee:ff", "aa:bb:cc:dd:ee:ff")]
    [InlineData("AA:BB:CC:DD:EE:FF", "aa:bb:cc:dd:ee:ff")]
    [InlineData("aabbccddeeff", "aa:bb:cc:dd:ee:ff")]
    [InlineData("AA-BB-CC-DD-EE-FF", "aa:bb:cc:dd:ee:ff")]
    [InlineData("aabb.ccdd.eeff", "aa:bb:cc:dd:ee:ff")] // Cisco dotted
    // macOS drops the leading zero per octet → must be padded back to two.
    [InlineData("0:11:22:33:44:5", "00:11:22:33:44:05")]
    [InlineData("1:2:3:4:5:6", "01:02:03:04:05:06")]
    // Not a recognizable MAC → lowercased input, unchanged shape.
    [InlineData("not-a-mac", "not-a-mac")]
    [InlineData("aa:bb:cc:dd:ee", "aa:bb:cc:dd:ee")] // 5 octets → neither path
    [InlineData("", "")]
    public void Normalize_ProducesCanonicalForm(string raw, string expected) =>
        Assert.Equal(expected, MacFormat.Normalize(raw));

    [Theory]
    // Canonical bare 12-hex, any separator style → stripped + lowercased.
    [InlineData("aa:bb:cc:dd:ee:ff", "aabbccddeeff")]
    [InlineData("AA:BB:CC:DD:EE:FF", "aabbccddeeff")]
    [InlineData("aabbccddeeff", "aabbccddeeff")]
    [InlineData("aa-bb-cc-dd-ee-ff", "aabbccddeeff")]
    [InlineData("aabb.ccdd.eeff", "aabbccddeeff")]
    // macOS drops the leading zero per octet — ToBareHex must pad it back (the D34-critical case).
    [InlineData("0:11:22:33:44:5", "001122334405")]
    [InlineData("1:2:3:4:5:6", "010203040506")]
    public void ToBareHex_ValidMac_ReturnsBareHex(string raw, string expected) =>
        Assert.Equal(expected, MacFormat.ToBareHex(raw));

    [Theory]
    [InlineData("not-a-mac")]
    [InlineData("aa:bb:cc:dd:ee")] // 5 octets
    [InlineData("aabbccddeeff00")] // too long
    [InlineData("gg:bb:cc:dd:ee:ff")] // non-hex octet
    [InlineData("")]
    [InlineData(null)]
    public void ToBareHex_NotAMac_ReturnsNull(string? raw) =>
        Assert.Null(MacFormat.ToBareHex(raw));
}