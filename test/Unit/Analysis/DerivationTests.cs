using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;
using JMW.Discovery.Core.Analysis.Derivations;

namespace JMW.Discovery.Tests;

/// <summary>
/// Unit tests for production IDerivation implementations. Tests call Derive() directly
/// so they exercise derivation logic without any DB or infrastructure dependencies.
/// </summary>
public sealed class DerivationTests
{
    private static readonly DateTimeOffset T = new(2026, 6, 4, 12, 0, 0, TimeSpan.Zero);
    private const string Dev = "d1";

    // ── MemoryUsedPercentDerivation ───────────────────────────────────────────

    [Fact]
    public void MemoryUsedPercent_HappyPath_ComputesPercent()
    {
        MemoryUsedPercentDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].System.MemUsedBytes", 4_000_000_000L, T),
            Fact.Create($"Device[{Dev}].System.MemTotalBytes", 8_000_000_000L, T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal(50.0, results[0].Value.AsDouble());
        Assert.Equal(FactPaths.Derived.SystemMemUsedPercent, results[0].AttributePath);
    }

    [Fact]
    public void MemoryUsedPercent_TotalIsZero_ReturnsEmpty()
    {
        MemoryUsedPercentDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].System.MemUsedBytes", 0L, T),
            Fact.Create($"Device[{Dev}].System.MemTotalBytes", 0L, T),
        ];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void MemoryUsedPercent_MissingTotal_ReturnsEmpty()
    {
        MemoryUsedPercentDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].System.MemUsedBytes", 1_000_000_000L, T),
        ];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void MemoryUsedPercent_MissingUsed_ReturnsEmpty()
    {
        MemoryUsedPercentDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].System.MemTotalBytes", 8_000_000_000L, T),
        ];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void MemoryUsedPercent_FullMemory_Returns100()
    {
        MemoryUsedPercentDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].System.MemUsedBytes", 8_000_000_000L, T),
            Fact.Create($"Device[{Dev}].System.MemTotalBytes", 8_000_000_000L, T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);
        Assert.Equal(100.0, results[0].Value.AsDouble());
    }

    // ── UsedPercentDerivation (filesystem) ────────────────────────────────────

    [Fact]
    public void FsUsedPercent_HappyPath_ComputesPercent()
    {
        UsedPercentDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Filesystem[/].UsedBytes", 30_000_000_000L, T),
            Fact.Create($"Device[{Dev}].Filesystem[/].TotalBytes", 100_000_000_000L, T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal(30.0, results[0].Value.AsDouble());
        Assert.Equal(FactPaths.Derived.FsUsedPercent, results[0].AttributePath);
    }

    [Fact]
    public void FsUsedPercent_TotalIsZero_ReturnsEmpty()
    {
        UsedPercentDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Filesystem[/].UsedBytes", 0L, T),
            Fact.Create($"Device[{Dev}].Filesystem[/].TotalBytes", 0L, T),
        ];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void FsUsedPercent_MissingOneFact_ReturnsEmpty()
    {
        UsedPercentDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Filesystem[/].TotalBytes", 100_000_000_000L, T),
        ];

        Assert.Empty(d.Derive(facts));
    }

    // ── TotalBytesDerivation (interface) ─────────────────────────────────────

    [Fact]
    public void InterfaceTotalBytes_HappyPath_SumsRxAndTx()
    {
        TotalBytesDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Interface[eth0].RxBytes", 1_000L, T),
            Fact.Create($"Device[{Dev}].Interface[eth0].TxBytes", 2_000L, T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal(3_000L, results[0].Value.AsLong());
        Assert.Equal(FactPaths.Derived.InterfaceTotalBytes, results[0].AttributePath);
    }

    [Fact]
    public void InterfaceTotalBytes_MissingTx_ReturnsEmpty()
    {
        TotalBytesDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Interface[eth0].RxBytes", 1_000L, T),
        ];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void InterfaceTotalBytes_BothZero_ProducesZero()
    {
        TotalBytesDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Interface[eth0].RxBytes", 0L, T),
            Fact.Create($"Device[{Dev}].Interface[eth0].TxBytes", 0L, T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);
        Assert.Equal(0L, results[0].Value.AsLong());
    }

    // ── BatteryHealthDerivation ───────────────────────────────────────────────

    [Fact]
    public void BatteryHealth_HappyPath_ComputesPercent()
    {
        BatteryHealthDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Battery.CurrentCapacityWh", 45.0, T),
            Fact.Create($"Device[{Dev}].Battery.DesignCapacityWh", 50.0, T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal(90.0, results[0].Value.AsDouble());
        Assert.Equal(FactPaths.Derived.BatteryHealthPercent, results[0].AttributePath);
    }

    [Fact]
    public void BatteryHealth_DesignCapIsZero_ReturnsEmpty()
    {
        BatteryHealthDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Battery.CurrentCapacityWh", 10.0, T),
            Fact.Create($"Device[{Dev}].Battery.DesignCapacityWh", 0.0, T),
        ];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void BatteryHealth_MissingCurrent_ReturnsEmpty()
    {
        BatteryHealthDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Battery.DesignCapacityWh", 50.0, T),
        ];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void BatteryHealth_MissingDesign_ReturnsEmpty()
    {
        BatteryHealthDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Battery.CurrentCapacityWh", 45.0, T),
        ];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void BatteryHealth_NewBattery_Returns100()
    {
        BatteryHealthDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Battery.CurrentCapacityWh", 50.0, T),
            Fact.Create($"Device[{Dev}].Battery.DesignCapacityWh", 50.0, T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);
        Assert.Equal(100.0, results[0].Value.AsDouble());
    }

    // ── DeviceVendorDerivation ─────────────────────────────────────────────────

    [Fact]
    public void DeviceVendor_HardwareOnly_FansInToCanonical()
    {
        DeviceVendorDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Hardware.SystemVendor", "Dell", T)];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal("Dell", results[0].Value.AsString());
        Assert.Equal($"Device[{Dev}].VendorCanonical", results[0].Id);
        Assert.Equal(FactPaths.Derived.DeviceVendorCanonical, results[0].AttributePath);
    }

    [Fact]
    public void DeviceVendor_BacnetOnly_FansInToCanonical()
    {
        DeviceVendorDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].BACnet.VendorName", "Honeywell", T)];

        IReadOnlyList<Fact> results = d.Derive(facts);
        Assert.Equal("Honeywell", results[0].Value.AsString());
    }

    [Fact]
    public void DeviceVendor_ModbusOnly_FansInToCanonical()
    {
        DeviceVendorDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Modbus.VendorName", "Schneider Electric", T)];

        IReadOnlyList<Fact> results = d.Derive(facts);
        Assert.Equal("Schneider Electric", results[0].Value.AsString());
    }

    [Fact]
    public void DeviceVendor_GoogleWifiOnly_FansInToCanonical()
    {
        DeviceVendorDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Vendor", "Google", T)];

        IReadOnlyList<Fact> results = d.Derive(facts);
        Assert.Equal("Google", results[0].Value.AsString());
    }

    [Fact]
    public void DeviceVendor_MultipleSourcesPresent_PriorityOrderWins()
    {
        // In practice a device is only ever monitored by ONE protocol — this just locks the
        // declared tie-break order (DeviceVendor > HwSystemVendor > Bacnet > Modbus) in case
        // that assumption ever breaks.
        DeviceVendorDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Modbus.VendorName", "Schneider Electric", T),
            Fact.Create($"Device[{Dev}].BACnet.VendorName", "Honeywell", T),
            Fact.Create($"Device[{Dev}].Hardware.SystemVendor", "Dell", T),
            Fact.Create($"Device[{Dev}].Vendor", "Google", T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);
        Assert.Single(results);
        Assert.Equal("Google", results[0].Value.AsString());
    }

    [Fact]
    public void DeviceVendor_NoSourcesPresent_ReturnsEmpty()
    {
        DeviceVendorDerivation d = new();
        Assert.Empty(d.Derive([]));
    }

    [Fact]
    public void DeviceVendor_GuessOnly_FansInToCanonical()
    {
        // Phase 6a (architecture-identity-facts.md §12): the inferred guess is the lowest-priority
        // fan-in input, not a separate "guess" column — it fires only when no protocol self-reports
        // a vendor.
        DeviceVendorDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].VendorGuess", "Mikrotik", T)];

        IReadOnlyList<Fact> results = d.Derive(facts);
        Assert.Single(results);
        Assert.Equal("Mikrotik", results[0].Value.AsString());
        Assert.Equal(FactPaths.Derived.DeviceVendorCanonical, results[0].AttributePath);
    }

    [Fact]
    public void DeviceVendor_GuessAndRealSourcePresent_RealSourceWins()
    {
        DeviceVendorDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].VendorGuess", "Mikrotik", T),
            Fact.Create($"Device[{Dev}].Hardware.SystemVendor", "Dell", T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);
        Assert.Single(results);
        Assert.Equal("Dell", results[0].Value.AsString());
    }

    [Fact]
    public void DeviceVendor_WhitespaceOnlyValue_SkipsToNextSource()
    {
        DeviceVendorDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Vendor", "   ", T),
            Fact.Create($"Device[{Dev}].Hardware.SystemVendor", "Dell", T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);
        Assert.Single(results);
        Assert.Equal("Dell", results[0].Value.AsString());
    }

    // ── VendorFromOsDistroDerivation ───────────────────────────────────────────

    [Theory]
    [InlineData("RouterOS", "Mikrotik")]
    [InlineData("JunOS", "Juniper")]
    [InlineData("IOS-XE", "Cisco")]
    [InlineData("NX-OS", "Cisco")]
    [InlineData("EdgeOS", "Ubiquiti")]
    [InlineData("UniFi OS", "Ubiquiti")]
    [InlineData("PAN-OS", "Palo Alto Networks")]
    [InlineData("FortiOS", "Fortinet")]
    [InlineData("ArubaOS", "Aruba")]
    [InlineData("DSM", "Synology")]
    [InlineData("QTS", "QNAP")]
    public void VendorFromOsDistro_KnownDistro_ProducesGuess(string distro, string vendor)
    {
        VendorFromOsDistroDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].OS.Distro", distro, T)];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal(vendor, results[0].Value.AsString());
        Assert.Equal($"Device[{Dev}].VendorGuess", results[0].Id);
        Assert.Equal(FactPaths.Derived.DeviceVendorGuess, results[0].AttributePath);
    }

    [Fact]
    public void VendorFromOsDistro_KnownFamily_ProducesGuess()
    {
        VendorFromOsDistroDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].OS.Family", "RouterOS", T)];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal("Mikrotik", results[0].Value.AsString());
    }

    [Fact]
    public void VendorFromOsDistro_CaseInsensitive_StillMatches()
    {
        VendorFromOsDistroDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].OS.Distro", "routeros", T)];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal("Mikrotik", results[0].Value.AsString());
    }

    [Fact]
    public void VendorFromOsDistro_UnknownDistro_ReturnsEmpty()
    {
        VendorFromOsDistroDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].OS.Distro", "Ubuntu", T)];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void VendorFromOsDistro_NoInputs_ReturnsEmpty()
    {
        VendorFromOsDistroDerivation d = new();
        Assert.Empty(d.Derive([]));
    }

    [Fact]
    public void VendorFromOsDistro_WhitespaceOnlyValue_ReturnsEmpty()
    {
        VendorFromOsDistroDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].OS.Distro", "   ", T)];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void VendorFromOsDistro_UnrelatedFactsOnly_ReturnsEmpty()
    {
        VendorFromOsDistroDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Hardware.SystemVendor", "Dell", T)];

        Assert.Empty(d.Derive(facts));
    }

    // ── VendorOsFromDeviceBannerDerivation ──────────────────────────────────────
    // Supersedes the former separate VendorFromSnmpSysDescrDerivation/OsFromSnmpSysDescrDerivation.

    [Theory]
    [InlineData("Cisco IOS Software, C2960 Software", "Cisco", "Cisco IOS")]
    [InlineData("Cisco IOS-XE Software, ASR1000 Software", "Cisco", "Cisco IOS-XE")]
    [InlineData("Cisco NX-OS(tm) n9000", "Cisco", "Cisco NX-OS")]
    [InlineData("Juniper Networks, Inc. srx340, kernel JUNOS 20.4R3-S1.6", "Juniper", "JunOS")]
    [InlineData("UniFi OS 3.2.9", "Ubiquiti", "UniFi OS")]
    [InlineData("Palo Alto Networks PA-220 series firewall, PAN-OS 10.1.6", "Palo Alto Networks", "PAN-OS")]
    [InlineData("ArubaOS 8.10.0.4", "Aruba", "ArubaOS")]
    public void VendorOsFromDeviceBanner_KnownSignature_ProducesBothGuesses(string banner, string vendor, string os)
    {
        VendorOsFromDeviceBannerDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].SNMP.SysDescr", banner, T)];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Equal(2, results.Count);
        Fact vendorFact = Assert.Single(results, r => r.AttributePath == FactPaths.Derived.DeviceVendorGuess);
        Fact osFact = Assert.Single(results, r => r.AttributePath == FactPaths.Derived.DeviceOsGuess);
        Assert.Equal(vendor, vendorFact.Value.AsString());
        Assert.Equal(os, osFact.Value.AsString());
        Assert.Equal($"Device[{Dev}].VendorGuess", vendorFact.Id);
        Assert.Equal($"Device[{Dev}].OsGuess", osFact.Id);
    }

    // Legacy fallback signatures kept from the derivations this replaces (not present in the
    // ported source cascade) — must keep working.
    [Theory]
    [InlineData("RouterOS 6.49.6", "Mikrotik", "RouterOS")]
    [InlineData("EdgeOS 2.0.9-hotfix.4", "Ubiquiti", "EdgeOS")]
    [InlineData("FortiOS 7.2.5", "Fortinet", "FortiOS")]
    public void VendorOsFromDeviceBanner_LegacyFallbackSignature_StillProducesBothGuesses(
        string banner,
        string vendor,
        string os
    )
    {
        VendorOsFromDeviceBannerDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].SNMP.SysDescr", banner, T)];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Equal(2, results.Count);
        Assert.Equal(vendor, Assert.Single(results, r => r.AttributePath == FactPaths.Derived.DeviceVendorGuess).Value.AsString());
        Assert.Equal(os, Assert.Single(results, r => r.AttributePath == FactPaths.Derived.DeviceOsGuess).Value.AsString());
    }

    [Fact]
    public void VendorOsFromDeviceBanner_HpProCurve_ProducesVendorOnly()
    {
        VendorOsFromDeviceBannerDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].SNMP.SysDescr", "HP ProCurve Switch 2530-24G", T)];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal(FactPaths.Derived.DeviceVendorGuess, results[0].AttributePath);
        Assert.Equal("HP", results[0].Value.AsString());
    }

    [Fact]
    public void VendorOsFromDeviceBanner_BareLinux_ProducesOsOnlyNoVendor()
    {
        VendorOsFromDeviceBannerDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].SNMP.SysDescr", "Linux server 5.15.0 x86_64", T)];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal(FactPaths.Derived.DeviceOsGuess, results[0].AttributePath);
        Assert.Equal("Linux", results[0].Value.AsString());
    }

    [Fact]
    public void VendorOsFromDeviceBanner_NxOsBeforeIos_MostSpecificWins()
    {
        VendorOsFromDeviceBannerDerivation d = new();
        // "cisco nx-os" — a plain "cisco ios" substring scan would misfire if checked first.
        Fact[] facts = [Fact.Create($"Device[{Dev}].SNMP.SysDescr", "Cisco NX-OS(tm) n9000", T)];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Equal("Cisco NX-OS", Assert.Single(results, r => r.AttributePath == FactPaths.Derived.DeviceOsGuess).Value.AsString());
    }

    [Fact]
    public void VendorOsFromDeviceBanner_CaseInsensitive_StillMatches()
    {
        VendorOsFromDeviceBannerDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].SNMP.SysDescr", "ROUTEROS 6.49.6", T)];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Equal("Mikrotik", Assert.Single(results, r => r.AttributePath == FactPaths.Derived.DeviceVendorGuess).Value.AsString());
    }

    [Fact]
    public void VendorOsFromDeviceBanner_ReadsAcrossMultipleBannerFields()
    {
        VendorOsFromDeviceBannerDerivation d = new();
        // Vendor/OS present only once the SSH banner and SysDescr are considered together.
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].SNMP.SysDescr", "generic embedded device", T),
            Fact.Create($"Device[{Dev}].Discovered[d1].SshBanner", "SSH-2.0 EdgeOS", T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Equal("Ubiquiti", Assert.Single(results, r => r.AttributePath == FactPaths.Derived.DeviceVendorGuess).Value.AsString());
    }

    [Fact]
    public void VendorOsFromDeviceBanner_PoweredgeCorrectedToDell_NotSourcesHp()
    {
        VendorOsFromDeviceBannerDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].SNMP.SysDescr", "Dell PowerEdge R740 BMC", T)];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Equal("Dell", Assert.Single(results, r => r.AttributePath == FactPaths.Derived.DeviceVendorGuess).Value.AsString());
    }

    [Fact]
    public void VendorOsFromDeviceBanner_UnknownBanner_ReturnsEmpty()
    {
        VendorOsFromDeviceBannerDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].SNMP.SysDescr", "Widget 3000 embedded controller", T)];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void VendorOsFromDeviceBanner_NoInputs_ReturnsEmpty()
    {
        VendorOsFromDeviceBannerDerivation d = new();
        Assert.Empty(d.Derive([]));
    }

    [Fact]
    public void VendorOsFromDeviceBanner_WhitespaceOnlyValue_ReturnsEmpty()
    {
        VendorOsFromDeviceBannerDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].SNMP.SysDescr", "   ", T)];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void VendorOsFromDeviceBanner_UnrelatedFactsOnly_ReturnsEmpty()
    {
        VendorOsFromDeviceBannerDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].SNMP.SysName", "switch1", T)];

        Assert.Empty(d.Derive(facts));
    }

    // ── VendorFromModelDerivation ───────────────────────────────────────────────

    [Theory]
    [InlineData("ThinkPad X1 Carbon Gen 10", "Lenovo")]
    [InlineData("ThinkCentre M75q", "Lenovo")]
    [InlineData("ThinkStation P360", "Lenovo")]
    [InlineData("OptiPlex 7090", "Dell")]
    [InlineData("Latitude 5420", "Dell")]
    [InlineData("PowerEdge R740", "Dell")]
    [InlineData("EliteBook 840 G8", "HP")]
    [InlineData("Pavilion 15", "HP")]
    [InlineData("LaserJet Pro M404n", "HP")]
    [InlineData("Galaxy Tab S8", "Samsung")]
    [InlineData("iPhone 14 Pro", "Apple")]
    [InlineData("iPad Air", "Apple")]
    [InlineData("MacBookPro18,3", "Apple")]
    [InlineData("iMac21,2", "Apple")]
    [InlineData("Mac15,10", "Apple")] // bare Apple model identifier (Mac Studio / notebook)
    [InlineData("Mac15,3", "Apple")]
    [InlineData("Surface Pro 9", "Microsoft")]
    [InlineData("ROG Strix G15", "ASUS")]
    [InlineData("ZenBook 14X", "ASUS")]
    public void VendorFromModel_HwSystemModel_Known_ProducesGuess(string model, string vendor)
    {
        VendorFromModelDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Hardware.SystemModel", model, T)];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal(vendor, results[0].Value.AsString());
        Assert.Equal($"Device[{Dev}].VendorGuess", results[0].Id);
        Assert.Equal(FactPaths.Derived.DeviceVendorGuess, results[0].AttributePath);
    }

    [Fact]
    public void VendorFromModel_Contains_MidStringToken_Matches()
    {
        // Contains (not StartsWith): a vendor-prefixed SMBIOS string still resolves the model
        // line sitting mid-string. "OptiPlex" is not at the start here.
        VendorFromModelDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Hardware.SystemModel", "Dell Inc. OptiPlex 7090", T)];

        IReadOnlyList<Fact> results = d.Derive(facts);
        Assert.Single(results);
        Assert.Equal("Dell", results[0].Value.AsString());
    }

    [Fact]
    public void VendorFromModel_DiscoveredModel_NotHandledHere()
    {
        // A discovered neighbor's model is VendorFromDiscoveredModelDerivation's job (it scopes the
        // vendor to the station); this derivation only reads the device's own SMBIOS model.
        VendorFromModelDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Discovered[192.168.1.50].Model", "Galaxy S23", T)];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void VendorFromModel_CaseInsensitive_StillMatches()
    {
        VendorFromModelDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Hardware.SystemModel", "thinkpad t14", T)];

        IReadOnlyList<Fact> results = d.Derive(facts);
        Assert.Single(results);
        Assert.Equal("Lenovo", results[0].Value.AsString());
    }

    [Fact]
    public void VendorFromModel_UnknownModel_ReturnsEmpty()
    {
        VendorFromModelDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Hardware.SystemModel", "Generic PC", T)];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void VendorFromModel_NoInputs_ReturnsEmpty()
    {
        VendorFromModelDerivation d = new();
        Assert.Empty(d.Derive([]));
    }

    [Fact]
    public void VendorFromModel_WhitespaceOnlyValue_ReturnsEmpty()
    {
        VendorFromModelDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Hardware.SystemModel", "   ", T)];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void VendorFromModel_UnrelatedFactsOnly_ReturnsEmpty()
    {
        VendorFromModelDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Hardware.SystemVendor", "Dell", T)];

        Assert.Empty(d.Derive(facts));
    }

    // ── VendorFromDiscoveredModelDerivation ─────────────────────────────────────

    [Theory]
    [InlineData("Nest Audio", "Google")]
    [InlineData("Google Nest Mini", "Google")] // mid-string token — Contains, not StartsWith
    [InlineData("Pixel Tablet", "Google")]
    [InlineData("Mac15,10", "Apple")] // bare Apple model identifier
    [InlineData("MacBookPro14,2", "Apple")]
    [InlineData("Galaxy S23", "Samsung")]
    public void VendorFromDiscoveredModel_KnownModel_ProducesDiscoveredVendor(string model, string vendor)
    {
        VendorFromDiscoveredModelDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Discovered[192.168.1.50].Model", model, T)];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal(vendor, results[0].Value.AsString());
        Assert.Equal($"Device[{Dev}].Discovered[192.168.1.50].Vendor", results[0].Id);
        Assert.Equal(FactPaths.DiscoveredVendor, results[0].AttributePath);
    }

    [Fact]
    public void VendorFromDiscoveredModel_ObservedVendorPresent_ReturnsEmpty()
    {
        // An observed UPnP manufacturer always wins over the model-derived vendor.
        VendorFromDiscoveredModelDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Discovered[192.168.1.50].Model", "Mac15,10", T),
            Fact.Create($"Device[{Dev}].Discovered[192.168.1.50].Vendor", "Apple Inc.", T),
        ];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void VendorFromDiscoveredModel_UnknownModel_ReturnsEmpty()
    {
        VendorFromDiscoveredModelDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Discovered[192.168.1.50].Model", "Generic Widget", T)];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void VendorFromDiscoveredModel_NoInputs_ReturnsEmpty()
    {
        VendorFromDiscoveredModelDerivation d = new();
        Assert.Empty(d.Derive([]));
    }

    // ── VendorFromHostnamePrefixDerivation ──────────────────────────────────────

    [Theory]
    [InlineData("amazon-a1b2c3", "Amazon")]
    [InlineData("roku-3810x", "Roku")]
    [InlineData("sonos-b8e9377f4c21", "Sonos")]
    public void VendorFromHostnamePrefix_SystemHostname_KnownPrefix_ProducesGuess(string hostname, string vendor)
    {
        VendorFromHostnamePrefixDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].OS.Hostname", hostname, T)];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal(vendor, results[0].Value.AsString());
        Assert.Equal($"Device[{Dev}].VendorGuess", results[0].Id);
        Assert.Equal(FactPaths.Derived.DeviceVendorGuess, results[0].AttributePath);
    }

    [Fact]
    public void VendorFromHostnamePrefix_DiscoveredHostname_KnownPrefix_ProducesGuess()
    {
        VendorFromHostnamePrefixDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Discovered[192.168.1.60].Hostname", "sonos-abc123", T)];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal("Sonos", results[0].Value.AsString());
    }

    [Fact]
    public void VendorFromHostnamePrefix_MustIncludeDelimiter_NotJustLeadingSubstring()
    {
        VendorFromHostnamePrefixDerivation d = new();
        // "amazonian-nas" starts with "amazon" but not "amazon-" — must not match.
        Fact[] facts = [Fact.Create($"Device[{Dev}].OS.Hostname", "amazonian-nas", T)];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void VendorFromHostnamePrefix_AndroidPrefix_NotMappedToAnyVendor()
    {
        // Android's OS-level DHCP hostname default, not vendor-exclusive — deliberately excluded.
        VendorFromHostnamePrefixDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].OS.Hostname", "android-a1b2c3d4e5f6", T)];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void VendorFromHostnamePrefix_CaseInsensitive_StillMatches()
    {
        VendorFromHostnamePrefixDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].OS.Hostname", "ROKU-3810X", T)];

        IReadOnlyList<Fact> results = d.Derive(facts);
        Assert.Single(results);
        Assert.Equal("Roku", results[0].Value.AsString());
    }

    [Fact]
    public void VendorFromHostnamePrefix_UnknownHostname_ReturnsEmpty()
    {
        VendorFromHostnamePrefixDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].OS.Hostname", "my-laptop", T)];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void VendorFromHostnamePrefix_NoInputs_ReturnsEmpty()
    {
        VendorFromHostnamePrefixDerivation d = new();
        Assert.Empty(d.Derive([]));
    }

    [Fact]
    public void VendorFromHostnamePrefix_WhitespaceOnlyValue_ReturnsEmpty()
    {
        VendorFromHostnamePrefixDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].OS.Hostname", "   ", T)];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void VendorFromHostnamePrefix_UnrelatedFactsOnly_ReturnsEmpty()
    {
        VendorFromHostnamePrefixDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Hardware.SystemVendor", "Dell", T)];

        Assert.Empty(d.Derive(facts));
    }

    // ── SystemOsDistroDerivation ────────────────────────────────────────────────

    [Fact]
    public void SystemOsDistro_RealValueOnly_FansInToCanonical()
    {
        SystemOsDistroDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].OS.Distro", "Ubuntu", T)];

        IReadOnlyList<Fact> results = d.Derive(facts);
        Assert.Single(results);
        Assert.Equal("Ubuntu", results[0].Value.AsString());
        Assert.Equal(FactPaths.Derived.SystemOsDistroCanonical, results[0].AttributePath);
    }

    [Fact]
    public void SystemOsDistro_GuessOnly_FansInToCanonical()
    {
        // Phase 6b (architecture-identity-facts.md §12): the inferred guess is the lowest-priority
        // fan-in input, not a separate "guess" column — it fires only when no device-reported
        // distro is present.
        SystemOsDistroDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].OsGuess", "Cisco IOS-XE", T)];

        IReadOnlyList<Fact> results = d.Derive(facts);
        Assert.Single(results);
        Assert.Equal("Cisco IOS-XE", results[0].Value.AsString());
    }

    [Fact]
    public void SystemOsDistro_RealValueAndGuessPresent_RealValueWins()
    {
        SystemOsDistroDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].OsGuess", "Cisco IOS-XE", T),
            Fact.Create($"Device[{Dev}].OS.Distro", "Ubuntu", T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);
        Assert.Single(results);
        Assert.Equal("Ubuntu", results[0].Value.AsString());
    }

    [Fact]
    public void SystemOsDistro_NoSourcesPresent_ReturnsEmpty()
    {
        SystemOsDistroDerivation d = new();
        Assert.Empty(d.Derive([]));
    }

    [Fact]
    public void SystemOsDistro_WhitespaceOnlyValue_SkipsToNextSource()
    {
        SystemOsDistroDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].OS.Distro", "   ", T),
            Fact.Create($"Device[{Dev}].OsGuess", "Cisco IOS-XE", T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);
        Assert.Single(results);
        Assert.Equal("Cisco IOS-XE", results[0].Value.AsString());
    }

    // ── DeviceKindDerivation ───────────────────────────────────────────────────

    [Theory]
    [InlineData("Notebook", "laptop")]
    [InlineData("Portable", "laptop")]
    [InlineData("Sub Notebook", "laptop")]
    [InlineData("Desktop", "desktop")]
    [InlineData("Mini Tower", "desktop")]
    [InlineData("Main Server Chassis", "server")]
    [InlineData("Rack Mount Chassis", "server")]
    [InlineData("Tablet", "tablet")]
    public void DeviceKind_HostWithKnownChassisType_RefinesKind(string chassisType, string expectedKind)
    {
        DeviceKindDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Kind", "host", T),
            Fact.Create($"Device[{Dev}].Hardware.ChassisType", chassisType, T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal(expectedKind, results[0].Value.AsString());
        Assert.Equal($"Device[{Dev}].Kind", results[0].Id);
        Assert.Equal(FactPaths.DeviceKind, results[0].AttributePath);
    }

    [Fact]
    public void DeviceKind_HostWithUnknownChassisType_ReturnsEmpty()
    {
        DeviceKindDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Kind", "host", T),
            Fact.Create($"Device[{Dev}].Hardware.ChassisType", "Hand Held", T),
        ];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void DeviceKind_HostWithNoChassisType_ReturnsEmpty()
    {
        DeviceKindDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Kind", "host", T)];

        Assert.Empty(d.Derive(facts));
    }

    [Theory]
    [InlineData("Synology", "nas")]
    [InlineData("QNAP", "nas")]
    [InlineData("APC", "ups")]
    [InlineData("Mikrotik", "router")]
    [InlineData("Brother", "printer")]
    public void DeviceKind_NetworkDeviceWithKnownVendor_RefinesKind(string vendor, string expectedKind)
    {
        DeviceKindDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Kind", "network-device", T),
            Fact.Create($"Device[{Dev}].VendorCanonical", vendor, T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal(expectedKind, results[0].Value.AsString());
    }

    [Fact]
    public void DeviceKind_NetworkDeviceFallsBackToVendorGuess_WhenNoCanonicalVendor()
    {
        DeviceKindDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Kind", "network-device", T),
            Fact.Create($"Device[{Dev}].VendorGuess", "Synology", T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal("nas", results[0].Value.AsString());
    }

    [Theory]
    [InlineData("HP LaserJet Pro M404", null, "printer")]
    [InlineData(null, "HP OfficeJet 9015e", "printer")]
    [InlineData("UniFi AP-AC-Pro", null, "access-point")]
    [InlineData(null, "UniFi Switch 24 PoE", "switch")]
    [InlineData("EdgeRouter X", null, "router")]
    public void DeviceKind_NetworkDeviceWithKnownProductSignature_RefinesKind(
        string? model,
        string? sysDescr,
        string expectedKind
    )
    {
        DeviceKindDerivation d = new();
        List<Fact> facts = [Fact.Create($"Device[{Dev}].Kind", "network-device", T)];
        if (model is not null)
        {
            facts.Add(Fact.Create($"Device[{Dev}].Hardware.SystemModel", model, T));
        }

        if (sysDescr is not null)
        {
            facts.Add(Fact.Create($"Device[{Dev}].SNMP.SysDescr", sysDescr, T));
        }

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal(expectedKind, results[0].Value.AsString());
    }

    [Fact]
    public void DeviceKind_NetworkDeviceWithNoSignal_ReturnsEmpty()
    {
        DeviceKindDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Kind", "network-device", T)];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void DeviceKind_AlreadySpecificKind_IsNotOverridden()
    {
        DeviceKindDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Kind", "router", T),
            Fact.Create($"Device[{Dev}].VendorCanonical", "Synology", T),
        ];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void DeviceKind_NoInputs_ReturnsEmpty()
    {
        DeviceKindDerivation d = new();
        Assert.Empty(d.Derive([]));
    }

    [Theory]
    [InlineData("Aruba", "AOS-CX", "switch")]
    [InlineData("HP", "ProVision", "switch")]
    [InlineData("Cisco", "Cisco ISE", "server-appliance")]
    [InlineData("Cisco", "Cisco IOS-XR", "router")]
    [InlineData("Cisco", "Cisco UCOS", "uc-session-controller")]
    [InlineData("Forcepoint", "SecureOS", "firewall")]
    [InlineData("Gigamon", "GigaVUE", "tap")]
    [InlineData("Nortel", "NNCLI", "switch")]
    [InlineData("Infoblox", "NIOS", "application")]
    [InlineData("NetApp", "ONTAP", "server-appliance")]
    [InlineData("AudioCodes", "PSOS", "sbc")]
    [InlineData("VMware", "ESXi", "vm-hypervisor")]
    public void DeviceKind_NetworkDeviceWithKnownVendorOs_RefinesKind(string vendor, string os, string expectedKind)
    {
        DeviceKindDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Kind", "network-device", T),
            Fact.Create($"Device[{Dev}].VendorCanonical", vendor, T),
            Fact.Create($"Device[{Dev}].OsGuess", os, T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal(expectedKind, results[0].Value.AsString());
    }

    [Theory]
    [InlineData("Cisco", "Firmware-UC", "Cisco IP Phone 7841", "phone")]
    [InlineData("Cisco", "Firmware-UC", "Telepresence MX700", "vtc")]
    [InlineData("Polycom", "Firmware-UC", "Poly Trio 8500", "vtc")]
    public void DeviceKind_FirmwareUc_DistinguishesPhoneFromVtcByModel(
        string vendor,
        string os,
        string model,
        string expectedKind
    )
    {
        DeviceKindDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Kind", "network-device", T),
            Fact.Create($"Device[{Dev}].VendorCanonical", vendor, T),
            Fact.Create($"Device[{Dev}].OsGuess", os, T),
            Fact.Create($"Device[{Dev}].ModelCanonical", model, T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal(expectedKind, results[0].Value.AsString());
    }

    [Theory]
    [InlineData("Catalyst 9300", "switch")]
    [InlineData("Nexus 9300", "switch")]
    [InlineData("Aironet 2800", "access-point")]
    [InlineData("ASA 5500-X", "firewall")]
    [InlineData("Firepower 4100", "firewall")]
    [InlineData("ISR 4000", "router")]
    [InlineData("Meraki Switch", "switch")]
    [InlineData("Meraki Wireless", "access-point")]
    [InlineData("Meraki Security", "firewall")]
    [InlineData("QFX5100", "switch")]
    [InlineData("vSRX", "firewall")]
    [InlineData("BIG-IP VE", "load-balancer")]
    [InlineData("Smart-UPS", "ups")]
    public void DeviceKind_NetworkDeviceWithKnownModelFamily_RefinesKindViaCanonicalModel(
        string canonicalModel,
        string expectedKind
    )
    {
        DeviceKindDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Kind", "network-device", T),
            Fact.Create($"Device[{Dev}].ModelCanonical", canonicalModel, T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal(expectedKind, results[0].Value.AsString());
    }

    [Fact]
    public void DeviceKind_ModelCanonicalPreferredOverRawModel()
    {
        DeviceKindDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Kind", "network-device", T),
            // Raw SKU text alone wouldn't match any ProductSignature; the canonical family name would.
            Fact.Create($"Device[{Dev}].Hardware.SystemModel", "WS-C9300-48P", T),
            Fact.Create($"Device[{Dev}].ModelCanonical", "Catalyst 9300", T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal("switch", results[0].Value.AsString());
    }

    [Theory]
    [InlineData("HanwhaVision", "camera")]
    [InlineData("AxisCommunications", "camera")]
    [InlineData("Illustra", "camera")]
    [InlineData("Pelco", "camera")]
    [InlineData("Zenitel", "intercom")]
    [InlineData("Shure", "microphone")]
    public void DeviceKind_NetworkDeviceWithSinglePurposeVendor_RefinesKind(string vendor, string expectedKind)
    {
        DeviceKindDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Kind", "network-device", T),
            Fact.Create($"Device[{Dev}].VendorCanonical", vendor, T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal(expectedKind, results[0].Value.AsString());
    }

    // ── DeviceModelDerivation ────────────────────────────────────────────────────

    [Theory]
    [InlineData("Cisco", "Cisco IOS-XE", "WS-C9300-48P", "Catalyst 9300")]
    [InlineData("Cisco", "Cisco NX-OS", "N9K-C93180YC-EX", "Nexus 9300")]
    [InlineData("Juniper", "JunOS", "QFX5100-48S", "QFX5100")]
    [InlineData("Aruba", "ArubaOS", "MM-VA-50", "Aruba MM-VA")]
    [InlineData("F5", "TMOS", "BIG-IP Virtual Edition", "BIG-IP VE")]
    [InlineData("HP", "Comware", "A5800-48G", "A5800")]
    [InlineData("Palo Alto Networks", "PAN-OS", "PA-220", "PA 200")]
    public void DeviceModel_KnownVendorOsModel_ProducesCanonicalModel(
        string vendor,
        string os,
        string rawModel,
        string expected
    )
    {
        DeviceModelDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Hardware.SystemModel", rawModel, T),
            Fact.Create($"Device[{Dev}].VendorCanonical", vendor, T),
            Fact.Create($"Device[{Dev}].OsGuess", os, T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal(expected, results[0].Value.AsString());
        Assert.Equal($"Device[{Dev}].ModelCanonical", results[0].Id);
        Assert.Equal(FactPaths.Derived.DeviceModelCanonical, results[0].AttributePath);
    }

    [Fact]
    public void DeviceModel_JuniperJunosUnrecognizedModel_FallsBackToUppercase()
    {
        DeviceModelDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Hardware.SystemModel", "something-random-9999", T),
            Fact.Create($"Device[{Dev}].VendorCanonical", "Juniper", T),
            Fact.Create($"Device[{Dev}].OsGuess", "JunOS", T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal("SOMETHING-RANDOM-9999", results[0].Value.AsString());
    }

    [Fact]
    public void DeviceModel_UnrecognizedVendorOsCombination_ReturnsEmpty()
    {
        DeviceModelDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Hardware.SystemModel", "OptiPlex 7090", T),
            Fact.Create($"Device[{Dev}].VendorCanonical", "Dell", T),
        ];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void DeviceModel_NoVendorOrOs_ReturnsEmpty()
    {
        DeviceModelDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Hardware.SystemModel", "WS-C9300-48P", T)];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void DeviceModel_PrefersHwSystemModelOverDiscoveredModel()
    {
        DeviceModelDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Hardware.SystemModel", "WS-C9300-48P", T),
            Fact.Create($"Device[{Dev}].Discovered[192.168.1.1].Model", "some other model", T),
            Fact.Create($"Device[{Dev}].VendorCanonical", "Cisco", T),
            Fact.Create($"Device[{Dev}].OsGuess", "Cisco IOS-XE", T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal("Catalyst 9300", results[0].Value.AsString());
    }

    [Fact]
    public void DeviceModel_FallsBackToVendorGuess_WhenNoCanonicalVendor()
    {
        DeviceModelDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Hardware.SystemModel", "PA-220", T),
            Fact.Create($"Device[{Dev}].VendorGuess", "Palo Alto Networks", T),
            Fact.Create($"Device[{Dev}].OsGuess", "PAN-OS", T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal("PA 200", results[0].Value.AsString());
    }

    [Fact]
    public void DeviceModel_NoInputs_ReturnsEmpty()
    {
        DeviceModelDerivation d = new();
        Assert.Empty(d.Derive([]));
    }

    // ── FirmwareOsDerivation ───────────────────────────────────────────────────

    [Theory]
    [InlineData(DeviceKinds.Camera)]
    [InlineData(DeviceKinds.Thermostat)]
    [InlineData(DeviceKinds.IndustrialIot)]
    [InlineData(DeviceKinds.BuildingAutomation)]
    [InlineData(DeviceKinds.Printer)]
    public void FirmwareOs_FirmwareKindWithoutOs_EmitsFirmwareOsFamily(string kind)
    {
        FirmwareOsDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Kind", kind, T)];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal("firmware", results[0].Value.AsString());
        Assert.Equal($"Device[{Dev}].OS.Family", results[0].Id);
        Assert.Equal(FactPaths.SystemOsFamily, results[0].AttributePath);
    }

    [Theory]
    [InlineData(DeviceKinds.Host)] // general-purpose OS kinds stay untouched
    [InlineData(DeviceKinds.Server)]
    [InlineData(DeviceKinds.Router)] // network gear deliberately excluded (may self-report an OS)
    [InlineData(DeviceKinds.Switch)]
    [InlineData("some-unknown-kind")]
    public void FirmwareOs_NonFirmwareKind_ReturnsEmpty(string kind)
    {
        FirmwareOsDerivation d = new();
        Assert.Empty(d.Derive([Fact.Create($"Device[{Dev}].Kind", kind, T)]));
    }

    [Fact]
    public void FirmwareOs_OsFamilyPresentInBatch_ReturnsEmpty()
    {
        FirmwareOsDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Kind", DeviceKinds.Camera, T),
            Fact.Create($"Device[{Dev}].OS.Family", "linux", T),
        ];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void FirmwareOs_OsDistroPresentInBatch_ReturnsEmpty()
    {
        FirmwareOsDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Kind", DeviceKinds.Camera, T),
            Fact.Create($"Device[{Dev}].OS.Distro", "OpenWrt", T),
        ];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void FirmwareOs_WhitespaceOsValue_StillEmits()
    {
        // A blank OS value is "no OS reported", not a real OS — the firmware fact still fires.
        FirmwareOsDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Kind", DeviceKinds.Thermostat, T),
            Fact.Create($"Device[{Dev}].OS.Family", "  ", T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);
        Assert.Single(results);
        Assert.Equal("firmware", results[0].Value.AsString());
    }

    [Fact]
    public void FirmwareOs_NoInputs_ReturnsEmpty()
    {
        FirmwareOsDerivation d = new();
        Assert.Empty(d.Derive([]));
    }

    // ── VendorOsFromDiscoveredTypeDerivation ──────────────────────────────────

    [Theory]
    [InlineData("OnHub Mesh Point")]
    [InlineData("onhub")] // case-insensitive substring match
    public void VendorOsFromDiscoveredType_OnHubType_EmitsGoogleAndLinux(string deviceType)
    {
        VendorOsFromDiscoveredTypeDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Discovered[192.168.1.2].DeviceType", deviceType, T)];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Equal(2, results.Count);
        Fact vendor = results.First(f => f.AttributePath == FactPaths.DiscoveredVendor);
        Fact os = results.First(f => f.AttributePath == FactPaths.DiscoveredOs);
        Assert.Equal("Google", vendor.Value.AsString());
        Assert.Equal($"Device[{Dev}].Discovered[192.168.1.2].Vendor", vendor.Id);
        Assert.Equal("linux", os.Value.AsString());
        Assert.Equal($"Device[{Dev}].Discovered[192.168.1.2].Os", os.Id);
    }

    [Fact]
    public void VendorOsFromDiscoveredType_ObservedVendorPresent_OnlyEmitsOs()
    {
        // An observed UPnP manufacturer always wins over the kind-derived constant.
        VendorOsFromDiscoveredTypeDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Discovered[192.168.1.2].DeviceType", "OnHub Mesh Point", T),
            Fact.Create($"Device[{Dev}].Discovered[192.168.1.2].Vendor", "Google Inc.", T),
        ];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Fact os = Assert.Single(results);
        Assert.Equal(FactPaths.DiscoveredOs, os.AttributePath);
        Assert.Equal("linux", os.Value.AsString());
    }

    [Fact]
    public void VendorOsFromDiscoveredType_BothPresent_ReturnsEmpty()
    {
        VendorOsFromDiscoveredTypeDerivation d = new();
        Fact[] facts =
        [
            Fact.Create($"Device[{Dev}].Discovered[192.168.1.2].DeviceType", "OnHub Mesh Point", T),
            Fact.Create($"Device[{Dev}].Discovered[192.168.1.2].Vendor", "Google Inc.", T),
            Fact.Create($"Device[{Dev}].Discovered[192.168.1.2].Os", "ChromiumOS", T),
        ];

        Assert.Empty(d.Derive(facts));
    }

    [Theory]
    [InlineData("Nest-Audio")] // known Google type, but not OnHub — no rule matches
    [InlineData("Chromecast")]
    public void VendorOsFromDiscoveredType_UnknownType_ReturnsEmpty(string deviceType)
    {
        VendorOsFromDiscoveredTypeDerivation d = new();
        Assert.Empty(d.Derive([Fact.Create($"Device[{Dev}].Discovered[192.168.1.2].DeviceType", deviceType, T)]));
    }

    [Fact]
    public void VendorOsFromDiscoveredType_NoInputs_ReturnsEmpty()
    {
        VendorOsFromDiscoveredTypeDerivation d = new();
        Assert.Empty(d.Derive([]));
    }
}