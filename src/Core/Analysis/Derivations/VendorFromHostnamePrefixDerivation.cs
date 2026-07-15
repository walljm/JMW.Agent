namespace JMW.Discovery.Core.Analysis.Derivations;

/// <summary>
/// Infers a device's vendor from a curated, self-branded DHCP default-hostname prefix — the
/// lowest-confidence signal of the four vendor derivations in this plan, since a hostname is
/// user-editable (see docs/plans/vendor-derivation-updates.md §2.4). Gated more conservatively
/// than the others in two ways: (1) each prefix includes its trailing delimiter (e.g. "amazon-"
/// not "amazon"), so a StartsWith check can't be fooled by an unrelated hostname that merely
/// begins with the same letters (e.g. "amazonian-nas" does not start with "amazon-"); (2) only
/// prefixes that are genuinely vendor-exclusive are included at all.
/// Deliberately excludes generic OS/platform-driven hostname patterns like "android-" —
/// Android's own DHCP client sets that default hostname pattern regardless of which of dozens
/// of manufacturers (Samsung, Google, OnePlus, Motorola, ...) made the phone, so mapping it to
/// any single vendor would be confidently wrong most of the time (plan §1.3: "a confidently
/// wrong vendor is worse than an honest Unknown"). That signal indicates OS, not vendor — out of
/// scope here.
/// Reads both <see cref="FactPaths.SystemHostname" /> (agent-reported OS hostname) and
/// <see cref="FactPaths.DiscoveredHostname" /> (passive discovery — DHCP/mDNS/NBNS hostname for
/// devices with no agent), since either can carry the recognizable prefix.
/// Still feeds the shared <see cref="FactPaths.Derived.DeviceVendorGuess" /> field despite the
/// lower confidence (plan §3's explicit call: stays in the guess field, not a separate
/// "suggested vendor" field, given the guess field is already labeled "inferred" wherever shown).
/// </summary>
public sealed class VendorFromHostnamePrefixDerivation : IDerivation
{
    public IReadOnlyList<string> Inputs { get; } = [FactPaths.SystemHostname, FactPaths.DiscoveredHostname];

    public IReadOnlyList<string> Outputs { get; } = [FactPaths.Derived.DeviceVendorGuess];

    public IReadOnlyList<string> Scope => ["Device"];

    // Each prefix includes its trailing delimiter — see class remarks. Only vendors whose
    // devices self-brand their DHCP hostname this way (verified against real device behavior,
    // not just an OS-driven default) are listed.
    private static readonly (string Prefix, string Vendor)[] KnownHostnamePrefixes =
    [
        ("amazon-", "Amazon"),
        ("roku-", "Roku"),
        ("sonos-", "Sonos"),
    ];

    public IReadOnlyList<Fact> Derive(IReadOnlyList<Fact> scopedFacts)
    {
        foreach (Fact fact in scopedFacts)
        {
            if (fact.AttributePath != FactPaths.SystemHostname && fact.AttributePath != FactPaths.DiscoveredHostname)
            {
                continue;
            }

            string? hostname = fact.Value.AsString();
            if (string.IsNullOrWhiteSpace(hostname))
            {
                continue;
            }

            string trimmed = hostname.Trim();
            foreach ((string prefix, string vendor) in KnownHostnamePrefixes)
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