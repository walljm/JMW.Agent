using System.Text;

using NpgsqlTypes;

namespace JMW.Discovery.Server.Projections;

/// <summary>
/// Generates the DDL for the projection tables directly from the <see cref="ProjectionDef" />
/// library, so a new projection column is a one-line library edit rather than a hand-written
/// migration that must be kept in sync by name and type. The DDL is idempotent and purely
/// additive (<c>CREATE TABLE IF NOT EXISTS</c> + <c>ADD COLUMN IF NOT EXISTS</c>): it creates
/// missing tables/columns and never drops or retypes anything. Non-additive changes (renames,
/// drops, backfills, indexes) and columns written outside the generic projection (e.g.
/// <c>proj_systems.last_seen_ip</c>, written by the DiscoveryMaterializer) remain the domain
/// of hand-authored migrations, which run first and own the base schema.
/// </summary>
public static class ProjectionSchema
{
    /// <summary>
    /// Maps a projection column's <see cref="NpgsqlDbType" /> to its Postgres type name.
    /// Shared with <see cref="GenericProjection" /> so the generated column type and the
    /// <c>unnest(...::type[])</c> cast the writer uses can never diverge.
    /// </summary>
    public static string PgType(NpgsqlDbType kind) => kind switch
    {
        NpgsqlDbType.Bigint => "bigint",
        NpgsqlDbType.Integer => "integer",
        NpgsqlDbType.Smallint => "smallint",
        NpgsqlDbType.Boolean => "boolean",
        NpgsqlDbType.Double => "float8",
        NpgsqlDbType.Real => "real",
        NpgsqlDbType.TimestampTz => "timestamptz",
        NpgsqlDbType.Uuid => "uuid",
        NpgsqlDbType.Inet => "inet",
        NpgsqlDbType.Jsonb => "jsonb",
        _ => "text",
    };

    /// <summary>
    /// Emits idempotent additive DDL for every projection: a <c>CREATE TABLE IF NOT EXISTS</c>
    /// (dimension text keys → value columns → <c>updated_at</c>, PK on the dimensions) plus an
    /// <c>ADD COLUMN IF NOT EXISTS</c> per value column so a column added to an existing
    /// projection lands without a migration. Table/dimension shape mirrors
    /// <see cref="GenericProjection.BuildSql" /> exactly (unqualified name via search_path,
    /// lowercased dimension columns), so what this creates is what the writer expects.
    /// </summary>
    public static string GenerateDdl(IEnumerable<ProjectionDef> defs)
    {
        StringBuilder sql = new();
        foreach (ProjectionDef def in defs)
        {
            string[] dimCols = def.DimensionNames.Select(n => n.ToLowerInvariant()).ToArray();

            sql.Append("CREATE TABLE IF NOT EXISTS ").Append(def.TableName).Append(" (\n");
            foreach (string dim in dimCols)
            {
                sql.Append("    ").Append(dim).Append(" text NOT NULL,\n");
            }

            foreach (ProjectionColumnDef col in def.Columns)
            {
                sql.Append("    ").Append(col.ColumnName).Append(' ').Append(PgType(col.Kind)).Append(",\n");
            }

            if (def.TracksAgentId)
            {
                sql.Append("    agent_id uuid,\n");
            }

            sql.Append("    updated_at timestamptz NOT NULL DEFAULT now(),\n");
            sql.Append("    PRIMARY KEY (").Append(string.Join(", ", dimCols)).Append(")\n);\n");

            // Cover columns added to a projection whose table already exists.
            foreach (ProjectionColumnDef col in def.Columns)
            {
                sql.Append("ALTER TABLE ")
                    .Append(def.TableName)
                    .Append(" ADD COLUMN IF NOT EXISTS ")
                    .Append(col.ColumnName)
                    .Append(' ')
                    .Append(PgType(col.Kind))
                    .Append(";\n");
            }

            if (def.TracksAgentId)
            {
                sql.Append("ALTER TABLE ")
                    .Append(def.TableName)
                    .Append(" ADD COLUMN IF NOT EXISTS agent_id uuid;\n");
            }

            foreach (ProjectionIndexDef ix in def.Indexes)
            {
                sql.Append("CREATE INDEX IF NOT EXISTS ")
                    .Append(ix.Name)
                    .Append(" ON ")
                    .Append(def.TableName);
                if (ix.Method == ProjectionIndexMethod.GinTrgm)
                {
                    sql.Append(" USING gin (")
                        .Append(string.Join(", ", ix.Columns.Select(c => c + " gin_trgm_ops")))
                        .Append(')');
                }
                else
                {
                    sql.Append(" (").Append(string.Join(", ", ix.Columns)).Append(')');
                }

                if (ix.Where is { } where)
                {
                    sql.Append(" WHERE ").Append(where);
                }

                sql.Append(";\n");
            }
        }

        return sql.ToString();
    }
}