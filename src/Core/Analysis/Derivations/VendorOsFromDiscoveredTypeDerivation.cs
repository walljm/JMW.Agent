namespace JMW.Discovery.Core.Analysis.Derivations;

/// <summary>
/// Derives a discovered station's vendor and OS from its device type (the discovered-row "kind")
/// when the type alone pins the hardware: an OnHub / Google Wifi mesh unit is by definition Google
/// hardware running a ChromiumOS-derived Linux. Emits the actual
/// <see cref="FactPaths.DiscoveredVendor" /> / <see cref="FactPaths.DiscoveredOs" /> facts
/// (absent-guarded — an observed UPnP manufacturer or scanner OS hint always wins), so the values
/// project into proj_discovered and ride the existing promotion paths onto the minted device.
/// This keeps device-specific knowledge out of DiscoveryMaterializer, which promotes columns
/// generically.
/// </summary>
public sealed class VendorOsFromDiscoveredTypeDerivation : IDerivation
{
    /// <summary>Case-insensitive device-type substring → known (vendor, os). Extend as other
    /// self-identifying device types appear.</summary>
    private static readonly (string TypeToken, string Vendor, string Os)[] Rules =
    [
        ("onhub", "Google", "linux"),
    ];

    public IReadOnlyList<string> Inputs { get; } =
    [
        FactPaths.DiscoveredDeviceType,
        FactPaths.DiscoveredVendor,
        FactPaths.DiscoveredOs,
    ];

    public IReadOnlyList<string> Outputs { get; } =
    [
        FactPaths.DiscoveredVendor,
        FactPaths.DiscoveredOs,
    ];

    // Inferred from the inputs' shared list dimensions: Device[] + Discovered[] — one scope
    // group per discovered station.
    public IReadOnlyList<string>? Scope => null;

    public IReadOnlyList<Fact> Derive(IReadOnlyList<Fact> scopedFacts)
    {
        Fact? typeFact = null;
        bool hasVendor = false;
        bool hasOs = false;

        foreach (Fact f in scopedFacts)
        {
            if (string.IsNullOrWhiteSpace(f.Value.AsString()))
            {
                continue;
            }

            switch (f.AttributePath)
            {
                case FactPaths.DiscoveredDeviceType:
                    typeFact = f;
                    break;
                case FactPaths.DiscoveredVendor:
                    hasVendor = true;
                    break;
                case FactPaths.DiscoveredOs:
                    hasOs = true;
                    break;
            }
        }

        if (typeFact is not { } type || (hasVendor && hasOs) || type.Value.AsString() is not { } deviceType)
        {
            return [];
        }

        foreach ((string token, string vendor, string os) in Rules)
        {
            if (!deviceType.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            List<Fact> results = new(2);
            if (!hasVendor)
            {
                results.Add(Fact.Create(AnalysisEngine.BuildId(FactPaths.DiscoveredVendor, type), vendor, type.CollectedAt));
            }

            if (!hasOs)
            {
                results.Add(Fact.Create(AnalysisEngine.BuildId(FactPaths.DiscoveredOs, type), os, type.CollectedAt));
            }

            return results;
        }

        return [];
    }
}
