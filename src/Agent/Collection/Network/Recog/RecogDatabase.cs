using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace JMW.Discovery.Agent.Collection.Network.Recog;

/// <summary>
/// One Recog fingerprint database (one XML file, one <c>matches</c> assertion source such as
/// <c>http_header.server</c>). Fingerprints are evaluated in document order, first match wins —
/// Recog's contract. Patterns that use Ruby/Onigmo regex features .NET can't compile are skipped
/// on load (counted in <see cref="SkippedCount"/>) rather than failing the whole database.
/// </summary>
public sealed class RecogDatabase
{
    private readonly IReadOnlyList<RecogFingerprint> fingerprints;

    public string MatchType { get; }
    public IReadOnlyList<RecogFingerprint> Fingerprints => fingerprints;

    /// <summary>Count of fingerprints whose regex would not compile under .NET (skipped on load).</summary>
    public int SkippedCount { get; }

    private RecogDatabase(string matchType, IReadOnlyList<RecogFingerprint> fingerprints, int skippedCount)
    {
        MatchType = matchType;
        this.fingerprints = fingerprints;
        SkippedCount = skippedCount;
    }

    /// <summary>Returns the first fingerprint that matches <paramref name="input"/>, or null.</summary>
    public RecogMatch? Match(string input)
    {
        foreach (RecogFingerprint fp in fingerprints)
        {
            RecogMatch? match = fp.Match(input);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    public static RecogDatabase Parse(Stream xml)
    {
        // Defense-in-depth: the corpus is trusted (embedded), but block DTDs/external entities anyway.
        XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using XmlReader reader = XmlReader.Create(xml, settings);
        XElement root = XDocument.Load(reader).Root
            ?? throw new InvalidDataException("Recog XML has no root element.");

        string matchType = (string?)root.Attribute("matches") ?? "";

        List<RecogFingerprint> fingerprints = new();
        int skipped = 0;
        foreach (XElement fe in root.Elements("fingerprint"))
        {
            string patternText = (string?)fe.Attribute("pattern") ?? "";

            RegexOptions options = ParseFlags((string?)fe.Attribute("flags"));
            Regex? regex = TryCompile(patternText, options);
            if (regex is null)
            {
                skipped++;
                continue;
            }

            fingerprints.Add(
                new RecogFingerprint(
                    regex,
                    patternText,
                    (string?)fe.Element("description") ?? "",
                    ParseParams(fe),
                    ParseExamples(fe)
                )
            );
        }

        return new RecogDatabase(matchType, fingerprints, skipped);
    }

    private static List<RecogParam> ParseParams(XElement fe)
    {
        List<RecogParam> ps = new();
        foreach (XElement pe in fe.Elements("param"))
        {
            ps.Add(
                new RecogParam(
                    (int?)pe.Attribute("pos") ?? 0,
                    (string?)pe.Attribute("name") ?? "",
                    (string?)pe.Attribute("value")
                )
            );
        }

        return ps;
    }

    private static List<RecogExample> ParseExamples(XElement fe)
    {
        List<RecogExample> exs = new();
        foreach (XElement ee in fe.Elements("example"))
        {
            Dictionary<string, string> expected = new(StringComparer.Ordinal);
            string? encoding = null;
            foreach (XAttribute a in ee.Attributes())
            {
                if (a.Name.LocalName == "_encoding")
                {
                    encoding = a.Value;
                }
                else if (!a.Name.LocalName.StartsWith('_'))
                {
                    // Non-reserved attributes are the fields the example expects to extract.
                    expected[a.Name.LocalName] = a.Value;
                }
            }

            exs.Add(new RecogExample(ee.Value, expected, encoding));
        }

        return exs;
    }

    private static Regex? TryCompile(string pattern, RegexOptions options)
    {
        try
        {
            return new Regex(pattern, options);
        }
        catch (ArgumentException)
        {
            // Ruby/Onigmo tolerates redundant escapes (e.g. "\_") that .NET rejects as an
            // unrecognized escape sequence. "_" is never a regex metacharacter, so normalizing
            // "\_" -> "_" is always safe. Retry once; if it still won't compile, the caller skips it.
            if (!pattern.Contains("\\_", StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                return new Regex(pattern.Replace("\\_", "_", StringComparison.Ordinal), options);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }

    private static RegexOptions ParseFlags(string? flags)
    {
        RegexOptions options = RegexOptions.CultureInvariant;
        if (string.IsNullOrEmpty(flags))
        {
            return options;
        }

        foreach (string f in flags.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            options |= f switch
            {
                "REG_ICASE" => RegexOptions.IgnoreCase,
                "REG_MULTILINE" => RegexOptions.Multiline,
                "REG_DOT_NEWLINE" => RegexOptions.Singleline,
                "REG_EXTENDED" => RegexOptions.IgnorePatternWhitespace,
                _ => RegexOptions.None,
            };
        }

        return options;
    }
}