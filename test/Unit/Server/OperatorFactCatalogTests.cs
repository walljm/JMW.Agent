using JMW.Discovery.Core.Analysis;
using JMW.Discovery.Server.ManualFacts;

namespace JMW.Discovery.UnitTests.Server;

/// <summary>
/// Disambiguation + carve-out classification for unified operator facts (REQ-003, NFR-8,
/// architecture §5.2). The ordering of the write gate is load-bearing: dimension block first, then
/// identity const, then Derived/Metric, then catalog override, else arbitrary.
/// </summary>
public sealed class OperatorFactCatalogTests
{
    [Theory]
    [InlineData(FactPaths.DeviceKind)]
    [InlineData(FactPaths.InterfaceSpeedBps)] // child-collection catalog path, adjacent to InterfaceMAC
    [InlineData(FactPaths.SystemOsDistro)]
    [InlineData(FactPaths.SystemFriendlyName)] // materializer gap-fill read, but authorable by design
    public void Classify_CatalogPath_IsOverride(string path) =>
        Assert.Equal(OperatorFactCatalog.PathClass.Override, OperatorFactCatalog.Classify(path));

    [Theory]
    [InlineData(FactPaths.InterfaceMAC)]
    [InlineData(FactPaths.ArpMac)]
    [InlineData(FactPaths.DiscoveredMAC)]
    [InlineData(FactPaths.HwSystemSerial)]
    [InlineData(FactPaths.SystemHostname)]
    [InlineData(FactPaths.DiscoveredFriendlyName)] // the *promotion source* stays blocked; only the display rollup is authorable
    public void Classify_IdentityBearingConst_IsIdentityProtected(string path) =>
        Assert.Equal(OperatorFactCatalog.PathClass.IdentityProtected, OperatorFactCatalog.Classify(path));

    [Theory]
    [InlineData("Device[].Lease[].Source")]
    [InlineData("Device[].Lease[].Expires")]
    [InlineData("Device[].Lease[].IP")] // also an identity const, but dimension check wins (comes first)
    [InlineData("Device[].Lease[].Hostname")]
    [InlineData("Device[].Lease[].Foo")] // arbitrary attribute under the blocked dimension
    public void Classify_LeaseDimension_IsIdentityProtectedDimension(string path) =>
        Assert.Equal(OperatorFactCatalog.PathClass.IdentityProtectedDimension, OperatorFactCatalog.Classify(path));

    [Theory]
    [InlineData(FactPaths.InterfaceRxBytes)] // a MetricPaths entry
    public void Classify_MetricPath_IsNotAuthorable(string path) =>
        Assert.Equal(OperatorFactCatalog.PathClass.NotAuthorable, OperatorFactCatalog.Classify(path));

    [Fact]
    public void Classify_DerivedPath_IsNotAuthorable() =>
        Assert.Equal(
            OperatorFactCatalog.PathClass.NotAuthorable,
            OperatorFactCatalog.Classify(FactPaths.Derived.FsUsedPercent)
        );

    [Theory]
    [InlineData("Device[].SwitchPortLabel")]
    [InlineData("Device[].Rack.Position")]
    [InlineData("Device[].SwitchPort[].Label")]
    public void Classify_UnknownPath_IsArbitrary(string path) =>
        Assert.Equal(OperatorFactCatalog.PathClass.Arbitrary, OperatorFactCatalog.Classify(path));

    [Fact]
    public void OverridablePaths_ExcludeIdentityMetricAndDerivedButKeepAdjacentOverridable()
    {
        Assert.Contains(FactPaths.InterfaceSpeedBps, OperatorFactCatalog.OverridablePaths);
        Assert.DoesNotContain(FactPaths.InterfaceMAC, OperatorFactCatalog.OverridablePaths);
        Assert.DoesNotContain(FactPaths.ArpMac, OperatorFactCatalog.OverridablePaths);
        Assert.DoesNotContain(FactPaths.HwSystemSerial, OperatorFactCatalog.OverridablePaths);
        Assert.DoesNotContain(FactPaths.DhcpLocalLeaseIP, OperatorFactCatalog.OverridablePaths); // Lease dimension
        Assert.DoesNotContain(FactPaths.InterfaceRxBytes, OperatorFactCatalog.OverridablePaths); // metric
    }

    [Fact]
    public void IsOverride_TreatsAnyCatalogPathAsOverride_IncludingProtectedOnes()
    {
        Assert.True(OperatorFactCatalog.IsOverride(FactPaths.DeviceKind));
        Assert.True(OperatorFactCatalog.IsOverride(FactPaths.InterfaceMAC)); // protected, but still a catalog path
        Assert.False(OperatorFactCatalog.IsOverride("Device[].SwitchPortLabel"));
    }
}