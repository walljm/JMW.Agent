using System.Text.Json;

using ITPIE.Database.Abstractions;

using Npgsql;

namespace JMW.Discovery.Server.Queries;

/// <summary>
/// Per-user UI preference storage (see migration 0095). Generic key/value keyed by user so a
/// preference (first use: saved table column widths) follows the user across browsers/devices.
/// </summary>
public static partial class PreferencesQueries
{
    /// <summary>Loads one per-user preference value (JSONB), or no rows when unset.</summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<UserPreferenceValue> GetUserPreferenceAsync(
        this NpgsqlConnection connection,
        Guid userId,
        string prefKey,
        CancellationToken cancellationToken
    );

    /// <summary>Upserts one per-user preference value (JSONB), stamping updated_at.</summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<UserPreferenceOwner> UpsertUserPreferenceAsync(
        this NpgsqlConnection connection,
        Guid userId,
        string prefKey,
        JsonElement prefValue,
        CancellationToken cancellationToken
    );
}

/// <summary>Single-column JSONB result for <see cref="PreferencesQueries.GetUserPreferenceAsync" />.</summary>
public sealed record UserPreferenceValue(JsonElement PrefValue);

/// <summary>RETURNING shape for <see cref="PreferencesQueries.UpsertUserPreferenceAsync" />.</summary>
public sealed record UserPreferenceOwner(Guid UserId);