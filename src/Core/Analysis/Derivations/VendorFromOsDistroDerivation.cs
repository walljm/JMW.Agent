namespace JMW.Discovery.Core.Analysis.Derivations;

/// <summary>
/// Infers a device's vendor from a vendor-locked OS family/distro name — network-appliance and
/// NAS operating systems that only ever run on one manufacturer's hardware (RouterOS only ships
/// on Mikrotik, JunOS only on Juniper, etc.), so an exact match is a safe, high-confidence signal
/// even with no other vendor-reporting protocol in play. Exists to fill in vendor for devices seen
/// only via passive discovery (no agent, no BACnet/Modbus, no self-reported vendor) — see
/// docs/plans/vendor-derivation-updates.md §2.1.
/// Outputs to the separate "guess" field (<see cref="FactPaths.Derived.DeviceVendorGuess" />), not
/// the canonical vendor fan-in — this is an inference from a proxy signal, not a self-report, and
/// keeping it separate keeps the distinction auditable (see plan §3).
/// Values are matched case-insensitively: by the time this derivation runs, the normal pipeline
/// has already lowercased Device[].OS.Family/OS.Distro (see AnalysisLibrary's LowercaseTrimNormalizer),
/// but this derivation is also exercised directly in unit tests against raw-cased input.
/// </summary>
public sealed class VendorFromOsDistroDerivation : IDerivation
{
    public IReadOnlyList<string> Inputs { get; } = [FactPaths.SystemOsFamily, FactPaths.SystemOsDistro];

    public IReadOnlyList<string> Outputs { get; } = [FactPaths.Derived.DeviceVendorGuess];

    public IReadOnlyList<string> Scope => ["Device"];

    // Vendor-exclusive OS names — each of these only ever runs on one manufacturer's hardware,
    // so exact match is safe (unlike a generic substring scan over free text; see plan §1.3).
    private static readonly Dictionary<string, string> VendorLockedOsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RouterOS"] = "Mikrotik",
        ["JunOS"] = "Juniper",
        ["IOS-XE"] = "Cisco",
        ["NX-OS"] = "Cisco",
        ["EdgeOS"] = "Ubiquiti",
        ["UniFi OS"] = "Ubiquiti",
        ["PAN-OS"] = "Palo Alto Networks",
        ["FortiOS"] = "Fortinet",
        ["ArubaOS"] = "Aruba",
        ["DSM"] = "Synology",
        ["QTS"] = "QNAP",
    };

    public IReadOnlyList<Fact> Derive(IReadOnlyList<Fact> scopedFacts)
    {
        foreach (Fact fact in scopedFacts)
        {
            if (fact.AttributePath != FactPaths.SystemOsFamily && fact.AttributePath != FactPaths.SystemOsDistro)
            {
                continue;
            }

            string? value = fact.Value.AsString();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (VendorLockedOsNames.TryGetValue(value.Trim(), out string? vendor))
            {
                string id = AnalysisEngine.BuildId(Outputs[0], fact);
                return [Fact.Create(id, vendor, fact.CollectedAt)];
            }
        }

        return [];
    }
}