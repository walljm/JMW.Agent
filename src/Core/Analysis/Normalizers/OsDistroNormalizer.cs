namespace JMW.Discovery.Core.Analysis.Normalizers;

/// <summary>
/// Canonicalizes OS distro/edition strings to a consistent display form. Raw values vary by
/// collection source for what is ultimately a small set of platforms: Linux collectors
/// (OsCollector, SshCollector) read the distro's own self-description from /etc/os-release's
/// NAME field, which itself varies wildly in convention — "Ubuntu" vs "Debian GNU/Linux" vs
/// "Raspbian GNU/Linux" vs "Red Hat Enterprise Linux" vs "Fedora Linux" — while macOS reads
/// sw_vers's ProductName ("Mac OS X" on older releases, "macOS" since 10.12).
/// This replaces a bare LowercaseTrimNormalizer registration that folded case without
/// reconciling any of that noise ("Debian GNU/Linux" -> "debian gnu/linux", "Ubuntu" -> "ubuntu")
/// — two distros end up no more comparable to each other than before, just lowercased. That is
/// the actual "not normalizing Linux well" bug this normalizer fixes.
/// Pipeline: trim -> reject "no real value" placeholders -> if the result matches a known distro
/// in <see cref="Aliases" />, return its canonical display name. This is intentionally an
/// exact-match alias table, not a suffix-stripping heuristic (unlike VendorNormalizer's legal-suffix
/// stripping, there is no single mechanical rule that is safe here — e.g. blindly stripping a
/// trailing " Linux" would turn "Arch Linux" into "Arch" and "Rocky Linux" into "Rocky", which are
/// not the distros' actual common names). A distro we don't recognize still gets a value back
/// (trimmed, original casing) — same "safe default" discipline as VendorNormalizer.
/// </summary>
public sealed class OsDistroNormalizer : INormalizer
{
    public IReadOnlyList<string> AttributePathPatterns => [FactPaths.SystemOsDistro];

    public FactValue? Normalize(FactValue raw)
    {
        string? str = raw.AsString();
        if (str is null)
        {
            return null;
        }

        string trimmed = str.Trim();
        if (trimmed.Length == 0 || Junk.Contains(trimmed))
        {
            return null;
        }

        return FactValue.FromString(Aliases.TryGetValue(trimmed, out string? canonical) ? canonical : trimmed);
    }

    private static readonly HashSet<string> Junk = new(StringComparer.OrdinalIgnoreCase)
    {
        "unknown",
        "n/a",
        "none",
        "linux", // bare "Linux" with no distro name carries no information Family doesn't already
    };

    // Case-insensitive lookup keyed on the raw /etc/os-release NAME (or sw_vers ProductName /
    // Win32_OperatingSystem Caption) value; the mapped value is the canonical display name.
    // Built from real /etc/os-release NAME conventions for distros this codebase's Linux
    // collectors (OsCollector, SshCollector) can encounter. Extend as new distros surface.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // Debian family — NAME carries "GNU/Linux" noise that isn't part of the distro's common name
        ["Debian GNU/Linux"] = "Debian",
        ["Raspbian GNU/Linux"] = "Raspbian",
        ["Kali GNU/Linux"] = "Kali Linux",
        ["Ubuntu"] = "Ubuntu", // already canonical, passthrough unchanged
        ["Linux Mint"] = "Linux Mint",
        ["Pop!_OS"] = "Pop!_OS",
        ["Zorin OS"] = "Zorin OS",
        ["elementary OS"] = "elementary OS",

        // Red Hat family
        ["Fedora Linux"] = "Fedora",
        ["Fedora"] = "Fedora",
        ["CentOS Linux"] = "CentOS",
        ["CentOS Stream"] = "CentOS Stream",
        ["Red Hat Enterprise Linux"] = "Red Hat Enterprise Linux",
        ["Rocky Linux"] = "Rocky Linux",
        ["AlmaLinux"] = "AlmaLinux",
        ["Amazon Linux"] = "Amazon Linux",

        // SUSE family
        ["openSUSE Leap"] = "openSUSE Leap",
        ["openSUSE Tumbleweed"] = "openSUSE Tumbleweed",
        ["SLES"] = "SUSE Linux Enterprise Server",

        // Others
        ["Arch Linux"] = "Arch Linux",
        ["Alpine Linux"] = "Alpine Linux",
        ["Gentoo"] = "Gentoo",
        ["Gentoo Linux"] = "Gentoo",
        ["Manjaro Linux"] = "Manjaro",
        ["Void Linux"] = "Void Linux",
        ["OpenWrt"] = "OpenWrt",
        ["Proxmox VE"] = "Proxmox VE",

        // macOS — sw_vers ProductName varies by era; canonicalize to the current brand name
        ["Mac OS X"] = "macOS",
        ["OS X"] = "macOS",
    };
}
