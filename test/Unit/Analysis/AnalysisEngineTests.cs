using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;
using JMW.Discovery.Core.Analysis.Normalizers;

namespace JMW.Discovery.Tests;

public sealed class AnalysisEngineTests
{
    private static readonly DateTimeOffset T = new(2026, 6, 4, 12, 0, 0, TimeSpan.Zero);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Fact F(string id, long value) => Fact.Create(id, value, T);
    private static Fact F(string id, string value) => Fact.Create(id, value, T);
    private static Fact F(string id, bool value) => Fact.Create(id, value, T);

    // ── Normalization ─────────────────────────────────────────────────────────

    [Fact]
    public void Normalize_MatchingPattern_TransformsValue()
    {
        // Normalizer converts speed strings like "1Gbps" to bps longs
        AnalysisEngine engine = new(
            [new SpeedNormalizer()],
            []
        );

        IReadOnlyList<Fact> results = engine.Analyze([F("Device[r1].Interface[eth0].Speed", "1Gbps")]);

        Fact speed = results.Single(f => f.AttributePath == "Device[].Interface[].Speed");
        Assert.Equal(1_000_000_000L, speed.Value.AsLong());
    }

    [Fact]
    public void Normalize_NoMatchingPattern_PassesThrough()
    {
        AnalysisEngine engine = new([new SpeedNormalizer()], []);

        Fact fact = F("Device[r1].Hostname", "router-1");
        IReadOnlyList<Fact> results = engine.Analyze([fact]);

        Assert.Equal("router-1", results.Single().Value.AsString());
    }

    // ── Hydration (derivation over full current state, §11) ─────────────────────

    // A priority fan-in: return the first present input in priority order. This is the shape
    // (DeviceVendorDerivation and siblings) that batch-locality clobbers.
    private sealed class PriorityFanInDerivation : IDerivation
    {
        public IReadOnlyList<string> Inputs { get; } = ["Device[].HiVendor", "Device[].LoVendor"];
        public IReadOnlyList<string> Outputs { get; } = ["Device[].VendorCanonical"];
        public IReadOnlyList<string> Scope => ["Device"];

        public IReadOnlyList<Fact> Derive(IReadOnlyList<Fact> scopedFacts)
        {
            foreach (string path in Inputs)
            {
                Fact? match = scopedFacts.FirstOrDefault(f => f.AttributePath == path);
                if (match is { } fact && fact.Value.AsString() is { Length: > 0 } v)
                {
                    return [Fact.Create(AnalysisEngine.BuildId("Device[].VendorCanonical", fact), v, fact.CollectedAt)];
                }
            }

            return [];
        }
    }

    [Fact]
    public void Analyze_BatchOnly_LowPriorityAlone_ProducesLowPriority_TheBugWithoutHydration()
    {
        // Baseline documenting the batch-locality failure: with only the low-priority source in the
        // batch (delta-tracking omitted the unchanged high-priority one), the fan-in falls through
        // to the low-priority value — which last-write-wins would then clobber the stored canonical.
        AnalysisEngine engine = new([], [new PriorityFanInDerivation()]);

        IReadOnlyList<Fact> result = engine.Analyze([F("Device[r1].LoVendor", "LowGuess")]);

        Assert.Equal("LowGuess", result.Single(f => f.AttributePath == "Device[].VendorCanonical").Value.AsString());
    }

    [Fact]
    public void Analyze_WithHydratedHigherPriorityInput_KeepsHighPriority_NoClobber()
    {
        // The fix: the unchanged high-priority source, hydrated from current state, is visible to the
        // derivation even though only the low-priority source changed this cycle — so the canonical
        // stays the authoritative value instead of being clobbered.
        AnalysisEngine engine = new([], [new PriorityFanInDerivation()]);

        Fact loInBatch = F("Device[r1].LoVendor", "LowGuess");
        Fact hiHydrated = F("Device[r1].HiVendor", "AuthoritativeVendor");

        IReadOnlyList<Fact> result = engine.Analyze([loInBatch], [hiHydrated]);

        Assert.Equal(
            "AuthoritativeVendor",
            result.Single(f => f.AttributePath == "Device[].VendorCanonical").Value.AsString()
        );
        // The hydrated input is not itself re-emitted (not newly observed) — only the batch fact
        // and the derived output flow onward.
        Assert.DoesNotContain(result, f => f.AttributePath == "Device[].HiVendor");
        Assert.Contains(result, f => f.AttributePath == "Device[].LoVendor");
    }

