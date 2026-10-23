using MiniMouseHub.Core.Hidpp;

namespace MiniMouseHub.Core;

/// <summary>
/// Finds every Logitech HID++ device (through receivers or cables), polls their batteries, and
/// listens for the notifications they send (DPI button presses, battery changes, sleep and wake).
/// </summary>
internal sealed class LogitechProvider(IDeviceHost host) : DeviceProvider(host)
{
    private readonly List<ChannelContext> _channels = new();
    private readonly List<DeviceContext> _devices = new();

    public override IEnumerable<ManagedDevice> Devices => _devices;

    public override void Refresh()
    {
        Scan();
        foreach (var device in _devices.ToList())
            Try(device, () => RefreshAll(device));
    }

    public override void Poll()
    {
        foreach (var device in _devices.ToList())
        {
            if (device.State.Awake)
                Try(device, () => RefreshBattery(device));
            else
                Try(device, () => Wake(device));
        }
    }

    /// <summary>Runs device work, turning timeouts into "asleep" and I/O failures into a dropped receiver.</summary>
    private void Try(DeviceContext device, Action action)
    {
        try
        {
            action();
        }
        catch (HidppTimeoutException)
        {
            Update(device, device.State with { Awake = false });
        }
        catch (HidppException)
        {
        }
        catch (Exception)
        {
            DropChannel(device.Channel);
        }
    }

    // ---- Discovery ----

    public override void Scan()
    {
        // Drop receivers that were unplugged.
        var present = HidppDiscovery.FindHidppDevices();
        var presentPaths = present.Select(g => g[0].DevicePath).ToHashSet();
        foreach (var channel in _channels.Where(c => !presentPaths.Contains(c.Channel.DevicePath)).ToList())
            DropChannel(channel);

        foreach (var group in present)
        {
            if (_channels.Any(c => c.Channel.DevicePath == group[0].DevicePath))
                continue;
            HidppChannel opened;
            try { opened = new HidppChannel(group); }
            catch (Exception) { continue; }

            var context = new ChannelContext(opened);
            opened.ReportReceived += report => OnReport(context, report);
            _channels.Add(context);
            Enumerate(context);
        }
    }

    /// <summary>Finds the devices behind a receiver (or the wired device itself) and adds any new ones.</summary>
    private void Enumerate(ChannelContext channel)
    {
        List<LogitechDevice> found;
        try { found = LogitechDevice.Enumerate(channel.Channel); }
        catch (Exception) { return; }

        foreach (var logi in found)
        {
            var id = $"{channel.Channel.DevicePath}#{logi.DeviceIndex}";
            var existing = _devices.FirstOrDefault(d => d.State.Id == id);
            if (existing != null)
            {
                existing.Device = logi;
                Try(existing, () => RefreshAll(existing));
                continue;
            }

            var device = new DeviceContext(this, channel, logi, new DeviceState(
                id, "Logitech", "Logitech device", DeviceKind.Unknown, logi.IsWired, true, null, null, null, null, null, null));
            _devices.Add(device);
            Try(device, () => RefreshAll(device));
        }
        Host.Publish();
    }

    private void Wake(DeviceContext device)
    {
        // A cheap ping; if it answers, the device is back.
        device.Channel.Channel.Request(device.Device.DeviceIndex, 0x00, 1, [0, 0, 0x5A], timeoutMs: 700);
        RefreshAll(device);
    }

    private void DropChannel(ChannelContext channel)
    {
        _channels.Remove(channel);
        _devices.RemoveAll(d => d.Channel == channel);
        try { channel.Channel.Dispose(); } catch { }
        Host.Publish();
    }

    // ---- Reading state ----

    private void RefreshAll(DeviceContext device)
    {
        var d = device.Device;
        device.DpiFeatureIndex = d.GetFeatureIndex(FeatureId.ExtendedAdjustableDpi) ?? d.GetFeatureIndex(FeatureId.AdjustableDpi);
        device.BatteryFeatureIndex = d.GetFeatureIndex(FeatureId.UnifiedBattery)
            ?? d.GetFeatureIndex(FeatureId.BatteryVoltage) ?? d.GetFeatureIndex(FeatureId.BatteryStatus);
        device.OnboardFeatureIndex = d.GetFeatureIndex(FeatureId.OnboardProfiles);

        var name = device.State.Name;
        var kind = device.State.Kind;
        if (d.HasFeature(FeatureId.DeviceName))
        {
            try { name = d.GetName(); } catch (HidppErrorException) { }
            try { kind = d.GetKind(); } catch (HidppErrorException) { }
        }

        Update(device, device.State with
        {
            Name = name,
            Kind = kind,
            Awake = true,
            DpiCapabilities = d.HasAdjustableDpi ? d.GetDpiCapabilities() : null,
        });
        if (device.BatteryFeatureIndex != null)
            RefreshBattery(device);
        RefreshDpi(device, raiseEvent: false);
    }

