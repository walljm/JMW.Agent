namespace JMW.Discovery.Core.Analysis;

/// <summary>
/// Canonicalizes MAC/IP values that appear as fact-ID DIMENSION KEYS. The <see cref="AnalysisEngine" />
/// value normalizers only touch fact VALUES; this runs at ingest (before storage) so a MAC/IP is
/// byte-identical whether it is a value or a key — DHCP lease MAC keys → bare hex, ARP/Discovered IP
/// keys → canonical IP. That keeps <c>facts_history</c> and every projection dimension consistent, and
/// lets the reporting joins compare keys directly. Reuses the same <see cref="MacFormat" /> /
/// <see cref="IpFormat" /> primitives as the value side (review D34/D2).
/// </summary>
public static class KeyNormalization
{
    // Dimension name → canonicalizer for that dimension's list key (returns null when the key is not a
    // MAC/IP, in which case the key is left unchanged).
    private static readonly Dictionary<string, Func<string, string?>> ByDimension = new(StringComparer.Ordinal)
    {
        ["Lease"] = MacFormat.ToBareHex, // Device[].Lease[<mac>] + Service[].DHCP.Scope[].Lease[<mac>]
        ["ARP"] = IpFormat.Canonicalize, // Device[].ARP[<ip>]
        ["Discovered"] = IpFormat.Canonicalize, // Device[].Discovered[<ip>]
    };

    /// <summary>
    /// Returns <paramref name="fact" /> with any normalizable dimension keys canonicalized, or
    /// the same instance unchanged when nothing applies.
    /// </summary>
    public static Fact Normalize(Fact fact)
    {
        string id = fact.Id;

        // Fast path: only parse+rebuild when the id references a normalizable dimension.
        if (id.IndexOf("Lease[", StringComparison.Ordinal) < 0
         && id.IndexOf("ARP[", StringComparison.Ordinal) < 0
         && id.IndexOf("Discovered[", StringComparison.Ordinal) < 0)
        {
            return fact;
        }

        FactSegment[] segments = FactSegment.ParsePath(id);
        bool changed = false;
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Key is { } key
             && ByDimension.TryGetValue(segments[i].Name, out Func<string, string?>? normalize)
             && normalize(key) is { } canonical
             && !string.Equals(canonical, key, StringComparison.Ordinal))
            {
                segments[i] = segments[i] with
                {
                    Key = canonical,
                };
                changed = true;
            }
        }

        if (!changed)
        {
            return fact;
        }

        string newId = string.Join('.', segments.Select(s => s.ToString()));
        return Fact.Create(newId, fact.Value, fact.CollectedAt);
    }
}