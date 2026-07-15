using JMW.Discovery.Agent.Collection.Network;
using JMW.Discovery.Core;

using AgentClass = JMW.Discovery.Agent.Agent;

namespace JMW.Discovery.UnitTests.Agent;

/// <summary>
/// Agent.IsCollectorEnabledCore decides which collectors run absent (or beyond) explicit
/// server config. NetworkDiscoveryCollector — subnet-wide scanning — must default to off so
/// a freshly registered agent only reports on itself until an operator opts it in; every
/// other collector must default to on, matching the pre-existing "no server config = run
/// everything" contract these tests also pin.
/// </summary>
public sealed class CollectorDefaultsTests
{
    [Fact]
    public void NoServerConfig_NetworkDiscoveryCollector_DefaultsDisabled()
    {
        bool enabled = AgentClass.IsCollectorEnabledCore(null, nameof(NetworkDiscoveryCollector));
        Assert.False(enabled);
    }

    [Theory]
    [InlineData("HardwareCollector")]
    [InlineData("DiskCollector")]
    [InlineData("DockerCollector")]
    public void NoServerConfig_OtherCollectors_DefaultEnabled(string collectorClassName)
    {
        bool enabled = AgentClass.IsCollectorEnabledCore(null, collectorClassName);
        Assert.True(enabled);
    }

    [Fact]
    public void ServerConfigWithoutEntry_NetworkDiscoveryCollector_DefaultsDisabled()
    {
        Dictionary<string, CollectorSetting> collectors = new()
        {
            ["HardwareCollector"] = new CollectorSetting(Enabled: true, IntervalSecs: null),
        };

        bool enabled = AgentClass.IsCollectorEnabledCore(collectors, nameof(NetworkDiscoveryCollector));
        Assert.False(enabled);
    }

    [Fact]
    public void ServerConfigWithoutEntry_OtherCollector_DefaultsEnabled()
    {
        Dictionary<string, CollectorSetting> collectors = new()
        {
            [nameof(NetworkDiscoveryCollector)] = new CollectorSetting(Enabled: true, IntervalSecs: null),
        };

        bool enabled = AgentClass.IsCollectorEnabledCore(collectors, "HardwareCollector");
        Assert.True(enabled);
    }

    [Fact]
    public void ServerConfig_ExplicitOptIn_EnablesNetworkDiscoveryCollector()
    {
        Dictionary<string, CollectorSetting> collectors = new()
        {
            [nameof(NetworkDiscoveryCollector)] = new CollectorSetting(Enabled: true, IntervalSecs: null),
        };

        bool enabled = AgentClass.IsCollectorEnabledCore(collectors, nameof(NetworkDiscoveryCollector));
        Assert.True(enabled);
    }

    [Fact]
    public void ServerConfig_ExplicitDisable_DisablesOtherCollector()
    {
        Dictionary<string, CollectorSetting> collectors = new()
        {
            ["HardwareCollector"] = new CollectorSetting(Enabled: false, IntervalSecs: null),
        };

        bool enabled = AgentClass.IsCollectorEnabledCore(collectors, "HardwareCollector");
        Assert.False(enabled);
    }
}