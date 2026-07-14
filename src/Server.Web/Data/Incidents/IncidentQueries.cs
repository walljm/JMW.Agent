using ITPIE.Database.Abstractions;

using Npgsql;

namespace JMW.Discovery.Server.Queries;

/// <summary>
/// Source-generated queries over <c>incidents</c> and <c>change_events</c> — the curated
/// incident/event change model (docs: "From Noise to Signal" design proposal). Each method's SQL
/// lives in a peer file named after the method minus the "Async" suffix.
/// </summary>
public static partial class IncidentQueries
{
    // ── Ingest-time writes (IncidentEvaluator, AgentLivenessSweepService) ────────

    /// <summary>
    /// Opens a new incident, reopens a recently-resolved one within the reopen window (flap
    /// suppression), or refreshes last_seen_at/detail on an already-open one.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<long> OpenOrTouchIncidentAsync(
        this NpgsqlConnection connection,
        string entityKind,
        string entityId,
        string incidentType,
        string? detail,
        double reopenWindowSeconds,
        CancellationToken cancellationToken
    );

    /// <summary>Auto-resolves the open incident for this entity+type, if any.</summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<long> ResolveIncidentAsync(
        this NpgsqlConnection connection,
        string entityKind,
        string entityId,
        string incidentType,
        CancellationToken cancellationToken
    );

    /// <summary>Manually resolves the open incident for this entity+type (admin action).</summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<long> ResolveIncidentManualAsync(
        this NpgsqlConnection connection,
        string entityKind,
        string entityId,
        string incidentType,
        CancellationToken cancellationToken
    );

    /// <summary>Records a one-shot change event (discovered/promoted/merged/...).</summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<long> InsertChangeEventAsync(
        this NpgsqlConnection connection,
        string eventType,
        string entityKind,
        string entityId,
        string? detail,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Every agent's current heartbeat-derived liveness, for AgentLivenessSweepService — reuses
    /// agent_liveness(), the single liveness definition also used by GetAgentHealthSummary.sql/
    /// GetAgentHealthList.sql/AgentsApi.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(Guid AgentId, string Liveness)> ListAgentLivenessAsync(
        this NpgsqlConnection connection,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Fingerprint pairs currently shared by more than one un-excluded device — the same set
    /// ConflictsApi's admin listing shows. Used by FingerprintConflictSweepService to open/keep
    /// open a fingerprint_conflict incident per pair.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string FpType, string FpValue)> ListOpenFingerprintConflictsAsync(
        this NpgsqlConnection connection,
        CancellationToken cancellationToken
    );

    /// <summary>Entity IDs with a currently-open incident of the given type — used to detect ones that no longer apply.</summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<string> ListOpenIncidentEntityIdsAsync(
        this NpgsqlConnection connection,
        string entityKind,
        string incidentType,
        CancellationToken cancellationToken
    );

    // ── Reads ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Dashboard "Needs Attention": open incident counts grouped by type, fleet-wide. Replaces
    /// GetPostureSummary.sql's hand-written per-category COUNT...FILTER queries with one query
    /// over the incidents table — a new incident type shows up here automatically, no new SQL.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string IncidentType, long OpenCount, long DistinctEntities)>
        GetOpenIncidentCountsAsync(this NpgsqlConnection connection, CancellationToken cancellationToken);

    /// <summary>
    /// Recent Activity, fleet-wide: incidents (opened or resolved) and change_events merged into
    /// one most-recent-first feed, capped at $1. Resolved incidents carry their duration natively
    /// (resolved_at - opened_at) — the raw fact-diff feed this replaces never could.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string Kind, string TypeName, string EntityKind, string EntityId,
        string? Detail, DateTimeOffset At, TimeSpan? Duration, string? Resolution, string? EntityName)>
        ListRecentActivityAsync(this NpgsqlConnection connection, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Device Detail History tab: full incident+event timeline for one entity, richest detail,
    /// most-recent-first.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string Kind, string TypeName, string? Detail, DateTimeOffset At,
        TimeSpan? Duration, string? Resolution)> ListEntityHistoryAsync(
        this NpgsqlConnection connection,
        string entityKind,
        string entityId,
        int limit,
        CancellationToken cancellationToken
    );
}
