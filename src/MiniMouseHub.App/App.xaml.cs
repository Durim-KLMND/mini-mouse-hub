using System.Windows;
using MiniMouseHub.Core;

namespace MiniMouseHub.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private MouseMonitor? _monitor;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private readonly LowBatteryAlert _lowBattery = new(20, 10, 5);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, "MiniMouseHub.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Mini Mouse Hub is already running. Look for it in the system tray.", "Mini Mouse Hub");
            Shutdown();
            return;
        }

        _monitor = new MouseMonitor();
        _window = new MainWindow(_monitor);
        _tray = new TrayIcon(ShowWindow, Quit);

        _monitor.StateChanged += state => Dispatcher.BeginInvoke(() => OnStateChanged(state));
        _monitor.DpiChangedOnDevice += (before, after) => Dispatcher.BeginInvoke(() =>
            _tray.Notify("DPI changed", $"{before} → {after} DPI"));

        var startHidden = e.Args.Contains("--minimized");
        if (!startHidden)
            ShowWindow();
        _monitor.Start();
    }

    private void OnStateChanged(MouseState state)
    {
        _window?.Render(state);
        _tray?.Update(state);

        if (state.Battery is { } battery && _lowBattery.Check(battery) is { } threshold)
            _tray?.Notify("Mouse battery low", $"{state.Name ?? "Your mouse"} is at {battery.Percent}%. Time to charge it soon.", warning: threshold <= 10);
    }

    private void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

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
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