    private void RefreshBattery(DeviceContext device)
    {
        if (device.BatteryFeatureIndex == null) return;
        Update(device, device.State with { Awake = true, Battery = device.Device.GetBattery() });
    }

    private void RefreshDpi(DeviceContext device, bool raiseEvent)
    {
        var d = device.Device;
        if (!d.HasAdjustableDpi) return;
        var old = device.State.Dpi;
        var dpi = d.GetDpi().Current;

        IReadOnlyList<int>? slots = null;
        int? activeSlot = null;
        OnboardMode? mode = null;
        if (device.OnboardFeatureIndex != null)
        {
            mode = d.GetOnboardMode();
            if (mode == OnboardMode.Onboard)
            {
                var store = new OnboardProfileStore(d);
                var description = store.GetDescription();
                var current = store.GetCurrentProfile();
                var header = store.GetProfileHeaders(description).FirstOrDefault(h => h.Number == current);
                if (header != null)
                    slots = store.ReadProfile(header, description).DpiSlots.Where(v => v is not (0 or 0xFFFF)).ToArray();
                activeSlot = store.GetCurrentDpiIndex();
            }
        }

        var state = device.State with { Awake = true, Dpi = dpi, DpiSlots = slots, ActiveDpiSlot = activeSlot, Mode = mode };
        Update(device, state);
        if (raiseEvent && old is { } before && before != dpi && !device.SuppressNextDpiEvent)
            Host.RaiseDpiChanged(state, before, dpi);
        device.SuppressNextDpiEvent = false;
    }

    /// <summary>Runs on a HID reader thread: only decide what to re-read, never talk to the device here.</summary>
    private void OnReport(ChannelContext channel, byte[] report)
    {
        if (report.Length < 5) return;

        // Receiver notification 0x41: a paired device connected or disconnected (woke up or went to sleep).
        if (report[0] == HidppConstants.ShortReportId && report[2] == 0x41)
        {
            var deviceIndex = report[1];
            var linkLost = (report[4] & 0x40) != 0;
            Host.Enqueue(() =>
            {
                var device = _devices.FirstOrDefault(d => d.Channel == channel && d.Device.DeviceIndex == deviceIndex);
                if (linkLost)
                {
                    if (device != null) Update(device, device.State with { Awake = false });
                }
                else if (device != null)
                {
                    Try(device, () => RefreshAll(device));
                }
                else
                {
                    Enumerate(channel); // a device we haven't seen yet
                }
            });
            return;
        }

        if ((report[3] & 0x0F) != 0)
            return; // a response to one of our requests, not an event

        var index = report[1];
        var feature = report[2];
        Host.Enqueue(() =>
        {
            // The device list belongs to the monitor thread, so look the device up there.
            var target = _devices.FirstOrDefault(d => d.Channel == channel && d.Device.DeviceIndex == index);
            if (target == null) return;
            if (feature == target.DpiFeatureIndex || feature == target.OnboardFeatureIndex)
                Try(target, () => RefreshDpi(target, raiseEvent: true));
            else if (feature == target.BatteryFeatureIndex)
                Try(target, () => RefreshBattery(target));
        });
    }

    public override void Dispose()
    {
        foreach (var channel in _channels)
            channel.Channel.Dispose();
    }

    private sealed class ChannelContext(HidppChannel channel)
    {
        public HidppChannel Channel { get; } = channel;
    }

    private sealed class DeviceContext(LogitechProvider provider, ChannelContext channel, LogitechDevice device, DeviceState state)
        : ManagedDevice(state)
    {
        public ChannelContext Channel { get; } = channel;
        public LogitechDevice Device { get; set; } = device;
        public byte? DpiFeatureIndex { get; set; }
        public byte? BatteryFeatureIndex { get; set; }
        public byte? OnboardFeatureIndex { get; set; }
        public bool SuppressNextDpiEvent { get; set; }

        public override void SetDpi(int dpi)
        {
            if (!Device.HasAdjustableDpi)
                base.SetDpi(dpi);
            SuppressNextDpiEvent = true;
            if (Device.HasFeature(FeatureId.OnboardProfiles) && Device.GetOnboardMode() == OnboardMode.Onboard)
            {
                var store = new OnboardProfileStore(Device);
                store.SetDpiInActiveProfile(store.GetCurrentDpiIndex(), dpi);
            }
            else
            {
                Device.SetDpi(dpi);
            }
            provider.RefreshDpi(this, raiseEvent: false);
        }

        public override void SelectDpiSlot(int slot)
        {
            SuppressNextDpiEvent = true;
            new OnboardProfileStore(Device).SetCurrentDpiIndex(slot);
            provider.RefreshDpi(this, raiseEvent: false);
        }
    }
}
