namespace JMW.Discovery.Core.Analysis.Normalizers;

/// <summary>
/// The single MAC normalizer: canonicalizes a MAC fact value to bare 12-hex lowercase — the same
/// form <see cref="FingerprintNormalizer.NormalizeMac" /> produces, so these values join directly
/// against <c>device_fingerprints.fp_value</c> (review D34/D2). Registered for every MAC VALUE path
/// (interface MAC + PermMAC, ARP, discovered). Preserves locally-administered/multicast MACs (the
/// real observed address); drops the null MAC (00:00:00:00:00:00 — utun/gif interfaces with no
/// Ethernet address) and any value that is not a 12-hex MAC. MACs used as dimension KEYS (DHCP
/// leases) are handled by ingest key-normalization; obscured MACs keep their own path.
/// </summary>
public sealed class MacValueNormalizer : INormalizer
{
    public IReadOnlyList<string> AttributePathPatterns =>
    [
        FactPaths.ArpMac,
        FactPaths.DiscoveredMAC,
        FactPaths.InterfaceMAC,
        FactPaths.InterfacePermMAC,
    ];

    public FactValue? Normalize(FactValue raw)
    {
        if (raw.AsString() is not { } str)
        {
            return null;
        }

        // Bare 12-hex, or drop: a non-MAC or the null MAC (no Ethernet address) is not stored;
        // LA/multicast are preserved (the real observed address).
        string? bare = MacFormat.ToBareHex(str);
        return bare is null or "000000000000" ? null : FactValue.FromString(bare);
    }
}

/// <summary>
/// Canonicalizes an IP-address fact value to its normal form (System.Net.IPAddress): IPv4 with no
/// leading zeros, IPv6 lowercase + compressed ("FE80::1" → "fe80::1"). An optional CIDR suffix
/// ("192.168.1.5/24") is preserved. Values that don't parse as an IP (e.g. "*", a socket path) pass
/// through unchanged rather than being dropped. Registered for the IP VALUE paths; IP-keyed
/// dimensions (ARP/Discovered) are canonicalized by ingest key-normalization.
/// </summary>
public sealed class IpAddressNormalizer : INormalizer
{
    public IReadOnlyList<string> AttributePathPatterns =>
        [FactPaths.InterfaceIPv4, FactPaths.InterfaceIPv6, FactPaths.DhcpLocalLeaseIP, FactPaths.PortAddress];

    public FactValue? Normalize(FactValue raw)
    {
        if (raw.AsString() is not { } str)
        {
            return raw;
        }

        // Not an IP, or already canonical → leave untouched; else store the canonical form.
        string? canonical = IpFormat.Canonicalize(str);
        return canonical is null || canonical == str ? raw : FactValue.FromString(canonical);
    }
}

/// <summary>
/// Canonicalizes a discovered device's serial-number fact value to the same
/// <c>"bare:&lt;value&gt;"</c> form the server's unscoped-serial fingerprint path (via
/// <see cref="FingerprintNormalizer.NormalizeSerial" />) writes to <c>device_fingerprints.fp_value</c>
/// for fp_type='chassis-serial' — without this, ONVIF/Roku/SNMP scanners write the raw scanner-cased
/// serial to <c>proj_discovered</c> while the fingerprint side stores the prefixed, lowercased
/// form, so the anti-join in GetNewDiscoveredSerials.sql can never match (same bug class as the
/// MAC mismatch MacValueNormalizer fixes, for the no-MAC/serial-identified device population).
/// Drops values NormalizeSerial itself rejects (too short, blocklisted, all-same-character) —
/// those can never resolve to a fingerprint either, so keeping the raw junk value serves no purpose.
/// </summary>
public sealed class SerialValueNormalizer : INormalizer
{
    public IReadOnlyList<string> AttributePathPatterns =>
        [FactPaths.DiscoveredOnvifSerial, FactPaths.DiscoveredRokuSerial, FactPaths.DiscoveredSnmpSerial];

    public FactValue? Normalize(FactValue raw)
    {
        if (raw.AsString() is not { } str)
        {
            return null;
        }

        string? normalized = FingerprintNormalizer.NormalizeSerial(str, "bare");
        return normalized is null ? null : FactValue.FromString(normalized);
    }
}

/// <summary>
/// Canonicalizes a discovered device's UUID fact value (SSDP USN / WS-Discovery endpoint
/// reference) to the same lowercase, hyphenated, braces-stripped form
/// <see cref="FingerprintNormalizer.NormalizeUuid" /> writes to <c>device_fingerprints.fp_value</c>
/// for fp_type='uuid' — so GetNewDiscoveredSerials.sql's uuid anti-join matches regardless of the
/// scanner's original casing/braces. Drops values that aren't a parseable, non-empty GUID.
/// </summary>
public sealed class UuidValueNormalizer : INormalizer
{
    public IReadOnlyList<string> AttributePathPatterns =>
        [FactPaths.DiscoveredSsdpUuid, FactPaths.DiscoveredWsdUuid];

    public FactValue? Normalize(FactValue raw)
    {
        if (raw.AsString() is not { } str)
        {
            return null;
        }

        string? normalized = FingerprintNormalizer.NormalizeUuid(str);
        return normalized is null ? null : FactValue.FromString(normalized);
    }
}

/// <summary>
/// Passes FactValues of kind <typeparamref name="T" /> through unchanged, and returns null
/// for any other kind.  Useful in tests to confirm that facts without a registered
/// normalizer are not silently transformed.
/// </summary>
/// <typeparam name="T">
/// The <see cref="FactValueKind" /> this noop normalizer accepts.  Values of any other
/// kind are rejected (null returned) so tests get a clear signal when an unexpected
/// kind arrives.
/// </typeparam>
public sealed class NoopNormalizer<T> : INormalizer where T : struct
{
    private readonly FactValueKind _expectedKind;

    /// <param name="attributePathPattern">The attribute_path this normalizer is registered for.</param>
    /// <param name="expectedKind">Only values of this kind pass through; all others return null.</param>
    public NoopNormalizer(string attributePathPattern, FactValueKind expectedKind)
    {
        AttributePathPatterns = [attributePathPattern];
        _expectedKind = expectedKind;
    }

    public IReadOnlyList<string> AttributePathPatterns { get; }

    /// <summary>
    /// Returns <paramref name="raw" /> unchanged when its kind matches the expected kind;
    /// returns null otherwise.
    /// </summary>
    public FactValue? Normalize(FactValue raw) =>
        raw.Kind == _expectedKind ? raw : null;
}