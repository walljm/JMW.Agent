namespace JMW.Discovery.Core.Analysis.Derivations;

/// <summary>
/// Base for derivations that combine exactly two same-scope input facts into one output fact
/// (review D33) — the shape shared by <see cref="UsedPercentDerivation" />,
/// <see cref="MemoryUsedPercentDerivation" />, and <see cref="BatteryHealthDerivation" />: pull two
/// named inputs from <c>scopedFacts</c>, bail (empty result) if either is missing or unparseable,
/// combine them, and emit one fact using the first input as the id/timestamp reference.
/// <see cref="TotalBytesDerivation" /> does NOT use this base — it outputs a <c>long</c> sum, and
/// generalizing that would mean either changing its stored fact type (a real compatibility
/// concern — <c>facts_history</c> has separate <c>value_long</c>/<c>value_double</c> columns) or
/// adding an output-type parameter that would undercut the "one-line registration" this exists
/// for. Left as its own class.
/// </summary>
public abstract class BinaryDerivation : IDerivation
{
    private readonly string _inPathA;
    private readonly string _inPathB;
    private readonly string _outPath;
    private readonly Func<Fact, double?> _extract;
    private readonly Func<double, double, double?> _combine;

    /// <param name="outPath"></param>
    /// <param name="extract">
    /// Reads the numeric value out of an input fact — <c>f => f.Value.AsLong()</c> or
    /// <c>f => f.Value.AsDouble()</c>, matching however that input's <see cref="FactValueKind" />
    /// is actually stored (a mismatched accessor returns null, and this reports the fact as
    /// "missing" rather than throwing).
    /// </param>
    /// <param name="combine">
    /// Computes the output value from the two extracted inputs (A, B — in declaration order).
    /// Return null to suppress emission for this scope (e.g. a divide-by-zero guard).
    /// </param>
    /// <param name="inPathA"></param>
    /// <param name="inPathB"></param>
    protected BinaryDerivation(
        string inPathA,
        string inPathB,
        string outPath,
        Func<Fact, double?> extract,
        Func<double, double, double?> combine
    )
    {
        _inPathA = inPathA;
        _inPathB = inPathB;
        _outPath = outPath;
        _extract = extract;
        _combine = combine;
    }

    public IReadOnlyList<string> Inputs => [_inPathA, _inPathB];

    public IReadOnlyList<string> Outputs => [_outPath];

    public IReadOnlyList<Fact> Derive(IReadOnlyList<Fact> scopedFacts)
    {
        Fact? factA = null;
        Fact? factB = null;

        foreach (Fact fact in scopedFacts)
        {
            string path = fact.AttributePath;
            if (path == _inPathA)
            {
                factA = fact;
            }
            else if (path == _inPathB)
            {
                factB = fact;
            }
        }

        if (factA is not { } a || factB is not { } b)
        {
            return [];
        }

        double? valueA = _extract(a);
        double? valueB = _extract(b);

        if (valueA is null || valueB is null)
        {
            return [];
        }

        double? result = _combine(valueA.Value, valueB.Value);
        if (result is null)
        {
            return [];
        }

        string id = AnalysisEngine.BuildId(_outPath, a);
        return [Fact.Create(id, result.Value, a.CollectedAt)];
    }
}