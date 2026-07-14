using System.Text.Json;

using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Infrastructure;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Admin;

public static class RetentionApi
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/retention/run", RunRetention)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapGet("/retention", ListPolicies)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapPatch("/retention/{tableName}", UpdatePolicy)
            .RequireAuthorization(RbacPolicies.Admin);
    }

    private static async Task<IResult> RunRetention(
        RetentionService retention,
        AuditLog audit,
        HttpContext context,
        CancellationToken ct
    )
    {
        RetentionRunResult? result = await retention.TriggerAsync(ct);

        if (result is null)
        {
            return ApiError.Problem(409, "sweep_in_progress", "A retention sweep is already running.");
        }

        string actor = "user:" + (context.User.Identity?.Name ?? "unknown");
        await audit.WriteAsync(
            actor,
            "retention.run",
            null,
            new
            {
                tables_pruned = result.Tables.Count,
                duration_ms = result.Duration.TotalMilliseconds,
            },
            ct
        );

        return Results.Ok(result);
    }

    private static async Task<IResult> ListPolicies(NpgsqlDataSource db, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        List<(string TableName, string Category, string TimeColumn, TimeSpan? StaleAfter, bool Enabled, string? Notes)>
            rows =
                await conn.ListAllRetentionPoliciesAsync(ct).ToListAsync(ct);

        object[] policies = rows.Select(r => (object)new
        {
            table_name = r.TableName,
            category = r.Category,
            time_column = r.TimeColumn,
            stale_after_secs = r.StaleAfter.HasValue ? (long?)(long)r.StaleAfter.Value.TotalSeconds : null,
            enabled = r.Enabled,
            notes = r.Notes,
        }
            )
            .ToArray();

        return Results.Json(
            new
            {
                policies,
            },
            JsonOpts
        );
    }

    private static async Task<IResult> UpdatePolicy(
        string tableName,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        UpdatePolicyRequest? body;
        try
        {
            body = await context.Request.ReadFromJsonAsync<UpdatePolicyRequest>(JsonOpts, ct);
        }
        catch
        {
            return ApiError.InvalidBody("Request body could not be parsed.");
        }

        if (body is null)
        {
            return ApiError.InvalidBody("Request body is required.");
        }

        TimeSpan? staleAfter = body.StaleAfterSecs.HasValue
            ? TimeSpan.FromSeconds(body.StaleAfterSecs.Value)
            : null;

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        TableNameResult result = await conn.UpdateRetentionPolicyAsync(tableName, staleAfter, body.Enabled, ct)
            .FirstOrDefaultAsync(ct);

        if (result.TableName is null)
        {
            return ApiError.NotFound($"No retention policy for table '{tableName}'.");
        }

        string actor = "user:" + (context.User.Identity?.Name ?? "unknown");
        await audit.WriteAsync(
            actor,
            "retention.update",
            tableName,
            new
            {
                stale_after_secs = body.StaleAfterSecs,
                enabled = body.Enabled,
            },
            ct
        );

        return Results.Ok(
            new
            {
                table_name = tableName,
                stale_after_secs = body.StaleAfterSecs,
                enabled = body.Enabled,
            }
        );
    }

    private sealed record UpdatePolicyRequest(long? StaleAfterSecs, bool Enabled);
}