using JMW.Discovery.Core;
using JMW.Discovery.Server.Projections;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Ingest.Context;

/// <summary>
/// Runs the registered <see cref="IContextDerivation" />s (docs/plans/context-derivations.md
/// §3.1). Invoked after each ingest batch's materializer pass, gated per derivation on
/// (batch touched-tables ∩ TriggerTables) plus a debounce; <see cref="RunAllAsync" /> runs the
/// full set unconditionally (startup bootstrap).
///
/// Suppression is layered three deep, all existing machinery: this engine's per-derivation
/// <see cref="EntityStateCache" /> (warmed by the startup full pass) drops unchanged values
/// before they enter the pipeline; <c>FactRepository.AppendAsync</c>'s dedup-on-write absorbs
/// anything that slips through into history; and proj_devices' own GenericProjection cache +
/// ON CONFLICT WHERE guard suppress no-op projection writes. A steady-state pass where
/// nothing changed costs one set query per due derivation and zero writes.
///
/// Overlapping invocations are skipped, not queued (TryWait(0)): recompute-all is self-healing,
/// so the next gated batch picks up whatever a skipped pass would have found.
/// </summary>
public sealed partial class ContextDerivationEngine : IDisposable
{
    private const int MaxCacheEntries = 500_000;

    private readonly NpgsqlDataSource _db;
    private readonly FactRepository _facts;
    private readonly ProjectionRouter _router;
    private readonly IReadOnlyList<IContextDerivation> _derivations;
    private readonly ILogger<ContextDerivationEngine> _logger;
    private readonly Dictionary<string, DerivationState> _state;
    private readonly SemaphoreSlim _gate;

    public ContextDerivationEngine(
        NpgsqlDataSource db,
        FactRepository facts,
        ProjectionRouter router,
        IReadOnlyList<IContextDerivation> derivations,
        ILogger<ContextDerivationEngine> logger
    )
    {
        _db = db;
        _facts = facts;
        _router = router;
        _derivations = derivations;
        _logger = logger;
        _gate = new SemaphoreSlim(1, 1);
        _state = new Dictionary<string, DerivationState>(StringComparer.Ordinal);
        foreach (IContextDerivation d in derivations)
        {
            _state.Add(d.Name, new DerivationState());
        }
    }

    /// <summary>Runs every derivation whose trigger tables overlap the batch's touched tables
    /// and whose debounce interval has elapsed. Skips entirely if a pass is already running.</summary>
    public async Task RunDueAsync(IReadOnlySet<string> touchedTables, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            return;
        }

        try
        {
            foreach (IContextDerivation d in _derivations)
            {
                DerivationState state = _state[d.Name];
                if (!touchedTables.Overlaps(d.TriggerTables))
                {
                    continue;
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (now - state.LastRun < d.MinInterval)
                {
                    continue;
                }

                await RunOneAsync(d, state, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs every derivation unconditionally — the startup bootstrap pass. The first
    /// pass doubles as the cache warm-up: every resolved value flows through Filter (populating
    /// the cache) and downstream dedup absorbs the re-emits of already-stored values.</summary>
    public async Task RunAllAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureRowsAsync(ct);
            foreach (IContextDerivation d in _derivations)
            {
                await RunOneAsync(d, _state[d.Name], ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Backfills a bare proj_devices row for any device missing one, so every device is
    /// reachable in the driving-table index walk reports use (docs/plans/context-derivations.md
    /// §3.3). New devices get their row at creation (DeviceRegistry.CreateDeviceAsync); this
    /// covers devices that predate that write. Same hand-write-into-a-projection precedent as
    /// proj_systems.last_seen_ip.
    /// </summary>
    private async Task EnsureRowsAsync(CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO proj_devices (device)
            SELECT device_id::text FROM devices
            ON CONFLICT (device) DO NOTHING
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task RunOneAsync(IContextDerivation d, DerivationState state, CancellationToken ct)
    {
        state.LastRun = DateTimeOffset.UtcNow;
        try
        {
            List<ResolvedContextRow> resolved;
            await using (NpgsqlConnection conn = await _db.OpenConnectionAsync(ct))
            {
                resolved = await d.ResolveAsync(conn, ct).ToListAsync(ct);
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            List<(string[] DimKeys, Dictionary<string, FactValue> Attrs, DateTimeOffset UpdatedAt, Guid? AgentId)>
                entities = new(resolved.Count);
            foreach (ResolvedContextRow row in resolved)
            {
                if (string.IsNullOrWhiteSpace(row.Device) || string.IsNullOrWhiteSpace(row.Value))
                {
                    continue;
                }

                entities.Add(
                    (
                        [row.Device],
                        new Dictionary<string, FactValue>(1) { [d.OutputPath] = FactValue.FromString(row.Value) },
                        now,
                        null
                    )
                );
            }

            List<(string[] DimKeys, Dictionary<string, FactValue> Attrs, DateTimeOffset UpdatedAt, Guid? AgentId)>
                changed = state.Cache.Filter(entities);
            if (changed.Count == 0)
            {
                return;
            }

            List<Fact> facts = new(changed.Count);
            foreach ((string[] dimKeys, Dictionary<string, FactValue> attrs, _, _) in changed)
            {
                if (attrs[d.OutputPath].AsString() is not string value)
                {
                    continue;
                }

                string id = d.OutputPath.Replace("Device[]", $"Device[{dimKeys[0]}]", StringComparison.Ordinal);
                facts.Add(
                    Fact.Create(id, value, now) with
                    {
                        Source = FactSource.ContextDerivation,
                        SourceName = d.Name,
                    }
                );
            }

            if (facts.Count == 0)
            {
                return;
            }

            await _facts.AppendAsync(facts, ct);
            await _router.RouteAsync(facts, ct);
            Log.Resolved(_logger, d.Name, facts.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One derivation's failure must not block the others (same per-unit isolation the
            // materializer uses) — recompute-all self-heals on the next gated pass.
            Log.RunFailed(_logger, d.Name, ex);
        }
    }

    public void Dispose() => _gate.Dispose();

    private sealed class DerivationState
    {
        public DateTimeOffset LastRun;
        public EntityStateCache Cache { get; } = new(MaxCacheEntries);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Context derivation {Name} resolved {Count} changed values.")]
        internal static partial void Resolved(ILogger logger, string name, int count);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Context derivation {Name} pass failed; values self-heal on the next gated pass."
        )]
        internal static partial void RunFailed(ILogger logger, string name, Exception ex);
    }
}