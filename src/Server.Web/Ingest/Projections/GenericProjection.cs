using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

using JMW.Discovery.Core;
using JMW.Discovery.Server.Data;

using Npgsql;

using NpgsqlTypes;

namespace JMW.Discovery.Server.Projections;

/// <summary>
/// A projection driven entirely by a <see cref="ProjectionDef" />.
/// SQL is generated once at construction; ApplyAsync only does value extraction
/// and a single parameterized statement per call.
/// Scale design notes:
/// The primary defense against needless writes is the EntityStateCache:
/// entities whose combined column values haven't changed are filtered before
/// they touch Postgres at all — no heap reads, no WAL writes.
/// The SQL WHERE guard is a secondary safety net for the cache's overflow
/// region (entities beyond maxCacheEntries). At 80K-device scale, keep
/// high-cardinality projections (e.g. per-interface) under the cache limit
/// or rely on collector-side delta tracking to reduce incoming volume first.
/// The cache bound exists because FactValue is a 32-byte struct and storing
/// full entity state for millions of entities is prohibitive. Default 500K
/// covers ~115 MB; tune per projection cardinality.
/// </summary>
[SuppressMessage("ReSharper", "BitwiseOperatorOnEnumWithoutFlags")]
public sealed class GenericProjection : IProjection
{
    private readonly string _sql;

    private readonly EntityStateCache _cache;

    // Stripped attribute names per column — used as dict keys in ApplyAsync.
    // FactPaths constants include the full path ("Device[].OS.Hostname") but
    // Fact.Attribute strips the dimension prefix to just "OS.Hostname".
    private readonly string[] _strippedAttributes;

    public IReadOnlyList<string> DimensionNames => Def.DimensionNames;
    public IReadOnlySet<string> TrackedAttributes { get; }
    public ProjectionDef Def { get; }

    public GenericProjection(ProjectionDef def, int maxCacheEntries = 500_000)
    {
        Def = def;
        _strippedAttributes = def.Columns.Select(c => Fact.DeriveAttribute(c.Attribute)).ToArray();
        TrackedAttributes = _strippedAttributes.ToHashSet();
        _sql = BuildSql(def);
        _cache = new EntityStateCache(maxCacheEntries);
    }

    // ── Cache warm-up ─────────────────────────────────────────────────────────

    /// <summary>
    /// Populates the entity state cache from the projection table on server startup.
    /// The projection table IS the current state — no history scan needed.
    /// Reads up to <paramref name="limit" /> recently updated rows (ordered by updated_at DESC)
    /// so the cache fills with the entities most likely to change next.
    /// </summary>
    public async Task WarmCacheAsync(NpgsqlDataSource db, int limit = 500_000, CancellationToken ct = default)
    {
        string table = Def.TableName;
        string[] dimCols = Def.DimensionNames.Select(n => n.ToLowerInvariant()).ToArray();
        string[] dataCols = Def.Columns.Select(c => c.ColumnName).ToArray();
        string allCols = string.Join(", ", dimCols.Concat(dataCols));

        // Order by updated_at DESC so the cache fills with the hottest entities
        // first — the ones most likely to change in the next cycle.
        // LIMIT keeps memory bounded to maxCacheEntries even if the table is larger.
        string sql = $"""
            SELECT {allCols}
            FROM {table}
            ORDER BY updated_at DESC
            LIMIT {limit}
            """;

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        int dimCount = dimCols.Length;
        int dataCount = dataCols.Length;

        while (await reader.ReadAsync(ct))
        {
            string[] dimKeys = new string[dimCount];
            for (int i = 0; i < dimCount; i++)
            {
                dimKeys[i] = reader.GetString(i);
            }

            Dictionary<string, FactValue> attrs = new(dataCount);
            for (int i = 0; i < dataCount; i++)
            {
                if (reader.IsDBNull(dimCount + i))
                {
                    continue;
                }

                ProjectionColumnDef col = Def.Columns[i];

                // Key by the stripped attribute name — Filter() compares against
                // batch attrs keyed by Fact.Attribute, which is the stripped form.
                attrs[_strippedAttributes[i]] = ReadFactValue(reader, dimCount + i, col.Kind);
            }

            // Inject directly into cache as if these values were just written
            _cache.Seed(dimKeys, attrs);
        }
    }

