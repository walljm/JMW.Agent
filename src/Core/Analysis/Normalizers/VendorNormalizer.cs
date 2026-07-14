namespace JMW.Discovery.Core.Analysis.Normalizers;

/// <summary>
/// Canonicalizes manufacturer/vendor name strings to a consistent, human-readable
/// proper-case form. Raw values vary wildly by collection source for what is
/// ultimately the same handful of companies — DMI/SMBIOS ("Dell Inc.",
/// "ASUSTeK COMPUTER INC."), /proc/cpuinfo vendor_id ("GenuineIntel"), ONVIF/UPnP
/// manufacturer strings, BACnet/Modbus vendor registries, IEEE-style all-caps
/// forms ("GOOGLE, INC.").
/// This is intentionally NOT a lowercase/trim transform — the goal is the
/// vendor's own preferred display name (e.g. "Google", "Dell", "AMD"), not a
/// case-folded token.
/// Pipeline: trim -> reject "no real value" placeholders -> strip a trailing
/// legal-entity suffix (", Inc.", " Corporation", " Co., Ltd.", ...) -> if the
/// result matches a known vendor in <see cref="Aliases" />, return its canonical
/// name. Suffix stripping is mechanical and always applied — it's a safe,
/// reversible cleanup regardless of whether we recognize the vendor. Renaming
/// (the alias table) is the part that requires recognition: a vendor we've
/// never seen still gets a value back (suffix-stripped, original casing), it
/// just doesn't get remapped to a different display name we haven't vetted.
/// </summary>
public sealed class VendorNormalizer : INormalizer
{
    public IReadOnlyList<string> AttributePathPatterns =>
    [
        FactPaths.DeviceVendor,
        FactPaths.HwCpuVendor,
        FactPaths.HwBoardVendor,
        FactPaths.HwSystemVendor,
        FactPaths.HwBiosVendor,
        FactPaths.HwChassisVendor,
        FactPaths.HwComponentVendor,
        FactPaths.GpuVendor,
        FactPaths.BacnetVendorName,
        FactPaths.ModbusVendorName,
        FactPaths.DiscoveredVendor,
    ];

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

        // Suffix stripping is mechanical (safe regardless of whether we recognize
        // the vendor); alias lookup is the only step gated on recognition.
        string stripped = StripLegalSuffixes(trimmed);
        if (stripped.Length == 0)
        {
            return null;
        }

