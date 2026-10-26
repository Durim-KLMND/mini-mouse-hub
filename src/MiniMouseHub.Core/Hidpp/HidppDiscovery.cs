using System.Text.RegularExpressions;
using HidSharp;

namespace MiniMouseHub.Core.Hidpp;

/// <summary>Finds Logitech USB devices that speak HID++ and opens channels to them.</summary>
public static partial class HidppDiscovery
{
    private const uint ShortUsage = ((uint)HidppConstants.HidppUsagePage << 16) | 0x0001;
    private const uint LongUsage = ((uint)HidppConstants.HidppUsagePage << 16) | 0x0002;
    private const uint BluetoothUsage = ((uint)HidppConstants.BluetoothHidppUsagePage << 16) | HidppConstants.BluetoothHidppUsage;

    // Windows names Bluetooth LE HID devices after the HID-over-GATT service.
    private const string HidOverGattService = "00001812-0000-1000-8000-00805f9b34fb";

    /// <summary>Lists each physical HID++ device with the HID collections that belong to it.</summary>
    public static List<IReadOnlyList<HidDevice>> FindHidppDevices()
    {
        var collections = DeviceList.Local.GetHidDevices(HidppConstants.LogitechVendorId)
            .Where(IsHidppCollection)
            .ToList();

        // On Windows each HID collection has its own path ending in e.g. "&col02"; group them back together.
        return collections
            .GroupBy(d => CollectionSuffix().Replace(d.DevicePath, "").ToLowerInvariant())
            .Select(g => (IReadOnlyList<HidDevice>)g.OrderByDescending(d => d.GetMaxOutputReportLength()).ToList())
            .Where(g => g.Any(d => d.GetMaxOutputReportLength() >= HidppConstants.LongReportLength))
            .ToList();
    }

    public static List<HidppChannel> OpenAll()
    {
        var channels = new List<HidppChannel>();
        foreach (var group in FindHidppDevices())
        {
            try { channels.Add(new HidppChannel(group)); }
            catch (HidppException) { }
        }
        return channels;
    }

    private static bool IsHidppCollection(HidDevice device)
    {
        try
        {
            var usages = device.GetReportDescriptor().DeviceItems
                .SelectMany(item => item.Usages.GetAllValues());
            return usages.Any(u => u is ShortUsage or LongUsage or BluetoothUsage);
        }
        catch
        {
            // Some platforms can't read the descriptor; fall back to the report sizes HID++ uses.
            try
            {
                return device.GetMaxOutputReportLength() is HidppConstants.ShortReportLength or HidppConstants.LongReportLength;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Whether a device is connected by Bluetooth rather than through a receiver or a cable. Logitech gives
    /// Bluetooth devices product IDs from 0xB000.
    /// </summary>
    public static bool IsBluetooth(HidDevice device) =>
        device.DevicePath.Contains(HidOverGattService, StringComparison.OrdinalIgnoreCase)
        || (device.ProductID & 0xF000) == 0xB000;

    /// <summary>Every Logitech HID collection with its usages, for the diagnostic report.</summary>
    public static string DescribeAll()
    {
        var lines = new List<string>();
        foreach (var device in DeviceList.Local.GetHidDevices(HidppConstants.LogitechVendorId))
        {
            string usages;
            try
            {
                usages = string.Join(", ", device.GetReportDescriptor().DeviceItems
                    .SelectMany(item => item.Usages.GetAllValues())
                    .Distinct()
                    .Select(u => $"{u >> 16:X4}:{u & 0xFFFF:X4}"));
            }
            catch (Exception e)
            {
                usages = $"descriptor unreadable ({e.GetType().Name})";
            }
            string Try(Func<object> read) { try { return read().ToString() ?? "?"; } catch { return "?"; } }
            lines.Add($"PID {device.ProductID:X4}  in {Try(() => device.GetMaxInputReportLength())} out {Try(() => device.GetMaxOutputReportLength())}  " +
                $"HID++ {(IsHidppCollection(device) ? "yes" : "no")}  Bluetooth {(IsBluetooth(device) ? "yes" : "no")}  usages [{usages}]  " +
                $"\"{Try(device.GetProductName)}\"  {device.DevicePath}");
        }
        return lines.Count == 0 ? "No Logitech HID devices." : string.Join(Environment.NewLine, lines);
    }

    [GeneratedRegex("&col[0-9a-f]{2}", RegexOptions.IgnoreCase)]
    private static partial Regex CollectionSuffix();
}