    [Fact]
    public void Analyze_BatchInputWinsOverHydratedSameId()
    {
        // When the batch carries a fresh value for a hydratable path, the batch value is used, not
        // the stale hydrated one (hydration fills gaps, never overrides the current batch).
        AnalysisEngine engine = new([], [new PriorityFanInDerivation()]);

        Fact hiInBatch = F("Device[r1].HiVendor", "FreshVendor");
        Fact hiHydrated = F("Device[r1].HiVendor", "StaleVendor");

        IReadOnlyList<Fact> result = engine.Analyze([hiInBatch], [hiHydrated]);

        Assert.Equal(
            "FreshVendor",
            result.Single(f => f.AttributePath == "Device[].VendorCanonical").Value.AsString()
        );
    }

    [Fact]
    public void HydratableInputPaths_FollowThePerScopeRules()
    {
        // Pins the per-scope hydration contract (context-derivations.md §6.5):
        // Device scope = raw inputs only (outputs recomputed, never frozen — self-refining
        // derivations would feed back); Device|Discovered scope = ALL inputs, including
        // also-output guard paths (the discovered derivations are absence-guarded gap-fills,
        // and batch-wins + injected-subtraction makes hydrating a guard path safe).
        AnalysisEngine engine = AnalysisLibrary.CreateEngine();

        // Device scope: priority fan-in raw inputs are hydratable...
        Assert.Contains(FactPaths.HwSystemVendor, engine.HydratableInputPaths);
        Assert.Contains(FactPaths.DeviceVendor, engine.HydratableInputPaths);
        // ...derived outputs never are.
        Assert.DoesNotContain(FactPaths.Derived.DeviceVendorCanonical, engine.HydratableInputPaths);
        Assert.DoesNotContain(FactPaths.Derived.DeviceVendorGuess, engine.HydratableInputPaths);

        // Discovered scope: the absence-guard paths ARE hydratable despite being outputs —
        // this is what stops a delta-tracked model-only batch re-inferring over an observation.
        Assert.Contains(FactPaths.DiscoveredVendor, engine.HydratableInputPaths);
        Assert.Contains(FactPaths.DiscoveredOs, engine.HydratableInputPaths);
        Assert.Contains(FactPaths.DiscoveredModel, engine.HydratableInputPaths);

        // No other scope leaks in (per-interface/-filesystem cardinality stays excluded).
        Assert.All(
            engine.HydratableInputPaths,
            p => Assert.Contains(Fact.DeriveDimKey(p), new[] { "Device", "Device|Discovered" })
        );
    }

    // ── Vendor context ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("cisco", "Management0/0", "Mg0/0")] // Cisco: Management → Mg
    [InlineData("arista", "Management0", "Ma0")] // Arista: Management → Ma
    [InlineData(null, "Management0", "Mg0")] // no vendor: fall back to Mg
    [InlineData("juniper", "ge-0/0/0", "ge-0/0/0")] // Juniper: pass through unchanged
    [InlineData("cisco", "GigabitEthernet0/1", "Gi0/1")] // Cisco: long → short
    public void InterfaceName_VendorContextChangesCanonicalForm(
        string? vendor,
        string rawName,
        string expectedShort
    )
    {
        AnalysisEngine engine = new([new InterfaceNameNormalizer()], []);

        List<Fact> facts = new()
        {
            Fact.Create("Device[r1].Interface[eth0].Name", rawName, T),
        };
        if (vendor is not null)
        {
            facts.Add(Fact.Create("Device[r1].Vendor", vendor, T));
        }

        IReadOnlyList<Fact> analyzed = engine.Analyze(facts);
        Fact nameResult = analyzed.Single(f => f.AttributePath == FactPaths.InterfaceName);
        Assert.Equal(expectedShort, nameResult.Value.AsString());
    }

