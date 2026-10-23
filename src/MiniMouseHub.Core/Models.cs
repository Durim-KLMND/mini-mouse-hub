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
