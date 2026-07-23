using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;
using JMW.Discovery.Server.Incidents;
using JMW.Discovery.Server.Projections;

namespace JMW.Discovery.Server;

/// <summary>
/// Coordinates fact ingestion: normalize (via the AnalysisEngine) → history append + projection
/// updates. Agents emit raw facts; the server is the authoritative normalization point.
/// Device→server affinity requirement:
/// GenericProjection's EntityStateCache is per-instance. For the cache to
/// be coherent, all facts for a given device must always reach the same
/// server instance. Enforce this at the load balancer (hash on DeviceId)
/// or accept that the SQL WHERE guard on projections will catch missed
/// cache entries — at the cost of more heap reads on projection rows.
/// If you cannot enforce affinity (e.g. k8s with rolling restarts),
/// disable the EntityStateCache (set maxCacheEntries: 0) and rely entirely
/// on the DB-level WHERE guard. Correct, but ~6 buffer reads per projection
/// row per cycle instead of ~0.
/// Route tables and other high-cardinality children:
/// A device with 100K route entries sends a 500K-fact batch. This is handled
/// by the same pipeline but needs chunking at ~10K facts per DB round-trip
/// (already done in FactRepository). The projection for route entries may
/// have 80K × 100K = 8B rows over all devices — do NOT project route tables
/// the same way as interfaces. Options:
/// a) Store routes only in facts_history; query history directly for routing views.
/// b) Project only summary facts (route count, default route presence, etc.).
/// c) Use a time-series DB for route facts; Postgres for everything else.
/// </summary>
public sealed class FactIngestPipeline
{
    private readonly ProjectionRouter _router;
    private readonly FactRepository _repo;
    private readonly AnalysisEngine _analysis;
    private readonly IncidentEvaluator _incidents;

    public FactIngestPipeline(
        FactRepository repo,
        ProjectionRouter router,
        AnalysisEngine analysis,
        IncidentEvaluator incidents
    )
    {
        _repo = repo;
        _router = router;
        _analysis = analysis;
        _incidents = incidents;
    }

    // Maximum facts accepted in a single batch.
    // A normal per-device batch is 1 000–5 000 facts; 50 000 is generous for
    // high-cardinality devices (route tables, containers) while still bounding
    // the damage a compromised device can cause (held DB connections, memory).
    // Fact IDs and segment count are bounded in FactSegment.ParsePath, which
    // fires during deserialization before IngestAsync is reached.
    public const int MaxFactsPerBatch = 50_000;

    private static readonly IReadOnlySet<string> EmptyTableNames = new HashSet<string>();

    /// <exception cref="ArgumentOutOfRangeException">
    /// Batch exceeds <see cref="MaxFactsPerBatch" /> facts.
    /// </exception>
    public async Task<IReadOnlySet<string>> IngestAsync(IReadOnlyList<Fact> facts, CancellationToken ct = default)
    {
        if (facts.Count == 0)
        {
            return EmptyTableNames;
        }

        if (facts.Count > MaxFactsPerBatch)
        {
            throw new ArgumentOutOfRangeException(
                nameof(facts),
                $"Batch of {facts.Count} facts exceeds maximum of {MaxFactsPerBatch}. "
              + "Split large collections (e.g. route tables) into separate batches."
            );
        }

        // Canonicalize dimension KEYS (MAC/IP) first — the value normalizers below only touch fact
        // values, not the keys embedded in the fact id (DHCP lease MAC, ARP/discovered IP).
        List<Fact> keyed = new(facts.Count);
        foreach (Fact fact in facts)
        {
            keyed.Add(KeyNormalization.Normalize(fact));
        }

        // Hydrate derivation inputs from current state so a partial batch is derived against
        // everything currently true, not just what changed this cycle (docs/plans/
        // architecture-identity-facts.md §11). Without this, a batch carrying only a low-priority
        // vendor source (because that's the one that changed; delta-tracking omits the unchanged
        // high-priority source) makes a priority fan-in derivation see the high-priority source as
        // absent and clobber the correct stored value. Scoped to Device-level raw inputs only.
        IReadOnlyList<Fact> hydrated = await HydrateInputsAsync(keyed, ct);

        // Normalize + derive on the server, before anything is stored — agents now emit raw facts
        // (they no longer run the AnalysisEngine). This is the authoritative normalization point for
        // EVERY ingest source, not just agent-emitted facts. Runs per batch, matching the grouping the
        // agent used to analyze.
        IReadOnlyList<Fact> analyzed = _analysis.Analyze(keyed, hydrated);
        if (analyzed.Count == 0)
        {
            return EmptyTableNames;
        }

        Task appendTask = _repo.AppendAsync(analyzed, ct);
        Task<IReadOnlySet<string>> routeTask = _router.RouteAsync(analyzed, ct);
        Task incidentTask = _incidents.EvaluateAsync(analyzed, ct);
        await Task.WhenAll(appendTask, routeTask, incidentTask);
        return await routeTask;
    }

    /// <summary>
    /// Reads the current value of the engine's hydratable input paths (Device- and
    /// Discovered-scoped — <see cref="AnalysisEngine.HydratableInputPaths" />) for every device
    /// present in the batch, so <see cref="AnalysisEngine.Analyze(IReadOnlyList{Fact}, IReadOnlyList{Fact})" />
    /// can derive against full current state. Discovered-scoped rows hydrate under the observer
    /// device's key with the station re-keyed from entity_key, so absence-guarded gap-fill
    /// derivations see already-observed values a delta-tracked batch omitted. Returns empty when
    /// the engine has no hydratable paths or the batch touches no device (e.g. a service-only batch).
    /// </summary>
    private async Task<IReadOnlyList<Fact>> HydrateInputsAsync(IReadOnlyList<Fact> keyed, CancellationToken ct)
    {
        IReadOnlySet<string> paths = _analysis.HydratableInputPaths;
        if (paths.Count == 0)
        {
            return [];
        }

        HashSet<string> devices = new(StringComparer.Ordinal);
        foreach (Fact fact in keyed)
        {
            // Only facts carrying a Device dimension can hydrate a Device-scoped input.
            if (!fact.DimKey.StartsWith("Device", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (FactSegment seg in fact.ParseId())
            {
                if (seg is { IsList: true, Name: "Device", Key: { Length: > 0 } key })
                {
                    devices.Add(key);
                    break;
                }
            }
        }

        return devices.Count == 0 ? [] : await _repo.HydrateInputsAsync(devices, paths, ct);
    }
}