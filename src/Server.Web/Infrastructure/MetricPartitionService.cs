using System.Text.RegularExpressions;

using ITPIE.Migrations;

using Npgsql;

namespace JMW.Discovery.Server.Infrastructure;

/// <summary>
/// Maintains <c>metrics_raw</c>'s day partitions: provisions a rolling window of future
/// partitions so a slow day never blocks a write on DDL, and drops partitions older than
/// the retention window via <c>DROP TABLE</c> (metadata-only — no row-level delete, unlike
/// <see cref="RetentionService" />, which doesn't apply to a partitioned table). Runs once
/// on startup and then on a daily timer. See docs/plans/metrics-retention.md §2.3.
/// </summary>
public sealed partial class MetricPartitionService : BackgroundService
{
    private readonly MigrationCompletedSignal _migrationSignal;
    private readonly NpgsqlDataSource _db;
    private readonly IConfiguration _config;
    private readonly ILogger<MetricPartitionService> _logger;

    // Keep today + this many future days provisioned.
    private const int LookaheadDays = 2;

    private const string PartitionPrefix = "metrics_raw_";

    [GeneratedRegex(@"^metrics_raw_(\d{4})_(\d{2})_(\d{2})$")]
    private static partial Regex PartitionNamePattern();

    public MetricPartitionService(
        NpgsqlDataSource db,
        MigrationCompletedSignal migrationSignal,
        IConfiguration config,
        ILogger<MetricPartitionService> logger
    )
    {
        _db = db;
        _migrationSignal = migrationSignal;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _migrationSignal.Completed.WaitAsync(stoppingToken);

        TimeSpan interval = _config.GetValue<TimeSpan?>("MetricRetention:SweepInterval") ?? TimeSpan.FromHours(24);
        using PeriodicTimer timer = new(interval);

        do
        {
            try
            {
                await TriggerAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.MaintenanceFailed(_logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Runs partition maintenance immediately: provisions the lookahead window, drops
    /// partitions past the retention window. Idempotent (CREATE/DROP IF [NOT] EXISTS), so
    /// safe to call concurrently with the background timer — used directly by tests.
    /// </summary>
    public async Task TriggerAsync(CancellationToken ct = default)
    {
        bool enabled = _config.GetValue<bool?>("MetricRetention:Enabled") ?? true;
        if (!enabled)
        {
            Log.MaintenanceDisabled(_logger);
            return;
        }

        TimeSpan staleAfter = _config.GetValue<TimeSpan?>("MetricRetention:StaleAfter") ?? TimeSpan.FromDays(3);
        DateOnly today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);

        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);

        List<(string Name, DateOnly Day)> existing = await ListPartitionsAsync(conn, ct);
        HashSet<string> existingNames = existing.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        for (int offset = 0; offset <= LookaheadDays; offset++)
        {
            DateOnly day = today.AddDays(offset);
            string name = PartitionName(day);
            if (existingNames.Contains(name))
            {
                continue;
            }

            await CreatePartitionAsync(conn, name, day, ct);
            Log.PartitionCreated(_logger, name);
        }

        DateOnly cutoff = DateOnly.FromDateTime((DateTimeOffset.UtcNow - staleAfter).UtcDateTime);
        foreach ((string name, DateOnly day) in existing)
        {
            if (day >= cutoff)
            {
                continue;
            }

            await DropPartitionAsync(conn, name, ct);
            Log.PartitionDropped(_logger, name);
        }
    }

    private static string PartitionName(DateOnly day) => $"{PartitionPrefix}{day:yyyy_MM_dd}";

    // Reads partitions from Postgres catalog rather than assuming a fixed set — a partition
    // could exist from a previous deploy with a different lookahead/retention setting.
    private static async Task<List<(string Name, DateOnly Day)>> ListPartitionsAsync(
        NpgsqlConnection conn,
        CancellationToken ct
    )
    {
        List<(string, DateOnly)> result = [];

        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT child.relname
            FROM pg_inherits
            JOIN pg_class parent ON pg_inherits.inhparent = parent.oid
            JOIN pg_class child ON pg_inherits.inhrelid = child.oid
            WHERE parent.relname = 'metrics_raw'
            """;

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string name = reader.GetString(0);
            Match match = PartitionNamePattern().Match(name);
            if (!match.Success)
            {
                continue; // not one of our day-named partitions — leave untouched
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

    // Partition name comes from PartitionName() (self-generated, numeric-only) or was just
    // validated by PartitionNamePattern() above — never external input — so string
    // interpolation into DDL here is safe; range bounds can't be bind parameters in
    // CREATE TABLE ... PARTITION OF ... FOR VALUES FROM/TO.
    private static async Task CreatePartitionAsync(
        NpgsqlConnection conn,
        string name,
        DateOnly day,
        CancellationToken ct
    )
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {name} PARTITION OF metrics_raw
            FOR VALUES FROM ('{day:yyyy-MM-dd}') TO ('{day.AddDays(1):yyyy-MM-dd}')
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task DropPartitionAsync(NpgsqlConnection conn, string name, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP TABLE IF EXISTS {name}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Metric partition maintenance disabled via MetricRetention:Enabled config."
        )]
        internal static partial void MaintenanceDisabled(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Created metrics_raw partition {PartitionName}.")]
        internal static partial void PartitionCreated(ILogger logger, string partitionName);

        [LoggerMessage(Level = LogLevel.Information, Message = "Dropped metrics_raw partition {PartitionName}.")]
        internal static partial void PartitionDropped(ILogger logger, string partitionName);

        [LoggerMessage(Level = LogLevel.Error, Message = "Metric partition maintenance failed.")]
        internal static partial void MaintenanceFailed(ILogger logger, Exception ex);
    }
}
