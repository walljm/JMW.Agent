using ITPIE.Database.Abstractions;

using Npgsql;

namespace JMW.Discovery.Server.Queries;

/// <summary>
/// Read/write queries for unified operator-authored facts (docs/plans/architecture-operator-facts.md):
/// per-device operator-fact listing, path-level label/description metadata, child-collection key
/// suggestions, and the source-scoped hard-delete used by revert/clear. Fleet-wide keyset browse
/// queries live inline in <c>OperatorFactsApi</c> (raw-SQL keyset pattern, like the reporting
/// endpoints), not here.
/// </summary>
public static partial class ManualFactQueries
{
    /// <summary>
    /// Every operator-authored (FactSource.ManualEntry) fact for one device — latest value per fact
    /// id — with any path-level label metadata. The caller derives the Override/Arbitrary kind from
    /// the attribute path (architecture §7.1).
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string AttributePath, string? KeyValues, string? Value, string? Label,
        string SourceName, DateTimeOffset CollectedAt)>
        GetDeviceOperatorFactsAsync(
            this NpgsqlConnection connection,
            Guid deviceId,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Upserts path-level label/description metadata keyed by the fact's device-independent identity
    /// (attribute_path + non-device key_values). Returns the row id.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<MetadataIdResult> UpsertFactPathMetadataAsync(
        this NpgsqlConnection connection,
        string attributePath,
        string keyValues,
        string? label,
        string? description,
        string updatedBy,
        bool? showInReports,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// The device-scoped arbitrary fact paths flagged to appear as extra columns in device-listing
    /// reports (fact_path_metadata.show_in_reports), with their display labels. Ordinal path order.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string AttributePath, string? Label)> GetReportFactColumnsAsync(
        this NpgsqlConnection connection,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Distinct observed keys of a device's child-collection dimension (e.g. the MACs of its known
    /// interfaces), backing the child-key combo box (REQ-010). Works for any dimension.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<CollectionKeyResult> GetDeviceCollectionKeysAsync(
        this NpgsqlConnection connection,
        string deviceId,
        string dimension,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Whether an operator-authored value already exists at this exact fact id — backs the
    /// overwrite-confirmation guard (REQ-005).
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<InUseResult> GetOperatorFactExistsAsync(
        this NpgsqlConnection connection,
        string id,
        short source,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Reverts one operator-authored fact — deletes every FactSource.ManualEntry row at that exact
    /// fact id, leaving any collector-authored history for the same id untouched. Returns the deleted
    /// row ids (empty if none existed).
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<FactIdResult> DeleteManualFactByIdAsync(
        this NpgsqlConnection connection,
        string id,
        short source,
        CancellationToken cancellationToken
    );
}
