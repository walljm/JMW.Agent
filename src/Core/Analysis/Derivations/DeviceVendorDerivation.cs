namespace JMW.Discovery.Core.Analysis.Derivations;

/// <summary>
/// Fans in a device's vendor from whichever protocol collector actually reports it — self-reported
/// hardware (dmidecode), BACnet, Modbus, or Google Wifi's own "Device[].Vendor" assertion — into
/// one canonical field. In practice a device is only ever monitored by ONE of these paths (a
/// BACnet controller isn't also a dmidecode-inspectable host), so this is a fan-in, not conflict
/// resolution; the input order below is just a tie-break if that ever stops holding.
/// <see cref="FactPaths.Derived.DeviceVendorGuess" /> is the last (lowest-priority) input — an
/// inference from a proxy signal (OS distro/model/hostname/banner — see the VendorFrom*
/// derivations), not a protocol self-report. Retired from its own "guess" projection column
/// (architecture-identity-facts.md §12, Phase 6a); the hydrated fan-in (§11) is what makes folding
/// it in here safe — an inference can never clobber a real value stored from a prior batch.
/// </summary>
public sealed class DeviceVendorDerivation : PriorityFanInDerivation
{
    public override IReadOnlyList<string> Inputs { get; } =
    [
        FactPaths.DeviceVendor,
        FactPaths.HwSystemVendor,
        FactPaths.BacnetVendorName,
        FactPaths.ModbusVendorName,
        FactPaths.Derived.DeviceVendorGuess,
    ];

    protected override string Output => FactPaths.Derived.DeviceVendorCanonical;
}
