using HidSharp;

namespace MiniMouseHub.Core.Via;

/// <summary>
/// Finds keyboards running QMK with VIA (any brand, including most Keychron boards), and Glorious
/// and Keychron keyboards the app can't talk to, which get a tile that explains why.
/// </summary>
internal sealed class ViaProvider(IDeviceHost host) : DeviceProvider(host)
{
    private const uint RawHidUsage = 0xFF60_0061;
    private const int GloriousVendorId = 0x320F;
    private const uint KeyboardUsage = 0x0001_0006;
    private const uint MouseUsage = 0x0001_0002;

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
        var stocks = hid.Where(d => d.VendorID is GloriousVendorId or ViaEffects.KeychronVendorId).GroupBy(d => (d.VendorID, d.ProductID));
        foreach (var stock in stocks)
        {
            if (via.Any(v => (v.VendorID, v.ProductID) == stock.Key))
                continue;
            // Keychron also makes mice and receivers; only keyboards get a tile.
            if (stock.Key.VendorID == ViaEffects.KeychronVendorId && !IsKeyboard(stock))
                continue;
            var brand = stock.Key.VendorID == GloriousVendorId ? "glorious" : "keychron";
            found.Add(($"{brand}:{stock.Key.VendorID:X4}:{stock.Key.ProductID:X4}", null, stock.First()));
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

    private static bool IsKeyboard(IEnumerable<HidDevice> interfaces)
    {
        var usages = interfaces.SelectMany(d =>
        {
            try { return d.GetReportDescriptor().DeviceItems.SelectMany(item => item.Usages.GetAllValues()); }
            catch { return []; }
        }).ToHashSet();
        return usages.Contains(KeyboardUsage) && !usages.Contains(MouseUsage);
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
    private readonly int _vendorId;
    private readonly int _productId;
    private IReadOnlyList<string> _effectNames;
    private int? _protocol;
    private bool _lightingChecked;
    private LightingChannel? _channel;
    private bool _effectsCounted;
    private bool _keymapChecked;
    private KeymapState? _keymap;

    private ViaDevice(ViaProvider provider, DeviceState state, HidStream? stream, HidDevice device) : base(state)
    {
        _provider = provider;
        _stream = stream;
        _vendorId = device.VendorID;
        _productId = device.ProductID;
        _effectNames = ViaEffects.For(_vendorId, _productId);
        if (stream != null)
            _keyboard = new ViaKeyboard(Exchange);
    }

    public static ViaDevice? Open(ViaProvider provider, string id, HidDevice device)
    {
        if (!device.TryOpen(out HidStream stream))
            return null;
        stream.ReadTimeout = (int)ReplyTimeout.TotalMilliseconds;
        var (vendor, name) = Names(device);
        return new ViaDevice(provider, NewState(id, vendor, name, null), stream, device);
    }

    /// <summary>A keyboard we recognise but can't talk to yet.</summary>
    public static ViaDevice Stock(ViaProvider provider, string id, HidDevice device)
    {
        var (vendor, name) = Names(device);
        var note = device.VendorID == ViaEffects.KeychronVendorId
            ? "This Keychron keyboard isn't answering VIA. Connect it with the USB cable and set its switch to cable mode. " +
              "Keychron keyboards without QMK firmware, such as the original K and C series, can't be changed here."
            : "This keyboard runs Glorious's stock firmware. Flash QMK with VIA to change its lighting and keys here.";
        return new ViaDevice(provider, NewState(id, vendor, name, note), null, device);
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
        if (device.VendorID == ViaEffects.KeychronVendorId && string.IsNullOrWhiteSpace(vendor))
            vendor = "Keychron";
        return (string.IsNullOrWhiteSpace(vendor) ? "Keyboard" : vendor.Trim(),
                string.IsNullOrWhiteSpace(name) ? $"Keyboard {device.VendorID:X4}:{device.ProductID:X4}" : name.Trim());
    }

    /// <summary>Re-reads the lighting, and the keymap the first time.</summary>
    public void Read()
    {
        if (_keyboard == null) return;
        _protocol ??= _keyboard.GetProtocolVersion();
        if (_protocol < ViaKeyboard.MinLightingProtocol)
        {
            _provider.Changed(this, State with { Note = $"This keyboard's VIA firmware is too old (protocol {_protocol}). Update it to change lighting and keys here." });
            return;
        }

        var lighting = ReadLighting();
        if (!_keymapChecked)
        {
            _keymapChecked = true;
            try
            {
                var keymap = _keyboard.ReadKeymap();
                _keymap = new KeymapState($"{_vendorId:X4}:{_productId:X4}", keymap,
                    HardwareKeys.DescribeLayers(keymap, macWindowsPairs: _vendorId == ViaEffects.KeychronVendorId));
            }
            catch (ViaException) { } // no dynamic keymap
        }
        _provider.Changed(this, State with { Lighting = lighting, Keymap = _keymap });
    }

    private LightingState? ReadLighting()
    {
        var keyboard = _keyboard!;
        if (!_lightingChecked)
        {
            // Per-key RGB first, then single-color backlights such as white-backlit Keychron boards.
            foreach (var channel in new[] { LightingChannel.RgbMatrix, LightingChannel.LedMatrix })
            {
                try
                {
                    keyboard.GetLighting(channel);
                    _channel = channel;
                    break;
                }
                catch (ViaException) { }
            }
            _lightingChecked = true;
        }
        if (_channel is not { } found)
            return null;

        if (!_effectsCounted && ViaEffects.NeedsCount(_vendorId, _productId))
        {
            if (keyboard.CountEffects(found) is { } count)
            {
                _effectsCounted = true;
                _effectNames = ViaEffects.For(_vendorId, _productId, count);
            }
        }
        try { return new LightingState(keyboard.GetLighting(found), _effectNames, HasColor: found == LightingChannel.RgbMatrix); }
        catch (ViaException) { return null; }
    }

    public override void SetLighting(LightingSettings lighting)
    {
        if (_keyboard == null || _channel == null)
            base.SetLighting(lighting);
        _keyboard!.SetLighting(lighting, _channel!.Value);
        Read();
    }

    public override void SetKeycode(KeyPosition key, ushort keycode)
    {
        if (_keyboard == null || _keymap == null)
            base.SetKeycode(key, keycode);
        _keyboard!.SetKeycode(key.Layer, key.Row, key.Column, keycode);
        // Read it back rather than trusting the write.
        var stored = _keyboard.GetKeycode(key.Layer, key.Row, key.Column);
        _keymap = _keymap! with { Keymap = _keymap.Keymap.With(key.Layer, key.Row, key.Column, stored) };
        _provider.Changed(this, State with { Keymap = _keymap });
        if (stored != keycode)
            throw new ViaException("The keyboard didn't keep the change.");
    }

    public override void ResetKeymap()
    {
        if (_keyboard == null || _keymap == null)
            base.ResetKeymap();
        _keyboard!.ResetKeymap();
        _keymap = _keymap! with { Keymap = _keyboard.ReadKeymap() };
        _provider.Changed(this, State with { Keymap = _keymap });
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
