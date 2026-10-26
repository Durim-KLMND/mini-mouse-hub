using System.Text.Json;
using MiniMouseHub.Core;

namespace MiniMouseHub.Tests;

public class DeviceSettingsTests
{
    [Fact]
    public void SurvivesSavingAsJson()
    {
        var settings = new DeviceSettings
        {
            Dpi = 1600,
            WheelMode = WheelMode.SmartShift,
            SmartShiftThreshold = 12,
            WheelInverted = true,
            Remaps = { [0x0053] = 0x0056 },
            Actions = { [0x00C3] = "task-view" },
        };

        var copy = JsonSerializer.Deserialize<DeviceSettings>(JsonSerializer.Serialize(settings))!;

        Assert.Equal(1600, copy.Dpi);
        Assert.Equal(WheelMode.SmartShift, copy.WheelMode);
        Assert.Equal(12, copy.SmartShiftThreshold);
        Assert.True(copy.WheelInverted);
        Assert.Null(copy.ThumbWheelInverted);
        Assert.Equal(0x0056, copy.Remaps[0x0053]);
        Assert.Equal("task-view", copy.Actions[0x00C3]);
    }

    [Fact]
    public void NamesKnownControls()
    {
        Assert.Equal("Gesture button", ControlNames.NameOf(0x00C3));
        Assert.Equal("Control 0x1234", ControlNames.NameOf(0x1234));
    }
}
