using System.Text.Json;
using System.Text.RegularExpressions;

using ITPIE.Migrations;

using JMW.Discovery.Server.Data;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Infrastructure;

public sealed record TablePruneResult(string TableName, long RowsDeleted);

public sealed record RetentionRunResult(
    DateTimeOffset RanAt,
    TimeSpan Duration,
    IReadOnlyList<TablePruneResult> Tables
);

/// <summary>
/// Prunes stale rows from all tables registered in retention_policies.
/// Runs once on startup and then on a periodic timer (default: every 4 hours).
/// Can also be triggered on demand via TriggerAsync (called by the admin API).
/// </summary>
public sealed partial class RetentionService : BackgroundService
{
    private readonly MigrationCompletedSignal _migrationSignal;
    private readonly NpgsqlDataSource _db;
    private readonly IConfiguration _config;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(
        NpgsqlDataSource db,
        MigrationCompletedSignal migrationSignal,
        IConfiguration config,
        ILogger<RetentionService> logger
    )
    {
        _db = db;
        _migrationSignal = migrationSignal;
        _config = config;
        _logger = logger;
    }

    // Prevents concurrent sweeps. TriggerAsync waits up to 30s; the timer skips if busy.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Only letters, digits, and underscores — guards the dynamic table/column names
    // sourced from retention_policies against accidental injection. The table was
    // seeded by our own migration so this is a defence-in-depth check, not primary validation.
    [GeneratedRegex(@"^[a-z_][a-z0-9_]*$")]
    private static partial Regex SafeIdentifier();

    private const int BatchSize = 10_000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _migrationSignal.Completed.WaitAsync(stoppingToken);

        // Startup sweep — always runs once so expired sessions are cleared before the
        // app accepts traffic, regardless of when the next timer tick falls.
        await RunAsync(skipIfBusy: false, stoppingToken);

        TimeSpan interval = _config.GetValue<TimeSpan?>("Retention:SweepInterval") ?? TimeSpan.FromHours(4);
        using PeriodicTimer timer = new(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // Skip if a manually triggered sweep is already in progress.
            await RunAsync(skipIfBusy: true, stoppingToken);
        }
    }

    /// <summary>
    /// Triggers an immediate retention sweep. Waits up to 30 seconds to acquire the
    /// sweep lock; returns null if a sweep is already running and the wait times out.
    /// </summary>
    public async Task<RetentionRunResult?> TriggerAsync(CancellationToken ct = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            await _gate.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // sweep already running; caller should return 409
        }

        try
        {
            // ReSharper disable once PossiblyMistakenUseOfCancellationToken
            return await SweepAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunAsync(bool skipIfBusy, CancellationToken ct)
    {
        if (skipIfBusy && !await _gate.WaitAsync(0, ct))
        {
            return;
        }

        if (!skipIfBusy)
        {
            await _gate.WaitAsync(ct);
        }

        try
        {
            await SweepAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.SweepFailed(_logger, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RetentionRunResult> SweepAsync(CancellationToken ct)
    {
        bool enabled = _config.GetValue<bool?>("Retention:Enabled") ?? true;
        if (!enabled)
        {
            Log.SweepDisabled(_logger);
            return new RetentionRunResult(DateTimeOffset.UtcNow, TimeSpan.Zero, []);
        }

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        Log.SweepStarted(_logger);

        List<TablePruneResult> results = [];

        // Load policies with a short-lived connection; close it before the per-table work.
        List<(string TableName, string TimeColumn, TimeSpan? StaleAfter)> policies = [];
        await using (NpgsqlConnection loadConn = await _db.OpenConnectionAsync(ct))
        {
            await foreach ((string TableName, string TimeColumn, TimeSpan? StaleAfter) policy in loadConn
                .ListRetentionPoliciesAsync(ct))
            {
                policies.Add(policy);
            }
        }

        foreach ((string tableName, string timeColumn, TimeSpan? staleAfter) in policies)
        {
            if (staleAfter is null) { continue; }

            if (!SafeIdentifier().IsMatch(tableName) || !SafeIdentifier().IsMatch(timeColumn))
            {
                Log.UnsafeIdentifier(_logger, tableName, timeColumn);
                continue;
            }

            // Open a fresh connection per table so the 50ms inter-batch delays don't
            // hold a pooled connection idle across the entire sweep.
            long deleted;
            await using (NpgsqlConnection conn = await _db.OpenConnectionAsync(ct))
            {
                deleted = await PruneTableAsync(conn, tableName, timeColumn, staleAfter.Value, ct);

                if (deleted > 0)
                {
                    await conn.InsertAuditLogAsync(
                            "system",
                            "retention.prune",
                            tableName,
                            JsonSerializer.SerializeToElement(
                                new
                                {
                                    table = tableName,
                                    rows_deleted = deleted,
                                    threshold = staleAfter.ToString(),
                                }
                            ),
                            ct
                        )
                        .ExecuteAsync(ct);
                }
            }

            if (deleted == 0)
            {
                continue;
            }

            results.Add(new TablePruneResult(tableName, deleted));
            Log.TablePruned(_logger, tableName, deleted);
        }

        TimeSpan elapsed = DateTimeOffset.UtcNow - startedAt;
        Log.SweepCompleted(_logger, results.Count, elapsed);

        return new RetentionRunResult(startedAt, elapsed, results);
    }

    private static async Task<long> PruneTableAsync(
        NpgsqlConnection conn,
        string tableName,
        string timeColumn,
        TimeSpan staleAfter,
        CancellationToken ct
    )
    {
        long totalDeleted = 0;

        while (true)
        {
            await using NpgsqlCommand cmd = conn.CreateCommand();
            // Batched delete via ctid to bound transaction size and lock hold time.
            // The subquery selects a page of qualifying ctids; the outer DELETE removes them.
            cmd.CommandText = $"""
                DELETE FROM {tableName}
                WHERE ctid IN (
                    SELECT ctid FROM {tableName}
                    WHERE {timeColumn} < now() - $1
                    LIMIT {BatchSize}
                )
                """;
            cmd.Parameters.Add(Param.Interval(staleAfter));

            long rows = await cmd.ExecuteNonQueryAsync(ct);
            totalDeleted += rows;

            if (rows < BatchSize)
            {
                break;
            }

            // Brief pause between large batches to yield to normal traffic.
            await Task.Delay(50, ct);
        }

        return totalDeleted;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Retention sweep started.")]
        internal static partial void SweepStarted(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Retention sweep disabled via Retention:Enabled config."
        )]
        internal static partial void SweepDisabled(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Retention sweep complete: {TableCount} tables pruned in {Elapsed}."
        )]
        internal static partial void SweepCompleted(ILogger logger, int tableCount, TimeSpan elapsed);

        [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {RowsDeleted} rows from {TableName}.")]
        internal static partial void TablePruned(ILogger logger, string tableName, long rowsDeleted);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message =
                "Skipping retention_policies row with unsafe identifier: table='{TableName}' column='{TimeColumn}'."
        )]
        internal static partial void UnsafeIdentifier(ILogger logger, string tableName, string timeColumn);

        [LoggerMessage(Level = LogLevel.Error, Message = "Retention sweep failed.")]
        internal static partial void SweepFailed(ILogger logger, Exception ex);
    }
}