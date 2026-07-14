using JMW.Discovery.Core;

namespace JMW.Discovery.Tests;

public sealed class FingerprintNormalizerTests
{
    // ── MAC ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("00:1a:2b:3c:4d:5e", "001a2b3c4d5e")] // colon-separated
    [InlineData("00-1A-2B-3C-4D-5E", "001a2b3c4d5e")] // dash-separated, uppercase
    [InlineData("001a.2b3c.4d5e", "001a2b3c4d5e")] // dot-separated
    [InlineData("001A2B3C4D5E", "001a2b3c4d5e")] // no separators, uppercase
    [InlineData("  00:1a:2b:3c:4d:5e  ", "001a2b3c4d5e")] // leading/trailing whitespace
    public void NormalizeMac_ValidInputs_ReturnsCanonicalForm(string input, string expected) =>
        Assert.Equal(expected, FingerprintNormalizer.NormalizeMac(input));

    [Theory]
    [InlineData("aa:bb:cc:dd:ee:ff")] // 0xAA — bit 1 set — locally administered
    [InlineData("02:00:00:00:00:01")] // 0x02 — LA bit
    [InlineData("fe:00:00:00:00:01")] // 0xFE — LA bit
    [InlineData("01:00:5e:00:00:01")] // multicast (bit 0 set)
    [InlineData("ff:ff:ff:ff:ff:ff")] // broadcast
    [InlineData("33:33:00:00:00:01")] // IPv6 multicast
    [InlineData("000000000000")] // all zeros
    [InlineData("ffffffffffff")] // broadcast (no separators)
    [InlineData("00:1a:2b:3c:4d")] // too short
    [InlineData("00:1a:2b:3c:4d:5e:6f")] // too long
    [InlineData("zz:bb:cc:dd:ee:ff")] // invalid hex
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeMac_InvalidInputs_ReturnsNull(string input) =>
        Assert.Null(FingerprintNormalizer.NormalizeMac(input));

    // ── Chassis serial ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("FTX2144ABCD", "cisco", "cisco:ftx2144abcd")]
    [InlineData("  SN12345  ", "juniper", "juniper:sn12345")]
    [InlineData("SN-12345", "arista", "arista:sn-12345")]
    [InlineData("A1B2C3D4", "f5", "f5:a1b2c3d4")]
    [InlineData("SN12345", "Palo Alto", "palo-alto:sn12345")] // vendor normalized
    public void NormalizeSerial_ValidInputs_ReturnsVendorScopedForm(
        string serial,
        string vendor,
        string expected
    ) =>
        Assert.Equal(expected, FingerprintNormalizer.NormalizeSerial(serial, vendor));

    [Theory]
    [InlineData("N/A", "cisco")]
    [InlineData("n/a", "cisco")]
    [InlineData("none", "cisco")]
    [InlineData("unknown", "cisco")]
    [InlineData("tbd", "cisco")]
    [InlineData("0000000000", "cisco")] // all same character
    [InlineData("??????????", "cisco")] // all same character
    [InlineData("abc", "cisco")] // too short (< 4 chars)
    [InlineData("SN1", "cisco")] // too short
    [InlineData("SN12345", null)] // no vendor
    [InlineData("SN12345", "")] // empty vendor
    [InlineData("", "cisco")]
    public void NormalizeSerial_InvalidInputs_ReturnsNull(string serial, string? vendor) =>
        Assert.Null(FingerprintNormalizer.NormalizeSerial(serial, vendor));

