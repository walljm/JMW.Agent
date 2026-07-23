using System.Collections.Concurrent;
using System.Globalization;

using JMW.Discovery.Core;
using JMW.Discovery.Server.Data;

using Npgsql;

namespace JMW.Discovery.Server.Projections;

/// <summary>
/// Routes the analysis engine's hydratable derivation-input paths into the fact-shaped
/// <c>materialization_facts</c> current-value store (docs/plans/context-derivations.md §6.5,
/// migration 0107), making hydration retention-proof: <c>FactRepository.HydrateInputsAsync</c>
/// reads this table instead of facts_history's prunable latest-per-id, so an unchanged-but-true
/// input can never transiently vanish. Adding a derivation input is a data change (the path set
/// is computed from the registered derivations), never a column migration.
///
/// Sibling to <see cref="IdentityFactProjection" /> on the same table, deliberately separate:
/// that projection is text-only by contract (scanner identity signals); this one carries typed
/// values (kind/value_long/value_double — mem-bytes fan-in inputs are numeric), and its path set
/// excludes anything IdentitySignalPaths already routes so the two never double-write one
/// (device, entity_key, path) key. One instance per scope (the router indexes projections by
/// their DimKey): Device-scoped rows have entity_key = '' and are permanent; Discovered-scoped
/// rows carry the station key and prune on the steady neighbor-sighting tier — same lifetime as
/// the proj_discovered rows their derivations gap-fill, so hydration and the store age out
/// together (migration 0107).
/// </summary>
public sealed class DerivationInputProjection : IProjection
{
    private const string Table = "materialization_facts";

    public IReadOnlyList<string> DimensionNames { get; }
    public IReadOnlySet<string> TrackedAttributes { get; }

    // stripped attribute -> full FactPaths template (stored as attribute_path).
    private readonly Dictionary<string, string> _attributeToPath;

    // Flat change cache keyed by "device\0entityKey\0path" — same shape as
    // IdentityFactProjection's. Bounded by entities x input-paths, so uncapped is safe.
    private readonly ConcurrentDictionary<string, FactValue> _cache = new();

    public DerivationInputProjection(IReadOnlyList<string> dimensionNames, IEnumerable<string> hydratableInputPaths)
    {
        string dimKey = string.Join('|', dimensionNames);
        DimensionNames = dimensionNames;
        _attributeToPath = hydratableInputPaths
            .Where(p => !IdentitySignalPaths.All.Contains(p) && Fact.DeriveDimKey(p) == dimKey)
            .ToDictionary(Fact.DeriveAttribute, p => p, StringComparer.Ordinal);
        TrackedAttributes = _attributeToPath.Keys.ToHashSet(StringComparer.Ordinal);
    }

