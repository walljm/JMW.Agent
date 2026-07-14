using JMW.Discovery.Agent.Collection.Device.OnHub;

namespace JMW.Discovery.UnitTests.Agent.OnHub;

public sealed class OnHubTextFormatTests
{
    [Fact]
    public void Parse_ScalarFields_QuotedAndBareword()
    {
        IReadOnlyList<TextNode> nodes = OnHubTextFormat.Parse(
            """
            state_seq_no: "9784"
            connected: true
            count: 42
            """
        );

        Assert.Equal("9784", Scalar(nodes, "state_seq_no"));
        Assert.Equal("true", Scalar(nodes, "connected"));
        Assert.Equal("42", Scalar(nodes, "count"));
    }

    [Fact]
    public void Parse_NestedMessage_BuildsChildren()
    {
        IReadOnlyList<TextNode> nodes = OnHubTextFormat.Parse(
            """
            timestamp {
              seconds: 1783458962
              nanos: 5
            }
            """
        );

        TextNode ts = Single(nodes, "timestamp");
        Assert.Null(ts.Value);
        Assert.Equal("1783458962", ts.ScalarOf("seconds"));
        Assert.Equal("5", ts.ScalarOf("nanos"));
    }

    [Fact]
    public void Parse_RepeatedKeys_AllRetained()
    {
        IReadOnlyList<TextNode> nodes = OnHubTextFormat.Parse(
            """
            station {
              ip_addresses: "192.168.1.10"
              ip_addresses: "192.168.1.11"
            }
            """
        );

        TextNode station = Single(nodes, "station");
        string[] ips = station.ChildrenNamed("ip_addresses").Select(n => n.Value!).ToArray();
        Assert.Equal(["192.168.1.10", "192.168.1.11"], ips);
    }

    [Fact]
    public void Parse_QuotedValue_ResolvesEscapesAndKeepsSpecialChars()
    {
        IReadOnlyList<TextNode> nodes = OnHubTextFormat.Parse(
            """
            a: "model=MacBookPro14,2"
            b: "line\none\ttab"
            c: "quote\"inside"
            d: "braces { } and : colon"
            """
        );

        Assert.Equal("model=MacBookPro14,2", Scalar(nodes, "a"));
        Assert.Equal("line\none\ttab", Scalar(nodes, "b"));
        Assert.Equal("quote\"inside", Scalar(nodes, "c"));
        Assert.Equal("braces { } and : colon", Scalar(nodes, "d"));
    }

    [Fact]
    public void Parse_MaskedValues_PreservedVerbatim()
    {
        IReadOnlyList<TextNode> nodes = OnHubTextFormat.Parse(
            """
            station_info {
              mac_address: "00e0bf1fc40*"
              station_id: "****"
            }
            """
        );

        TextNode s = Single(nodes, "station_info");
        Assert.Equal("00e0bf1fc40*", s.ScalarOf("mac_address"));
        Assert.Equal("****", s.ScalarOf("station_id"));
    }

    [Fact]
    public void Parse_DeeplyNested_FindsLeaf()
    {
        IReadOnlyList<TextNode> nodes = OnHubTextFormat.Parse(
            """
            outer {
              middle {
                inner {
                  key: "value"
                }
              }
            }
            """
        );

        TextNode inner = Single(Single(Single(nodes, "outer").Children, "middle").Children, "inner").Children[0];
        Assert.Equal("key", inner.Name);
        Assert.Equal("value", inner.Value);
    }

    [Fact]
    public void Parse_Comments_Ignored()
    {
        IReadOnlyList<TextNode> nodes = OnHubTextFormat.Parse(
            """
            # a leading comment
            a: "1"
            # another
            b: "2"
            """
        );

        Assert.Equal("1", Scalar(nodes, "a"));
        Assert.Equal("2", Scalar(nodes, "b"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  \n")]
    [InlineData("}}}}")] // stray closes
    [InlineData("garbage without structure ~~~")]
    public void Parse_MalformedOrEmpty_DoesNotThrow(string input)
    {
        Exception? ex = Record.Exception(() => OnHubTextFormat.Parse(input));
        Assert.Null(ex);
    }

    [Fact]
    public void Parse_TruncatedMessage_RecoversWhatItCan()
    {
        // Missing closing brace at EOF — should still capture the field it saw.
        IReadOnlyList<TextNode> nodes = OnHubTextFormat.Parse(
            """
            station_info {
              mac_address: "00e0bf1fc40*"
            """
        );

        Assert.Equal("00e0bf1fc40*", Single(nodes, "station_info").ScalarOf("mac_address"));
    }

    private static string? Scalar(IReadOnlyList<TextNode> nodes, string name) =>
        nodes.First(n => n.Name == name).Value;

    private static TextNode Single(IEnumerable<TextNode> nodes, string name) =>
        nodes.Single(n => n.Name == name);
}