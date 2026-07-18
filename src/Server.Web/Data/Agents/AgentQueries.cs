using System.Text.Json;

using ITPIE.Database.Abstractions;

using Npgsql;

namespace JMW.Discovery.Server.Queries;

public static partial class AgentQueries
{
    // ── Agents ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts a new agent registration. Returns the inserted agent_id.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<AgentIdResult> InsertAgentAsync(
        this NpgsqlConnection connection,
        Guid agentId,
        string hostname,
        string apiKeyHash,
        string? zone,
        string version,
        string? passiveDiscoveryMode,
        string? os,
        string? arch,
        string? ipAddress,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Returns (agent_id, status) for an agent matching the given API key hash.
    /// Returns no rows if the hash is not found.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(Guid AgentId, string Status)> FindAgentByHashAsync(
        this NpgsqlConnection connection,
        string apiKeyHash,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Updates heartbeat fields and returns the current status.
    /// Returns no rows if the agent is not found.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<HeartbeatStatusResult> UpdateAgentHeartbeatAsync(
        this NpgsqlConnection connection,
        Guid agentId,
        string? version,
        string? passiveDiscoveryMode,
        JsonElement? capabilities,
        CancellationToken cancellationToken
    );

    // ── Admin: Agents ─────────────────────────────────────────────────────────
    // Hand-built in AgentsApi.QueryAsync — its ORDER BY / keyset cursor are chosen dynamically
    // from a sortable-column allowlist, which a static [DatabaseCommand] can't express.

    /// <summary>
    /// Sets an agent's status to 'approved' and returns its agent_id.
    /// Returns no rows if the agent is not found.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<AgentIdResult> ApproveAgentAsync(
        this NpgsqlConnection connection,
        Guid agentId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Deletes an agent and returns its agent_id.
    /// Returns no rows if the agent is not found.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<AgentIdResult> DeleteAgentAsync(
        this NpgsqlConnection connection,
        Guid agentId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Sets an agent's status to 'disabled' — reversible, unlike delete: heartbeats/facts are
    /// rejected (HeartbeatEndpoint/FactsEndpoint already reject any non-'approved' status) but
    /// the agent's registration, config, and targets are untouched. Returns no rows if not found.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<AgentIdResult> DisableAgentAsync(
        this NpgsqlConnection connection,
        Guid agentId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Sets a disabled agent's status back to 'approved', letting it resume sending data.
    /// Returns no rows if the agent is not found.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<AgentIdResult> EnableAgentAsync(
        this NpgsqlConnection connection,
        Guid agentId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Sets an agent's zone (pass null to clear it) and returns its agent_id.
    /// Returns no rows if the agent is not found.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<AgentIdResult> UpdateAgentZoneAsync(
        this NpgsqlConnection connection,
        Guid agentId,
        string? zone,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Sets device_id on an agent if not already set (first successful fact batch).
    /// No-op if device_id is already populated.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<AgentIdResult> StampAgentDeviceAsync(
        this NpgsqlConnection connection,
        Guid agentId,
        Guid deviceId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Returns the status ('pending', 'approved', 'rejected') for an agent by ID.
    /// Returns no rows if the agent is not found.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<AgentStatusResult> GetAgentStatusAsync(
        this NpgsqlConnection connection,
        Guid agentId,
        CancellationToken cancellationToken
    );

    // ── Admin: Agent config ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the configuration block (intervals + collectors_config JSONB) for one agent.
    /// Returns no rows if the agent id is unknown.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(Guid AgentId, string Hostname, string Status, int HeartbeatIntervalSecs, int
            DiscoveryIntervalSecs
          , int InventoryIntervalSecs, JsonElement CollectorsConfig, DateTimeOffset? ClearTrackersRequestedAt,
            DateTimeOffset? LogsRequestedAt, int? LogsRequestedLines, string? LogsRequestedBefore)>
        GetAgentConfigAsync(
            this NpgsqlConnection connection,
            Guid agentId,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Requests that the agent clear its local delta-tracker cache on its next heartbeat.
    /// Returns the agent_id, or no rows if the agent is not found.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<AgentIdResult> RequestClearTrackersAsync(
        this NpgsqlConnection connection,
        Guid agentId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Requests that the agent upload its recent console/journald log output on its next
    /// heartbeat. <paramref name="lines"/> is the page size; <paramref name="before"/> is an
    /// opaque paging token (ring-buffer Seq or journald __CURSOR) relayed to the agent verbatim,
    /// or null for the newest page. Returns the agent_id, or no rows if the agent is not found.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(Guid AgentId, DateTimeOffset LogsRequestedAt)> RequestLogsAsync(
        this NpgsqlConnection connection,
        Guid agentId,
        int lines,
        string? before,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Returns the full agent identity plus configuration for the Agent Detail page.
    /// Returns no rows if the agent id is unknown.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(Guid AgentId, string Hostname, string Status, DateTimeOffset? LastHeartbeat,
            string? Zone, string? Version, string? Os, string? Arch, string? IpAddress, Guid? DeviceId, int
            HeartbeatIntervalSecs, int DiscoveryIntervalSecs, int InventoryIntervalSecs, JsonElement CollectorsConfig,
            string? Liveness, JsonElement? Capabilities)>
        GetAgentDetailAsync(
            this NpgsqlConnection connection,
            Guid agentId,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Updates an agent's heartbeat/discovery/inventory intervals.
    /// Returns the agent_id, or no rows if not found.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<AgentIdResult> UpdateAgentIntervalsAsync(
        this NpgsqlConnection connection,
        Guid agentId,
        int heartbeatIntervalSecs,
        int discoveryIntervalSecs,
        int inventoryIntervalSecs,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Merges a partial collectors_config JSONB object into the agent's stored config.
    /// The merge is a shallow top-level merge — the caller must supply the complete
    /// per-collector object so no inner fields are dropped.
    /// Returns the agent_id, or no rows if not found.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<AgentIdResult> UpdateCollectorConfigAsync(
        this NpgsqlConnection connection,
        Guid agentId,
        JsonElement configPatch,
        CancellationToken cancellationToken
    );

    // ── Agent Cycles ───────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts an agent cycle summary. Returns the inserted cycle_id.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<CycleIdResult> InsertAgentCycleAsync(
        this NpgsqlConnection connection,
        Guid agentId,
        DateTimeOffset cycleAt,
        int durationMs,
        int factsSent,
        int errorCount,
        JsonElement collectors,
        JsonElement scanners,
        JsonElement deviceScanners,
        JsonElement services,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Lists recent agent cycles for an agent, newest first. Pass null for since/until to leave
    /// that bound open; errorsOnly restricts to cycles where error_count &gt; 0; collectionOnly
    /// excludes bare heartbeat ticks (no collector/scanner/device-scanner/service ran) so a
    /// short heartbeat cadence doesn't drown a browsable history in empty rows.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(long CycleId, DateTimeOffset CycleAt, int DurationMs, int FactsSent, int ErrorCount,
            JsonElement Collectors, JsonElement Scanners, JsonElement DeviceScanners, JsonElement Services)>
        ListAgentCyclesAsync(
            this NpgsqlConnection connection,
            Guid agentId,
            DateTimeOffset? since,
            DateTimeOffset? until,
            bool errorsOnly,
            int limit,
            bool collectionOnly,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Per-collector/scanner/device-target/service-target run count, error count, and median
    /// duration across the given cycle window (null since/until leaves that bound open). Kind
    /// is "collector", "scanner", "device-scanner", or "service" — a local collector and a
    /// network scanner can report the same runtime name (e.g. ArpCollector and ArpScanner both
    /// report "arp"), so Kind disambiguates them. For "device-scanner" rows Name is the
    /// protocol (e.g. "ssh"); for "service" rows Name is the service type (e.g. "http") — those
    /// two kinds have no single-instance identity worth grouping by since they're per-target.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string? Name, string? Kind, int? RunCount, int? ErrorCount, double? MedianDurationMs)>
        GetCollectorHealthSummaryAsync(
            this NpgsqlConnection connection,
            Guid agentId,
            DateTimeOffset? since,
            DateTimeOffset? until,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Per-remote-target run count, error count, and median duration across the given cycle
    /// window (null since/until leaves that bound open), for the Targets tab's inline health
    /// cue. Target is the stat's own key — the endpoint for device-scanner runs, the
    /// label-or-endpoint for service runs — and CollectorType mirrors targets.collector_type,
    /// so the pair identifies one configured target row.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string? Target, string? CollectorType, int? RunCount, int? ErrorCount, double?
            MedianDurationMs)>
        GetTargetHealthSummaryAsync(
            this NpgsqlConnection connection,
            Guid agentId,
            DateTimeOffset? since,
            DateTimeOffset? until,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Agent-level collection health for the Overview "Collection" tile: the most recent cycle
    /// that actually ran a collector/scanner/service probe (any age — heartbeat-only cycles are
    /// excluded, see GetAgentCollectionSummary.sql) plus cycle counts within the rolling window
    /// since <paramref name="since"/>. Always returns one row — columns are null/0 when the
    /// agent has no cycles yet.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(DateTimeOffset? LastCycleAt, int? LastFacts, int? LastErrors, int? LastDurationMs, int?
            WindowTotal, int? WindowErrored)>
        GetAgentCollectionSummaryAsync(
            this NpgsqlConnection connection,
            Guid agentId,
            DateTimeOffset since,
            CancellationToken cancellationToken
        );
}