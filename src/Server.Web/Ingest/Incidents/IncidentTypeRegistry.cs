using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.Server.Incidents;

/// <summary>
/// The value-driven incident types wired into <see cref="IncidentEvaluator" />. Each entry is a
/// registry row, not a code path — adding a new incident type is one entry here plus (if it's new
/// to the fleet) a FactPaths constant, and it shows up on the Dashboard/Recent Activity/Device
/// History automatically (see docs: "From Noise to Signal" design proposal, §06).
/// Deliberately NOT covered here (fast-follow, not silently dropped):
///   service_down          — no single unambiguous "service health" fact path exists yet across
///                            service types (DNS/HA/CA each expose different signals).
///   cert_expiring         — "expires within 30 days" crosses its threshold as the clock advances,
///                            not when a fact value changes, so it needs periodic reconciliation
///                            (like agent_offline) rather than an ingest-triggered evaluator.
///   device_offline        — silence-driven like agent_offline, but devices (unlike agents) have no
///                            per-entity heartbeat/interval to derive a threshold from. Flagged as
///                            an open sequencing question in the design doc itself.
///   fingerprint_conflict  — not fact-driven at all; opened/resolved from DeviceRegistry/ConflictsApi
///                            directly (see IncidentsRepository.OpenConflictAsync/ResolveManualAsync).
///   agent_offline         — sweep-driven, see AgentLivenessSweepService.
/// </summary>
public static class IncidentTypeRegistry
{
    public static readonly TimeSpan DefaultReopenWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Reopen window for flap-prone availability incidents (agent/device offline — wifi blips,
    /// power cycles). Used by AgentLivenessSweepService, not by any entry in CreateAll() below
    /// (those are all value-driven, not availability types).
    /// </summary>
    public static readonly TimeSpan AvailabilityReopenWindow = TimeSpan.FromMinutes(15);

    public static IReadOnlyList<IncidentTypeDef> CreateAll() =>
    [
        new(
            IncidentType: "smart_failing",
            EntityKind: "device",
            Attribute: FactPaths.DiskSmartOverallHealth,
            ShouldOpen: v => v.AsString() is { Length: > 0 } s && !IsHealthy(s, "PASSED"),
            ShouldResolve: v => v.AsString() is { } s && IsHealthy(s, "PASSED"),
            Detail: v => v.AsString() is { } s ? $"smart_health: {s}" : null,
            ReopenWindow: DefaultReopenWindow
        ),

        // Hysteresis: opens at >=90%, resolves only once back under 85% — the 85-90% band is a
        // dead zone where ShouldOpen/ShouldResolve are both false, so a value sitting on the
        // boundary doesn't flap the incident open/closed on every poll.
        new(
            IncidentType: "filesystem_full",
            EntityKind: "device",
            Attribute: FactPaths.Derived.FsUsedPercent,
            ShouldOpen: v => v.AsDouble() is { } d && d >= 90.0,
            ShouldResolve: v => v.AsDouble() is { } d && d < 85.0,
            Detail: v => v.AsDouble() is { } d ? $"used: {d:F1}%" : null,
            ReopenWindow: DefaultReopenWindow
        ),

        new(
            IncidentType: "container_not_running",
            EntityKind: "device",
            Attribute: FactPaths.ContainerState,
            ShouldOpen: v => v.AsString() is { Length: > 0 } s && !IsHealthy(s, "running"),
            ShouldResolve: v => v.AsString() is { } s && IsHealthy(s, "running"),
            Detail: v => v.AsString() is { } s ? $"state: {s}" : null,
            ReopenWindow: DefaultReopenWindow
        ),

        new(
            IncidentType: "hardware_failed",
            EntityKind: "device",
            Attribute: FactPaths.HwComponentStatus,
            ShouldOpen: v => v.AsString() is { Length: > 0 } s && !IsHealthy(s, "ok", "healthy"),
            ShouldResolve: v => v.AsString() is { } s && IsHealthy(s, "ok", "healthy"),
            Detail: v => v.AsString() is { } s ? $"status: {s}" : null,
            ReopenWindow: DefaultReopenWindow
        ),
    ];

    private static bool IsHealthy(string value, params ReadOnlySpan<string> healthyValues)
    {
        foreach (string healthy in healthyValues)
        {
            if (string.Equals(value, healthy, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}