using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;
using JMW.Discovery.Server.ManualFacts;

namespace JMW.Discovery.UnitTests.Server;

/// <summary>
/// Unit tests for ManualFactCatalog — the reflected set of existing FactPaths constants an
/// operator may set directly (docs/plans/user-provided.md).
/// </summary>
public sealed class ManualFactCatalogTests
{
    [Fact]
    public void IncludesScalarDeviceFields()
    {
        Assert.Contains(FactPaths.SystemHostname, ManualFactCatalog.EditablePaths);
        Assert.Contains(FactPaths.DeviceKind, ManualFactCatalog.EditablePaths);
    }

    [Fact]
    public void ExcludesMetricPaths()
    {
        Assert.DoesNotContain(FactPaths.InterfaceRxBytes, ManualFactCatalog.EditablePaths);
    }

    [Fact]
    public void ExcludesPathsWithMoreThanOneListDimension()
    {
        // Device + Temperature — an operator editing "this device" has no interface/zone key.
        Assert.DoesNotContain(FactPaths.HwTemperatureCelsius, ManualFactCatalog.EditablePaths);
        Assert.DoesNotContain(FactPaths.InterfaceSpeedBps, ManualFactCatalog.EditablePaths);
    }

    [Fact]
    public void ExcludesDerivedAndServicePaths()
    {
        // FactPaths.Derived / ServicePaths constants are never reflected in the first place —
        // pin the specific values that would otherwise be tempting to include.
        Assert.DoesNotContain(FactPaths.Derived.DeviceVendorGuess, ManualFactCatalog.EditablePaths);
        Assert.DoesNotContain(FactPaths.Derived.DeviceVendorCanonical, ManualFactCatalog.EditablePaths);
    }

    [Fact]
    public void EveryEntryHasExactlyOneDeviceListDimension()
    {
        foreach (string path in ManualFactCatalog.EditablePaths)
        {
            FactSegment[] segments = FactSegment.ParsePath(path);
            FactSegment[] listSegments = segments.Where(s => s.IsList).ToArray();
            Assert.True(listSegments.Length == 1 && listSegments[0].Name == "Device",
                $"'{path}' has an unexpected list-dimension shape for a manually-editable field."
            );
        }
    }
}