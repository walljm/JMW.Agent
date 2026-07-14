using System.Reflection;

using JMW.Discovery.Agent.Collection;
using JMW.Discovery.Server.Pages.Fleet;

namespace JMW.Discovery.UnitTests.Server;

/// <summary>
/// Fitness function: AgentDetail's hardcoded KnownCollectors/KnownScanners arrays (used to render
/// toggles and, via collectors_config keys, to gate what actually runs) must exactly match the
/// real ILocalCollector/INetworkScanner classes in the agent assembly. These lists have already
/// drifted once — five scanner entries used a "Probe" suffix that doesn't match any real class,
/// so toggling them silently did nothing (Agent.cs looks up config by scanner.GetType().Name).
/// This test fails the build the next time a class is renamed or added without updating the UI list.
/// </summary>
public sealed class AgentDetailKnownScannerFitnessTests
{
    [Fact]
    public void KnownScanners_MatchesEveryRegisteredScannerClass()
    {
        HashSet<string> actual = ConcreteTypeNames<INetworkScanner>();
        HashSet<string> known = AgentDetailModel.KnownScanners.ToHashSet();

        Assert.Equal(actual, known);
    }

    [Fact]
    public void KnownCollectors_MatchesEveryRegisteredLocalCollectorClass()
    {
        HashSet<string> actual = ConcreteTypeNames<ILocalCollector>();
        HashSet<string> known = AgentDetailModel.KnownCollectors.ToHashSet();

        Assert.Equal(actual, known);
    }

    [Fact]
    public void EveryKnownScannerHasAProtocolFamily()
    {
        // ScannerFamilies (E9's Discovery-tab grouping) must cover every scanner, or a new
        // scanner silently lands in the "Other" bucket instead of its real family.
        foreach (string name in AgentDetailModel.KnownScanners)
        {
            Assert.True(
                AgentDetailModel.ScannerFamilies.ContainsKey(name),
                $"{name} has no entry in ScannerFamilies — it will render under 'Other'."
            );
        }
    }

    [Fact]
    public void EveryKnownCollectorHasAStatName()
    {
        // CollectorStatNames maps class name -> the runtime slug each collector reports in
        // cycle stats, used to resolve the Host Collectors tab's inline Health cue. A missing
        // entry means that collector's health silently shows "—" forever.
        foreach (string name in AgentDetailModel.KnownCollectors)
        {
            Assert.True(
                AgentDetailModel.CollectorStatNames.ContainsKey(name),
                $"{name} has no entry in CollectorStatNames — its Health cue will always show '—'."
            );
        }
    }

    [Fact]
    public void EveryKnownScannerHasAStatName()
    {
        // Same as EveryKnownCollectorHasAStatName, for the Discovery tab's scanners.
        foreach (string name in AgentDetailModel.KnownScanners)
        {
            Assert.True(
                AgentDetailModel.ScannerStatNames.ContainsKey(name),
                $"{name} has no entry in ScannerStatNames — its Health cue will always show '—'."
            );
        }
    }

    private static HashSet<string> ConcreteTypeNames<TInterface>()
    {
        Assembly agentAssembly = typeof(TInterface).Assembly;
        return agentAssembly.GetTypes()
            .Where(t => typeof(TInterface).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .Select(t => t.Name)
            .ToHashSet();
    }
}