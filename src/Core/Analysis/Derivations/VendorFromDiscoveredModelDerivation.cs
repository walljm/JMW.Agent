namespace JMW.Discovery.Core.Analysis.Derivations;

/// <summary>
/// Infers a passively-discovered neighbor's vendor from its mDNS model string (e.g. "Nest Audio"
/// → Google, "Mac15,10" / "MacBookPro14,2" → Apple) when the observer reported a model but no
/// vendor. Emits the actual <see cref="FactPaths.DiscoveredVendor" /> fact (absent-guarded — an
/// observed UPnP manufacturer always wins), so the value projects into proj_discovered and rides
/// the existing promotion path onto the minted device — exactly like
/// <see cref="VendorOsFromDiscoveredTypeDerivation" /> does from the discovered device type.
///
/// This is the discovered-scope counterpart to <see cref="VendorFromModelDerivation" /> (which
/// handles a device's OWN SMBIOS model → the device-level vendor guess). A discovered model
/// belongs to the observer's Discovered[] subtree, so its vendor must be scoped to that station,
/// not to the observing device — hence a separate derivation rather than one shared output.
/// </summary>
public sealed class VendorFromDiscoveredModelDerivation : IDerivation
{
    public IReadOnlyList<string> Inputs { get; } = [FactPaths.DiscoveredModel, FactPaths.DiscoveredVendor];

    public IReadOnlyList<string> Outputs { get; } = [FactPaths.DiscoveredVendor];

    // Scope inferred from the inputs' shared list dimensions: Device[] + Discovered[] — one group
    // per discovered station, so the absent-guard below is per-station, not per-device.
    public IReadOnlyList<string>? Scope => null;

    public IReadOnlyList<Fact> Derive(IReadOnlyList<Fact> scopedFacts)
    {
        Fact? modelFact = null;
        bool hasVendor = false;

        foreach (Fact f in scopedFacts)
        {
            if (string.IsNullOrWhiteSpace(f.Value.AsString()))
            {
                continue;
            }

            switch (f.AttributePath)
            {
                case FactPaths.DiscoveredModel:
                    modelFact = f;
                    break;
                case FactPaths.DiscoveredVendor:
                    hasVendor = true;
                    break;
            }
        }

        // An observed vendor always wins — only fill the gap.
        if (hasVendor
         || modelFact is not { } model
         || ModelVendor.Resolve(model.Value.AsString()) is not { } vendor)
        {
            return [];
        }

        return [Fact.Create(AnalysisEngine.BuildId(FactPaths.DiscoveredVendor, model), vendor, model.CollectedAt)];
    }
}