using System.Reflection;

using JMW.Discovery.Core;
using JMW.Discovery.Server.Agents;

namespace JMW.Discovery.UnitTests.Server;

/// <summary>
/// <see cref="FactsEndpoint" />'s RewriteFactIds/RewriteServiceFactIds rebuild each incoming
/// Fact via Fact.Create() to swap the agent's placeholder root key for the resolved
/// device/service id. That rebuild silently dropped Source back to Unknown until fixed here
/// -- found by a live deployment showing every ingested fact stamped Unknown despite the
/// agent correctly setting it before the wire; the loss was in this server-side rewrite, not
/// in JSON (de)serialization or the agent's own fact construction (both covered by other
/// tests and were already correct).
/// </summary>
public sealed class FactsEndpointRewriteTests
{
    private static readonly Guid TestAgentId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static List<Fact> Invoke(string methodName, IReadOnlyList<Fact> facts, string newKey)
    {
        MethodInfo m = typeof(FactsEndpoint).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
         ?? throw new InvalidOperationException($"FactsEndpoint.{methodName} not found.");
        return (List<Fact>)m.Invoke(null, [facts, newKey, DateTimeOffset.UtcNow, TestAgentId])!;
    }

    [Fact]
    public void RewriteFactIds_PreservesSource()
    {
        Fact original = Fact.Create("Device[_local_].Hostname", "router-1") with { Source = FactSource.HttpBanner };

        List<Fact> rewritten = Invoke("RewriteFactIds", [original], "device-123");

        Fact result = Assert.Single(rewritten);
        Assert.Equal(FactSource.HttpBanner, result.Source);
        Assert.Equal("Device[device-123].Hostname", result.Id);
        Assert.Equal(TestAgentId, result.AgentId);
    }

    [Fact]
    public void RewriteServiceFactIds_PreservesSource()
    {
        Fact original = Fact.Create("Service[_pending_].DNS.Stats.TotalQueries", 42L) with
        {
            Source = FactSource.TechnitiumDns,
        };

        List<Fact> rewritten = Invoke("RewriteServiceFactIds", [original], "service-456");

        Fact result = Assert.Single(rewritten);
        Assert.Equal(FactSource.TechnitiumDns, result.Source);
        Assert.Equal("Service[service-456].DNS.Stats.TotalQueries", result.Id);
        Assert.Equal(TestAgentId, result.AgentId);
    }
}