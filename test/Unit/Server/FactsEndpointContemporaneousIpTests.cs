using System.Reflection;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;
using JMW.Discovery.Server.Agents;

namespace JMW.Discovery.UnitTests.Server;

/// <summary>
/// <see cref="FactsEndpoint" />'s CollectContemporaneousIps feeds the same-collection-cycle IP
/// correlation merge (ADR-002 amendment, 2026-07-23): devices resolved within one agent request
/// that report the same interface IP in their own facts are corroborated as one physical host.
/// </summary>
public sealed class FactsEndpointContemporaneousIpTests
{
    private static Dictionary<string, HashSet<string>> Invoke(List<Fact> facts, string deviceId)
    {
        MethodInfo m = typeof(FactsEndpoint).GetMethod("CollectContemporaneousIps", BindingFlags.NonPublic | BindingFlags.Static)
         ?? throw new InvalidOperationException("FactsEndpoint.CollectContemporaneousIps not found.");
        Dictionary<string, HashSet<string>> map = new(StringComparer.Ordinal);
        m.Invoke(null, [facts, deviceId, map]);
        return map;
    }

    [Fact]
    public void InterfaceIPv4_StripsCidrSuffix()
    {
        Fact fact = Fact.Create("Device[device-1].Interface[eth0].IPv4", "192.168.1.5/24");

        Dictionary<string, HashSet<string>> map = Invoke([fact], "device-1");

        HashSet<string> ids = Assert.Single(map).Value;
        Assert.Equal("192.168.1.5", Assert.Single(map).Key);
        Assert.Contains("device-1", ids);
    }

    [Fact]
    public void SshInterfaceIP_BareAddress_NoCidrToStrip()
    {
        Fact fact = Fact.Create("Device[device-2].Interface[eth0].IP", "10.0.0.7");

        Dictionary<string, HashSet<string>> map = Invoke([fact], "device-2");

        KeyValuePair<string, HashSet<string>> entry = Assert.Single(map);
        Assert.Equal("10.0.0.7", entry.Key);
        Assert.Contains("device-2", entry.Value);
    }

    [Fact]
    public void TwoDevices_SameIp_BothCollectedUnderOneKey()
    {
        Fact factA = Fact.Create("Device[device-a].Interface[eth0].IPv4", "192.168.1.9/24");
        Fact factB = Fact.Create("Device[device-b].Interface[eth0].IP", "192.168.1.9");

        Dictionary<string, HashSet<string>> map = Invoke([factA], "device-a");
        Invoke([factB], "device-b").ToList().ForEach(kv =>
        {
            if (!map.TryGetValue(kv.Key, out HashSet<string>? existing))
            {
                map[kv.Key] = kv.Value;
                return;
            }

            existing.UnionWith(kv.Value);
        });

        HashSet<string> ids = Assert.Single(map).Value;
        Assert.Equal(2, ids.Count);
        Assert.Contains("device-a", ids);
        Assert.Contains("device-b", ids);
    }

    [Fact]
    public void UnrelatedFactPath_Ignored()
    {
        Fact fact = Fact.Create(FactPaths.SystemHostname.Replace("Device[]", "Device[device-1]"), "router-1");

        Dictionary<string, HashSet<string>> map = Invoke([fact], "device-1");

        Assert.Empty(map);
    }

    [Fact]
    public void BlankValue_Ignored()
    {
        Fact fact = Fact.Create("Device[device-1].Interface[eth0].IPv4", "");

        Dictionary<string, HashSet<string>> map = Invoke([fact], "device-1");

        Assert.Empty(map);
    }
}