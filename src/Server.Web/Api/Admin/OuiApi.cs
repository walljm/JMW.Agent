using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Infrastructure;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Admin;

public static class OuiApi
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/oui/update", TriggerUpdate)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapGet("/oui/status", GetStatus)
            .RequireAuthorization(RbacPolicies.Admin);
    }

    private static async Task<IResult> TriggerUpdate(
        OuiUpdateService oui,
        AuditLog audit,
        HttpContext context,
        CancellationToken ct
    )
    {
        OuiUpdateResult? result = await oui.TriggerAsync(ct);

        if (result is null)
        {
            return ApiError.Problem(409, "update_in_progress", "An OUI database update is already running.");
        }

        string actor = "user:" + (context.User.Identity?.Name ?? "unknown");
        await audit.WriteAsync(
            actor,
            "oui.update",
            null,
            new
            {
                record_count = result.RecordCount,
                duration_ms = result.Duration.TotalMilliseconds,
                version_hash = result.VersionHash,
            },
            ct
        );

        return Results.Ok(result);
    }

    private static async Task<IResult> GetStatus(NpgsqlDataSource db, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        (string VersionHash, DateTimeOffset UpdatedAt, long RecordCount) meta =
            await conn.GetOuiMetaAsync(ct).FirstOrDefaultAsync(ct);

        if (meta == default)
        {
            return Results.Ok(
                new
                {
                    has_data = false,
                }
            );
        }

        return Results.Ok(
            new
            {
                has_data = true,
                version_hash = meta.VersionHash,
                updated_at = meta.UpdatedAt,
                record_count = meta.RecordCount,
            }
        );
    }
}