using ITPIE.Database.Abstractions;

using Npgsql;

namespace JMW.Discovery.Server.Queries;

/// <summary>
/// custom_field_definitions CRUD and the hard-delete paths for operator-authored
/// (FactSource.ManualEntry) facts_history rows. See docs/plans/user-provided.md.
/// </summary>
public static partial class ManualFactQueries
{
    /// <summary>
    /// Creates a custom field definition. Returns no rows if the slug is already taken
    /// (ON CONFLICT DO NOTHING) — the caller should treat an empty result as a 409.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(Guid Id, string Label, string Slug, string? TargetViewTitle,
        string? TargetViewGroup, bool IsNewView, DateTimeOffset CreatedAt, string CreatedBy)>
        InsertCustomFieldDefinitionAsync(
            this NpgsqlConnection connection,
            string label,
            string slug,
            string? targetViewTitle,
            string? targetViewGroup,
            bool isNewView,
            string createdBy,
            CancellationToken cancellationToken
        );

    /// <summary>Lists every custom field definition, ordered by label.</summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(Guid Id, string Label, string Slug, string? TargetViewTitle,
        string? TargetViewGroup, bool IsNewView, DateTimeOffset CreatedAt, string CreatedBy)>
        ListCustomFieldDefinitionsAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    /// <summary>Looks up one definition by its slug. No rows if the slug is unknown.</summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(Guid Id, string Label, string Slug, string? TargetViewTitle,
        string? TargetViewGroup, bool IsNewView, DateTimeOffset CreatedAt, string CreatedBy)>
        GetCustomFieldDefinitionBySlugAsync(
            this NpgsqlConnection connection,
            string slug,
            CancellationToken cancellationToken
        );

    /// <summary>Deletes a definition. Returns its slug (for the facts_history cascade), or no
    /// rows if the id was unknown.</summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(Guid Id, string Slug)> DeleteCustomFieldDefinitionAsync(
        this NpgsqlConnection connection,
        Guid id,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Reverts one device's manual override or custom field value — deletes every
    /// FactSource.ManualEntry row at that exact fact id, leaving any collector-authored
    /// history for the same id untouched. Returns the deleted row ids (empty if none existed).
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<FactIdResult> DeleteManualFactByIdAsync(
        this NpgsqlConnection connection,
        string id,
        short source,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Cascade for deleting a custom field definition: removes every device's
    /// FactSource.ManualEntry history for that slug. Returns the deleted row ids.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<FactIdResult> DeleteManualFactsByCustomSlugAsync(
        this NpgsqlConnection connection,
        string slug,
        short source,
        CancellationToken cancellationToken
    );
}
