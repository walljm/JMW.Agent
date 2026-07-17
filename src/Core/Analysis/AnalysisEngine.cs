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
        HydratableInputPaths = ComputeHydratableInputPaths(_derivations);
        AllDerivationInputPaths = _derivations.SelectMany(d => d.Inputs).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The derivation input paths worth hydrating from current state before deriving
    /// (docs/plans/architecture-identity-facts.md §11): a raw input (not itself any derivation's
    /// output — derived values are always recomputed, never frozen) that is <b>Device-scoped</b>
    /// (DimKey == "Device"). Restricting to Device-scoped is the deliberate first cut: it covers
    /// the priority fan-ins that suffer batch-locality clobbering (vendor/os/model/hostname) while
    /// excluding high-cardinality per-child paths (per-interface/-filesystem metrics) whose
    /// hydration would be costly at the 80K-device design target. The pipeline reads the current
    /// value of these paths for the devices in a batch and passes them to
    /// <see cref="Analyze(IReadOnlyList{Fact}, IReadOnlyList{Fact})" /> so a partial batch is
    /// derived against full current state, not just the delta.
    /// </summary>
    public IReadOnlySet<string> HydratableInputPaths { get; }

    /// <summary>
    /// Every path consumed as ANY derivation's input, unfiltered — unlike
    /// <see cref="HydratableInputPaths" /> this includes paths that are themselves another
    /// derivation's output (e.g. <see cref="FactPaths.Derived.DeviceVendorGuess" />, consumed by
    /// <see cref="Derivations.DeviceVendorDerivation" />) and paths outside the Device scope. Used
    /// by <c>FactPathRoutingFitnessTests</c> as a fourth valid routing home: a pure intermediate
    /// that has no projection column or fact view of its own is still a legitimately routed fact
    /// path if a derivation consumes it (architecture-identity-facts.md §12).
    /// </summary>
    public IReadOnlySet<string> AllDerivationInputPaths { get; }

    private static HashSet<string> ComputeHydratableInputPaths(IReadOnlyList<IDerivation> derivations)
    {
        HashSet<string> outputs = derivations.SelectMany(d => d.Outputs).ToHashSet(StringComparer.Ordinal);
        return derivations
            .SelectMany(d => d.Inputs)
            .Where(p => !outputs.Contains(p) && Fact.DeriveDimKey(p) == "Device")
            .ToHashSet(StringComparer.Ordinal);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public IReadOnlyList<Fact> Analyze(IReadOnlyList<Fact> rawFacts)
    {
        List<Fact> normalized = Normalize(rawFacts);
        return Derive(normalized);
    }

    /// <summary>
    /// Same as <see cref="Analyze(IReadOnlyList{Fact})" /> but derives against full current state:
    /// <paramref name="hydratedInputs" /> are current values (already normalized, read from storage)
    /// of <see cref="HydratableInputPaths" /> for the devices in the batch, injected only where the
    /// batch doesn't already carry that fact id, so a priority fan-in / combinational derivation sees
    /// every input that is currently true — not just the ones that changed this cycle
    /// (docs/plans/architecture-identity-facts.md §11). The injected hydration facts are subtracted
    /// from the result: they are not newly observed, so they must not be re-appended to history or
    /// re-routed. Derived outputs and the batch's own facts are returned unchanged. The engine stays
    /// stateless — prior state is passed in as data.
    /// </summary>
    public IReadOnlyList<Fact> Analyze(IReadOnlyList<Fact> rawFacts, IReadOnlyList<Fact> hydratedInputs)
    {
        List<Fact> normalized = Normalize(rawFacts);

        if (hydratedInputs.Count == 0)
        {
            return Derive(normalized);
        }

        // Inject hydrated inputs the batch doesn't already carry (batch value always wins).
        HashSet<string> presentIds = normalized.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        HashSet<string> injectedIds = new(StringComparer.Ordinal);
        List<Fact> forDerive = [.. normalized];
        foreach (Fact h in hydratedInputs)
        {
            if (presentIds.Contains(h.Id) || !injectedIds.Add(h.Id))
            {
                continue;
            }

            forDerive.Add(h);
        }

        // Derive() appends derived facts to forDerive and returns it. A hydratable path is never a
        // derivation output (ComputeHydratableInputPaths excludes outputs), so no derived fact can
        // share an id with an injected input — subtracting injectedIds removes exactly the injected
        // hydration facts, leaving the batch facts and every derived output.
        List<Fact> derived = Derive(forDerive);
        return injectedIds.Count == 0
            ? derived
            : derived.Where(f => !injectedIds.Contains(f.Id)).ToList();
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