using System.Text;

using JMW.Discovery.Agent.Collection.Network;

namespace JMW.Discovery.UnitTests.Agent;

/// <summary>
/// Pins the two favicon fingerprint hashes against reference values produced by an independent
/// MurmurHash3 implementation in Python (which agrees with the canonical `mmh3` library — e.g.
/// mmh3.hash("foo") == -156908512). The Shodan recipe's silent-failure trap is the base64
/// newline: it MUST be MIME-style ('\n' every 76 chars + trailing '\n', i.e. Python
/// base64.encodebytes), NOT plain base64 and NOT .NET InsertLineBreaks (which emits CRLF).
/// The discriminator test proves a newline regression changes the hash.
/// </summary>
public sealed class FaviconHashTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("0", -764297089)]
    [InlineData("foo", -156908512)]
    [InlineData("hello", 613153351)]
    [InlineData("The quick brown fox jumps over the lazy dog", 776992547)]
    public void Murmur3_MatchesReferenceVectors(string input, int expected)
    {
        Assert.Equal(expected, FaviconHash.MurmurHash3(Encoding.UTF8.GetBytes(input)));
    }

    // Deterministic 768-byte fixture: bytes 0..255 repeated 3x. Chosen to force multi-line base64
    // (1024 chars -> 14 lines) so the newline handling is actually exercised.
    private static byte[] Fixture()
    {
        byte[] data = new byte[768];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 256);
        }

        return data;
    }

    [Fact]
    public void ToMimeBase64_InsertsNewlineEvery76CharsWithTrailingNewline()
    {
        string mime = FaviconHash.ToMimeBase64(Fixture());

        Assert.EndsWith("\n", mime);
        Assert.DoesNotContain("\r", mime); // LF only, never CRLF
        string[] lines = mime.TrimEnd('\n').Split('\n');
        Assert.Equal(14, lines.Length);
        Assert.All(lines[..^1], line => Assert.Equal(76, line.Length));
    }

    [Fact]
    public void ShodanHash_MatchesReferenceValue()
    {
        Assert.Equal(1836528006, FaviconHash.ShodanHash(Fixture()));
    }

    [Fact]
    public void Md5Hex_MatchesReferenceValue()
    {
        Assert.Equal("e6899eaaf06fd702f3ed3f988eb19362", FaviconHash.Md5Hex(Fixture()));
    }

    [Fact]
    public void ShodanHash_NewlineDetailChangesTheHash()
    {
        // Guard: hashing the WRONG (plain, no-newline) base64 yields a different value. If a
        // refactor drops the MIME newlines, ShodanHash would collapse to this value and the
        // MatchesReferenceValue test would fail — this documents why they differ.
        int wrong = FaviconHash.MurmurHash3(Encoding.ASCII.GetBytes(Convert.ToBase64String(Fixture())));
        Assert.Equal(1905855828, wrong);
        Assert.NotEqual(wrong, FaviconHash.ShodanHash(Fixture()));
    }

    [Theory]
    // rel before href, root-relative
    [InlineData("<link rel=\"icon\" href=\"/static/fav.png\">", "http://10.0.0.1/static/fav.png")]
    // href before rel
    [InlineData("<link href=\"brand.ico\" rel=\"shortcut icon\">", "http://10.0.0.1/brand.ico")]
    // absolute href
    [InlineData("<link rel=\"icon\" href=\"https://cdn.example/x.ico\">", "https://cdn.example/x.ico")]
    // single quotes + apple-touch-icon variant
    [InlineData("<link rel='apple-touch-icon' href='/a.png'>", "http://10.0.0.1/a.png")]
    // no link tag -> default
    [InlineData("<html><head></head></html>", "http://10.0.0.1/favicon.ico")]
    public void ResolveFaviconUri_HandlesLinkTagsAndFallback(string body, string expected)
    {
        Uri baseUri = new("http://10.0.0.1/");
        Assert.Equal(expected, HttpBannerScanner.ResolveFaviconUri(baseUri, body).ToString());
    }

    [Fact]
    public void ResolveFaviconUri_ResolvesRelativeAgainstRequestPath()
    {
        // baseUri carries a path (e.g. after a redirect to /login); a relative href resolves
        // against that path, matching browser behavior.
        Uri baseUri = new("http://10.0.0.1:8080/admin/login");
        Uri resolved = HttpBannerScanner.ResolveFaviconUri(baseUri, "<link rel=\"icon\" href=\"fav.ico\">");
        Assert.Equal("http://10.0.0.1:8080/admin/fav.ico", resolved.ToString());
    }
}