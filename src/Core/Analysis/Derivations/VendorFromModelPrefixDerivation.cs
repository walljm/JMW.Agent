namespace JMW.Discovery.Core.Analysis.Derivations;

/// <summary>
/// Infers a device's vendor from an exact-prefix match against a curated model-family table —
/// model line names a single manufacturer invented and always ships under (a "ThinkPad" is
/// always Lenovo, an "OptiPlex" is always Dell), so a prefix match is safe even with no other
/// vendor-reporting protocol in play. Reads both `Device[].Hardware.SystemModel` (SMBIOS,
/// already DmiDecode-cleaned at collection time) and `Device[].Discovered[].Model` (passive
/// discovery — ONVIF/UPnP/HTTP identity) since either can carry a recognizable model string.
/// See docs/plans/vendor-derivation-updates.md §2.3.
/// Deliberately a small curated prefix list, not a generic scan over free text (plan §1.3) —
/// e.g. matching anywhere a substring like "Pro" or "Air" appears would be far too loose;
/// StartsWith against a real product-line name is the safe form here.
/// Outputs to the same "guess" field as VendorFromOsDistroDerivation/VendorOsFromDeviceBannerDerivation
/// (see plan §3): an inference from a proxy signal, kept separate from the canonical vendor fan-in.
/// </summary>
public sealed class VendorFromModelPrefixDerivation : IDerivation
{
    public IReadOnlyList<string> Inputs { get; } = [FactPaths.HwSystemModel, FactPaths.DiscoveredModel];

    public IReadOnlyList<string> Outputs { get; } = [FactPaths.Derived.DeviceVendorGuess];

    public IReadOnlyList<string> Scope => ["Device"];

    private static readonly (string Prefix, string Vendor)[] KnownModelPrefixes =
    [
        ("ThinkPad", "Lenovo"),
        ("ThinkCentre", "Lenovo"),
        ("ThinkStation", "Lenovo"),
        ("OptiPlex", "Dell"),
        ("Latitude", "Dell"),
        ("PowerEdge", "Dell"),
        ("EliteBook", "HP"),
        ("Pavilion", "HP"),
        ("LaserJet", "HP"),
        ("Galaxy", "Samsung"),
        ("iPhone", "Apple"),
        ("iPad", "Apple"),
        ("MacBook", "Apple"),
        ("iMac", "Apple"),
        ("Surface", "Microsoft"),
        ("ROG", "ASUS"),
        ("ZenBook", "ASUS"),
    ];

    public IReadOnlyList<Fact> Derive(IReadOnlyList<Fact> scopedFacts)
    {
        foreach (Fact fact in scopedFacts)
        {
            if (fact.AttributePath != FactPaths.HwSystemModel && fact.AttributePath != FactPaths.DiscoveredModel)
            {
                continue;
            }

            string? model = fact.Value.AsString();
            if (string.IsNullOrWhiteSpace(model))
            {
                continue;
            }

            string trimmed = model.Trim();
            foreach ((string prefix, string vendor) in KnownModelPrefixes)
            {
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    string id = AnalysisEngine.BuildId(Outputs[0], fact);
                    return [Fact.Create(id, vendor, fact.CollectedAt)];
                }
            }
        }

        return [];
    }
}