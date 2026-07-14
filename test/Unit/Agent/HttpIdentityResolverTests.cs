using System.Text;

using JMW.Discovery.Agent.Collection.Network;
using JMW.Discovery.Agent.Collection.Network.Recog;

namespace JMW.Discovery.UnitTests.Agent;

/// <summary>
/// Tests the fusion logic in <see cref="HttpIdentityResolver"/> using controlled Recog databases:
/// Recog fields map to our identity fields, the strongest signal wins per field, confidence tracks
/// the contributing signal, independent agreement on vendor/model bumps confidence, and a match
/// carrying none of the mapped fields yields no identity.
/// </summary>
public sealed class HttpIdentityResolverTests
{
    private static RecogDatabase Db(string xml) =>
        RecogDatabase.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    private static HttpIdentityResolver ResolverWith(params string[] dbXml) =>
        new(RecogCorpus.FromDatabases(dbXml.Select(Db)));

    [Fact]
    public void MapsRecogFieldsToIdentity()
    {
        HttpIdentityResolver r = ResolverWith(
            """
            <fingerprints matches="http_header.server">
              <fingerprint pattern="^AcmeOS/([\d.]+)$">
                <param pos="0" name="hw.vendor" value="Acme"/>
                <param pos="0" name="hw.device" value="Router"/>
                <param pos="0" name="os.product" value="AcmeOS"/>
                <param pos="1" name="service.version"/>
              </fingerprint>
            </fingerprints>
            """
        );

        HttpIdentity? id = r.Resolve(new HttpIdentitySignals("AcmeOS/1.0", null, null, null));

        Assert.NotNull(id);
        Assert.Equal("Acme", id.Vendor);
        Assert.Equal("Router", id.DeviceType);
        Assert.Equal("AcmeOS", id.Os);
        Assert.Equal("1.0", id.Firmware);
        Assert.Equal(0.75, id.Confidence); // server base, no corroboration
        Assert.Contains("vendor:server", id.Provenance);
    }

    [Fact]
    public void StrongerSignalWinsPerField()
    {
        HttpIdentityResolver r = ResolverWith(
            """
            <fingerprints matches="favicon.md5">
              <fingerprint pattern="^deadbeef$"><param pos="0" name="hw.vendor" value="FromFavicon"/></fingerprint>
            </fingerprints>
            """,
            """
            <fingerprints matches="http_header.server">
              <fingerprint pattern="^srv$"><param pos="0" name="hw.vendor" value="FromServer"/></fingerprint>
            </fingerprints>
            """
        );

        HttpIdentity? id = r.Resolve(new HttpIdentitySignals("srv", null, null, "deadbeef"));

        Assert.NotNull(id);
        Assert.Equal("FromFavicon", id.Vendor); // favicon (0.85) outranks server (0.75)
        Assert.Contains("vendor:favicon", id.Provenance);
    }

    [Fact]
    public void ConfidenceTracksWeakestUsedWhenOnlyTitleMatches()
    {
        HttpIdentityResolver r = ResolverWith(
            """
            <fingerprints matches="html_title">
              <fingerprint pattern="^Acme Login$"><param pos="0" name="hw.vendor" value="Acme"/></fingerprint>
            </fingerprints>
            """
        );

        HttpIdentity? id = r.Resolve(new HttpIdentitySignals(null, "Acme Login", null, null));

        Assert.NotNull(id);
        Assert.Equal(0.60, id.Confidence); // title base only
    }

    [Fact]
    public void IndependentAgreementBoostsConfidence()
    {
        HttpIdentityResolver r = ResolverWith(
            """
            <fingerprints matches="favicon.md5">
              <fingerprint pattern="^abc$"><param pos="0" name="hw.vendor" value="Acme"/></fingerprint>
            </fingerprints>
            """,
            """
            <fingerprints matches="http_header.server">
              <fingerprint pattern="^srv$"><param pos="0" name="hw.vendor" value="Acme"/></fingerprint>
            </fingerprints>
            """
        );

        HttpIdentity? id = r.Resolve(new HttpIdentitySignals("srv", null, null, "abc"));

        Assert.NotNull(id);
        Assert.Equal("Acme", id.Vendor);
        Assert.Equal(0.95, id.Confidence); // 0.85 favicon base + 0.10 corroboration
    }

