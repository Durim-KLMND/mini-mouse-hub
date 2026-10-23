using System.Collections.Concurrent;
using HidSharp;

namespace MiniMouseHub.Core;

/// <summary>A snapshot of what the app knows about one device, whatever its brand.</summary>
/// <param name="Vendor">The brand, such as "Logitech" or "Glorious".</param>
/// <param name="Lighting">The keyboard's RGB settings, when it has lighting the app can control.</param>
/// <param name="Note">Something the user should know about this device, such as why its settings can't be changed.</param>
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
    string? Note = null)
{
    public bool HasDpi => DpiCapabilities != null;
    public bool HasLighting => Lighting != null;
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

    /// <summary>Changes a keyboard's lighting and saves it on the keyboard.</summary>
    public Task SetLightingAsync(string deviceId, LightingSettings lighting) => RunOnDevice(deviceId, d => d.SetLighting(lighting));

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
        catch (Exception) { }
    }

    public void Publish()
    {
        Devices = _providers.SelectMany(p => p.Devices).Select(d => d.State).ToArray();
        DevicesChanged?.Invoke(Devices);
    }

    public void RaiseDpiChanged(DeviceState device, int before, int after) => DpiChangedOnDevice?.Invoke(device, before, after);

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

    public virtual void SetLighting(LightingSettings lighting) => throw new NotSupportedException($"{State.Name}'s lighting can't be changed.");
}
