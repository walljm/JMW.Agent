using System.Diagnostics.CodeAnalysis;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;
using JMW.Discovery.Server.Data;

using Npgsql;

namespace JMW.Discovery.Server;

/// <summary>
/// Appends changed facts to the history table.
/// Column design:
/// attribute_path : structural path with no keys — "Device.Interface.Speed"
/// Enables: WHERE attribute_path = 'Device.Interface.Speed'
/// key_values     : JSONB {"Device":"r1","Interface":"eth0"}
/// Enables: WHERE key_values->>'Interface' = 'eth0'
/// WHERE key_values @> '{"Device":"r1"}'::jsonb
/// id             : full human-readable path — kept for dedup and readability
/// Dedup is handled entirely at the DB layer via a LATERAL LIMIT 1 lookup against
/// the covering index (id, collected_at DESC) INCLUDE (kind, value_str, ...).
/// One index seek per ID in the batch; stops immediately at the most recent row.
/// Facts whose path is in <see cref="FactPaths.MetricPaths" /> (monotonic counters that
/// differ on nearly every poll) skip this dedup path entirely and go to
/// <see cref="MetricsRepository" /> instead — see docs/plans/metrics-retention.md.
/// </summary>
[SuppressMessage("ReSharper", "BitwiseOperatorOnEnumWithoutFlags")]
public sealed class FactRepository
{
    private readonly int _chunkSize;
    private readonly NpgsqlDataSource _db;
    private readonly MetricsRepository _metrics;

    public FactRepository(NpgsqlDataSource db, MetricsRepository metrics, int chunkSize = 10_000)
    {
        _db = db;
        _metrics = metrics;
        _chunkSize = chunkSize;
    }

    public Task AppendAsync(IEnumerable<Fact> facts, CancellationToken ct = default)
    {
        IReadOnlyList<Fact> factList = facts is IReadOnlyList<Fact> list ? list : facts.ToList();

        List<Fact> logged = new(factList.Count);
        List<Fact> metrics = new();
        foreach (Fact fact in factList)
        {
            if (FactPaths.MetricPaths.Contains(fact.AttributePath))
            {
                metrics.Add(fact);
            }
            else
            {
                logged.Add(fact);
            }
        }

        Task loggedTask = AppendLoggedAsync(logged, ct);
        Task metricsTask = metrics.Count == 0 ? Task.CompletedTask : _metrics.AppendAsync(metrics, ct);
        return Task.WhenAll(loggedTask, metricsTask);
    }

    private async Task AppendLoggedAsync(IReadOnlyList<Fact> factList, CancellationToken ct)
    {
        (string[] ids, string[] attrPaths, string[] keyVals, short[] kinds, string?[] strs, long?[] longs,
            double?[] doubles, DateTimeOffset[] times, short[] sources, string[] sourceNames, int count) =
                BuildArrays(factList);
        if (count == 0)
        {
            return;
        }

        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);

        if (count <= _chunkSize)
        {
            // Common path: single chunk — pass full arrays, no copy.
            await AppendChunkAsync(
                conn,
                ids,
                attrPaths,
                keyVals,
                kinds,
                strs,
                longs,
                doubles,
                times,
                sources,
                sourceNames,
                ct
            );
            return;
        }

