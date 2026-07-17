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
/// </summary>
public sealed class SystemOsDistroDerivation : PriorityFanInDerivation
{
    public override IReadOnlyList<string> Inputs { get; } =
    [
        FactPaths.SystemOsDistro,
        FactPaths.Derived.DeviceOsGuess,
    ];

    protected override string Output => FactPaths.Derived.SystemOsDistroCanonical;
}
