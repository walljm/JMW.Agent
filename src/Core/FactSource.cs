namespace JMW.Discovery.Core;

/// <summary>
/// Which collector/scanner produced a fact. A ushort ordinal so it's cheap to carry on
/// every <see cref="Fact" />; stamped once at the small number of collection-orchestration
/// choke points in the agent (see Agent.CollectOneLocalWithStatsAsync/CollectDeviceAsync/
/// CollectServicesAsync and NetworkDiscoveryCollector's per-attribute-key lookup), not at
/// every individual Fact.Create() call site. <see cref="Unknown" /> is the safe default for
/// facts constructed before this existed, or by any path that doesn't stamp a source.
///
/// Values are PERSISTED (facts_history.source) — this is an on-disk contract, not an
/// implementation detail. Every member has an EXPLICIT value:
/// - Never renumber or reuse a value, even for a removed source — old history rows still
///   reference it.
/// - Add new members at the end of their category's block, using the next free number in
///   that block.
/// - Categories are separated into 100-wide blocks so a value's range alone identifies its
///   kind, and each block has headroom to grow without colliding with the next.
/// </summary>
public enum FactSource : ushort
{
    Unknown = 0,

    /// <summary>Facts merged across multiple network scanners before creation (MAC/Hostname/
    /// Sources) — no single scanner owns those, so this is the generic bucket for them.</summary>
    NetworkDiscovery = 1,

    /// <summary>
    /// A fact typed in directly by an operator (fill an unknown field, override a wrong one, or
    /// author a custom field value) rather than observed by any collector/scanner. Written through
    /// the same <see>
    ///     <cref>FactRepository</cref>
    /// </see>
    /// /<see cref="Fact" /> pipeline as everything else —
    /// last-write-wins against whatever a collector reports next — but it is the only source ever
    /// allowed to be hard-deleted from facts_history (reverting an override or clearing a custom
    /// field), since every other source's history must stay append-only. See
    /// docs/plans/user-provided.md.
    /// </summary>
    ManualEntry = 2,

    // ── Network scanners (INetworkScanner.Name): 100-199 ──────────────────────
    AirPlay = 100,
    ArpScanner = 101,
    BacnetScanner = 102,
    Coap = 103,
    DnsPtr = 104,
    Eureka = 105,
    GatewayArp = 106,
    HttpBanner = 107,
    Ipp = 108,
    Ldap = 109,
    Llmnr = 110,
    Mdns = 111,
    ModbusScanner = 112,
    Mqtt = 113,
    Nbns = 114,
    Onvif = 115,
    PhilipsHue = 116,
    PingSweep = 117,
    Roku = 118,
    Rtsp = 119,
    Smb2 = 120,
    SnmpBroadcast = 121,
    SnmpPrinter = 122,
    Ssdp = 123,
    SshBanner = 124,
    TlsCert = 125,
    WsDiscovery = 126,

    // ── Local collectors (ILocalCollector.Name): 200-299 ──────────────────────
    DhcpLeasesLocal = 200,
    ArpLocal = 201,
    CertScan = 202,
    Battery = 203,
    Filesystem = 204,
    Disk = 205,
    Docker = 206,
    NetworkLocal = 207,
    Gpu = 208,
    HwInventory = 209,
    Port = 210,
    Packages = 211,
    Hardware = 212,
    Os = 213,
    Security = 214,
    Routes = 215,
    Process = 216,
    StepClient = 217,
    Updates = 218,
    ServiceLocal = 219,
    RebootHistory = 220,
    User = 221,
    StepCa = 222,

    // ── Device collectors (keyed by DeviceTarget.Protocol): 300-399 ───────────
    Ssh = 300,
    SnmpDevice = 301,
    BacnetDevice = 302,
    ModbusDevice = 303,
    GoogleWifi = 304,

    // ── Service collectors (IServiceCollector.ServiceType): 400-499 ───────────
    TechnitiumDns = 400,
    HomeAssistant = 401,
}