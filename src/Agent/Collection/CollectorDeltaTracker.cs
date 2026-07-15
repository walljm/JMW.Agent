using System.Text.Json;

using JMW.Discovery.Core;

namespace JMW.Discovery.Agent.Collection;

/// <summary>
/// Tracks the last-sent value per fact on the collector side.
/// Filters a raw fact list down to only facts that changed since the last
/// successful send. This is the PRIMARY dedup mechanism in the system —
/// the server's DB-level dedup is a safety net, not the main guard.
/// Lives on the collector, not the server:
/// - One instance per device (or per collector worker handling N devices).
/// - At 1000 facts per device, memory cost is trivial (~150KB per device).
/// - No coherence concerns: a device is collected by exactly one collector
/// instance at a time, so there is no concurrent writer to this cache.
/// - Persisted to disk between restarts (see <see cref="LoadOrCreate" />/<see cref="Save" />)
/// so the first post-restart cycle doesn't send the full device state (only genuine changes).
/// Route tables and other high-cardinality child tables:
/// The same mechanism applies but with more entries. A device with 100K
/// route entries × 5 attrs = 500K facts. At 154 bytes/entry ≈ 77MB per
/// device — feasible on a collector that handles a handful of such devices,
/// but not if one collector handles hundreds. Partition high-cardinality
/// devices to dedicated collectors or use a separate route-collection path.
/// </summary>
public sealed class CollectorDeltaTracker
{
    private readonly Dictionary<string, long> _lastSent;

    public CollectorDeltaTracker() : this(new Dictionary<string, long>()) { }

    private CollectorDeltaTracker(Dictionary<string, long> lastSent)
    {
        _lastSent = lastSent;
    }

    /// <summary>
    /// Loads persisted state from <paramref name="path" />, or returns an empty tracker if the
    /// file is missing or unreadable/corrupt (a fresh tracker just re-sends full state on the
    /// next cycle, which is always safe — never let a bad state file block startup).
    /// </summary>
    public static CollectorDeltaTracker LoadOrCreate(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new CollectorDeltaTracker();
            }

            using FileStream stream = File.OpenRead(path);
            Dictionary<string, long>? loaded = JsonSerializer.Deserialize<Dictionary<string, long>>(stream);
            return new CollectorDeltaTracker(loaded ?? new Dictionary<string, long>());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new CollectorDeltaTracker();
        }
    }

    /// <summary>
    /// Persists current state to <paramref name="path" />, writing to a temp file and moving it
    /// into place so a crash mid-write can never leave a corrupt/partial state file behind.
    /// </summary>
    public void Save(string path)
    {
        string tempPath = $"{path}.tmp-{Environment.ProcessId}";
        try
        {
            using (FileStream stream = File.Create(tempPath))
            {
                JsonSerializer.Serialize(stream, _lastSent);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (IOException)
        {
            // Best-effort — losing this write just means the next restart re-sends full state.
            File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Returns only facts whose value changed since the last call.
    /// Updates internal state for returned facts — on successful send, no
    /// further action needed. On failed send, call <see cref="Rollback" />.
    /// </summary>
    public IReadOnlyList<Fact> FilterChanged(IEnumerable<Fact> facts)
    {
        List<Fact> changed = new();
        List<(string Id, long? PrevHash)> pending = new();

        foreach (Fact fact in facts)
        {
            long hash = ValueHash(fact.Value);
            bool had = _lastSent.TryGetValue(fact.Id, out long prev);
            if (!had || prev != hash)
            {
                changed.Add(fact);
                // Remember the OLD hash (or null if the key was absent) so Rollback
                // can restore it exactly rather than just deleting the key.
                pending.Add((fact.Id, had ? prev : null));
                _lastSent[fact.Id] = hash;
            }
        }

        // Optimistically update state. Call Rollback() if the send fails.
        _pendingRollback = pending;
        return changed;
    }

    private List<(string Id, long? PrevHash)>? _pendingRollback;

    /// <summary>
    /// Reverts the state updated by the last <see cref="FilterChanged" /> call.
    /// Call this when the server returns an error so the next cycle re-sends
    /// the facts that were not confirmed.
    /// </summary>
    public void Rollback()
    {
        if (_pendingRollback is null)
        {
            return;
        }

        foreach ((string id, long? prevHash) in _pendingRollback)
        {
            // Restore the previous state exactly: if the key existed before this
            // cycle's FilterChanged call, put its old hash back; if it was a new
            // key, remove it entirely so it looks unsent to the next cycle.
            if (prevHash.HasValue)
            {
                _lastSent[id] = prevHash.Value;
            }
            else
            {
                _lastSent.Remove(id);
            }
        }

        _pendingRollback = null;
    }

    /// <summary>Total facts tracked (memory footprint indicator).</summary>
    public int TrackedCount => _lastSent.Count;

    // FNV-1a-64 over "{Kind}{canonical value}". Must be deterministic across process
    // restarts (unlike the default object.GetHashCode()/HashCode.Combine, which is seeded
    // per-process for hash-flood resistance and is explicitly documented as unsuitable for
    // persistence) since this hash is what LoadOrCreate/Save round-trip to disk.
    private static long ValueHash(FactValue v)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;

        ulong hash = offsetBasis;
        hash = Step(hash, (byte)v.Kind);

        string? canonical = v.ToString();
        if (canonical is not null)
        {
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(canonical))
            {
                hash = Step(hash, b);
            }
        }

        return unchecked((long)hash);

        static ulong Step(ulong h, byte b) => (h ^ b) * prime;
    }
}