    // ── UUID ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("550e8400-e29b-41d4-a716-446655440000", "550e8400-e29b-41d4-a716-446655440000")]
    [InlineData("550E8400-E29B-41D4-A716-446655440000", "550e8400-e29b-41d4-a716-446655440000")] // uppercase
    [InlineData("{550e8400-e29b-41d4-a716-446655440000}", "550e8400-e29b-41d4-a716-446655440000")] // braces
    [InlineData("550e8400e29b41d4a716446655440000", "550e8400-e29b-41d4-a716-446655440000")] // no dashes
    public void NormalizeUuid_ValidInputs_ReturnsCanonicalForm(string input, string expected) =>
        Assert.Equal(expected, FingerprintNormalizer.NormalizeUuid(input));

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")] // nil UUID
    [InlineData("not-a-uuid")]
    [InlineData("550e8400-e29b-41d4-a716")] // too short
    [InlineData("")]
    public void NormalizeUuid_InvalidInputs_ReturnsNull(string input) =>
        Assert.Null(FingerprintNormalizer.NormalizeUuid(input));

    // ── SNMP engine ID ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("80001f8880c0a8010100", "80001f8880c0a8010100")]
    [InlineData("80:00:1f:88:80:c0:a8:01:01:00", "80001f8880c0a8010100")] // colons
    [InlineData("0x80001f8880c0a8010100", "80001f8880c0a8010100")] // 0x prefix
    [InlineData("80 00 1f 88 80 c0 a8 01 01 00", "80001f8880c0a8010100")] // spaces
    [InlineData("8000000000", "8000000000")] // minimum 5 bytes
    public void NormalizeSnmpEngineId_ValidInputs_ReturnsCanonicalHex(string input, string expected) =>
        Assert.Equal(expected, FingerprintNormalizer.NormalizeSnmpEngineId(input));

    [Theory]
    [InlineData("80001f88")] // too short (4 bytes)
    [InlineData("00000000000000000000")] // all zeros
    [InlineData("gggggggggggggggggggg")] // invalid hex
    [InlineData("8000")] // too short (2 bytes)
    [InlineData("")]
    public void NormalizeSnmpEngineId_InvalidInputs_ReturnsNull(string input) =>
        Assert.Null(FingerprintNormalizer.NormalizeSnmpEngineId(input));

    // ── SSH host key ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("SHA256:abc123XYZ+/", "sha256:abc123XYZ+/")] // algorithm lowercased
    [InlineData("sha256:abc123XYZ+/", "sha256:abc123XYZ+/")] // already lowercase
    // The exact shape SshBannerScanner now emits: "sha256:" + FingerPrintSHA256 (OpenSSH
    // canonical base64, no padding, standard +/ alphabet). Must survive normalization.
    [InlineData(
        "sha256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU",
        "sha256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU"
    )]
    [InlineData("SHA1:abc123XYZ+/", "sha1:abc123XYZ+/")]
    [InlineData(
        "MD5:aa:bb:cc:dd:ee:ff:00:11:22:33:44:55:66:77:88:99",
        "md5:aabbccddeeff00112233445566778899"
    )]
    public void NormalizeSshHostKey_ValidInputs_ReturnsCanonicalForm(string input, string expected) =>
        Assert.Equal(expected, FingerprintNormalizer.NormalizeSshHostKey(input));

    [Theory]
    [InlineData("BLAKE2:abc")] // unknown algorithm
    [InlineData("sha256")] // no colon
    [InlineData(":abc")] // empty algorithm
    [InlineData("MD5:aa:bb:cc")] // wrong MD5 length (3 bytes not 16)
    [InlineData("")]
    public void NormalizeSshHostKey_InvalidInputs_ReturnsNull(string input) =>
        Assert.Null(FingerprintNormalizer.NormalizeSshHostKey(input));

    // ── BGP / OSPF router-id ──────────────────────────────────────────────────

    [Theory]
    [InlineData("10.0.0.1", "10.0.0.1")]
    [InlineData("172.16.0.1", "172.16.0.1")]
    [InlineData("  10.0.0.1 ", "10.0.0.1")] // whitespace
    public void NormalizeRouterId_ValidInputs_ReturnsCanonicalForm(string input, string expected) =>
        Assert.Equal(expected, FingerprintNormalizer.NormalizeRouterId(input));

