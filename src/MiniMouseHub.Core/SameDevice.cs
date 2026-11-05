namespace MiniMouseHub.Core;

/// <summary>
/// Tells when two entries are one mouse reached two ways, such as a mouse charging over its cable
/// while it is still paired with its receiver, and which of the two to show.
/// </summary>
public static class SameDevice
{
    /// <param name="unitA">The unit ID the device reported (HID++ feature 0x0003), when known.</param>
    public static bool Match(DeviceState a, string? unitA, DeviceState b, string? unitB)
    {
        if (a.Vendor != b.Vendor)
            return false;
        if (unitA != null && unitB != null)
            return string.Equals(unitA, unitB, StringComparison.OrdinalIgnoreCase);
        // A device that hasn't been read yet (such as one paired but switched off) has no unit ID. Going by
        // its name is only safe when one of them is on a cable: two of the same wireless mouse on two
        // receivers are really two mice.
        return (a.IsWired || b.IsWired) && a.IsWired != b.IsWired
            && a.Name != "Logitech device" && string.Equals(a.Name.Trim(), b.Name.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Higher is better: a connection that answers first, then the cable over the receiver.</summary>
    public static int Rank(DeviceState device) => (device.Awake ? 2 : 0) + (device.IsWired ? 1 : 0);
}
