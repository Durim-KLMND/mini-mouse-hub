using System.Runtime.InteropServices;
using System.Windows;
using MiniMouseHub.Core;

namespace MiniMouseHub.App;

public partial class App : Application
{
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(4);

    private const string ShowWindowEventName = "MiniMouseHub.ShowWindow";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showWindowRequest;
    private DeviceMonitor? _monitor;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly Updater _updater = new();
    private SettingsSync? _sync;
    private readonly Dictionary<string, LowBatteryAlert> _lowBattery = new();
    private System.Windows.Threading.DispatcherTimer? _updateTimer;
    private bool _firstScanDone;
    private bool _installingUpdate;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var justUpdated = e.Args.Contains("--updated");

        // After an update the previous copy may still be closing, so wait for it.
        _singleInstance = new Mutex(false, "MiniMouseHub.SingleInstance");
        bool isFirst;
        try { isFirst = _singleInstance.WaitOne(justUpdated ? TimeSpan.FromSeconds(15) : TimeSpan.Zero); }
        catch (AbandonedMutexException) { isFirst = true; }
        if (!isFirst)
        {
            // Ask the running copy to bring its window forward, then leave quietly.
            AskRunningCopyToShowWindow();
            _singleInstance = null;
            Shutdown();
            return;
        }
        ListenForShowWindowRequests();

        Task.Run(Updater.CleanUpOldVersion);
        StartWithWindows.RefreshPath();
        Installation.RefreshInstalledVersion();

        Theme.Initialize(_settings.Theme);
        _monitor = new DeviceMonitor { SettingsStore = new DeviceSettingsStore(_settings, Dispatcher) };
        // Run straight away on the monitor thread, so a held button feels like a real one.
        _monitor.ButtonActionTriggered += (_, action, pressed) => ButtonActions.Run(action, pressed);
        _sync = new SettingsSync(_settings);
        _sync.Pulled += () =>
        {
            Theme.Set(_settings.Theme);
            _window?.Redraw();
        };
        _window = new MainWindow(_monitor, _settings, CheckForUpdatesFromSettings, _sync);
        _tray = new TrayIcon(ShowWindow, Quit, _settings.TrayShowsBattery);
        // Covers both the Settings page and settings pulled from GitHub.
        AppSettings.Saved += () => Dispatcher.BeginInvoke(() =>
        {
            if (_tray != null) _tray.ShowBattery = _settings.TrayShowsBattery;
        });

        _monitor.DevicesChanged += devices => Dispatcher.BeginInvoke(() => OnDevicesChanged(devices));
        _monitor.FirstScanCompleted += () => Dispatcher.BeginInvoke(() =>
        {
            _firstScanDone = true;
            OnDevicesChanged(_monitor.Devices);
        });
        _monitor.DpiChangedOnDevice += (device, before, after) => Dispatcher.BeginInvoke(() =>
        {
            if (_settings.DpiNotifications)
                _tray.Notify($"{device.DisplayName}: DPI changed", $"{before} → {after} DPI");
        });

        if (!e.Args.Contains("--minimized"))
            ShowWindow();
        if (justUpdated)
            _tray.Notify("Mini mouse hub updated", $"You're now on version {Updater.CurrentVersion}.");
        _monitor.Start();

        _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = UpdateCheckInterval };
        _updateTimer.Tick += async (_, _) =>
        {
            await _sync.SyncAsync();
            await AutoUpdate();
        };
        _updateTimer.Start();
        Dispatcher.BeginInvoke(async () =>
        {
            await _sync.SyncAsync();
            await AutoUpdate();
        });
    }

    private void OnDevicesChanged(IReadOnlyList<DeviceState> devices)
    {
        devices = WithTimeLeft(devices);
        _window?.Render(devices, _firstScanDone);
        _tray?.Update(devices);

        foreach (var device in devices)
        {
            if (device.Battery is not { } battery) continue;
            if (!_lowBattery.TryGetValue(device.Id, out var alert))
                _lowBattery[device.Id] = alert = new LowBatteryAlert(20, 10, 5);
            if (alert.Check(battery) is { } threshold && _settings.LowBatteryNotifications)
                _tray?.Notify($"{device.DisplayName}: battery low",
                    $"{battery.Percent}% left{(device.TimeLeft is { } left ? $", {left.Describe().ToLowerInvariant()}" : "")}. Time to charge it soon.",
                    warning: threshold <= 10);
        }
    }

    /// <summary>Records each battery reading and adds the estimated time left to the devices.</summary>
    private IReadOnlyList<DeviceState> WithTimeLeft(IReadOnlyList<DeviceState> devices)
    {
        var now = DateTimeOffset.Now;
        var changed = false;
        var result = devices.Select(device =>
        {
            if (device.Battery is not { } battery) return device;
            // By model name, like device settings, so it survives the receiver moving to another USB port.
            if (!_settings.BatteryHistory.TryGetValue(device.Name, out var history))
                _settings.BatteryHistory[device.Name] = history = new BatteryHistory();
            // A sleeping device shows the level it last reported, which says nothing new.
            if (device.Awake)
                changed |= BatteryEstimator.Record(history, battery, now);
            return device with { TimeLeft = BatteryEstimator.Estimate(history, battery, now) };
        }).ToList();
        if (changed)
            _settings.Save();
        return result;
    }

    // ---- Updates ----

    private async Task AutoUpdate()
    {
        if (!_settings.AutoUpdate || _installingUpdate) return;
        try
        {
            var release = await _updater.CheckAsync();
            if (release != null)
                await Install(release);
        }
        catch (Exception)
        {
            // Offline or GitHub unavailable; try again at the next check.
        }
    }

    private async Task<string> CheckForUpdatesFromSettings()
    {
        var release = await _updater.CheckAsync();
        if (release == null)
            return $"You're up to date (version {Updater.CurrentVersion}).";
        await Install(release);
        return $"Installing version {release.Version}…";
    }

    private async Task Install(Updater.Release release)
    {
        if (_installingUpdate) return;
        _installingUpdate = true;
        try
        {
            var windowHidden = _window?.IsVisible != true;
            _singleInstance?.ReleaseMutex();
            _singleInstance?.Dispose();
            _singleInstance = null;
            await _updater.InstallAsync(release, startMinimized: windowHidden);
            Quit();
        }
        catch (Exception)
        {
            _installingUpdate = false;
            throw;
        }
    }

    // ---- Window and exit ----

    private void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    // A second launch sets this event instead of starting another copy.
    private void ListenForShowWindowRequests()
    {
        _showWindowRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        var request = _showWindowRequest;
        new Thread(() =>
        {
            try
            {
                while (request.WaitOne())
                    Dispatcher.BeginInvoke(ShowWindow);
            }
            catch (ObjectDisposedException) { }
        }) { IsBackground = true, Name = "Show window requests" }.Start();
    }

    private static void AskRunningCopyToShowWindow()
    {
        try
        {
            // We were just launched, so we may bring a window to the front; let the running copy do it instead.
            AllowSetForegroundWindow(AsfwAny);
            using var request = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
            request.Set();
        }
        catch (Exception)
        {
            // Nothing useful to do; the running copy is still in the tray.
        }
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private void Quit()
    {
        _window?.AllowClose();
        _window?.Close();
        _tray?.Dispose();
        _monitor?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _monitor?.Dispose();
        _showWindowRequest?.Dispose();
        try { _singleInstance?.ReleaseMutex(); } catch (ApplicationException) { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
