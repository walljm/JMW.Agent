namespace JMW.Discovery.Core.Analysis.Derivations;

/// <summary>
/// Fans in a device's OS distro from whichever source is present — the device-reported
/// <see cref="FactPaths.SystemOsDistro" /> (authoritative) or, failing that, the inferred
/// <see cref="FactPaths.Derived.DeviceOsGuess" /> (a proxy-signal inference — see
/// <see cref="VendorOsFromDeviceBannerDerivation" />) — into one canonical field. Mirrors
/// <see cref="DeviceVendorDerivation" /> one level down: <see cref="FactPaths.SystemOsDistro" />
/// is itself the previously-raw, last-write-wins path, and this canonical output is what
/// proj_systems.os_distro now maps to (architecture-identity-facts.md §12, Phase 6b). Retired
/// <see cref="FactPaths.Derived.DeviceOsGuess" /> from its own "guess" projection column the same
/// way; the hydrated fan-in (§11) is what makes folding it in here safe — an inference can never
/// clobber a real value stored from a prior batch.
/// Scope is forced to [Device] rather than inferred: both inputs are Device[]-scoped with no
/// further list dimension, so inference would already yield [Device] — declared explicitly since
/// this is a fan-in (only one input is ever present per device) rather than the usual
/// "all inputs must be present" derivation shape.
/// </summary>
public sealed class SystemOsDistroDerivation : IDerivation
{
    public IReadOnlyList<string> Inputs { get; } =
    [
        FactPaths.SystemOsDistro,
        FactPaths.Derived.DeviceOsGuess,
    ];

    public IReadOnlyList<string> Outputs { get; } = [FactPaths.Derived.SystemOsDistroCanonical];

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

            string? distro = fact.Value.AsString();
            if (string.IsNullOrWhiteSpace(distro))
            {
                continue;
            }

            string id = AnalysisEngine.BuildId(Outputs[0], fact);
            return [Fact.Create(id, distro, fact.CollectedAt)];
        }

        return [];
    }
}
