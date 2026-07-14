using System.Text;

using JMW.Discovery.Agent.Collection.Network.Recog;

namespace JMW.Discovery.UnitTests.Agent;

/// <summary>
/// Tests the C# Recog matcher against the authentic Rapid7 Recog corpus embedded in the agent.
/// The headline test runs every fingerprint's own &lt;example&gt; vectors back through the engine —
/// the same self-verification Recog's Ruby test harness performs — proving format compatibility on
/// real data. The rest are focused unit tests for extraction semantics (statics, capture groups,
/// interpolation, temporaries, first-match-wins, flags, and graceful skipping of Ruby-only regex).
/// </summary>
public sealed class RecogMatcherTests
{
    private static readonly RecogCorpus Corpus = RecogCorpus.LoadEmbedded();

    private static RecogDatabase ParseXml(string xml) =>
        RecogDatabase.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    [Fact]
    public void Corpus_LoadsAllFourDatabases()
    {
        Assert.NotNull(Corpus.Database(RecogCorpus.HttpServer));
        Assert.NotNull(Corpus.Database(RecogCorpus.HtmlTitle));
        Assert.NotNull(Corpus.Database(RecogCorpus.HttpWwwAuth));
        Assert.NotNull(Corpus.Database(RecogCorpus.FaviconMd5));
    }

    [Fact]
    public void EveryEmbeddedExample_ExtractsItsDeclaredFields()
    {
        List<string> failures = [];
        int examples = 0;
        int skippedOnLoad = 0;

        foreach (RecogDatabase db in Corpus.Databases)
        {
            skippedOnLoad += db.SkippedCount;
            foreach (RecogFingerprint fp in db.Fingerprints)
            {
                foreach (RecogExample ex in fp.Examples)
                {
                    // Base64 / external-file examples aren't present in the curated set; skip if any appear.
                    if (ex.Encoding is not null || string.IsNullOrEmpty(ex.Text))
                    {
                        continue;
                    }

                    examples++;
                    RecogMatch? match = fp.Match(ex.Text);
                    if (match is null)
                    {
                        failures.Add($"[{db.MatchType}] NO MATCH: \"{ex.Text}\"  (pattern: {fp.PatternText})");
                        continue;
                    }

                    foreach ((string field, string expected) in ex.Expected)
                    {
                        if (!match.Fields.TryGetValue(field, out string? actual))
                        {
                            failures.Add($"[{db.MatchType}] \"{ex.Text}\": missing {field} (expected \"{expected}\")");
                        }
                        else if (!string.Equals(actual, expected, StringComparison.Ordinal))
                        {
                            failures.Add($"[{db.MatchType}] \"{ex.Text}\": {field}=\"{actual}\" expected \"{expected}\"");
                        }
                    }
                }
            }
        }

        Assert.True(examples > 500, $"expected the curated corpus to yield many examples, got {examples}");
        Assert.True(
            failures.Count == 0,
            $"{failures.Count} of {examples} example checks failed (skipped-on-load={skippedOnLoad}):\n"
                + string.Join("\n", failures.Take(50))
        );
    }

