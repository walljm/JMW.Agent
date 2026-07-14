using JMW.Discovery.Agent.Collection;
using JMW.Discovery.Core;

namespace JMW.Discovery.Tests;

public sealed class CollectorDeltaTrackerTests
{
    private static Fact F(string id, long value) =>
        Fact.Create(id, value, DateTimeOffset.UtcNow);

    private static Fact F(string id, string value) =>
        Fact.Create(id, value, DateTimeOffset.UtcNow);

    // ── First call ────────────────────────────────────────────────────────────

    [Fact]
    public void FirstCall_AllFactsReturned()
    {
        CollectorDeltaTracker tracker = new();
        Fact[] facts = new[]
        {
            F("Device[r1].Speed", 1000L),
            F("Device[r1].Enabled", "true"),
        };

        IReadOnlyList<Fact> changed = tracker.FilterChanged(facts);

        Assert.Equal(2, changed.Count);
    }

    // ── Second call, same values ──────────────────────────────────────────────

    [Fact]
    public void SecondCall_SameValues_ReturnsEmpty()
    {
        CollectorDeltaTracker tracker = new();
        Fact[] facts = new[]
        {
            F("Device[r1].Speed", 1000L),
            F("Device[r1].Hostname", "router-1"),
        };

        tracker.FilterChanged(facts);
        IReadOnlyList<Fact> changed = tracker.FilterChanged(facts);

        Assert.Empty(changed);
    }

    // ── Second call, one value changed ───────────────────────────────────────

    [Fact]
    public void SecondCall_OneChanged_ReturnsOnlyChanged()
    {
        CollectorDeltaTracker tracker = new();
        string speed = "Device[r1].Interface[eth0].Speed";
        string enabled = "Device[r1].Interface[eth0].Enabled";

        tracker.FilterChanged(
            new[]
            {
                F(speed, 1000L),
                F(enabled, "true"),
            }
        );

        // Speed changed, enabled did not
        IReadOnlyList<Fact> changed = tracker.FilterChanged(
            new[]
            {
                F(speed, 2000L),
                F(enabled, "true"),
            }
        );

        Assert.Single(changed);
        Assert.Equal(speed, changed[0].Id);
        Assert.Equal(2000L, changed[0].Value.AsLong());
    }

    // ── Value reverts to original ─────────────────────────────────────────────
    // A→B→A: third pass should report A as a change (B was the last confirmed state)

    [Fact]
    public void ValueReverts_ReportedAsChange()
    {
        CollectorDeltaTracker tracker = new();
        string id = "Device[r1].Speed";

        tracker.FilterChanged(
            new[]
            {
                F(id, 1000L),
            }
        ); // A — new
        tracker.FilterChanged(
            new[]
            {
                F(id, 2000L),
            }
        ); // B — changed
        IReadOnlyList<Fact> changed = tracker.FilterChanged(
            new[]
            {
                F(id, 1000L),
            }
        ); // A again

        Assert.Single(changed);
        Assert.Equal(1000L, changed[0].Value.AsLong());
    }

    // ── New fact appears in subsequent cycle ──────────────────────────────────

    [Fact]
    public void NewFact_InLaterCycle_Returned()
    {
        CollectorDeltaTracker tracker = new();
        Fact existing = F("Device[r1].Speed", 1000L);
        Fact newFact = F("Device[r1].Mtu", 1500L);

        tracker.FilterChanged(
            new[]
            {
                existing,
            }
        );

        IReadOnlyList<Fact> changed = tracker.FilterChanged(
            new[]
            {
                existing,
                newFact,
            }
        );

        Assert.Single(changed);
        Assert.Equal(newFact.Id, changed[0].Id);
    }

    // ── Rollback ──────────────────────────────────────────────────────────────

    [Fact]
    public void Rollback_AfterFilterChanged_ResetsState()
    {
        CollectorDeltaTracker tracker = new();
        string id = "Device[r1].Speed";

        tracker.FilterChanged(
            new[]
            {
                F(id, 1000L),
            }
        ); // first call: established state
        tracker.FilterChanged(
            new[]
            {
                F(id, 2000L),
            }
        ); // sends 2000 — "in flight"
        tracker.Rollback(); // send failed — revert

        // Tracker should report 2000 as a change again on retry
        IReadOnlyList<Fact> changed = tracker.FilterChanged(
            new[]
            {
                F(id, 2000L),
            }
        );
        Assert.Single(changed);
    }

    [Fact]
    public void Rollback_WithoutPriorFilterChanged_IsNoOp()
    {
        CollectorDeltaTracker tracker = new();
        tracker.Rollback(); // should not throw
    }

    // ── TrackedCount ──────────────────────────────────────────────────────────

    [Fact]
    public void TrackedCount_IncreasesAsNewFactsSeen()
    {
        CollectorDeltaTracker tracker = new();
        Assert.Equal(0, tracker.TrackedCount);

        tracker.FilterChanged(
            new[]
            {
                F("Device[r1].Speed", 1000L),
            }
        );
        Assert.Equal(1, tracker.TrackedCount);

        tracker.FilterChanged(
            new[]
            {
                F("Device[r1].Speed", 1000L),
                F("Device[r1].Mtu", 1500L),
            }
        );
        Assert.Equal(2, tracker.TrackedCount);
    }

