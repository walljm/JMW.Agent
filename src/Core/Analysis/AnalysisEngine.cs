using System.Text;

namespace JMW.Discovery.Core.Analysis;

/// <summary>
/// Runs normalization and layered derivation over a batch of raw facts.
/// Pipeline:
/// 1. Normalize  — apply per-value transforms, drop invalid values
/// 2. Derive     — run registered derivations in dependency order;
/// each layer's output is available to the next
/// The engine is stateless and re-entrant. Construct once, call Analyze per batch.
/// </summary>
public sealed class AnalysisEngine
{
    private readonly Dictionary<string, INormalizer> _normalizers;
    private readonly IReadOnlyList<IDerivation> _derivations; // topologically sorted

    public AnalysisEngine(
        IEnumerable<INormalizer> normalizers,
        IEnumerable<IDerivation> derivations
    )
    {
        _normalizers = new Dictionary<string, INormalizer>();
        foreach (INormalizer n in normalizers)
        {
            foreach (string pattern in n.AttributePathPatterns)
            {
                _normalizers[pattern] = n;
            }
        }

        _derivations = TopologicalSort(derivations.ToList());
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public IReadOnlyList<Fact> Analyze(IReadOnlyList<Fact> rawFacts)
    {
        List<Fact> normalized = Normalize(rawFacts);
        return Derive(normalized);
    }

    // ── Normalization ─────────────────────────────────────────────────────────

    private List<Fact> Normalize(IReadOnlyList<Fact> facts)
    {
        NormalizationContext ctx = BuildContext(facts);

        List<Fact> result = new(facts.Count);
        foreach (Fact fact in facts)
        {
            // AttributePath is a precomputed field — field access, no parsing.
            if (!_normalizers.TryGetValue(fact.AttributePath, out INormalizer? normalizer))
            {
                result.Add(fact);
                continue;
            }

            FactValue? normalized = normalizer.Normalize(fact.Value, ctx);
            if (normalized is null)
            {
                continue;
            }

            result.Add(
                normalized.Value == fact.Value
                    ? fact
                    : fact with
                    {
                        Value = normalized.Value,
                    }
            );
        }

        return result;
    }

    private static NormalizationContext BuildContext(IReadOnlyList<Fact> facts)
    {
        string? vendor = null, osFamily = null, osVersion = null;
        foreach (Fact fact in facts)
        {
            // AttributePath is a stored field — no allocation.
            if (fact.AttributePath == FactPaths.DeviceVendor)
            {
                vendor = fact.Value.AsString();
            }
            else if (fact.AttributePath == FactPaths.SystemOsFamily)
            {
                osFamily = fact.Value.AsString();
            }
            else if (fact.AttributePath == FactPaths.SystemOsVersion)
            {
                osVersion = fact.Value.AsString();
            }

            if (vendor is not null && osFamily is not null && osVersion is not null)
            {
                break;
            }
        }

        return new NormalizationContext(vendor, osFamily, osVersion);
    }

    // ── Derivation ────────────────────────────────────────────────────────────

    private List<Fact> Derive(List<Fact> facts)
    {
        Dictionary<string, List<Fact>> index = new();
        foreach (Fact fact in facts)
        {
            IndexFact(index, fact);
        }

        foreach (IDerivation derivation in _derivations)
        {
            List<Fact> inputFacts = derivation.Inputs
                .SelectMany(p => index.GetValueOrDefault(p) ?? [])
                .ToList();

            if (inputFacts.Count == 0)
            {
                continue;
            }

            IReadOnlyList<string> scopeDims = derivation.Scope ?? InferScope(derivation.Inputs);

            IEnumerable<IGrouping<string, Fact>> groups = inputFacts.GroupBy(f => ScopeKey(f, scopeDims));

            foreach (IGrouping<string, Fact> group in groups)
            {
                IReadOnlyList<Fact> derived = derivation.Derive(group.ToList());
                foreach (Fact df in derived)
                {
                    facts.Add(df);
                    IndexFact(index, df);
                }
            }
        }

        return facts;
    }

    // ── Scope inference ───────────────────────────────────────────────────────

    public static IReadOnlyList<string> InferScope(IReadOnlyList<string> inputPatterns)
    {
        if (inputPatterns.Count == 0)
        {
            return [];
        }

        // Extract list dimension names (in order) from each pattern
        List<List<string>> dimSets = inputPatterns
            .Select(p => FactSegment.ParsePath(p)
                .Where(s => s.IsList)
                .Select(s => s.Name)
                .ToList()
            )
            .ToList();

        // Intersect all sets to find common dimensions
        HashSet<string> common = new(dimSets[0]);
        foreach (List<string> set in dimSets.Skip(1))
        {
            common.IntersectWith(set);
        }

        // Preserve order from the first pattern
        return dimSets[0].Where(common.Contains).ToList();
    }

    // Scope key groups facts by their key values for the scope dimensions.
    // Calls ParseId() — but only on facts within a scope group (small lists),
    // not on the full batch.
    private static string ScopeKey(Fact fact, IReadOnlyList<string> scopeDims)
    {
        if (scopeDims.Count == 0)
        {
            return string.Empty;
        }

        FactSegment[] segs = fact.ParseId();
        StringBuilder sb = new(64);
        bool first = true;

        foreach (string dim in scopeDims)
        {
            foreach (FactSegment seg in segs)
            {
                if (seg.IsList && seg.Name == dim)
                {
                    if (!first)
                    {
                        sb.Append('|');
                    }

                    sb.Append(dim).Append('=').Append(seg.Key ?? string.Empty);
                    first = false;
                    break;
                }
            }
        }

        return sb.ToString();
    }

    // ── ID building ───────────────────────────────────────────────────────────

    /// <summary>
    /// Fills scope keys into an attribute_path template using a context fact.
    /// template = "Device[].Interface[].TotalBytes"
    /// context  = "Device[r1].Interface[eth0].InBytes"
    /// → "Device[r1].Interface[eth0].TotalBytes"
    /// </summary>
    public static string BuildId(string template, Fact contextFact)
    {
        // Build lookup of segment name → key for list segments with keys
        Dictionary<string, string>? replacements = null;
        foreach (FactSegment seg in contextFact.ParseId())
        {
            if (seg.IsList && seg.Key is { Length: > 0 })
            {
                (replacements ??= new Dictionary<string, string>())[seg.Name] = seg.Key;
            }
        }

        if (replacements is null)
        {
            return template;
        }

        // Single-pass scan: find "Name[]" patterns and substitute "Name[key]"
        StringBuilder sb = new(template.Length + 32);
        ReadOnlySpan<char> span = template.AsSpan();
        int pos = 0;

        while (pos < span.Length)
        {
            // Find next "[]"
            int bracketOpen = span[pos..].IndexOf("[]");
            if (bracketOpen < 0)
            {
                sb.Append(span[pos..]);
                break;
            }

            bracketOpen += pos;

            // Find the segment name before '[' (scan backwards to the previous '.' or start)
            int nameEnd = bracketOpen;
            int nameStart = nameEnd - 1;
            while (nameStart > 0 && span[nameStart - 1] != '.')
            {
                nameStart--;
            }

            ReadOnlySpan<char> name = span[nameStart..nameEnd];

            // Check if this name has a replacement key
            // Need to convert to string for dictionary lookup — small cost, one per replaced segment
            string nameStr = name.ToString();
            if (replacements.TryGetValue(nameStr, out string? key))
            {
                sb.Append(span[pos..bracketOpen]);
                sb.Append('[');
                sb.Append(key);
                sb.Append(']');
            }
            else
            {
                sb.Append(span[pos..(bracketOpen + 2)]); // include "[]"
            }

            pos = bracketOpen + 2;
        }

        return sb.ToString();
    }

    // ── Topological sort ──────────────────────────────────────────────────────
    //
    // A derivation D depends on derivation E if any of D's inputs match
    // any of E's declared outputs. Kahn's algorithm over that dependency graph.

    private static List<IDerivation> TopologicalSort(List<IDerivation> derivations)
    {
        // output pattern → which derivation produces it
        Dictionary<string, IDerivation> producerOf = new();
        foreach (IDerivation d in derivations)
        {
            foreach (string output in d.Outputs)
            {
                producerOf[output] = d;
            }
        }

        // dependencies[D] = set of derivations D depends on
        Dictionary<IDerivation, HashSet<IDerivation>> dependencies = derivations.ToDictionary(
            d => d,
            d => d.Inputs
                .Where(producerOf.ContainsKey)
                .Select(i => producerOf[i])
                .Where(producer => producer != d)
                .ToHashSet()
        );

        // dependents[D] = derivations that depend on D
        Dictionary<IDerivation, List<IDerivation>> dependents = derivations.ToDictionary(
            d => d,
            _ => new List<IDerivation>()
        );
        Dictionary<IDerivation, int> inDegree = derivations.ToDictionary(d => d, d => dependencies[d].Count);

        foreach ((IDerivation d, HashSet<IDerivation> deps) in dependencies)
        {
            foreach (IDerivation dep in deps)
            {
                dependents[dep].Add(d);
            }
        }

        Queue<IDerivation> queue = new(derivations.Where(d => inDegree[d] == 0));
        List<IDerivation> result = new(derivations.Count);

        while (queue.Count > 0)
        {
            IDerivation d = queue.Dequeue();
            result.Add(d);
            foreach (IDerivation dependent in dependents[d])
            {
                if (--inDegree[dependent] == 0)
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        if (result.Count != derivations.Count)
        {
            throw new InvalidOperationException(
                "Cycle detected in derivation dependencies. "
              + $"Unresolved: {string.Join(", ", derivations.Except(result).Select(d => d.GetType().Name))}"
            );
        }

        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void IndexFact(Dictionary<string, List<Fact>> index, Fact fact)
    {
        // AttributePath is a stored field — no parsing.
        if (!index.TryGetValue(fact.AttributePath, out List<Fact>? list))
        {
            list = [];
            index[fact.AttributePath] = list;
        }

        list.Add(fact);
    }
}