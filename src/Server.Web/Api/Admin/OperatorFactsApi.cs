using System.Text.Json;

using JMW.Discovery.Core;
using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Data;
using JMW.Discovery.Server.ManualFacts;
using JMW.Discovery.Server.Queries;
using JMW.Discovery.Server.Reporting;

using Npgsql;

namespace JMW.Discovery.Server.Admin;

/// <summary>
/// Unified operator-authored device facts (docs/plans/architecture-operator-facts.md). Replaces the
/// two former mechanisms (Manual Fact Overrides + Custom Fields) with one: override any non-identity
/// catalog fact of any dimensionality, or create an arbitrary free-form fact, scoped to a device or a
/// child-collection instance. Every write is an ordinary <see cref="FactSource.ManualEntry" /> row in
/// facts_history (source_name = the acting operator), so audit/revert semantics and cross-device
/// projection are inherited unchanged. Every delete is source-scoped, so it can only ever remove rows
/// this feature itself wrote — never a collector's history.
///
/// The write gate rejects, in order: identity-bearing dimensions, identity-bearing consts (NFR-8),
/// and Derived/Metric paths (non-authorable) — see <see cref="OperatorFactCatalog" />. Near-miss
/// detection (<see cref="NearMissMatcher" />) is advisory only.
/// </summary>
public static class OperatorFactsApi
{
    private const short ManualEntrySource = (short)FactSource.ManualEntry;

