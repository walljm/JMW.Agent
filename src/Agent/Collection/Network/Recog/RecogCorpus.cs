using System.Reflection;

namespace JMW.Discovery.Agent.Collection.Network.Recog;

/// <summary>
/// The set of Recog fingerprint databases shipped with the agent, keyed by assertion source
/// (<c>matches</c>). Loaded once from embedded XML resources; matching runs on the agent so it can
/// drive adaptive follow-up probing. The corpus is a curated device-family subset of Rapid7 Recog
/// (BSD-2) plus room for JMW-supplemental fingerprints in the same format.
/// </summary>
public sealed class RecogCorpus
{
    // Recog assertion-source names (the root <fingerprints matches="..."> value) for the shipped DBs.
    public const string HttpServer = "http_header.server";
    public const string HttpWwwAuth = "http_header.wwwauth";
    public const string HtmlTitle = "html_title";
    public const string FaviconMd5 = "favicon.md5";

    private readonly IReadOnlyDictionary<string, RecogDatabase> databases;

    private RecogCorpus(IReadOnlyDictionary<string, RecogDatabase> databases)
    {
        this.databases = databases;
    }

    public IReadOnlyCollection<RecogDatabase> Databases => databases.Values.ToList();

    /// <summary>Matches an input against the database for <paramref name="matchType"/> (null if no such DB or no match).</summary>
    public RecogMatch? Match(string matchType, string input) =>
        databases.TryGetValue(matchType, out RecogDatabase? db) ? db.Match(input) : null;

    public RecogDatabase? Database(string matchType) =>
        databases.TryGetValue(matchType, out RecogDatabase? db) ? db : null;

    /// <summary>Builds a corpus from already-parsed databases (keyed by their match type).</summary>
    public static RecogCorpus FromDatabases(IEnumerable<RecogDatabase> databases)
    {
        Dictionary<string, RecogDatabase> dbs = new(StringComparer.Ordinal);
        foreach (RecogDatabase db in databases)
        {
            dbs[db.MatchType] = db;
        }

        return new RecogCorpus(dbs);
    }

    /// <summary>Loads every embedded Recog XML database (resources under the Recog folder).</summary>
    public static RecogCorpus LoadEmbedded()
    {
        Assembly asm = typeof(RecogCorpus).Assembly;
        Dictionary<string, RecogDatabase> dbs = new(StringComparer.Ordinal);

        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.Contains(".Recog.", StringComparison.Ordinal)
             || !name.EndsWith(".xml", StringComparison.Ordinal))
            {
                continue;
            }

            using Stream? stream = asm.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            RecogDatabase db = RecogDatabase.Parse(stream);
            dbs[db.MatchType] = db;
        }

        return new RecogCorpus(dbs);
    }
}