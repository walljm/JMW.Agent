namespace JMW.Discovery.Core.Analysis.Derivations;

/// <summary>
/// Computes TotalBytes (RxBytes + TxBytes) for each interface on each device.
/// Scope is inferred from the common list dimensions of the two input patterns:
/// Device[].Interface[].RxBytes ∩ Device[].Interface[].TxBytes → [Device, Interface]
/// One derivation instance runs per (device, interface) pair.
/// </summary>
public sealed class TotalBytesDerivation : IDerivation
{
    private const string RxPath = FactPaths.InterfaceRxBytes;
    private const string TxPath = FactPaths.InterfaceTxBytes;
    private const string OutputPath = FactPaths.Derived.InterfaceTotalBytes;

    public IReadOnlyList<string> Inputs => [RxPath, TxPath];
    public IReadOnlyList<string> Outputs => [OutputPath];

    // Scope null → inferred: both inputs share Device[] and Interface[] → correct grouping.
    public IReadOnlyList<string>? Scope => null;

    public IReadOnlyList<Fact> Derive(IReadOnlyList<Fact> scopedFacts)
    {
        Fact? rxFact = null;
        Fact? txFact = null;

        foreach (Fact fact in scopedFacts)
        {
            string path = fact.AttributePath;
            if (path == RxPath)
            {
                rxFact = fact;
            }
            else if (path == TxPath)
            {
                txFact = fact;
            }
        }

        if (rxFact is not { } rx || txFact is not { } tx)
        {
            return [];
        }

        long? rxVal = rx.Value.AsLong();
        long? txVal = tx.Value.AsLong();

        if (rxVal is null || txVal is null)
        {
            return [];
        }

        string id = AnalysisEngine.BuildId(OutputPath, rx);
        Fact total = Fact.Create(id, rxVal.Value + txVal.Value, rx.CollectedAt);
        return [total];
    }
}