    private const string BoundsMessage =
        "Fact IDs are limited to 512 characters and 32 path segments.";

    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/devices/{id:guid}/operator-facts", SetFact);
        app.MapDelete("/devices/{id:guid}/operator-facts", RevertFact);
        app.MapGet("/devices/{id:guid}/collection-keys", GetCollectionKeys);
        app.MapPut("/operator-facts/metadata", SetMetadata);
        app.MapGet("/operator-facts", ListFleetFactsForPath);
        app.MapGet("/operator-facts/paths", ListFleetPaths);
    }

    // ── Write: set / override / create ────────────────────────────────────────

    private static async Task<IResult> SetFact(
        Guid id,
        HttpContext context,
        NpgsqlDataSource db,
        FactIngestPipeline ingest,
        AuditLog audit,
        CancellationToken ct
    )
    {
        SetOperatorFactRequest? body = await ReadBodyAsync<SetOperatorFactRequest>(context, ct);
        if (body is null || string.IsNullOrEmpty(body.AttributePath) || string.IsNullOrEmpty(body.Value))
        {
            return ApiError.InvalidBody("attribute_path and value are required.");
        }

        string template = body.AttributePath;
        string[] keys = body.Keys ?? [];

        if (StructuralError(template, keys.Length) is { } structuralError)
        {
            return structuralError;
        }

        switch (OperatorFactCatalog.Classify(template))
        {
            case OperatorFactCatalog.PathClass.IdentityProtectedDimension:
                return ApiError.Problem(
                    422,
                    "identity_protected_dimension",
                    $"'{template}' lives under a dimension whose key is a device fingerprint and can never "
                  + "be operator-authored — it would risk corrupting device identity resolution."
                );
            case OperatorFactCatalog.PathClass.IdentityProtected:
                return ApiError.Problem(
                    422,
                    "identity_protected",
                    $"'{template}' feeds device-identity/fingerprint resolution and is protected from override."
                );
            case OperatorFactCatalog.PathClass.NotAuthorable:
                return ApiError.Problem(
                    422,
                    "not_authorable",
                    $"'{template}' is a derived or metric fact that the system recomputes — it cannot be "
                  + "authored by hand."
                );
            case OperatorFactCatalog.PathClass.Arbitrary when !body.ConfirmArbitrary:
                string? suggestion = NearMissMatcher.FindSuggestion(template, OperatorFactCatalog.AllCatalogPaths);
                if (suggestion is not null && !string.Equals(suggestion, template, StringComparison.Ordinal))
                {
                    return Results.Problem(
                        detail: $"This looks similar to catalog fact '{suggestion}'. Did you mean to override that "
                              + "instead of creating a new arbitrary fact?",
                        statusCode: 409,
                        extensions: new Dictionary<string, object?>
                        {
                            ["code"] = "near_miss",
                            ["suggestion"] = suggestion,
                        }
                    );
                }

                break;
        }

        string[] allKeys = [id.ToString(), .. keys];
        Fact fact;
        try
        {
            fact = Fact.Create(template, allKeys, body.Value) with
            {
                Source = FactSource.ManualEntry,
                SourceName = Actor(context),
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return ApiError.InvalidRequest(BoundsMessage);
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        if (!body.ConfirmOverwrite)
        {
            InUseResult existing = await conn
                .GetOperatorFactExistsAsync(fact.Id, ManualEntrySource, ct)
                .FirstOrDefaultAsync(ct);
            if (existing.InUse == true)
            {
                return Results.Problem(
                    detail: "An operator-authored value already exists for this fact. Confirm to overwrite it.",
                    statusCode: 409,
                    extensions: new Dictionary<string, object?> { ["code"] = "overwrite" }
                );
            }
        }

        await ingest.IngestAsync([fact], ct);

        if (body.Label is not null || body.Description is not null)
        {
            await conn
                .UpsertFactPathMetadataAsync(template, fact.KeyValuesJson, body.Label, body.Description, Actor(context),
                    showInReports: null, ct)
                .FirstOrDefaultAsync(ct);
        }

        await audit.WriteAsync(Actor(context), "device.operator-fact.set", id.ToString(),
            new { path = template, keys, value = body.Value }, ct
        );

        return Results.Json(new { path = template, keys, value = body.Value }, JsonOpts);
    }

    // ── Revert / clear ────────────────────────────────────────────────────────

    private static async Task<IResult> RevertFact(
        Guid id,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        RevertOperatorFactRequest? body = await ReadBodyAsync<RevertOperatorFactRequest>(context, ct);
        if (body is null || string.IsNullOrEmpty(body.AttributePath))
        {
            return ApiError.InvalidBody("attribute_path is required.");
        }

        string[] allKeys = [id.ToString(), .. body.Keys ?? []];
        string factId;
        try
        {
            factId = Fact.Create(body.AttributePath, allKeys, FactValue.Null).Id;
        }
        catch (ArgumentException)
        {
            // Wrong key count or oversized path — same clean 400 as the set path.
            return ApiError.InvalidRequest(
                $"'{body.AttributePath}' with the supplied keys is not a valid fact path.");
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        List<FactIdResult> removed = await conn
            .DeleteManualFactByIdAsync(factId, ManualEntrySource, ct)
            .ToListAsync(ct);

        await audit.WriteAsync(Actor(context), "device.operator-fact.revert", id.ToString(),
            new { path = body.AttributePath, keys = body.Keys ?? [], reverted = removed.Count > 0 }, ct
        );

        return removed.Count > 0
            ? Results.NoContent()
            : ApiError.NotFound("No operator-authored value set for that path on this device.");
    }

    // ── Child-collection key suggestions (REQ-010) ────────────────────────────

    private static async Task<IResult> GetCollectionKeys(
        Guid id,
        string? dim,
        NpgsqlDataSource db,
        CancellationToken ct
    )
    {
        if (string.IsNullOrEmpty(dim))
        {
            return ApiError.InvalidRequest("A dim query parameter is required.");
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        List<string> keys = await conn.GetDeviceCollectionKeysAsync(id.ToString(), dim, ct)
            .Where(k => !string.IsNullOrEmpty(k.CollectionKey))
            .Select(k => k.CollectionKey!)
            .ToListAsync(ct);

        return Results.Json(new { keys }, JsonOpts);
    }

    // ── Path-level label/description metadata (UX §6.5.3) ──────────────────────

    private static async Task<IResult> SetMetadata(
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        SetMetadataRequest? body = await ReadBodyAsync<SetMetadataRequest>(context, ct);
        if (body is null || string.IsNullOrEmpty(body.AttributePath))
        {
            return ApiError.InvalidBody("attribute_path is required.");
        }

        // Build the canonical key_values from a placeholder device + the non-device keys; the upsert
        // strips the Device entry, leaving the device-independent metadata key.
        string[] allKeys = ["placeholder", .. body.Keys ?? []];
        string keyValuesJson;
        try
        {
            keyValuesJson = Fact.Create(body.AttributePath, allKeys, FactValue.Null).KeyValuesJson;
        }
        catch (ArgumentException)
        {
            return ApiError.InvalidRequest(
                $"'{body.AttributePath}' with the supplied keys is not a valid fact path.");
        }

        // The report-column flag only makes sense for a device-scoped arbitrary fact: overrides
        // already have real report columns, and a child-collection fact has no single per-device
        // value to show.
        if (body.ShowInReports == true)
        {
            if (OperatorFactCatalog.Classify(body.AttributePath) != OperatorFactCatalog.PathClass.Arbitrary)
            {
                return ApiError.Problem(
                    422,
                    "not_report_flaggable",
                    $"'{body.AttributePath}' is a catalog fact — only arbitrary operator facts can be "
                  + "flagged as report columns."
                );
            }

            if ((body.Keys ?? []).Length > 0)
            {
                return ApiError.Problem(
                    422,
                    "not_report_flaggable",
                    "Only device-scoped facts can be shown as report columns — a child-collection fact "
                  + "has no single per-device value."
                );
            }
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await conn
            .UpsertFactPathMetadataAsync(body.AttributePath, keyValuesJson, body.Label, body.Description, Actor(context),
                body.ShowInReports, ct)
            .FirstOrDefaultAsync(ct);

        await audit.WriteAsync(Actor(context), "operator-fact.metadata.set", body.AttributePath,
            new { path = body.AttributePath, keys = body.Keys ?? [], label = body.Label, show_in_reports = body.ShowInReports }, ct
        );

        return Results.Json(
            new
            {
                path = body.AttributePath,
                label = body.Label,
                description = body.Description,
                show_in_reports = body.ShowInReports,
            },
            JsonOpts);
    }

    // ── Fleet-wide browse (REQ-006) ───────────────────────────────────────────

    private static Task<IResult> ListFleetFactsForPath(
        NpgsqlDataSource db,
        string? path,
        string? after,
        int limit = DefaultLimit,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrEmpty(path))
        {
            return Task.FromResult(ApiError.InvalidRequest("A path query parameter is required."));
        }

        return KeysetPage.RunAsync<FleetFactItem>(
            after,
            limit,
            MaxLimit,
            cursorArity: 2,
            fetch: (parts, lim) => QueryFleetFactsAsync(db, path, parts?[0], parts?[1], lim, ct)
        );
    }

    private static Task<IResult> ListFleetPaths(
        NpgsqlDataSource db,
        string? after,
        int limit = DefaultLimit,
        CancellationToken ct = default
    ) =>
        KeysetPage.RunAsync<FleetPathItem>(
            after,
            limit,
            MaxLimit,
            cursorArity: 2,
            fetch: (parts, lim) => QueryFleetPathsAsync(db, parts?[0], parts?[1], lim, ct)
        );

    /// <summary>Shared keyset query for the fleet path-search endpoint; public for integration testing.</summary>
    public static async Task<(List<FleetFactItem> Items, string? NextCursor)> QueryFleetFactsAsync(
        NpgsqlDataSource db,
        string path,
        string? afterDevice,
        string? afterId,
        int limit,
        CancellationToken ct
    )
    {
        const string sql = """
            WITH latest AS (
                SELECT DISTINCT ON (h.id)
                    h.id,
                    h.attribute_path,
                    h.key_values,
                    COALESCE(h.value_str, h.value_long::text, h.value_double::text) AS value,
                    h.source_name,
                    h.collected_at
                FROM facts_history h
                WHERE h.attribute_path = $1 AND h.source = 2
                ORDER BY h.id, h.collected_at DESC
            )
            SELECT
                l.id,
                l.key_values::text AS key_values,
                l.key_values->>'Device' AS device_id,
                l.value,
                l.source_name,
                l.collected_at,
                m.label,
                COALESCE(sys.friendly_name, sys.hostname) AS device_label
            FROM latest l
                LEFT JOIN fact_path_metadata m
                    ON m.attribute_path = l.attribute_path AND m.key_values = (l.key_values - 'Device')
                LEFT JOIN proj_systems sys ON sys.device = (l.key_values->>'Device')
            WHERE ($2::text IS NULL OR ((l.key_values->>'Device'), l.id) > ($2, $3))
            ORDER BY (l.key_values->>'Device'), l.id
            LIMIT $4
            """;

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.Add(Param.Text(path));
        cmd.Parameters.Add(Param.Text(afterDevice));
        cmd.Parameters.Add(Param.Text(afterId));
        cmd.Parameters.Add(Param.Integer(limit + 1));

        List<FleetFactItem> items = new();
        List<(string Device, string Id)> tiebreakers = new();
        await using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                string factId = reader.GetString(0);
                string? keyValues = GetStr(reader, 1);
                string deviceId = GetStr(reader, 2) ?? string.Empty;
                items.Add(
                    new FleetFactItem(
                        DeviceId: deviceId,
                        DeviceLabel: GetStr(reader, 7),
                        Scope: FormatScope(keyValues),
                        Keys: ExtractKeys(keyValues),
                        Value: GetStr(reader, 3),
                        SourceName: GetStr(reader, 4),
                        CollectedAt: reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                        Label: GetStr(reader, 6)
                    )
                );
                tiebreakers.Add((deviceId, factId));
            }
        }

        string? nextCursor = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            tiebreakers.RemoveAt(tiebreakers.Count - 1);
            (string Device, string Id) last = tiebreakers[^1];
            nextCursor = KeysetCursor.EncodeParts(last.Device, last.Id);
        }

        return (items, nextCursor);
    }

    /// <summary>Shared keyset query for the fleet browse-all-paths endpoint; public for integration testing.</summary>
    public static async Task<(List<FleetPathItem> Items, string? NextCursor)> QueryFleetPathsAsync(
        NpgsqlDataSource db,
        string? afterPath,
        string? afterMetaKey,
        int limit,
        CancellationToken ct
    )
    {
        const string sql = """
            WITH sigs AS (
                SELECT
                    h.attribute_path,
                    (h.key_values - 'Device') AS meta_key,
                    h.key_values->>'Device' AS device_id
                FROM facts_history h
                WHERE h.source = 2
            )
            SELECT
                s.attribute_path,
                s.meta_key::text AS meta_key,
                count(DISTINCT s.device_id) AS device_count,
                m.label,
                COALESCE(m.show_in_reports, FALSE) AS show_in_reports
            FROM sigs s
                LEFT JOIN fact_path_metadata m
                    ON m.attribute_path = s.attribute_path AND m.key_values = s.meta_key
            WHERE ($1::text IS NULL OR (s.attribute_path, s.meta_key::text) > ($1, $2))
            GROUP BY s.attribute_path, s.meta_key, m.label, m.show_in_reports
            ORDER BY s.attribute_path, s.meta_key::text
            LIMIT $3
            """;

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.Add(Param.Text(afterPath));
        cmd.Parameters.Add(Param.Text(afterMetaKey));
        cmd.Parameters.Add(Param.Integer(limit + 1));

        List<FleetPathItem> items = new();
        List<(string Path, string MetaKey)> tiebreakers = new();
        await using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                string attributePath = reader.GetString(0);
                string metaKey = reader.GetString(1);
                string scope = FormatScope(metaKey);
                items.Add(
                    new FleetPathItem(
                        AttributePath: attributePath,
                        Scope: scope,
                        DeviceCount: reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                        Label: GetStr(reader, 3),
                        ShowInReports: !reader.IsDBNull(4) && reader.GetBoolean(4),
                        CanShowInReports:
                            string.Equals(scope, "Device", StringComparison.Ordinal)
                         && OperatorFactCatalog.Classify(attributePath) == OperatorFactCatalog.PathClass.Arbitrary
                    )
                );
                tiebreakers.Add((attributePath, metaKey));
            }
        }

        string? nextCursor = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            tiebreakers.RemoveAt(tiebreakers.Count - 1);
            (string Path, string MetaKey) last = tiebreakers[^1];
            nextCursor = KeysetCursor.EncodeParts(last.Path, last.MetaKey);
        }

        return (items, nextCursor);
    }

    // ── Structural validation shared by set/revert ────────────────────────────

    /// <summary>
    /// Returns a 400 result if the template is malformed, or null when it is structurally valid: the
    /// path must parse within bounds, start with a <c>Device[]</c> list segment, and expect exactly
    /// <paramref name="suppliedKeyCount" /> non-device keys (one per list dimension beyond the device).
    /// </summary>
    private static IResult? StructuralError(string template, int suppliedKeyCount)
    {
        FactSegment[] segments;
        try
        {
            segments = FactSegment.ParsePath(template);
        }
        catch (ArgumentOutOfRangeException)
        {
            return ApiError.InvalidRequest(BoundsMessage);
        }

        if (segments.Length == 0 || !segments[0].IsList
         || !string.Equals(segments[0].Name, "Device", StringComparison.Ordinal))
        {
            return ApiError.InvalidRequest("The fact path must start with a Device[] list segment.");
        }

        int expectedKeys = segments.Count(s => s.IsList) - 1;
        if (suppliedKeyCount != expectedKeys)
        {
            return ApiError.InvalidRequest(
                $"This fact path needs {expectedKeys} key(s) beyond the device, but {suppliedKeyCount} "
              + "were supplied.");
        }

        return null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Actor(HttpContext context) =>
        "user:" + (context.User.Identity?.Name ?? "unknown");

    private static async Task<T?> ReadBodyAsync<T>(HttpContext context, CancellationToken ct)
        where T : class
    {
        try
        {
            return await context.Request.ReadFromJsonAsync<T>(JsonOpts, ct);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetStr(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>
    /// Compact display of a fact's non-device scope from its key_values JSON: "Device" for a
    /// device-only fact, else the child-collection keys joined as "Interface[aa:bb:...]".
    /// </summary>
    private static string FormatScope(string? keyValuesJson)
    {
        if (string.IsNullOrEmpty(keyValuesJson))
        {
            return "Device";
        }

        using JsonDocument doc = JsonDocument.Parse(keyValuesJson);
        List<string> parts = new();
        foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            if (string.Equals(prop.Name, "Device", StringComparison.Ordinal))
            {
                continue;
            }

            parts.Add($"{prop.Name}[{prop.Value.GetString()}]");
        }

        return parts.Count == 0 ? "Device" : string.Join(".", parts);
    }

    /// <summary>The non-device key values (path order) — the array a revert call needs.</summary>
    private static string[] ExtractKeys(string? keyValuesJson)
    {
        if (string.IsNullOrEmpty(keyValuesJson))
        {
            return [];
        }

        using JsonDocument doc = JsonDocument.Parse(keyValuesJson);
        List<string> keys = new();
        foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            if (!string.Equals(prop.Name, "Device", StringComparison.Ordinal))
            {
                keys.Add(prop.Value.GetString() ?? string.Empty);
            }
        }

        return [.. keys];
    }

    private sealed record SetOperatorFactRequest(
        string AttributePath,
        string[]? Keys,
        string Value,
        string? Label,
        string? Description,
        bool ConfirmArbitrary,
        bool ConfirmOverwrite
    );

    private sealed record RevertOperatorFactRequest(string AttributePath, string[]? Keys);

    private sealed record SetMetadataRequest(
        string AttributePath,
        string[]? Keys,
        string? Label,
        string? Description,
        bool? ShowInReports
    );

    public sealed record FleetFactItem(
        string DeviceId,
        string? DeviceLabel,
        string Scope,
        string[] Keys,
        string? Value,
        string? Label,
        string? SourceName,
        DateTime? CollectedAt
    );

    public sealed record FleetPathItem(
        string AttributePath,
        string Scope,
        long DeviceCount,
        string? Label,
        bool ShowInReports,
        bool CanShowInReports
    );
}
