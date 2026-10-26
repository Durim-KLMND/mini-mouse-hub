using System.Collections.Concurrent;
using System.Text;
using HidSharp;

namespace MiniMouseHub.Core;

/// <summary>A snapshot of what the app knows about one device, whatever its brand.</summary>
/// <param name="Vendor">The brand, such as "Logitech" or "Glorious".</param>
/// <param name="Lighting">The keyboard's RGB settings, when it has lighting the app can control.</param>
/// <param name="Note">Something the user should know about this device, such as why its settings can't be changed.</param>
/// <param name="Scroll">Scroll wheel settings, on mice that have them (such as SmartShift on the MX Master).</param>
/// <param name="Buttons">Buttons that can be given another job, on mice without onboard profiles.</param>
public record DeviceState(
    string Id,
    string Vendor,
    string Name,
    DeviceKind Kind,
    bool IsWired,
    bool Awake,
    BatteryInfo? Battery,
    int? Dpi,
    IReadOnlyList<int>? DpiSlots,
    int? ActiveDpiSlot,
    DpiCapabilities? DpiCapabilities,
    OnboardMode? Mode,
    LightingState? Lighting = null,
    string? Note = null,
    ConnectionKind Connection = ConnectionKind.Unknown,
    ScrollSettings? Scroll = null,
    IReadOnlyList<ButtonControl>? Buttons = null)
{
    public bool HasDpi => DpiCapabilities != null;
    public bool HasLighting => Lighting != null;

    /// <summary>The keymap of a keyboard whose keys can be changed, so keys such as the lighting key can be turned off.</summary>
    public Via.KeymapState? Keymap { get; init; }

    /// <summary>Brand and model, such as "Logitech PRO X Superlight 2", so devices can be told apart at a glance.</summary>
    public string DisplayName => DeviceNames.Display(Vendor, Name);
}

/// <summary>Turns the names devices report into the names people know them by.</summary>
public static class DeviceNames
{
    // Devices whose own name is shorter than their product name.
    private static readonly Dictionary<string, string> Models = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PRO X 2"] = "PRO X Superlight 2",
        ["PRO X SUPERLIGHT"] = "PRO X Superlight",
    };

    public static string Display(string vendor, string name)
    {
        var model = Models.GetValueOrDefault(name.Trim(), name.Trim());
        return model.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) ? model : $"{vendor} {model}";
    }
}

