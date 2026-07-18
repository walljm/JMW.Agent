using System.Text;

namespace JMW.Discovery.Core.Analysis.Normalizers;

/// <summary>
/// Cleans up device/hardware model strings: trims outer whitespace, collapses internal runs of
/// whitespace to a single space, and rejects "no real value" placeholders. Accepts multiple
/// attribute_path patterns — SMBIOS system/board model, passively-discovered model (ONVIF/UPnP/
/// HTTP identity via <see cref="FactPaths.DiscoveredModel" />), BACnet model name, hardware
/// component model, and the raw HomeAssistant HaDevice[] model fact (<see cref="ServicePaths.HomeAssistantHaDeviceModel" />)
/// all reach this normalizer with the same class of raw-string noise. Note: the HomeAssistant
/// model that reaches the *resolved device's* hardware row is promoted via a direct DB upsert in
/// HomeAssistantDevicePromotion, entirely outside this Fact/AnalysisEngine pipeline — that call
/// site applies this same normalizer directly (see its NormalizeModel helper), since registering
/// the pattern here only covers the raw Service[]-scoped fact, not the promoted value.
/// SMBIOS-sourced model values are already run through DmiDecode.Clean() at collection time (see
/// HardwareCollector.cs), which nulls out placeholder strings like "System Product Name" — this
/// normalizer applies the same placeholder discipline to the non-DMI sources (BACnet/ONVIF/UPnP/
/// HomeAssistant) that never pass through DmiDecode, plus whitespace cleanup for all of them.
/// Deliberately does NOT attempt a canonical-name alias table like VendorNormalizer/
/// OsDistroNormalizer: unlike vendor names or OS distros, there is no small closed set of "known
/// model strings" for this codebase's device catalog to curate against — model values are
/// effectively unbounded free text, so the safe move is cleanup only, never rewriting.
/// </summary>
public sealed class ModelNormalizer : INormalizer
{
    private readonly IReadOnlyList<string> _patterns;

    public ModelNormalizer(IReadOnlyList<string> patterns)
    {
        _patterns = patterns;
    }

    public IReadOnlyList<string> AttributePathPatterns => _patterns;

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

        string collapsed = CollapseWhitespace(trimmed);
        return FactValue.FromString(collapsed);
    }

    // "No real model" placeholders from non-DMI sources (BACnet/ONVIF/UPnP/HomeAssistant) — DMI's
    // own placeholder set (DmiDecode.Placeholders) already covers the SMBIOS-sourced paths at
    // collection time, before this normalizer ever sees them.
    private static readonly HashSet<string> Junk = new(StringComparer.OrdinalIgnoreCase)
    {
        "unknown",
        "n/a",
        "none",
        "not specified",
        "not applicable",
        "default",
        "generic",
    };

    private static string CollapseWhitespace(string value)
    {
        StringBuilder sb = new(value.Length);
        bool lastWasSpace = false;
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                }

                lastWasSpace = true;
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }

        return sb.ToString();
    }
}