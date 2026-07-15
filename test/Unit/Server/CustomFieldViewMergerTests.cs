using JMW.Discovery.Core.Analysis;
using JMW.Discovery.Server.FactViews;
using JMW.Discovery.Server.ManualFacts;

namespace JMW.Discovery.UnitTests.Server;

/// <summary>
/// Unit tests for CustomFieldViewMerger — merging runtime custom_field_definitions rows into
/// the compiled FactViewLibrary (docs/plans/user-provided.md).
/// </summary>
public sealed class CustomFieldViewMergerTests
{
    private static CustomFieldDefinition Def(
        string slug,
        string? targetViewTitle,
        string? targetViewGroup = null,
        bool isNewView = false
    ) => new(Guid.NewGuid(), "Label " + slug, slug, targetViewTitle, targetViewGroup, isNewView,
        DateTimeOffset.UtcNow, "user:test"
    );

    [Fact]
    public void NoTargetedDefs_LeavesCompiledListUnchanged()
    {
        IReadOnlyList<FactViewDef> merged = CustomFieldViewMerger.MergeDefs(FactViewLibrary.All, [Def("a", null)]);

        Assert.Equal(FactViewLibrary.All.Count, merged.Count);
        Assert.All(FactViewLibrary.All.Zip(merged), pair => Assert.Equal(pair.First.Columns.Count,
            pair.Second.Columns.Count)
        );
    }

    [Fact]
    public void AttachToExistingPropertiesView_AppendsColumn()
    {
        List<CustomFieldDefinition> defs = [Def("warranty-expiration", "OS Details")];

        IReadOnlyList<FactViewDef> merged = CustomFieldViewMerger.MergeDefs(FactViewLibrary.All, defs);

        FactViewDef osDetails = Assert.Single(merged, v => v.Title == "OS Details");
        FactViewColumn column = Assert.Single(osDetails.Columns, c => c.KeyFilter == "warranty-expiration");
        Assert.Equal("Label warranty-expiration", column.Label);
        Assert.Equal(FactPaths.CustomFieldValue, column.FactPath);
    }

    [Fact]
    public void AttachToUnknownOrNonPropertiesView_IsIgnored()
    {
        // "Thermal" exists but is List-kind, not Properties — must not be spliced into.
        List<CustomFieldDefinition> defs = [Def("x", "Thermal"), Def("y", "Nonexistent View")];

        IReadOnlyList<FactViewDef> merged = CustomFieldViewMerger.MergeDefs(FactViewLibrary.All, defs);

        FactViewDef thermal = Assert.Single(merged, v => v.Title == "Thermal");
        Assert.DoesNotContain(thermal.Columns, c => c.KeyFilter is not null);
        Assert.Equal(FactViewLibrary.All.Count, merged.Count);
    }

    [Fact]
    public void NewView_SynthesizesPropertiesViewGroupingAllItsFields()
    {
        List<CustomFieldDefinition> defs =
        [
            Def("asset-tag", "Asset Info", nameof(FactViewGroup.Custom), isNewView: true),
            Def("purchase-date", "Asset Info", nameof(FactViewGroup.Custom), isNewView: true),
        ];

        IReadOnlyList<FactViewDef> merged = CustomFieldViewMerger.MergeDefs(FactViewLibrary.All, defs);

        FactViewDef assetInfo = Assert.Single(merged, v => v.Title == "Asset Info");
        Assert.Equal(FactViewKind.Properties, assetInfo.Kind);
        Assert.Equal(FactViewGroup.Custom, assetInfo.Group);
        Assert.Equal(2, assetInfo.Columns.Count);
        Assert.Contains(assetInfo.Columns, c => c.KeyFilter == "asset-tag");
        Assert.Contains(assetInfo.Columns, c => c.KeyFilter == "purchase-date");
    }

    [Fact]
    public void FilterBaselineRows_DropsRowsForTargetedSlugsOnly()
    {
        RenderedFactView baseline = new(
            FactViewLibrary.CustomFieldsViewTitle,
            ["Field", "Value"],
            [["targeted", "1"], ["untargeted", "2"]],
            FactViewGroup.Custom,
            FactViewKind.List
        );
        List<CustomFieldDefinition> defs = [Def("targeted", "OS Details"), Def("untargeted", null)];

        IReadOnlyList<RenderedFactView> filtered = CustomFieldViewMerger.FilterBaselineRows([baseline], defs);

        RenderedFactView result = Assert.Single(filtered);
        IReadOnlyList<string?> row = Assert.Single(result.Rows);
        Assert.Equal("untargeted", row[0]);
    }

    [Fact]
    public void FilterBaselineRows_RemovesViewEntirelyWhenEveryRowIsTargeted()
    {
        RenderedFactView baseline = new(
            FactViewLibrary.CustomFieldsViewTitle,
            ["Field", "Value"],
            [["a", "1"]],
            FactViewGroup.Custom,
            FactViewKind.List
        );
        List<CustomFieldDefinition> defs = [Def("a", "OS Details")];

        IReadOnlyList<RenderedFactView> filtered = CustomFieldViewMerger.FilterBaselineRows([baseline], defs);

        Assert.Empty(filtered);
    }
}