    private static FactValue ReadFactValue(NpgsqlDataReader reader, int ordinal, NpgsqlDbType kind) =>
        kind switch
        {
            NpgsqlDbType.Bigint or NpgsqlDbType.Integer or NpgsqlDbType.Smallint
                => FactValue.FromLong(reader.GetInt64(ordinal)),
            NpgsqlDbType.Boolean
                => FactValue.FromBool(reader.GetBoolean(ordinal)),
            NpgsqlDbType.Double or NpgsqlDbType.Real
                => FactValue.FromDouble(reader.GetDouble(ordinal)),
            _ => FactValue.FromString(reader.GetString(ordinal)),
        };

    // ── IProjection ───────────────────────────────────────────────────────────

    public async Task ApplyAsync(
        IReadOnlyList<RoutedFact> facts,
        NpgsqlConnection conn,
        CancellationToken ct
    )
    {
        List<(string[] DimKeys, Dictionary<string, FactValue> Attrs, DateTimeOffset UpdatedAt)> groups =
            GroupByEntity(facts);
        if (groups.Count == 0)
        {
            return;
        }

        // Filter through the entity state cache before touching Postgres.
        // Entities whose combined column values haven't changed are dropped here.
        // The cache tracks full entity state so partial-column batches are handled
        // correctly: an attribute not in this batch is preserved from prior state.
        List<(string[] DimKeys, Dictionary<string, FactValue> Attrs, DateTimeOffset UpdatedAt)> changed =
            _cache.Filter(groups);
        if (changed.Count == 0)
        {
            return;
        }

        int n = changed.Count;

        int dimCount = Def.DimensionNames.Count;
        string[][] dimArrays = new string[dimCount][];
        for (int d = 0; d < dimCount; d++) { dimArrays[d] = new string[n]; }

        ColumnArray[] colArrays = new ColumnArray[Def.Columns.Count];
        for (int c = 0; c < colArrays.Length; c++) { colArrays[c] = ColumnArray.Create(Def.Columns[c], n); }

        DateTimeOffset[] updatedAts = new DateTimeOffset[n];

        int i = 0;
        foreach ((string[] dimKeys, Dictionary<string, FactValue> attrs, DateTimeOffset updatedAt) in changed)
        {
            for (int d = 0; d < dimKeys.Length; d++)
            {
                dimArrays[d][i] = TextSanitizer.StripNul(dimKeys[d]);
            }

            for (int c = 0; c < colArrays.Length; c++)
            {
                attrs.TryGetValue(_strippedAttributes[c], out FactValue val);
                colArrays[c].Set(i, val);
            }

            updatedAts[i] = updatedAt;
            i++;
        }

        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = _sql;

        foreach (string[] arr in dimArrays)
        {
            cmd.Parameters.Add(Param.TextArray(arr));
        }

        foreach (ColumnArray col in colArrays)
        {
            cmd.Parameters.Add(col.ToParameter());
        }

        cmd.Parameters.Add(Param.TimestampTzArray(updatedAts));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Grouping ──────────────────────────────────────────────────────────────

    private static List<(string[] DimKeys, Dictionary<string, FactValue> Attrs, DateTimeOffset UpdatedAt)>
        GroupByEntity(IReadOnlyList<RoutedFact> facts)
    {
        Dictionary<string, (string[] DimKeys, Dictionary<string, FactValue> Attrs, DateTimeOffset UpdatedAt)>
            map = new();

        foreach (RoutedFact fact in facts)
        {
            string key = fact.DimensionKeys.Length switch
            {
                0 => string.Empty,
                1 => fact.DimensionKeys[0],
                _ => string.Join('\0', fact.DimensionKeys),
            };
            if (!map.TryGetValue(
                key,
                out (string[] DimKeys, Dictionary<string, FactValue> Attrs, DateTimeOffset UpdatedAt) entry
            ))
            {
                entry = (fact.DimensionKeys, [], fact.CollectedAt);
                map[key] = entry;
            }

            entry.Attrs[fact.Attribute] = fact.Value;
            if (fact.CollectedAt > entry.UpdatedAt)
            {
                map[key] = entry with
                {
                    UpdatedAt = fact.CollectedAt,
                };
            }
        }

        return [.. map.Values];
    }

    // ── SQL generation ────────────────────────────────────────────────────────
    //
    // Parameter layout (1-based):
    //   $1 .. $D      : dimension text[] arrays      (D = DimensionNames.Count)
    //   $D+1 .. $D+C  : column typed[] arrays        (C = Columns.Count)
    //   $D+C+1        : updated_at timestamptz[]
    //
    // WHERE clause uses (EXCLUDED.col IS NOT NULL AND col IS DISTINCT FROM EXCLUDED.col)
    // rather than the COALESCE form. Both are logically equivalent but this avoids
    // a COALESCE function call per column per row on the Postgres side.
    // The entity state cache above is the primary write guard; this WHERE is a
    // fallback for entities that overflow the cache.

    private static string BuildSql(ProjectionDef def)
    {
        string table = def.TableName;
        string[] dimCols = def.DimensionNames.Select(n => n.ToLowerInvariant()).ToArray();
        string[] dataCols = def.Columns.Select(c => c.ColumnName).ToArray();

        int p = 1;
        string[] dimSelects = dimCols.Select(_ => $"unnest(${p++}::text[])").ToArray();
        string[] dataSelects = def.Columns.Select(c => $"unnest(${p++}::{PgType(c.Kind)}[])").ToArray();
        string tsSelect = $"unnest(${p}::timestamptz[])";

        StringBuilder sb = new();
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"INSERT INTO {table} ({Join(dimCols)}, {Join(dataCols)}, updated_at)"
        );
        sb.AppendLine("SELECT");
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"    {string.Join(",\n    ", dimSelects.Concat(dataSelects).Append(tsSelect))}"
        );
        sb.AppendLine(CultureInfo.InvariantCulture, $"ON CONFLICT ({Join(dimCols)}) DO UPDATE SET");

