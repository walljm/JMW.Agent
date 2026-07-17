using JMW.Discovery.Core;

using Npgsql;

namespace JMW.Discovery.Server.Projections;

/// <summary>
/// Routes incoming facts to matching projections and drives their updates.
/// Routing is O(1) per fact: projections are indexed by (dimensionCount, attribute)
/// so there are no scans or regex matches at runtime.
/// </summary>
public sealed class ProjectionRouter
{
    private readonly NpgsqlDataSource _db;

    // Index: (dimension name sequence key, attribute) → projections
    // The dimension key is the dimension names joined, e.g. "Device|Interface".
    // Most lookups will hit a single projection.
    private readonly Dictionary<(string DimKey, string Attribute), List<IProjection>> _index;

    public ProjectionRouter(NpgsqlDataSource db, IEnumerable<IProjection> projections)
    {
        _db = db;
        _index = BuildIndex(projections);
    }

    /// <summary>
    /// Routes a batch of facts to matching projections.
    /// DimKey and Attribute are stored fields on Fact — no parsing for routing lookup.
    /// ParseId() is called once per routed fact to extract DimensionKeys (key values).
    /// Returns the table names actually written, so callers can gate work that only matters
    /// when specific projections changed (e.g. DiscoveryMaterializer) — GenericProjection is
    /// currently the only IProjection implementation, so every touched projection has a Def.
    /// </summary>
    public async Task<IReadOnlySet<string>> RouteAsync(IEnumerable<Fact> facts, CancellationToken ct = default)
    {
        Dictionary<IProjection, List<RoutedFact>> pending = new();

        foreach (Fact fact in facts)
        {
            // DimKey and Attribute are stored fields — index lookup costs a dictionary probe.
            if (fact.DimKey.Length == 0 || fact.Attribute.Length == 0)
            {
                continue;
            }

            if (!_index.TryGetValue((fact.DimKey, fact.Attribute), out List<IProjection>? targets))
            {
                continue;
            }

            // ParseId() called once per routed fact — only to extract key VALUES.
            // ALL list segments carry dimension keys, in path order — dimensions
            // may be separated by bare grouping segments (matches Fact.DimKey).
            FactSegment[] segs = fact.ParseId();
            int listCount = 0;
            foreach (FactSegment seg in segs)
            {
                if (seg.IsList)
                {
                    listCount++;
                }
            }

            string[] dimKeys = new string[listCount];
            int k = 0;
            foreach (FactSegment seg in segs)
            {
                if (seg.IsList)
                {
                    dimKeys[k++] = seg.Key ?? string.Empty;
                }
            }

            RoutedFact routed = new(
                DimensionKeys: dimKeys,
                Attribute: fact.Attribute,
                Value: fact.Value,
                CollectedAt: fact.CollectedAt,
                AgentId: fact.AgentId
            );

            foreach (IProjection proj in targets)
            {
                if (!pending.TryGetValue(proj, out List<RoutedFact>? list))
                {
                    list = [];
                    pending[proj] = list;
                }

                list.Add(routed);
            }
        }

        if (pending.Count == 0)
        {
            return EmptyTableNames;
        }

        // All projections share one connection for the batch.
        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);

        HashSet<string> touchedTables = new(StringComparer.Ordinal);
        foreach ((IProjection proj, List<RoutedFact> routed) in pending)
        {
            await proj.ApplyAsync(routed, conn, ct);
            if (proj is GenericProjection generic)
            {
                touchedTables.Add(generic.Def.TableName);
            }
        }

        return touchedTables;
    }

    private static readonly IReadOnlySet<string> EmptyTableNames = new HashSet<string>();

    private static Dictionary<(string, string), List<IProjection>> BuildIndex(
        IEnumerable<IProjection> projections
    )
    {
        Dictionary<(string, string), List<IProjection>> index = new();
        foreach (IProjection proj in projections)
        {
            string dimKey = string.Join("|", proj.DimensionNames);
            foreach (string attr in proj.TrackedAttributes)
            {
                (string dimKey, string attr) key = (dimKey, attr);
                if (!index.TryGetValue(key, out List<IProjection>? list))
                {
                    list = [];
                    index[key] = list;
                }

                list.Add(proj);
            }
        }

        return index;
    }
}