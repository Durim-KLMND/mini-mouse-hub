namespace MiniMouseHub.Core;

public enum ChargingState
{
    Unknown,
    Discharging,
    Charging,
    ChargingSlow,
    Full,
    NotCharging,
    Error,
}

/// <summary>Device types reported by HID++ feature 0x0005.</summary>
public enum DeviceKind
{
    Keyboard = 0,
    RemoteControl = 1,
    Numpad = 2,
    Mouse = 3,
    Trackpad = 4,
    Trackball = 5,
    Presenter = 6,
    Receiver = 7,
    Headset = 8,
    Webcam = 9,
    SteeringWheel = 10,
    Joystick = 11,
    Gamepad = 12,
    Dock = 13,
    Speaker = 14,
    Microphone = 15,
    Light = 16,
    Unknown = 255,
}

public enum OnboardMode
{
    Unknown = 0,
    /// <summary>The mouse uses the settings stored in its active onboard profile.</summary>
    Onboard = 1,
    /// <summary>Software controls DPI and other settings live.</summary>
    Host = 2,
}

public enum BatteryLevel
{
    Unknown,
    Critical,
    Low,
    Good,
    Full,
}

/// <param name="Percent">Charge in percent, or null when the device only reports a coarse level.</param>
/// <param name="VoltageMillivolts">Battery voltage, when the device reports it.</param>
/// <param name="Source">Which HID++ feature the reading came from.</param>
public record BatteryInfo(int? Percent, BatteryLevel Level, ChargingState Charging, int? VoltageMillivolts, string Source)
{
    public bool IsCharging => Charging is ChargingState.Charging or ChargingState.ChargingSlow;
}

/// <summary>A run of allowed DPI values from Min to Max in steps of Step. A single value has Min == Max.</summary>
public record DpiRange(int Min, int Max, int Step)
{
    public bool Contains(int dpi) => dpi >= Min && dpi <= Max && (Step == 0 ? dpi == Min : (dpi - Min) % Step == 0);

    public override string ToString() => Min == Max ? $"{Min}" : $"{Min}-{Max} in steps of {Step}";
}

/// <summary>DPI values a sensor accepts.</summary>
public record DpiCapabilities(IReadOnlyList<DpiRange> Ranges)
{
    public bool Accepts(int dpi) => Ranges.Any(r => r.Contains(dpi));

    public int Min => Ranges.Min(r => r.Min);
    public int Max => Ranges.Max(r => r.Max);

    public override string ToString() => Ranges.Count > 3
        ? $"{Min}-{Max}, step size grows with DPI"
        : string.Join(", ", Ranges);

    /// <summary>
    /// Parses the DPI list format shared by HID++ features 0x2201 and 0x2202: 2-byte values, where a value
    /// with the top 3 bits set (0xE000) is a step size and the value after it is the end of that range.
    /// </summary>
    public static DpiCapabilities Parse(IReadOnlyList<int> values)
    {
        // e.g. 100, E001, 200, E002, 500 means 100-200 in steps of 1, then 200-500 in steps of 2.
        var ranges = new List<DpiRange>();
        int? pending = null;  // a plain value not yet emitted
        int? lastEnd = null;  // end of the previous range, which can start the next one
        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            var start = pending ?? lastEnd;
            if ((value & 0xE000) == 0xE000 && start.HasValue && i + 1 < values.Count)
            {
                var end = values[++i];
                ranges.Add(new DpiRange(start.Value, end, value & 0x1FFF));
                lastEnd = end;
                pending = null;
            }
            else
            {
                if (pending.HasValue)
                    ranges.Add(new DpiRange(pending.Value, pending.Value, 0));
                pending = value;
                lastEnd = null;
            }
        }
        if (pending.HasValue)
            ranges.Add(new DpiRange(pending.Value, pending.Value, 0));
        return new DpiCapabilities(ranges);
    }
}

public record DpiInfo(int SensorIndex, int Current, int Default, DpiCapabilities Capabilities);

public record FeatureInfo(byte Index, ushort Id, byte Type, byte Version)
{
    public string Name => Hidpp.FeatureId.NameOf(Id);
    public bool IsHidden => (Type & 0x40) != 0;
    public bool IsEngineering => (Type & 0x20) != 0;
}

/// <summary>RGB settings that can be changed. Brightness, speed, hue and saturation run from 0 to 255; effect 0 means the lights are off.</summary>
public record LightingSettings(int Effect, int Brightness, int Speed, int Hue, int Saturation)
{
    public bool IsOff => Effect == 0;
}

