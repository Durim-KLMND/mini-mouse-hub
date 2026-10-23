using MiniMouseHub.Core;

namespace MiniMouseHub.Tests;

public class LowBatteryAlertTests
{
    private static BatteryInfo At(int percent, ChargingState charging = ChargingState.Discharging) =>
        new(percent, BatteryLevel.Unknown, charging, null, "test");

    [Fact]
    public void WarnsOncePerThresholdAsChargeDrops()
    {
        var alert = new LowBatteryAlert(20, 10);

        Assert.Null(alert.Check(At(50)));
        Assert.Equal(20, alert.Check(At(20)));
        Assert.Null(alert.Check(At(19)));
        Assert.Null(alert.Check(At(15)));
        Assert.Equal(10, alert.Check(At(9)));
        Assert.Null(alert.Check(At(5)));
    }

    [Fact]
    public void JumpingStraightBelowBothThresholdsWarnsForTheLowerOne()
    {
        var alert = new LowBatteryAlert(20, 10);
        Assert.Equal(10, alert.Check(At(8)));
        Assert.Null(alert.Check(At(7)));
    }

    [Fact]
    public void ChargingResetsTheWarnings()
    {
        var alert = new LowBatteryAlert(20, 10);
        Assert.Equal(20, alert.Check(At(18)));
        Assert.Null(alert.Check(At(30, ChargingState.Charging)));
        Assert.Equal(20, alert.Check(At(19)));
    }
}
