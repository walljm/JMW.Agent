using System.Text.Json;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;
using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.ManualFacts;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Admin;

/// <summary>
/// Operator-authored device facts (docs/plans/user-provided.md): filling in/overriding an
/// existing <see cref="FactPaths" /> field, and setting/clearing a device's value for a
/// custom_field_definitions field. Every write goes through the same
/// <see cref="FactIngestPipeline" /> a collector's facts do (so cross-device projections stay in
/// sync), tagged <see cref="FactSource.ManualEntry" /> with the acting user as SourceName. Every
/// delete is scoped to that source, so it can only ever remove rows this feature itself wrote.
/// </summary>
public static class DeviceFactsApi
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/devices/{id:guid}/facts", SetFact).RequireAuthorization(RbacPolicies.Admin);
        app.MapDelete("/devices/{id:guid}/facts", RevertFact).RequireAuthorization(RbacPolicies.Admin);
        app.MapPost("/devices/{id:guid}/custom-fields/{slug}", SetCustomFieldValue)
            .RequireAuthorization(RbacPolicies.Admin);
        app.MapDelete("/devices/{id:guid}/custom-fields/{slug}", ClearCustomFieldValue)
            .RequireAuthorization(RbacPolicies.Admin);
    }

    private static async Task<IResult> SetFact(
        Guid id,
        HttpContext context,
        FactIngestPipeline ingest,
        AuditLog audit,
        CancellationToken ct
    )
    {
        SetFactRequest? body;
        try
        {
            body = await context.Request.ReadFromJsonAsync<SetFactRequest>(JsonOpts, ct);
        }
        catch
        {
            return ApiError.InvalidBody("Request body could not be parsed.");
        }

        if (body is null || string.IsNullOrEmpty(body.Path) || string.IsNullOrEmpty(body.Value))
        {
            return ApiError.InvalidBody("path and value are required.");
        }

        if (!ManualFactCatalog.EditablePaths.Contains(body.Path, StringComparer.Ordinal))
        {
            return ApiError.InvalidRequest($"'{body.Path}' is not an editable fact path.");
        }

        string actor = "user:" + (context.User.Identity?.Name ?? "unknown");
        Fact fact = Fact.Create(body.Path, [id.ToString()], body.Value) with
        {
            Source = FactSource.ManualEntry,
            SourceName = actor,
        };

        await ingest.IngestAsync([fact], ct);
        await audit.WriteAsync(actor, "device.fact.set", id.ToString(), new { path = body.Path, value = body.Value },
            ct
        );

        return Results.Ok(new { path = body.Path, value = body.Value });
    }

    private static async Task<IResult> RevertFact(
        Guid id,
        string path,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (!ManualFactCatalog.EditablePaths.Contains(path, StringComparer.Ordinal))
        {
            return ApiError.InvalidRequest($"'{path}' is not an editable fact path.");
        }

        string factId = Fact.Create(path, [id.ToString()], FactValue.Null).Id;

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        List<FactIdResult> removed = await conn
            .DeleteManualFactByIdAsync(factId, (short)FactSource.ManualEntry, ct)
            .ToListAsync(ct);

        string actor = "user:" + (context.User.Identity?.Name ?? "unknown");
        await audit.WriteAsync(actor, "device.fact.revert", id.ToString(), new { path, reverted = removed.Count > 0 },
            ct
        );

        return removed.Count > 0
            ? Results.NoContent()
            : ApiError.NotFound("No manual value set for that path on this device.");
    }

    private static async Task<IResult> SetCustomFieldValue(
        Guid id,
        string slug,
        HttpContext context,
        NpgsqlDataSource db,
        FactIngestPipeline ingest,
        AuditLog audit,
        CancellationToken ct
    )
    {
        SetCustomFieldValueRequest? body;
        try
        {
            body = await context.Request.ReadFromJsonAsync<SetCustomFieldValueRequest>(JsonOpts, ct);
        }
        catch
        {
            return ApiError.InvalidBody("Request body could not be parsed.");
        }

        if (body is null || string.IsNullOrEmpty(body.Value))
        {
            return ApiError.InvalidBody("value is required.");
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        bool exists = await conn.GetCustomFieldDefinitionBySlugAsync(slug, ct).AnyAsync(ct);
        if (!exists)
        {
            return ApiError.NotFound($"No custom field definition with slug '{slug}'.");
        }

        string actor = "user:" + (context.User.Identity?.Name ?? "unknown");
        Fact fact = Fact.Create(FactPaths.CustomFieldValue, [id.ToString(), slug], body.Value) with
        {
            Source = FactSource.ManualEntry,
            SourceName = actor,
        };

        await ingest.IngestAsync([fact], ct);
        await audit.WriteAsync(actor, "device.customfield.set", id.ToString(), new { slug, value = body.Value }, ct);

        return Results.Ok(new { slug, value = body.Value });
    }

    private static async Task<IResult> ClearCustomFieldValue(
        Guid id,
        string slug,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        string factId = Fact.Create(FactPaths.CustomFieldValue, [id.ToString(), slug], FactValue.Null).Id;

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        List<FactIdResult> removed = await conn
            .DeleteManualFactByIdAsync(factId, (short)FactSource.ManualEntry, ct)
            .ToListAsync(ct);

        string actor = "user:" + (context.User.Identity?.Name ?? "unknown");
        await audit.WriteAsync(actor, "device.customfield.clear", id.ToString(),
            new { slug, cleared = removed.Count > 0 }, ct
        );

        return removed.Count > 0
            ? Results.NoContent()
            : ApiError.NotFound("No value set for that custom field on this device.");
    }

    private sealed record SetFactRequest(string Path, string Value);

    private sealed record SetCustomFieldValueRequest(string Value);
}