        IEnumerable<string> sets = dataCols
            .Select(c => $"    {c} = COALESCE(EXCLUDED.{c}, {table}.{c})")
            .Append($"    updated_at = GREATEST(EXCLUDED.updated_at, {table}.updated_at)");
        sb.AppendLine(string.Join(",\n", sets));

        // IS NOT NULL short-circuits when the column wasn't in this batch (NULL),
        // avoiding the IS DISTINCT FROM comparison entirely for those columns.
        sb.AppendLine("WHERE");
        IEnumerable<string> guards = dataCols.Select(c =>
            $"    (EXCLUDED.{c} IS NOT NULL AND {table}.{c} IS DISTINCT FROM EXCLUDED.{c})"
        );
        sb.Append(string.Join(" OR\n", guards));

        return sb.ToString();
    }

    private static string PgType(NpgsqlDbType kind) => ProjectionSchema.PgType(kind);

    private static string Join(IEnumerable<string> cols) => string.Join(", ", cols);

    // ── Entity state cache ────────────────────────────────────────────────────
    //
    // Tracks the full current state of each entity (all tracked columns).
    // When an entity batch arrives, attributes not in the batch are merged from
    // the cached state so the change detection is always over the full row,
    // not just the partial batch.
    //
    // Memory: each entry stores N FactValue structs (32 bytes each) plus a string
    // key. At 5 columns, roughly 230 bytes per entity. Default 500K cap = ~115 MB.
    // Entities beyond the cap pass through to the SQL WHERE guard.

    private sealed class EntityStateCache
    {
        private readonly int _maxEntries;

        public EntityStateCache(int maxEntries)
        {
            _maxEntries = maxEntries;
        }

        private readonly ConcurrentDictionary<string, EntityState> _state = new();
        private int _count;

        public List<(string[] DimKeys, Dictionary<string, FactValue> Attrs, DateTimeOffset UpdatedAt)>
            Filter(List<(string[] DimKeys, Dictionary<string, FactValue> Attrs, DateTimeOffset UpdatedAt)> entities)
        {
            List<(string[], Dictionary<string, FactValue>, DateTimeOffset)> changed = new(entities.Count);

            foreach ((string[] dimKeys, Dictionary<string, FactValue> batchAttrs, DateTimeOffset updatedAt) in entities)
            {
                string key = string.Join('\0', dimKeys);

                if (!_state.TryGetValue(key, out EntityState? state))
                {
                    // Cache full: pass through and let the SQL WHERE guard decide
                    if (_count >= _maxEntries)
                    {
                        changed.Add((dimKeys, batchAttrs, updatedAt));
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
                    changed.Add((dimKeys, batchAttrs, updatedAt));
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

    // ── Typed column buffers ──────────────────────────────────────────────────

    private abstract class ColumnArray
    {
        public string Attribute { get; }

        protected ColumnArray(string attribute)
        {
            Attribute = attribute;
        }


        public static ColumnArray Create(ProjectionColumnDef col, int size) => col.Kind switch
        {
            NpgsqlDbType.Bigint or
                NpgsqlDbType.Integer or
                NpgsqlDbType.Smallint => new LongArray(col.Attribute, size),
            NpgsqlDbType.Boolean => new BoolArray(col.Attribute, size),
            NpgsqlDbType.Double or
                NpgsqlDbType.Real => new DoubleArray(col.Attribute, size),
            _ => new TextArray(col.Attribute, size),
        };

        public abstract void Set(int index, FactValue? value);
        public abstract NpgsqlParameter ToParameter();
    }

    private sealed class TextArray : ColumnArray
    {
        private readonly string?[] _data;

        public TextArray(string attribute, int size) : base(attribute)
        {
            _data = new string?[size];
        }

        public override void Set(int i, FactValue? v) => _data[i] = TextSanitizer.StripNul(v?.ToString());

        public override NpgsqlParameter ToParameter() => new()
        {
            Value = _data,
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
        };
    }

    private sealed class LongArray : ColumnArray
    {
        private readonly long?[] _data;

        public LongArray(string attribute, int size) : base(attribute)
        {
            _data = new long?[size];
        }


        public override void Set(int i, FactValue? v) => _data[i] = v is null
            ? null
            : v.Value.Kind switch
            {
                FactValueKind.Long => v.Value.AsLong(),
                FactValueKind.Bool => v.Value.AsBool() is true ? 1L : 0L,
                FactValueKind.DateTimeOffset => v.Value.AsDateTimeOffset()?.UtcTicks,
                FactValueKind.TimeSpan => v.Value.AsTimeSpan()?.Ticks,
                _ => null,
            };

        public override NpgsqlParameter ToParameter() => new()
        {
            Value = _data,
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint,
        };
    }

    private sealed class BoolArray : ColumnArray
    {
        private readonly bool?[] _data;

        public BoolArray(string attribute, int size) : base(attribute)
        {
            _data = new bool?[size];
        }


        public override void Set(int i, FactValue? v)
            => _data[i] = v is null ? null : v.Value.AsBool() ?? (v.Value.AsLong() is { } l ? l != 0 : null);

        public override NpgsqlParameter ToParameter() => new()
        {
            Value = _data,
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Boolean,
        };
    }

    private sealed class DoubleArray : ColumnArray
    {
        private readonly double?[] _data;

        public DoubleArray(string attribute, int size) : base(attribute)
        {
            _data = new double?[size];
        }

        public override void Set(int i, FactValue? v) => _data[i] = v?.AsDouble();

        public override NpgsqlParameter ToParameter() => new()
        {
            Value = _data,
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Double,
        };
    }
}