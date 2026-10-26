using System.Text;
using MiniMouseHub.Core.Hidpp;

namespace MiniMouseHub.Core;

/// <summary>
/// Finds every Logitech HID++ device (through receivers or cables), polls their batteries, and
/// listens for the notifications they send (DPI button presses, battery changes, sleep and wake).
/// Mice without onboard profiles (the Logi Options family, such as the MX Master) forget settings
/// when they reconnect, so the settings the app set are kept and set again each time they connect.
/// </summary>
internal sealed class LogitechProvider(IDeviceHost host) : DeviceProvider(host)
{
    private readonly List<ChannelContext> _channels = new();
    private readonly List<DeviceContext> _devices = new();

    // How often a sleeping device is pinged, in case the receiver doesn't say when it wakes up.
    private static readonly TimeSpan WakeProbeInterval = TimeSpan.FromSeconds(4);

    // A device that just reconnected may not answer straight away.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(400);

    public override IEnumerable<ManagedDevice> Devices => _devices;

    public override void Refresh()
    {
        Scan();
        foreach (var device in _devices.ToList())
            Try(device, () => RefreshAll(device));
    }

    public override void Poll()
    {
        // A receiver with nothing found on it may have a device that was switched on without the
        // receiver saying so (when connection notifications couldn't be turned on): look again.
        foreach (var channel in _channels.Where(c => !c.Channel.IsBluetooth && _devices.All(d => d.Channel != c)).ToList())
            Enumerate(channel);

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
            MarkAsleep(device);
        }
        catch (HidppException e)
        {
            DiagnosticLog.Add($"{device.State.Name}: {e.Message}");
        }
        catch (IOException e) when (device.Channel.Channel.IsBluetooth)
        {
            // Windows keeps a Bluetooth device's HID collection while it's asleep, but writes to it fail.
            DiagnosticLog.Add($"{device.State.Name}: {e.Message}");
            MarkAsleep(device);
        }
        catch (Exception e)
        {
            DiagnosticLog.Add($"{device.State.Name}: dropping {device.Channel.Channel.ProductName} after {e}");
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
            catch (Exception e)
            {
                DiagnosticLog.Add($"Couldn't open {group[0].DevicePath}: {e.Message}");
                continue;
            }

            var label = $"{opened.ProductName} ({opened.ProductId:X4})";
            DiagnosticLog.Add($"Opened {label}{(opened.IsBluetooth ? " over Bluetooth" : "")}: {opened.DevicePath}");
            opened.ReportSent += report => DiagnosticLog.Report("→", label, report);
            opened.ReportReceived += report => DiagnosticLog.Report("←", label, report);
            var context = new ChannelContext(opened);
            opened.ReportReceived += report => OnReport(context, report);
            _channels.Add(context);
            Enumerate(context);
            EnableConnectionNotifications(context);
        }
    }

    /// <summary>
    /// Asks a receiver to tell us when its devices connect and disconnect (notification 0x41), so a
    /// device that wakes up is seen right away. Receivers only send these when the flag is set, and
    /// it is cleared whenever the receiver is plugged in again.
    /// </summary>
    private void EnableConnectionNotifications(ChannelContext channel)
    {
        if (channel.Channel.IsBluetooth || _devices.Any(d => d.Channel == channel && d.Device.IsWired))
            return;
        try
        {
            var flags = channel.Channel.ReadRegister(HidppConstants.WiredDeviceIndex, HidppConstants.NotificationFlagsRegister);
            if ((flags[1] & HidppConstants.WirelessNotificationsFlag) != 0)
                return;
            flags[1] |= HidppConstants.WirelessNotificationsFlag;
            channel.Channel.WriteRegister(HidppConstants.WiredDeviceIndex, HidppConstants.NotificationFlagsRegister, flags);
            DiagnosticLog.Add($"Turned on connection notifications on {channel.Channel.ProductName}");
        }
        catch (Exception e)
        {
            // Not a receiver, or one that doesn't have the register. The wake probe and the poll still cover it.
            DiagnosticLog.Add($"Couldn't turn on connection notifications on {channel.Channel.ProductName}: {e.Message}");
        }
    }

    /// <summary>
    /// Finds the devices behind a receiver (or the wired or Bluetooth device itself) and adds any new
    /// ones. Devices paired with a receiver are added even when they are switched off, so they show
    /// as offline straight away and come online as soon as they connect.
    /// </summary>
    private void Enumerate(ChannelContext channel)
    {
        var c = channel.Channel;
        var found = new List<LogitechDevice>();
        List<PairedDevice>? paired = null;
        try
        {
            // A wired or Bluetooth HID++ 2.0 device answers at 0xFF. A receiver answers there too, but with a HID++ 1.0 error.
            if (LogitechDevice.TryConnect(c, HidppConstants.WiredDeviceIndex) is { } direct)
            {
                found.Add(direct);
            }
            else if (!c.IsBluetooth)
            {
                // Ask every slot, as before pairing information was read: the pairing registers only add
                // the devices that are switched off, so a receiver that reports them oddly can't hide one.
                for (byte index = 1; index <= 6; index++)
                {
                    if (LogitechDevice.TryConnect(c, index) is { } device)
                        found.Add(device);
                }
                paired = ReceiverPairings.Read(c); // never throws: it only adds offline devices
                if (paired != null)
                    DiagnosticLog.Add($"{c.ProductName} has paired: {(paired.Count == 0 ? "nothing" : string.Join(", ", paired.Select(p => $"{p.Index}: {p.Name ?? "?"} ({p.Kind})")))}");
            }
        }
        catch (Exception e)
        {
            // Keep the devices found so far.
            DiagnosticLog.Add($"Couldn't list all the devices on {c.ProductName}: {e.Message}");
        }

        foreach (var logi in found)
            Add(channel, logi);

        // Paired but switched off or asleep: show it offline and keep checking for it. When a device
        // answered in a slot the receiver didn't list, its pairing information can't be trusted.
        if (paired != null && found.Any(f => paired.All(p => p.Index != f.DeviceIndex)))
        {
            DiagnosticLog.Add($"{c.ProductName}'s pairing information doesn't match its devices; not showing offline ones");
            paired = null;
        }
        var offline = c.IsBluetooth && found.Count == 0
            ? [new PairedDevice(HidppConstants.WiredDeviceIndex, c.ProductName, DeviceKind.Unknown)]
            : paired?.Where(p => found.All(f => f.DeviceIndex != p.Index)) ?? [];
        foreach (var p in offline)
        {
            var id = $"{c.DevicePath}#{p.Index}";
            if (_devices.Any(d => d.State.Id == id))
                continue;
            var logi = LogitechDevice.Offline(c, p.Index);
            DiagnosticLog.Add($"{p.Name ?? "A device"} at index 0x{p.Index:X2} on {c.ProductName} is paired but offline");
            var device = new DeviceContext(this, channel, logi, new DeviceState(
                id, "Logitech", p.Name ?? "Logitech device", p.Kind, false, false, null, null, null, null, null, null,
                Connection: logi.Connection));
            _devices.Add(device);
            ScheduleWakeProbe(device);
        }
        Host.Publish();
    }

    /// <summary>Adds a device that answered, or updates the one already shown for that slot, and reads it.</summary>
    private void Add(ChannelContext channel, LogitechDevice logi)
    {
        var id = $"{channel.Channel.DevicePath}#{logi.DeviceIndex}";
        var existing = _devices.FirstOrDefault(d => d.State.Id == id);
        if (existing != null)
        {
            existing.Device = logi;
            Try(existing, () => RefreshAll(existing));
            return;
        }

        DiagnosticLog.Add($"Found a HID++ {logi.ProtocolVersion} device at index 0x{logi.DeviceIndex:X2} on {channel.Channel.ProductName} ({logi.Connection})");
        var device = new DeviceContext(this, channel, logi, new DeviceState(
            id, "Logitech", "Logitech device", DeviceKind.Unknown, logi.IsWired, true, null, null, null, null, null, null,
            Connection: logi.Connection));
        _devices.Add(device);
        Try(device, () => RefreshAll(device));
    }

    /// <summary>Marks a device asleep, keeping what we last read from it, and starts checking for it to wake up.</summary>
    private void MarkAsleep(DeviceContext device)
    {
        if (device.State.Awake)
            DiagnosticLog.Add($"{device.State.Name} went to sleep or disconnected");
        device.NeedsRestore = true;
        device.PressedControls.Clear();
        Update(device, device.State with { Awake = false });
        ScheduleWakeProbe(device);
    }

    private void ScheduleWakeProbe(DeviceContext device)
    {
        if (device.WakeProbeScheduled) return;
        device.WakeProbeScheduled = true;
        After(WakeProbeInterval, () =>
        {
            device.WakeProbeScheduled = false;
            if (!_devices.Contains(device) || device.State.Awake) return;
            Try(device, () => Wake(device));
            if (_devices.Contains(device) && !device.State.Awake)
                ScheduleWakeProbe(device);
        });
    }

    /// <summary>Runs work on the monitor thread after a delay.</summary>
    private void After(TimeSpan delay, Action action) =>
        Task.Delay(delay).ContinueWith(_ => Host.Enqueue(action));

    private void Wake(DeviceContext device)
    {
        // A cheap ping; if it answers, the device is back. A receiver answers for a device that is
        // switched off with an error, which means the same as no answer.
        try
        {
            device.Channel.Channel.Request(device.Device.DeviceIndex, 0x00, 1, [0, 0, 0x5A], timeoutMs: 700);
        }
        catch (HidppErrorException e)
        {
            throw new HidppTimeoutException($"{device.State.Name} isn't connected ({e.Message}).");
        }
        if (device.Device.IsOffline && LogitechDevice.TryConnect(device.Channel.Channel, device.Device.DeviceIndex) is { } connected)
        {
            DiagnosticLog.Add($"{device.State.Name} came online (HID++ {connected.ProtocolVersion})");
            device.Device = connected;
        }
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
        device.ReprogFeatureIndex = d.GetFeatureIndex(FeatureId.ReprogControls);
        device.SmartShiftFeatureIndex = d.GetFeatureIndex(FeatureId.SmartShiftEnhanced) ?? d.GetFeatureIndex(FeatureId.SmartShift);

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
        if (device.NeedsRestore)
        {
            device.NeedsRestore = false;
            RestoreSettings(device);
        }
        if (device.BatteryFeatureIndex != null)
            RefreshBattery(device);
        RefreshDpi(device, raiseEvent: false);
        // Newer features: if one misbehaves, the rest of the device should still work.
        foreach (var (what, read) in new (string, Action)[] { ("scroll settings", () => RefreshScroll(device)), ("buttons", () => RefreshButtons(device)) })
        {
            try { read(); }
            catch (HidppErrorException e) { DiagnosticLog.Add($"{device.State.Name}: couldn't read {what}: {e.Message}"); }
        }
    }

    private void RefreshScroll(DeviceContext device)
    {
        Update(device, device.State with { Scroll = device.Device.GetScrollSettings() });
    }

    // Left and right click are never offered, so the mouse can't be made unusable.
    private static readonly HashSet<int> ProtectedControls = [0x0050, 0x0051];

    private void RefreshButtons(DeviceContext device)
    {
        // Mice with onboard profiles keep their buttons in the profile instead.
        if (device.ReprogFeatureIndex == null || device.OnboardFeatureIndex != null)
            return;
        var d = device.Device;
        device.Controls ??= d.GetControls();
        var actions = Remembered(device).Actions;

        var buttons = new List<ButtonControl>();
        foreach (var control in device.Controls)
        {
            if (control.IsVirtual || ProtectedControls.Contains(control.Cid))
                continue;
            // A control can do the job of any control whose group is in its group mask.
            var targets = control.GroupMask == 0 ? [] : device.Controls
                .Where(t => t.Cid != control.Cid && t.Group is >= 1 and <= 8 && (control.GroupMask & (1 << (t.Group - 1))) != 0)
                .Select(t => new ButtonTarget(t.Cid, ControlNames.NameOf(t.Cid)))
                .ToList();
            if (targets.Count == 0 && !control.IsDivertable)
                continue;
            targets.Insert(0, new ButtonTarget(control.Cid, ControlNames.NameOf(control.Cid)));

            var (mappedTo, diverted) = d.GetControlReporting(control.Cid);
            buttons.Add(new ButtonControl(control.Cid, ControlNames.NameOf(control.Cid), targets, mappedTo, control.IsDivertable,
                diverted ? actions.GetValueOrDefault(control.Cid) : null));
        }
        Update(device, device.State with { Buttons = buttons.Count > 0 ? buttons : null });
    }

    // ---- Remembered settings ----

    private DeviceSettings Remembered(DeviceContext device) =>
        Host.SettingsStore?.Load(device.State.Name) ?? new DeviceSettings();

    private void Remember(DeviceContext device, Action<DeviceSettings> change)
    {
        var settings = Remembered(device);
        change(settings);
        Host.SettingsStore?.Save(device.State.Name, settings);
    }

    /// <summary>Sets again what the app set on a device that forgets settings when it reconnects.</summary>
    private void RestoreSettings(DeviceContext device)
    {
        var s = Remembered(device);
        var d = device.Device;
        void Restore(string what, Action action)
        {
            try { action(); }
            catch (HidppErrorException e) { DiagnosticLog.Add($"{device.State.Name}: couldn't restore {what}: {e.Message}"); }
        }

        if (s.Dpi is { } dpi && d.HasAdjustableDpi && device.OnboardFeatureIndex == null)
            Restore("DPI", () => d.SetDpi(dpi));
        if (s.WheelMode is { } mode && device.SmartShiftFeatureIndex != null)
            Restore("SmartShift", () => d.SetWheelMode(mode, s.SmartShiftThreshold, s.Torque));
        if (s.WheelInverted is { } wheel && d.HasFeature(FeatureId.HiResWheel))
            Restore("scroll direction", () => d.SetWheelInverted(wheel));
        if (s.ThumbWheelInverted is { } thumb && d.HasFeature(FeatureId.ThumbWheel))
            Restore("thumb wheel direction", () => d.SetThumbWheelInverted(thumb));
        if (device.ReprogFeatureIndex != null)
        {
            foreach (var cid in s.Remaps.Keys.Union(s.Actions.Keys))
                Restore($"button {ControlNames.NameOf(cid)}", () => d.SetControlReporting(cid, s.Remaps.GetValueOrDefault(cid, cid), s.Actions.ContainsKey(cid)));
        }
        if (s.Dpi != null || s.WheelMode != null || s.Remaps.Count > 0 || s.Actions.Count > 0)
            DiagnosticLog.Add($"{device.State.Name}: restored its settings");
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

    /// <summary>A diverted button event lists the controls held down now (up to 4); compare it with the last one.</summary>
    private void OnButtonsHeld(DeviceContext device, HashSet<int> held)
    {
        var actions = Remembered(device).Actions;
        foreach (var cid in held.Except(device.PressedControls))
        {
            if (actions.TryGetValue(cid, out var action))
                Host.RaiseButtonAction(device.State, action, true);
        }
        foreach (var cid in device.PressedControls.Except(held))
        {
            if (actions.TryGetValue(cid, out var action))
                Host.RaiseButtonAction(device.State, action, false);
        }
        device.PressedControls = held;
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
                    if (device != null) MarkAsleep(device);
                }
                else if (device != null)
                {
                    device.NeedsRestore = true;
                    DiagnosticLog.Add($"{device.State.Name} connected");
                    // The receiver says it's connected, so show it awake now and read it once it has settled.
                    Update(device, device.State with { Awake = true });
                    After(SettleDelay, () =>
                    {
                        if (_devices.Contains(device) && device.State.Awake)
                            Try(device, () => Wake(device)); // also replaces a device that was offline since the app started
                    });
                }
                else
                {
                    LookForNewDevice(channel, deviceIndex);
                }
            });
            return;
        }

        if ((report[3] & 0x0F) != 0)
            return; // a response to one of our requests, not an event

        var index = report[1];
        var feature = report[2];
        var function = report[3] >> 4;
        // Only a HID++ 2.0 event from a device says it is there; error answers to our pings don't.
        var fromDevice = report[0] == HidppConstants.LongReportId && index is >= 1 and <= 6
            && feature != HidppConstants.Hidpp20ErrorMarker && feature != HidppConstants.Hidpp10ErrorMarker;
        Host.Enqueue(() =>
        {
            // The device list belongs to the monitor thread, so look the device up there.
            var target = _devices.FirstOrDefault(d => d.Channel == channel && d.Device.DeviceIndex == index);
            // A device talking on its own has just been switched on or woken up. This is how a device is
            // noticed on receivers that refuse to send connection notifications (such as the C54D Lightspeed).
            if (target == null)
            {
                if (fromDevice)
                    LookForNewDevice(channel, index);
                return;
            }
            if (!target.State.Awake)
            {
                if (fromDevice && !target.WakePending)
                {
                    target.WakePending = true;
                    After(SettleDelay, () =>
                    {
                        target.WakePending = false;
                        if (_devices.Contains(target) && !target.State.Awake)
                            Try(target, () => Wake(target));
                    });
                }
                return;
            }
            if (feature == target.ReprogFeatureIndex && function == 0)
            {
                var held = new HashSet<int>();
                for (var i = 4; i + 1 < report.Length && i < 12; i += 2)
                {
                    var cid = (report[i] << 8) | report[i + 1];
                    if (cid != 0) held.Add(cid);
                }
                OnButtonsHeld(target, held);
            }
            else if (feature == target.SmartShiftFeatureIndex)
                Try(target, () => RefreshScroll(target)); // the SmartShift button was pressed
            else if (feature == target.DpiFeatureIndex || feature == target.OnboardFeatureIndex)
                Try(target, () => RefreshDpi(target, raiseEvent: true));
            else if (feature == target.BatteryFeatureIndex)
                Try(target, () => RefreshBattery(target));
        });
    }

    /// <summary>
    /// Looks for a device that just appeared on a receiver. It may not answer straight after connecting,
    /// so look once it has settled, and again a little later if it still didn't answer.
    /// </summary>
    private void LookForNewDevice(ChannelContext channel, byte deviceIndex)
    {
        if (channel.LookingForNewDevice) return; // a device sends several reports when it connects
        channel.LookingForNewDevice = true;
        bool Found() => _devices.Any(d => d.Channel == channel && d.Device.DeviceIndex == deviceIndex);
        // Ask just that slot: asking all of them waits for every switched-off slot to time out.
        void TryAdd()
        {
            if (_channels.Contains(channel) && !Found() && LogitechDevice.TryConnect(channel.Channel, deviceIndex) is { } logi)
            {
                Add(channel, logi);
                Host.Publish();
            }
        }
        After(SettleDelay, () =>
        {
            TryAdd();
            if (!_channels.Contains(channel) || Found())
            {
                channel.LookingForNewDevice = false;
                return;
            }
            After(TimeSpan.FromSeconds(3), () =>
            {
                channel.LookingForNewDevice = false;
                TryAdd();
            });
        });
    }

    public override void Dispose()
    {
        // Give diverted buttons back to the mouse, or they would do nothing while the app isn't running.
        foreach (var device in _devices.Where(d => d.State.Awake && d.ReprogFeatureIndex != null))
        {
            foreach (var cid in device.State.Buttons?.Where(b => b.Action != null).Select(b => b.Cid) ?? [])
            {
                try { device.Device.SetControlReporting(cid, device.State.Buttons!.First(b => b.Cid == cid).MappedTo, false); }
                catch (Exception) { }
            }
        }
        foreach (var channel in _channels)
            channel.Channel.Dispose();
    }

    public override void Describe(StringBuilder report)
    {
        foreach (var channel in _channels)
        {
            var c = channel.Channel;
            report.AppendLine();
            report.AppendLine($"== {c.ProductName} (PID {c.ProductId:X4}{(c.IsBluetooth ? ", Bluetooth" : "")}) ==");
            report.AppendLine(c.DevicePath);
            foreach (var device in _devices.Where(d => d.Channel == channel))
                DescribeDevice(device, report);
        }
    }

    private static void DescribeDevice(DeviceContext device, StringBuilder report)
    {
        var d = device.Device;
        report.AppendLine();
        report.AppendLine($"-- {device.State.Name}: index 0x{d.DeviceIndex:X2}, {d.Connection}, HID++ {d.ProtocolVersion}, {(device.State.Awake ? "awake" : "asleep")} --");
        void Section(string title, Action write)
        {
            try { write(); }
            catch (Exception e) { report.AppendLine($"{title}: {e.GetType().Name}: {e.Message}"); }
        }

        Section("Features", () =>
        {
            foreach (var f in d.GetFeatures())
                report.AppendLine($"  [{f.Index:X2}] 0x{f.Id:X4} v{f.Version} {f.Name}{(f.IsHidden ? " (hidden)" : "")}{(f.IsEngineering ? " (engineering)" : "")}");
        });
        Section("Battery", () => report.AppendLine($"Battery: {d.GetBattery()}"));
        if (d.HasAdjustableDpi)
            Section("DPI", () => report.AppendLine($"DPI: {d.GetDpi()}"));
        Section("Scroll", () => report.AppendLine($"Scroll: {d.GetScrollSettings()?.ToString() ?? "none"}"));
        if (d.HasFeature(FeatureId.ReprogControls))
        {
            Section("Controls", () =>
            {
                report.AppendLine("Controls (cid, task, flags, group, group mask, mapped to, diverted):");
                foreach (var control in d.GetControls())
                {
                    var (mapped, diverted) = d.GetControlReporting(control.Cid);
                    report.AppendLine($"  0x{control.Cid:X4} {ControlNames.NameOf(control.Cid),-24} task 0x{control.TaskId:X4} flags 0x{control.Flags:X2} " +
                        $"group {control.Group} mask 0x{control.GroupMask:X2} -> 0x{mapped:X4}{(diverted ? " diverted" : "")}");
                }
            });
        }
    }

    private sealed class ChannelContext(HidppChannel channel)
    {
        public HidppChannel Channel { get; } = channel;
        public bool LookingForNewDevice { get; set; }
    }

    private sealed class DeviceContext(LogitechProvider provider, ChannelContext channel, LogitechDevice device, DeviceState state)
        : ManagedDevice(state)
    {
        public ChannelContext Channel { get; } = channel;
        public LogitechDevice Device { get; set; } = device;
        public byte? DpiFeatureIndex { get; set; }
        public byte? BatteryFeatureIndex { get; set; }
        public byte? OnboardFeatureIndex { get; set; }
        public byte? ReprogFeatureIndex { get; set; }
        public byte? SmartShiftFeatureIndex { get; set; }
        public List<LogitechDevice.ControlInfo>? Controls { get; set; }
        public HashSet<int> PressedControls { get; set; } = new();

        /// <summary>The device (re)connected, so the settings it forgot need setting again.</summary>
        public bool NeedsRestore { get; set; } = true;
        public bool SuppressNextDpiEvent { get; set; }
        public bool WakeProbeScheduled { get; set; }
        public bool WakePending { get; set; }

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
                provider.Remember(this, s => s.Dpi = dpi);
            }
            provider.RefreshDpi(this, raiseEvent: false);
        }

        public override void SetWheelMode(WheelMode mode, int? threshold, int? torque)
        {
            if (SmartShiftFeatureIndex == null)
                base.SetWheelMode(mode, threshold, torque);
            Device.SetWheelMode(mode, threshold, torque);
            provider.Remember(this, s =>
            {
                s.WheelMode = mode;
                s.SmartShiftThreshold = threshold ?? s.SmartShiftThreshold;
                s.Torque = torque ?? s.Torque;
            });
            provider.RefreshScroll(this);
        }

        public override void SetWheelInverted(bool inverted)
        {
            if (!Device.HasFeature(FeatureId.HiResWheel))
                base.SetWheelInverted(inverted);
            Device.SetWheelInverted(inverted);
            provider.Remember(this, s => s.WheelInverted = inverted);
            provider.RefreshScroll(this);
        }

        public override void SetThumbWheelInverted(bool inverted)
        {
            if (!Device.HasFeature(FeatureId.ThumbWheel))
                base.SetThumbWheelInverted(inverted);
            Device.SetThumbWheelInverted(inverted);
            provider.Remember(this, s => s.ThumbWheelInverted = inverted);
            provider.RefreshScroll(this);
        }

        public override void SetButton(int cid, ButtonAssignment assignment)
        {
            if (ReprogFeatureIndex == null)
                base.SetButton(cid, assignment);
            var remapTo = assignment.Action == null ? assignment.RemapTo ?? cid : cid;
            Device.SetControlReporting(cid, remapTo, diverted: assignment.Action != null);
            provider.Remember(this, s =>
            {
                if (remapTo != cid) s.Remaps[cid] = remapTo; else s.Remaps.Remove(cid);
                if (assignment.Action != null) s.Actions[cid] = assignment.Action; else s.Actions.Remove(cid);
            });
            PressedControls.Remove(cid);
            provider.RefreshButtons(this);
        }

        public override void SelectDpiSlot(int slot)
        {
            SuppressNextDpiEvent = true;
            new OnboardProfileStore(Device).SetCurrentDpiIndex(slot);
            provider.RefreshDpi(this, raiseEvent: false);
        }
    }
}
