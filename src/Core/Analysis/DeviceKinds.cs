namespace JMW.Discovery.Core.Analysis;

/// <summary>
/// Canonical <see cref="FactPaths.DeviceKind" /> values. Collectors set the coarse buckets
/// (<see cref="Host" />, <see cref="NetworkDevice" />, <see cref="Router" />,
/// <see cref="IndustrialIot" />, <see cref="BuildingAutomation" />) at identification time;
/// <see cref="Derivations.DeviceKindDerivation" /> refines the coarse buckets into the more
/// specific values below when it has a confident signal (SMBIOS chassis type, vendor, model,
/// SNMP sysDescr). Kept as a small closed set sized to the device types this codebase actually
/// discovers (home/SMB network + IoT), not the enterprise carrier-grade catalog this pattern is
/// conceptually borrowed from.
/// </summary>
public static class DeviceKinds
{
    // ── Coarse buckets set directly by collectors ─────────────────────────────
    public const string Host = "host";
    public const string NetworkDevice = "network-device";
    public const string Router = "router";
    public const string IndustrialIot = "industrial-iot";
    public const string BuildingAutomation = "building-automation";

    // ── Refined by DeviceKindDerivation from Host ─────────────────────────────
    public const string Desktop = "desktop";
    public const string Laptop = "laptop";
    public const string Server = "server";
    public const string Tablet = "tablet";

    // ── Refined by DeviceKindDerivation from NetworkDevice ────────────────────
    public const string Switch = "switch";
    public const string AccessPoint = "access-point";
    public const string Firewall = "firewall";
    public const string Nas = "nas";
    public const string Printer = "printer";
    public const string Ups = "ups";
}
