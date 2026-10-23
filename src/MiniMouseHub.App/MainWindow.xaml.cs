using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MiniMouseHub.Core;

namespace MiniMouseHub.App;

public partial class MainWindow : Window
{
    private enum PageKind { Home, Device, Settings, KeyTester }

    private readonly DeviceMonitor _monitor;
    private readonly AppSettings _settings;
    private readonly Func<Task<string>> _checkForUpdates;
    private readonly SettingsSync _sync;
    private IReadOnlyList<DeviceState> _devices = [];
    private bool _firstScanDone;
    private bool _allowClose;

    private PageKind _page = PageKind.Home;
    private string? _deviceId;
    private DevicePage? _devicePage;
    private KeyTesterPage? _keyTester;

    public MainWindow(DeviceMonitor monitor, AppSettings settings, Func<Task<string>> checkForUpdates, SettingsSync sync)
    {
        _monitor = monitor;
        _settings = settings;
        _checkForUpdates = checkForUpdates;
        _sync = sync;
        InitializeComponent();
        FooterText.Text = $"Version {Updater.CurrentVersion}";
        StateChanged += (_, _) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        ShowHome();
        Theme.Changed += Redraw;
    }

    /// <summary>Pages built in code take their colors when they are built, so build the current one again.</summary>
    internal void Redraw()
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
            case PageKind.KeyTester when _devices.FirstOrDefault(d => d.Id == _deviceId) is { } device:
                ShowKeyTester(device);
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
            case PageKind.KeyTester when devices.All(d => d.Id != _deviceId):
                ShowHome(); // unplugged
                break;
        }
    }

    // ---- Navigation ----

    private void ShowHome()
    {
        _page = PageKind.Home;
        _devicePage = null;
        _keyTester = null;
        BackButton.Visibility = Visibility.Collapsed;
        TitleText.Text = "Mini mouse hub";
        Page.Content = HomePage.Build(_devices, _firstScanDone, ShowDevice);
    }

    private void ShowDevice(DeviceState device)
    {
        _page = PageKind.Device;
        _deviceId = device.Id;
        _keyTester = null;
        BackButton.Visibility = Visibility.Visible;
        TitleText.Text = device.DisplayName;
        _devicePage = new DevicePage(_monitor, device, () => ShowKeyTester(device));
        Page.Content = _devicePage.Root;
    }

    private void ShowKeyTester(DeviceState device)
    {
        _page = PageKind.KeyTester;
        _deviceId = device.Id;
        _devicePage = null;
        BackButton.Visibility = Visibility.Visible;
        TitleText.Text = $"{device.DisplayName}: key tester";
        _keyTester = new KeyTesterPage();
        Page.Content = _keyTester.Root;
        Keyboard.ClearFocus(); // so no button reacts to Space or Enter
    }

    /// <summary>From the key tester, go back to its device; from anywhere else, go home.</summary>
    private void GoBack()
    {
        if (_page == PageKind.KeyTester && _devices.FirstOrDefault(d => d.Id == _deviceId) is { } device)
            ShowDevice(device);
        else
            ShowHome();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_keyTester != null)
            _keyTester.OnKeyDown(e);
        else
            base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (_keyTester != null)
            _keyTester.OnKeyUp(e);
        else
            base.OnPreviewKeyUp(e);
    }

    private void ShowSettings()
    {
        _page = PageKind.Settings;
        _devicePage = null;
        _keyTester = null;
        BackButton.Visibility = Visibility.Visible;
        TitleText.Text = "Settings";
        Page.Content = SettingsPage.Build(_settings, _checkForUpdates, _sync);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private void Refresh_Click(object sender, RoutedEventArgs e) => _monitor.Refresh();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.XButton1 && _page != PageKind.Home)
            GoBack(); // mouse back button
    }

    internal static Brush Res(string key) => (Brush)Application.Current.Resources[key];
}
