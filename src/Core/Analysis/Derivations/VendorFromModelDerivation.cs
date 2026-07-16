namespace JMW.Discovery.Core.Analysis.Derivations;

/// <summary>
/// Infers a device's vendor from its own SMBIOS model (`Device[].Hardware.SystemModel`, already
/// DmiDecode-cleaned at collection time) via the shared <see cref="ModelVendor" /> table — a
/// model line names a single manufacturer it always ships under (a "ThinkPad" is always Lenovo,
/// an "OptiPlex" is always Dell), so the match is safe even with no other vendor-reporting
/// protocol in play.
/// Outputs to the same "guess" field as VendorFromOsDistroDerivation/VendorOsFromDeviceBannerDerivation
/// (see docs/plans/vendor-derivation-updates.md §3): an inference from a proxy signal, kept
/// separate from the canonical vendor fan-in.
/// A passively-discovered neighbor's model (`Device[].Discovered[].Model`) is handled by
/// <see cref="VendorFromDiscoveredModelDerivation" /> instead, which scopes the vendor to the
/// discovered station rather than mis-attributing it to the observing device.
/// </summary>
public sealed class VendorFromModelDerivation : IDerivation
{
    public IReadOnlyList<string> Inputs { get; } = [FactPaths.HwSystemModel];

    public IReadOnlyList<string> Outputs { get; } = [FactPaths.Derived.DeviceVendorGuess];

    public IReadOnlyList<string> Scope => ["Device"];

    public IReadOnlyList<Fact> Derive(IReadOnlyList<Fact> scopedFacts)
    {
        foreach (Fact fact in scopedFacts)
        {
            if (fact.AttributePath != FactPaths.HwSystemModel)
            {
                continue;
            }

            if (ModelVendor.Resolve(fact.Value.AsString()) is { } vendor)
            {
                string id = AnalysisEngine.BuildId(Outputs[0], fact);
                return [Fact.Create(id, vendor, fact.CollectedAt)];
            }
        }

        return [];
    }
}