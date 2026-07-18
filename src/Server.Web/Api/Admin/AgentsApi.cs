using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Data;
using JMW.Discovery.Server.Infrastructure;
using JMW.Discovery.Server.Queries;
using JMW.Discovery.Server.Reporting;

using Npgsql;

namespace JMW.Discovery.Server.Admin;

public static class AgentsApi
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 200;

    /// <summary>
    /// Columns the agent list may be sorted by. Only columns with a supporting index are
    /// exposed — see <c>agents_status_idx</c>. One cursor shape —
    /// (sort_key, created_at, agent_id) — covers every sort.
    /// </summary>
    private static readonly Dictionary<string, string> SortExpressions =
        new(StringComparer.Ordinal)
        {
            ["created_at"] = "to_char(created_at at time zone 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US')",
            ["status"] = "status",
        };

    public const string DefaultSort = "created_at";
    public const string DefaultDir = "desc";

    /// <summary>Fixed liveness values — matches the CASE branches computed in QueryAsync
    /// (and duplicated in GetAgentHealthSummary.sql / GetAgentHealthList.sql).</summary>
    public static readonly IReadOnlyList<string> LivenessValues = ["online", "stale", "offline"];

    public static readonly IReadOnlySet<string> SortableColumns =
        SortExpressions.Keys.ToHashSet(StringComparer.Ordinal);

    public static bool IsSortable(string? sort) => sort is not null && SortExpressions.ContainsKey(sort);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/agents", ListAgents)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapPatch("/agents/{id}/approve", ApproveAgent)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapPatch("/agents/{id}/disable", DisableAgent)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapPatch("/agents/{id}/enable", EnableAgent)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapPatch("/agents/{id}/zone", SetZone)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapPost("/agents/{id}/clear-cache", ClearCache)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapPost("/agents/{id}/request-logs", RequestLogs)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapGet("/agents/{id}/logs", GetLogs)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapDelete("/agents/{id}", DeleteAgent)
            .RequireAuthorization(RbacPolicies.Admin);
    }

    /// <summary>Page sizes the admin may request. Discrete, small — this is a manual, occasional
    /// pull, not a bulk export (docs/plans/agent-log-viewer.md §4.1).</summary>
    public static readonly IReadOnlySet<int> AllowedLogLines = new HashSet<int> { 200, 500, 1000 };

    public const int DefaultLogLines = 500;

    private static Task<IResult> ListAgents(
        NpgsqlDataSource db,
        string? status,
        string? zone,
        string? version,
        string? liveness,
        string? q,
        string? after,
        string? sort,
        string? dir,
        int limit = DefaultLimit,
        CancellationToken ct = default
    ) =>
        KeysetPage.RunAsync<AgentListItem>(
            after,
            limit,
            MaxLimit,
            cursorArity: 3,
            fetch: (parts, lim) =>
                QueryAsync(db, status, zone, version, liveness, q, parts?[0], parts?[1], parts?[2], lim, ct, sort, dir)
        );

    /// <summary>Shared query used by both the JSON endpoint and the Agents page model.</summary>
    public static async Task<(List<AgentListItem> Items, string? NextCursor)> QueryAsync(
        NpgsqlDataSource db,
        string? status,
        string? zone,
        string? version,
        string? liveness,
        string? q,
        string? afterSortKey,
        string? afterCreatedAt,
        string? afterAgentId,
        int limit,
        CancellationToken ct,
        string? sort = null,
        string? dir = null
    )
    {
        string sortKeyCol = sort is not null && SortExpressions.TryGetValue(sort, out string? expr)
            ? expr
            : SortExpressions[DefaultSort];
        bool descending = !string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);
        string cmp = descending ? "<" : ">";
        string direction = descending ? "DESC" : "ASC";
        string createdAtSortKey = SortExpressions[DefaultSort];

        // Liveness derived by agent_liveness() (see migration 0056_agent_liveness_settings.sql) —
        // the single definition shared by GetAgentHealthSummary.sql / GetAgentHealthList.sql.
        string sql = $"""
            WITH agents_with_liveness AS (
                SELECT
                    agent_id, hostname, status, last_heartbeat, zone, version, passive_discovery_mode,
                    os, arch, ip_address, device_id, created_at,
                    agent_liveness(last_heartbeat, heartbeat_interval_secs) AS liveness
                FROM agents
            )
            SELECT
                agent_id, hostname, status, last_heartbeat, zone, version, passive_discovery_mode,
                os, arch, ip_address, device_id, created_at, liveness,
                {sortKeyCol} AS sort_key, {createdAtSortKey} AS created_at_key
            FROM agents_with_liveness
            WHERE ($1::text IS NULL OR status = $1)
              AND ($2::text IS NULL OR zone = $2)
              AND ($3::text IS NULL OR version = $3)
              AND ($4::text IS NULL OR liveness = $4)
              AND ($5::text IS NULL OR hostname ILIKE '%' || $5 || '%' OR ip_address ILIKE '%' || $5 || '%')
              AND ($6::text IS NULL
                    OR (({sortKeyCol}, {createdAtSortKey}, agent_id::text) {cmp} ($6, $7, $8)))
            ORDER BY {sortKeyCol} {direction}, {createdAtSortKey} {direction}, agent_id::text {direction}
            LIMIT $9
            """;

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.Add(Param.Text(status));
        cmd.Parameters.Add(Param.Text(zone));
        cmd.Parameters.Add(Param.Text(version));
        cmd.Parameters.Add(Param.Text(liveness));
        cmd.Parameters.Add(Param.Text(string.IsNullOrWhiteSpace(q) ? null : q));
        cmd.Parameters.Add(Param.Text(afterSortKey));
        cmd.Parameters.Add(Param.Text(afterCreatedAt));
        cmd.Parameters.Add(Param.Text(afterAgentId));
        cmd.Parameters.Add(Param.Integer(limit + 1));

        List<AgentListItem> items = new();
        // sort_key[i] / created_at_key[i] are the SQL-computed sort expressions for items[i] —
        // used verbatim for the cursor so it always matches the keyset comparison (no C#-side
        // re-derivation to drift).
        List<string> sortKeys = new();
        List<string> createdAtKeys = new();
        List<string> agentIds = new();
        await using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                Guid agentId = reader.GetGuid(0);
                items.Add(
                    new AgentListItem(
                        AgentId: agentId.ToString(),
                        Hostname: reader.GetString(1),
                        Status: reader.GetString(2),
                        LastHeartbeat: reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3).UtcDateTime,
                        Zone: GetStr(reader, 4),
                        Version: GetStr(reader, 5),
                        PassiveDiscoveryMode: GetStr(reader, 6),
                        Os: GetStr(reader, 7),
                        Arch: GetStr(reader, 8),
                        IpAddress: GetStr(reader, 9),
                        DeviceId: reader.IsDBNull(10) ? null : reader.GetGuid(10),
                        CreatedAt: reader.GetFieldValue<DateTimeOffset>(11).UtcDateTime,
                        Liveness: reader.GetString(12)
                    )
                );
                sortKeys.Add(GetStr(reader, 13) ?? string.Empty);
                createdAtKeys.Add(GetStr(reader, 14) ?? string.Empty);
                agentIds.Add(agentId.ToString());
            }
        }

        string? nextCursor = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            sortKeys.RemoveAt(sortKeys.Count - 1);
            createdAtKeys.RemoveAt(createdAtKeys.Count - 1);
            agentIds.RemoveAt(agentIds.Count - 1);
            nextCursor = KeysetCursor.EncodeParts(sortKeys[^1], createdAtKeys[^1], agentIds[^1]);
        }

        return (items, nextCursor);
    }

    /// <summary>Distinct zones/versions across all agents, for the Fleet list's filter chips.</summary>
    public static async Task<(List<string> Zones, List<string> Versions)> GetFilterFacetsAsync(
        NpgsqlDataSource db,
        CancellationToken ct
    )
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        List<string> zones = [];
        await using (NpgsqlCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT zone FROM agents WHERE zone IS NOT NULL ORDER BY zone";
            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                zones.Add(reader.GetString(0));
            }
        }

        List<string> versions = [];
        await using (NpgsqlCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT version FROM agents WHERE version IS NOT NULL ORDER BY version";
            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                versions.Add(reader.GetString(0));
            }
        }

        return (zones, versions);
    }

    private static string? GetStr(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static async Task<IResult> ApproveAgent(
        string id,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(id, out Guid agentGuid))
        {
            return ApiError.InvalidId("Invalid agent id.");
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        AgentIdResult result = await conn.ApproveAgentAsync(agentGuid, ct).FirstOrDefaultAsync(ct);

        if (result == default)
        {
            return ApiError.NotFound("Agent not found.");
        }

        string actor = context.User.Identity?.Name ?? "admin";
        await audit.WriteAsync($"user:{actor}", "agent.approve", id, ct: ct);

        return Results.Ok(
            new
            {
                status = "approved",
            }
        );
    }

    /// <summary>
    /// Requests that the agent clear its local delta-tracker cache on its next heartbeat —
    /// needed when server-side data (e.g. a projection table) was reset independently of the
    /// agent, so its cache no longer reflects what the server actually has. Takes effect on
    /// the agent's next heartbeat; there is no synchronous confirmation it happened.
    /// </summary>
    private static async Task<IResult> ClearCache(
        string id,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(id, out Guid agentGuid))
        {
            return ApiError.InvalidId("Invalid agent id.");
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        AgentIdResult result = await conn.RequestClearTrackersAsync(agentGuid, ct).FirstOrDefaultAsync(ct);

        if (result == default)
        {
            return ApiError.NotFound("Agent not found.");
        }

        string actor = context.User.Identity?.Name ?? "admin";
        await audit.WriteAsync($"user:{actor}", "agent.clear_cache", id, ct: ct);

        return Results.Ok(
            new
            {
                status = "requested",
            }
        );
    }

    /// <summary>
    /// Requests that the agent upload a page of its recent console/journald log output on its
    /// next heartbeat, for on-demand viewing in the Fleet UI. Only a request timestamp + page
    /// size + paging token are persisted (agents columns) — the log text itself is never written
    /// to the database, only cached in memory once uploaded (docs/plans/agent-log-viewer.md).
    /// Takes effect on the agent's next heartbeat; there is no synchronous confirmation.
    /// </summary>
    private static async Task<IResult> RequestLogs(
        string id,
        RequestLogsRequest request,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(id, out Guid agentGuid))
        {
            return ApiError.InvalidId("Invalid agent id.");
        }

        int lines = request.Lines ?? DefaultLogLines;
        if (!AllowedLogLines.Contains(lines))
        {
            return ApiError.InvalidRequest("lines must be one of 200, 500, or 1000.");
        }

        string? before = string.IsNullOrWhiteSpace(request.Before) ? null : request.Before;

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        (Guid AgentId, DateTimeOffset? LogsRequestedAt) result =
            await conn.RequestLogsAsync(agentGuid, lines, before, ct).FirstOrDefaultAsync(ct);

        if (result == default)
        {
            return ApiError.NotFound("Agent not found.");
        }

        string actor = context.User.Identity?.Name ?? "admin";
        await audit.WriteAsync($"user:{actor}", "agent.request_logs", id, ct: ct);

        return Results.Ok(
            new
            {
                status = "requested",
                requested_at = result.LogsRequestedAt,
            }
        );
    }

    /// <summary>
    /// Returns the most recent log page this agent has uploaded, from the in-memory
    /// <see cref="AgentLogCache" /> (never the database). Returns <c>status: "pending"</c> when no
    /// page has arrived yet — the UI polls this after issuing a request until a page whose
    /// <c>requested_at</c> matches (or is newer than) the one it asked for shows up.
    /// </summary>
    private static IResult GetLogs(
        string id,
        AgentLogCache cache
    )
    {
        if (!Guid.TryParse(id, out Guid agentGuid))
        {
            return ApiError.InvalidId("Invalid agent id.");
        }

        if (!cache.TryGet(agentGuid, out AgentLogBundle? bundle) || bundle is null)
        {
            return Results.Ok(new { status = "pending" });
        }

        return Results.Ok(
            new
            {
                status = "ready",
                requested_at = bundle.RequestedAt,
                received_at = bundle.ReceivedAt,
                source = bundle.Source,
                truncated = bundle.Truncated,
                text = bundle.Text,
                next_before_token = bundle.NextBeforeToken,
            }
        );
    }

    private static async Task<IResult> DisableAgent(
        string id,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(id, out Guid agentGuid))
        {
            return ApiError.InvalidId("Invalid agent id.");
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        AgentIdResult result = await conn.DisableAgentAsync(agentGuid, ct).FirstOrDefaultAsync(ct);

        if (result == default)
        {
            return ApiError.NotFound("Agent not found.");
        }

        string actor = context.User.Identity?.Name ?? "admin";
        await audit.WriteAsync($"user:{actor}", "agent.disable", id, ct: ct);

        return Results.Ok(
            new
            {
                status = "disabled",
            }
        );
    }

    private static async Task<IResult> EnableAgent(
        string id,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(id, out Guid agentGuid))
        {
            return ApiError.InvalidId("Invalid agent id.");
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        AgentIdResult result = await conn.EnableAgentAsync(agentGuid, ct).FirstOrDefaultAsync(ct);

        if (result == default)
        {
            return ApiError.NotFound("Agent not found.");
        }

        string actor = context.User.Identity?.Name ?? "admin";
        await audit.WriteAsync($"user:{actor}", "agent.enable", id, ct: ct);

        return Results.Ok(
            new
            {
                status = "approved",
            }
        );
    }

    private static async Task<IResult> SetZone(
        string id,
        SetZoneRequest request,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(id, out Guid agentGuid))
        {
            return ApiError.InvalidId("Invalid agent id.");
        }

        string? zone = string.IsNullOrWhiteSpace(request.Zone) ? null : request.Zone.Trim();
        if (zone is { Length: > 100 })
        {
            return ApiError.InvalidRequest("Zone must be 100 characters or fewer.");
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        AgentIdResult result = await conn.UpdateAgentZoneAsync(agentGuid, zone, ct).FirstOrDefaultAsync(ct);

        if (result == default)
        {
            return ApiError.NotFound("Agent not found.");
        }

        string actor = context.User.Identity?.Name ?? "admin";
        await audit.WriteAsync($"user:{actor}", "agent.zone.set", id, ct: ct);

        return Results.Ok(
            new
            {
                zone,
            }
        );
    }

    private static async Task<IResult> DeleteAgent(
        string id,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(id, out Guid agentGuid))
        {
            return ApiError.InvalidId("Invalid agent id.");
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        AgentIdResult result = await conn.DeleteAgentAsync(agentGuid, ct).FirstOrDefaultAsync(ct);

        if (result == default)
        {
            return ApiError.NotFound("Agent not found.");
        }

        string actor = context.User.Identity?.Name ?? "admin";
        await audit.WriteAsync($"user:{actor}", "agent.delete", id, ct: ct);

        return Results.NoContent();
    }
}

public sealed record AgentListItem(
    string AgentId,
    string Hostname,
    string Status,
    DateTime? LastHeartbeat,
    string? Zone,
    string? Version,
    string? PassiveDiscoveryMode,
    string? Os,
    string? Arch,
    string? IpAddress,
    Guid? DeviceId,
    DateTime CreatedAt,
    string Liveness = "offline"
);

public sealed record SetZoneRequest(string? Zone);

/// <summary>Body for POST /agents/{id}/request-logs. <c>Lines</c> is the page size (200/500/1000,
/// default 500 when null); <c>Before</c> is the opaque paging token from a prior page's
/// <c>next_before_token</c>, or null for the newest page.</summary>
public sealed record RequestLogsRequest(int? Lines, string? Before);