/// <param name="EffectNames">The effects the keyboard offers; effect number n is EffectNames[n - 1].</param>
/// <param name="HasColor">False for single-color backlights, which only have brightness, effect and speed.</param>
public record LightingState(LightingSettings Settings, IReadOnlyList<string> EffectNames, bool HasColor = true);

/// <summary>How a device reaches the computer.</summary>
public enum ConnectionKind
{
    Unknown,
    /// <summary>Through a Lightspeed, Unifying or Bolt receiver.</summary>
    Receiver,
    Cable,
    Bluetooth,
}

/// <summary>How the scroll wheel of a mouse with SmartShift (such as the MX Master) behaves.</summary>
public enum WheelMode
{
    /// <summary>Clicks while scrolling slowly and spins freely when flicked.</summary>
    SmartShift,
    AlwaysRatchet,
    AlwaysFreeSpin,
}

/// <summary>Scroll wheel settings. A null value means the mouse doesn't have that setting.</summary>
/// <param name="SmartShiftThreshold">How hard a flick switches to free spin, from 1 (easily) to 50 (hardly ever).</param>
/// <param name="Torque">How strongly the wheel clicks, from 1 to 100, on mice that can change it.</param>
public record ScrollSettings(
    WheelMode? Mode,
    int? SmartShiftThreshold,
    int? Torque,
    bool? WheelInverted,
    bool? ThumbWheelInverted)
{
    public bool HasSmartShift => Mode != null;
}

/// <summary>A button or key that can be given another job.</summary>
/// <param name="Cid">Logitech's control ID for it.</param>
/// <param name="Targets">The other controls whose job it can take on in the mouse itself, including its own.</param>
/// <param name="MappedTo">The control whose job it does now; its own <see cref="Cid"/> when unchanged.</param>
/// <param name="CanDivert">The mouse can send its presses to the app instead, so the app can run any action.</param>
/// <param name="Action">The app action it runs, when it is sent to the app.</param>
public record ButtonControl(int Cid, string Name, IReadOnlyList<ButtonTarget> Targets, int MappedTo, bool CanDivert, string? Action);

public record ButtonTarget(int Cid, string Name);

/// <summary>Names of Logitech control IDs (HID++ feature 0x1B04).</summary>
public static class ControlNames
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [0x0050] = "Left click",
        [0x0051] = "Right click",
        [0x0052] = "Middle click",
        [0x0053] = "Back",
        [0x0054] = "Back",
        [0x0056] = "Forward",
        [0x0057] = "Forward",
        [0x0059] = "Button 6",
        [0x005A] = "Scroll left",
        [0x005B] = "Tilt left",
        [0x005C] = "Scroll right",
        [0x005D] = "Tilt right",
        [0x005E] = "Button 9",
        [0x005F] = "Button 10",
        [0x00C3] = "Gesture button",
        [0x00C4] = "SmartShift button",
        [0x00CE] = "Back",
        [0x00CF] = "Forward",
        [0x00D0] = "Gesture button",
        [0x00D7] = "Virtual gesture button",
        [0x00FD] = "DPI switch",
        [0x01A0] = "Haptic sense panel",
    };

    public static string NameOf(int cid) => Names.TryGetValue(cid, out var name) ? name : $"Control 0x{cid:X4}";
}

/// <summary>
/// Settings the app sets on a device that forgets them when it is switched off or reconnects (mice
/// without onboard profiles, such as the MX Master). The app keeps them and sets them again each
/// time the device connects. Null means "leave it as the device has it".
/// </summary>
public sealed class DeviceSettings
{
    public int? Dpi { get; set; }
    public WheelMode? WheelMode { get; set; }
    public int? SmartShiftThreshold { get; set; }
    public int? Torque { get; set; }
    public bool? WheelInverted { get; set; }
    public bool? ThumbWheelInverted { get; set; }

    /// <summary>Buttons given another button's job in the mouse: control ID to control ID.</summary>
    public Dictionary<int, int> Remaps { get; set; } = new();

    /// <summary>Buttons whose presses go to the app: control ID to app action.</summary>
    public Dictionary<int, string> Actions { get; set; } = new();
}

/// <summary>Keeps <see cref="DeviceSettings"/> between runs. Called on the device monitor thread.</summary>
public interface IDeviceSettingsStore
{
    DeviceSettings? Load(string deviceKey);
    void Save(string deviceKey, DeviceSettings settings);
}

/// <summary>What a button should do: its own job, another control's job, or an app action.</summary>
public record ButtonAssignment(int? RemapTo, string? Action)
{
    public static ButtonAssignment Default { get; } = new(null, null);
}
