using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Incidents;

/// <summary>
/// Fingerprint conflicts already behave exactly like an incident — open until an admin merges or
/// excludes it — they just weren't named that (docs: "From Noise to Signal" design proposal, §02).
/// There's no single call site in DeviceRegistry where a conflict is "created" (it's an emergent
/// property of the device_fingerprints table, the same query ConflictsApi's admin listing already
/// runs), so this periodic sweep is the open/keep-open side; ConflictsApi's merge/exclude actions
/// resolve immediately (resolution='manual') rather than waiting for the next sweep tick, and this
/// sweep is the fallback that catches a conflict resolving itself (e.g. one of the two devices
/// gets deleted) without going through ConflictsApi at all.
/// entity_kind is 'fingerprint', not 'device' — a conflict is a property of the (fp_type, fp_value)
/// pair itself, not owned by either conflicting device.
/// </summary>
public sealed partial class FingerprintConflictSweepService : BackgroundService
{
    private const string IncidentType = "fingerprint_conflict";
    private const string EntityKind = "fingerprint";
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly NpgsqlDataSource _db;
    private readonly ILogger<FingerprintConflictSweepService> _logger;

    public FingerprintConflictSweepService(NpgsqlDataSource db, ILogger<FingerprintConflictSweepService> logger)
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

    private static string EntityId(string fpType, string fpValue) => $"{fpType}:{fpValue}";

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        try
        {
            await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);

            // Materialize both reads before writing — a second command can't open on this
            // connection while a reader is still streaming (see AGENTS.md's DB access rules).
            List<(string FpType, string FpValue)> conflicts = [];
            await foreach ((string fpType, string fpValue) in conn.ListOpenFingerprintConflictsAsync(ct))
            {
                conflicts.Add((fpType, fpValue));
            }

            List<string> openEntityIds = [];
            await foreach (EntityIdResult row in conn.ListOpenIncidentEntityIdsAsync(EntityKind, IncidentType, ct))
            {
                openEntityIds.Add(row.EntityId);
            }

            HashSet<string> stillConflicting = new(StringComparer.Ordinal);
            foreach ((string fpType, string fpValue) in conflicts)
            {
                string entityId = EntityId(fpType, fpValue);
                stillConflicting.Add(entityId);
                await conn.OpenOrTouchIncidentAsync(
                        EntityKind,
                        entityId,
                        IncidentType,
                        $"{fpType}: multiple devices share this fingerprint",
                        IncidentTypeRegistry.DefaultReopenWindow.TotalSeconds,
                        ct
                    )
                    .ExecuteAsync(ct);
            }

            foreach (string openEntityId in openEntityIds)
            {
                if (!stillConflicting.Contains(openEntityId))
                {
                    await conn.ResolveIncidentAsync(EntityKind, openEntityId, IncidentType, ct).ExecuteAsync(ct);
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
        [LoggerMessage(Level = LogLevel.Warning, Message = "Fingerprint conflict sweep failed.")]
        public static partial void SweepFailed(ILogger logger, Exception ex);
    }
}