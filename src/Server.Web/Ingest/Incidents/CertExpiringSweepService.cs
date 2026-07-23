using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Incidents;

/// <summary>
/// cert_expiring is threshold-driven by the passage of time ("expires within 30 days" crosses
/// its threshold as the clock advances), not by an incoming fact value changing, so it needs a
/// periodic sweep rather than an IncidentEvaluator hook — same shape as AgentLivenessSweepService.
/// Reuses ListCaServicesAsync (Terrain's CA inventory query) and the same 30-day threshold as the
/// CA Expiry badges on Device/Service Detail and the Terrain CA tables.
/// </summary>
public sealed partial class CertExpiringSweepService : BackgroundService
{
    private const string IncidentType = "cert_expiring";
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);
    private const int ExpiringWithinDays = 30;

    private readonly NpgsqlDataSource _db;
    private readonly ILogger<CertExpiringSweepService> _logger;

    public CertExpiringSweepService(NpgsqlDataSource db, ILogger<CertExpiringSweepService> logger)
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
            DateTimeOffset horizon = DateTimeOffset.UtcNow.AddDays(ExpiringWithinDays);

            // Materialize before writing — a second command can't open on this connection while
            // ListCaServicesAsync's reader is still streaming (see AGENTS.md's DB access rules).
            List<(string ServiceRef, DateTimeOffset? RootNotAfter, DateTimeOffset? IntNotAfter)> cas = [];
            await foreach ((string serviceRef, _, _, _, _, _, DateTimeOffset? rootNotAfter, _, _, _,
                DateTimeOffset? intNotAfter, _) in conn.ListCaServicesAsync(null, ct))
            {
                cas.Add((serviceRef, rootNotAfter, intNotAfter));
            }

            foreach ((string serviceRef, DateTimeOffset? rootNotAfter, DateTimeOffset? intNotAfter) in cas)
            {
                bool rootExpiring = rootNotAfter is { } r && r <= horizon;
                bool intExpiring = intNotAfter is { } i && i <= horizon;

                if (rootExpiring || intExpiring)
                {
                    DateTimeOffset soonest = rootExpiring && intExpiring
                        ? (rootNotAfter!.Value < intNotAfter!.Value ? rootNotAfter.Value : intNotAfter.Value)
                        : rootExpiring
                            ? rootNotAfter!.Value
                            : intNotAfter!.Value;
                    string which = rootExpiring && (!intExpiring || rootNotAfter!.Value <= intNotAfter!.Value)
                        ? "root"
                        : "intermediate";

                    await conn.OpenOrTouchIncidentAsync(
                            "service",
                            serviceRef,
                            IncidentType,
                            detail: $"{which} cert expires {soonest.UtcDateTime:yyyy-MM-dd}",
                            IncidentTypeRegistry.DefaultReopenWindow.TotalSeconds,
                            ct
                        )
                        .ExecuteAsync(ct);
                }
                else
                {
                    await conn.ResolveIncidentAsync("service", serviceRef, IncidentType, ct).ExecuteAsync(ct);
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
        [LoggerMessage(Level = LogLevel.Warning, Message = "Cert-expiring sweep failed.")]
        public static partial void SweepFailed(ILogger logger, Exception ex);
    }
}