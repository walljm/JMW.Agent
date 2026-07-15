using System.Reflection;

namespace JMW.Discovery.Agent.Collection.Device.EnterpriseNumbers;

/// <summary>
/// The full IANA Private Enterprise Numbers registry (snapshot — see ATTRIBUTION.md), embedded
/// as a TSV resource and loaded once into an in-memory lookup. Used by <see cref="SnmpCollector" />
/// to resolve a device's vendor from the enterprise number embedded in its SNMP sysObjectID
/// (<c>1.3.6.1.4.1.&lt;enterprise-number&gt;...</c>). Raw IANA registrant names are returned
/// as-is (mixed casing, legal suffixes, occasional odd formatting) — canonicalized downstream by
/// <c>VendorNormalizer</c>, same as every other vendor-string source in this codebase.
/// See docs/plans/vendor-derivation-updates.md §2.5.
/// </summary>
public static class EnterpriseNumberRegistry
{
    private static readonly Lazy<IReadOnlyDictionary<int, string>> Entries = new(LoadEmbedded);

    /// <summary>Looks up the registrant organization name for an IANA enterprise number, or null if unassigned/unknown.</summary>
    public static string? Lookup(int enterpriseNumber) =>
        Entries.Value.TryGetValue(enterpriseNumber, out string? vendor) ? vendor : null;

    private static Dictionary<int, string> LoadEmbedded()
    {
        Assembly asm = typeof(EnterpriseNumberRegistry).Assembly;
        string? name = Array.Find(
            asm.GetManifestResourceNames(),
            n => n.Contains(".EnterpriseNumbers.", StringComparison.Ordinal) && n.EndsWith(".tsv", StringComparison.Ordinal)
        );

        Dictionary<int, string> result = new();
        if (name is null)
        {
            return result;
        }

        using Stream? stream = asm.GetManifestResourceStream(name);
        if (stream is null)
        {
            return result;
        }

        using StreamReader reader = new(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            int tab = line.IndexOf('\t');
            if (tab < 0 || !int.TryParse(line.AsSpan(0, tab), out int number))
            {
                continue;
            }

            result[number] = line[(tab + 1)..];
        }

        return result;
    }
}