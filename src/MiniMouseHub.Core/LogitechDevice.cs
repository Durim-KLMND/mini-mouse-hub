using System.Text;
using MiniMouseHub.Core.Hidpp;

namespace MiniMouseHub.Core;

/// <summary>A HID++ 2.0 device (such as a mouse) reached through a channel at a given device index.</summary>
public sealed class LogitechDevice
{
    private readonly Dictionary<ushort, byte> _featureIndexes = new();

    public HidppChannel Channel { get; }
    public byte DeviceIndex { get; }
    public Version ProtocolVersion { get; }
    public bool IsWired => DeviceIndex == HidppConstants.WiredDeviceIndex;

    private LogitechDevice(HidppChannel channel, byte deviceIndex, Version protocolVersion)
    {
        Channel = channel;
        DeviceIndex = deviceIndex;
        ProtocolVersion = protocolVersion;
    }

    /// <summary>
    /// Finds HID++ 2.0 devices on a channel: the device itself when it's wired, or the devices paired
    /// with it when it's a receiver. Devices that are switched off or asleep don't answer and are skipped.
    /// </summary>
    public static List<LogitechDevice> Enumerate(HidppChannel channel)
    {
        var found = new List<LogitechDevice>();

        // A wired HID++ 2.0 device answers at 0xFF. A receiver answers there too, but with a HID++ 1.0 error.
        var direct = TryPing(channel, HidppConstants.WiredDeviceIndex);
        if (direct != null)
        {
            found.Add(direct);
            return found;
        }

        for (byte index = 1; index <= 6; index++)
        {
            var device = TryPing(channel, index);
            if (device != null)
                found.Add(device);
        }
        return found;
    }

    private static LogitechDevice? TryPing(HidppChannel channel, byte deviceIndex)
    {
        const byte pingData = 0x5A;
        try
        {
            // Root feature (index 0), function 1 = getProtocolVersion.
            var response = channel.Request(deviceIndex, 0x00, 1, [0, 0, pingData], timeoutMs: 700);
            if (response[2] != pingData || response[0] < 2)
                return null;
            return new LogitechDevice(channel, deviceIndex, new Version(response[0], response[1]));
        }
        catch (HidppException)
        {
            return null;
        }
    }

    /// <summary>Calls a function of a feature by its ID. Throws when the device doesn't have the feature.</summary>
    public byte[] Call(ushort featureId, byte function, params byte[] parameters)
    {
        var index = GetFeatureIndex(featureId)
            ?? throw new HidppException($"Device doesn't support feature 0x{featureId:X4} ({FeatureId.NameOf(featureId)}).");
        return Channel.Request(DeviceIndex, index, function, parameters);
    }

    public bool HasFeature(ushort featureId) => GetFeatureIndex(featureId) != null;

    public byte? GetFeatureIndex(ushort featureId)
    {
        if (featureId == FeatureId.Root)
            return 0;
        if (_featureIndexes.TryGetValue(featureId, out var cached))
            return cached == 0 ? null : cached;

        // Root function 0 = getFeature(featureId) -> [index, type, version]; index 0 means unsupported.
        var response = Channel.Request(DeviceIndex, 0x00, 0, [(byte)(featureId >> 8), (byte)featureId]);
        _featureIndexes[featureId] = response[0];
        return response[0] == 0 ? null : response[0];
    }

    public List<FeatureInfo> GetFeatures()
    {
        var count = Call(FeatureId.FeatureSet, 0)[0];
        var features = new List<FeatureInfo> { new(0, FeatureId.Root, 0, 0) };
        for (byte i = 1; i <= count; i++)
        {
            var r = Call(FeatureId.FeatureSet, 1, i);
            var feature = new FeatureInfo(i, (ushort)((r[0] << 8) | r[1]), r[2], r[3]);
            _featureIndexes[feature.Id] = i;
            features.Add(feature);
        }
        return features;
    }

    public string GetName()
    {
        var length = Call(FeatureId.DeviceName, 0)[0];
        var name = new StringBuilder();
        while (name.Length < length)
        {
            var chunk = Call(FeatureId.DeviceName, 1, (byte)name.Length);
            var take = Math.Min(chunk.Length, length - name.Length);
            name.Append(Encoding.UTF8.GetString(chunk, 0, take).TrimEnd('\0'));
            if (chunk[0] == 0) break;
        }
        return name.ToString();
    }

    public DeviceKind GetKind()
    {
        // 0x0005 function 2 = getDeviceType -> [type]
        var type = Call(FeatureId.DeviceName, 2)[0];
        return Enum.IsDefined(typeof(DeviceKind), (int)type) ? (DeviceKind)type : DeviceKind.Unknown;
    }

