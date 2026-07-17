using JMW.Discovery.Core.Analysis;
using JMW.Discovery.Server;
using JMW.Discovery.Server.ManualFacts;
using JMW.Discovery.Server.Projections;

namespace JMW.Discovery.UnitTests.Server;

/// <summary>
/// NFR-8 completeness fitness (architecture §4.3, two arms). The operator-facts identity exclusion
/// sets must stay in <b>exact</b> set-equality with the read set <c>DiscoveryMaterializer</c>
/// actually declares (<c>IdentityInputColumns</c>), so that neither a missing exclusion (a
/// materializer read newly added but not blocked — the device-merge-corruption vector) nor a bogus
/// exclusion (a blocked const with no backing read) can slip through. Exact equality fails on both
/// directions.
/// </summary>
public sealed class OperatorFactsIdentityFitnessTests
{
    [Fact]
    public void ValueArm_IdentityBearingFactPaths_ExactlyMatchesMaterializerValueReads()
    {
        // The expected exclusion set is built from three sources (docs/plans/architecture-identity-facts.md
        // §6.2, after the Phase-3 move):
        //   • the surviving wide-column reads — each Value-tagged (table, column) the materializer
        //     reads, mapped to the FactPaths const that projection column carries;
        //   • IdentitySignalPaths — the eleven materializer-only signals that moved to
        //     materialization_facts (no longer projection columns, so IdentitySignalPaths *is* their
        //     identity-bearing declaration);
        //   • plus HwSystemSerial (agent-path chassis-serial fingerprint, not a materializer read);
        //   • minus the gap-fill-only exemptions (reads that only decide whether promotion auto-fills
        //     display metadata — operator-authorable by design, see GapFillOnlyFactPaths).
        HashSet<string> expected = DiscoveryMaterializer.IdentityInputColumns
            .Where(c => c.Kind == DiscoveryMaterializer.IdentityInputKind.Value)
            .Select(c => MapValueColumnToConst(c.Table, c.ColumnOrDimension))
            .ToHashSet(StringComparer.Ordinal);
        expected.UnionWith(IdentitySignalPaths.All);
        expected.Add(FactPaths.HwSystemSerial);
        expected.ExceptWith(OperatorFactCatalog.GapFillOnlyFactPaths);

        Assert.Equal(
            expected.OrderBy(p => p, StringComparer.Ordinal),
            OperatorFactCatalog.IdentityBearingFactPaths.OrderBy(p => p, StringComparer.Ordinal)
        );
    }

    [Fact]
    public void DimensionArm_IdentityBearingDimensions_ExactlyMatchesMaterializerDimensionKeyReads()
    {
        HashSet<string> expected = DiscoveryMaterializer.IdentityInputColumns
            .Where(c => c.Kind == DiscoveryMaterializer.IdentityInputKind.DimensionKey)
            .Select(c => MapDimensionToDimKey(c.Table, c.ColumnOrDimension))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            expected.OrderBy(d => d, StringComparer.Ordinal),
            OperatorFactCatalog.IdentityBearingDimensions.OrderBy(d => d, StringComparer.Ordinal)
        );
    }

    [Fact]
    public void IdentityInputTables_AreASubsetOfRelevantTables()
    {
        // A materializer read must come from a table the batch-relevance guard already knows about;
        // otherwise the pass reading it can run stale. This backs the value/dimension arms above.
        HashSet<string> tables = DiscoveryMaterializer.IdentityInputColumns
            .Select(c => c.Table)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Subset(DiscoveryMaterializer.RelevantTables.ToHashSet(StringComparer.Ordinal), tables);
    }

    private static string MapValueColumnToConst(string table, string column)
    {
        ProjectionDef def = ProjectionLibrary.AllDefs.FirstOrDefault(d =>
                string.Equals(d.TableName, table, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"IdentityInputColumns references unknown projection table '{table}'.");

        ProjectionColumnDef col = def.Columns.FirstOrDefault(c =>
                string.Equals(c.ColumnName, column, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"IdentityInputColumns references unknown column '{column}' on projection '{table}'.");

        // ProjectionColumnDef.Attribute holds the full FactPaths template for this column.
        return col.Attribute;
    }

    private static string MapDimensionToDimKey(string table, string dimension)
    {
        ProjectionDef def = ProjectionLibrary.AllDefs.FirstOrDefault(d =>
                string.Equals(d.TableName, table, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"IdentityInputColumns references unknown projection table '{table}'.");

        if (!def.DimensionNames.Contains(dimension, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"IdentityInputColumns references unknown dimension '{dimension}' on projection '{table}'.");
        }

        // A submission's DimKey is every list-segment name in path order — the projection's full
        // dimension list, not just the fingerprint dimension.
        return string.Join("|", def.DimensionNames);
    }
}
