using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Admin;

public static class DeviceLivenessSettingsApi
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/device-liveness-settings", GetSettings)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapPut("/device-liveness-settings", UpdateSettings)
            .RequireAuthorization(RbacPolicies.Admin);
    }

    private static async Task<IResult> GetSettings(NpgsqlDataSource db, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        DeviceLivenessSettingsResult settings = await conn.GetDeviceLivenessSettingsAsync(ct).FirstAsync(ct);

        return Results.Ok(new { window_hours = settings.WindowHours });
    }

    private static async Task<IResult> UpdateSettings(
        UpdateDeviceLivenessSettingsRequest request,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (request.WindowHours < 1)
        {
            return ApiError.InvalidRequest("window_hours must be at least 1.");
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        DeviceLivenessSettingsResult settings =
            await conn.UpdateDeviceLivenessSettingsAsync(request.WindowHours, ct).FirstAsync(ct);

        string actor = context.User.Identity?.Name ?? "admin";
        await audit.WriteAsync(
            $"user:{actor}",
            "settings.device_liveness.update",
            null,
            new { settings.WindowHours },
            ct
        );

        return Results.Ok(new { window_hours = settings.WindowHours });
    }
}

public sealed record UpdateDeviceLivenessSettingsRequest(int WindowHours);