using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;
using JMW.Discovery.Core.Analysis.Normalizers;

namespace JMW.Discovery.Tests;

/// <summary>
/// Unit tests for production INormalizer implementations. Tests call Normalize() directly
/// so they exercise the transform logic without any DB or infrastructure dependencies.
/// </summary>
public sealed class NormalizerTests
{
    // ── LowercaseTrimNormalizer ───────────────────────────────────────────────

    [Theory]
    [InlineData("Linux", "linux")]
    [InlineData("  UBUNTU 22.04  ", "ubuntu 22.04")]
    [InlineData("ssd", "ssd")]
    [InlineData("HDD", "hdd")]
    public void LowercaseTrim_StringValue_LowercasesAndTrims(string raw, string expected)
    {
        LowercaseTrimNormalizer n = new();
        FactValue? result = n.Normalize(FactValue.FromString(raw));
        Assert.Equal(expected, result?.AsString());
    }

    [Fact]
    public void LowercaseTrim_EmptyString_ReturnsNull()
    {
        LowercaseTrimNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromString("   ")));
    }

    [Fact]
    public void LowercaseTrim_NonString_ReturnsNull()
    {
        LowercaseTrimNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromLong(42L)));
    }

    // ── SmartHealthNormalizer ─────────────────────────────────────────────────

    [Theory]
    [InlineData("PASSED", "PASSED")]
    [InlineData("passed", "PASSED")]
    [InlineData("pass", "PASSED")]
    [InlineData("ok", "PASSED")]
    [InlineData("good", "PASSED")]
    [InlineData("healthy", "PASSED")]
    public void SmartHealth_PassVariants_ReturnPassed(string raw, string expected)
    {
        SmartHealthNormalizer n = new();
        Assert.Equal(expected, n.Normalize(FactValue.FromString(raw))?.AsString());
    }

    [Theory]
    [InlineData("FAILED", "FAILED")]
    [InlineData("failed", "FAILED")]
    [InlineData("fail", "FAILED")]
    [InlineData("failing", "FAILED")]
    [InlineData("bad", "FAILED")]
    [InlineData("critical", "FAILED")]
    public void SmartHealth_FailVariants_ReturnFailed(string raw, string expected)
    {
        SmartHealthNormalizer n = new();
        Assert.Equal(expected, n.Normalize(FactValue.FromString(raw))?.AsString());
    }

    [Fact]
    public void SmartHealth_UnknownVariant_ReturnsUnknown()
    {
        SmartHealthNormalizer n = new();
        Assert.Equal("UNKNOWN", n.Normalize(FactValue.FromString("caution"))?.AsString());
    }

    [Fact]
    public void SmartHealth_EmptyString_ReturnsNull()
    {
        SmartHealthNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromString("")));
    }

    [Fact]
    public void SmartHealth_NonString_ReturnsNull()
    {
        SmartHealthNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromBool(true)));
    }

    // ── ClampPercentNormalizer ────────────────────────────────────────────────

    [Theory]
    [InlineData(75.0, 75.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(100.0, 100.0)]
    [InlineData(150.0, 100.0)]
    [InlineData(-5.0, 0.0)]
    [InlineData(200.0, 100.0)]
    public void ClampPercent_Value_Clamps(double raw, double expected)
    {
        ClampPercentNormalizer n = new();
        Assert.Equal(expected, n.Normalize(FactValue.FromDouble(raw))?.AsDouble());
    }

    [Fact]
    public void ClampPercent_NaN_ReturnsZero()
    {
        ClampPercentNormalizer n = new();
        Assert.Equal(0.0, n.Normalize(FactValue.FromDouble(double.NaN))?.AsDouble());
    }

    [Fact]
    public void ClampPercent_Infinity_ReturnsZero()
    {
        ClampPercentNormalizer n = new();
        Assert.Equal(0.0, n.Normalize(FactValue.FromDouble(double.PositiveInfinity))?.AsDouble());
    }

    [Fact]
    public void ClampPercent_NonDouble_ReturnsNull()
    {
        ClampPercentNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromString("50%")));
    }

    // ── NonNegativeBytesNormalizer ────────────────────────────────────────────

    [Fact]
    public void NonNegativeBytes_PositiveValue_PassesThrough()
    {
        NonNegativeBytesNormalizer n = new();
        FactValue? result = n.Normalize(FactValue.FromLong(1024L));
        Assert.Equal(1024L, result?.AsLong());
    }

    [Fact]
    public void NonNegativeBytes_Zero_PassesThroughByDefault()
    {
        NonNegativeBytesNormalizer n = new();
        Assert.Equal(0L, n.Normalize(FactValue.FromLong(0L))?.AsLong());
    }

    [Fact]
    public void NonNegativeBytes_Zero_RejectZeroOption_ReturnsNull()
    {
        NonNegativeBytesNormalizer n = new(rejectZero: true);
        Assert.Null(n.Normalize(FactValue.FromLong(0L)));
    }

    [Fact]
    public void NonNegativeBytes_Negative_ReturnsNull()
    {
        NonNegativeBytesNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromLong(-1L)));
    }

    [Fact]
    public void NonNegativeBytes_NonLong_ReturnsNull()
    {
        NonNegativeBytesNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromString("1024")));
    }

    // ── DiskTypeNormalizer ────────────────────────────────────────────────────

    [Theory]
    [InlineData("hdd", "hdd")]
    [InlineData("HDD", "hdd")]
    [InlineData("Hard Disk Drive", "hdd")]
    [InlineData("rotational", "hdd")]
    [InlineData("spinning", "hdd")]
    [InlineData("ssd", "ssd")]
    [InlineData("SSD", "ssd")]
    [InlineData("Solid State Drive", "ssd")]
    [InlineData("flash", "ssd")]
    [InlineData("nvme", "nvme")]
    [InlineData("NVMe", "nvme")]
    [InlineData("NVM Express", "nvme")]
    [InlineData("m.2 nvme", "nvme")]
    [InlineData("virtual", "virtual")]
    [InlineData("lvm", "virtual")]
    [InlineData("loop", "virtual")]
    public void DiskType_KnownVariants_Canonicalizes(string raw, string expected)
    {
        DiskTypeNormalizer n = new();
        Assert.Equal(expected, n.Normalize(FactValue.FromString(raw))?.AsString());
    }

    [Fact]
    public void DiskType_UnknownType_ReturnsLowercased()
    {
        DiskTypeNormalizer n = new();
        Assert.Equal("scsi", n.Normalize(FactValue.FromString("SCSI"))?.AsString());
    }

    [Fact]
    public void DiskType_EmptyString_ReturnsNull()
    {
        DiskTypeNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromString("  ")));
    }

    // ── MacValueNormalizer (the single MAC normalizer → bare 12-hex text) ─────

    [Theory]
    [InlineData("00:11:22:33:44:55", "001122334455")] // colon-separated, universal
    [InlineData("00-11-22-33-44-55", "001122334455")] // dash-separated
    [InlineData("001122334455", "001122334455")] // no separator
    [InlineData("02:00:00:00:00:01", "020000000001")] // locally administered — preserved
    [InlineData("0A:A7:F0:26:F6:77", "0aa7f026f677")] // randomized (LA) — lowercased
    [InlineData("01:00:5e:00:00:01", "01005e000001")] // multicast — preserved
    [InlineData("ff:ff:ff:ff:ff:ff", "ffffffffffff")] // broadcast
    [InlineData("0:11:22:33:44:5", "001122334405")] // macOS zero-dropped octets — padded, not dropped
    public void MacValue_ValidMAC_ReturnsBareHexText(string raw, string expected)
    {
        MacValueNormalizer n = new();
        FactValue? result = n.Normalize(FactValue.FromString(raw));
        Assert.NotNull(result);
        Assert.Equal(FactValueKind.String, result.Value.Kind);
        Assert.Equal(expected, result.Value.AsString());
    }

    [Fact]
    public void MacValue_NullMac_ReturnsNull()
    {
        // Interfaces with no Ethernet address (utun/gif/etc.) should show no MAC.
        MacValueNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromString("00:00:00:00:00:00")));
    }

    [Fact]
    public void MacValue_WrongLength_ReturnsNull()
    {
        MacValueNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromString("001122")));
    }

    [Fact]
    public void MacValue_NonHexChars_ReturnsNull()
    {
        MacValueNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromString("gg:11:22:33:44:55")));
    }

    [Fact]
    public void MacValue_NonString_ReturnsNull()
    {
        MacValueNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromLong(0x001122334455L)));
    }

    // ── IpAddressNormalizer ───────────────────────────────────────────────────

    [Theory]
    [InlineData("FE80::1", "fe80::1")] // IPv6 uppercase → lowercase
    [InlineData("fe80:0:0:0:0:0:0:1", "fe80::1")] // IPv6 → compressed
    [InlineData("192.168.1.5", "192.168.1.5")] // IPv4 already canonical
    [InlineData("192.168.1.5/24", "192.168.1.5/24")] // CIDR suffix preserved
    [InlineData("FE80::1/64", "fe80::1/64")] // IPv6 canonicalized, CIDR preserved
    [InlineData("*", "*")] // wildcard bind address — not an IP, passthrough
    [InlineData("not-an-ip", "not-an-ip")] // passthrough
    public void IpAddress_Canonicalizes(string raw, string expected)
    {
        IpAddressNormalizer n = new();
        FactValue? result = n.Normalize(FactValue.FromString(raw));
        Assert.NotNull(result);
        Assert.Equal(expected, result.Value.AsString());
    }

    // ── SerialValueNormalizer (discovered onvif/roku/snmp serial → match fp_value format) ──

    [Theory]
    [InlineData("ABC123XYZ", "bare:abc123xyz")] // uppercase → lowercased, "bare:" prefixed
    [InlineData("  abc123xyz  ", "bare:abc123xyz")] // trimmed
    public void SerialValue_ValidSerial_ReturnsBarePrefixedLowercase(string raw, string expected)
    {
        SerialValueNormalizer n = new();
        FactValue? result = n.Normalize(FactValue.FromString(raw));
        Assert.NotNull(result);
        Assert.Equal(expected, result.Value.AsString());
    }

    [Fact]
    public void SerialValue_AttributePathPatterns_IncludesSnmpSerial()
    {
        // Regression guard: Device[].Discovered[].SnmpSerial must be normalized the same way as
        // OnvifSerial/RokuSerial, or the raw scanner-cased value written to proj_discovered never
        // matches the "bare:<value>" form the fingerprint side stores, and the anti-join in
        // GetNewDiscoveredSerials.sql can never resolve it (same bug class as the MAC mismatch
        // MacValueNormalizer fixes).
        SerialValueNormalizer n = new();
        Assert.Contains(FactPaths.DiscoveredSnmpSerial, n.AttributePathPatterns);
    }

    [Fact]
    public void SerialValue_TooShort_ReturnsNull()
    {
        SerialValueNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromString("ab")));
    }

    [Fact]
    public void SerialValue_AllSameCharacter_ReturnsNull()
    {
        SerialValueNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromString("0000000000")));
    }

    [Fact]
    public void SerialValue_NonString_ReturnsNull()
    {
        SerialValueNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromLong(12345L)));
    }

    // ── UuidValueNormalizer (discovered ssdp/wsd uuid → match fp_value format) ──

    [Theory]
    [InlineData("AABBCCDD-EEFF-0011-2233-445566778899", "aabbccdd-eeff-0011-2233-445566778899")]
    [InlineData("{aabbccdd-eeff-0011-2233-445566778899}", "aabbccdd-eeff-0011-2233-445566778899")]
    [InlineData("  aabbccdd-eeff-0011-2233-445566778899  ", "aabbccdd-eeff-0011-2233-445566778899")]
    public void UuidValue_ValidUuid_ReturnsCanonicalLowercase(string raw, string expected)
    {
        UuidValueNormalizer n = new();
        FactValue? result = n.Normalize(FactValue.FromString(raw));
        Assert.NotNull(result);
        Assert.Equal(expected, result.Value.AsString());
    }

    [Fact]
    public void UuidValue_EmptyGuid_ReturnsNull()
    {
        UuidValueNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromString("00000000-0000-0000-0000-000000000000")));
    }

    [Fact]
    public void UuidValue_NotAGuid_ReturnsNull()
    {
        UuidValueNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromString("not-a-uuid")));
    }

    [Fact]
    public void UuidValue_NonString_ReturnsNull()
    {
        UuidValueNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromLong(12345L)));
    }

    // ── SpeedNormalizer (production) ──────────────────────────────────────────

    [Theory]
    [InlineData("1Gbps", 1_000_000_000L)]
    [InlineData("1G", 1_000_000_000L)]
    [InlineData("10G", 10_000_000_000L)]
    [InlineData("100G", 100_000_000_000L)]
    [InlineData("1000Mbps", 1_000_000_000L)]
    [InlineData("100Mbps", 100_000_000L)]
    [InlineData("10Mbps", 10_000_000L)]
    // "10GBase-LR" strips "Base-LR" → "10g" → matches compact 10G form → 10_000_000_000
    [InlineData("10GBase-LR", 10_000_000_000L)]
    // "1000BASE-T" strips "BASE-T" → "1000" with no unit → treated as raw bps (not Mbps)
    [InlineData("1000BASE-T", 1_000L)]
    public void SpeedNormalizer_StringForms_ParsesToBps(string raw, long expectedBps)
    {
        SpeedNormalizer n = new();
        long? result = n.Normalize(FactValue.FromString(raw))?.AsLong();
        Assert.Equal(expectedBps, result);
    }

    [Fact]
    public void SpeedNormalizer_LargeAlreadyBps_PassesThrough()
    {
        SpeedNormalizer n = new();
        // > 400_000 → treated as already bps
        Assert.Equal(1_000_000_000L, n.Normalize(FactValue.FromLong(1_000_000_000L))?.AsLong());
    }

    [Fact]
    public void SpeedNormalizer_SmallLong_TreatsAsMbps()
    {
        SpeedNormalizer n = new();
        // ≤ 400_000 → likely Mbps → multiply by 1_000_000
        Assert.Equal(100_000_000L, n.Normalize(FactValue.FromLong(100L))?.AsLong());
    }

    [Fact]
    public void SpeedNormalizer_NegativeLong_ReturnsNull()
    {
        SpeedNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromLong(-1L)));
    }

    [Fact]
    public void SpeedNormalizer_UnparsableString_ReturnsNull()
    {
        SpeedNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromString("unknown")));
    }

    // ── VendorNormalizer ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("Dell Inc.", "Dell")] // DMI legal name -> canonical
    [InlineData("Dell Inc", "Dell")] // no trailing period
    [InlineData("DELL", "Dell")] // all-caps -> proper case
    [InlineData("Apple Inc.", "Apple")]
    [InlineData("Google", "Google")] // already canonical, passthrough unchanged
    [InlineData("GOOGLE, INC.", "Google")] // IEEE-registry style all-caps + legal suffix
    [InlineData("GenuineIntel", "Intel")] // /proc/cpuinfo vendor_id
    [InlineData("AuthenticAMD", "AMD")]
    [InlineData("Advanced Micro Devices, Inc.", "AMD")]
    [InlineData("ASUSTeK COMPUTER INC.", "ASUS")]
    [InlineData("Micro-Star International Co., Ltd.", "MSI")]
    [InlineData("LENOVO", "Lenovo")]
    [InlineData("Cisco Systems, Inc.", "Cisco")]
    [InlineData("Arista Networks, Inc.", "Arista")]
    [InlineData("Ubiquiti Networks Inc.", "Ubiquiti")]
    [InlineData("  Apple Inc.  ", "Apple")] // outer whitespace trimmed before matching
    // IANA Private Enterprise Numbers registrant names (via SnmpCollector's sysObjectID
    // lookup, see EnterpriseNumberRegistry) — real raw strings from the registry, none of
    // which follow the same naming convention as the DMI/legal-name forms above.
    [InlineData("ciscoSystems", "Cisco")] // IANA's literal registrant name for enterprise 9
    [InlineData("American Power Conversion Corp.", "APC")]
    [InlineData("MikroTik", "Mikrotik")] // IANA casing differs from this codebase's canonical form
    [InlineData("TP-Link Systems Inc.", "TP-Link")] // distinct legal name from the DMI "TP-Link Technologies" form
    [InlineData("D-Link Systems, Inc.", "D-Link")]
    [InlineData("PALO ALTO NETWORKS", "Palo Alto Networks")] // IANA registrant name is all-caps
    [InlineData("Aruba, a Hewlett Packard Enterprise company", "Aruba")] // no legal-suffix pattern matches this
    // Ported from ITPIE.DeviceAnalysis's GetVendor exact-match table (see VendorNormalizer.Aliases).
    [InlineData("f5", "F5")]
    [InlineData("h3c", "H3C")]
    [InlineData("brocade", "Brocade")]
    [InlineData("juniper", "Juniper")] // bare form, distinct key from "Juniper Networks"
    [InlineData("hp", "HP")]
    [InlineData("hpe", "HPE")]
    [InlineData("palo alto", "Palo Alto Networks")] // realigned to this codebase's canonical form, not the reference project's "PaloAlto"
    [InlineData("paloalto", "Palo Alto Networks")]
    [InlineData("fortinet", "Fortinet")]
    [InlineData("ruckus", "Ruckus")]
    [InlineData("ubiquiti", "Ubiquiti")] // bare form, distinct key from "Ubiquiti Networks"
    public void Vendor_RecognizedVariants_ReturnsCanonicalName(string raw, string expected)
    {
        VendorNormalizer n = new();
        Assert.Equal(expected, n.Normalize(FactValue.FromString(raw))?.AsString());
    }

    [Theory]
    [InlineData("Netgear", "NETGEAR")] // this codebase's established canonical form wins over the reference project's "Netgear"
    [InlineData("Lenovo", "Lenovo")]
    [InlineData("MikroTik", "Mikrotik")]
    [InlineData("Super Micro Computer", "Supermicro")]
    public void Vendor_ConflictingWithReferenceProject_KeepsThisCodebasesCanonicalForm(string raw, string expected)
    {
        VendorNormalizer n = new();
        Assert.Equal(expected, n.Normalize(FactValue.FromString(raw))?.AsString());
    }

    [Theory]
    [InlineData("Acme Widgets, Inc.", "Acme Widgets")] // unrecognized vendor — suffix still stripped mechanically
    [InlineData("Some Random OEM Ltd", "Some Random OEM")]
    [InlineData("Frobnicator Systems", "Frobnicator Systems")] // no legal suffix present — nothing to strip
    public void Vendor_UnrecognizedVariant_StripsSuffixButDoesNotRename(string raw, string expected)
    {
        VendorNormalizer n = new();
        Assert.Equal(expected, n.Normalize(FactValue.FromString(raw))?.AsString());
    }

    [Fact]
    public void Vendor_UnrecognizedVariant_TrimsOuterWhitespaceAndStripsSuffix()
    {
        VendorNormalizer n = new();
        Assert.Equal("Acme Widgets", n.Normalize(FactValue.FromString("  Acme Widgets, Inc.  "))?.AsString());
    }

    [Theory]
    [InlineData("To Be Filled By O.E.M.")]
    [InlineData("System manufacturer")]
    [InlineData("Unknown")]
    [InlineData("N/A")]
    [InlineData("")]
    [InlineData("   ")]
    public void Vendor_JunkPlaceholder_ReturnsNull(string raw)
    {
        VendorNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromString(raw)));
    }

    [Fact]
    public void Vendor_NonString_ReturnsNull()
    {
        VendorNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromLong(42L)));
    }

    // ── FsTypeNormalizer ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("ntfs", "NTFS")] // Linux-style lowercase input for a Windows-idiom filesystem
    [InlineData("NTFS", "NTFS")]
    [InlineData("fat32", "FAT32")]
    [InlineData("exfat", "exFAT")]
    [InlineData("EXFAT", "exFAT")]
    [InlineData("apfs", "APFS")]
    [InlineData("ext4", "ext4")]
    [InlineData("EXT4", "ext4")] // uppercase input for a Linux-idiom filesystem
    [InlineData("btrfs", "btrfs")]
    [InlineData("nfs", "NFS")]
    public void FsType_KnownVariants_Canonicalizes(string raw, string expected)
    {
        FsTypeNormalizer n = new();
        Assert.Equal(expected, n.Normalize(FactValue.FromString(raw))?.AsString());
    }

    [Fact]
    public void FsType_UnrecognizedType_PassesThroughOriginalCasing()
    {
        FsTypeNormalizer n = new();
        Assert.Equal("ReFS", n.Normalize(FactValue.FromString("ReFS"))?.AsString());
    }

    [Fact]
    public void FsType_EmptyString_ReturnsNull()
    {
        FsTypeNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromString("   ")));
    }

    [Fact]
    public void FsType_NonString_ReturnsNull()
    {
        FsTypeNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromLong(1L)));
    }

    // ── OsDistroNormalizer ────────────────────────────────────────────────────

    [Theory]
    [InlineData("Debian GNU/Linux", "Debian")] // /etc/os-release NAME noise stripped
    [InlineData("Raspbian GNU/Linux", "Raspbian")]
    [InlineData("Kali GNU/Linux", "Kali Linux")]
    [InlineData("Ubuntu", "Ubuntu")] // already canonical, passthrough unchanged
    [InlineData("Fedora Linux", "Fedora")]
    [InlineData("CentOS Linux", "CentOS")]
    [InlineData("Red Hat Enterprise Linux", "Red Hat Enterprise Linux")]
    [InlineData("Rocky Linux", "Rocky Linux")]
    [InlineData("Arch Linux", "Arch Linux")] // not stripped to "Arch" — not a mechanical suffix rule
    [InlineData("Alpine Linux", "Alpine Linux")]
    [InlineData("SLES", "SUSE Linux Enterprise Server")]
    [InlineData("Mac OS X", "macOS")]
    [InlineData("OS X", "macOS")]
    public void OsDistro_RecognizedVariants_ReturnsCanonicalName(string raw, string expected)
    {
        OsDistroNormalizer n = new();
        Assert.Equal(expected, n.Normalize(FactValue.FromString(raw))?.AsString());
    }

    [Theory]
    [InlineData("Ubuntu 22.04")] // version embedded in NAME — not in the alias table, passthrough
    [InlineData("RouterOS")] // vendor-locked OS name — not a Linux distro NAME value, untouched
    [InlineData("Chrome OS")]
    public void OsDistro_UnrecognizedVariant_PassesThroughUnchanged(string raw)
    {
        OsDistroNormalizer n = new();
        Assert.Equal(raw, n.Normalize(FactValue.FromString(raw))?.AsString());
    }

    [Theory]
    [InlineData("Linux")] // bare family name carries no info OS.Family doesn't already have
    [InlineData("Unknown")]
    [InlineData("N/A")]
    [InlineData("")]
    [InlineData("   ")]
    public void OsDistro_JunkOrUninformative_ReturnsNull(string raw)
    {
        OsDistroNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromString(raw)));
    }

    [Fact]
    public void OsDistro_NonString_ReturnsNull()
    {
        OsDistroNormalizer n = new();
        Assert.Null(n.Normalize(FactValue.FromLong(1L)));
    }

    // ── ModelNormalizer ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("OptiPlex   7090", "OptiPlex 7090")] // internal whitespace run collapsed
    [InlineData("  ThinkPad T480  ", "ThinkPad T480")] // outer whitespace trimmed
    [InlineData("HP\tLaserJet\n4000", "HP LaserJet 4000")] // tabs/newlines collapse to a single space
    public void Model_WhitespaceNoise_Collapses(string raw, string expected)
    {
        ModelNormalizer n = new([]);
        Assert.Equal(expected, n.Normalize(FactValue.FromString(raw))?.AsString());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("N/A")]
    [InlineData("Not Specified")]
    [InlineData("")]
    [InlineData("   ")]
    public void Model_JunkPlaceholder_ReturnsNull(string raw)
    {
        ModelNormalizer n = new([]);
        Assert.Null(n.Normalize(FactValue.FromString(raw)));
    }

    [Fact]
    public void Model_NonString_ReturnsNull()
    {
        ModelNormalizer n = new([]);
        Assert.Null(n.Normalize(FactValue.FromLong(1L)));
    }
}