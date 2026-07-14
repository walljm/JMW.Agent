using DotNet.Testcontainers.Builders;

using JMW.Discovery.Server.Projections;

using Npgsql;

using NpgsqlTypes;

using Testcontainers.PostgreSql;

namespace JMW.Discovery.Tests;

/// <summary>
/// Integration test: starts a real Postgres container, applies the production migration
/// chain, then verifies every column declared in ProjectionLibrary actually exists
/// in the database. Catches col-name typos and schema drift before they
/// silently empty projection tables in production.
/// </summary>
public sealed class ProjectionSchemaTests : IAsyncLifetime
{
    private PostgreSqlContainer _pg = null!;
    private NpgsqlDataSource _ds = null!;

    public async Task InitializeAsync()
    {
        _pg = new PostgreSqlBuilder()
            .WithDatabase("discovery_test")
            .WithUsername("test")
            .WithPassword("test")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(5432))
            .Build();

        await _pg.StartAsync();

        // Match production: connect with search_path=jmwdiscovery,public; migrations
        // create every object in the jmwdiscovery schema.
        string connectionString = new NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
        {
            SearchPath = "jmwdiscovery,public",
        }.ConnectionString;

        _ds = new NpgsqlDataSourceBuilder(connectionString).Build();

        await MigrationTestRunner.ApplyAsync(connectionString);

        // pg_trgm installs into jmwdiscovery (first in the migration search_path). Move it to
        // public so a throwaway schema built with path "{schema}, public" can resolve
        // gin_trgm_ops without pulling jmwdiscovery's proj_ tables into scope (which would make
        // CREATE TABLE IF NOT EXISTS no-op instead of creating in the throwaway schema).
        await using NpgsqlConnection conn = await _ds.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new("ALTER EXTENSION pg_trgm SET SCHEMA public;", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await _ds.DisposeAsync();
        await _pg.DisposeAsync();
    }

