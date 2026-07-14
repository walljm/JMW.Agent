using System.Text.Json.Serialization;

namespace JMW.Discovery.Core;

/// <summary>
/// Wire format for a single collection cycle from one device.
/// Sent as gzip-compressed JSON. Fact IDs share long common prefixes
/// ("Device[uuid].Interface[mac].*") that compress 80–90%.
/// AgentId is verified server-side against the API key — the server rejects
/// batches where AgentId does not match the authenticated agent.
/// </summary>
public sealed record FactBatch(
    string AgentId,
    string DeviceId,
    DateTimeOffset CollectedAt,
    [property: JsonPropertyName("facts")] IReadOnlyList<Fact> Facts
);

/// <summary>
/// One device's or service's contribution in an agent facts submission.
/// For device batches, Fingerprints identify the device and Service is null.
/// For service batches, Service carries the identity probe and Fingerprints is
/// empty — the server resolves a stable ServiceId from the probe's logical
/// fingerprints (e.g. DNS zones) instead of hardware fingerprints.
/// Fact IDs use a placeholder root that the server rewrites to
/// Device[{resolved_device_id}] or Service[{resolved_service_id}] after resolution.
/// </summary>
public sealed record FactBatchElement(
    IReadOnlyList<Fingerprint> Fingerprints,
    IReadOnlyList<Fact> Facts,
    ServiceProbe? Service = null
);

/// <summary>
/// Per-collector timing and fact-count summary for one collection cycle.
/// </summary>
public sealed record CollectorStat(string Name, int Facts, int DurationMs, string? Error);

/// <summary>
/// Per-scanner timing and device-count summary for one network discovery run.
/// </summary>
public sealed record ScannerStat(string Name, int DevicesFound, int DurationMs, string? Error);

/// <summary>
/// Per-target timing and fact-count summary for one remote device-collector
/// ("device scanner") run — e.g. an ssh/snmp/google-wifi target.
/// </summary>
public sealed record DeviceScannerStat(string Target, string Protocol, int Facts, int DurationMs, string? Error);

/// <summary>
/// Per-target timing and fact-count summary for one service-collector run — e.g. a
/// Technitium DNS or Home Assistant target polled over its API.
/// </summary>
public sealed record ServiceStat(string Target, string Type, int Facts, int DurationMs, string? Error);

/// <summary>
/// Summary of one agent heartbeat cycle — what ran, how long, how much was collected.
/// Attached to AgentFactsRequest so the server can persist it for the Activity view.
/// </summary>
public sealed record AgentCycleSummary(
    int DurationMs,
    int FactsSent,
    IReadOnlyList<CollectorStat>? Collectors,
    IReadOnlyList<ScannerStat>? Scanners,
    IReadOnlyList<DeviceScannerStat>? DeviceScanners = null,
    IReadOnlyList<ServiceStat>? Services = null
);

/// <summary>
/// Body of POST /api/v1/agent/facts — one request per collection cycle,
/// covering all devices the agent collected during that cycle.
/// Sent as gzip-compressed JSON.
/// </summary>
public sealed record AgentFactsRequest(
    Guid AgentId,
    DateTimeOffset CollectedAt,
    IReadOnlyList<FactBatchElement> FactBatches,
    AgentCycleSummary? CycleSummary = null
);

/// <summary>
/// Response to POST /api/v1/agent/facts.
/// </summary>
public sealed record AgentFactsResponse(
    int AcceptedBatches,
    IReadOnlyList<ResolvedDevice> ResolvedDevices
);

/// <summary>
/// Device resolution result for one batch element.
/// </summary>
public sealed record ResolvedDevice(
    string FingerprintsHash,
    string DeviceId,
    bool IsNew
);