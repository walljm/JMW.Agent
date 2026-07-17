using System.Buffers;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JMW.Discovery.Core;

/// <summary>
/// A single piece of information known about a network node.
/// ID format — keys always present in the full Id:
/// Device[router-1].Interface[eth0].Speed
/// Device[router-1].Inventory.Modules[0].Name
/// Derived string fields are computed ONCE from Id in Create() and stored.
/// Every pipeline stage (normalization, derivation, storage, routing) reads
/// them as property accesses — no re-parsing, no re-allocation.
/// Only Id, Value, and CollectedAt participate in equality — the derived
/// fields are definitionally determined by Id and are excluded, and Source
/// (provenance, not identity) is excluded too.
/// </summary>
[JsonConverter(typeof(FactJsonConverter))]
public readonly record struct Fact
{
    // ── Semantic fields (participate in equality) ─────────────────────────────

    public required string Id { get; init; }
    public required FactValue Value { get; init; }
    public required DateTimeOffset CollectedAt { get; init; }

    /// <summary>
    /// Which collector/scanner produced this fact. Not derivable from Id — set by the
    /// producer (or backfilled by the agent's collection loop) — and excluded from
    /// equality like the derived fields below, since it's provenance, not identity.
    /// Defaults to <see cref="FactSource.Unknown" /> so existing Create() call sites are
    /// unaffected; see <see cref="FactSource" /> for where it actually gets stamped.
    /// </summary>
    public FactSource Source { get; init; }

    /// <summary>
    /// Overrides the persisted <c>source_name</c> column instead of deriving it from
    /// <see cref="Source" />'s <c>ToString()</c>. Null (the default) preserves existing
    /// behavior for every other call site. Used for <see cref="FactSource.ManualEntry" />
    /// facts, where <c>source_name</c> carries the acting operator's identity rather than
    /// the generic enum name — see <see cref="FactSource.ManualEntry" />. Provenance, not
    /// identity, so excluded from equality like <see cref="Source" />.
    /// </summary>
    public string? SourceName { get; init; }

    /// <summary>
    /// The agent that reported this fact, when known. Not derivable from Id — stamped by
    /// <c>FactsEndpoint</c> from the authenticated agent's id when rewriting the placeholder
    /// root — and excluded from equality like <see cref="Source" />, since it's provenance,
    /// not identity. Used to scope IP/MAC-join lookups to the reporting agent's own LAN
    /// (RFC1918 addresses are commonly reused across independent LANs this server ingests
    /// from) — see docs/plans/ha-device-enrichment.md §5.
    /// </summary>
    public Guid? AgentId { get; init; }

    // ── Derived fields (computed once from Id, excluded from equality) ────────

    /// <summary>
    /// Structural path — empty brackets mark list positions, keys omitted.
    /// "Device[r1].Interface[eth0].Speed" → "Device[].Interface[].Speed"
    /// Used as attribute_path in DB and as normalizer/derivation lookup key.
    /// </summary>
    public string AttributePath { get; init; }

    /// <summary>
    /// JSONB key-value pairs for list segments — DB key_values column.
    /// "Device[r1].Interface[eth0].Speed" → {"Device":"r1","Interface":"eth0"}
    /// </summary>
    public string KeyValuesJson { get; init; }

    /// <summary>
    /// Pipe-joined list dimension names — routing index lookup key.
    /// "Device[r1].Interface[eth0].Speed" → "Device|Interface"
    /// </summary>
    public string DimKey { get; init; }

    /// <summary>
    /// Dot-joined trailing bare segment names — routing attribute key.
    /// "Device[r1].Interface[eth0].Speed" → "Speed"
    /// </summary>
    public string Attribute { get; init; }

    // ── Equality — only semantic fields ───────────────────────────────────────

    public bool Equals(Fact other) => Id == other.Id && Value == other.Value && CollectedAt == other.CollectedAt;

    public override int GetHashCode() => HashCode.Combine(Id, Value, CollectedAt);

    // ── On-demand parsing — only where actual key VALUES are needed ───────────

    /// <summary>
    /// Parses Id into segments. Use only when key VALUES (not just names) are
    /// required — e.g. routing (DimensionKeys), BuildId template filling.
    /// All structural information is already in the derived string fields.
    /// </summary>
    public FactSegment[] ParseId() => FactSegment.ParsePath(Id);

    /// <summary>
    /// Derives the routing <c>Attribute</c> (bare names after the last list segment) from a
    /// fact path or projection-column template. This is the SINGLE definition the emit side
    /// (Create) and the projection side both use, so a fact's attribute and a
    /// projection column's attribute can never be computed two different ways.
    /// </summary>
    public static string DeriveAttribute(string path) => ComputeAttribute(FactSegment.ParsePath(path));

    /// <summary>
    /// Derives the routing <c>DimKey</c> (all list-segment names, path order, joined by '|')
    /// from a fact path or template — the companion to <see cref="DeriveAttribute" />.
    /// </summary>
    public static string DeriveDimKey(string path) => ComputeDimKey(FactSegment.ParsePath(path));

    // ── Factories ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Base factory. Parses Id once to compute all derived fields.
    /// All other Create overloads delegate here.
    /// </summary>
    public static Fact Create(string id, FactValue value, DateTimeOffset? collectedAt = null)
    {
        // Sanitize before parsing: a NUL surviving into a list-segment key gets baked
        // into KeyValuesJson as a six-character JSON escape sequence (see TextSanitizer), which
        // no longer contains a literal NUL for a later pass to strip. This is the single
        // construction path every Fact goes through, so stripping here — before any
        // parsing or JSON serialization — is the only point that reliably catches it.
        id = TextSanitizer.StripNul(id);
        FactSegment[] segs = FactSegment.ParsePath(id);
        return new()
        {
            Id = id,
            Value = value,
            CollectedAt = collectedAt ?? DateTimeOffset.UtcNow,
            AttributePath = ComputeAttributePath(segs),
            KeyValuesJson = ComputeKeyValuesJson(segs),
            DimKey = ComputeDimKey(segs),
            Attribute = ComputeAttribute(segs),
        };
    }

    public static Fact Create(string id, string value, DateTimeOffset? collectedAt = null)
        => Create(id, FactValue.FromString(value), collectedAt);

    public static Fact Create(string id, long value, DateTimeOffset? collectedAt = null)
        => Create(id, FactValue.FromLong(value), collectedAt);

    public static Fact Create(string id, double value, DateTimeOffset? collectedAt = null)
        => Create(id, FactValue.FromDouble(value), collectedAt);

    public static Fact Create(string id, bool value, DateTimeOffset? collectedAt = null)
        => Create(id, FactValue.FromBool(value), collectedAt);

    public static Fact Create(string id, DateTimeOffset value, DateTimeOffset? collectedAt = null)
        => Create(id, FactValue.FromDateTimeOffset(value), collectedAt);

    public static Fact Create(string id, TimeSpan value, DateTimeOffset? collectedAt = null)
        => Create(id, FactValue.FromTimeSpan(value), collectedAt);

    public static Fact Create(string id, IPAddress value, DateTimeOffset? collectedAt = null)
        => Create(id, FactValue.FromIPAddress(value), collectedAt);

    public static Fact Create(string id, IPNetwork value, DateTimeOffset? collectedAt = null)
        => Create(id, FactValue.FromIPNetwork(value), collectedAt);

    public static Fact Create(string id, PhysicalAddress value, DateTimeOffset? collectedAt = null)
        => Create(id, FactValue.FromPhysicalAddress(value), collectedAt);

    // ── Key-substitution overloads — fill [] placeholders left-to-right ───────
    // Use with FactPaths/ServicePaths constants: Fact.Create(FactPaths.HwCpuModel, [deviceId], model)

    public static Fact Create(string attributePath, string[] keys, FactValue value, DateTimeOffset? collectedAt = null)
        => Create(FillKeys(attributePath, keys), value, collectedAt);

    public static Fact Create(string attributePath, string[] keys, string value, DateTimeOffset? collectedAt = null)
        => Create(FillKeys(attributePath, keys), value, collectedAt);

    public static Fact Create(string attributePath, string[] keys, long value, DateTimeOffset? collectedAt = null)
        => Create(FillKeys(attributePath, keys), value, collectedAt);

    public static Fact Create(string attributePath, string[] keys, double value, DateTimeOffset? collectedAt = null)
        => Create(FillKeys(attributePath, keys), value, collectedAt);

    public static Fact Create(string attributePath, string[] keys, bool value, DateTimeOffset? collectedAt = null)
        => Create(FillKeys(attributePath, keys), value, collectedAt);

    public static Fact Create(
        string attributePath,
        string[] keys,
        DateTimeOffset value,
        DateTimeOffset? collectedAt = null
    ) => Create(FillKeys(attributePath, keys), value, collectedAt);

    public static Fact Create(string attributePath, string[] keys, TimeSpan value, DateTimeOffset? collectedAt = null)
        => Create(FillKeys(attributePath, keys), value, collectedAt);

    public static Fact Create(string attributePath, string[] keys, IPAddress value, DateTimeOffset? collectedAt = null)
        => Create(FillKeys(attributePath, keys), value, collectedAt);

    public static Fact Create(string attributePath, string[] keys, IPNetwork value, DateTimeOffset? collectedAt = null)
        => Create(FillKeys(attributePath, keys), value, collectedAt);

    public static Fact Create(
        string attributePath,
        string[] keys,
        PhysicalAddress value,
        DateTimeOffset? collectedAt = null
    ) => Create(FillKeys(attributePath, keys), value, collectedAt);

    private static string FillKeys(string template, string[] keys)
    {
        int bracketCount = 0;
        int pos = 0;
        while (pos < template.Length)
        {
            int idx = template.IndexOf("[]", pos, StringComparison.Ordinal);
            if (idx < 0) { break; }

            bracketCount++;
            pos = idx + 2;
        }

        if (keys.Length != bracketCount)
        {
            throw new ArgumentException(
                $"Fact path template '{template}' has {bracketCount} placeholder(s) but {keys.Length} key(s) were provided.",
                nameof(keys)
            );
        }

        StringBuilder sb = new(template.Length + keys.Sum(k => k.Length));
        int ki = 0;
        pos = 0;
        while (pos < template.Length)
        {
            int bracket = template.IndexOf("[]", pos, StringComparison.Ordinal);
            if (bracket < 0 || ki >= keys.Length)
            {
                sb.Append(template, pos, template.Length - pos);
                break;
            }

            sb.Append(template, pos, bracket - pos);
            sb.Append('[');
            sb.Append(keys[ki++]);
            sb.Append(']');
            pos = bracket + 2;
        }

        return sb.ToString();
    }

    // ── Derivation helpers ────────────────────────────────────────────────────

    private static string ComputeAttributePath(FactSegment[] segs)
    {
        if (segs.Length == 0)
        {
            return string.Empty;
        }

        // Compute exact length so string.Create avoids intermediate string[] allocation.
        int len = segs.Length - 1; // dots
        foreach (FactSegment seg in segs)
        {
            len += seg.Name.Length + (seg.IsList ? 2 : 0); // "[]" = 2
        }

        return string.Create(
            len,
            segs,
            static (span, segs) =>
            {
                int pos = 0;
                for (int i = 0; i < segs.Length; i++)
                {
                    if (i > 0) { span[pos++] = '.'; }

                    segs[i].Name.AsSpan().CopyTo(span[pos..]);
                    pos += segs[i].Name.Length;
                    if (segs[i].IsList)
                    {
                        span[pos++] = '[';
                        span[pos++] = ']';
                    }
                }
            }
        );
    }

    private static string ComputeKeyValuesJson(FactSegment[] segs)
    {
        bool hasKeys = false;
        foreach (FactSegment seg in segs)
        {
            if (seg.IsList && seg.Key is { Length: > 0 })
            {
                hasKeys = true;
                break;
            }
        }

        if (!hasKeys)
        {
            return "{}";
        }

        ArrayBufferWriter<byte> buf = new(64);
        Utf8JsonWriter writer = new(buf);
        writer.WriteStartObject();
        foreach (FactSegment seg in segs)
        {
            if (seg.IsList && seg.Key is { Length: > 0 })
            {
                writer.WriteString(seg.Name, seg.Key);
            }
        }

        writer.WriteEndObject();
        writer.Flush();
        writer.Dispose();
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    private static string ComputeDimKey(FactSegment[] segs)
    {
        // ALL list segments form the dimension key, in path order. Dimensions may
        // be separated by bare grouping segments:
        // "Service[x].DNS.Zone[y].Type" → "Service|Zone".
        int count = 0;
        foreach (FactSegment seg in segs)
        {
            if (seg.IsList)
            {
                count++;
            }
        }

        if (count == 0)
        {
            return string.Empty;
        }

        string[] names = new string[count];
        int j = 0;
        foreach (FactSegment seg in segs)
        {
            if (seg.IsList)
            {
                names[j++] = seg.Name;
            }
        }

        return string.Join("|", names);
    }

    private static string ComputeAttribute(FactSegment[] segs)
    {
        // Bare segment names after the LAST list segment — mirrors how
        // projections strip the dimension prefix from their column paths:
        // "Service[x].DNS.Zone[y].Type" → "Type"
        // "Service[x].DNS.Stats.TotalQueries" → "DNS.Stats.TotalQueries"
        int lastList = -1;
        for (int i = 0; i < segs.Length; i++)
        {
            if (segs[i].IsList)
            {
                lastList = i;
            }
        }

        int start = lastList + 1;
        if (start >= segs.Length)
        {
            return string.Empty;
        }

        string[] parts = new string[segs.Length - start];
        for (int j = start; j < segs.Length; j++)
        {
            parts[j - start] = segs[j].Name;
        }

        return string.Join(".", parts);
    }
}