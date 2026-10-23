namespace MiniMouseHub.Core;

/// <summary>
/// Decides when to warn about a low battery: once per threshold as the charge drops, and again
/// only after the mouse has been charged.
/// </summary>
public sealed class LowBatteryAlert(params int[] thresholds)
{
    private readonly int[] _thresholds = thresholds.OrderDescending().ToArray();
    private int? _lastWarnedAt;

    /// <summary>Returns the threshold just crossed, or null when there's nothing new to warn about.</summary>
    public int? Check(BatteryInfo battery)
    {
        if (battery.IsCharging || battery.Charging == ChargingState.Full)
        {
            _lastWarnedAt = null;
            return null;
        }
        if (battery.Percent is not { } percent)
            return null;

        var crossed = _thresholds.Where(t => percent <= t).Cast<int?>().LastOrDefault();
        if (crossed == null || crossed >= _lastWarnedAt)
            return null;
        _lastWarnedAt = crossed;
        return crossed;
    }
}
