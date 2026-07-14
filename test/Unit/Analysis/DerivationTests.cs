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

    // ── VendorFromSnmpSysDescrDerivation ───────────────────────────────────────

    [Theory]
    [InlineData("Cisco IOS Software, C2960 Software", "Cisco")]
    [InlineData("Cisco NX-OS(tm) n9000", "Cisco")]
    [InlineData("RouterOS 6.49.6", "Mikrotik")]
    [InlineData("EdgeOS 2.0.9-hotfix.4", "Ubiquiti")]
    [InlineData("HP ProCurve Switch 2530-24G", "HP")]
    [InlineData("FortiOS 7.2.5", "Fortinet")]
    public void VendorFromSnmpSysDescr_KnownSignature_ProducesGuess(string sysDescr, string vendor)
    {
        VendorFromSnmpSysDescrDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].SNMP.SysDescr", sysDescr, T)];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal(vendor, results[0].Value.AsString());
        Assert.Equal($"Device[{Dev}].VendorGuess", results[0].Id);
        Assert.Equal(FactPaths.Derived.DeviceVendorGuess, results[0].AttributePath);
    }

    [Fact]
    public void VendorFromSnmpSysDescr_CaseInsensitive_StillMatches()
    {
        VendorFromSnmpSysDescrDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].SNMP.SysDescr", "cisco ios software", T)];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal("Cisco", results[0].Value.AsString());
    }

    [Theory]
    [InlineData("Cisco NX-OS and IOS hybrid", "Cisco")] // NX-OS checked before IOS
    public void VendorFromSnmpSysDescr_MultipleSignaturesPresent_MostSpecificWins(string sysDescr, string vendor)
    {
        VendorFromSnmpSysDescrDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].SNMP.SysDescr", sysDescr, T)];

        IReadOnlyList<Fact> results = d.Derive(facts);
        Assert.Single(results);
        Assert.Equal(vendor, results[0].Value.AsString());
    }

    [Fact]
    public void VendorFromSnmpSysDescr_UnknownDescr_ReturnsEmpty()
    {
        VendorFromSnmpSysDescrDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].SNMP.SysDescr", "Linux server 5.15.0 x86_64", T)];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void VendorFromSnmpSysDescr_NoInputs_ReturnsEmpty()
    {
        VendorFromSnmpSysDescrDerivation d = new();
        Assert.Empty(d.Derive([]));
    }

    [Fact]
    public void VendorFromSnmpSysDescr_WhitespaceOnlyValue_ReturnsEmpty()
    {
        VendorFromSnmpSysDescrDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].SNMP.SysDescr", "   ", T)];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void VendorFromSnmpSysDescr_UnrelatedFactsOnly_ReturnsEmpty()
    {
        VendorFromSnmpSysDescrDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].SNMP.SysName", "switch1", T)];

        Assert.Empty(d.Derive(facts));
    }

    // ── OsFromSnmpSysDescrDerivation ────────────────────────────────────────────

    [Theory]
    [InlineData("Cisco IOS Software, C2960 Software", "Cisco IOS")]
    [InlineData("Cisco IOS-XE Software, ASR1000 Software", "Cisco IOS-XE")]
    [InlineData("Cisco NX-OS(tm) n9000", "Cisco NX-OS")]
    [InlineData("RouterOS 6.49.6", "RouterOS")]
    [InlineData("Juniper Networks, Inc. srx340, kernel JUNOS 20.4R3-S1.6", "JunOS")]
    [InlineData("EdgeOS 2.0.9-hotfix.4", "EdgeOS")]
    [InlineData("UniFi OS 3.2.9", "UniFi OS")]
    [InlineData("Palo Alto Networks PA-220 series firewall, PAN-OS 10.1.6", "PAN-OS")]
    [InlineData("FortiOS 7.2.5", "FortiOS")]
    [InlineData("ArubaOS 8.10.0.4", "ArubaOS")]
    public void OsFromSnmpSysDescr_KnownSignature_ProducesGuess(string sysDescr, string osName)
    {
        OsFromSnmpSysDescrDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].SNMP.SysDescr", sysDescr, T)];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal(osName, results[0].Value.AsString());
        Assert.Equal($"Device[{Dev}].OsGuess", results[0].Id);
        Assert.Equal(FactPaths.Derived.DeviceOsGuess, results[0].AttributePath);
    }

    [Fact]
    public void OsFromSnmpSysDescr_IosXeNotMisclassifiedAsPlainIos()
    {
        OsFromSnmpSysDescrDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].SNMP.SysDescr", "Cisco IOS-XE Software, Version 17.03.04a", T)];

        IReadOnlyList<Fact> results = d.Derive(facts);
        Assert.Single(results);
        Assert.Equal("Cisco IOS-XE", results[0].Value.AsString());
    }

    [Fact]
    public void OsFromSnmpSysDescr_CaseInsensitive_StillMatches()
    {
        OsFromSnmpSysDescrDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].SNMP.SysDescr", "routeros 6.49.6", T)];

        IReadOnlyList<Fact> results = d.Derive(facts);
        Assert.Single(results);
        Assert.Equal("RouterOS", results[0].Value.AsString());
    }

    [Fact]
    public void OsFromSnmpSysDescr_UnknownDescr_ReturnsEmpty()
    {
        OsFromSnmpSysDescrDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].SNMP.SysDescr", "Linux server 5.15.0 x86_64", T)];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void OsFromSnmpSysDescr_NoInputs_ReturnsEmpty()
    {
        OsFromSnmpSysDescrDerivation d = new();
        Assert.Empty(d.Derive([]));
    }

    [Fact]
    public void OsFromSnmpSysDescr_WhitespaceOnlyValue_ReturnsEmpty()
    {
        OsFromSnmpSysDescrDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].SNMP.SysDescr", "   ", T)];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void OsFromSnmpSysDescr_UnrelatedFactsOnly_ReturnsEmpty()
    {
        OsFromSnmpSysDescrDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].SNMP.SysName", "switch1", T)];

        Assert.Empty(d.Derive(facts));
    }

    // ── VendorFromModelPrefixDerivation ─────────────────────────────────────────

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
    [InlineData("Surface Pro 9", "Microsoft")]
    [InlineData("ROG Strix G15", "ASUS")]
    [InlineData("ZenBook 14X", "ASUS")]
    public void VendorFromModelPrefix_HwSystemModel_KnownPrefix_ProducesGuess(string model, string vendor)
    {
        VendorFromModelPrefixDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Hardware.SystemModel", model, T)];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal(vendor, results[0].Value.AsString());
        Assert.Equal($"Device[{Dev}].VendorGuess", results[0].Id);
        Assert.Equal(FactPaths.Derived.DeviceVendorGuess, results[0].AttributePath);
    }

    [Fact]
    public void VendorFromModelPrefix_DiscoveredModel_KnownPrefix_ProducesGuess()
    {
        VendorFromModelPrefixDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Discovered[192.168.1.50].Model", "Galaxy S23", T)];

        IReadOnlyList<Fact> results = d.Derive(facts);

        Assert.Single(results);
        Assert.Equal("Samsung", results[0].Value.AsString());
    }

    [Fact]
    public void VendorFromModelPrefix_MustBeAtStart_NotJustContained()
    {
        VendorFromModelPrefixDerivation d = new();
        // "Surface" appears mid-string, not as a prefix — must not match.
        Fact[] facts = [Fact.Create($"Device[{Dev}].Hardware.SystemModel", "Generic Surface-Mount Board", T)];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void VendorFromModelPrefix_CaseInsensitive_StillMatches()
    {
        VendorFromModelPrefixDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Hardware.SystemModel", "thinkpad t14", T)];

        IReadOnlyList<Fact> results = d.Derive(facts);
        Assert.Single(results);
        Assert.Equal("Lenovo", results[0].Value.AsString());
    }

    [Fact]
    public void VendorFromModelPrefix_UnknownModel_ReturnsEmpty()
    {
        VendorFromModelPrefixDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Hardware.SystemModel", "Generic PC", T)];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void VendorFromModelPrefix_NoInputs_ReturnsEmpty()
    {
        VendorFromModelPrefixDerivation d = new();
        Assert.Empty(d.Derive([]));
    }

    [Fact]
    public void VendorFromModelPrefix_WhitespaceOnlyValue_ReturnsEmpty()
    {
        VendorFromModelPrefixDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Hardware.SystemModel", "   ", T)];

        Assert.Empty(d.Derive(facts));
    }

    [Fact]
    public void VendorFromModelPrefix_UnrelatedFactsOnly_ReturnsEmpty()
    {
        VendorFromModelPrefixDerivation d = new();
        Fact[] facts = [Fact.Create($"Device[{Dev}].Hardware.SystemVendor", "Dell", T)];

        Assert.Empty(d.Derive(facts));
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
}