        return FactValue.FromString(Aliases.TryGetValue(stripped, out string? canonical) ? canonical : stripped);
    }

    // "No real vendor" placeholders. DMI/SMBIOS-sourced fields are already run
    // through DmiDecode.Clean() at collection time (see HardwareCollector.cs), but
    // SNMP/ONVIF/UPnP/BACnet/Modbus vendor strings reach this normalizer raw.
    private static readonly HashSet<string> Junk = new(StringComparer.OrdinalIgnoreCase)
    {
        "unknown",
        "n/a",
        "none",
        "not specified",
        "not applicable",
        "to be filled by o.e.m.",
        "default string",
        "system manufacturer",
        "system product name",
        "oem",
        "generic",
    };

    private static string StripLegalSuffixes(string value)
    {
        string current = value;
        bool changed;
        do
        {
            changed = false;
            foreach (string suffix in LegalSuffixes)
            {
                if (current.Length > suffix.Length && current.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    current = current[..^suffix.Length].TrimEnd(' ', ',', '.');
                    changed = true;
                    break;
                }
            }
        } while (changed && current.Length > 0);

        return current;
    }

    // Longest/most-specific first so e.g. ", Co., Ltd." is stripped whole rather
    // than leaving a dangling ", Co.". Each entry requires a leading space or
    // comma so we never clip mid-word (" Co." won't match the tail of "Cisco").
    private static readonly string[] LegalSuffixes =
    [
        ", Co., Ltd.", " Co., Ltd.", ", Co. Ltd.", " Co. Ltd.", ", Co Ltd", " Co Ltd",
        ", Corporation", " Corporation",
        ", Incorporated", " Incorporated",
        ", Inc.", " Inc.", ", Inc", " Inc",
        ", Corp.", " Corp.", ", Corp", " Corp",
        ", Ltd.", " Ltd.", ", Ltd", " Ltd", " Limited",
        ", LLC", " LLC", ", L.L.C.", " L.L.C.",
        " GmbH", " AG", " S.A.", " SA", " N.V.", " NV",
        " Pty. Ltd.", " Pty Ltd",
        " Co.",
    ];

    // Case-insensitive lookup keyed on the suffix-stripped value; the value is
    // the canonical display name. Built from real raw values seen in this
    // codebase's collectors (DMI manufacturer strings, /proc/cpuinfo vendor_id,
    // hardcoded literals) plus the common home/SMB network vendors this system
    // identifies via SNMP/ONVIF/UPnP/mDNS. Extend as new vendors surface.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // CPU vendor_id tokens (/proc/cpuinfo) vs SMBIOS processor manufacturer
        ["GenuineIntel"] = "Intel",
        ["AuthenticAMD"] = "AMD",
        ["Advanced Micro Devices"] = "AMD",

        // Board/system/BIOS manufacturers (DMI) — legal-name and all-caps variants
        ["ASUSTeK Computer"] = "ASUS",
        ["Hewlett-Packard"] = "HP",
        ["Hewlett Packard"] = "HP",
        ["Hewlett Packard Enterprise"] = "HPE",
        ["Micro-Star International"] = "MSI",
        ["Gigabyte Technology"] = "Gigabyte",
        ["Super Micro Computer"] = "Supermicro",
        ["Lenovo"] = "Lenovo",
        ["Dell"] = "Dell",
        ["Nvidia"] = "NVIDIA",
        ["Qemu"] = "QEMU",

        // Home/SMB network + IoT vendors surfaced via SNMP/ONVIF/UPnP/mDNS/BACnet.
        // These are curated pairs, not a generic "strip business words" rule —
        // dropping a descriptor like "Networks"/"Systems" is only safe once we've
        // confirmed the shortened form is the vendor's actual common name.
        ["Google"] = "Google",
        ["Apple"] = "Apple",
        ["Microsoft"] = "Microsoft",
        ["Amazon"] = "Amazon",
        ["Samsung Electronics"] = "Samsung",
        ["Ubiquiti Networks"] = "Ubiquiti",
        ["TP-Link Technologies"] = "TP-Link",
        ["Netgear"] = "NETGEAR",
        ["Cisco Systems"] = "Cisco",
        ["Arista Networks"] = "Arista",
        ["Juniper Networks"] = "Juniper",
        ["Synology"] = "Synology",
        ["QNAP Systems"] = "QNAP",
        ["Philips"] = "Philips",
        ["Roku"] = "Roku",
        ["Sonos"] = "Sonos",

        // IANA Private Enterprise Numbers registrant names (raw, via SnmpCollector's
        // sysObjectID lookup — see EnterpriseNumberRegistry / vendor-derivation-updates.md
        // §2.5) don't follow any consistent naming convention, so each needs its own mapping
        // to the canonical form already used by this plan's other vendor derivations.
        ["ciscoSystems"] = "Cisco", // IANA's literal registrant name for enterprise 9
        ["American Power Conversion"] = "APC", // suffix-stripped from "...Corp."
        ["MikroTik"] = "Mikrotik", // IANA casing differs from this codebase's canonical "Mikrotik"
        ["TP-Link Systems"] = "TP-Link", // distinct legal name from the "TP-Link Technologies" DMI form
        ["D-Link Systems"] = "D-Link",
        ["PALO ALTO NETWORKS"] = "Palo Alto Networks", // IANA registrant name is all-caps
        ["Aruba, a Hewlett Packard Enterprise company"] = "Aruba", // no legal-suffix pattern matches this
    };
}