    [Fact]
    public void NormalizationContext_BuiltFromBatchBeforeTransforms()
    {
        // Vendor "Arista" (mixed case, not yet normalized) should still be
        // recognized — context uses raw values with case-insensitive comparison.
        AnalysisEngine engine = new([new InterfaceNameNormalizer()], []);

        Fact[] facts = new[]
        {
            Fact.Create("Device[r1].Vendor", "Arista Networks", T), // raw, not yet lowercased
            Fact.Create("Device[r1].Interface[eth0].Name", "Management0", T),
        };

        IReadOnlyList<Fact> analyzed = engine.Analyze(facts);
        Fact name = analyzed.Single(f => f.AttributePath == FactPaths.InterfaceName);
        Assert.Equal("Ma0", name.Value.AsString()); // Arista context recognized despite mixed case
    }

    // ── NormalizerPipeline ────────────────────────────────────────────────────

    // FactPaths.SystemHostname = "Device[].OS.Hostname"
    // A fact with that attribute_path: "Device[r1].OS.Hostname"
    private const string TestHostnameFact = "Device[r1].OS.Hostname";

    [Theory]
    [InlineData("Router-1.example.com.", "router-1.example.com")] // lowercase + strip trailing dot
    [InlineData("  HOST.LOCAL.  ", "host.local")] // trim + lowercase + strip dot
    [InlineData("device", "device")] // no-op, passes through
    [InlineData(".", null)] // just a dot → empty after strip → dropped
    [InlineData("", null)] // empty → dropped
    public void Pipeline_Hostname_AppliesStepsInOrder(string raw, string? expected)
    {
        AnalysisEngine engine = new([HostnameNormalizer.Create()], []);
        IReadOnlyList<Fact> results = engine.Analyze([F(TestHostnameFact, raw)]);

        if (expected is null)
        {
            Assert.DoesNotContain(results, f => f.AttributePath == FactPaths.SystemHostname);
        }
        else
        {
            Assert.Equal(
                expected,
                results.Single(f => f.AttributePath == FactPaths.SystemHostname).Value.AsString()
            );
        }
    }

    [Fact]
    public void Pipeline_ShortCircuitsOnNullFromAnyStep()
    {
        // Step 1: lowercase — passes "abc"
        // Step 2: RejectEmptyString — passes "abc"
        // If step 1 returned null (empty input), step 2 is never called
        string path = "Device[].TestField";
        NormalizerPipeline pipeline = new(
            patterns: [path],
            steps:
            [
                new LowercaseTrimNormalizer(), // empty string → null (short-circuits)
                new RejectEmptyString(), // never reached for empty input
            ]
        );

        AnalysisEngine engine = new([pipeline], []);
        IReadOnlyList<Fact> empty = engine.Analyze([F("Device[r1].TestField", "")]);
        IReadOnlyList<Fact> nonEmpty = engine.Analyze([F("Device[r1].TestField", "HELLO")]);

        Assert.DoesNotContain(empty, f => f.AttributePath == path);
        Assert.Equal("hello", nonEmpty.Single(f => f.AttributePath == path).Value.AsString());
    }

    [Fact]
    public void Pipeline_ExistingNormalizersWorkAsSteps()
    {
        // Any INormalizer can be a step — ClampPercentNormalizer used inline
        string path = "Device[].SomePercent";
        NormalizerPipeline pipeline = new(
            patterns: [path],
            steps:
            [
                new ClampPercentNormalizer(), // clamp to [0,100]
            ]
        );

        AnalysisEngine engine = new([pipeline], []);
        IReadOnlyList<Fact> over = engine.Analyze([Fact.Create("Device[r1].SomePercent", 150.0)]);
        IReadOnlyList<Fact> under = engine.Analyze([Fact.Create("Device[r1].SomePercent", -5.0)]);
        IReadOnlyList<Fact> valid = engine.Analyze([Fact.Create("Device[r1].SomePercent", 75.0)]);

        Assert.Equal(100.0, over.Single(f => f.AttributePath == path).Value.AsDouble());
        Assert.Equal(0.0, under.Single(f => f.AttributePath == path).Value.AsDouble());
        Assert.Equal(75.0, valid.Single(f => f.AttributePath == path).Value.AsDouble());
    }

    [Fact]
    public void Normalize_InvalidValue_DropsTheFact()
    {
        AnalysisEngine engine = new([new SpeedNormalizer()], []);

        // "unknown" can't be parsed as a speed
        IReadOnlyList<Fact> results = engine.Analyze([F("Device[r1].Interface[eth0].Speed", "unknown")]);

        Assert.Empty(results);
    }

