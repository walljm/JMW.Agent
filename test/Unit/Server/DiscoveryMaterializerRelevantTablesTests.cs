using JMW.Discovery.Server;
using JMW.Discovery.Server.Projections;

using Npgsql;

namespace JMW.Discovery.UnitTests.Server;

/// <summary>
/// Fitness function for performance-03's gating: FactsEndpoint only calls
/// <see cref="DiscoveryMaterializer.MaterializeAsync" /> when a batch touched one of
/// <see cref="DiscoveryMaterializer.RelevantTables" />. Those strings are hand-maintained
/// alongside DiscoveryMaterializer's sub-passes — this test catches a typo or a renamed
/// projection table silently making the gate permanently false (materializer never runs again)
/// rather than failing loudly.
/// </summary>
public sealed class DiscoveryMaterializerRelevantTablesTests
{
    // Tables written outside ProjectionLibrary (not a GenericProjection) that RelevantTables is
    // still allowed to name. materialization_facts is fact-shaped, written by
    // IdentityFactProjection (docs/plans/architecture-identity-facts.md) — same reasoning as
    // MergeRepointCoverageTests.ExplicitlyRepointedExtras.
    private static readonly HashSet<string> NonProjectionLibraryExtras =
        new(StringComparer.Ordinal) { "materialization_facts" };

    [Fact]
    public void EveryRelevantTable_MatchesARegisteredProjection()
    {
        using NpgsqlDataSource ds = NpgsqlDataSource.Create("Host=localhost"); // never opened
        HashSet<string> registeredTables = ProjectionLibrary.CreateAll(ds)
            .OfType<GenericProjection>()
            .Select(p => p.Def.TableName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string table in DiscoveryMaterializer.RelevantTables)
        {
            if (NonProjectionLibraryExtras.Contains(table))
            {
                continue;
            }

            Assert.Contains(table, registeredTables);
        }
    }

    [Fact]
    public void RelevantTables_IsNotEmpty()
    {
        // An empty set would make the gate in FactsEndpoint always false — the materializer
        // would never run again. Guards against an accidental clear of the field.
        Assert.NotEmpty(DiscoveryMaterializer.RelevantTables);
    }
}