using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis.Normalizers;
using JMW.Discovery.Server;

namespace JMW.Discovery.Tests;

public sealed class SecurityBoundsTests
{
    // ── P3: SpeedNormalizer overflow ──────────────────────────────────────────

    [Theory]
    // long.MaxValue ≈ 9.22×10¹⁸ bps. Inputs that produce > that overflow.
    [InlineData("10000000000Gbps")] // 10¹⁰ × 10⁹ = 10¹⁹ — overflows long
    [InlineData("9999999999999Mbps")] // 10¹³ × 10⁶ = 10¹⁹ — overflows long
    [InlineData("-1Gbps")] // negative
    [InlineData("0Gbps")] // zero
    public void SpeedNormalizer_PathologicalInput_ReturnsNull(string raw)
    {
        SpeedNormalizer normalizer = new();
        Assert.Null(normalizer.Normalize(FactValue.FromString(raw)));
    }

    [Theory]
    [InlineData("400Gbps", 400_000_000_000L)] // max realistic — 400G, should not overflow
    [InlineData("100Gbps", 100_000_000_000L)]
    [InlineData("1Gbps", 1_000_000_000L)]
    public void SpeedNormalizer_LargeButValidInput_ReturnsCorrectBps(string raw, long expectedBps)
    {
        SpeedNormalizer normalizer = new();
        FactValue? result = normalizer.Normalize(FactValue.FromString(raw));
        Assert.Equal(expectedBps, result?.AsLong());
    }

    // ── S2a: Fact ID length cap ───────────────────────────────────────────────

    [Fact]
    public void ParsePath_IdExceedsMaxLength_Throws()
    {
        string oversized = new('A', FactSegment.MaxIdLength + 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => FactSegment.ParsePath(oversized));
    }

    [Fact]
    public void ParsePath_IdAtMaxLength_Succeeds()
    {
        // A path exactly at the limit should not throw
        string atLimit = "Device[" + new string('a', FactSegment.MaxIdLength - 10) + "].X";
        // This will be at or under the limit — just verify no exception
        // (may be truncated at MaxIdLength, which is the point)
        Exception? ex = Record.Exception(() => FactSegment.ParsePath(atLimit));
        Assert.Null(ex);
    }

    [Fact]
    public void FactCreate_IdExceedsMaxLength_Throws()
    {
        // Fact.Create calls ParsePath, so the check fires here
        string oversized = "Device[" + new string('a', FactSegment.MaxIdLength) + "].Speed";
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Fact.Create(oversized, 1000L)
        );
    }

    // ── S2b: Segment count cap ────────────────────────────────────────────────

    [Fact]
    public void ParsePath_ExceedsMaxSegments_Throws()
    {
        // Build a path with MaxSegments + 1 segments
        string deep = string.Join(
            ".",
            Enumerable.Range(0, FactSegment.MaxSegments + 1)
                .Select(i => $"Seg{i}")
        );
        Assert.Throws<ArgumentOutOfRangeException>(() => FactSegment.ParsePath(deep));
    }

    [Fact]
    public void ParsePath_AtMaxSegments_Succeeds()
    {
        string atLimit = string.Join(
            ".",
            Enumerable.Range(0, FactSegment.MaxSegments)
                .Select(i => $"Seg{i}")
        );
        FactSegment[] segs = FactSegment.ParsePath(atLimit);
        Assert.Equal(FactSegment.MaxSegments, segs.Length);
    }

    // ── S2c: Batch size cap ───────────────────────────────────────────────────

    // Note: FactIngestPipeline requires a real NpgsqlDataSource to construct,
    // so we test the constant and the guard logic shape rather than wiring up
    // a full pipeline in unit tests. Integration tests cover the full path.

    [Fact]
    public void MaxFactsPerBatch_IsReasonable()
    {
        // Sanity-check that the constant is in a sensible range.
        // A normal device batch is ~1 000–5 000 facts.
        // Route tables can be 100K+ but should use a separate path.
        const int limit = FactIngestPipeline.MaxFactsPerBatch;
        Assert.InRange(limit, 10_000, 200_000);
    }
}