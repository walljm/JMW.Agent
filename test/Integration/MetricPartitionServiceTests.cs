using System.Text.RegularExpressions;

using ITPIE.Migrations;

using JMW.Discovery.Server.Infrastructure;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace JMW.Discovery.Tests;

/// <summary>
/// Verifies MetricPartitionService's partition maintenance: provisioning the lookahead
/// window and dropping partitions past the retention window — see
/// docs/plans/metrics-retention.md §2.3.
/// </summary>
[Collection("Integration")]
public sealed partial class MetricPartitionServiceTests : IAsyncLifetime
{
    [GeneratedRegex(@"^metrics_raw_(\d{4})_(\d{2})_(\d{2})$")]
    private static partial Regex PartitionNamePatternRegex();

    private static readonly Regex PartitionNamePattern = PartitionNamePatternRegex();

    private readonly IntegrationFixture _fixture;

    public MetricPartitionServiceTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // Only drop the synthetic out-of-window partitions this class creates (old/recent test
    // dates) — never the standard today..today+2 baseline. The Postgres container (and its
    // one migration run) is shared across the whole "Integration" collection, so wiping the
    // baseline here would starve every other test class's now()-timestamped inserts of a
    // partition to land in.
    public async Task DisposeAsync()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        DateOnly baselineEnd = today.AddDays(2);
        foreach ((string name, DateOnly day) in await ListPartitionsAsync())
        {
            if (day < today || day > baselineEnd)
            {
                await DropPartitionAsync(name);
            }
        }
    }

    [Fact]
    public async Task TriggerAsync_ProvisionsTodayThroughLookaheadPartitions()
    {
        MetricPartitionService svc = CreateService();

        await svc.TriggerAsync();

        List<string> partitions = await ListPartitionNamesAsync();
        DateTime today = DateTime.UtcNow.Date;
        Assert.Contains($"metrics_raw_{today:yyyy_MM_dd}", partitions);
        Assert.Contains($"metrics_raw_{today.AddDays(1):yyyy_MM_dd}", partitions);
        Assert.Contains($"metrics_raw_{today.AddDays(2):yyyy_MM_dd}", partitions);
    }

    [Fact]
    public async Task TriggerAsync_SecondRun_IsIdempotent()
    {
        MetricPartitionService svc = CreateService();

        await svc.TriggerAsync();
        await svc.TriggerAsync(); // must not throw — CREATE TABLE IF NOT EXISTS

        List<string> partitions = await ListPartitionNamesAsync();
        DateTime today = DateTime.UtcNow.Date;
        Assert.Contains($"metrics_raw_{today:yyyy_MM_dd}", partitions);
    }

    [Fact]
    public async Task TriggerAsync_DropsPartitionsOlderThanRetentionWindow()
    {
        DateOnly oldDay = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-10));
        await CreatePartitionAsync(oldDay);

        MetricPartitionService svc = CreateService(staleAfter: TimeSpan.FromDays(3));
        await svc.TriggerAsync();

        List<string> partitions = await ListPartitionNamesAsync();
        Assert.DoesNotContain($"metrics_raw_{oldDay:yyyy_MM_dd}", partitions);
    }

    [Fact]
    public async Task TriggerAsync_KeepsPartitionsWithinRetentionWindow()
    {
        DateOnly recentDay = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        await CreatePartitionAsync(recentDay);

        MetricPartitionService svc = CreateService(staleAfter: TimeSpan.FromDays(3));
        await svc.TriggerAsync();

        List<string> partitions = await ListPartitionNamesAsync();
        Assert.Contains($"metrics_raw_{recentDay:yyyy_MM_dd}", partitions);
    }

    [Fact]
    public async Task TriggerAsync_Disabled_LeavesExistingPartitionsUntouched()
    {
        DateOnly oldDay = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-10));
        await CreatePartitionAsync(oldDay);

        MetricPartitionService svc = CreateService(enabled: false);
        await svc.TriggerAsync();

        List<string> partitions = await ListPartitionNamesAsync();
        Assert.Contains($"metrics_raw_{oldDay:yyyy_MM_dd}", partitions); // not dropped — disabled
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private MetricPartitionService CreateService(bool enabled = true, TimeSpan? staleAfter = null)
    {
        Dictionary<string, string?> settings = new() { ["MetricRetention:Enabled"] = enabled.ToString() };
        if (staleAfter is not null)
        {
            settings["MetricRetention:StaleAfter"] = staleAfter.Value.ToString();
        }

        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new MetricPartitionService(
            _fixture.DataSource,
            new MigrationCompletedSignal(),
            config,
            NullLogger<MetricPartitionService>.Instance
        );
    }

    private async Task CreatePartitionAsync(DateOnly day)
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS metrics_raw_{day:yyyy_MM_dd} PARTITION OF metrics_raw
            FOR VALUES FROM ('{day:yyyy-MM-dd}') TO ('{day.AddDays(1):yyyy-MM-dd}')
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task DropPartitionAsync(string name)
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP TABLE IF EXISTS {name}";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> ListPartitionNamesAsync() =>
        (await ListPartitionsAsync()).Select(p => p.Name).ToList();

    private async Task<List<(string Name, DateOnly Day)>> ListPartitionsAsync()
    {
        List<(string, DateOnly)> result = [];
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT child.relname
            FROM pg_inherits
            JOIN pg_class parent ON pg_inherits.inhparent = parent.oid
            JOIN pg_class child ON pg_inherits.inhrelid = child.oid
            WHERE parent.relname = 'metrics_raw'
            """;
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            string name = reader.GetString(0);
            Match match = PartitionNamePattern.Match(name);
            if (!match.Success)
            {
                continue;
            }

            DateOnly day = new(
                int.Parse(match.Groups[1].Value),
                int.Parse(match.Groups[2].Value),
                int.Parse(match.Groups[3].Value)
            );
            result.Add((name, day));
        }

        return result;
    }
}