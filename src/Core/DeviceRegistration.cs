namespace JMW.Discovery.Core;

// ── Device identity ───────────────────────────────────────────────────────────

public sealed record DeviceIdentifyRequest(
    string AgentId,
    IReadOnlyList<Fingerprint> Fingerprints
);

public sealed record DeviceIdentifyResponse(
    string DeviceId,
    bool IsNew
);

// ── Agent registration ────────────────────────────────────────────────────────

/// <summary>Sent to POST /api/v1/agent/register on first contact.</summary>
public sealed record AgentRegistrationRequest(
    Guid AgentId,
    string Hostname,
    string Version,
    string? Zone,
    string? PassiveDiscoveryMode,
    string? Os,
    string? Arch,
    string? IpAddress
);

/// <summary>
/// Server ack — api_key is returned immediately (status starts as "pending").
/// The key is needed to authenticate heartbeats while waiting for admin approval.
/// </summary>
public sealed record AgentRegistrationResponse(
    Guid AgentId,
    string Status,
    string ApiKey
);

// ── Heartbeat config delivery ──────────────────────────────────────────────────

/// <summary>
/// Server-assembled configuration delivered to an approved agent in the heartbeat
/// response. The agent applies these over its file-based config: intervals override
/// the local schedule, disabled collectors are skipped, and targets are merged into
/// the collection loop alongside file-configured ones.
/// This is the single shared contract between the server's config assembler and the
/// agent's heartbeat handler — both projects reference Core so the shape cannot drift.
/// </summary>
public sealed record HeartbeatConfig(
    int HeartbeatIntervalSecs,
    int DiscoveryIntervalSecs,
    int InventoryIntervalSecs,
    IReadOnlyDictionary<string, CollectorSetting> Collectors,
    IReadOnlyList<TargetConfig> Targets,
    // <summary>
    // PEM-encoded CA certificates the agent should trust in addition to the OS system
    // trust store, so validating collectors can authenticate operator-run HTTPS endpoints
    // signed by a private CA. A single entry may contain a chain (root + intermediates).
    // Null/empty means "system trust only". Delivered fleet-wide from server configuration;
    // this does not affect the agent's own server channel, which uses SHA-256 pinning.
    // Optional/defaulted so the contract stays compatible across mixed agent/server versions.
    // </summary>
    IReadOnlyList<string>? TrustedCaCertificates = null,
    // <summary>
    // Set when an admin requests (via the Fleet UI) that this agent clear its local
    // delta-tracker cache — null means no clear has ever been requested. The agent
    // compares this against a locally persisted marker of the last clear it acted on;
    // when this is newer, it wipes its tracker files and updates the marker.
    // </summary>
    DateTimeOffset? ClearTrackersRequestedAt = null
);

/// <summary>
/// Per-collector enable flag and optional interval override.
/// IntervalSecs is null when the collector inherits the agent-level interval.
/// Keyed in <see cref="HeartbeatConfig.Collectors" /> by collector class name
/// (e.g. "OsCollector").
/// </summary>
public sealed record CollectorSetting(
    bool Enabled,
    int? IntervalSecs
);

/// <summary>
/// A remote collection target delivered to the agent, with its decrypted credential.
/// CollectorType is the collector slug (e.g. "ssh", "snmp", "google-wifi",
/// "technitium-dns", "home-assistant"); the agent selects the matching
/// IDeviceCollector/IServiceCollector by it. Endpoint is a bare host/IP for
/// device-style collectors or a full URL for service-style ones. Label is an
/// optional human-readable name shown in agent logs. Credentials is null when the
/// target has no associated credential.
/// </summary>
public sealed record TargetConfig(
    string Endpoint,
    string CollectorType,
    string? Label,
    TargetCredential? Credentials
);

/// <summary>
/// A decrypted credential for a target. Type matches the credential store types:
/// ssh-key | ssh-password | snmp | api-token. Secret is the decrypted plaintext
/// (private key, password, community string, or token).
/// </summary>
public sealed record TargetCredential(
    string Type,
    string Secret
);