/// <summary>
/// Finds every supported device, keeps track of them, polls them, and listens for the
/// notifications they send. Each brand is handled by a <see cref="DeviceProvider"/>. All device
/// traffic runs on one background thread; events are raised from that thread.
/// </summary>
public sealed class DeviceMonitor : IDisposable, IDeviceHost
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;
    private readonly List<DeviceProvider> _providers;
    private volatile bool _disposed;
    private DateTime _nextPoll = DateTime.MinValue;

    /// <summary>Raised whenever the device list or any device's state changes.</summary>
    public event Action<IReadOnlyList<DeviceState>>? DevicesChanged;

    /// <summary>Raised when a device's DPI changes on the device itself, such as with the DPI button.</summary>
    public event Action<DeviceState, int, int>? DpiChangedOnDevice;

    /// <summary>Raised once the first scan for devices has finished.</summary>
    public event Action? FirstScanCompleted;

    /// <summary>Raised when a button that was given an app action is pressed (true) or released (false).</summary>
    public event Action<DeviceState, string, bool>? ButtonActionTriggered;

    /// <summary>Where settings that devices forget are kept, so they can be set again when the device reconnects.</summary>
    public IDeviceSettingsStore? SettingsStore { get; set; }

    public IReadOnlyList<DeviceState> Devices { get; private set; } = [];

    public DeviceMonitor()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Device monitor" };
        _providers = [new LogitechProvider(this), new Via.ViaProvider(this)];
    }

    public void Start()
    {
        DeviceList.Local.Changed += OnUsbDevicesChanged;
        _thread.Start();
    }

    /// <summary>Looks for new devices and re-reads everything.</summary>
    public void Refresh() => Enqueue(() =>
    {
        foreach (var provider in _providers)
            SafeRun(provider.Refresh);
    });

    /// <summary>
    /// Sets the DPI. When the device runs from its onboard profile, the value is saved into the
    /// profile's current DPI slot; otherwise it is set directly.
    /// </summary>
    public Task SetDpiAsync(string deviceId, int dpi) => RunOnDevice(deviceId, d => d.SetDpi(dpi));

    /// <summary>Switches to another DPI slot of the active onboard profile, like pressing the DPI button.</summary>
    public Task SelectDpiSlotAsync(string deviceId, int slot) => RunOnDevice(deviceId, d => d.SelectDpiSlot(slot));

    /// <summary>Changes what a key does, such as setting it to do nothing. The keyboard saves it right away.</summary>
    public Task SetKeycodeAsync(string deviceId, Via.KeyPosition key, ushort keycode) => RunOnDevice(deviceId, d => d.SetKeycode(key, keycode));

    /// <summary>Puts every key back to what the keyboard's firmware does by default.</summary>
    public Task ResetKeymapAsync(string deviceId) => RunOnDevice(deviceId, d => d.ResetKeymap());

    /// <summary>Changes a keyboard's lighting and saves it on the keyboard.</summary>
    public Task SetLightingAsync(string deviceId, LightingSettings lighting) => RunOnDevice(deviceId, d => d.SetLighting(lighting));

    /// <summary>Sets how the scroll wheel ratchets (SmartShift).</summary>
    public Task SetWheelModeAsync(string deviceId, WheelMode mode, int? threshold, int? torque) =>
        RunOnDevice(deviceId, d => d.SetWheelMode(mode, threshold, torque));

    public Task SetWheelInvertedAsync(string deviceId, bool inverted) => RunOnDevice(deviceId, d => d.SetWheelInverted(inverted));

    public Task SetThumbWheelInvertedAsync(string deviceId, bool inverted) => RunOnDevice(deviceId, d => d.SetThumbWheelInverted(inverted));

    /// <summary>Gives a button another job, or an app action.</summary>
    public Task SetButtonAsync(string deviceId, int cid, ButtonAssignment assignment) => RunOnDevice(deviceId, d => d.SetButton(cid, assignment));

    /// <summary>
    /// Writes a plain-text report of every device the app can see, what it supports and the recent
    /// HID++ traffic, for someone testing a device to send back.
    /// </summary>
    public Task<string> CreateDiagnosticReportAsync(string header)
    {
        var done = new TaskCompletionSource<string>();
        Enqueue(() =>
        {
            var report = new StringBuilder();
            report.AppendLine(header);
            report.AppendLine($"Created {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} on {Environment.OSVersion}");
            report.AppendLine();
            report.AppendLine("== Logitech HID collections ==");
            try { report.AppendLine(Hidpp.HidppDiscovery.DescribeAll()); }
            catch (Exception e) { report.AppendLine($"Couldn't list them: {e.Message}"); }
            foreach (var provider in _providers)
            {
                try { provider.Describe(report); }
                catch (Exception e) { report.AppendLine($"{provider.GetType().Name} failed: {e}"); }
            }
            report.AppendLine();
            report.AppendLine("== What the app shows ==");
            foreach (var device in Devices)
                report.AppendLine(device.ToString());
            report.AppendLine();
            report.AppendLine("== Recent log (→ sent, ← received) ==");
            foreach (var line in DiagnosticLog.Snapshot())
                report.AppendLine(line);
            done.SetResult(report.ToString());
        });
        return done.Task;
    }

    private Task RunOnDevice(string deviceId, Action<ManagedDevice> action)
    {
        var done = new TaskCompletionSource();
        Enqueue(() =>
        {
            try
            {
                var device = _providers.SelectMany(p => p.Devices).FirstOrDefault(d => d.State.Id == deviceId)
                    ?? throw new InvalidOperationException("That device isn't connected any more.");
                action(device);
                done.SetResult();
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        });
        return done.Task;
    }

    public void Enqueue(Action action)
    {
        try { _work.TryAdd(action); }
        catch (InvalidOperationException) { } // disposed
    }

    private void OnUsbDevicesChanged(object? sender, DeviceListChangedEventArgs e) => Enqueue(ScanAll);

    private void ScanAll()
    {
        foreach (var provider in _providers)
            SafeRun(provider.Scan);
    }

    private void Run()
    {
        ScanAll();
        _nextPoll = DateTime.UtcNow + PollInterval;
        FirstScanCompleted?.Invoke();
        while (!_disposed)
        {
            var wait = _nextPoll - DateTime.UtcNow;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            if (_work.TryTake(out var action, wait))
            {
                SafeRun(action);
                continue;
            }
            if (_disposed) break;

            _nextPoll = DateTime.UtcNow + PollInterval;
            foreach (var provider in _providers)
                SafeRun(provider.Poll);
        }
    }

    private static void SafeRun(Action action)
    {
        try { action(); }
        catch (Exception e) { DiagnosticLog.Add($"Error: {e}"); }
    }

    public void Publish()
    {
        Devices = _providers.SelectMany(p => p.Devices).Select(d => d.State).ToArray();
        DevicesChanged?.Invoke(Devices);
    }

    public void RaiseDpiChanged(DeviceState device, int before, int after) => DpiChangedOnDevice?.Invoke(device, before, after);

    public void RaiseButtonAction(DeviceState device, string action, bool pressed) => ButtonActionTriggered?.Invoke(device, action, pressed);

    public void Dispose()
    {
        _disposed = true;
        DeviceList.Local.Changed -= OnUsbDevicesChanged;
        _work.CompleteAdding();
        foreach (var provider in _providers)
            SafeRun(provider.Dispose);
    }
}

