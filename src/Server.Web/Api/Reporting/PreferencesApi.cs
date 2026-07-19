using System.Security.Claims;
using System.Text.Json;

using ITPIE.Database.Abstractions;

using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Reporting;

/// <summary>
/// Per-user UI preferences (first consumer: saved table column widths). Every operation is scoped
/// to the authenticated user (from the session claim) — a user can only read/write their own
/// preferences. Values are opaque JSON objects owned by the client feature that stores them.
/// </summary>
public static class PreferencesApi
{
    private const int MaxKeyLength = 128;
    private const int MaxValueBytes = 16 * 1024;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/preferences/{key}", GetPreference);
        app.MapPut("/preferences/{key}", SetPreference);
    }

    private static async Task<IResult> GetPreference(
        string key,
        HttpContext context,
        NpgsqlDataSource db,
        CancellationToken ct
    )
    {
        if (!TryGetUserId(context, out Guid userId) || !IsValidKey(key))
        {
            return ApiError.Problem(400, "invalid_request", "Invalid preference request.");
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await foreach (UserPreferenceValue row in conn.GetUserPreferenceAsync(userId, key, ct))
        {
            return Results.Content(row.PrefValue.GetRawText(), "application/json");
        }

        // Unset — an empty object keeps the client's parse path uniform.
        return Results.Content("{}", "application/json");
    }

    private static async Task<IResult> SetPreference(
        string key,
        JsonElement body,
        HttpContext context,
        NpgsqlDataSource db,
        CancellationToken ct
    )
    {
        if (!TryGetUserId(context, out Guid userId) || !IsValidKey(key))
        {
            return ApiError.Problem(400, "invalid_request", "Invalid preference request.");
        }

        if (body.ValueKind != JsonValueKind.Object)
        {
            return ApiError.Problem(422, "invalid_value", "Preference value must be a JSON object.");
        }

        if (body.GetRawText().Length > MaxValueBytes)
        {
            return ApiError.Problem(413, "value_too_large", "Preference value is too large.");
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await conn.UpsertUserPreferenceAsync(userId, key, body, ct).ExecuteAsync(ct);
        return Results.NoContent();
    }

    private static bool TryGetUserId(HttpContext context, out Guid userId)
    {
        string? raw = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(raw, out userId);
    }

    /// <summary>Keys are client-defined slugs (e.g. "cols:devices"); keep them to a safe charset.</summary>
    private static bool IsValidKey(string key) =>
        key.Length is > 0 and <= MaxKeyLength
     && key.All(c => char.IsLetterOrDigit(c) || c is ':' or '-' or '_' or '.');
}