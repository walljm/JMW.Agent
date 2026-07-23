using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Incidents;

/// <summary>
/// service_down has no single unambiguous fact path across service types (DNS/HA/CA each expose
/// different signals — see IncidentTypeRegistry's remarks) — a service collector that can't reach
/// its target today just emits nothing, indistinguishable from "briefly not polled this cycle", so
/// there's no ingest-time value change to hook an IncidentEvaluator off. This periodic sweep fills
/// that gap the same way AgentLivenessSweepService/CertExpiringSweepService do, using whichever
/// signal a service type actually has: an explicit ca_status for CA services, staleness of
/// proj_services.updated_at for everything else (the one thing every service type touches on a
/// successful poll, regardless of type).
/// </summary>
public sealed partial class ServiceDownSweepService : BackgroundService
{
    private const string IncidentType = "service_down";
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    // No successful poll in over an hour is a reasonable "this has gone quiet" signal for a
    // home-network service expected to report every collection cycle (minutes, not hours).
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    private readonly NpgsqlDataSource _db;
    private readonly ILogger<ServiceDownSweepService> _logger;

    public ServiceDownSweepService(NpgsqlDataSource db, ILogger<ServiceDownSweepService> logger)
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
            DateTimeOffset staleBefore = DateTimeOffset.UtcNow - StaleAfter;

            // Materialize before writing — a second command can't open on this connection while
            // ListServiceHealthAsync's reader is still streaming (see AGENTS.md's DB access rules).
            List<(string Service, string? Type, string? CaStatus, DateTimeOffset UpdatedAt)> services = [];
            await foreach ((string service, string? type, string? caStatus, DateTimeOffset updatedAt) in
                conn.ListServiceHealthAsync(ct))
            {
                services.Add((service, type, caStatus, updatedAt));
            }

            foreach ((string service, string? _, string? caStatus, DateTimeOffset updatedAt) in services)
            {
                bool down;
                string? detail;
                if (caStatus is { Length: > 0 })
                {
                    down = !string.Equals(caStatus, "running", StringComparison.OrdinalIgnoreCase);
                    detail = down ? $"ca_status: {caStatus}" : null;
                }
                else
                {
                    down = updatedAt < staleBefore;
                    detail = down ? "no update in over an hour" : null;
                }

                if (down)
                {
                    await conn.OpenOrTouchIncidentAsync(
                            "service",
                            service,
                            IncidentType,
                            detail,
                            IncidentTypeRegistry.DefaultReopenWindow.TotalSeconds,
                            ct
                        )
                        .ExecuteAsync(ct);
                }
                else
                {
                    await conn.ResolveIncidentAsync("service", service, IncidentType, ct).ExecuteAsync(ct);
                }
            }
        }
        catch (Exception ex)
        {
            Log.SweepFailed(_logger, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Service-down sweep failed.")]
        public static partial void SweepFailed(ILogger logger, Exception ex);
    }
}