using NpgsqlTypes;

namespace JMW.Discovery.Server.Projections;

/// <summary>Describes one attribute-to-column mapping in a projection.</summary>
public sealed record ProjectionColumnDef(
    string Attribute, // fact attribute name, e.g. "Speed"
    string ColumnName, // SQL column name,     e.g. "speed"
    NpgsqlDbType Kind
); // Postgres storage type — use NpgsqlDbType directly

/// <summary>How a projection index is built.</summary>
public enum ProjectionIndexMethod
{
    /// <summary>Standard btree over the listed columns.</summary>
    Btree,

    /// <summary>GIN with <c>gin_trgm_ops</c> per column (trigram fuzzy text search).</summary>
    GinTrgm,
}

/// <summary>
/// Declares one index on a projection table, so the DDL generator can create it — a new
/// projection's indexes live in the library alongside its columns, not in a hand migration.
/// </summary>
public sealed record ProjectionIndexDef(
    string Name,
    IReadOnlyList<string> Columns,
    ProjectionIndexMethod Method = ProjectionIndexMethod.Btree,
    string? Where = null // optional partial-index predicate, raw SQL (e.g. "mac IS NOT NULL")
);

/// <summary>
/// Fully describes a projection table: what entity it tracks,
/// which facts feed it, how they map to columns, and its indexes.
/// </summary>
public sealed record ProjectionDef(
    string TableName,
    IReadOnlyList<string> DimensionNames,
    IReadOnlyList<ProjectionColumnDef> Columns
)
{
    /// <summary>
    /// Indexes owned by this projection. Empty = the table's indexes (if any) are
    /// still migration-managed. Declared indexes are generated as
    /// <c>
    /// CREATE INDEX IF NOT
    /// EXISTS
    /// </c>
    /// , so they no-op against an index a migration already created by the same name.
    /// </summary>
    public IReadOnlyList<ProjectionIndexDef> Indexes { get; init; } = [];
}