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

/// <summary>DPI values a sensor accepts: either a fixed list or a range with a step.</summary>
public record DpiCapabilities(IReadOnlyList<int> Values, int? RangeMin, int? RangeMax, int? RangeStep)
{
    public bool IsRange => RangeStep.HasValue;

    public bool Accepts(int dpi) => IsRange
        ? dpi >= RangeMin && dpi <= RangeMax && (dpi - RangeMin!.Value) % RangeStep!.Value == 0
        : Values.Contains(dpi);

    public override string ToString() => IsRange
        ? $"{RangeMin}-{RangeMax} in steps of {RangeStep}"
        : string.Join(", ", Values);
}

public record DpiInfo(int SensorIndex, int Current, int Default, DpiCapabilities Capabilities);

public record FeatureInfo(byte Index, ushort Id, byte Type, byte Version)
{
    public string Name => Hidpp.FeatureId.NameOf(Id);
    public bool IsHidden => (Type & 0x40) != 0;
    public bool IsEngineering => (Type & 0x20) != 0;
}
