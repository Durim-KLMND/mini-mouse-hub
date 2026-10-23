namespace MiniMouseHub.Core.Hidpp;

/// <summary>Report IDs and well-known values of Logitech's HID++ protocol.</summary>
public static class HidppConstants
{
    public const int LogitechVendorId = 0x046D;

    /// <summary>Vendor usage page that carries HID++ traffic.</summary>
    public const ushort HidppUsagePage = 0xFF00;

    public const byte ShortReportId = 0x10; // 7 bytes total
    public const byte LongReportId = 0x11;  // 20 bytes total
    public const int ShortReportLength = 7;
    public const int LongReportLength = 20;

    /// <summary>Device index used when talking to a device plugged in by cable.</summary>
    public const byte WiredDeviceIndex = 0xFF;

    /// <summary>Feature index byte that marks a HID++ 2.0 error response.</summary>
    public const byte Hidpp20ErrorMarker = 0xFF;

    /// <summary>Sub-ID byte that marks a HID++ 1.0 error response (sent by receivers).</summary>
    public const byte Hidpp10ErrorMarker = 0x8F;

    /// <summary>Software ID put in our requests so responses can be told apart from notifications (which use 0).</summary>
    public const byte SoftwareId = 0x0A;
}

/// <summary>HID++ 2.0 feature IDs used by this project.</summary>
public static class FeatureId
{
    public const ushort Root = 0x0000;
    public const ushort FeatureSet = 0x0001;
    public const ushort DeviceName = 0x0005;
    public const ushort BatteryStatus = 0x1000;
    public const ushort BatteryVoltage = 0x1001;
    public const ushort UnifiedBattery = 0x1004;
    public const ushort AdjustableDpi = 0x2201;
    public const ushort ExtendedAdjustableDpi = 0x2202;
    public const ushort ReportRate = 0x8060;
    public const ushort ExtendedReportRate = 0x8061;
    public const ushort ColorLedEffects = 0x8070;
    public const ushort RgbEffects = 0x8071;
    public const ushort OnboardProfiles = 0x8100;

    private static readonly Dictionary<ushort, string> Names = new()
    {
        [0x0000] = "Root",
        [0x0001] = "Feature set",
        [0x0002] = "Feature info",
        [0x0003] = "Device firmware info",
        [0x0005] = "Device name and type",
        [0x0007] = "Device friendly name",
        [0x0008] = "Keep alive",
        [0x0020] = "Config change",
        [0x0021] = "Unique random ID",
        [0x00C2] = "DFU control (signed)",
        [0x00C3] = "DFU control (secure)",
        [0x00D0] = "DFU",
        [0x1000] = "Battery status",
        [0x1001] = "Battery voltage",
        [0x1004] = "Unified battery",
        [0x1300] = "LED control",
        [0x1801] = "Manufacturing mode",
        [0x1802] = "Device reset",
        [0x1803] = "GPIO access",
        [0x1806] = "Configurable device properties",
        [0x1811] = "Production test",
        [0x1890] = "RF test",
        [0x18A1] = "LED test",
        [0x1B04] = "Special keys and mouse buttons",
        [0x1EB0] = "Tracking data",
        [0x2201] = "Adjustable DPI",
        [0x2202] = "Extended adjustable DPI",
        [0x2250] = "Analysis mode",
        [0x8060] = "Report rate",
        [0x8061] = "Extended report rate",
        [0x8070] = "Color LED effects",
        [0x8071] = "RGB effects",
        [0x8090] = "Mode status",
        [0x8100] = "Onboard profiles",
        [0x8110] = "Mouse button spy",
    };

    public static string NameOf(ushort id) => Names.TryGetValue(id, out var name) ? name : "Unknown";
}
