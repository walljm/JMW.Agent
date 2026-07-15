using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Incidents;

/// <summary>
/// Agent-offline is silence-driven — it becomes true through the ABSENCE of a heartbeat, so
/// there's no incoming fact for IncidentEvaluator to evaluate against. This periodic sweep fills
/// that gap, same shape as ReleaseRescanService/RetentionService: reread agents' heartbeat-derived
/// liveness (agent_liveness(), shared with the Fleet Dashboard's Agent Health panel) and open/
/// resolve the "agent_offline" incident per agent. 'stale' liveness is a dead zone — matches
/// IncidentTypeDef's hysteresis pattern — so an agent doesn't flap the incident open/closed while
/// merely running late on one heartbeat.
/// Device-offline (a device that stops reporting at all, as opposed to a specific agent) is
/// deliberately NOT covered here — devices have no per-entity heartbeat/interval to derive a
/// threshold from the way agents do, so it needs its own design pass. Flagged as an open
/// sequencing question in the design doc this feature implements.
/// </summary>
public sealed partial class AgentLivenessSweepService : BackgroundService
{
    private const string IncidentType = "agent_offline";
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly NpgsqlDataSource _db;
    private readonly ILogger<AgentLivenessSweepService> _logger;

    public AgentLivenessSweepService(NpgsqlDataSource db, ILogger<AgentLivenessSweepService> logger)
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

            // Materialize before writing — a second command can't open on this connection while
            // ListAgentLivenessAsync's reader is still streaming (see AGENTS.md's DB access rules).
            List<(Guid AgentId, string? Liveness)> agents = [];
            await foreach ((Guid agentId, string? liveness) in conn.ListAgentLivenessAsync(ct))
            {
                agents.Add((agentId, liveness));
            }

            foreach ((Guid agentId, string? liveness) in agents)
            {
                string entityId = agentId.ToString();
                if (liveness is "offline" or null)
                {
                    await conn.OpenOrTouchIncidentAsync(
                            "agent",
                            entityId,
                            IncidentType,
                            detail: null,
                            IncidentTypeRegistry.AvailabilityReopenWindow.TotalSeconds,
                            ct
                        )
                        .ExecuteAsync(ct);
                }
                else if (liveness == "online")
                {
                    await conn.ResolveIncidentAsync("agent", entityId, IncidentType, ct).ExecuteAsync(ct);
                }

                // 'stale' — dead zone, no action (matches the filesystem_full hysteresis pattern).
            }
        }
        catch (Exception ex)
        {
            Log.SweepFailed(_logger, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Agent liveness sweep failed.")]
        public static partial void SweepFailed(ILogger logger, Exception ex);
    }
}
