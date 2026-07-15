namespace JMW.Discovery.Core.Analysis.Derivations;

/// <summary>
/// Infers a device's OS distro from a curated set of known signature substrings inside its SNMP
/// sysDescr string (`Device[].SNMP.SysDescr`) — the network/NAS-appliance OS names that are
/// vendor-exclusive by convention (see VendorFromOsDistroDerivation and
/// VendorFromSnmpSysDescrDerivation, which match the same kind of signal for the canonical
/// vendor). This is the OS-side analog: same input, same curated-substring discipline (plan
/// §1.3 — no generic scan over arbitrary product names), different output.
/// See docs/plans/vendor-derivation-updates.md §5 (OS derivation, scope broadened from vendor).
/// Outputs to a separate guess field, not the raw Device[].OS.Distro path — see
/// FactPaths.Derived.DeviceOsGuess for why (last-write-wins projection risk).
/// </summary>
public sealed class OsFromSnmpSysDescrDerivation : IDerivation
{
    public IReadOnlyList<string> Inputs { get; } = [FactPaths.SnmpSysDescr];

    public IReadOnlyList<string> Outputs { get; } = [FactPaths.Derived.DeviceOsGuess];

    public IReadOnlyList<string> Scope => ["Device"];

    // Ordered longest/most-specific first — same reasoning as VendorFromSnmpSysDescrDerivation
    // (e.g. "IOS-XE" before "IOS" avoids misclassifying an IOS-XE box as plain "IOS"). Only OS
    // names distinctive enough to search for as a free-text substring safely — DSM/QTS (used as
    // an exact-field match in VendorFromOsDistroDerivation) are excluded here as too short/generic
    // to substring-match safely against arbitrary sysDescr prose.
    private static readonly (string Signature, string OsName)[] KnownSignatures =
    [
        ("Cisco IOS-XE", "Cisco IOS-XE"),
        ("Cisco NX-OS", "Cisco NX-OS"),
        ("Cisco IOS", "Cisco IOS"),
        ("RouterOS", "RouterOS"),
        ("JunOS", "JunOS"),
        ("EdgeOS", "EdgeOS"),
        ("UniFi OS", "UniFi OS"),
        ("PAN-OS", "PAN-OS"),
        ("FortiOS", "FortiOS"),
        ("ArubaOS", "ArubaOS"),
    ];

    public IReadOnlyList<Fact> Derive(IReadOnlyList<Fact> scopedFacts)
    {
        foreach (Fact fact in scopedFacts)
        {
            if (fact.AttributePath != FactPaths.SnmpSysDescr)
            {
                continue;
            }

            string? sysDescr = fact.Value.AsString();
            if (string.IsNullOrWhiteSpace(sysDescr))
            {
                continue;
            }

            foreach ((string signature, string osName) in KnownSignatures)
            {
                if (sysDescr.Contains(signature, StringComparison.OrdinalIgnoreCase))
                {
                    string id = AnalysisEngine.BuildId(Outputs[0], fact);
                    return [Fact.Create(id, osName, fact.CollectedAt)];
                }
            }
        }

        return [];
    }
}