    // ── Different value types treated distinctly ──────────────────────────────

    [Fact]
    public void StringAndLongSameValue_TreatedAsSeparateFacts()
    {
        CollectorDeltaTracker tracker = new();
        Fact string1 = Fact.Create("Device[r1].A", "hello", DateTimeOffset.UtcNow);
        Fact string2 = Fact.Create("Device[r1].B", 42L, DateTimeOffset.UtcNow);

        tracker.FilterChanged(
            new[]
            {
                string1,
                string2,
            }
        );
        IReadOnlyList<Fact> changed = tracker.FilterChanged(
            new[]
            {
                string1,
                string2,
            }
        );

        Assert.Empty(changed); // both unchanged
    }

    // ── Empty input ───────────────────────────────────────────────────────────

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        CollectorDeltaTracker tracker = new();
        Assert.Empty(tracker.FilterChanged(Array.Empty<Fact>()));
    }

    // ── Persistence (performance-06) ──────────────────────────────────────────
    // The whole point of persistence is that a *different* CollectorDeltaTracker instance —
    // standing in for the tracker re-created after an agent restart — must agree with the
    // saved state on whether a value changed. This is also what catches a hash that isn't
    // stable across instances/processes (the original bug: object.GetHashCode()/HashCode is
    // seeded per-process and would make every fact look "changed" after every restart).

    private sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"tracker-test-{Guid.NewGuid():N}.json"
        );

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }

    [Fact]
    public void LoadOrCreate_MissingFile_ReturnsEmptyTracker()
    {
        using TempFile temp = new();
        CollectorDeltaTracker tracker = CollectorDeltaTracker.LoadOrCreate(temp.Path);
        Assert.Equal(0, tracker.TrackedCount);
    }

    [Fact]
    public void LoadOrCreate_CorruptFile_FallsBackToEmptyTracker()
    {
        using TempFile temp = new();
        File.WriteAllText(temp.Path, "{ not valid json ][");

        CollectorDeltaTracker tracker = CollectorDeltaTracker.LoadOrCreate(temp.Path);

        Assert.Equal(0, tracker.TrackedCount);
    }

    [Fact]
    public void SaveThenLoad_NewInstance_TreatsUnchangedValueAsUnchanged()
    {
        using TempFile temp = new();
        Fact fact = F("Device[r1].Speed", 1000L);

        CollectorDeltaTracker original = new();
        original.FilterChanged(new[] { fact });
        original.Save(temp.Path);

        CollectorDeltaTracker restored = CollectorDeltaTracker.LoadOrCreate(temp.Path);
        IReadOnlyList<Fact> changed = restored.FilterChanged(new[] { fact });

        Assert.Empty(changed);
    }

    [Fact]
    public void SaveThenLoad_NewInstance_TreatsChangedValueAsChanged()
    {
        using TempFile temp = new();
        string id = "Device[r1].Speed";

        CollectorDeltaTracker original = new();
        original.FilterChanged(new[] { F(id, 1000L) });
        original.Save(temp.Path);

        CollectorDeltaTracker restored = CollectorDeltaTracker.LoadOrCreate(temp.Path);
        IReadOnlyList<Fact> changed = restored.FilterChanged(new[] { F(id, 2000L) });

        Assert.Single(changed);
    }

    [Fact]
    public void SaveThenLoad_PreservesTrackedCount()
    {
        using TempFile temp = new();
        CollectorDeltaTracker original = new();
        original.FilterChanged(
            new[]
            {
                F("Device[r1].Speed", 1000L),
                F("Device[r1].Hostname", "router-1"),
            }
        );
        original.Save(temp.Path);

        CollectorDeltaTracker restored = CollectorDeltaTracker.LoadOrCreate(temp.Path);

        Assert.Equal(2, restored.TrackedCount);
    }

    [Fact]
    public void SaveThenLoad_DifferentValueKindsSameId_TreatedDistinctly()
    {
        using TempFile temp = new();
        string id = "Device[r1].Value";

        CollectorDeltaTracker original = new();
        original.FilterChanged(new[] { Fact.Create(id, "1000", DateTimeOffset.UtcNow) });
        original.Save(temp.Path);

        CollectorDeltaTracker restored = CollectorDeltaTracker.LoadOrCreate(temp.Path);
        // Same textual value, different FactValueKind (Long vs String) — must not collide.
        IReadOnlyList<Fact> changed = restored.FilterChanged(new[] { Fact.Create(id, 1000L, DateTimeOffset.UtcNow) });

        Assert.Single(changed);
    }

    [Fact]
    public void Save_OverwritesExistingFile()
    {
        using TempFile temp = new();
        string id = "Device[r1].Speed";

        CollectorDeltaTracker first = new();
        first.FilterChanged(new[] { F(id, 1000L) });
        first.Save(temp.Path);

        CollectorDeltaTracker second = new();
        second.FilterChanged(new[] { F(id, 2000L) });
        second.Save(temp.Path);

        CollectorDeltaTracker restored = CollectorDeltaTracker.LoadOrCreate(temp.Path);
        Assert.Empty(restored.FilterChanged(new[] { F(id, 2000L) }));
    }
}