    // ── Same-scope derivation ─────────────────────────────────────────────────
    // TotalBytes = InBytes + OutBytes, scoped to (Device, Interface)

    [Fact]
    public void Derive_SameScope_ProducesOneOutputPerScopedGroup()
    {
        AnalysisEngine engine = new([], [new TotalBytesDerivation()]);

        Fact[] facts = new[]
        {
            F("Device[r1].Interface[eth0].InBytes", 1000L),
            F("Device[r1].Interface[eth0].OutBytes", 2000L),
            F("Device[r1].Interface[eth1].InBytes", 500L),
            F("Device[r1].Interface[eth1].OutBytes", 300L),
        };

        IReadOnlyList<Fact> results = engine.Analyze(facts);

        List<Fact> totals = results
            .Where(f => f.AttributePath == FactPaths.Derived.InterfaceTotalBytes)
            .OrderBy(f => f.Id)
            .ToList();

        Assert.Equal(2, totals.Count);
        Assert.Equal(3000L, totals[0].Value.AsLong()); // eth0: 1000 + 2000
        Assert.Equal(800L, totals[1].Value.AsLong()); // eth1: 500 + 300
    }

    [Fact]
    public void Derive_SameScope_MissingOneInput_ProducesNoOutput()
    {
        AnalysisEngine engine = new([], [new TotalBytesDerivation()]);

        // eth0 has both inputs, eth1 only has InBytes
        Fact[] facts = new[]
        {
            F("Device[r1].Interface[eth0].InBytes", 1000L),
            F("Device[r1].Interface[eth0].OutBytes", 2000L),
            F("Device[r1].Interface[eth1].InBytes", 500L), // OutBytes missing
        };

        IReadOnlyList<Fact> results = engine.Analyze(facts);
        List<Fact> totals = results.Where(f => f.AttributePath == FactPaths.Derived.InterfaceTotalBytes).ToList();

        Assert.Single(totals); // only eth0
        Assert.Contains("eth0", totals[0].Id);
    }

    // ── Cross-scope derivation ────────────────────────────────────────────────
    // ActiveInterfaceCount = count(Interface.Enabled == true), scoped to Device

    [Fact]
    public void Derive_CrossScope_GroupsAcrossChildEntities()
    {
        AnalysisEngine engine = new([], [new ActiveInterfaceCountDerivation()]);

        Fact[] facts = new[]
        {
            F("Device[r1].Interface[eth0].Enabled", true),
            F("Device[r1].Interface[eth1].Enabled", false),
            F("Device[r1].Interface[eth2].Enabled", true),
            F("Device[r2].Interface[eth0].Enabled", true),
        };

        IReadOnlyList<Fact> results = engine.Analyze(facts);
        Dictionary<string, long> counts = results
            .Where(f => f.AttributePath == "Device[].ActiveInterfaceCount")
            .ToDictionary(
                f => f.ParseId().First(s => s.Name == "Device").Key!,
                f => f.Value.AsLong()!.Value
            );

        Assert.Equal(2L, counts["r1"]); // two enabled out of three
        Assert.Equal(1L, counts["r2"]);
    }

    // ── Layered derivation ────────────────────────────────────────────────────
    // Layer 1: TotalBytes = InBytes + OutBytes (interface scope)
    // Layer 2: Utilization = TotalBytes / Speed  (interface scope, consumes Layer 1 output)

    [Fact]
    public void Derive_Layered_Layer2ConsumesLayer1Output()
    {
        AnalysisEngine engine = new(
            [],
            [new TotalBytesDerivation(), new UtilizationDerivation()]
        );

        Fact[] facts = new[]
        {
            F("Device[r1].Interface[eth0].InBytes", 600L),
            F("Device[r1].Interface[eth0].OutBytes", 400L),
            F("Device[r1].Interface[eth0].Speed", 1000L), // capacity in same units
        };

        IReadOnlyList<Fact> results = engine.Analyze(facts);

        Fact total = results.Single(f => f.AttributePath == FactPaths.Derived.InterfaceTotalBytes);
        Assert.Equal(1000L, total.Value.AsLong()); // 600 + 400

        Fact util = results.Single(f => f.AttributePath == "Device[].Interface[].Utilization");
        Assert.Equal(100L, util.Value.AsLong()); // 1000 / 1000 * 100 = 100%
    }

