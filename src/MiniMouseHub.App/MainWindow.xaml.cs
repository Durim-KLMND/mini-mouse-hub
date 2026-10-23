using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MiniMouseHub.Core;

namespace MiniMouseHub.App;

public partial class MainWindow : Window
{
    private enum PageKind { Home, Device, Settings }

    private readonly DeviceMonitor _monitor;
    private readonly AppSettings _settings;
    private readonly Func<Task<string>> _checkForUpdates;
    private IReadOnlyList<DeviceState> _devices = [];
    private bool _firstScanDone;
    private bool _allowClose;

    private PageKind _page = PageKind.Home;
    private string? _deviceId;
    private DevicePage? _devicePage;

    public MainWindow(DeviceMonitor monitor, AppSettings settings, Func<Task<string>> checkForUpdates)
    {
        _monitor = monitor;
        _settings = settings;
        _checkForUpdates = checkForUpdates;
        InitializeComponent();
        FooterText.Text = $"Version {Updater.CurrentVersion}";
        StateChanged += (_, _) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        ShowHome();
        Theme.Changed += Redraw;
    }

    /// <summary>Pages built in code take their colors when they are built, so build the current one again.</summary>
    private void Redraw()
    {
        switch (_page)
        {
            case PageKind.Home:
                ShowHome();
                break;
            case PageKind.Settings:
                ShowSettings();
                break;
            case PageKind.Device when _devices.FirstOrDefault(d => d.Id == _deviceId) is { } device:
                ShowDevice(device);
                break;
        }
    }

    /// <summary>Lets the next Close() really close the window instead of hiding it.</summary>
    public void AllowClose() => _allowClose = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    public void Render(IReadOnlyList<DeviceState> devices, bool firstScanDone)
    {
        _devices = devices;
        _firstScanDone = firstScanDone;
        switch (_page)
        {
            case PageKind.Home:
                ShowHome();
                break;
            case PageKind.Device:
                var device = devices.FirstOrDefault(d => d.Id == _deviceId);
                if (device == null)
                    ShowHome(); // unplugged
                else
                    _devicePage?.Update(device);
                break;
        }
    }

    // ---- Navigation ----

    private void ShowHome()
    {
        _page = PageKind.Home;
        _devicePage = null;
        BackButton.Visibility = Visibility.Collapsed;
        TitleText.Text = "Mini Mouse Hub";
        Page.Content = HomePage.Build(_devices, _firstScanDone, ShowDevice);
    }

    private void ShowDevice(DeviceState device)
    {
        _page = PageKind.Device;
        _deviceId = device.Id;
        BackButton.Visibility = Visibility.Visible;
        TitleText.Text = device.DisplayName;
        _devicePage = new DevicePage(_monitor, device);
        Page.Content = _devicePage.Root;
    }

    private void ShowSettings()
    {
        _page = PageKind.Settings;
        _devicePage = null;
        BackButton.Visibility = Visibility.Visible;
        TitleText.Text = "Settings";
        Page.Content = SettingsPage.Build(_settings, _checkForUpdates);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowHome();
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private void Refresh_Click(object sender, RoutedEventArgs e) => _monitor.Refresh();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.XButton1 && _page != PageKind.Home)
            ShowHome(); // mouse back button
    }

    internal static Brush Res(string key) => (Brush)Application.Current.Resources[key];
}
