using System.Diagnostics.CodeAnalysis;

using JMW.Discovery.Core;
using JMW.Discovery.Server.Data;

using Npgsql;

namespace JMW.Discovery.Server;

/// <summary>
/// Appends metric-classified facts (<see cref="JMW.Discovery.Core.Analysis.FactPaths.MetricPaths" />)
/// to <c>metrics_raw</c> — always inserted, no dedup lookup. Unlike <see cref="FactRepository" />'s
/// facts_history dedup, an unchanged value between polls is itself informative for a monotonic
/// counter (zero traffic in the interval), so there is no cheaper-comparison variant worth keeping
/// here. Retention is per-partition DROP TABLE, handled by MetricPartitionService, not row deletes.
/// See docs/plans/metrics-retention.md.
/// </summary>
[SuppressMessage("ReSharper", "BitwiseOperatorOnEnumWithoutFlags")]
public sealed class MetricsRepository
{
    private readonly int _chunkSize;
    private readonly NpgsqlDataSource _db;

    public MetricsRepository(NpgsqlDataSource db, int chunkSize = 10_000)
    {
        _db = db;
        _chunkSize = chunkSize;
    }

    public async Task AppendAsync(IEnumerable<Fact> facts, CancellationToken ct = default)
    {
        IReadOnlyList<Fact> factList = facts is IReadOnlyList<Fact> list ? list : facts.ToList();
        (string[] ids, string[] attrPaths, string[] keyVals, short[] kinds, string?[] strs, long?[] longs,
            double?[] doubles, DateTimeOffset[] times, short[] sources, string[] sourceNames, int count) =
                FactRepository.BuildArrays(factList);
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

        // No dedup lookup — unconditional insert. ON CONFLICT DO NOTHING only guards against
        // an exact-duplicate (id, collected_at) pair from a retried batch, not value changes.
        cmd.CommandText = """
            INSERT INTO metrics_raw
                (id, attribute_path, key_values, kind, value_str, value_long, value_double, collected_at,
                    source, source_name)
            SELECT * FROM unnest($1,$2,$3::text[]::jsonb[],$4,$5,$6,$7,$8,$9,$10)
                AS t(id, attribute_path, key_values, kind, value_str, value_long, value_double, collected_at,
                    source, source_name)
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
}