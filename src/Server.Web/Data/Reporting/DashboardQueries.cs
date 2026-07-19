using ITPIE.Database.Abstractions;

using Npgsql;

namespace JMW.Discovery.Server.Queries;

/// <summary>
/// Source-generated aggregate queries backing the redesigned Fleet Dashboard (SCR-003).
/// Each method's SQL lives in a peer file named after the method minus the "Async" suffix
/// (e.g. <c>GetNetworkSummaryAsync</c> → <c>GetNetworkSummary.sql</c>) in this directory.
/// Counts/sums/averages are typed nullable to match the validator's schema-only inference.
/// Device counts exclude merged/alias devices (via <c>device_aliases</c>) so totals agree
/// with the Devices report they link to.
/// </summary>
public static partial class DashboardQueries
{
    // ── Network zone: device / service / zone totals + 24h coverage ─────────────

    /// <summary>
    /// Single-row network totals: device counts by management status, total services,
    /// distinct agent zones, and 24h reporting-vs-quiet device coverage.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(long? TotalDevices, long? ManagedDevices, long? DiscoveredDevices,
            long? ServicesTotal, long? DistinctZones, string? ZoneNames, long? Reporting24h, long? Quiet24h)>
        GetNetworkSummaryAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    /// <summary>Services grouped by type (descending), for the Services tile part-to-whole bar.</summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string Type, long? Count)> GetServicesByTypeAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    // ── Recent activity zone ────────────────────────────────────────────────────

    /// <summary>Single-row activity counts: new devices (7d), not-seen devices (7d), changes (24h).</summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(long? NewDevices7d, long? NotSeen7d, long? Changes24h)>
        GetActivitySummaryAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    /// <summary>Newest devices first observed within <paramref name="days" /> days, newest first.</summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(Guid DeviceId, string? FriendlyName, string ManagementStatus, DateTimeOffset CreatedAt)>
        GetNewDevicesAsync(
            this NpgsqlConnection connection,
            int days,
            int limit,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Devices whose newest fingerprint <c>last_seen</c> is older than <paramref name="days" />
    /// days (gone quiet), oldest first.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(Guid DeviceId, string? FriendlyName, DateTimeOffset? LastSeen)>
        GetNotSeenDevicesAsync(
            this NpgsqlConnection connection,
            int days,
            int limit,
            CancellationToken cancellationToken
        );

    // ── Fleet health zone: agents + collection pipeline ─────────────────────────

    /// <summary>
    /// Single-row agent health: total / approved / pending counts plus heartbeat-derived
    /// online / stale / offline liveness. Liveness is relative to each agent's own
    /// <c>heartbeat_interval_secs</c> (online ≤ 3× interval; offline if never seen or &gt; 1h; else stale).
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(long? TotalAgents, long? ApprovedAgents, long? PendingAgents,
            long? OnlineAgents, long? StaleAgents, long? OfflineAgents)>
        GetAgentHealthSummaryAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Agents for the health panel table, worst liveness first (offline, then stale, then online),
    /// limited to <paramref name="limit" /> rows. Liveness derived as in GetAgentHealthSummary.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(Guid AgentId, string Hostname, string Status, DateTimeOffset? LastHeartbeat,
            string? Zone, string? Version, string? PassiveDiscoveryMode, string? Liveness)>
        GetAgentHealthListAsync(
            this NpgsqlConnection connection,
            int limit,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Single-row collection rollup over the latest REAL cycle per agent (heartbeat-only ticks
    /// excluded — see GetCollectionSummary.sql): total facts sent, agents with errors, average
    /// cycle duration (ms), and agents that have a real cycle on record.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(long? FactsSentTotal, long? AgentsWithErrors, long? AvgDurationMs, long? AgentsReporting)>
        GetCollectionSummaryAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    /// <summary>Collection error counts bucketed hourly over the last 24h, for the error-rate sparkline.</summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(DateTimeOffset? Bucket, long? Errors)> GetCollectionErrorSeriesAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Facts sent per day, summed fleet-wide, over the last <paramref name="days" /> days — raw
    /// collection volume. Paired with <see cref="GetCollectionDailyChangesAsync" />, which counts
    /// only the confirmed subset that actually changed a value.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(DateTimeOffset? Day, long? FactsSent)> GetCollectionDailyFactsSentAsync(
            this NpgsqlConnection connection,
            int days,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Confirmed fact changes per day, summed fleet-wide, over the last <paramref name="days" />
    /// days. Paired with <see cref="GetCollectionDailyFactsSentAsync" />; a gap between the two
    /// series indicates resend noise (e.g. an agent's delta cache reset) rather than genuine drift.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(DateTimeOffset? Day, long? Count)> GetCollectionDailyChangesAsync(
            this NpgsqlConnection connection,
            int days,
            CancellationToken cancellationToken
        );

    // ── Needs-attention zone: posture rollup from surviving projections ─────────

    /// <summary>
    /// Service CA certificates expiring within 30 days. The one Needs-Attention signal not yet
    /// migrated to the incidents table — see IncidentQueries.GetOpenIncidentCountsAsync for
    /// everything else (disks/filesystems/containers/hardware/conflicts/agents).
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<CertsExpiringResult> GetCertsExpiringAsync(
        this NpgsqlConnection connection,
        CancellationToken cancellationToken
    );

    // ── Network composition breakdowns (device counts by dimension) ─────────────

    /// <summary>Live-device counts by device-maker vendor (matches Devices vendor semantics), descending.</summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string? Vendor, long? Count)> GetCompositionByVendorAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    /// <summary>Live-device counts by OS family, descending.</summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string? OsFamily, long? Count)> GetCompositionByOsFamilyAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    /// <summary>Live-device counts by device kind, descending.</summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string? Kind, long? Count)> GetCompositionByKindAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    /// <summary>Live-device counts by management status (managed vs discovered), descending.</summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string? ManagementStatus, long? Count)> GetCompositionByManagementStatusAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Live-device counts by discovery source (ARP, google-wifi, ssh-banner, …), descending. A
    /// device counts once per source it was seen by, so totals exceed the device count — a
    /// per-source reach breakdown, not a partition.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string? Source, long? Count)> GetCompositionByDiscoverySourceAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );
}