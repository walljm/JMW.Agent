using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Infrastructure;

/// <summary>
/// Periodically forces a full re-collect on agents whose delta cache has gone stale relative to
/// server-side retention. The agent only re-sends facts that CHANGED; the server prunes 'steady'
/// current-state on its own clock. Without a nudge, a stable device's projection row can be pruned
/// while the agent still believes it already reported it — a permanent hole until the value changes
/// or an admin clears the cache. This sweep is that nudge, automated: it sets
/// clear_trackers_requested_at (the same signal the admin "clear cache" button uses), which makes
/// the agent drop its delta cache and re-send full state next cycle, refilling anything pruned.
///
/// Cadence and due-selection live in GetAgentsDueForRecollect.sql: due = last cleared more than
/// ¼-of-shortest-'steady'-retention ago (so refills always beat the prune, and the cadence follows
/// the retention automatically). Oldest-first with a per-sweep cap staggers the fleet instead of
/// re-collecting everyone at once. Mirrors AgentLivenessSweepService/RetentionService in shape.
/// </summary>
public sealed partial class DeviceRecollectSweepService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(30);

    // Max agents nudged per sweep. Oldest-first, so the fleet spreads across sweeps rather than
    // re-collecting in one herd. At 30-min sweeps this covers a large fleet well inside a weekly
    // cadence; raise it if a very large fleet can't keep up (a backlog just delays a refill, never
    // drops data).
    private const int MaxPerSweep = 4;

    private readonly NpgsqlDataSource _db;
    private readonly ILogger<DeviceRecollectSweepService> _logger;

    public DeviceRecollectSweepService(NpgsqlDataSource db, ILogger<DeviceRecollectSweepService> logger)
    {
        _db = db;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(SweepInterval);
        await SweepOnceAsync(stoppingToken);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SweepOnceAsync(stoppingToken);
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        try
        {
            await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);

            // Materialize the due list before issuing any request — a second command can't open on
            // this connection while the reader is still streaming (AGENTS.md DB access rules).
            List<Guid> due = [];
            await foreach (AgentIdResult row in conn.GetAgentsDueForRecollectAsync(MaxPerSweep, ct))
            {
                due.Add(row.AgentId);
            }

            foreach (Guid agentId in due)
            {
                await conn.RequestClearTrackersAsync(agentId, ct).ExecuteAsync(ct);
            }

            if (due.Count > 0)
            {
                Log.Requested(_logger, due.Count);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.SweepFailed(_logger, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Forced re-collect requested for {Count} agent(s).")]
        public static partial void Requested(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Device re-collect sweep failed.")]
        public static partial void SweepFailed(ILogger logger, Exception ex);
    }
}