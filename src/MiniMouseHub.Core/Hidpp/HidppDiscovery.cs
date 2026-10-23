using System.Text.RegularExpressions;
using HidSharp;

namespace MiniMouseHub.Core.Hidpp;

/// <summary>Finds Logitech USB devices that speak HID++ and opens channels to them.</summary>
public static partial class HidppDiscovery
{
    private const uint ShortUsage = ((uint)HidppConstants.HidppUsagePage << 16) | 0x0001;
    private const uint LongUsage = ((uint)HidppConstants.HidppUsagePage << 16) | 0x0002;

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
            return usages.Any(u => u == ShortUsage || u == LongUsage);
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

    [GeneratedRegex("&col[0-9a-f]{2}", RegexOptions.IgnoreCase)]
    private static partial Regex CollectionSuffix();
}