    [Fact]
    public void EndToEnd_ResolvesRealDeviceFromEmbeddedCorpus()
    {
        // Full path through the actual embedded Recog corpus, using one of its real example strings.
        HttpIdentityResolver r = new(RecogCorpus.LoadEmbedded());

        HttpIdentity? id = r.Resolve(new HttpIdentitySignals("ReeCam IP Camera", null, null, null));

        Assert.NotNull(id);
        Assert.Equal("Shenzhen Reecam Tech. Ltd.", id.Vendor);
        Assert.Equal("IP Camera", id.DeviceType);
        Assert.Contains("vendor:server", id.Provenance);
    }

    [Fact]
    public void NoSignalsMatch_ReturnsNull()
    {
        HttpIdentityResolver r = ResolverWith(
            """
            <fingerprints matches="http_header.server">
              <fingerprint pattern="^only-this$"><param pos="0" name="hw.vendor" value="X"/></fingerprint>
            </fingerprints>
            """
        );

        Assert.Null(r.Resolve(new HttpIdentitySignals("something-else", null, null, null)));
    }

    [Fact]
    public void Combine_DeepFollowUpOverridesShallowAndMergesProvenance()
    {
        HttpIdentity shallow = new(
            Vendor: "GenericGuess",
            Model: null,
            Firmware: null,
            DeviceType: "Router",
            Os: "Linux",
            Serial: null,
            Name: null,
            Confidence: 0.75,
            Provenance: "vendor:server,type:server,os:server"
        );
        HttpFollowUpResult deep = new(
            new HttpDeepFields(Vendor: "Netgear", Model: "R7000", Serial: "ABC123", FriendlyName: "Living Room"),
            "upnp",
            0.95
        );

        HttpIdentity? c = HttpIdentityResolver.Combine(shallow, deep);

        Assert.NotNull(c);
        Assert.Equal("Netgear", c.Vendor); // deep overrides shallow
        Assert.Equal("R7000", c.Model);
        Assert.Equal("ABC123", c.Serial);
        Assert.Equal("Living Room", c.Name);
        Assert.Equal("Router", c.DeviceType); // preserved from shallow
        Assert.Equal("Linux", c.Os);
        Assert.Equal(0.95, c.Confidence); // max(0.75, 0.95)
        Assert.Contains("vendor:upnp", c.Provenance); // re-sourced to the follow-up
        Assert.Contains("type:server", c.Provenance); // shallow source retained
        Assert.Contains("serial:upnp", c.Provenance);
    }

    [Fact]
    public void Combine_ShallowOnly_WhenNoFollowUp()
    {
        HttpIdentity shallow = new("Acme", null, null, null, null, null, null, 0.6, "vendor:title");
        Assert.Same(shallow, HttpIdentityResolver.Combine(shallow, null));
    }

    [Fact]
    public void Combine_DeepOnly_WhenNoShallow()
    {
        HttpFollowUpResult deep = new(new HttpDeepFields(Vendor: "Netgear", Serial: "S1"), "upnp", 0.95);
        HttpIdentity? c = HttpIdentityResolver.Combine(null, deep);

        Assert.NotNull(c);
        Assert.Equal("Netgear", c.Vendor);
        Assert.Equal("S1", c.Serial);
        Assert.Equal(0.95, c.Confidence);
        Assert.Equal("vendor:upnp,serial:upnp", c.Provenance);
    }

    [Fact]
    public void MatchWithNoMappedFields_ReturnsNull()
    {
        // A fingerprint that only sets cpe23 (which we don't map to an identity field) is not identity.
        HttpIdentityResolver r = ResolverWith(
            """
            <fingerprints matches="http_header.server">
              <fingerprint pattern="^srv$"><param pos="0" name="service.cpe23" value="cpe:/a:x:y:-"/></fingerprint>
            </fingerprints>
            """
        );

        Assert.Null(r.Resolve(new HttpIdentitySignals("srv", null, null, null)));
    }
}