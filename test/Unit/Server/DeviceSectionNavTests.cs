using JMW.Discovery.Server.FactViews;
using JMW.Discovery.Server.Pages.Reports;

namespace JMW.Discovery.Tests.Server;

/// <summary>
/// Guards the device-detail section nav ↔ panel id contract. Each fact view becomes its own
/// nav item (data-tab) and panel (data-panel), both derived from the view Title via
/// <see cref="DeviceDetailModel.FactViewSectionId" />. If two views slugged to the same id, or a
/// fact-view id collided with a built-in section id, clicking one nav item would toggle the wrong
/// (or multiple) panels. This test locks that invariant as the fact-view library grows.
/// </summary>
public sealed class DeviceSectionNavTests
{
    // The built-in (non-fact-view) section ids rendered by DeviceDetail.cshtml.
    private static readonly string[] BuiltinIds =
    [
        "hardware", "components", "disks", "filesystems", "interfaces", "ports",
        "advertised", "sightings", "system", "containers", "bacnet", "modbus",
        "sources", "services", "allfacts",
    ];

    [Fact]
    public void FactViewSectionIds_AreUnique_PrefixedAndDoNotCollideWithBuiltins()
    {
        List<string> ids = FactViewLibrary.All
            .Select(v => DeviceDetailModel.FactViewSectionId(v.Title))
            .ToList();

        // Every id is fact-view-namespaced so it can never equal a built-in id.
        Assert.All(ids, id => Assert.StartsWith("fv-", id, StringComparison.Ordinal));

        // No two fact views slug to the same panel id.
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

        // Belt-and-suspenders: no overlap with the built-in section ids.
        Assert.Empty(ids.Intersect(BuiltinIds, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("Hardware Details", "fv-hardware-details")]
    [InlineData("DNS Servers", "fv-dns-servers")]
    [InlineData("Interface Counters", "fv-interface-counters")]
    public void FactViewSectionId_SlugsTitleToAStableId(string title, string expected) =>
        Assert.Equal(expected, DeviceDetailModel.FactViewSectionId(title));
}