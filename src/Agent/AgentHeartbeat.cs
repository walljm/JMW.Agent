using JMW.Discovery.Core;

namespace JMW.Discovery.Agent;

// ── Heartbeat ─────────────────────────────────────────────────────────────────

/// <summary>Sent by the agent on each collection cycle.</summary>
public sealed record HeartbeatRequest(
    Guid AgentId,
    string? Version,
    string? PassiveDiscoveryMode,
    // Every registered local collector and network scanner, with whether IsSupported returned
    // true for this OS/platform — drives AgentDetail's collector/scanner list server-side
    // instead of a hardcoded guess. Null on older agent builds that predate this field.
    IReadOnlyList<AgentCapability>? Collectors = null,
    IReadOnlyList<AgentCapability>? Scanners = null
);

/// <summary>One registered collector or scanner's name and whether it can run on this host.</summary>
public sealed record AgentCapability(string Name, bool Supported);

/// <summary>
/// Server response — carries an optional update offer and the server-assembled
/// configuration block.
/// The agent applies the update if UpdateInfo is non-null and signature validates;
/// it applies Config (intervals, collector toggles, targets) over its file-based
/// config when present.
/// </summary>
public sealed record HeartbeatResponse(
    UpdateInfo? Update,
    HeartbeatConfig? Config = null
);

// ── Update offer ──────────────────────────────────────────────────────────────

/// <summary>
/// Describes a binary update offered by the server.
/// All fields must be verified by the agent before applying.
/// </summary>
public sealed record UpdateInfo(
    // <summary>Human-readable version string. e.g. "1.3.2"</summary>
    string Version,
    // <summary>HTTPS URL to download the new binary from.</summary>
    string Url,
    // <summary>Lowercase hex SHA-256 of the binary at Url.</summary>
    string Sha256,
    // <summary>Expected size in bytes. 0 = not checked.</summary>
    long Size,
    // <summary>
    // Base64-encoded ECDSA P-256 signature over the canonical metadata string.
    // The metadata string is: "version={Version}\nfilename={filename}\nsha256={Sha256}\nsize={Size}\n"
    // where filename is the last path segment of Url.
    // </summary>
    string Signature,
    // <summary>Must equal Updater.Algorithm or the update is rejected.</summary>
    string SignatureAlgorithm
);