using System.Globalization;

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
    /// Columns the agent list may be sorted by. Both are plain columns on <c>agents</c> — "status"
    /// is covered by <c>agents_status_sort_idx (status, created_at)</c>, "created_at" by the
    /// existing <c>agents_keyset_idx (created_at DESC, agent_id)</c>. One cursor shape —
    /// (sort_key, created_at, agent_id) — covers every sort; created_at is always the tiebreaker
    /// (kept as a real timestamptz throughout, never formatted to text, so the comparison stays
    /// index-driven regardless of which column is primary). The two sorts are separate generated
    /// commands (the cursor's primary element is timestamptz vs text) — this allowlist is the
    /// union of their generated [SortableBy] sets so the UI cannot drift from the validated SQL.
    /// </summary>
    public static readonly IReadOnlySet<string> SortableColumns =
        AgentQueries.ListAgentsByCreatedAtAsyncSortKeys
            .Concat(AgentQueries.ListAgentsByStatusAsyncSortKeys)
            .ToHashSet(StringComparer.Ordinal);

    public const string DefaultSort = "created_at";
    public const string DefaultDir = "desc";

    /// <summary>Fixed liveness values — matches the CASE branches computed by agent_liveness()
    /// (see ListAgentsByCreatedAt.sql / GetAgentHealthSummary.sql / GetAgentHealthList.sql).</summary>
    public static readonly IReadOnlyList<string> LivenessValues = ["online", "stale", "offline"];

    public static bool IsSortable(string? sort) => sort is not null && SortableColumns.Contains(sort);

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
        // This list defaults to descending (newest agents first) — anything but an explicit
        // "asc" flips to "desc" before reaching the generated command, whose own convention is
        // ascending unless dir equals "desc".
        bool descending = !string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);
        string normalizedDir = descending ? "desc" : "asc";
        bool sortingByCreatedAt = !string.Equals(sort, "status", StringComparison.Ordinal);

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        // The two sorts are separate generated commands because the cursor's primary element is
        // a real timestamptz for created_at (never formatted to text, so the comparison stays
        // index-driven) but text for status. Same row shape, so either feeds the loop below.
        IAsyncEnumerable<(Guid AgentId, string Hostname, string Status, DateTimeOffset? LastHeartbeat, string? Zone,
            string? Version, string? PassiveDiscoveryMode, string? Os, string? Arch, string? IpAddress,
            Guid? DeviceId, DateTimeOffset CreatedAt, string? Liveness)> rows = sortingByCreatedAt
            ? conn.ListAgentsByCreatedAtAsync(
                status,
                zone,
                version,
                liveness,
                string.IsNullOrWhiteSpace(q) ? null : q,
                ParseCursorTimestamp(afterSortKey),
                ParseCursorTimestamp(afterCreatedAt),
                afterAgentId,
                limit + 1,
                "created_at",
                normalizedDir,
                ct
            )
            : conn.ListAgentsByStatusAsync(
                status,
                zone,
                version,
                liveness,
                string.IsNullOrWhiteSpace(q) ? null : q,
                afterSortKey,
                ParseCursorTimestamp(afterCreatedAt),
                afterAgentId,
                limit + 1,
                "status",
                normalizedDir,
                ct
            );

        List<AgentListItem> items = new();
        // sort_key/created_at cursor components are read back off the same status/created_at
        // values used to build items[i] — created_at's ISO round-trip ("O") is an exact,
        // invertible format of the identical value used in the WHERE/ORDER BY comparison, not a
        // re-derivation, so the cursor still always matches the keyset comparison.
        List<string> sortKeys = new();
        List<string> createdAtKeys = new();
        List<string> agentIds = new();
        await foreach ((Guid agentId, string hostname, string rowStatus, DateTimeOffset? lastHeartbeat,
            string? rowZone, string? rowVersion, string? passiveDiscoveryMode, string? os, string? arch,
            string? ipAddress, Guid? deviceId, DateTimeOffset createdAt, string? rowLiveness) in rows)
        {
            items.Add(
                new AgentListItem(
                    AgentId: agentId.ToString(),
                    Hostname: hostname,
                    Status: rowStatus,
                    LastHeartbeat: lastHeartbeat?.UtcDateTime,
                    Zone: rowZone,
                    Version: rowVersion,
                    PassiveDiscoveryMode: passiveDiscoveryMode,
                    Os: os,
                    Arch: arch,
                    IpAddress: ipAddress,
                    DeviceId: deviceId,
                    CreatedAt: createdAt.UtcDateTime,
                    Liveness: rowLiveness ?? "offline"
                )
            );
            sortKeys.Add(sortingByCreatedAt ? createdAt.ToString("O") : rowStatus);
            createdAtKeys.Add(createdAt.ToString("O"));
            agentIds.Add(agentId.ToString());
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

    /// <summary>Parses a cursor-carried timestamp (this class's own <see cref="DateTimeOffset.ToString(string)" />
    /// "O" output) back into a typed value for a native timestamptz comparison. Returns null for
    /// a missing/malformed cursor part (treated as "no cursor" by the $8 IS NULL guard).</summary>
    private static DateTimeOffset? ParseCursorTimestamp(string? value) =>
        value is not null
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed)
            ? parsed
            : null;

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