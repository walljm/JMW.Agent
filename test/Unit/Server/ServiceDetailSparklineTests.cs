using JMW.Discovery.Server.Pages.Reports;

namespace JMW.Discovery.Tests.Server;

/// <summary>
/// Guards <see cref="ServiceDetailModel.BuildSparklinePath" />'s step-chart shape — each value
/// must hold steady until the next point (matching facts_history's dedup-on-write semantics),
/// not interpolate smoothly, and the last known value must hold out to "now" rather than stopping
/// at its own timestamp.
/// </summary>
public sealed class ServiceDetailSparklineTests
{
    [Fact]
    public void Empty_ReturnsNull()
    {
        string? path = ServiceDetailModel.BuildSparklinePath([], 280, 48);

        Assert.Null(path);
    }

    [Fact]
    public void SinglePoint_HoldsFlatFromItsTimeToNow()
    {
        List<(double Value, DateTime CollectedAt)> points = [(42.0, DateTime.UtcNow.AddDays(-10))];

        string? path = ServiceDetailModel.BuildSparklinePath(points, 280, 48);

        Assert.NotNull(path);
        Assert.StartsWith("M ", path);
        // One "L" command: the flat hold from the single point out to "now" — same Y both ends.
        Assert.Equal(1, path.Split(" L ").Length - 1);
        string[] moveParts = path.Split(' ');
        string[] lineParts = path[(path.IndexOf(" L ", StringComparison.Ordinal) + 3)..].Split(' ');
        Assert.Equal(moveParts[2], lineParts[1]); // same Y coordinate at both ends
    }

    [Fact]
    public void MultiplePoints_StepsAtEachChange_ThenHoldsToNow()
    {
        DateTime now = DateTime.UtcNow;
        List<(double Value, DateTime CollectedAt)> points =
        [
            (10.0, now.AddDays(-20)),
            (50.0, now.AddDays(-10)),
            (5.0, now.AddDays(-2)),
        ];

        string? path = ServiceDetailModel.BuildSparklinePath(points, 280, 48);

        Assert.NotNull(path);
        // 2 changes x 2 "L" segments (horizontal hold + vertical step) + 1 final hold-to-now.
        Assert.Equal(5, path.Split(" L ").Length - 1);
    }

    [Theory]
    [InlineData(-5.0, 48)] // below 0% clamps to the bottom (y = height)
    [InlineData(150.0, 0)] // above 100% clamps to the top (y = 0)
    public void OutOfRangeValue_ClampsIntoTheZeroToHundredBand(double value, double expectedY)
    {
        List<(double Value, DateTime CollectedAt)> points = [(value, DateTime.UtcNow.AddDays(-1))];

        string? path = ServiceDetailModel.BuildSparklinePath(points, 280, 48);

        Assert.NotNull(path);
        double y = double.Parse(path.Split(' ')[2]);
        Assert.Equal(expectedY, y, 1);
    }
}