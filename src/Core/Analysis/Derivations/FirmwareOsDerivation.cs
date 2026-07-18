namespace JMW.Discovery.Core.Analysis.Derivations;

/// <summary>
/// Devices whose kind marks them as embedded/appliance-class (<see cref="DeviceKinds.FirmwareOnlyKinds" />
/// — cameras, thermostats, BACnet/Modbus controllers, printers…) never report a real OS inventory,
/// so their OS column stays blank forever. This emits the actual fact —
/// <see cref="FactPaths.SystemOsFamily" /> = "firmware" (lowercase, matching the
/// LowercaseTrimNormalizer convention for that path) — rather than a coalesced display guess,
/// whenever the batch carries a firmware-class <see cref="FactPaths.DeviceKind" /> and no real OS
/// fact accompanies it.
///
/// Like <see cref="DeviceKindDerivation" /> this writes to a collector-writable path, and the
/// absence check is batch-local (the analysis engine sees one ingest batch, not the device's stored
/// facts). That is safe for this set because firmware-class kinds are curated to devices no OS
/// collector ever runs against — the only way such a device gets a real OS fact is a collector that
/// would emit kind and OS in the same batch, in which case this stays silent.
/// </summary>
public sealed class FirmwareOsDerivation : IDerivation
{
    public IReadOnlyList<string> Inputs { get; } =
    [
        FactPaths.DeviceKind,
        FactPaths.SystemOsFamily,
        FactPaths.SystemOsDistro,
    ];

    public IReadOnlyList<string> Outputs { get; } = [FactPaths.SystemOsFamily];

    public IReadOnlyList<string> Scope => ["Device"];

    public IReadOnlyList<Fact> Derive(IReadOnlyList<Fact> scopedFacts)
    {
        Fact? kindFact = null;
        bool hasOs = false;

        foreach (Fact f in scopedFacts)
        {
            string? s = f.Value.AsString();
            if (string.IsNullOrWhiteSpace(s))
            {
                continue;
            }

            switch (f.AttributePath)
            {
                case FactPaths.DeviceKind:
                    kindFact = f;
                    break;
                case FactPaths.SystemOsFamily:
                case FactPaths.SystemOsDistro:
                    hasOs = true;
                    break;
            }
        }

        if (hasOs
            || kindFact is not { } kind
            || kind.Value.AsString() is not { } kindValue
            || !DeviceKinds.FirmwareOnlyKinds.Contains(kindValue))
        {
            return [];
        }

        string id = AnalysisEngine.BuildId(Outputs[0], kind);
        return [Fact.Create(id, "firmware", kind.CollectedAt)];
    }
}