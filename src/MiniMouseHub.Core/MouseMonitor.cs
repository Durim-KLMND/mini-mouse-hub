using System.Collections.Concurrent;
using MiniMouseHub.Core.Hidpp;

namespace MiniMouseHub.Core;

/// <summary>A snapshot of what the app knows about the mouse.</summary>
public record MouseState(
    bool ReceiverFound,
    bool MouseAwake,
    string? Name,
    BatteryInfo? Battery,
    int? Dpi,
    IReadOnlyList<int>? DpiSlots,
    int? ActiveDpiSlot,
    DpiCapabilities? DpiCapabilities,
    OnboardMode? Mode)
{
    public static readonly MouseState NoReceiver = new(false, false, null, null, null, null, null, null, null);
}

/// <summary>
/// Keeps a connection to the first Logitech mouse found, polls its battery, and listens for the
/// notifications it sends (DPI button presses, battery changes, sleep and wake).
/// All device traffic runs on one background thread; events are raised from that thread.
/// </summary>
public sealed class MouseMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(5);

    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;
    private volatile bool _disposed;

    private HidppChannel? _channel;
    private LogitechDevice? _mouse;
    private byte? _dpiFeatureIndex, _batteryFeatureIndex, _onboardFeatureIndex;
    private bool _suppressNextDpiEvent;

    public MouseState State { get; private set; } = MouseState.NoReceiver;

    /// <summary>Raised whenever any part of <see cref="State"/> changes.</summary>
    public event Action<MouseState>? StateChanged;

    /// <summary>Raised when the DPI changes on the mouse itself (old, new), such as with the DPI button.</summary>
    public event Action<int, int>? DpiChangedOnDevice;

    public MouseMonitor()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Mouse monitor" };
    }

    public void Start() => _thread.Start();

    /// <summary>Re-reads everything from the mouse.</summary>
    public void Refresh() => Enqueue(RefreshAll);

    private void Enqueue(Action action)
    {
        try { _work.TryAdd(action); }
        catch (InvalidOperationException) { } // disposed
    }

    /// <summary>
    /// Sets the DPI. When the mouse runs from its onboard profile, the value is saved into the
    /// profile's current DPI slot; otherwise it is set directly.
    /// </summary>
    public Task SetDpiAsync(int dpi)
    {
        var done = new TaskCompletionSource();
        Enqueue(() =>
        {
            try
            {
                var mouse = _mouse ?? throw new HidppException("No mouse connected.");
                _suppressNextDpiEvent = true;
                if (mouse.HasFeature(FeatureId.OnboardProfiles) && mouse.GetOnboardMode() == OnboardMode.Onboard)
                {
                    var store = new OnboardProfileStore(mouse);
                    store.SetDpiInActiveProfile(store.GetCurrentDpiIndex(), dpi);
                }
                else
                    mouse.SetDpi(dpi);
                RefreshDpi(raiseEvent: false);
                done.SetResult();
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        });
        return done.Task;
    }

    private void Run()
    {
        while (!_disposed)
        {
            if (_channel == null)
            {
                Connect();
                if (_channel == null)
                {
                    Wait(ReconnectInterval);
                    continue;
                }
            }

            if (!_work.TryTake(out var action, PollInterval))
                action = _mouse == null ? RefreshAll : RefreshBattery;
            Execute(action);
        }
    }

    private void Wait(TimeSpan time)
    {
        // Still run queued work (like a refresh request) while waiting.
        if (_work.TryTake(out var action, time))
            Execute(action);
    }

    private void Execute(Action action)
    {
        try
        {
            action();
        }
        catch (HidppTimeoutException)
        {
            // The mouse went to sleep or out of range; the receiver tells us when it's back.
            if (State.MouseAwake)
                Publish(State with { MouseAwake = false });
        }
        catch (HidppException)
        {
        }
        catch (Exception) when (_channel != null)
        {
            // Most likely the receiver was unplugged.
            Disconnect();
        }
    }

    private void Connect()
    {
        try
        {
            foreach (var channel in HidppDiscovery.OpenAll())
            {
                if (_channel == null)
                {
                    _channel = channel;
                    _channel.ReportReceived += OnReport;
                }
                else
                {
                    channel.Dispose();
                }
            }
        }
        catch (Exception)
        {
            _channel = null;
        }

        if (_channel == null)
        {
            Publish(MouseState.NoReceiver);
            return;
        }
        Execute(RefreshAll);
    }

    private void Disconnect()
    {
        if (_channel != null)
        {
            _channel.ReportReceived -= OnReport;
            _channel.Dispose();
        }
        _channel = null;
        _mouse = null;
        Publish(MouseState.NoReceiver);
    }

    private void RefreshAll()
    {
        if (_channel == null) return;
        var devices = LogitechDevice.Enumerate(_channel);
        _mouse = devices.FirstOrDefault(d => d.HasAdjustableDpi) ?? devices.FirstOrDefault();
        if (_mouse == null)
        {
            Publish(new MouseState(true, false, null, null, null, null, null, null, null));
            return;
        }

        _dpiFeatureIndex = _mouse.GetFeatureIndex(FeatureId.ExtendedAdjustableDpi) ?? _mouse.GetFeatureIndex(FeatureId.AdjustableDpi);
        _batteryFeatureIndex = _mouse.GetFeatureIndex(FeatureId.UnifiedBattery)
            ?? _mouse.GetFeatureIndex(FeatureId.BatteryVoltage) ?? _mouse.GetFeatureIndex(FeatureId.BatteryStatus);
        _onboardFeatureIndex = _mouse.GetFeatureIndex(FeatureId.OnboardProfiles);

        string? name = null;
        try { name = _mouse.GetName(); } catch (HidppException) { }

        Publish(State with
        {
            ReceiverFound = true,
            MouseAwake = true,
            Name = name ?? "Logitech mouse",
            DpiCapabilities = _mouse.HasAdjustableDpi ? _mouse.GetDpiCapabilities() : null,
        });
        RefreshBattery();
        RefreshDpi(raiseEvent: false);
    }

    private void RefreshBattery()
    {
        if (_mouse == null) return;
        var battery = _mouse.GetBattery();
        Publish(State with { MouseAwake = true, Battery = battery });
    }

    private void RefreshDpi(bool raiseEvent)
    {
        if (_mouse == null || !_mouse.HasAdjustableDpi) return;
        var old = State.Dpi;
        var dpi = _mouse.GetDpi().Current;

        IReadOnlyList<int>? slots = null;
        int? activeSlot = null;
        OnboardMode? mode = null;
        if (_onboardFeatureIndex != null)
        {
            mode = _mouse.GetOnboardMode();
            if (mode == OnboardMode.Onboard)
            {
                var store = new OnboardProfileStore(_mouse);
                var description = store.GetDescription();
                var current = store.GetCurrentProfile();
                var header = store.GetProfileHeaders(description).FirstOrDefault(h => h.Number == current);
                if (header != null)
                    slots = store.ReadProfile(header, description).DpiSlots.Where(d => d is not (0 or 0xFFFF)).ToArray();
                activeSlot = store.GetCurrentDpiIndex();
            }
        }

        Publish(State with { MouseAwake = true, Dpi = dpi, DpiSlots = slots, ActiveDpiSlot = activeSlot, Mode = mode });
        if (raiseEvent && old is { } before && before != dpi && !_suppressNextDpiEvent)
            DpiChangedOnDevice?.Invoke(before, dpi);
        _suppressNextDpiEvent = false;
    }

    /// <summary>Runs on the HID reader thread: only decide what to re-read, never talk to the device here.</summary>
    private void OnReport(byte[] report)
    {
        var mouse = _mouse;
        if (report.Length < 4) return;

        // Receiver notification 0x41: a paired device connected or disconnected (woke up or went to sleep).
        if (report[0] == HidppConstants.ShortReportId && report[2] == 0x41)
        {
            var linkLost = (report[4] & 0x40) != 0;
            if (linkLost)
                Enqueue(() => Publish(State with { MouseAwake = false }));
            else
                Enqueue(RefreshAll);
            return;
        }

        if (mouse == null || report[1] != mouse.DeviceIndex || (report[3] & 0x0F) != 0)
            return; // not an unsolicited event from the mouse

        var feature = report[2];
        if (feature == _dpiFeatureIndex || feature == _onboardFeatureIndex)
            Enqueue(() => RefreshDpi(raiseEvent: true));
        else if (feature == _batteryFeatureIndex)
            Enqueue(RefreshBattery);
    }

    private void Publish(MouseState state)
    {
        if (state == State) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        _disposed = true;
        _work.CompleteAdding();
        _channel?.Dispose();
    }
}
