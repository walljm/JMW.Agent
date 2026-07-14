using System.Text.Json;

using ITPIE.Database.Abstractions;

using Npgsql;

namespace JMW.Discovery.Server.Queries;

public static partial class MaintenanceQueries
{
    // ── OUI ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current OUI metadata row (version_hash, updated_at, record_count).
    /// Returns no rows if no OUI data has been loaded yet.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string VersionHash, DateTimeOffset UpdatedAt, long RecordCount)>
        GetOuiMetaAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    // ── Retention ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all enabled retention policies that have a stale_after interval set.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string TableName, string TimeColumn, TimeSpan? StaleAfter)>
        ListRetentionPoliciesAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Lists all retention policies (including disabled ones) ordered by category and table.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string TableName, string Category, string TimeColumn, TimeSpan? StaleAfter, bool Enabled,
            string?
            Notes)> ListAllRetentionPoliciesAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Updates a retention policy's stale_after interval and enabled flag.
    /// Returns the table_name, or no rows if not found.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<TableNameResult> UpdateRetentionPolicyAsync(
        this NpgsqlConnection connection,
        string tableName,
        TimeSpan? staleAfter,
        bool enabled,
        CancellationToken cancellationToken
    );

    // ── Audit ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts an audit log entry. Returns the inserted id.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<AuditIdResult> InsertAuditLogAsync(
        this NpgsqlConnection connection,
        string actor,
        string action,
        string? targetRef,
        JsonElement? detail,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Lists audit log entries with keyset pagination and optional filters.
    /// Pass null for afterOccurredAt/afterId to start from the first page.
    /// actionPrefix and actorPrefix use SQL LIKE matching (append % in caller).
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(long Id, DateTimeOffset OccurredAt, string Actor, string Action, string? TargetRef,
            JsonElement?
            Detail)> ListAuditLogAsync(
            this NpgsqlConnection connection,
            int limit,
            DateTimeOffset? afterOccurredAt,
            long? afterId,
            string? action,
            string? actor,
            string? targetRefQuery,
            DateTimeOffset? since,
            DateTimeOffset? until,
            CancellationToken cancellationToken
        );
}