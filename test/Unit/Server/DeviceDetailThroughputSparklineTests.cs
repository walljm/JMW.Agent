using JMW.Discovery.Server.Pages.Reports;

namespace JMW.Discovery.Tests.Server;

/// <summary>
/// Guards <see cref="DeviceDetailModel.BuildThroughputSparklinePath" />'s counter-to-rate
/// conversion — Interface[].TotalBytes is a raw cumulative counter, not a rate, and a naive plot
/// of the raw values would just show an ever-increasing line instead of throughput.
/// </summary>
public sealed class DeviceDetailThroughputSparklineTests
{
    [Fact]
    public void FewerThanTwoPoints_ReturnsNull()
    {
        string? none = DeviceDetailModel.BuildThroughputSparklinePath([], 280, 48, out double peakNone);
        string? one = DeviceDetailModel.BuildThroughputSparklinePath(
            [new ThroughputPoint(1000, DateTime.UtcNow)], 280, 48, out double peakOne);

        Assert.Null(none);
        Assert.Equal(0, peakNone);
        Assert.Null(one);
        Assert.Equal(0, peakOne);
    }

    [Fact]
    public void TwoPoints_DerivesRateFromTheDelta()
    {
        DateTime t0 = DateTime.UtcNow.AddSeconds(-10);
        DateTime t1 = DateTime.UtcNow;
        List<ThroughputPoint> points = [new(1_000, t0), new(11_000, t1)]; // +10,000 bytes / 10s = 1,000 B/s

        string? path = DeviceDetailModel.BuildThroughputSparklinePath(points, 280, 48, out double peak);

        Assert.NotNull(path);
        Assert.Equal(1_000, peak, 1);
    }

    [Fact]
    public void CounterReset_ClampsToZeroInsteadOfGoingNegative()
    {
        DateTime t0 = DateTime.UtcNow.AddSeconds(-20);
        DateTime t1 = DateTime.UtcNow.AddSeconds(-10);
        DateTime t2 = DateTime.UtcNow;
        // Counter resets between t0 and t1 (interface restart) — the "peak" must come from the
        // t1->t2 leg, not a spurious huge value from the apparent negative delta.
        List<ThroughputPoint> points = [new(50_000, t0), new(100, t1), new(2_100, t2)];

        string? path = DeviceDetailModel.BuildThroughputSparklinePath(points, 280, 48, out double peak);

        Assert.NotNull(path);
        Assert.Equal(200, peak, 1); // (2100-100)/10s
    }

    [Fact]
    public void ZeroTimeDelta_SkipsThatSampleInsteadOfDividingByZero()
    {
        DateTime t = DateTime.UtcNow;
        List<ThroughputPoint> points = [new(1_000, t), new(2_000, t), new(3_000, t.AddSeconds(10))];

        string? path = DeviceDetailModel.BuildThroughputSparklinePath(points, 280, 48, out double peak);

        Assert.NotNull(path);
        Assert.False(double.IsNaN(peak));
        Assert.False(double.IsInfinity(peak));
    }
}