    [Fact]
    public async Task AllProjectionColumnsMustExistInSchema()
    {
        // Load actual column names from the database, keyed by table name.
        Dictionary<string, HashSet<string>> dbColumns = await LoadDbColumnsAsync();

        IReadOnlyList<IProjection> projections = ProjectionLibrary.CreateAll(_ds);
        List<string> failures = new();

        foreach (GenericProjection proj in projections.OfType<GenericProjection>())
        {
            ProjectionDef def = proj.Def;

            if (!dbColumns.TryGetValue(def.TableName, out HashSet<string>? cols))
            {
                failures.Add($"Table '{def.TableName}' does not exist in Schema.sql");
                continue;
            }

            // Dimension key columns (lowercased dimension names, e.g. "device", "disk")
            foreach (string dim in def.DimensionNames)
            {
                string col = dim.ToLowerInvariant();
                if (!cols.Contains(col))
                {
                    failures.Add($"{def.TableName}: dimension key column '{col}' is missing");
                }
            }

            // Mapped attribute columns
            foreach (ProjectionColumnDef c in def.Columns)
            {
                if (!cols.Contains(c.ColumnName))
                {
                    failures.Add($"{def.TableName}: column '{c.ColumnName}' (from path '{c.Attribute}') is missing");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Projection/schema mismatches:\n" + string.Join("\n", failures)
        );
    }

    [Fact]
    public async Task AllProjectionTablesMustExistInSchema()
    {
        Dictionary<string, HashSet<string>> dbColumns = await LoadDbColumnsAsync();
        IReadOnlyList<IProjection> projections = ProjectionLibrary.CreateAll(_ds);

        List<string> missing = projections
            .OfType<GenericProjection>()
            .Select(p => p.Def.TableName)
            .Distinct()
            .Where(t => !dbColumns.ContainsKey(t))
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Tables referenced by projections but missing from Schema.sql:\n" + string.Join("\n", missing)
        );
    }

    [Fact]
    public async Task AllProjectionColumnTypesMatchSchema()
    {
        Dictionary<string, Dictionary<string, string>> dbTypes = await LoadDbColumnTypesAsync();

        IReadOnlyList<IProjection> projections = ProjectionLibrary.CreateAll(_ds);
        List<string> failures = new();

        foreach (GenericProjection proj in projections.OfType<GenericProjection>())
        {
            ProjectionDef def = proj.Def;

            if (!dbTypes.TryGetValue(def.TableName, out Dictionary<string, string>? cols))
            {
                continue; // missing table is caught by AllProjectionTablesMustExistInSchema
            }

            foreach (ProjectionColumnDef c in def.Columns)
            {
                if (!cols.TryGetValue(c.ColumnName, out string? pgType))
                {
                    continue; // missing column caught by AllProjectionColumnsMustExistInSchema
                }

                string expected = MapNpgsqlType(c.Kind);
                if (!string.Equals(pgType, expected, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(
                        $"{def.TableName}.{c.ColumnName}: declared as {c.Kind} (expects '{expected}') "
                      + $"but schema has '{pgType}'"
                    );
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Projection/schema type mismatches:\n" + string.Join("\n", failures)
        );
    }

    [Fact]
    public async Task GeneratedDdl_IsAnIdempotentNoOpOnTheMigratedSchema()
    {
        // ProjectionSchemaService runs this DDL at boot on top of the migration-owned
        // schema. It MUST add nothing to the current schema (migrations already satisfy
        // the library) and must run without error — proving the cutover is safe.
        IEnumerable<ProjectionDef> defs = ProjectionLibrary.CreateAll(_ds)
            .OfType<GenericProjection>()
            .Select(p => p.Def);
        string ddl = ProjectionSchema.GenerateDdl(defs);

        Dictionary<string, HashSet<string>> before = await LoadDbColumnsAsync();
        HashSet<string> indexesBefore =
            (await LoadIndexDefsAsync("jmwdiscovery")).Keys.ToHashSet(StringComparer.Ordinal);
        await using (NpgsqlConnection conn = await _ds.OpenConnectionAsync())
        await using (NpgsqlCommand cmd = new(ddl, conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        Dictionary<string, HashSet<string>> after = await LoadDbColumnsAsync();
        HashSet<string> indexesAfter =
            (await LoadIndexDefsAsync("jmwdiscovery")).Keys.ToHashSet(StringComparer.Ordinal);

        List<string> added = new();
        foreach ((string table, HashSet<string> cols) in after)
        {
            HashSet<string> prev = before.TryGetValue(table, out HashSet<string>? p) ? p : new(StringComparer.Ordinal);
            added.AddRange(cols.Where(c => !prev.Contains(c)).Select(c => $"{table}.{c}"));
        }

        added.AddRange(indexesAfter.Except(indexesBefore).Select(i => $"index {i}"));

        Assert.True(
            added.Count == 0,
            "Generator added columns/indexes the migrations lacked (drift — a projection column or "
          + "index is declared but its name doesn't match the migration):\n"
          + string.Join("\n", added)
        );
    }

    [Fact]
    public async Task GeneratedDdl_BuildsCompleteProjectionSchemaFromScratch()
    {
        // The generator alone must reproduce every projection table (dimension keys, typed
        // value columns, updated_at) — proving it can own a brand-new projection with no
        // migration. Built into a throwaway schema so the migrated schema is untouched.
        IReadOnlyList<GenericProjection> projs =
            ProjectionLibrary.CreateAll(_ds).OfType<GenericProjection>().ToList();
        string ddl = ProjectionSchema.GenerateDdl(projs.Select(p => p.Def));

        await BuildSchemaFromDdlAsync("gen_test", ddl);

        Dictionary<string, Dictionary<string, string>> gen = await LoadColumnTypesAsync("gen_test");
        List<string> failures = new();
        foreach (GenericProjection p in projs)
        {
            ProjectionDef def = p.Def;
            if (!gen.TryGetValue(def.TableName, out Dictionary<string, string>? cols))
            {
                failures.Add($"generator did not create table '{def.TableName}'");
                continue;
            }

            foreach (string dim in def.DimensionNames)
            {
                if (!cols.ContainsKey(dim.ToLowerInvariant()))
                {
                    failures.Add($"{def.TableName}: missing dimension column '{dim.ToLowerInvariant()}'");
                }
            }

            foreach (ProjectionColumnDef c in def.Columns)
            {
                if (!cols.TryGetValue(c.ColumnName, out string? pgType))
                {
                    failures.Add($"{def.TableName}: missing column '{c.ColumnName}'");
                }
                else if (!string.Equals(pgType, MapNpgsqlType(c.Kind), StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(
                        $"{def.TableName}.{c.ColumnName}: generated '{pgType}', expected '{MapNpgsqlType(c.Kind)}'"
                    );
                }
            }

            if (!cols.ContainsKey("updated_at"))
            {
                failures.Add($"{def.TableName}: missing updated_at");
            }
        }

        // Every library-declared index must be reproduced by the generator, and match the
        // migration-created index of the same name byte-for-byte (schema qualifier aside).
        // This is what makes it safe to move an index's ownership into the library.
        Dictionary<string, string> genIx = await LoadIndexDefsAsync("gen_test");
        Dictionary<string, string> migIx = await LoadIndexDefsAsync("jmwdiscovery");
        foreach (ProjectionIndexDef ix in projs.SelectMany(p => p.Def.Indexes))
        {
            if (!genIx.TryGetValue(ix.Name, out string? genDef))
            {
                failures.Add($"generator did not create index '{ix.Name}'");
            }
            else if (migIx.TryGetValue(ix.Name, out string? migDef)
             && !string.Equals(genDef, migDef, StringComparison.Ordinal))
            {
                failures.Add($"index '{ix.Name}': generated '{genDef}' but migration has '{migDef}'");
            }
        }

        Assert.True(failures.Count == 0, "Generator produced an incomplete schema:\n" + string.Join("\n", failures));
    }

    [Fact]
    public async Task GeneratedDdl_EmitsPartialAndGinIndexes()
    {
        // Covers the index generator branches not exercised by the dogfooded (no-predicate)
        // indexes: a partial btree (WHERE) and a GIN trigram index.
        ProjectionDef def = new(
            "demo_idx_proj",
            ["Device"],
            [new("Mac", "mac", NpgsqlDbType.Text)]
        )
        {
            Indexes =
            [
                new("demo_mac_idx", ["mac"], ProjectionIndexMethod.Btree, "mac IS NOT NULL"),
                new("demo_mac_trgm", ["mac"], ProjectionIndexMethod.GinTrgm),
            ],
        };

        await BuildSchemaFromDdlAsync("gen_idx", ProjectionSchema.GenerateDdl([def]));

        Dictionary<string, string> ix = await LoadIndexDefsAsync("gen_idx");
        Assert.True(ix.ContainsKey("demo_mac_idx"), "partial index not created");
        Assert.Contains("WHERE (mac IS NOT NULL)", ix["demo_mac_idx"]);
        Assert.True(ix.ContainsKey("demo_mac_trgm"), "gin index not created");
        Assert.Contains("gin", ix["demo_mac_trgm"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gin_trgm_ops", ix["demo_mac_trgm"]);
    }

    [Fact]
    public async Task GeneratedDdl_AddsAMissingColumnToAnExistingTable()
    {
        // The headline workflow: adding a column to an existing projection is a one-line
        // library edit — the generator's ADD COLUMN IF NOT EXISTS lands it with no migration.
        ProjectionDef def = new(
            "demo_proj",
            ["Device"],
            [new("Existing", "existing_col", NpgsqlDbType.Text), new("New", "new_col", NpgsqlDbType.Bigint)]
        );

        await using (NpgsqlConnection conn = await _ds.OpenConnectionAsync())
        await using (NpgsqlTransaction tx = await conn.BeginTransactionAsync())
        {
            await using (NpgsqlCommand setup = new(
                "DROP SCHEMA IF EXISTS gen_add CASCADE; CREATE SCHEMA gen_add; SET LOCAL search_path TO gen_add;\n"
              // Pre-existing table WITHOUT new_col (as if migrated before the column was declared).
              + "CREATE TABLE demo_proj (device text NOT NULL, existing_col text, "
              + "updated_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY (device));",
                conn,
                tx
            ))
            {
                await setup.ExecuteNonQueryAsync();
            }

            await using (NpgsqlCommand cmd = new(ProjectionSchema.GenerateDdl([def]), conn, tx))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }

        Dictionary<string, Dictionary<string, string>> cols = await LoadColumnTypesAsync("gen_add");
        Assert.True(cols["demo_proj"].ContainsKey("new_col"), "generator did not add the new column");
        Assert.Equal("bigint", cols["demo_proj"]["new_col"]);
        Assert.True(cols["demo_proj"].ContainsKey("existing_col"), "generator dropped the existing column");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string MapNpgsqlType(NpgsqlDbType kind) => kind switch
    {
        NpgsqlDbType.Text => "text",
        NpgsqlDbType.Integer => "integer",
        NpgsqlDbType.Bigint => "bigint",
        NpgsqlDbType.Double => "double precision",
        NpgsqlDbType.Boolean => "boolean",
        NpgsqlDbType.TimestampTz => "timestamp with time zone",
        NpgsqlDbType.Jsonb => "jsonb",
        NpgsqlDbType.Smallint => "smallint",
        NpgsqlDbType.Real => "real",
        NpgsqlDbType.Uuid => "uuid",
        NpgsqlDbType.Inet => "inet",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private Task<Dictionary<string, Dictionary<string, string>>> LoadDbColumnTypesAsync() =>
        LoadColumnTypesAsync("jmwdiscovery");

    private async Task<Dictionary<string, Dictionary<string, string>>> LoadColumnTypesAsync(string schema)
    {
        const string sql = """
            SELECT table_name, column_name, data_type
            FROM information_schema.columns
            WHERE table_schema = @schema
            ORDER BY table_name, column_name
            """;

        Dictionary<string, Dictionary<string, string>> result = new(StringComparer.Ordinal);

        await using NpgsqlConnection conn = await _ds.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("schema", schema);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            string table = reader.GetString(0);
            string column = reader.GetString(1);
            string pgType = reader.GetString(2);

            if (!result.TryGetValue(table, out Dictionary<string, string>? cols))
            {
                cols = new Dictionary<string, string>(StringComparer.Ordinal);
                result[table] = cols;
            }

            cols[column] = pgType;
        }

        return result;
    }

    private async Task<Dictionary<string, HashSet<string>>> LoadDbColumnsAsync()
    {
        const string sql = """
            SELECT table_name, column_name
            FROM information_schema.columns
            WHERE table_schema = 'jmwdiscovery'
            ORDER BY table_name, column_name
            """;

        Dictionary<string, HashSet<string>> result = new(StringComparer.Ordinal);

        await using NpgsqlConnection conn = await _ds.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            string table = reader.GetString(0);
            string column = reader.GetString(1);
            if (!result.TryGetValue(table, out HashSet<string>? cols))
            {
                cols = new HashSet<string>(StringComparer.Ordinal);
                result[table] = cols;
            }

            cols.Add(column);
        }

        return result;
    }

    // indexname -> indexdef with the schema qualifier stripped, so definitions built in
    // different schemas (jmwdiscovery vs a throwaway) compare equal.
    private async Task<Dictionary<string, string>> LoadIndexDefsAsync(string schema)
    {
        const string sql = "SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = @schema";
        Dictionary<string, string> result = new(StringComparer.Ordinal);

        await using NpgsqlConnection conn = await _ds.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("schema", schema);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            string name = reader.GetString(0);
            string def = reader.GetString(1).Replace($"{schema}.", "", StringComparison.Ordinal);
            result[name] = def;
        }

        return result;
    }

    // Drops+recreates a schema and runs the given DDL into it, isolated so the migrated
    // schema is untouched. SET LOCAL resets at commit, returning the pooled connection clean.
    private async Task BuildSchemaFromDdlAsync(string schema, string ddl)
    {
        await using NpgsqlConnection conn = await _ds.OpenConnectionAsync();
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync();
        // {schema} first (tables land there); public follows so gin_trgm_ops resolves (pg_trgm
        // is relocated to public in setup). jmwdiscovery is deliberately absent so its proj_
        // tables don't shadow the CREATE TABLE IF NOT EXISTS into a no-op.
        await using (NpgsqlCommand setup = new(
            $"DROP SCHEMA IF EXISTS {schema} CASCADE; CREATE SCHEMA {schema}; "
          + $"SET LOCAL search_path TO {schema}, public;",
            conn,
            tx
        ))
        {
            await setup.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand cmd = new(ddl, conn, tx))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }
}