    // ---- Battery ----

    public BatteryInfo GetBattery()
    {
        if (HasFeature(FeatureId.UnifiedBattery))
            return ReadUnifiedBattery();
        if (HasFeature(FeatureId.BatteryVoltage))
            return ReadBatteryVoltage();
        if (HasFeature(FeatureId.BatteryStatus))
            return ReadBatteryStatus();
        throw new HidppException("Device doesn't report its battery.");
    }

    private BatteryInfo ReadUnifiedBattery()
    {
        // Function 1 = getStatus -> [stateOfCharge %, level flags, charging status, external power]
        var r = Call(FeatureId.UnifiedBattery, 1);
        var level = (r[1] & 0x08) != 0 ? BatteryLevel.Full
            : (r[1] & 0x04) != 0 ? BatteryLevel.Good
            : (r[1] & 0x02) != 0 ? BatteryLevel.Low
            : (r[1] & 0x01) != 0 ? BatteryLevel.Critical
            : BatteryLevel.Unknown;
        var charging = r[2] switch
        {
            0 => ChargingState.Discharging,
            1 => ChargingState.Charging,
            2 => ChargingState.ChargingSlow,
            3 => ChargingState.Full,
            4 => ChargingState.Error,
            _ => ChargingState.Unknown,
        };
        int? percent = r[0] is > 0 and <= 100 ? r[0] : null;
        return new BatteryInfo(percent, level, charging, null, "Unified battery (0x1004)");
    }

    private BatteryInfo ReadBatteryVoltage()
    {
        // Function 0 = getBatteryInfo -> [voltage mV (2 bytes), flags]
        var r = Call(FeatureId.BatteryVoltage, 0);
        var millivolts = (r[0] << 8) | r[1];
        var flags = r[2];
        var charging = (flags & 0x80) == 0
            ? ChargingState.Discharging
            : (flags & 0x07) switch
            {
                0 => (flags & 0x18) == 0x08 ? ChargingState.ChargingSlow : ChargingState.Charging,
                1 => ChargingState.Full,
                2 => ChargingState.NotCharging,
                _ => ChargingState.Error,
            };
        var percent = EstimatePercentFromVoltage(millivolts);
        return new BatteryInfo(percent, LevelFromPercent(percent), charging, millivolts, "Battery voltage (0x1001)");
    }

    private BatteryInfo ReadBatteryStatus()
    {
        // Function 0 = getBatteryLevelStatus -> [level %, next level %, status]
        var r = Call(FeatureId.BatteryStatus, 0);
        var charging = r[2] switch
        {
            0 => ChargingState.Discharging,
            1 or 2 => ChargingState.Charging,
            3 => ChargingState.Full,
            4 => ChargingState.ChargingSlow,
            _ => ChargingState.Error,
        };
        int? percent = r[0] is > 0 and <= 100 ? r[0] : null;
        return new BatteryInfo(percent, percent is { } p ? LevelFromPercent(p) : BatteryLevel.Unknown, charging, null, "Battery status (0x1000)");
    }

    private static readonly (int Millivolts, int Percent)[] VoltageCurve =
    [
        (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50),
        (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0),
    ];

    /// <summary>Rough charge estimate for a single-cell Li-ion battery.</summary>
    private static int EstimatePercentFromVoltage(int millivolts)
    {
        if (millivolts >= VoltageCurve[0].Millivolts) return 100;
        for (var i = 1; i < VoltageCurve.Length; i++)
        {
            var (hiV, hiP) = VoltageCurve[i - 1];
            var (loV, loP) = VoltageCurve[i];
            if (millivolts >= loV)
                return loP + (millivolts - loV) * (hiP - loP) / (hiV - loV);
        }
        return 0;
    }

    private static BatteryLevel LevelFromPercent(int percent) => percent switch
    {
        >= 90 => BatteryLevel.Full,
        >= 30 => BatteryLevel.Good,
        >= 10 => BatteryLevel.Low,
        _ => BatteryLevel.Critical,
    };

    // ---- DPI ----
    // Older mice use Adjustable DPI (0x2201); newer ones such as the PRO X Superlight 2 use
    // Extended adjustable DPI (0x2202), which adds separate X/Y values and lift-off distance.
    // Only one sensor (index 0) is used; Logitech mice have a single sensor.

    public bool HasAdjustableDpi => HasFeature(FeatureId.ExtendedAdjustableDpi) || HasFeature(FeatureId.AdjustableDpi);

