using MiniMouseHub.Core;

namespace MiniMouseHub.Tests;

public class SameDeviceTests
{
    private static DeviceState Mouse(string id, bool wired, bool awake = true, string name = "PRO X 2") =>
        new(id, "Logitech", name, DeviceKind.Mouse, wired, awake, null, null, null, null, null, null);

    [Fact]
    public void SameUnitIdIsOneMouse() =>
        Assert.True(SameDevice.Match(Mouse("cable", true), "1A2B3C4D", Mouse("receiver", false), "1a2b3c4d"));

    [Fact]
    public void DifferentUnitIdsAreTwoMice() =>
        Assert.False(SameDevice.Match(Mouse("cable", true), "1A2B3C4D", Mouse("receiver", false), "99999999"));

    [Fact]
    public void WithoutUnitIdTheCableAndReceiverMatchByName() =>
        Assert.True(SameDevice.Match(Mouse("cable", true), "1A2B3C4D", Mouse("receiver", false, awake: false), null));

    [Fact]
    public void TwoWirelessMiceOfOneModelStayApart() =>
        Assert.False(SameDevice.Match(Mouse("receiver 1", false), null, Mouse("receiver 2", false), null));

    [Fact]
    public void UnnamedDevicesDontMatch() =>
        Assert.False(SameDevice.Match(Mouse("cable", true, name: "Logitech device"), null, Mouse("receiver", false, name: "Logitech device"), null));

    [Fact]
    public void PrefersAnAwakeConnectionThenTheCable()
    {
        Assert.True(SameDevice.Rank(Mouse("cable", true)) > SameDevice.Rank(Mouse("receiver", false)));
        Assert.True(SameDevice.Rank(Mouse("receiver", false)) > SameDevice.Rank(Mouse("cable", true, awake: false)));
    }
}
