using System.Text.Json;
using System.Text.RegularExpressions;

using JMW.Discovery.Core;
using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.FactViews;
using JMW.Discovery.Server.ManualFacts;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Admin;

/// <summary>
/// Admin CRUD for custom_field_definitions (docs/plans/user-provided.md). Per-device values are
/// set/cleared through <see cref="DeviceFactsApi" />, not here — this is schema, not data.
/// </summary>
public static partial class CustomFieldsApi
{
    private static readonly Regex SlugPattern = SlugRegex();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/custom-fields", ListDefinitions).RequireAuthorization(RbacPolicies.Admin);
        app.MapPost("/custom-fields", CreateDefinition).RequireAuthorization(RbacPolicies.Admin);
        app.MapDelete("/custom-fields/{id:guid}", DeleteDefinition).RequireAuthorization(RbacPolicies.Admin);
        app.MapGet("/fact-catalog", GetFactCatalog).RequireAuthorization(RbacPolicies.Admin);
    }

    private static async Task<IResult> ListDefinitions(NpgsqlDataSource db, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        List<CustomFieldDefinition> defs = await conn.ListCustomFieldDefinitionsAsync(ct)
            .Select(ToDefinition)
            .ToListAsync(ct);

        string[] attachableViews = FactViewLibrary.All
            .Where(v => v.Kind == FactViewKind.Properties)
            .Select(v => v.Title)
            .ToArray();

        return Results.Json(
            new
            {
                definitions = defs,
                attachable_views = attachableViews,
            },
            JsonOpts
        );
    }

    private static IResult GetFactCatalog() =>
        Results.Json(new { paths = ManualFactCatalog.EditablePaths }, JsonOpts);

    private static async Task<IResult> CreateDefinition(
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        CreateCustomFieldRequest? body;
        try
        {
            body = await context.Request.ReadFromJsonAsync<CreateCustomFieldRequest>(JsonOpts, ct);
        }
        catch
        {
            return ApiError.InvalidBody("Request body could not be parsed.");
        }

        if (body is null || string.IsNullOrWhiteSpace(body.Label))
        {
            return ApiError.InvalidBody("Label is required.");
        }

        if (string.IsNullOrWhiteSpace(body.Slug) || !SlugPattern.IsMatch(body.Slug))
        {
            return ApiError.InvalidRequest(
                "Slug must be lowercase kebab-case (letters, digits, single hyphens), 1-64 characters."
            );
        }

        string[] compiledTitles = FactViewLibrary.All.Select(v => v.Title).ToArray();

        string? targetViewTitle = null;
        string? targetViewGroup = null;

        if (body.IsNewView)
        {
            if (string.IsNullOrWhiteSpace(body.TargetViewTitle))
            {
                return ApiError.InvalidRequest("A new view needs a title.");
            }

            if (compiledTitles.Contains(body.TargetViewTitle, StringComparer.Ordinal))
            {
                return ApiError.Conflict($"'{body.TargetViewTitle}' is already an existing view title.");
            }

            if (!Enum.TryParse(body.TargetViewGroup, ignoreCase: true, out FactViewGroup _))
            {
                return ApiError.InvalidRequest(
                    $"target_view_group must be one of: {string.Join(", ", Enum.GetNames<FactViewGroup>())}."
                );
            }

            targetViewTitle = body.TargetViewTitle;
            targetViewGroup = body.TargetViewGroup;
        }
        else if (!string.IsNullOrWhiteSpace(body.TargetViewTitle))
        {
            bool isPropertiesView = FactViewLibrary.All.Any(v => v.Kind == FactViewKind.Properties
             && string.Equals(v.Title, body.TargetViewTitle, StringComparison.Ordinal)
            );
            if (!isPropertiesView)
            {
                return ApiError.InvalidRequest(
                    $"'{body.TargetViewTitle}' is not an existing Properties-kind view."
                );
            }

            targetViewTitle = body.TargetViewTitle;
        }

        string actor = "user:" + (context.User.Identity?.Name ?? "unknown");

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        CustomFieldDefinition? created = await conn
            .InsertCustomFieldDefinitionAsync(
                body.Label,
                body.Slug,
                targetViewTitle,
                targetViewGroup,
                body.IsNewView,
                actor,
                ct
            )
            .Select(ToDefinition)
            .FirstOrDefaultAsync(ct);

        if (created is null)
        {
            return ApiError.Conflict($"Slug '{body.Slug}' is already in use.");
        }

        await audit.WriteAsync(
            actor,
            "customfield.create",
            created.Slug,
            new
            {
                label = created.Label,
                target_view_title = created.TargetViewTitle,
                is_new_view = created.IsNewView,
            },
            ct
        );

        return Results.Json(created, JsonOpts);
    }

    private static async Task<IResult> DeleteDefinition(
        Guid id,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        (Guid Id, string Slug) deleted = await conn.DeleteCustomFieldDefinitionAsync(id, ct).FirstOrDefaultAsync(ct);
        if (deleted == default)
        {
            return ApiError.NotFound($"No custom field definition '{id}'.");
        }

        List<FactIdResult> removed = await conn
            .DeleteManualFactsByCustomSlugAsync(deleted.Slug, (short)FactSource.ManualEntry, ct)
            .ToListAsync(ct);

        string actor = "user:" + (context.User.Identity?.Name ?? "unknown");
        await audit.WriteAsync(
            actor,
            "customfield.delete",
            deleted.Slug,
            new
            {
                facts_removed = removed.Count,
            },
            ct
        );

        return Results.NoContent();
    }

    private static CustomFieldDefinition ToDefinition(
        (Guid Id, string Label, string Slug, string? TargetViewTitle, string? TargetViewGroup, bool IsNewView,
            DateTimeOffset CreatedAt, string CreatedBy) row
    ) => new(row.Id, row.Label, row.Slug, row.TargetViewTitle, row.TargetViewGroup, row.IsNewView, row.CreatedAt,
        row.CreatedBy
    );

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugRegex();

    private sealed record CreateCustomFieldRequest(
        string Label,
        string Slug,
        string? TargetViewTitle,
        string? TargetViewGroup,
        bool IsNewView
    );
}
