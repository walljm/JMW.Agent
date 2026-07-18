using System.Text.Json;

using JMW.Discovery.Agent.Collection.Local;
using JMW.Discovery.Core;

namespace JMW.Discovery.UnitTests.Agent;

/// <summary>
/// Tests <see cref="DockerCollector.ParseNetworks" /> — the Docker <c>GET /networks</c> parser
/// that feeds host-local subnet classification (docs/plans/l3-topology.md Track 1). Covers the
/// default/user bridges, the routable driver families that must NOT be flagged host-local
/// downstream, multi-subnet fan-out, and the malformed/degenerate payloads a real daemon emits.
/// </summary>
public sealed class DockerCollectorTests
{
    private const string Dev = "dev-1";

    private static IReadOnlyList<Fact> Parse(string json) =>
        DockerCollector.ParseNetworks(Dev, JsonDocument.Parse(json).RootElement);

    private static string? Value(IEnumerable<Fact> facts, string id) =>
        facts.FirstOrDefault(f => f.Id == id) is { } f ? f.Value.AsString() : null;

    [Fact]
    public void ParseNetworks_DefaultBridge_EmitsNameDriverScopeAndDocker0()
    {
        const string json = """
            [
              {
                "Name": "bridge",
                "Driver": "bridge",
                "Scope": "local",
                "IPAM": { "Config": [ { "Subnet": "172.17.0.0/16", "Gateway": "172.17.0.1" } ] },
                "Options": { "com.docker.network.bridge.name": "docker0" }
              }
            ]
            """;

        IReadOnlyList<Fact> facts = Parse(json);

        Assert.Equal("bridge", Value(facts, $"Device[{Dev}].DockerNet[172.17.0.0/16].Name"));
        Assert.Equal("bridge", Value(facts, $"Device[{Dev}].DockerNet[172.17.0.0/16].Driver"));
        Assert.Equal("local", Value(facts, $"Device[{Dev}].DockerNet[172.17.0.0/16].Scope"));
        Assert.Equal("docker0", Value(facts, $"Device[{Dev}].DockerNet[172.17.0.0/16].BridgeName"));
    }

    [Fact]
    public void ParseNetworks_UserBridge_KeepsBrHashBridgeName()
    {
        const string json = """
            [
              {
                "Name": "mynet",
                "Driver": "bridge",
                "Scope": "local",
                "IPAM": { "Config": [ { "Subnet": "172.20.0.0/16" } ] },
                "Options": { "com.docker.network.bridge.name": "br-a1b2c3d4e5f6" }
              }
            ]
            """;

        IReadOnlyList<Fact> facts = Parse(json);

        Assert.Equal("mynet", Value(facts, $"Device[{Dev}].DockerNet[172.20.0.0/16].Name"));
        Assert.Equal("br-a1b2c3d4e5f6", Value(facts, $"Device[{Dev}].DockerNet[172.20.0.0/16].BridgeName"));
    }

    [Fact]
    public void ParseNetworks_Macvlan_EmitsDriverSoServerCanKeepItGloballyKeyed()
    {
        // macvlan holds real LAN IPs — the collector still reports it faithfully; the "not
        // host-local" decision is the server's (driver != bridge). Here we just prove the driver
        // survives so that classification has the signal it needs.
        const string json = """
            [
              {
                "Name": "pub",
                "Driver": "macvlan",
                "Scope": "local",
                "IPAM": { "Config": [ { "Subnet": "10.10.0.0/24" } ] }
              }
            ]
            """;

        IReadOnlyList<Fact> facts = Parse(json);

        Assert.Equal("macvlan", Value(facts, $"Device[{Dev}].DockerNet[10.10.0.0/24].Driver"));
        // No bridge-name option → no BridgeName fact.
        Assert.Null(Value(facts, $"Device[{Dev}].DockerNet[10.10.0.0/24].BridgeName"));
    }

    [Fact]
    public void ParseNetworks_MultipleSubnetsOnOneNetwork_FanOutToOneRowEach()
    {
        const string json = """
            [
              {
                "Name": "dual",
                "Driver": "bridge",
                "Scope": "local",
                "IPAM": { "Config": [ { "Subnet": "172.30.0.0/16" }, { "Subnet": "fd00::/64" } ] }
              }
            ]
            """;

        IReadOnlyList<Fact> facts = Parse(json);

        Assert.Equal("dual", Value(facts, $"Device[{Dev}].DockerNet[172.30.0.0/16].Name"));
        Assert.Equal("dual", Value(facts, $"Device[{Dev}].DockerNet[fd00::/64].Name"));
    }

    [Fact]
    public void ParseNetworks_HostAndNoneNetworks_EmitNothing()
    {
        // host/none have no IPAM.Config subnets — nothing to place on L3.
        const string json = """
            [
              { "Name": "host", "Driver": "host", "Scope": "local", "IPAM": { "Config": [] } },
              { "Name": "none", "Driver": "null", "Scope": "local", "IPAM": { "Config": [] } }
            ]
            """;

        Assert.Empty(Parse(json));
    }

    [Theory]
    [InlineData("[]")] // empty array
    [InlineData("{}")] // non-array root
    [InlineData("""[ { "Name": "x", "Driver": "bridge" } ]""")] // no IPAM at all
    [InlineData("""[ { "Name": "x", "Driver": "bridge", "IPAM": { "Config": [ { "Gateway": "1.2.3.4" } ] } } ]""")] // config entry with no Subnet
    [InlineData("""[ { "Name": "x", "Driver": "bridge", "IPAM": { "Config": [ { "Subnet": "" } ] } } ]""")] // blank subnet
    public void ParseNetworks_MalformedOrEmpty_EmitsNothing(string json)
    {
        Assert.Empty(Parse(json));
    }
}