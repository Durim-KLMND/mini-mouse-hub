using HidSharp;

namespace MiniMouseHub.Core.Via;

/// <summary>
/// Finds keyboards running QMK with VIA (any brand), and Glorious keyboards still on their stock
/// firmware, which get a tile that explains how to unlock their settings.
/// </summary>
internal sealed class ViaProvider(IDeviceHost host) : DeviceProvider(host)
{
    private const uint RawHidUsage = 0xFF60_0061;
    private const int GloriousVendorId = 0x320F;

    private readonly List<ViaDevice> _devices = new();

    public override IEnumerable<ManagedDevice> Devices => _devices;

    public override void Refresh()
    {
        Scan();
        foreach (var device in _devices.ToList())
            Try(device, () => device.Read());
    }

    public override void Poll()
    {
        // Lighting can change from the keyboard's own Fn keys, which VIA doesn't report.
        foreach (var device in _devices.ToList())
            Try(device, () => device.Read());
    }

    public override void Scan()
    {
        var hid = DeviceList.Local.GetHidDevices().Where(d => d.VendorID != Hidpp.HidppConstants.LogitechVendorId).ToList();
        var via = hid.Where(IsViaInterface).ToList();

        var found = new List<(string Id, HidDevice? Interface, HidDevice Any)>();
        foreach (var device in via)
            found.Add(($"via:{device.DevicePath}", device, device));
        foreach (var stock in hid.Where(d => d.VendorID == GloriousVendorId).GroupBy(d => (d.VendorID, d.ProductID)))
        {
            if (via.Any(v => (v.VendorID, v.ProductID) == stock.Key))
                continue;
            found.Add(($"glorious:{stock.Key.VendorID:X4}:{stock.Key.ProductID:X4}", null, stock.First()));
        }

        var changed = _devices.RemoveAll(d =>
        {
            if (found.Any(f => f.Id == d.State.Id)) return false;
            d.Dispose();
            return true;
        }) > 0;

        foreach (var (id, iface, any) in found)
        {
            if (_devices.Any(d => d.State.Id == id))
                continue;
            var device = iface != null ? ViaDevice.Open(this, id, iface) : ViaDevice.Stock(this, id, any);
            if (device == null)
                continue;
            _devices.Add(device);
            changed = true;
            Try(device, () => device.Read());
        }
        if (changed)
            Host.Publish();
    }

    private static bool IsViaInterface(HidDevice device)
    {
        try
        {
            // Cheap check first: raw HID uses 32-byte reports (33 with the report ID).
            if (device.GetMaxInputReportLength() != ViaKeyboard.ReportLength + 1)
                return false;
            return device.GetReportDescriptor().DeviceItems
                .SelectMany(item => item.Usages.GetAllValues())
                .Contains(RawHidUsage);
        }
        catch
        {
            return false;
        }
    }

    private void Try(ViaDevice device, Action action)
    {
        try
        {
            action();
        }
        catch (ViaException)
        {
        }
        catch (Exception)
        {
            // Unplugged or the handle broke: drop it, and the next scan finds it again.
            device.Dispose();
            _devices.Remove(device);
            Host.Publish();
        }
    }

    internal void Changed(ViaDevice device, DeviceState state) => Update(device, state);

    public override void Dispose()
    {
        foreach (var device in _devices)
            device.Dispose();
    }
}

internal sealed class ViaDevice : ManagedDevice, IDisposable
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(1);

    private readonly ViaProvider _provider;
    private readonly HidStream? _stream;
    private readonly ViaKeyboard? _keyboard;
    private readonly IReadOnlyList<string> _effectNames;
    private int? _protocol;

    private ViaDevice(ViaProvider provider, DeviceState state, HidStream? stream, IReadOnlyList<string> effectNames) : base(state)
    {
        _provider = provider;
        _stream = stream;
        _effectNames = effectNames;
        if (stream != null)
            _keyboard = new ViaKeyboard(Exchange);
    }

    public static ViaDevice? Open(ViaProvider provider, string id, HidDevice device)
    {
        if (!device.TryOpen(out HidStream stream))
            return null;
        stream.ReadTimeout = (int)ReplyTimeout.TotalMilliseconds;
        var (vendor, name) = Names(device);
        return new ViaDevice(provider, NewState(id, vendor, name, null), stream, ViaEffects.For(device.VendorID, device.ProductID));
    }

    /// <summary>A keyboard we recognise but can't talk to yet.</summary>
    public static ViaDevice Stock(ViaProvider provider, string id, HidDevice device)
    {
        var (vendor, name) = Names(device);
        return new ViaDevice(provider, NewState(id, vendor, name,
            "This keyboard runs Glorious's stock firmware. Flash QMK with VIA to change its lighting and keys here."), null, []);
    }

    private static DeviceState NewState(string id, string vendor, string name, string? note) =>
        new(id, vendor, name, DeviceKind.Keyboard, IsWired: true, Awake: true, null, null, null, null, null, null, Note: note);

    private static (string Vendor, string Name) Names(HidDevice device)
    {
        string? vendor = null, name = null;
        try { vendor = device.GetManufacturer(); } catch { }
        try { name = device.GetProductName(); } catch { }
        if (device.VendorID == 0x320F)
        {
            vendor = "Glorious";
            if (device.ProductID == 0x5044)
                name = "GMMK Pro"; // stock firmware and QMK report it differently
        }
        return (string.IsNullOrWhiteSpace(vendor) ? "Keyboard" : vendor.Trim(),
                string.IsNullOrWhiteSpace(name) ? $"Keyboard {device.VendorID:X4}:{device.ProductID:X4}" : name.Trim());
    }

    /// <summary>Re-reads the lighting.</summary>
    public void Read()
    {
        if (_keyboard == null) return;
        _protocol ??= _keyboard.GetProtocolVersion();
        if (_protocol < ViaKeyboard.MinLightingProtocol)
        {
            _provider.Changed(this, State with { Note = $"This keyboard's VIA firmware is too old (protocol {_protocol}). Update it to change lighting here." });
            return;
        }

        LightingState? lighting;
        try { lighting = new LightingState(_keyboard.GetLighting(), _effectNames); }
        catch (ViaException) { lighting = null; } // no RGB matrix on this keyboard
        _provider.Changed(this, State with { Lighting = lighting });
    }

    public override void SetLighting(LightingSettings lighting)
    {
        if (_keyboard == null || State.Lighting == null)
            base.SetLighting(lighting);
        _keyboard!.SetLighting(lighting);
        Read();
    }

    private byte[] Exchange(byte[] report)
    {
        var stream = _stream!;
        var buffer = new byte[ViaKeyboard.ReportLength + 1]; // byte 0 is the report ID, which raw HID doesn't use
        report.CopyTo(buffer, 1);
        stream.Write(buffer);

        var deadline = DateTime.UtcNow + ReplyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var reply = new byte[buffer.Length];
            int read;
            try { read = stream.Read(reply, 0, reply.Length); }
            catch (TimeoutException) { break; }
            if (read < buffer.Length) continue;
            // Skip anything left over from an earlier command.
            if (reply[1] == report[0] || reply[1] == 0xFF)
                return reply[1..];
        }
        throw new ViaException($"The keyboard didn't answer VIA command 0x{report[0]:X2}.");
    }

    public void Dispose() => _stream?.Dispose();
}
