namespace JMW.Discovery.Core.Analysis.Derivations;

/// <summary>
/// Shared shape for "priority fan-in" derivations: an ordered list of Device-scoped input paths
/// where, in practice, only one is ever populated per device — emit the first non-blank value
/// found, under a single canonical output path. The input order is just a tie-break for the rare
/// case more than one is present at once. See <see cref="DeviceVendorDerivation" /> and
/// <see cref="SystemOsDistroDerivation" /> for concrete examples; both also fold in an inferred
/// "guess" value as their lowest-priority input, made safe by the hydrated fan-in
/// (architecture-identity-facts.md §11) — an inference can never clobber a real value stored from
/// a prior batch.
/// </summary>
public abstract class PriorityFanInDerivation : IDerivation
{
    public abstract IReadOnlyList<string> Inputs { get; }

    /// <summary>The single fact path this fan-in emits to.</summary>
    protected abstract string Output { get; }

    public IReadOnlyList<string> Outputs => [Output];

    public IReadOnlyList<string> Scope => ["Device"];

    public IReadOnlyList<Fact> Derive(IReadOnlyList<Fact> scopedFacts)
    {
        foreach (string path in Inputs)
        {
            Fact? match = null;
            foreach (Fact candidate in scopedFacts)
            {
                if (candidate.AttributePath == path)
                {
                    match = candidate;
                    break;
                }
            }

            if (match is not { } fact)
            {
                continue;
            }

            string? value = fact.Value.AsString();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            string id = AnalysisEngine.BuildId(Output, fact);
            return [Fact.Create(id, value, fact.CollectedAt)];
        }

        return [];
    }
}
