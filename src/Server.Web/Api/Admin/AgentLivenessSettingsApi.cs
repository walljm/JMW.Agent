using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Admin;

public static class AgentLivenessSettingsApi
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/agent-liveness-settings", GetSettings)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapPut("/agent-liveness-settings", UpdateSettings)
            .RequireAuthorization(RbacPolicies.Admin);
    }

    private static async Task<IResult> GetSettings(NpgsqlDataSource db, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        (int OnlineMultiplier, int OfflineCeilingSecs) settings =
            await conn.GetAgentLivenessSettingsAsync(ct).FirstAsync(ct);

        return Results.Ok(
            new
            {
                online_multiplier = settings.OnlineMultiplier,
                offline_ceiling_secs = settings.OfflineCeilingSecs,
            }
        );
    }

    private static async Task<IResult> UpdateSettings(
        UpdateAgentLivenessSettingsRequest request,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (request.OnlineMultiplier < 1)
        {
            return ApiError.InvalidRequest("online_multiplier must be at least 1.");
        }

        if (request.OfflineCeilingSecs < 1)
        {
            return ApiError.InvalidRequest("offline_ceiling_secs must be at least 1.");
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        (int OnlineMultiplier, int OfflineCeilingSecs) settings = await conn
            .UpdateAgentLivenessSettingsAsync(request.OnlineMultiplier, request.OfflineCeilingSecs, ct)
            .FirstAsync(ct);

        string actor = context.User.Identity?.Name ?? "admin";
        await audit.WriteAsync(
            $"user:{actor}",
            "settings.agent_liveness.update",
            null,
            new { settings.OnlineMultiplier, settings.OfflineCeilingSecs },
            ct
        );

        return Results.Ok(
            new
            {
                online_multiplier = settings.OnlineMultiplier,
                offline_ceiling_secs = settings.OfflineCeilingSecs,
            }
        );
    }
}

public sealed record UpdateAgentLivenessSettingsRequest(int OnlineMultiplier, int OfflineCeilingSecs);