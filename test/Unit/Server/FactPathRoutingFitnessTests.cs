using System.Reflection;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;
using JMW.Discovery.Server.FactViews;
using JMW.Discovery.Server.Projections;

using Npgsql;

namespace JMW.Discovery.UnitTests.Server;

/// <summary>
/// Fitness function: every fact path the system can emit must have a curated home — a projection
/// column (cross-device query) or a fact view (device/service detail). There is no raw-only escape
/// hatch: if a fact isn't worth showing, stop emitting it and delete the constant. Without this,
/// forgetting a mapping silently routes a fact to nowhere — no compile error, no failure — the
/// exact silent data-loss the routing grammar makes easy. New unrouted path ⇒ red test.
/// </summary>
public sealed class FactPathRoutingFitnessTests
{
    // Locks the SINGLE routing-derivation contract both the emit and projection sides now
    // share (Fact.DeriveAttribute / DeriveDimKey), including the cases the two former
    // independent algorithms handled — and the Attr-sink case they used to disagree on.
    [Theory]
    [InlineData("Device[].Interface[].SpeedBps", "Device|Interface", "SpeedBps")]
    [InlineData("Device[].OS.Hostname", "Device", "OS.Hostname")]
    [InlineData("Service[x].DNS.Zone[y].Type", "Service|Zone", "Type")] // scalar between two lists
    [InlineData("Service[].DNS.Stats.TotalQueries", "Service", "DNS.Stats.TotalQueries")]
    [InlineData("Device[].Discovered[].Attr[key]", "Device|Discovered|Attr", "")] // ends on a list → no attribute
    public void DeriveAttributeAndDimKey_MatchTheGrammar(string path, string dimKey, string attr)
    {
        Assert.Equal(dimKey, Fact.DeriveDimKey(path));
        Assert.Equal(attr, Fact.DeriveAttribute(path));
    }

    [Fact]
    public void EveryFactPathIsRoutedToProjectionOrFactView()
    {
        // (DimKey, Attribute) consumed by every projection column — derived the SAME way a
        // fact's own routing key is (Fact.DeriveAttribute), so this mirrors real routing.
        using NpgsqlDataSource ds = NpgsqlDataSource.Create("Host=localhost"); // never opened; db param is unused
        HashSet<(string DimKey, string Attr)> routed = ProjectionLibrary.CreateAll(ds)
            .OfType<GenericProjection>()
            .SelectMany(p => p.Def.Columns.Select(c =>
                    (string.Join("|", p.Def.DimensionNames), Fact.DeriveAttribute(c.Attribute))
                )
            )
            .ToHashSet();

        // Fact-path values surfaced by a device-detail fact view instead of a projection.
        HashSet<string> viewPaths = FactViewLibrary.AllConsumedFactPaths().ToHashSet(StringComparer.Ordinal);

        // Pure intermediates: consumed as SOME derivation's input, so they have a legitimate home
        // even with no projection column or fact view of their own (architecture-identity-facts.md
        // §12 Phase 6 — DeviceVendorGuess/DeviceOsGuess lost their "guess" columns and now only
        // feed the canonical fan-ins).
        HashSet<string> derivationInputPaths = AnalysisLibrary.CreateEngine().AllDerivationInputPaths
            .ToHashSet(StringComparer.Ordinal);

        List<string> unrouted = new();
        foreach ((string name, string path) in AllFactPaths())
        {
            string attr = Fact.DeriveAttribute(path);
            if (attr.Length == 0)
            {
                continue; // ends on a list segment (Attr[] sink) — structurally never routable
            }

            if (routed.Contains((Fact.DeriveDimKey(path), attr)))
            {
                continue; // has a projection home
            }

            if (viewPaths.Contains(path))
            {
                continue; // surfaced by a device-detail or service fact view
            }

            if (IdentitySignalPaths.All.Contains(path))
            {
                continue; // routed to materialization_facts (fact-shaped, not a GenericProjection column)
            }

            if (derivationInputPaths.Contains(path))
            {
                continue; // pure intermediate consumed as a derivation input — no column needed
            }

            unrouted.Add($"{name} = \"{path}\"  (DimKey='{Fact.DeriveDimKey(path)}', Attribute='{attr}')");
        }

        Assert.True(
            unrouted.Count == 0,
            "These fact-path constants emit facts that no projection column and no fact view "
          + "consumes — they would silently land in facts_history unrouted. Every emitted fact must "
          + "have a home: add a projection column (cross-device query) or a fact view (device/service "
          + "detail). If the data isn't worth showing, stop emitting it and delete the constant — "
          + "there is no raw-only escape hatch:\n"
          + string.Join("\n", unrouted)
        );
    }

    private static IEnumerable<(string Name, string Path)> AllFactPaths()
    {
        foreach ((string n, string p) in ConstsOf(typeof(FactPaths)))
        {
            yield return (n, p);
        }

        foreach ((string n, string p) in ConstsOf(typeof(FactPaths.Derived)))
        {
            yield return ("Derived." + n, p);
        }

        foreach ((string n, string p) in ConstsOf(typeof(ServicePaths)))
        {
            yield return (n, p);
        }
    }

    private static IEnumerable<(string Name, string Path)> ConstsOf(Type t) =>
        t.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (f.Name, (string)f.GetValue(null)!));
}