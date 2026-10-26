namespace MiniMouseHub.Core;

/// <summary>When a battery was first seen at a level.</summary>
public record BatterySample(DateTimeOffset Time, int Percent);

/// <summary>
/// What the app remembers about one device's battery to estimate how long it lasts: the levels seen
/// since it was last charged, and how fast it ran down before that charge.
/// </summary>
public sealed class BatteryHistory
{
    public List<BatterySample> Samples { get; set; } = new();

    /// <summary>Percent used per hour between the previous two charges, if that was long enough to tell.</summary>
    public double? LastRatePerHour { get; set; }
}

/// <summary>How long a battery should last at the rate it has been running down.</summary>
/// <param name="FromLastCharge">True when there isn't enough data since this charge, so the rate comes from the one before.</param>
public record BatteryEstimate(TimeSpan Left, bool FromLastCharge)
{
    /// <summary>For example "About 3 days left".</summary>
    public string Describe() => Left.TotalHours switch
    {
        < 1 => "Less than an hour left",
        < 1.5 => "About an hour left",
        < 36 => $"About {Math.Round(Left.TotalHours)} hours left",
        _ => $"About {Math.Round(Left.TotalDays)} days left",
    };
}

/// <summary>
/// Estimates battery time left. Devices only report a percentage, so this watches it drop over real
/// time (asleep and switched off included, since the battery runs down then too) and divides what's
/// left by that rate.
/// </summary>
public static class BatteryEstimator
{
    /// <summary>A rate needs at least this many percent used over at least this long, so one step doesn't decide it.</summary>
    private const int MinDrop = 3;
    private static readonly TimeSpan MinSpan = TimeSpan.FromHours(1);

    /// <summary>A rise this big means it was charged while the app wasn't watching. Smaller rises are noise.</summary>
    private const int ChargedRise = 5;

    private const int MaxSamples = 200;
    private static readonly TimeSpan MaxLeft = TimeSpan.FromDays(60);

    /// <summary>Adds a battery reading. Returns true when the history changed and should be saved.</summary>
    public static bool Record(BatteryHistory history, BatteryInfo battery, DateTimeOffset now)
    {
        if (battery.Percent is not { } percent)
            return false;

        var samples = history.Samples;
        if (battery.IsCharging || battery.Charging == ChargingState.Full)
        {
            if (samples.Count == 0) return false;
            FinishCharge(history, now);
            return true;
        }

        if (samples.Count == 0)
        {
            samples.Add(new BatterySample(now, percent));
            return true;
        }

        var last = samples[^1];
        if (percent >= last.Percent + ChargedRise)
        {
            // We don't know when it was charged, so judge the old charge only by the levels seen.
            FinishCharge(history, last.Time);
            history.Samples.Add(new BatterySample(now, percent));
            return true;
        }
        if (percent >= last.Percent)
            return false;

        samples.Add(new BatterySample(now, percent));
        if (samples.Count > MaxSamples)
            samples.RemoveRange(0, samples.Count - MaxSamples);
        return true;
    }

    /// <summary>The time left, or null while charging or before there's enough to go on.</summary>
    public static BatteryEstimate? Estimate(BatteryHistory history, BatteryInfo battery, DateTimeOffset now)
    {
        if (battery.Percent is not { } percent || battery.IsCharging || battery.Charging == ChargingState.Full)
            return null;

        var fromLastCharge = false;
        var rate = Rate(history.Samples, now);
        if (rate == null)
        {
            rate = history.LastRatePerHour;
            fromLastCharge = true;
        }
        if (rate is not > 0)
            return null;

        var left = TimeSpan.FromHours(percent / rate.Value);
        return new BatteryEstimate(left > MaxLeft ? MaxLeft : left, fromLastCharge);
    }

    /// <summary>Percent per hour from the first level seen to the latest, or null if that's too little to tell.</summary>
    private static double? Rate(IReadOnlyList<BatterySample> samples, DateTimeOffset now)
    {
        if (samples.Count < 2) return null;
        var first = samples[0];
        var last = samples[^1];
        var drop = first.Percent - last.Percent;
        var hours = (last.Time - first.Time).TotalHours;
        if (drop < MinDrop || hours < MinSpan.TotalHours) return null;

        // The next step down hasn't come yet, so if it's overdue the battery is lasting longer than
        // the steps so far suggest.
        var untilNow = (now - first.Time).TotalHours;
        return Math.Min(drop / hours, (drop + 1) / untilNow);
    }

    /// <summary>Keeps the rate from the charge that just ended, and starts a new one.</summary>
    private static void FinishCharge(BatteryHistory history, DateTimeOffset endedAt)
    {
        if (Rate(history.Samples, endedAt) is { } rate)
            history.LastRatePerHour = rate;
        history.Samples.Clear();
    }
}
