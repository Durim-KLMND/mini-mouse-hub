using MiniMouseHub.Core;

namespace MiniMouseHub.Tests;

public class BatteryEstimatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private static BatteryInfo At(int percent, ChargingState charging = ChargingState.Discharging) =>
        new(percent, BatteryLevel.Unknown, charging, null, "test");

    /// <summary>Records one percent per hour from 80% down to <paramref name="to"/>.</summary>
    private static BatteryHistory OnePercentPerHour(int to)
    {
        var history = new BatteryHistory();
        for (var p = 80; p >= to; p--)
            BatteryEstimator.Record(history, At(p), Start.AddHours(80 - p));
        return history;
    }

    [Fact]
    public void NoEstimateUntilEnoughHasBeenUsed()
    {
        var history = OnePercentPerHour(to: 79);
        Assert.Null(BatteryEstimator.Estimate(history, At(79), Start.AddHours(1)));
    }

    [Fact]
    public void DividesWhatIsLeftByTheRate()
    {
        var history = OnePercentPerHour(to: 70);
        var estimate = BatteryEstimator.Estimate(history, At(70), Start.AddHours(10));
        Assert.NotNull(estimate);
        Assert.Equal(70, estimate.Left.TotalHours, 1);
        Assert.False(estimate.FromLastCharge);
        Assert.Equal("About 3 days left", estimate.Describe());
    }

    [Fact]
    public void AnOverdueStepMeansItLastsLonger()
    {
        var history = OnePercentPerHour(to: 70);
        // Ten hours later it is still at 70%, so it uses at most 11% in 20 hours.
        var estimate = BatteryEstimator.Estimate(history, At(70), Start.AddHours(20))!;
        Assert.Equal(70 / (11.0 / 20), estimate.Left.TotalHours, 1);
    }

    [Fact]
    public void RepeatedOrSlightlyHigherReadingsAreIgnored()
    {
        var history = OnePercentPerHour(to: 75);
        Assert.False(BatteryEstimator.Record(history, At(75), Start.AddHours(6)));
        Assert.False(BatteryEstimator.Record(history, At(77), Start.AddHours(7)));
        Assert.Equal(6, history.Samples.Count);
    }

    [Fact]
    public void ChargingStopsTheEstimateAndKeepsTheRateForNextTime()
    {
        var history = OnePercentPerHour(to: 70);
        Assert.True(BatteryEstimator.Record(history, At(71, ChargingState.Charging), Start.AddHours(10)));
        Assert.Null(BatteryEstimator.Estimate(history, At(71, ChargingState.Charging), Start.AddHours(10)));
        Assert.Empty(history.Samples);

        Assert.True(BatteryEstimator.Record(history, At(100), Start.AddHours(12)));
        var estimate = BatteryEstimator.Estimate(history, At(100), Start.AddHours(12))!;
        Assert.True(estimate.FromLastCharge);
        Assert.Equal(100, estimate.Left.TotalHours, 1);
    }

    [Fact]
    public void ABigRiseMeansItWasChargedWhileTheAppWasClosed()
    {
        var history = OnePercentPerHour(to: 70);
        Assert.True(BatteryEstimator.Record(history, At(95), Start.AddDays(3)));
        Assert.Single(history.Samples);
        // Judged by the levels seen, not by the days the app was closed.
        Assert.Equal(1, history.LastRatePerHour!.Value, 2);
    }

    [Theory]
    [InlineData(0.5, "Less than an hour left")]
    [InlineData(1.2, "About an hour left")]
    [InlineData(7.4, "About 7 hours left")]
    [InlineData(35, "About 35 hours left")]
    [InlineData(64, "About 3 days left")]
    public void DescribesTheTimeLeft(double hours, string text) =>
        Assert.Equal(text, new BatteryEstimate(TimeSpan.FromHours(hours), false).Describe());
}
