using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace JMW.Discovery.Core;

/// <summary>
/// Normalizes raw fingerprint values into a consistent canonical form.
/// Returns null for any input that is invalid or too unreliable to use as
/// a node identifier. All methods are pure — no I/O, no external state.
/// </summary>
public static class FingerprintNormalizer
{
    public static string? Normalize(string type, string rawValue, string? vendor = null) =>
        type switch
        {
            FingerprintType.Mac => NormalizeMac(rawValue),
            FingerprintType.ChassisSerial => NormalizeSerial(rawValue, vendor),
            FingerprintType.DiskSerial => NormalizeSerial(rawValue, vendor),
            FingerprintType.Uuid => NormalizeUuid(rawValue),
            FingerprintType.MachineId => NormalizeMachineId(rawValue),
            FingerprintType.SnmpEngineId => NormalizeSnmpEngineId(rawValue),
            FingerprintType.SshHostKey => NormalizeSshHostKey(rawValue),
            FingerprintType.BgpRouterId => NormalizeRouterId(rawValue),
            FingerprintType.OspfRouterId => NormalizeRouterId(rawValue),
            FingerprintType.IpPrefix => NormalizeIpPrefix(rawValue),
            FingerprintType.RouteDistinguisher => NormalizeRouteDistinguisher(rawValue),
            FingerprintType.BacnetVendorInstance => NormalizeBacnetVendorInstance(rawValue),
            FingerprintType.ModbusMeiProduct => NormalizeModbusMeiProduct(rawValue),
            FingerprintType.GoogleWifiDeviceId => NormalizeGoogleWifiDeviceId(rawValue),
            FingerprintType.CastId => NormalizeCastId(rawValue),
            FingerprintType.ObscuredMac => NormalizeObscuredMac(rawValue),
            FingerprintType.HaIdentifiers => NormalizeHaIdentifiers(rawValue),
            _ => null,
        };

    // ── Google Wifi per-unit hardware id ───────────────────────────────────────
    //
    // The Google Wifi diagnostic report's field-21 id is a hex string (128-bit in
    // the wild). Normalize by trimming and lowercasing; require at least 8 hex
    // digits and reject anything containing non-hex characters.

    public static string? NormalizeGoogleWifiDeviceId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string value = raw.Trim().ToLowerInvariant();
        if (value.Length < 8)
        {
            return null;
        }

