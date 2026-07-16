namespace JMW.Discovery.Core;

/// <summary>
/// A single normalized identifier for a device or network node.
/// Values are always in normalized form — use FingerprintNormalizer to produce them.
/// </summary>
public sealed record Fingerprint(string Type, string Value);

/// <summary>
/// Known fingerprint type identifiers.
/// Types are open strings — new types can be added and handled by the normalizer
/// without changing any other code.
/// </summary>
public static class FingerprintType
{
    // ── Device fingerprints ───────────────────────────────────────────────────

    /// <summary>
    /// Burned-in MAC address. Locally-administered and multicast MACs are rejected.
    /// Normalized: 12 lowercase hex digits, no separators. e.g. "001a2b3c4d5e"
    /// </summary>
    public const string Mac = "mac";

    /// <summary>
    /// Chassis serial number, vendor-scoped.
    /// Normalized: "{vendor}:{serial}" e.g. "cisco:ftx2144abcd"
    /// Requires vendor when normalizing.
    /// </summary>
    public const string ChassisSerial = "chassis-serial";

    /// <summary>
    /// Serial number of an internal, non-removable storage device, vendor-scoped.
    /// Only collected for fixed media (e.g. soldered SSDs) — removable/external
    /// drives are excluded because their serial follows the disk, not the device.
    /// Normalized: "{vendor}:{serial}" e.g. "apple:0ba020cac2882e30"
    /// </summary>
    public const string DiskSerial = "disk-serial";

    /// <summary>
    /// UUID assigned by hypervisor or platform (virtual devices).
    /// Normalized: lowercase with dashes. e.g. "550e8400-e29b-41d4-a716-446655440000"
    /// </summary>
    public const string Uuid = "uuid";

    /// <summary>
    /// SNMPv3 engine ID (RFC 3411). Stable secondary fingerprint when serial unavailable.
    /// Normalized: lowercase hex, no separators. e.g. "80001f8880c0a8010100"
    /// Length: 10–64 hex chars (5–32 bytes).
    /// </summary>
    public const string SnmpEngineId = "snmp-engine-id";

    /// <summary>
    /// SSH host key fingerprint. Changes only on key regeneration (rare).
    /// Only available via SSH — not via SNMP.
    /// Normalized: "{algorithm}:{hash}" e.g. "sha256:ABC123..."
    /// </summary>
    public const string SshHostKey = "ssh-host-key";

    /// <summary>
    /// BGP router-id. Stable by convention but reconfigurable — secondary fingerprint.
    /// Normalized: canonical IPv4 dotted decimal. e.g. "10.0.0.1"
    /// </summary>
    public const string BgpRouterId = "bgp-router-id";

    /// <summary>
    /// OSPF router-id. Same stability caveats as BGP router-id.
    /// Normalized: canonical IPv4 dotted decimal.
    /// </summary>
    public const string OspfRouterId = "ospf-router-id";

    // ── Network / VRF fingerprints ────────────────────────────────────────────

    /// <summary>
    /// IP prefix (IPv4 or IPv6). Used to identify Network nodes.
    /// Host bits are zeroed during normalization ("10.0.0.1/24" → "10.0.0.0/24").
    /// Normalized: canonical CIDR. e.g. "10.0.0.0/24" or "2001:db8::/32"
    /// </summary>
    public const string IpPrefix = "ip-prefix";

    /// <summary>
    /// BGP route distinguisher. Identifies a VRF in MPLS L3VPN networks.
    /// Three formats: "ASN:value" (Type 0/2) or "IP:value" (Type 1).
    /// Normalized: canonical parts, colon separator. e.g. "65000:100" or "192.168.1.1:1"
    /// </summary>
    public const string RouteDistinguisher = "route-distinguisher";

    // ── Host / server fingerprints ────────────────────────────────────────────

    /// <summary>
    /// OS-generated stable machine identity.
    /// Linux: /etc/machine-id (128-bit hex, generated at first boot).
    /// Windows: HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid.
    /// macOS: IOPlatformUUID from IOPlatformExpertDevice.
    /// Normalized: lowercase, no dashes. e.g. "b08ffa4a8c5e4a2b9f3d1e7c6a0b2d4e"
    /// </summary>
    public const string MachineId = "machine-id";

    // ── IoT / Building Automation fingerprints ────────────────────────────────

    /// <summary>
    /// BACnet vendor-scoped device instance. Per ASHRAE 135, the (VendorId, DeviceInstance)
    /// tuple must be globally unique across all BACnet networks.
    /// Normalized: "{vendor_id}:{device_instance}" e.g. "5:1024"
    /// </summary>
    public const string BacnetVendorInstance = "bacnet-vendor-instance";

    /// <summary>
    /// Modbus FC 43 / MEI Type 14 device identity. Uses VendorName + ProductCode from
    /// the device identification objects (available on ~30-50% of Modbus TCP devices).
    /// Normalized: "{vendor}:{product_code}" lowercase, e.g. "schneider-electric:m241"
    /// </summary>
    public const string ModbusMeiProduct = "modbus-mei-product";

    // ── Google Wifi (OnHub) ────────────────────────────────────────────────────

    /// <summary>
    /// Per-unit hardware identifier read locally from a Google Wifi / Nest Wifi
    /// access point's diagnostic report (report field 21) — a clean 128-bit hex
    /// string, e.g. "adec2ad42acef8cb5384a6d7cfda90a3". Distinguishes individual
    /// APs (unlike the shared platform "hardwareId" string). Normalized: trimmed,
    /// lowercase hex.
    /// </summary>
    public const string GoogleWifiDeviceId = "google-wifi-device-id";

    /// <summary>
    /// Stable Google Cast device id (32 hex) advertised in a device's
    /// <c>_googlecast._tcp</c> mDNS service instance. Intrinsic to the physical
    /// device and independent of its IP/MAC, so it anchors mDNS-derived identity
    /// across DHCP address changes. Normalized: trimmed, lowercase hex.
    /// </summary>
    public const string CastId = "cast-id";

    /// <summary>
    /// Google Wifi obscured MAC — the firmware masks the final device byte, leaving
    /// 11 real hex nibbles plus a '*' (e.g. "703acb70d06*"). Unique enough within a
    /// network to serve as a stable identity anchor, so all of a device's OnHub data
    /// stays cohesive on one record even when the real MAC can't be reconstructed
    /// from ARP/DHCP. When the real MAC *is* reconstructed it is registered as a
    /// separate <see cref="Mac" /> fingerprint, which bridges to other observers.
    /// Normalized: 11 lowercase hex digits + trailing '*', separators stripped; a
    /// multicast or locally-administered (randomized) first octet is rejected, same
    /// identity policy as <see cref="Mac" /> (see FingerprintNormalizer.NormalizeObscuredMac).
    /// </summary>
    public const string ObscuredMac = "obscured-mac";

    // ── Home Assistant ─────────────────────────────────────────────────────────

    /// <summary>
    /// Home Assistant device-registry identity for devices with no MAC — restricted to a
    /// small allow-list of identifier domains known to identify real, individually-addressable
    /// hardware (network printers via IPP; Nabu Casa USB radios via homeassistant_*; see
    /// HomeAssistantDeviceCollector.HasAllowedIdentifierDomain). Built from the registry
    /// entry's <c>identifiers</c> tuples, which HA guarantees are stable across restarts.
    /// Normalized: trimmed, case preserved (identifiers include case-sensitive tokens such as
    /// IEEE addresses and vendor-assigned ids).
    /// </summary>
    public const string HaIdentifiers = "ha-identifiers";
}