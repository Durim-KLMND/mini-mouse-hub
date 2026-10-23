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

    public override string ToString() => string.Join(", ", Ranges);

    /// <summary>
    /// Parses the DPI list format shared by HID++ features 0x2201 and 0x2202: 2-byte values, where a value
    /// with the top 3 bits set (0xE000) is a step size and the value after it is the end of that range.
    /// </summary>
    public static DpiCapabilities Parse(IReadOnlyList<int> values)
    {
        var ranges = new List<DpiRange>();
        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            if ((value & 0xE000) == 0xE000 && ranges.Count > 0 && i + 1 < values.Count)
            {
                var last = ranges[^1];
                ranges[^1] = new DpiRange(last.Min, values[i + 1], value & 0x1FFF);
                i++;
            }
            else
            {
                ranges.Add(new DpiRange(value, value, 0));
            }
        }
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
