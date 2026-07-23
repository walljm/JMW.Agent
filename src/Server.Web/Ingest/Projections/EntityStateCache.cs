using System.Collections.Concurrent;

using JMW.Discovery.Core;

namespace JMW.Discovery.Server.Projections;

/// <summary>
/// Change-suppression cache over per-entity attribute state. Tracks the full current state of
/// each entity (all tracked columns/attributes); when a batch arrives, attributes not in the
/// batch are merged from the cached state so change detection is always over the full row,
/// not just the partial batch. Extracted from <see cref="GenericProjection" /> (where it filters
/// no-op projection writes) so <see cref="Ingest.Context.ContextDerivationEngine" /> can reuse
/// the identical mechanism to suppress unchanged resolved values before they enter the fact
/// pipeline at all (docs/plans/context-derivations.md §3.1).
///
/// Memory: each entry stores N <see cref="FactValue" /> structs (32 bytes each) plus a string
/// key. At 5 columns, roughly 230 bytes per entity. Default 500K cap = ~115 MB. Entities beyond
/// the cap pass through <see cref="Filter" /> unfiltered — callers must have a downstream
/// correctness guard (GenericProjection's SQL WHERE guard / facts_history dedup-on-write).
/// </summary>
internal sealed class EntityStateCache
{
    private readonly int _maxEntries;

    public EntityStateCache(int maxEntries)
    {
        _maxEntries = maxEntries;
    }

    private readonly ConcurrentDictionary<string, EntityState> _state = new();
    private int _count;

    public List<(string[] DimKeys, Dictionary<string, FactValue> Attrs, DateTimeOffset UpdatedAt, Guid? AgentId)>
        Filter(
            List<(string[] DimKeys, Dictionary<string, FactValue> Attrs, DateTimeOffset UpdatedAt, Guid? AgentId)>
                entities
        )
    {
        List<(string[], Dictionary<string, FactValue>, DateTimeOffset, Guid?)> changed = new(entities.Count);

        foreach ((string[] dimKeys, Dictionary<string, FactValue> batchAttrs, DateTimeOffset updatedAt, Guid?
            agentId) in entities)
        {
            string key = string.Join('\0', dimKeys);

            if (!_state.TryGetValue(key, out EntityState? state))
            {
                // Cache full: pass through and let the caller's downstream guard decide
                if (_count >= _maxEntries)
                {
                    changed.Add((dimKeys, batchAttrs, updatedAt, agentId));
                    continue;
                }

                state = new EntityState();
                if (_state.TryAdd(key, state))
                {
                    Interlocked.Increment(ref _count);
                }
                else
                {
                    _state.TryGetValue(key, out state); // lost the race, use winner's state
                }
            }

            // Merge batch into full current state; detect any change
            bool hasChange = false;
            lock (state
             ?? throw new InvalidOperationException("Entity state unexpectedly null after race resolution."))
            {
                foreach ((string attr, FactValue val) in batchAttrs)
                {
                    if (!state.Values.TryGetValue(attr, out FactValue prev) || prev != val)
                    {
                        hasChange = true;
                        state.Values[attr] = val;
                    }
                }
            }

            if (hasChange)
            {
                changed.Add((dimKeys, batchAttrs, updatedAt, agentId));
            }
        }

        return changed;
    }

    /// <summary>
    /// Injects a known-current state during warm-up. Does not trigger change
    /// detection — these values are what the DB already has.
    /// </summary>
    public void Seed(string[] dimKeys, Dictionary<string, FactValue> attrs)
    {
        string key = string.Join('\0', dimKeys);
        if (_state.TryGetValue(key, out EntityState? existing))
        {
            lock (existing)
            {
                foreach ((string attr, FactValue val) in attrs)
                {
                    existing.Values[attr] = val;
                }
            }

            return;
        }

        if (_count >= _maxEntries)
        {
            return;
        }

        EntityState state = new();
        foreach ((string attr, FactValue val) in attrs)
        {
            state.Values[attr] = val;
        }

        if (_state.TryAdd(key, state))
        {
            Interlocked.Increment(ref _count);
        }
    }

    private sealed class EntityState
    {
        public Dictionary<string, FactValue> Values { get; } = [];
    }
}