    [Fact]
    public void Derive_Layered_CyclicDependency_ThrowsOnConstruction()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new AnalysisEngine([], [new CyclicDerivationA(), new CyclicDerivationB()])
        );
    }

    // ── Scope inference ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(
        new[]
        {
            "Device[].Interface[].InBytes",
            "Device[].Interface[].OutBytes",
        },
        new[]
        {
            "Device",
            "Interface",
        }
    )] // same-depth inputs → full scope
    [InlineData(
        new[]
        {
            FactPaths.InterfaceName,
            "Device[].Vlan[].Id",
        },
        new[]
        {
            "Device",
        }
    )] // different child dims → parent scope only
    [InlineData(
        new[]
        {
            "Device[].Speed",
        },
        new[]
        {
            "Device",
        }
    )] // single pattern → its own dims
    [InlineData(
        new[]
        {
            "Device[].Vrf[].BgpNeighbor[].State",
            "Device[].Interface[].Enabled",
        },
        new[]
        {
            "Device",
        }
    )] // three-dim vs two-dim → common = Device
    public void InferScope_ReturnsCommonDimensions(string[] inputs, string[] expectedScope)
    {
        IReadOnlyList<string> scope = AnalysisEngine.InferScope(inputs);
        Assert.Equal(expectedScope, scope);
    }

    [Fact]
    public void InferScope_NoCommonDimensions_ReturnsEmpty()
    {
        // Device-scoped vs Network-scoped → global scope (no common dims)
        IReadOnlyList<string> scope = AnalysisEngine.InferScope(["Device[].Speed", "Network[].Origin"]);
        Assert.Empty(scope);
    }

    // ── BuildId ───────────────────────────────────────────────────────────────

    [Fact]
    public void BuildId_FillsScopeKeysFromContextFact()
    {
        Fact context = F("Device[r1].Interface[eth0].InBytes", 100L);
        string id = AnalysisEngine.BuildId(FactPaths.Derived.InterfaceTotalBytes, context);
        Assert.Equal("Device[r1].Interface[eth0].TotalBytes", id);
    }

    [Fact]
    public void BuildId_DeviceScopeTemplate_FillsOnlyDeviceKey()
    {
        Fact context = F("Device[r1].Interface[eth0].InBytes", 100L);
        string id = AnalysisEngine.BuildId("Device[].ActiveInterfaceCount", context);
        Assert.Equal("Device[r1].ActiveInterfaceCount", id);
    }
}

// ── Example normalizer ────────────────────────────────────────────────────────

/// <summary>
/// Converts speed strings ("1Gbps", "100Mbps", "10Kbps") to bps longs.
/// Returns null for values that can't be parsed (fact will be dropped).
/// </summary>
file sealed class SpeedNormalizer : INormalizer
{
    public IReadOnlyList<string> AttributePathPatterns => ["Device[].Interface[].Speed"];

    public FactValue? Normalize(FactValue raw)
    {
        string? s = raw.AsString();
        if (s is null)
        {
            return raw; // already non-string — pass through
        }

        (double? num, string suffix) = ParseSuffix(s.Trim());
        if (num is null)
        {
            return null; // can't parse — drop
        }

        long bps = suffix.ToLowerInvariant() switch
        {
            "gbps" or "gbit/s" => (long)(num.Value * 1_000_000_000),
            "mbps" or "mbit/s" => (long)(num.Value * 1_000_000),
            "kbps" or "kbit/s" => (long)(num.Value * 1_000),
            "bps" => (long)num.Value,
            "" => (long)num.Value,
            _ => -1,
        };

        return bps < 0 ? null : FactValue.FromLong(bps);
    }

    private static (double? num, string suffix) ParseSuffix(string s)
    {
        int i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.'))
        {
            i++;
        }

        if (i == 0)
        {
            return (null, "");
        }

        if (!double.TryParse(s[..i], out double num))
        {
            return (null, "");
        }

        return (num, s[i..].Trim());
    }
}

// ── Example derivations ───────────────────────────────────────────────────────