    [Fact]
    public void FullCorpus_CompilesUnderDotNetWithNoSkips()
    {
        // The full Rapid7 Recog HTTP corpus is embedded. Ruby/Onigmo regex quirks .NET rejects are
        // normalized in RecogDatabase.TryCompile (e.g. redundant "\_" escapes). A non-zero skip count
        // means a new incompatibility slipped in with a corpus update and is silently costing
        // coverage — extend TryCompile to handle it rather than loosening this assertion.
        int total = Corpus.Databases.Sum(db => db.Fingerprints.Count);
        int skipped = Corpus.Databases.Sum(db => db.SkippedCount);

        Assert.True(total > 1400, $"expected the full corpus, got {total} fingerprints");
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void Extract_StaticCaptureAndInterpolation()
    {
        RecogDatabase db = ParseXml(
            """
            <fingerprints matches="test">
              <fingerprint pattern="^Server/([\d.]+)$">
                <description>Acme</description>
                <param pos="0" name="service.vendor" value="Acme"/>
                <param pos="1" name="service.version"/>
                <param pos="0" name="service.cpe23" value="cpe:/a:acme:server:{service.version}"/>
              </fingerprint>
            </fingerprints>
            """
        );

        RecogMatch? m = db.Match("Server/1.2");
        Assert.NotNull(m);
        Assert.Equal("Acme", m.Fields["service.vendor"]);
        Assert.Equal("1.2", m.Fields["service.version"]);
        Assert.Equal("cpe:/a:acme:server:1.2", m.Fields["service.cpe23"]);
    }

    [Fact]
    public void Extract_DropsUnresolvedInterpolation()
    {
        // The version capture group is optional and absent here, so the cpe23 param that
        // interpolates it must be dropped (Recog's behavior), not emitted with a literal "{...}".
        RecogDatabase db = ParseXml(
            """
            <fingerprints matches="test">
              <fingerprint pattern="^Acme(?:/([\d.]+))?$">
                <param pos="0" name="service.product" value="Acme"/>
                <param pos="1" name="service.version"/>
                <param pos="0" name="service.cpe23" value="cpe:/a:acme:acme:{service.version}"/>
              </fingerprint>
            </fingerprints>
            """
        );

        RecogMatch? m = db.Match("Acme");
        Assert.NotNull(m);
        Assert.Equal("Acme", m.Fields["service.product"]);
        Assert.False(m.Fields.ContainsKey("service.version"));
        Assert.False(m.Fields.ContainsKey("service.cpe23"));
    }

    [Fact]
    public void Extract_TemporariesAreUsedButNotEmitted()
    {
        RecogDatabase db = ParseXml(
            """
            <fingerprints matches="test">
              <fingerprint pattern="^X-(\w+)$">
                <param pos="1" name="_tmp.raw"/>
                <param pos="0" name="service.product" value="P-{_tmp.raw}"/>
              </fingerprint>
            </fingerprints>
            """
        );

        RecogMatch? m = db.Match("X-foo");
        Assert.NotNull(m);
        Assert.Equal("P-foo", m.Fields["service.product"]);
        Assert.False(m.Fields.ContainsKey("_tmp.raw"));
    }

    [Fact]
    public void Match_IsFirstMatchWins()
    {
        RecogDatabase db = ParseXml(
            """
            <fingerprints matches="test">
              <fingerprint pattern="^nginx"><param pos="0" name="p" value="First"/></fingerprint>
              <fingerprint pattern="^nginx/1"><param pos="0" name="p" value="Second"/></fingerprint>
            </fingerprints>
            """
        );

        Assert.Equal("First", db.Match("nginx/1.0")?.Fields["p"]);
    }

    [Fact]
    public void Match_ReturnsNullWhenNothingMatches()
    {
        RecogDatabase db = ParseXml(
            """
            <fingerprints matches="test">
              <fingerprint pattern="^ok$"><param pos="0" name="p" value="y"/></fingerprint>
            </fingerprints>
            """
        );

        Assert.Null(db.Match("nope"));
    }

    [Fact]
    public void Flags_RegIcaseMatchesCaseInsensitively()
    {
        RecogDatabase db = ParseXml(
            """
            <fingerprints matches="test">
              <fingerprint pattern="^apache$" flags="REG_ICASE"><param pos="0" name="p" value="A"/></fingerprint>
            </fingerprints>
            """
        );

        Assert.Equal("A", db.Match("APACHE")?.Fields["p"]);
    }

    [Fact]
    public void Parse_SkipsIncompatibleRegexWithoutFailingDatabase()
    {
        // A possessive quantifier (Ruby/Onigmo) does not compile under .NET; it must be skipped,
        // leaving the rest of the database usable.
        RecogDatabase db = ParseXml(
            """
            <fingerprints matches="test">
              <fingerprint pattern="a++"><param pos="0" name="p" value="x"/></fingerprint>
              <fingerprint pattern="^ok$"><param pos="0" name="p" value="y"/></fingerprint>
            </fingerprints>
            """
        );

        Assert.Equal(1, db.SkippedCount);
        Assert.Single(db.Fingerprints);
        Assert.Equal("y", db.Match("ok")?.Fields["p"]);
    }
}