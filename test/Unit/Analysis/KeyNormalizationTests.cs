using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.UnitTests.Analysis;

/// <summary>
/// Locks ingest key normalization (D34): MAC/IP values that appear as fact-ID dimension keys are
/// canonicalized the same way the value normalizers canonicalize values, so keys and values match.
/// </summary>
public sealed class KeyNormalizationTests
{
    [Theory]
    [InlineData("Device[d1].ARP[FE80::1].MAC", "Device[d1].ARP[fe80::1].MAC")] // ARP IP key → canonical
    [InlineData("Device[d1].ARP[192.168.1.1].State", "Device[d1].ARP[192.168.1.1].State")] // already canonical
    [InlineData(
        "Device[d1].Discovered[FE80::0:1].Hostname",
        "Device[d1].Discovered[fe80::1].Hostname"
    )] // IPv6 compress
    [InlineData(
        "Device[d1].Lease[AA:BB:CC:DD:EE:FF].IP",
        "Device[d1].Lease[aabbccddeeff].IP"
    )] // DHCP lease MAC key → bare hex
    [InlineData(
        "Service[s1].DHCP.Scope[sc0].Lease[00-11-22-33-44-55].IP",
        "Service[s1].DHCP.Scope[sc0].Lease[001122334455].IP"
    )] // service DHCP lease MAC key
    [InlineData("Device[d1].Interface[eth0].MAC", "Device[d1].Interface[eth0].MAC")] // Interface key not normalized
    [InlineData("Device[d1].OS.Hostname", "Device[d1].OS.Hostname")] // no list key → unchanged
    public void Normalize_CanonicalizesDimensionKeys(string id, string expectedId) =>
        Assert.Equal(expectedId, KeyNormalization.Normalize(Fact.Create(id, "x")).Id);

    [Fact]
    public void Normalize_PreservesTheValue()
    {
        Fact result = KeyNormalization.Normalize(Fact.Create("Device[d1].ARP[FE80::1].MAC", "aabbccddeeff"));
        Assert.Equal("aabbccddeeff", result.Value.AsString());
    }
}