    [Theory]
    [InlineData("0.0.0.0")] // unspecified
    [InlineData("255.255.255.255")] // broadcast
    [InlineData("127.0.0.1")] // loopback
    [InlineData("127.255.255.255")] // loopback range
    [InlineData("2001:db8::1")] // IPv6 rejected
    [InlineData("not-an-ip")]
    [InlineData("")]
    public void NormalizeRouterId_InvalidInputs_ReturnsNull(string input) =>
        Assert.Null(FingerprintNormalizer.NormalizeRouterId(input));

    // ── IP prefix ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("10.0.0.0/24", "10.0.0.0/24")]
    [InlineData("10.0.0.1/24", "10.0.0.0/24")] // host bits zeroed
    [InlineData("10.0.0.255/24", "10.0.0.0/24")] // host bits zeroed
    [InlineData("192.168.0.0/16", "192.168.0.0/16")]
    [InlineData("0.0.0.0/0", "0.0.0.0/0")] // default route allowed
    [InlineData("10.1.1.1/32", "10.1.1.1/32")] // host route allowed
    [InlineData("2001:db8::/32", "2001:db8::/32")]
    [InlineData("2001:db8::1/32", "2001:db8::/32")] // host bits zeroed
    [InlineData("2001:db8:cafe::1/48", "2001:db8:cafe::/48")]
    public void NormalizeIpPrefix_ValidInputs_ReturnsCanonicalCidr(string input, string expected) =>
        Assert.Equal(expected, FingerprintNormalizer.NormalizeIpPrefix(input));

    [Theory]
    [InlineData("10.0.0.0")] // no prefix length
    [InlineData("10.0.0.0/33")] // prefix > 32 for IPv4
    [InlineData("10.0.0.0/-1")] // negative prefix
    [InlineData("not-an-ip/24")]
    [InlineData("")]
    public void NormalizeIpPrefix_InvalidInputs_ReturnsNull(string input) =>
        Assert.Null(FingerprintNormalizer.NormalizeIpPrefix(input));

    // ── Route distinguisher ───────────────────────────────────────────────────

    [Theory]
    [InlineData("65000:100", "65000:100")] // Type 0: small ASN
    [InlineData("065000:0100", "65000:100")] // leading zeros stripped
    [InlineData("131072:1", "131072:1")] // Type 2: 4-byte ASN
    [InlineData("192.168.1.1:1", "192.168.1.1:1")] // Type 1: IP form
    [InlineData("1:1", "1:1")]
    [InlineData("4294967295:1", "4294967295:1")] // max 32-bit ASN
    public void NormalizeRd_ValidInputs_ReturnsCanonicalForm(string input, string expected) =>
        Assert.Equal(expected, FingerprintNormalizer.NormalizeRouteDistinguisher(input));

    [Theory]
    [InlineData("0:0")] // placeholder — rejected
    [InlineData("65000")] // no colon
    [InlineData("-1:100")] // negative ASN
    [InlineData("65000:-1")] // negative value
    [InlineData("4294967296:1")] // ASN > 32 bits
    [InlineData("not-a-rd")]
    [InlineData("")]
    public void NormalizeRd_InvalidInputs_ReturnsNull(string input) =>
        Assert.Null(FingerprintNormalizer.NormalizeRouteDistinguisher(input));

    // Critical: bare integers must NOT be misidentified as IPv4
    [Fact]
    public void NormalizeRd_BareIntegerLeftSide_TreatedAsAsn_NotIp()
    {
        // IPAddress.TryParse("65000") succeeds in .NET (returns 0.0.253.232).
        // The normalizer must require a dot before accepting left-side as IPv4.
        Assert.Equal("65000:100", FingerprintNormalizer.NormalizeRouteDistinguisher("65000:100"));
        Assert.NotEqual("0.0.253.232:100", FingerprintNormalizer.NormalizeRouteDistinguisher("65000:100"));
    }