/// <summary>What a <see cref="DeviceProvider"/> may ask of the monitor that owns it.</summary>
public interface IDeviceHost
{
    /// <summary>Queues work for the monitor thread. Use it from HID reader threads.</summary>
    void Enqueue(Action action);

    /// <summary>Tells the app that the device list or a device's state changed.</summary>
    void Publish();

    void RaiseDpiChanged(DeviceState device, int before, int after);

    void RaiseButtonAction(DeviceState device, string action, bool pressed);

    IDeviceSettingsStore? SettingsStore { get; }
}

/// <summary>
/// Finds and talks to the devices of one brand or protocol. Every method runs on the monitor
/// thread, so providers don't need locks.
/// </summary>
public abstract class DeviceProvider(IDeviceHost host) : IDisposable
{
    protected IDeviceHost Host { get; } = host;

    public abstract IEnumerable<ManagedDevice> Devices { get; }

    /// <summary>Adds devices that were plugged in and drops ones that were unplugged.</summary>
    public abstract void Scan();

    /// <summary>Called every minute: re-read anything the device doesn't report by itself.</summary>
    public abstract void Poll();

    /// <summary>Scans, then re-reads everything from every device.</summary>
    public abstract void Refresh();

    public virtual void Dispose() { }

    /// <summary>Adds what this provider knows about its devices to a diagnostic report.</summary>
    public virtual void Describe(StringBuilder report) { }

    protected void Update(ManagedDevice device, DeviceState state)
    {
        if (state == device.State) return;
        device.State = state;
        Host.Publish();
    }
}

/// <summary>One connected device. Settings it doesn't support throw <see cref="NotSupportedException"/>.</summary>
public abstract class ManagedDevice(DeviceState state)
{
    public DeviceState State { get; set; } = state;

    public virtual void SetDpi(int dpi) => throw new NotSupportedException($"{State.Name} has no adjustable DPI.");

    public virtual void SelectDpiSlot(int slot) => throw new NotSupportedException($"{State.Name} has no DPI slots.");

    public virtual void SetKeycode(Via.KeyPosition key, ushort keycode) => throw new NotSupportedException($"{State.Name}'s keys can't be changed.");

    public virtual void ResetKeymap() => throw new NotSupportedException($"{State.Name}'s keys can't be changed.");

    public virtual void SetLighting(LightingSettings lighting) => throw new NotSupportedException($"{State.Name}'s lighting can't be changed.");

    public virtual void SetWheelMode(WheelMode mode, int? threshold, int? torque) => throw new NotSupportedException($"{State.Name} has no SmartShift.");

    public virtual void SetWheelInverted(bool inverted) => throw new NotSupportedException($"{State.Name}'s scroll direction can't be changed.");

    public virtual void SetThumbWheelInverted(bool inverted) => throw new NotSupportedException($"{State.Name} has no thumb wheel.");

    public virtual void SetButton(int cid, ButtonAssignment assignment) => throw new NotSupportedException($"{State.Name}'s buttons can't be changed here.");
}