    public async Task ApplyAsync(IReadOnlyList<RoutedFact> facts, NpgsqlConnection conn, CancellationToken ct)
    {
        // Last fact wins per (device, entityKey, path), matching GenericProjection.GroupByEntity.
        Dictionary<string, (string Device, string EntityKey, string Path, FactValue Value, DateTimeOffset UpdatedAt)>
            byKey = new(StringComparer.Ordinal);

        foreach (RoutedFact fact in facts)
        {
            if (fact.Value.Kind is not (FactValueKind.String or FactValueKind.Long or FactValueKind.Double
                or FactValueKind.Bool))
            {
                continue; // hydration reconstructs only these kinds (FactRepository's contract)
            }

            if (fact.Value.Kind == FactValueKind.String && string.IsNullOrEmpty(fact.Value.AsString()))
            {
                continue;
            }

            if (!_attributeToPath.TryGetValue(fact.Attribute, out string? path))
            {
                continue;
            }

            string device = TextSanitizer.StripNul(fact.DimensionKeys[0]);
            string entityKey = fact.DimensionKeys.Length > 1
                ? TextSanitizer.StripNul(string.Join('\0', fact.DimensionKeys[1..]))
                : string.Empty;
            string key = string.Join('\0', device, entityKey, path);
            if (!byKey.TryGetValue(key, out (string, string, string, FactValue, DateTimeOffset UpdatedAt) existing)
                || fact.CollectedAt >= existing.UpdatedAt)
            {
                byKey[key] = (device, entityKey, path, fact.Value, fact.CollectedAt);
            }
        }

        if (byKey.Count == 0)
        {
            return;
        }

        List<(string Device, string EntityKey, string Path, FactValue Value, DateTimeOffset UpdatedAt)> changed =
            new(byKey.Count);
        foreach ((string key, (string Device, string EntityKey, string Path, FactValue Value, DateTimeOffset
            UpdatedAt) row) in byKey)
        {
            if (_cache.TryGetValue(key, out FactValue prev) && prev == row.Value)
            {
                continue;
            }

            _cache[key] = row.Value;
            changed.Add(row);
        }

        if (changed.Count == 0)
        {
            return;
        }

        int n = changed.Count;
        string[] devices = new string[n];
        string[] entityKeys = new string[n];
        string[] paths = new string[n];
        string[] texts = new string[n];
        short[] kinds = new short[n];
        long?[] longs = new long?[n];
        double?[] doubles = new double?[n];
        DateTimeOffset[] updatedAts = new DateTimeOffset[n];

        for (int i = 0; i < n; i++)
        {
            (string device, string entityKey, string path, FactValue value, DateTimeOffset updatedAt) = changed[i];
            devices[i] = device;
            entityKeys[i] = entityKey;
            paths[i] = path;
            // value keeps its NOT NULL text contract: canonical rendering for readability; the
            // typed payload rides kind/value_long/value_double and is what hydration reconstructs.
            texts[i] = TextSanitizer.StripNul(value.ToString() ?? string.Empty);
            kinds[i] = (short)value.Kind;
            longs[i] = value.Kind is FactValueKind.Long ? value.AsLong()
                : value.Kind is FactValueKind.Bool ? (value.AsBool() is true ? 1L : 0L)
                : null;
            doubles[i] = value.Kind is FactValueKind.Double ? value.AsDouble() : null;
            updatedAts[i] = updatedAt;
        }

        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {Table} (device, entity_key, attribute_path, value, kind, value_long, value_double, updated_at)
            SELECT device, entity_key, attribute_path, value, kind, value_long, value_double, updated_at
            FROM unnest($1::text[], $2::text[], $3::text[], $4::text[], $5::smallint[], $6::bigint[], $7::float8[], $8::timestamptz[])
              AS t(device, entity_key, attribute_path, value, kind, value_long, value_double, updated_at)
            ON CONFLICT (device, entity_key, attribute_path) DO UPDATE SET
                value = EXCLUDED.value,
                kind = EXCLUDED.kind,
                value_long = EXCLUDED.value_long,
                value_double = EXCLUDED.value_double,
                updated_at = EXCLUDED.updated_at
            WHERE {Table}.value IS DISTINCT FROM EXCLUDED.value
               OR {Table}.kind IS DISTINCT FROM EXCLUDED.kind
               OR {Table}.value_long IS DISTINCT FROM EXCLUDED.value_long
               OR {Table}.value_double IS DISTINCT FROM EXCLUDED.value_double
            """;
        cmd.Parameters.Add(Param.TextArray(devices));
        cmd.Parameters.Add(Param.TextArray(entityKeys));
        cmd.Parameters.Add(Param.TextArray(paths));
        cmd.Parameters.Add(Param.TextArray(texts));
        cmd.Parameters.Add(Param.SmallintArray(kinds));
        cmd.Parameters.Add(Param.BigintArray(longs));
        cmd.Parameters.Add(Param.DoubleArray(doubles));
        cmd.Parameters.Add(Param.TimestampTzArray(updatedAts));

        await cmd.ExecuteNonQueryAsync(ct);
    }
}