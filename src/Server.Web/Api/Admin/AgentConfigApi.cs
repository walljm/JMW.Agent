using System.Text.Json;

using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Admin;

public static class AgentConfigApi
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPut("/agents/{id}/config", UpdateIntervals)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapPost("/agents/{id}/collectors/{name}/toggle", ToggleCollector)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapPut("/agents/{id}/collectors/{name}/interval", SetCollectorInterval)
            .RequireAuthorization(RbacPolicies.Admin);
    }

    private static async Task<IResult> UpdateIntervals(
        string id,
        UpdateIntervalsRequest request,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(id, out Guid agentId))
        {
            return Error("invalid_id", "Invalid agent id.", 400);
        }

        if (request.HeartbeatIntervalSecs <= 0
         || request.DiscoveryIntervalSecs <= 0
         || request.InventoryIntervalSecs <= 0)
        {
            return Error("invalid_interval", "Intervals must be positive integers (seconds).", 422);
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        AgentIdResult result = await conn.UpdateAgentIntervalsAsync(
                agentId,
                request.HeartbeatIntervalSecs,
                request.DiscoveryIntervalSecs,
                request.InventoryIntervalSecs,
                ct
            )
            .FirstOrDefaultAsync(ct);

        if (result == default)
        {
            return Error("not_found", "Agent not found.", 404);
        }

        string actor = context.User.Identity?.Name ?? "admin";
        await audit.WriteAsync($"user:{actor}", "agent.config", id, ct: ct);

        return Results.Ok(
            new
            {
                heartbeat_interval_secs = request.HeartbeatIntervalSecs,
                discovery_interval_secs = request.DiscoveryIntervalSecs,
                inventory_interval_secs = request.InventoryIntervalSecs,
            }
        );
    }

    private static async Task<IResult> ToggleCollector(
        string id,
        string name,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(id, out Guid agentId))
        {
            return Error("invalid_id", "Invalid agent id.", 400);
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        CollectorSettingState current = await ReadCollectorAsync(conn, agentId, name, ct);
        if (!current.AgentExists)
        {
            return Error("not_found", "Agent not found.", 404);
        }

        // Flip enabled, preserving interval_secs. Write the complete per-collector
        // object — JSONB `||` is a shallow merge and would drop omitted fields.
        bool newEnabled = !current.Enabled;
        JsonElement patch = BuildPatch(name, newEnabled, current.IntervalSecs);

        await conn.UpdateCollectorConfigAsync(agentId, patch, ct).ExecuteAsync(ct);

        string actor = context.User.Identity?.Name ?? "admin";
        await audit.WriteAsync($"user:{actor}", "agent.collector.toggle", $"{id}/{name}", ct: ct);

        return Results.Ok(
            new
            {
                collector = name,
                enabled = newEnabled,
                interval_secs = current.IntervalSecs,
            }
        );
    }

    private static async Task<IResult> SetCollectorInterval(
        string id,
        string name,
        SetIntervalRequest request,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(id, out Guid agentId))
        {
            return Error("invalid_id", "Invalid agent id.", 400);
        }

        // null clears the override (inherit agent-level interval); a value must be positive.
        if (request.IntervalSecs is { } secs && secs <= 0)
        {
            return Error("invalid_interval", "Interval must be a positive integer (seconds) or null to inherit.", 422);
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        CollectorSettingState current = await ReadCollectorAsync(conn, agentId, name, ct);
        if (!current.AgentExists)
        {
            return Error("not_found", "Agent not found.", 404);
        }

        // Preserve enabled, set the interval override. Write the complete object.
        JsonElement patch = BuildPatch(name, current.Enabled, request.IntervalSecs);

        await conn.UpdateCollectorConfigAsync(agentId, patch, ct).ExecuteAsync(ct);

        string actor = context.User.Identity?.Name ?? "admin";
        await audit.WriteAsync($"user:{actor}", "agent.collector.interval", $"{id}/{name}", ct: ct);

        return Results.Ok(
            new
            {
                collector = name,
                enabled = current.Enabled,
                interval_secs = request.IntervalSecs,
            }
        );
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the current per-collector setting from the agent's collectors_config.
    /// A collector with no entry defaults to enabled=true, interval inherited (null).
    /// </summary>
    private static async Task<CollectorSettingState> ReadCollectorAsync(
        NpgsqlConnection conn,
        Guid agentId,
        string name,
        CancellationToken ct
    )
    {
        List<(Guid AgentId, string Hostname, string Status, int HeartbeatIntervalSecs, int DiscoveryIntervalSecs, int
            InventoryIntervalSecs, JsonElement CollectorsConfig, DateTimeOffset? ClearTrackersRequestedAt)>
            configRows = await conn.GetAgentConfigAsync(agentId, ct).ToListAsync(ct);
        if (configRows.Count == 0)
        {
            return new CollectorSettingState(AgentExists: false, Enabled: true, IntervalSecs: null);
        }

        JsonElement collectorsConfig = configRows[0].CollectorsConfig;
        bool enabled = true;
        int? intervalSecs = null;

        if (collectorsConfig.ValueKind == JsonValueKind.Object
         && collectorsConfig.TryGetProperty(name, out JsonElement entry)
         && entry.ValueKind == JsonValueKind.Object)
        {
            if (entry.TryGetProperty("enabled", out JsonElement enabledEl)
             && (enabledEl.ValueKind == JsonValueKind.True || enabledEl.ValueKind == JsonValueKind.False))
            {
                enabled = enabledEl.GetBoolean();
            }

            if (entry.TryGetProperty("interval_secs", out JsonElement intervalEl)
             && intervalEl.ValueKind == JsonValueKind.Number)
            {
                intervalSecs = intervalEl.GetInt32();
            }
        }

        return new CollectorSettingState(AgentExists: true, Enabled: enabled, IntervalSecs: intervalSecs);
    }

    private static JsonElement BuildPatch(string name, bool enabled, int? intervalSecs)
    {
        Dictionary<string, object?> setting = new()
        {
            ["enabled"] = enabled,
            ["interval_secs"] = intervalSecs,
        };
        Dictionary<string, object?> patch = new()
        {
            [name] = setting,
        };
        return JsonSerializer.SerializeToElement(patch);
    }

    private static IResult Error(string code, string message, int statusCode) =>
        ApiError.Problem(statusCode, code, message);

    private readonly record struct CollectorSettingState(bool AgentExists, bool Enabled, int? IntervalSecs);
}

public sealed record UpdateIntervalsRequest(
    int HeartbeatIntervalSecs,
    int DiscoveryIntervalSecs,
    int InventoryIntervalSecs
);

public sealed record SetIntervalRequest(int? IntervalSecs);