/// <summary>Interface-scoped: TotalBytes = InBytes + OutBytes.</summary>
file sealed class TotalBytesDerivation : IDerivation
{
    public IReadOnlyList<string> Inputs => ["Device[].Interface[].InBytes", "Device[].Interface[].OutBytes"];
    public IReadOnlyList<string> Outputs => [FactPaths.Derived.InterfaceTotalBytes];

    public IReadOnlyList<Fact> Derive(IReadOnlyList<Fact> scopedFacts)
    {
        Fact inBytes = scopedFacts.FirstOrDefault(f => f.AttributePath == "Device[].Interface[].InBytes");
        Fact outBytes = scopedFacts.FirstOrDefault(f => f.AttributePath == "Device[].Interface[].OutBytes");

        if (inBytes == default || outBytes == default)
        {
            return [];
        }

        if (inBytes.Value.AsLong() is not { } inVal)
        {
            return [];
        }

        if (outBytes.Value.AsLong() is not { } outVal)
        {
            return [];
        }

        string id = AnalysisEngine.BuildId(FactPaths.Derived.InterfaceTotalBytes, inBytes);
        return [Fact.Create(id, inVal + outVal, inBytes.CollectedAt)];
    }
}

/// <summary>
/// Interface-scoped: Utilization% = TotalBytes / Speed * 100.
/// Depends on TotalBytesDerivation — runs after it in topological order.
/// </summary>
file sealed class UtilizationDerivation : IDerivation
{
    public IReadOnlyList<string> Inputs => [FactPaths.Derived.InterfaceTotalBytes, "Device[].Interface[].Speed"];
    public IReadOnlyList<string> Outputs => ["Device[].Interface[].Utilization"];

    public IReadOnlyList<Fact> Derive(IReadOnlyList<Fact> scopedFacts)
    {
        Fact total = scopedFacts.FirstOrDefault(f => f.AttributePath == FactPaths.Derived.InterfaceTotalBytes);
        Fact speed = scopedFacts.FirstOrDefault(f => f.AttributePath == "Device[].Interface[].Speed");

        if (total == default || speed == default)
        {
            return [];
        }

        if (total.Value.AsLong() is not { } tVal)
        {
            return [];
        }

        if (speed.Value.AsLong() is not { } sVal || sVal == 0)
        {
            return [];
        }

        string id = AnalysisEngine.BuildId("Device[].Interface[].Utilization", total);
        return [Fact.Create(id, tVal * 100L / sVal, total.CollectedAt)];
    }
}

/// <summary>
/// Device-scoped: ActiveInterfaceCount = count of interfaces where Enabled == true.
/// Inference would give scope=[Device, Interface] (one group per interface — wrong).
/// Explicit Scope=["Device"] overrides to give one group per device, receiving all
/// its interface facts together for aggregation.
/// </summary>
file sealed class ActiveInterfaceCountDerivation : IDerivation
{
    public IReadOnlyList<string> Inputs => ["Device[].Interface[].Enabled"];
    public IReadOnlyList<string> Outputs => ["Device[].ActiveInterfaceCount"];
    public IReadOnlyList<string> Scope => ["Device"]; // explicit: aggregate to Device level

    public IReadOnlyList<Fact> Derive(IReadOnlyList<Fact> scopedFacts)
    {
        if (!scopedFacts.Any())
        {
            return [];
        }

        int count = scopedFacts.Count(f =>
            f.AttributePath == "Device[].Interface[].Enabled" && f.Value.AsBool() == true
        );

        // All facts share the same Device key — use any as the ID context
        string id = AnalysisEngine.BuildId("Device[].ActiveInterfaceCount", scopedFacts[0]);
        return [Fact.Create(id, count, scopedFacts[0].CollectedAt)];
    }
}

// ── Cycle detection test fixtures ─────────────────────────────────────────────

file sealed class CyclicDerivationA : IDerivation
{
    public IReadOnlyList<string> Inputs => ["Device[].FactB"];
    public IReadOnlyList<string> Outputs => ["Device[].FactA"];
    public IReadOnlyList<Fact> Derive(IReadOnlyList<Fact> _) => [];
}

file sealed class CyclicDerivationB : IDerivation
{
    public IReadOnlyList<string> Inputs => ["Device[].FactA"];
    public IReadOnlyList<string> Outputs => ["Device[].FactB"];
    public IReadOnlyList<Fact> Derive(IReadOnlyList<Fact> _) => [];
}