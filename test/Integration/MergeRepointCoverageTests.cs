using DotNet.Testcontainers.Builders;

using JMW.Discovery.Server.Projections;

using Npgsql;

using Testcontainers.PostgreSql;

namespace JMW.Discovery.Tests;

/// <summary>
/// Fitness function (docs/plans/architecture-identity-facts.md §6.1): every table with a literal
/// "device" dimension-key column must be covered by DeviceRegistry's merge repoint — either
/// generically (a <see cref="ProjectionDef"/> in <see cref="ProjectionLibrary.AllDefs"/> whose
/// first dimension is "Device") or explicitly hand-repointed (<see cref="ExplicitlyRepointedExtras"/>,
/// pinned to the live schema below). This is what the old hardcoded repoint list silently missed
/// for proj_device_certs/proj_discovered_tls before the AllDefs-driven rewrite — and what would
/// silently miss materialization_facts today, since it's fact-shaped rather than a ProjectionDef
/// and so invisible to AllDefs. A new "device"-columned table that is neither must fail this test.
/// </summary>
public sealed class MergeRepointCoverageTests : IAsyncLifetime
{
    // Tables with a "device" column that are repointed by hand in DeviceRegistry rather than via
    // the generic ProjectionLibrary.AllDefs loop, because they aren't (and shouldn't be) a
    // ProjectionDef. Keep in sync with DeviceRegistry.RepointProjectionsAsync.
    private static readonly HashSet<string> ExplicitlyRepointedExtras =
        new(StringComparer.Ordinal) { "materialization_facts" };

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

        string connectionString = new NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
        {
            SearchPath = "jmwdiscovery,public",
        }.ConnectionString;

        _ds = new NpgsqlDataSourceBuilder(connectionString).Build();

        await MigrationTestRunner.ApplyAsync(connectionString);

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
    public async Task EveryDeviceColumnedTableIsCoveredByTheMergeRepoint()
    {
        const string sql = """
            SELECT table_name FROM information_schema.columns
            WHERE table_schema = 'jmwdiscovery' AND column_name = 'device'
            ORDER BY table_name
            """;

        List<string> deviceColumnedTables = [];
        await using (NpgsqlConnection conn = await _ds.OpenConnectionAsync())
        await using (NpgsqlCommand cmd = new(sql, conn))
        await using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                deviceColumnedTables.Add(reader.GetString(0));
            }
        }

        HashSet<string> genericallyRepointed = ProjectionLibrary.AllDefs
            .Where(d => d.DimensionNames.Count > 0 && d.DimensionNames[0] == "Device")
            .Select(d => d.TableName)
            .ToHashSet(StringComparer.Ordinal);

        List<string> uncovered = deviceColumnedTables
            .Where(t => !genericallyRepointed.Contains(t) && !ExplicitlyRepointedExtras.Contains(t))
            .ToList();

        Assert.True(
            uncovered.Count == 0,
            "These tables have a 'device' column but are covered by neither ProjectionLibrary.AllDefs "
          + "(generic repoint) nor MergeRepointCoverageTests.ExplicitlyRepointedExtras (hand-written "
          + "repoint) — a device merge would silently leave their rows stuck under the loser id:\n"
          + string.Join("\n", uncovered)
        );

        // The inverse also matters: an extras entry that no longer exists in the schema means the
        // hand-written repoint code (or this list) has drifted from reality.
        List<string> staleExtras = ExplicitlyRepointedExtras
            .Where(t => !deviceColumnedTables.Contains(t))
            .ToList();

        Assert.True(
            staleExtras.Count == 0,
            "ExplicitlyRepointedExtras references tables with no 'device' column in the live schema "
          + "(stale entry — table renamed/dropped, or never had this column):\n"
          + string.Join("\n", staleExtras)
        );
    }
}