        for (int offset = 0; offset < count; offset += _chunkSize)
        {
            int len = Math.Min(_chunkSize, count - offset);
            await AppendChunkAsync(
                conn,
                ids[offset..(offset + len)],
                attrPaths[offset..(offset + len)],
                keyVals[offset..(offset + len)],
                kinds[offset..(offset + len)],
                strs[offset..(offset + len)],
                longs[offset..(offset + len)],
                doubles[offset..(offset + len)],
                times[offset..(offset + len)],
                sources[offset..(offset + len)],
                sourceNames[offset..(offset + len)],
                ct
            );
        }
    }

    private static async Task AppendChunkAsync(
        NpgsqlConnection conn,
        string[] ids,
        string[] attrPaths,
        string[] keyVals,
        short[] kinds,
        string?[] strs,
        long?[] longs,
        double?[] doubles,
        DateTimeOffset[] times,
        short[] sources,
        string[] sourceNames,
        CancellationToken ct
    )
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();

        // key_values is sent as text[] and cast to jsonb[] in the batch CTE.
        // The LATERAL LIMIT 1 dedup reads the covering index only — no heap access.
        // Source participates in the dedup comparison too: a value re-confirmed by a
        // DIFFERENT collector than last time is itself worth a new history row, even
        // when the observed value hasn't changed. source_name is fully determined by
        // source, so it doesn't need its own dedup comparison.
        cmd.CommandText = """
            WITH batch AS (
                SELECT * FROM unnest($1,$2,$3::text[]::jsonb[],$4,$5,$6,$7,$8,$9,$10)
                AS t(id, attribute_path, key_values, kind, value_str, value_long, value_double, collected_at,
                    source, source_name)
            ),
            latest AS (
                SELECT b_ids.id, l.kind, l.value_str, l.value_long, l.value_double, l.source
                FROM   (SELECT id FROM batch) b_ids
                CROSS JOIN LATERAL (
                    SELECT fh.kind, fh.value_str, fh.value_long, fh.value_double, fh.source
                    FROM   facts_history fh
                    WHERE  fh.id = b_ids.id
                    ORDER  BY fh.collected_at DESC
                    LIMIT  1
                ) l
            ),
            changed AS (
                SELECT b.*
                FROM batch b
                LEFT JOIN latest l ON l.id = b.id
                WHERE l.id IS NULL
                   OR l.kind         IS DISTINCT FROM b.kind
                   OR l.value_str    IS DISTINCT FROM b.value_str
                   OR l.value_long   IS DISTINCT FROM b.value_long
                   OR l.value_double IS DISTINCT FROM b.value_double
                   OR l.source       IS DISTINCT FROM b.source
            )
            INSERT INTO facts_history
                (id, attribute_path, key_values, kind, value_str, value_long, value_double, collected_at,
                    source, source_name)
            SELECT id, attribute_path, key_values, kind, value_str, value_long, value_double, collected_at,
                source, source_name
            FROM changed
            ON CONFLICT (id, collected_at) DO NOTHING
            """;

        cmd.Parameters.Add(Param.TextArray(ids));
        cmd.Parameters.Add(Param.TextArray(attrPaths));
        cmd.Parameters.Add(Param.TextArray(keyVals));
        cmd.Parameters.Add(Param.SmallintArray(kinds));
        cmd.Parameters.Add(Param.TextArray(strs));
        cmd.Parameters.Add(Param.BigintArray(longs));
        cmd.Parameters.Add(Param.DoubleArray(doubles));
        cmd.Parameters.Add(Param.TimestampTzArray(times));
        cmd.Parameters.Add(Param.SmallintArray(sources));
        cmd.Parameters.Add(Param.TextArray(sourceNames));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Array building ────────────────────────────────────────────────────────
    // AttributePath and KeyValuesJson are stored fields on Fact — no parsing.
    // Internal (not private): shared with MetricsRepository, which writes the same
    // column shape to metrics_raw instead of facts_history.

    internal static (string[] ids, string[] attrPaths, string[] keyVals, short[] kinds,
        string?[] strs, long?[] longs, double?[] doubles,
        DateTimeOffset[] times, short[] sources, string[] sourceNames, int count)
        BuildArrays(IReadOnlyList<Fact> facts)
    {
        int n = facts.Count;

        string[] ids = new string[n];
        string[] attrPaths = new string[n];
        string[] keyVals = new string[n];
        short[] kinds = new short[n];
        string?[] strs = new string?[n];
        long?[] longs = new long?[n];
        double?[] doubles = new double?[n];
        DateTimeOffset[] times = new DateTimeOffset[n];
        short[] sources = new short[n];
        string[] sourceNames = new string[n];

        for (int i = 0; i < n; i++)
        {
            Fact fact = facts[i];

            ids[i] = TextSanitizer.StripNul(fact.Id);
            attrPaths[i] = TextSanitizer.StripNul(fact.AttributePath); // stored field — no parsing
            keyVals[i] = TextSanitizer.StripNul(fact.KeyValuesJson); // stored field — no parsing
            kinds[i] = (short)fact.Value.Kind;
            times[i] = fact.CollectedAt;
            sources[i] = (short)fact.Source;
            sourceNames[i] = fact.SourceName ?? fact.Source.ToString();

            (string? vs, long? vl, double? vd) = fact.Value.Kind switch
            {
                FactValueKind.String => (fact.Value.AsString(), (long?)null, (double?)null),
                FactValueKind.Long => (null, fact.Value.AsLong(), null),
                FactValueKind.Double => (null, null, fact.Value.AsDouble()),
                FactValueKind.Bool => (null, fact.Value.AsBool() is true ? 1L : (long?)0L, null),
                FactValueKind.DateTimeOffset => (null, fact.Value.AsDateTimeOffset()?.UtcTicks, null),
                FactValueKind.TimeSpan => (null, fact.Value.AsTimeSpan()?.Ticks, null),
                _ => (fact.Value.ToString(), null, null),
            };
            strs[i] = TextSanitizer.StripNul(vs);
            longs[i] = vl;
            doubles[i] = vd;
        }

        return (ids, attrPaths, keyVals, kinds, strs, longs, doubles, times, sources, sourceNames, n);
    }
}