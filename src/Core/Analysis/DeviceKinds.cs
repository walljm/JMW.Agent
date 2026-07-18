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

    // ── Additional taxonomy ported from ITPIE.DeviceAnalysis's Types/ManufacturerTypes.cs,
    // converted to this codebase's kebab-case convention. Not every constant here has a
    // DeviceKindDerivation dispatch rule producing it yet — kept for completeness/future
    // extension, same as an unused enum member costs nothing. Deliberately excludes the
    // reference project's purely military/carrier-satellite categories (HAIPE crypto
    // terminals, SATCOM modems/terminals, generic "Crypto", MSPP/MSSP telecom transport,
    // "RuggedChassis") — those aren't IT/network hardware in the sense the rest of this
    // taxonomy is, and no signal in this codebase could ever produce them with confidence.
    public const string AnalogVoiceGateway = "analog-voice-gateway";
    public const string Apm = "apm";
    public const string Application = "application";
    public const string ApWids = "ap-wids";
    public const string AudioDevice = "audio-device";
    public const string BladeServer = "blade-server";
    public const string BladeServerChassis = "blade-server-chassis";
    public const string Camera = "camera";
    public const string CoaxModem = "coax-modem";
    public const string DocsisCableDevice = "docsis-cable-device";
    public const string DoorController = "door-controller";
    public const string EmbeddedSwitch = "embedded-switch";
    public const string EthernetPatchPanel = "ethernet-patch-panel";
    public const string FabricManager = "fabric-manager";
    public const string FiberModem = "fiber-modem";
    public const string FiberPatchPanel = "fiber-patch-panel";
    public const string Hub = "hub";
    public const string Intercom = "intercom";
    public const string IpsIds = "ips-ids";
    public const string Kvm = "kvm";
    public const string L3Switch = "l3-switch";
    public const string LoadBalancer = "load-balancer";
    public const string Microphone = "microphone";
    public const string Oob = "oob";
    public const string Pbx = "pbx";
    public const string Pdu = "pdu";
    public const string Phone = "phone";
    public const string Radio = "radio";
    public const string Repeater = "repeater";
    public const string San = "san";
    public const string Sbc = "sbc";
    public const string SdnOrchestrator = "sdn-orchestrator";
    public const string ServerAppliance = "server-appliance";
    public const string Speaker = "speaker";
    public const string Tap = "tap";
    public const string TerminalServer = "terminal-server";
    public const string Thermostat = "thermostat";
    public const string TimeClock = "time-clock";
    public const string TransparentBridge = "transparent-bridge";
    public const string UcSessionController = "uc-session-controller";
    public const string VirtualSwitch = "virtual-switch";
    public const string Vm = "vm";
    public const string VmHypervisor = "vm-hypervisor";
    public const string VoiceGateway = "voice-gateway";
    public const string VpnConcentrator = "vpn-concentrator";
    public const string Vtc = "vtc";
    public const string Waf = "waf";
    public const string WirelessController = "wireless-controller";

    /// <summary>
    /// Kinds whose devices run embedded firmware rather than a general-purpose, inventoriable
    /// OS (cameras, thermostats, appliance-class IoT). <see cref="Derivations.FirmwareOsDerivation" />
    /// emits <see cref="FactPaths.SystemOsFamily" /> = "firmware" for these when no real OS fact
    /// is present, so reports show "firmware" instead of a blank OS. Network gear
    /// (router/switch/AP/firewall) is deliberately excluded — those frequently self-report a
    /// nameable OS (RouterOS, OpenWrt, IOS, ChromiumOS) that collectors or banner derivations
    /// can still discover.
    /// </summary>
    public static readonly IReadOnlySet<string> FirmwareOnlyKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        IndustrialIot,
        BuildingAutomation,
        AudioDevice,
        Camera,
        DoorController,
        Intercom,
        Microphone,
        Pdu,
        Phone,
        Printer,
        Radio,
        Speaker,
        Thermostat,
        TimeClock,
        Ups,
    };
}