    public DpiInfo GetDpi()
    {
        if (HasFeature(FeatureId.ExtendedAdjustableDpi))
        {
            var p = ReadExtendedDpiParameters();
            return new DpiInfo(0, p.X, p.DefaultX, GetDpiCapabilities());
        }

        // 0x2201 function 2 = getSensorDpi(sensor) -> [sensor, dpi (2 bytes), default dpi (2 bytes)]
        var r = Call(FeatureId.AdjustableDpi, 2, 0);
        return new DpiInfo(0, (r[1] << 8) | r[2], (r[3] << 8) | r[4], GetDpiCapabilities());
    }

    public DpiCapabilities GetDpiCapabilities()
    {
        var values = new List<int>();
        if (HasFeature(FeatureId.ExtendedAdjustableDpi))
        {
            // 0x2202 function 2 = getSensorDpiRanges(sensor, direction, page) -> [sensor, direction, page, data...].
            // The data is a byte stream that continues across pages (a value can be split between two pages)
            // and ends with a 0 value.
            var data = new List<byte>();
            for (byte page = 0; page < 16; page++)
            {
                var r = Call(FeatureId.ExtendedAdjustableDpi, 2, 0, 0, page);
                data.AddRange(r.Skip(3));
                values.Clear();
                if (ReadDpiValues(data.ToArray(), 0, values))
                    break;
            }
        }
        else
        {
            // 0x2201 function 1 = getSensorDpiList(sensor) -> [sensor, values...]
            ReadDpiValues(Call(FeatureId.AdjustableDpi, 1, 0), 1, values);
        }
        return DpiCapabilities.Parse(values);
    }

    /// <summary>Appends 2-byte values from a reply; returns true when the 0 terminator was reached.</summary>
    private static bool ReadDpiValues(byte[] reply, int offset, List<int> values)
    {
        for (var i = offset; i + 1 < reply.Length; i += 2)
        {
            var value = (reply[i] << 8) | reply[i + 1];
            if (value == 0) return true;
            values.Add(value);
        }
        return false;
    }

    /// <summary>Sets the DPI and returns the value the mouse reports back.</summary>
    public int SetDpi(int dpi)
    {
        if (HasFeature(FeatureId.ExtendedAdjustableDpi))
        {
            // Function 6 = setSensorDpiParameters(sensor, dpi X, dpi Y, lift-off distance).
            // Y is only used when the sensor supports separate X/Y; keep the current lift-off distance.
            var capabilities = Call(FeatureId.ExtendedAdjustableDpi, 1, 0);
            var separateY = (capabilities[2] & 0x01) != 0;
            var current = ReadExtendedDpiParameters();
            var y = separateY ? dpi : 0;
            Call(FeatureId.ExtendedAdjustableDpi, 6, 0, (byte)(dpi >> 8), (byte)dpi, (byte)(y >> 8), (byte)y, current.LiftOffDistance);
            return ReadExtendedDpiParameters().X;
        }

        // 0x2201 function 3 = setSensorDpi(sensor, dpi) -> echoes [sensor, dpi]
        var r = Call(FeatureId.AdjustableDpi, 3, 0, (byte)(dpi >> 8), (byte)dpi);
        return (r[1] << 8) | r[2];
    }

    private record ExtendedDpiParameters(int X, int DefaultX, int Y, int DefaultY, byte LiftOffDistance);

    private ExtendedDpiParameters ReadExtendedDpiParameters()
    {
        // Function 5 = getSensorDpiParameters(sensor) -> [sensor, X, default X, Y, default Y (2 bytes each), lift-off distance]
        var r = Call(FeatureId.ExtendedAdjustableDpi, 5, 0);
        int U16(int i) => (r[i] << 8) | r[i + 1];
        var defaultX = U16(3);
        var x = U16(1) == 0 ? defaultX : U16(1);
        return new ExtendedDpiParameters(x, defaultX, U16(5), U16(7), r[9]);
    }

    // ---- Onboard profiles ----

    /// <summary>
    /// Whether the mouse runs from its onboard profile (settings stored on the mouse) or in host mode
    /// (software sets DPI and the rest live, the way G HUB does).
    /// </summary>
    public OnboardMode GetOnboardMode()
    {
        // 0x8100 function 2 = getOnboardMode -> [mode]
        var mode = Call(FeatureId.OnboardProfiles, 2)[0];
        return Enum.IsDefined(typeof(OnboardMode), (int)mode) ? (OnboardMode)mode : OnboardMode.Unknown;
    }

    public void SetOnboardMode(OnboardMode mode)
    {
        // 0x8100 function 1 = setOnboardMode(mode)
        Call(FeatureId.OnboardProfiles, 1, (byte)mode);
    }
}
