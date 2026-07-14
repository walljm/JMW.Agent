namespace JMW.Discovery.Agent.Collection.Local;

/// <summary>
/// Runs and parses <c>dmidecode -q</c> (SMBIOS/DMI table decode) on Linux.
/// One invocation returns every DMI structure — BIOS, System, Chassis, Base Board,
/// Processor, Memory Device, etc. — which both <see cref="HardwareCollector" /> (scalar
/// identity) and <see cref="HwInventoryCollector" /> (component inventory) consume.
/// dmidecode requires root; over a normal agent it is reached via <c>sudo -n</c> and any
/// failure (no sudo, not installed, access denied) yields an empty section list.
/// Output shape (blank-line-separated blocks; <c>-q</c> suppresses handle/DMI-type headers):
/// <code>
/// BIOS Information
/// 	Vendor: American Megatrends Inc.
/// 	Version: 1.0
/// 	Characteristics:
/// 		PCI is supported
/// </code>
/// The first non-blank line of a block is the section name; indented <c>Key: Value</c>
/// lines become fields. Multi-line values (a bare <c>Key:</c> followed by indented bullets,
/// e.g. "Characteristics") are intentionally ignored — only single-line scalars are captured.
/// </summary>
public static class DmiDecode
{
    public sealed record Section(string Name, IReadOnlyDictionary<string, string> Fields);

    public static async Task<IReadOnlyList<Section>> RunAsync(CancellationToken ct)
    {
        string output = await CollectorHelper.RunAsync("sudo", "-n dmidecode -q", ct);
        return Parse(output);
    }

    public static IReadOnlyList<Section> Parse(string output)
    {
        List<Section> sections = new();
        if (string.IsNullOrWhiteSpace(output))
        {
            return sections;
        }

        string[] blocks = output.Split(
            ["\n\n", "\r\n\r\n"],
            StringSplitOptions.RemoveEmptyEntries
        );

        foreach (string block in blocks)
        {
            string name = "";
            bool haveName = false;
            Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);

            foreach (string raw in block.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (!haveName)
                {
                    string title = line.Trim();
                    if (title.Length == 0)
                    {
                        continue;
                    }

                    name = title;
                    haveName = true;
                    continue;
                }

                int colon = line.IndexOf(':');
                if (colon < 0)
                {
                    continue;
                }

                string k = line[..colon].Trim();
                string v = line[(colon + 1)..].Trim();
                // Skip bare "Key:" lines (multi-line values like Characteristics/Flags) and
                // empty keys; keep only real single-line scalars.
                if (k.Length > 0 && v.Length > 0)
                {
                    fields[k] = v;
                }
            }

            if (haveName)
            {
                sections.Add(new Section(name, fields));
            }
        }

        return sections;
    }

    public static Section? Find(IReadOnlyList<Section> sections, string name)
    {
        foreach (Section s in sections)
        {
            if (s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }

        return null;
    }

    public static string? Get(Section? section, string field) =>
        section is not null && section.Fields.TryGetValue(field, out string? v) ? v : null;

    /// <summary>
    /// Normalizes a raw DMI value: trims, and maps the well-known "no real value" placeholder
    /// strings dmidecode/OEMs emit to <c>null</c> so callers can fall back to another source.
    /// </summary>
    public static string? Clean(string? value)
    {
        if (value is null)
        {
            return null;
        }

        string v = value.Trim();
        return v.Length == 0 || Placeholders.Contains(v) ? null : v;
    }

    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Not Specified",
        "Not Present",
        "None",
        "Unknown",
        "N/A",
        "To Be Filled By O.E.M.",
        "Default string",
        "System Serial Number",
        "System manufacturer",
        "System Product Name",
        "System Version",
        "Chassis Manufacturer",
        "Chassis Serial Number",
        "Chassis Version",
        "Asset Tag",
        "Base Board Manufacturer",
        "Base Board Product Name",
        "Base Board Version",
        "Base Board Serial Number",
        "OEM",
        "0x00000000",
        "00000000-0000-0000-0000-000000000000",
    };
}