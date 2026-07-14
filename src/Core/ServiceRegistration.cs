namespace JMW.Discovery.Core;

// ── Service identity ──────────────────────────────────────────────────────────

/// <summary>
/// A single named attribute used to identify a logical service across host migrations.
/// Unlike device fingerprints (which are hardware properties), service fingerprints
/// are logical properties of what the service knows or manages.
/// </summary>
public sealed record ServiceFingerprint(string Type, string Value);

/// <summary>Well-known service fingerprint type identifiers.</summary>
public static class ServiceFingerprintType
{
    /// <summary>
    /// A forward DNS zone the server is authoritative for.
    /// Reverse (.arpa) zones are excluded — they change with IP ranges.
    /// e.g. "home.lan", "corp.internal"
    /// </summary>
    public const string PrimaryZone = "primary-zone";

    /// <summary>A DHCP subnet the server manages. e.g. "192.168.1.0/24"</summary>
    public const string DhcpSubnet = "dhcp-subnet";

    /// <summary>
    /// Human-readable server name as configured in the service itself.
    /// Less stable than zone names but useful as a secondary fingerprint.
    /// </summary>
    public const string ServerName = "server-name";

    /// <summary>
    /// Fallback for services that don't have better fingerprints.
    /// Not recommended as primary — URL changes defeat the purpose of stable identity.
    /// </summary>
    public const string ServiceUrl = "service-url";
}

/// <summary>Describes a service for server-side identity resolution.</summary>
public sealed record ServiceProbe(
    string ServiceType,
    IReadOnlyList<ServiceFingerprint> Fingerprints
);

public sealed record ServiceIdentifyRequest(
    string AgentId,
    ServiceProbe Probe
);

public sealed record ServiceIdentifyResponse(
    string ServiceId,
    bool IsNew
);