    // ── Vendor ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("cisco", "cisco")]
    [InlineData("Cisco", "cisco")]
    [InlineData("Palo Alto", "palo-alto")]
    [InlineData("  Arista   ", "arista")]
    [InlineData("F5", "f5")]
    [InlineData("some-new-vendor", "some-new-vendor")]
    [InlineData("A10 Networks", "a10-networks")] // no lookup needed — rules handle it
    public void NormalizeVendor_ValidInputs_ReturnsSlug(string input, string expected) =>
        Assert.Equal(expected, FingerprintNormalizer.NormalizeVendor(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")] // only non-alphanumeric chars
    public void NormalizeVendor_InvalidInputs_ReturnsNull(string input) =>
        Assert.Null(FingerprintNormalizer.NormalizeVendor(input));

    // ── Google Wifi per-unit hardware id (report field 21) ────────────────────

    [Theory]
    [InlineData("ADEC2AD42ACEF8CB5384A6D7CFDA90A3", "adec2ad42acef8cb5384a6d7cfda90a3")]
    [InlineData("  a1b2c3d4  ", "a1b2c3d4")]
    public void NormalizeGoogleWifiDeviceId_ValidInputs_TrimsAndLowercases(string input, string expected) =>
        Assert.Equal(expected, FingerprintNormalizer.NormalizeGoogleWifiDeviceId(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a1b2c3")] // too short (< 8 hex)
    [InlineData("ghijklmn")] // non-hex characters
    [InlineData("17557098-abcd")] // hyphen is not a hex digit
    public void NormalizeGoogleWifiDeviceId_InvalidInputs_ReturnsNull(string input) =>
        Assert.Null(FingerprintNormalizer.NormalizeGoogleWifiDeviceId(input));

    // ── Google Cast device id ─────────────────────────────────────────────────

    [Theory]
    [InlineData("1294150E88618BCC369E24BF70D0C24A", "1294150e88618bcc369e24bf70d0c24a")]
    [InlineData("  5bda1ab442cfad53  ", "5bda1ab442cfad53")]
    public void NormalizeCastId_ValidInputs_TrimsAndLowercases(string input, string expected) =>
        Assert.Equal(expected, FingerprintNormalizer.NormalizeCastId(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1294150e8861")] // too short (< 16 hex)
    [InlineData("ghijklmnopqrstuv")] // non-hex characters
    public void NormalizeCastId_InvalidInputs_ReturnsNull(string input) =>
        Assert.Null(FingerprintNormalizer.NormalizeCastId(input));

    // ── Dispatch ──────────────────────────────────────────────────────────────

    [Fact]
    public void Normalize_UnknownType_ReturnsNull() =>
        Assert.Null(FingerprintNormalizer.Normalize("mystery-type", "somevalue"));

    [Fact]
    public void Normalize_DispatchesToCorrectNormalizer()
    {
        Assert.Equal("001a2b3c4d5e", FingerprintNormalizer.Normalize(FingerprintType.Mac, "00:1a:2b:3c:4d:5e"));
        Assert.Equal("10.0.0.0/24", FingerprintNormalizer.Normalize(FingerprintType.IpPrefix, "10.0.0.0/24"));
        Assert.Equal("65000:100", FingerprintNormalizer.Normalize(FingerprintType.RouteDistinguisher, "65000:100"));
        Assert.Equal(
            "apple:0ba020cac2882e30",
            FingerprintNormalizer.Normalize(FingerprintType.DiskSerial, "0ba020cac2882e30", "apple")
        );
        Assert.Equal(
            "adec2ad42acef8cb5384a6d7cfda90a3",
            FingerprintNormalizer.Normalize(FingerprintType.GoogleWifiDeviceId, "  ADEC2AD42ACEF8CB5384A6D7CFDA90A3  ")
        );
        Assert.Equal(
            "1294150e88618bcc369e24bf70d0c24a",
            FingerprintNormalizer.Normalize(FingerprintType.CastId, "1294150E88618BCC369E24BF70D0C24A")
        );
    }
}