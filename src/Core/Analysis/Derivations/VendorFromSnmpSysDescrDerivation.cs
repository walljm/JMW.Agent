namespace JMW.Discovery.Core.Analysis.Derivations;

/// <summary>
/// Infers a device's vendor from a curated set of known signature substrings inside its SNMP
/// sysDescr string (`Device[].SNMP.SysDescr`, collected by SnmpCollector but otherwise only
/// ever rendered as free text — see FactViewLibrary's "Description" column). Deliberately a
/// small curated substring list, not a generic scan over every known vendor name: sysDescr is
/// free text an operator could otherwise stuff with anything, so only signatures that are
/// vendor-exclusive by convention (an OS/product name a vendor invented) are safe to match.
/// See docs/plans/vendor-derivation-updates.md §2.2.
/// Overlaps in effect with VendorFromOsDistroDerivation (some devices report the same OS name
/// via both SNMP sysDescr and Device[].OS.Distro) — both are worth having since they're
/// populated by different collectors and won't always both fire for the same device.
/// Outputs to the same "guess" field as VendorFromOsDistroDerivation (see plan §3): an inference
/// from a proxy signal, not a self-report, kept separate from the canonical vendor fan-in.
/// </summary>
public sealed class VendorFromSnmpSysDescrDerivation : IDerivation
{
    public IReadOnlyList<string> Inputs { get; } = [FactPaths.SnmpSysDescr];

    public IReadOnlyList<string> Outputs { get; } = [FactPaths.Derived.DeviceVendorGuess];

    public IReadOnlyList<string> Scope => ["Device"];

    // Ordered longest/most-specific first — e.g. "Cisco IOS-XE" and "Cisco NX-OS" before the
    // shorter "Cisco IOS" (a plain substring match on "Cisco IOS" would otherwise also fire on
    // "Cisco IOS-XE Software..." sysDescr text). Each substring is a vendor-invented product/OS
    // name, not a generic word, so a case-insensitive Contains is safe here (unlike a scan over
    // vendor names).
    private static readonly (string Signature, string Vendor)[] KnownSignatures =
    [
        ("Cisco IOS-XE", "Cisco"),
        ("Cisco NX-OS", "Cisco"),
        ("Cisco IOS", "Cisco"),
        ("RouterOS", "Mikrotik"),
        ("EdgeOS", "Ubiquiti"),
        ("ProCurve", "HP"),
        ("FortiOS", "Fortinet"),
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

            foreach ((string signature, string vendor) in KnownSignatures)
            {
                if (sysDescr.Contains(signature, StringComparison.OrdinalIgnoreCase))
                {
                    string id = AnalysisEngine.BuildId(Outputs[0], fact);
                    return [Fact.Create(id, vendor, fact.CollectedAt)];
                }
            }
        }

        return [];
    }
}