        foreach (char c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return null;
            }
        }

        return value;
    }

    // ── Google Cast device id ──────────────────────────────────────────────────
    //
    // The _googlecast mDNS instance carries a stable per-device hex id (32 hex in
    // the wild). Normalize by trimming and lowercasing; require at least 16 hex
    // digits and reject anything containing non-hex characters.

    public static string? NormalizeCastId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string value = raw.Trim().ToLowerInvariant();
        if (value.Length < 16)
        {
            return null;
        }

        foreach (char c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return null;
            }
        }

        return value;
    }

    // ── Google Wifi obscured MAC ───────────────────────────────────────────────
    //
    // The firmware preserves the real OUI (first 3 bytes / 6 hex nibbles) and obfuscates the
    // device-specific bytes, then appends '*' (e.g. real "00e0bf400073" → reported "00e0bf1fc40*").
    // Only the OUI is trustworthy — the trailing nibbles are NOT the real device bytes, so they are
    // never used for matching (reconstruction keys off the OUI alone; see ObscuredMac.Pick). The full
    // value is kept verbatim only as an opaque, stable key for the obscured sighting. Strip
    // separators, lowercase, and require exactly 11 hex digits followed by a single '*'. A
    // fully-masked value ("************") has no hex nibbles and is rejected — no identifying signal.
    //
    // The preserved OUI means the first octet is genuine, so apply the same identity policy as
    // Apply the same identity policy as NormalizeMac: reject a first octet whose multicast (0x01)
    // or locally-administered (0x02) bit is set. A locally-administered value here is a randomized
    // MAC (e.g. an Apple "Private Wi-Fi Address"), which is NOT a stable device identity — without
    // this guard a rotating randomized Wi-Fi MAC mints a fresh device on every rotation (observed:
    // one MacBook split into two records, real OUI 64:4b:f0 vs randomized 3a:91:b0). The sighting
    // is still kept as an observation (proj_discovered); it just never becomes a fingerprint.

    public static string? NormalizeObscuredMac(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        Span<char> buf = stackalloc char[raw.Length];
        int len = 0;
        foreach (char c in raw.Trim())
        {
            if (c is ':' or '-' or '.')
            {
                continue;
            }

            buf[len++] = char.ToLowerInvariant(c);
        }

        // Exactly 11 hex nibbles + one trailing '*'.
        if (len != 12 || buf[11] != '*')
        {
            return null;
        }

        for (int i = 0; i < 11; i++)
        {
            if (!IsHexDigit(buf[i]))
            {
                return null;
            }
        }

        // First octet = first two nibbles. Reject multicast (0x01) / locally-administered (0x02),
        // mirroring NormalizeMac — a randomized OUI is not a stable identity.
        if (!byte.TryParse(buf[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte firstOctet)
         || (firstOctet & 0x03) != 0)
        {
            return null;
        }

        return new string(buf[..12]);
    }

    // ── Home Assistant device-registry identity ────────────────────────────────
    //
    // Built by the collector from a device's `identifiers` tuples. Case is preserved
    // (IEEE addresses and vendor ids are sometimes case-sensitive) — only trimmed and
    // rejected when blank.

    public static string? NormalizeHaIdentifiers(string raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    // ── MAC ───────────────────────────────────────────────────────────────────

    public static string? NormalizeMac(string raw)
    {
        // Shared canonical form (bare 12-hex lowercase) — identical to the fact/projection side.
        if (MacFormat.ToBareHex(raw) is not { } value)
        {
            return null;
        }

        // Identity policy layered on top: reject the values that are not stable device identifiers.
        if (value is "000000000000" or "ffffffffffff")
        {
            return null;
        }

        if (!byte.TryParse(value.AsSpan(0, 2), NumberStyles.HexNumber, null, out byte first))
        {
            return null;
        }

        if ((first & 0x01) != 0)
        {
            return null; // multicast
        }

        if ((first & 0x02) != 0)
        {
            return null; // locally administered
        }

        return value;
    }

    // ── Chassis serial ────────────────────────────────────────────────────────

    public static string? NormalizeSerial(string raw, string? vendor)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string value = raw.Trim().ToLowerInvariant();
        if (value.Length < 4)
        {
            return null;
        }

        if (InvalidSerials.Contains(value))
        {
            return null;
        }

        bool allSame = true;
        for (int i = 1; i < value.Length; i++)
        {
            if (value[i] != value[0])
            {
                allSame = false;
                break;
            }
        }

        if (allSame)
        {
            return null;
        }

        string? normalizedVendor = NormalizeVendor(vendor);
        if (normalizedVendor is null)
        {
            return null;
        }

        return $"{normalizedVendor}:{value}";
    }

    // ── Machine ID ───────────────────────────────────────────────────────────
    // Linux /etc/machine-id: 32 lowercase hex chars. Windows MachineGuid: 32
    // hex chars (hyphens stripped). macOS IOPlatformUUID: UUID with hyphens
    // stripped to 32 hex chars. Agent normalizes all three to 32-char lowercase
    // hex before sending; we accept either form (with or without hyphens).

    public static string? NormalizeMachineId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string value = raw.Trim().Replace("-", "").ToLowerInvariant();
        if (value.Length < 16)
        {
            return null;
        }

        foreach (char c in value)
        {
            if (!IsHexDigit(c))
            {
                return null;
            }
        }

        if (value.All(c => c == '0'))
        {
            return null;
        }

        return value;
    }

    // ── UUID ──────────────────────────────────────────────────────────────────

    public static string? NormalizeUuid(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string trimmed = raw.Trim().Trim('{', '}').Trim();
        if (!Guid.TryParse(trimmed, out Guid guid))
        {
            return null;
        }

        if (guid == Guid.Empty)
        {
            return null;
        }

        return guid.ToString("D").ToLowerInvariant();
    }

    // ── SNMP engine ID ────────────────────────────────────────────────────────
    //
    // RFC 3411: 5–32 bytes. Usually represented as hex, sometimes with colons,
    // spaces, or "0x" prefix. The first byte's bit 7 indicates authoritative
    // format (enterprise ID in bytes 2–4) — we don't validate the enterprise
    // structure, just the length and hex content.

    public static string? NormalizeSnmpEngineId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string trimmed = raw.Trim();

        // Strip common "0x" prefix
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        Span<char> buf = stackalloc char[trimmed.Length];
        int len = 0;
        foreach (char c in trimmed)
        {
            if (c is ':' or ' ' or '-')
            {
                continue;
            }

            buf[len++] = char.ToLowerInvariant(c);
        }

        // RFC 3411: 5–32 bytes → 10–64 hex chars
        if (len < 10 || len > 64 || len % 2 != 0)
        {
            return null;
        }

        Span<char> hex = buf[..len];
        foreach (char c in hex)
        {
            if (!IsHexDigit(c))
            {
                return null;
            }
        }

        // Reject all-zeros
        bool allZero = true;
        for (int i = 0; i < len; i++)
        {
            if (hex[i] != '0')
            {
                allZero = false;
                break;
            }
        }

        if (allZero)
        {
            return null;
        }

        return new string(hex);
    }

    // ── SSH host key ──────────────────────────────────────────────────────────
    //
    // OpenSSH format: "SHA256:base64" (no padding) or "MD5:xx:xx:...:xx"
    // The algorithm prefix is case-insensitive; the hash content is case-sensitive
    // for base64 but lowercased for MD5 hex. We normalize the algorithm to
    // lowercase and canonicalize the hash portion per algorithm.

    public static string? NormalizeSshHostKey(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string trimmed = raw.Trim();
        int colon = trimmed.IndexOf(':');
        if (colon <= 0)
        {
            return null;
        }

        string algorithm = trimmed[..colon].ToLowerInvariant();
        string hash = trimmed[(colon + 1)..];

        switch (algorithm)
        {
            case "sha256":
            case "sha1":
                // base64 (URL-safe, no padding) — keep verbatim, validate chars
                if (string.IsNullOrEmpty(hash))
                {
                    return null;
                }

                foreach (char c in hash)
                {
                    if (!IsBase64UrlChar(c))
                    {
                        return null;
                    }
                }

                return $"{algorithm}:{hash}";

            case "md5":
                // "xx:xx:xx:..." — strip colons, lowercase
                string md5 = hash.Replace(":", "").ToLowerInvariant();
                if (md5.Length != 32)
                {
                    return null;
                }

                foreach (char c in md5)
                {
                    if (!IsHexDigit(c))
                    {
                        return null;
                    }
                }

                return $"md5:{md5}";

            default:
                return null; // unknown algorithm
        }
    }

    // ── BGP / OSPF router-id ──────────────────────────────────────────────────

    public static string? NormalizeRouterId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!IPAddress.TryParse(raw.Trim(), out IPAddress? addr))
        {
            return null;
        }

        if (addr.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        byte[] bytes = addr.GetAddressBytes();
        if (bytes is [0, 0, 0, 0] or [255, 255, 255, 255])
        {
            return null;
        }

        if (bytes[0] == 127)
        {
            return null;
        }

        return addr.ToString();
    }

    // ── IP prefix ─────────────────────────────────────────────────────────────
    //
    // Used to identify Network nodes. Host bits are zeroed (lenient normalization)
    // so "10.0.0.1/24" and "10.0.0.0/24" resolve to the same canonical form.
    // Both IPv4 and IPv6 are supported.

    public static string? NormalizeIpPrefix(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string trimmed = raw.Trim();
        int slash = trimmed.LastIndexOf('/');
        if (slash <= 0)
        {
            return null;
        }

        if (!IPAddress.TryParse(trimmed[..slash], out IPAddress? addr))
        {
            return null;
        }

        if (!int.TryParse(trimmed[(slash + 1)..], out int prefixLen))
        {
            return null;
        }

        int maxBits = addr.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLen < 0 || prefixLen > maxBits)
        {
            return null;
        }

        // Zero host bits
        byte[] bytes = addr.GetAddressBytes();
        MaskHostBits(bytes, prefixLen);
        IPAddress networkAddr = new(bytes);

        return $"{networkAddr}/{prefixLen}";
    }

    // ── Route distinguisher ───────────────────────────────────────────────────
    //
    // Three encoding types, two string forms:
    //   "ASN:value"  — Type 0 (2-byte ASN, 4-byte value) or
    //                  Type 2 (4-byte ASN, 2-byte value)
    //   "IP:value"   — Type 1 (4-byte IP, 2-byte value)
    //
    // We distinguish by whether the left side parses as an IPv4 address.
    // Canonical form preserves the string representation with normalized parts
    // (no leading zeros, lowercase IP).

    public static string? NormalizeRouteDistinguisher(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string trimmed = raw.Trim();
        int colon = trimmed.IndexOf(':');
        if (colon <= 0)
        {
            return null;
        }

        string left = trimmed[..colon].Trim();
        string right = trimmed[(colon + 1)..].Trim();

        if (!long.TryParse(right, out long rightVal) || rightVal < 0)
        {
            return null;
        }

        // Type 1: left side is a dotted-decimal IPv4 address.
        // Must contain a dot — IPAddress.TryParse accepts bare integers as IPv4
        // (e.g. "65000" → "0.0.253.232") which would silently mis-classify ASNs.
        if (left.Contains('.')
         && IPAddress.TryParse(left, out IPAddress? ip)
         && ip.AddressFamily == AddressFamily.InterNetwork)
        {
            if (rightVal > 65535)
            {
                return null; // Type 1 value field is 16-bit
            }

            return $"{ip}:{rightVal}";
        }

        // Type 0 / Type 2: left side is an ASN (integer)
        if (!long.TryParse(left, out long asnVal) || asnVal < 0)
        {
            return null;
        }

        if (asnVal > 4_294_967_295L)
        {
            return null; // max 32-bit ASN
        }

        // Reject 0:0 (unassigned/placeholder)
        if (asnVal == 0 && rightVal == 0)
        {
            return null;
        }

        // Type 0: 2-byte ASN (≤65535) + 4-byte value (≤4294967295)
        // Type 2: 4-byte ASN (>65535) + 2-byte value (≤65535)
        if (asnVal > 65535 && rightVal > 65535)
        {
            return null;
        }

        return $"{asnVal}:{rightVal}";
    }

    // ── BACnet vendor-scoped instance ─────────────────────────────────────────
    //
    // Format: "{vendor_id}:{device_instance}" — both must be non-negative integers.
    // Per ASHRAE 135, this tuple must be globally unique across all BACnet networks.

    public static string? NormalizeBacnetVendorInstance(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        int colon = raw.IndexOf(':');
        if (colon <= 0)
        {
            return null;
        }

        if (!uint.TryParse(raw[..colon].Trim(), out uint vendorId))
        {
            return null;
        }

        if (!uint.TryParse(raw[(colon + 1)..].Trim(), out uint deviceInstance))
        {
            return null;
        }

        // Device instance 4194303 is the wildcard/unassigned value — reject it.
        if (deviceInstance == 4194303)
        {
            return null;
        }

        return $"{vendorId}:{deviceInstance}";
    }

    // ── Modbus MEI product identity ───────────────────────────────────────────
    //
    // Format: "{vendor}:{product_code}" — both normalized to lowercase, trimmed.
    // Derived from FC 43 MEI Type 14 Device Identification objects 0x00 and 0x01.

    public static string? NormalizeModbusMeiProduct(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        int colon = raw.IndexOf(':');
        if (colon <= 0)
        {
            return null;
        }

        string vendorPart = raw[..colon].Trim().ToLowerInvariant();
        string productPart = raw[(colon + 1)..].Trim().ToLowerInvariant();

        if (vendorPart.Length < 2 || productPart.Length < 1)
        {
            return null;
        }

        return $"{vendorPart}:{productPart}";
    }

    // ── Vendor ────────────────────────────────────────────────────────────────

    public static string? NormalizeVendor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string lower = raw.Trim().ToLowerInvariant();
        Span<char> buf = stackalloc char[lower.Length];
        int len = 0;
        bool lastWasHyphen = true;

        foreach (char c in lower)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                buf[len++] = c;
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen)
            {
                buf[len++] = '-';
                lastWasHyphen = true;
            }
        }

        if (len > 0 && buf[len - 1] == '-')
        {
            len--;
        }

        return len == 0 ? null : new string(buf[..len]);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsHexDigit(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static bool IsBase64UrlChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '-' or '_' or '=';

    private static void MaskHostBits(byte[] bytes, int prefixLen)
    {
        int fullBytes = prefixLen / 8;
        int remainder = prefixLen % 8;

        for (int i = fullBytes; i < bytes.Length; i++)
        {
            bytes[i] = i == fullBytes && remainder > 0
                ? (byte)(bytes[i] & (0xFF << (8 - remainder)))
                : (byte)0;
        }
    }

    private static readonly HashSet<string> InvalidSerials = new(StringComparer.Ordinal)
    {
        "n/a",
        "na",
        "none",
        "null",
        "unknown",
        "not available",
        "not applicable",
        "not specified",
        "to be filled by o.e.m.",
        "default string",
        "serial number",
        "chassis serial number",
        "tbd",
        "empty",
        "invalid",
    };
}