using System.Collections.Concurrent;

using JMW.Discovery.Core;
using JMW.Discovery.Server.Data;

using Npgsql;

namespace JMW.Discovery.Server.Projections;

/// <summary>
/// Writes <see cref="IdentitySignalPaths.All" /> facts into the fact-shaped
/// <c>materialization_facts</c> table — one row per (device, entity_key, attribute_path), rather
/// than one column per signal (docs/plans/architecture-identity-facts.md §3-4). Registered
/// alongside <see cref="ProjectionLibrary.AllDefs" /> in <c>Program.cs</c>, not inside it: the
/// router already supports multiple projections per (DimKey, Attribute) index entry, so during
/// the Phase 1-2 dual-write period the same routed fact hits both this projection and the
/// existing <c>proj_discovered</c> <see cref="GenericProjection" /> column.
/// Text-only by construction: every path in <see cref="IdentitySignalPaths.All" /> is a scanner
/// fingerprint/string identifier. A non-string value is dropped with a warning rather than
/// coerced — it would indicate a path wired into this set by mistake.
/// </summary>
public sealed class IdentityFactProjection : IProjection
{
    private const string Table = "materialization_facts";

    public IReadOnlyList<string> DimensionNames { get; } = ["Device", "Discovered"];
    public IReadOnlySet<string> TrackedAttributes { get; }

    // stripped attribute (e.g. "OnvifSerial") -> full FactPaths template (the narrow table's
    // attribute_path value) — the two differ because RoutedFact.Attribute is always stripped
    // (Fact.DeriveAttribute), but the narrow table stores the full template so pivots can
    // filter on it directly without re-deriving anything.
    private readonly Dictionary<string, string> _attributeToPath;

    private readonly ILogger<IdentityFactProjection> _logger;

    // Per-fact-row change cache, keyed by "device\0entityKey\0attributePath". Unlike
    // GenericProjection's per-entity cache, each identity fact is its own row (no partial-batch
    // column merge needed), so a flat value cache is sufficient. Deliberately uncapped (unlike
    // GenericProjection's EntityStateCache): cardinality here is discovered-neighbor count × 11
    // fixed signal paths, not per-device-scale fan-out (interfaces/disks/etc.), so it never
    // approaches the entry counts that make GenericProjection's cache bound necessary.
    private readonly ConcurrentDictionary<string, string> _cache = new();

    public IdentityFactProjection(ILogger<IdentityFactProjection> logger)
    {
        _logger = logger;
        _attributeToPath = IdentitySignalPaths.All.ToDictionary(Fact.DeriveAttribute, p => p, StringComparer.Ordinal);
        TrackedAttributes = _attributeToPath.Keys.ToHashSet(StringComparer.Ordinal);
    }

    public async Task ApplyAsync(IReadOnlyList<RoutedFact> facts, NpgsqlConnection conn, CancellationToken ct)
    {
        // Dedup within the batch: last fact wins per (device, entityKey, attributePath), matching
        // GenericProjection.GroupByEntity's last-collected-wins behavior for a repeated key.
        Dictionary<string, (string Device, string EntityKey, string AttributePath, string Value, DateTimeOffset
            UpdatedAt)> byKey = new(StringComparer.Ordinal);

        foreach (RoutedFact fact in facts)
        {
            if (fact.Value.Kind != FactValueKind.String)
            {
                IdentityFactProjectionLog.NonStringIdentityFact(_logger, fact.Attribute, fact.Value.Kind.ToString());
                continue;
            }

            string? value = fact.Value.AsString();
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (!_attributeToPath.TryGetValue(fact.Attribute, out string? path))
            {
                continue; // not one of ours (shouldn't happen — router indexes by TrackedAttributes)
            }

            string device = TextSanitizer.StripNul(fact.DimensionKeys[0]);
            string entityKey = fact.DimensionKeys.Length > 1
                ? TextSanitizer.StripNul(string.Join('\0', fact.DimensionKeys[1..]))
                : string.Empty;
            string cleanValue = TextSanitizer.StripNul(value);

            string key = string.Join('\0', device, entityKey, path);
            if (!byKey.TryGetValue(key, out (string, string, string, string, DateTimeOffset UpdatedAt) existing)
                || fact.CollectedAt >= existing.UpdatedAt)
            {
                byKey[key] = (device, entityKey, path, cleanValue, fact.CollectedAt);
            }
        }

        if (byKey.Count == 0)
        {
            return;
        }

        // Filter through the flat value cache before touching Postgres.
        List<(string Device, string EntityKey, string AttributePath, string Value, DateTimeOffset UpdatedAt)>
            changed = new(byKey.Count);
        foreach ((string key, (string Device, string EntityKey, string AttributePath, string Value, DateTimeOffset
            UpdatedAt) row) in byKey)
        {
            if (_cache.TryGetValue(key, out string? prev) && prev == row.Value)
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
        string[] attributePaths = new string[n];
        string[] values = new string[n];
        DateTimeOffset[] updatedAts = new DateTimeOffset[n];

        for (int i = 0; i < n; i++)
        {
            (string device, string entityKey, string attributePath, string value, DateTimeOffset updatedAt) =
                changed[i];
            devices[i] = device;
            entityKeys[i] = entityKey;
            attributePaths[i] = attributePath;
            values[i] = value;
            updatedAts[i] = updatedAt;
        }

        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {Table} (device, entity_key, attribute_path, value, updated_at)
            SELECT * FROM unnest($1::text[], $2::text[], $3::text[], $4::text[], $5::timestamptz[])
            ON CONFLICT (device, entity_key, attribute_path) DO UPDATE SET
                value = EXCLUDED.value,
                updated_at = EXCLUDED.updated_at
            WHERE {Table}.value IS DISTINCT FROM EXCLUDED.value
               OR {Table}.updated_at < EXCLUDED.updated_at
            """;
        cmd.Parameters.Add(Param.TextArray(devices));
        cmd.Parameters.Add(Param.TextArray(entityKeys));
        cmd.Parameters.Add(Param.TextArray(attributePaths));
        cmd.Parameters.Add(Param.TextArray(values));
        cmd.Parameters.Add(Param.TimestampTzArray(updatedAts));

        await cmd.ExecuteNonQueryAsync(ct);
    }
}

internal static partial class IdentityFactProjectionLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Dropped identity fact '{Attribute}': expected a string value but got {Kind}."
    )]
    internal static partial void NonStringIdentityFact(ILogger logger, string attribute, string kind);
}
