using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MiniMouseHub.Core;
using static MiniMouseHub.App.MainWindow;

namespace MiniMouseHub.App;

/// <summary>Small helpers for building pages in code.</summary>
internal static class Ui
{
    public static TextBlock Text(string text, double size = 14, string brush = "Text", FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Res(brush),
        FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Label(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["Label"],
    };

    public static Border Card(UIElement child) => new()
    {
        Style = (Style)Application.Current.Resources["Card"],
        Child = child,
    };

    public static string BatteryText(BatteryInfo? battery) => battery switch
    {
        null => "–",
        { Percent: { } p } => $"{p}%",
        _ => battery.Level.ToString(),
    };

    public static string ChargingText(BatteryInfo battery) => battery.Charging switch
    {
        ChargingState.Charging => "Charging",
        ChargingState.ChargingSlow => "Charging slowly",
        ChargingState.Full => "Fully charged",
        ChargingState.NotCharging => "Plugged in, not charging",
        ChargingState.Error => "Charging error",
        _ => "On battery",
    };

    public static string BatteryColor(BatteryInfo? battery) => battery?.Percent switch
    {
        null => "Muted",
        <= 10 => "Bad",
        <= 20 => "Warn",
        _ => "Good",
    };

    /// <summary>A thin bar filled to the battery level.</summary>
    public static Grid BatteryBar(BatteryInfo? battery, double height = 6)
    {
        var percent = Math.Clamp(battery?.Percent ?? 0, 0, 100);
        var grid = new Grid { Height = height };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(percent, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - percent, GridUnitType.Star) });
        var track = new Border { CornerRadius = new CornerRadius(height / 2), Background = Res("Border") };
        Grid.SetColumnSpan(track, 2);
        grid.Children.Add(track);
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(height / 2), Background = Res(BatteryColor(battery)) });
        return grid;
    }

    public static string KindName(DeviceKind kind) => kind switch
    {
        DeviceKind.Unknown => "Device",
        DeviceKind.RemoteControl => "Remote",
        DeviceKind.SteeringWheel => "Wheel",
        _ => kind.ToString(),
    };
}

/// <summary>The start screen: a tile for each connected Logitech device.</summary>
internal static class HomePage
{
    public static UIElement Build(IReadOnlyList<DeviceState> devices, bool firstScanDone, Action<DeviceState> open)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text("Your devices", 20, weight: FontWeights.SemiBold));
        panel.Children.Add(new Border { Height = 16 });

        if (devices.Count == 0)
        {
            panel.Children.Add(Ui.Text(firstScanDone
                ? "No Logitech devices found. Plug in a Lightspeed receiver or a cable, move the mouse to wake it, and make sure G HUB is closed."
                : "Looking for Logitech devices…", 14, "Muted"));
            return panel;
        }

        var tiles = new WrapPanel();
        foreach (var device in devices.OrderBy(d => d.Kind).ThenBy(d => d.Name))
            tiles.Children.Add(Tile(device, open));
        panel.Children.Add(tiles);
        return panel;
    }

    private static Button Tile(DeviceState device, Action<DeviceState> open)
    {
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var art = DeviceArt.For(device);
        art.Margin = new Thickness(18, 4, 18, 14);
        art.Opacity = device.Awake ? 1 : 0.45;
        content.Children.Add(art);

        var info = new StackPanel();
        Grid.SetRow(info, 1);
        info.Children.Add(Ui.Text(device.Name, 15, weight: FontWeights.SemiBold));
        var status = device.Awake
            ? $"{Ui.KindName(device.Kind)} · {(device.IsWired ? "Cable" : "Wireless")}"
            : "Asleep. Move it to wake it";
        info.Children.Add(Ui.Text(status, 12, "Muted"));

        if (device.Battery != null)
        {
            var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var bar = Ui.BatteryBar(device.Battery);
            bar.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(bar);
            var pct = Ui.Text(Ui.BatteryText(device.Battery) + (device.Battery.IsCharging ? " ⚡" : ""), 12);
            pct.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(pct, 1);
            row.Children.Add(pct);
            info.Children.Add(row);
        }

        var tile = new Button { Style = (Style)Application.Current.Resources["Tile"], Content = content };
        tile.Click += (_, _) => open(device);
        return tile;
    }
}

/// <summary>Details and settings for one device. Built once, then updated in place so typing isn't interrupted.</summary>
internal sealed class DevicePage
{
    private readonly DeviceMonitor _monitor;
    private DeviceState _device;

    private readonly ContentControl _art = new();
    private readonly TextBlock _status = Ui.Text("", 12, "Muted");
    private readonly TextBlock _batteryPercent = Ui.Text("–", 34, weight: FontWeights.SemiBold);
    private readonly TextBlock _charging = Ui.Text("", 13, "Muted");
    private readonly ContentControl _batteryBar = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _dpiValue = Ui.Text("–", 34, weight: FontWeights.SemiBold);
    private readonly WrapPanel _slots = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _slotsLabel = Ui.Label("DPI slots: click one to switch, or press the DPI button on the mouse");
    private readonly TextBox _dpiInput = new() { Padding = new Thickness(8, 6, 8, 6) };
    private readonly Button _apply = new() { Content = "Apply", Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _dpiHint = Ui.Text("", 12, "Muted");
    private readonly Border _dpiCard;

    public UIElement Root { get; }

    public DevicePage(DeviceMonitor monitor, DeviceState device)
    {
        _monitor = monitor;
        _device = device;

        _dpiInput.Background = Res("Background");
        _dpiInput.Foreground = Res("Text");
        _dpiInput.BorderBrush = Res("Border");
        _dpiInput.CaretBrush = Res("Text");
        _dpiInput.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(); };
        _apply.Click += (_, _) => Apply();
        _dpiHint.Margin = new Thickness(0, 6, 0, 0);

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        _art.Height = 260;
        left.Children.Add(_art);
        _status.Margin = new Thickness(0, 12, 0, 0);
        _status.TextAlignment = TextAlignment.Center;
        left.Children.Add(_status);
        layout.Children.Add(left);

        var right = new StackPanel();
        Grid.SetColumn(right, 1);

        var battery = new StackPanel();
        battery.Children.Add(Ui.Label("BATTERY"));
        var batteryRow = new Grid();
        batteryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        batteryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        batteryRow.Children.Add(_batteryPercent);
        _charging.VerticalAlignment = VerticalAlignment.Center;
        _charging.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_charging, 1);
        batteryRow.Children.Add(_charging);
        battery.Children.Add(batteryRow);
        battery.Children.Add(_batteryBar);
        right.Children.Add(Ui.Card(battery));

        var dpi = new StackPanel();
        dpi.Children.Add(Ui.Label("DPI"));
        dpi.Children.Add(_dpiValue);
        _slotsLabel.Margin = new Thickness(0, 12, 0, 0);
        dpi.Children.Add(_slotsLabel);
        dpi.Children.Add(_slots);
        var setLabel = Ui.Label("Set the current DPI");
        setLabel.Margin = new Thickness(0, 12, 0, 6);
        dpi.Children.Add(setLabel);
        var inputRow = new Grid();
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputRow.Children.Add(_dpiInput);
        Grid.SetColumn(_apply, 1);
        inputRow.Children.Add(_apply);
        dpi.Children.Add(inputRow);
        dpi.Children.Add(_dpiHint);
        _dpiCard = Ui.Card(dpi);
        right.Children.Add(_dpiCard);

        layout.Children.Add(right);
        Root = layout;
        Update(device);
    }

    public void Update(DeviceState device)
    {
        var artChanged = device.Kind != _device.Kind || device.Name != _device.Name || _art.Content == null;
        _device = device;
        if (artChanged)
            _art.Content = DeviceArt.For(device);
        _art.Opacity = device.Awake ? 1 : 0.45;

        _status.Text = device.Awake
            ? $"{Ui.KindName(device.Kind)} · {(device.IsWired ? "connected by cable" : "wireless")}"
            : "Asleep or switched off. Move it to wake it.";

        _batteryPercent.Text = Ui.BatteryText(device.Battery);
        _charging.Text = device.Battery != null ? Ui.ChargingText(device.Battery) : "Not reported by this device";
        _batteryBar.Content = Ui.BatteryBar(device.Battery, 8);

        _dpiCard.Visibility = device.HasDpi ? Visibility.Visible : Visibility.Collapsed;
        _dpiValue.Text = device.Dpi?.ToString() ?? "–";
        _slots.Children.Clear();
        _slotsLabel.Visibility = device.DpiSlots is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (device.DpiSlots != null)
        {
            for (var i = 0; i < device.DpiSlots.Count; i++)
            {
                var slot = i;
                var active = slot == device.ActiveDpiSlot;
                var pill = new Button
                {
                    Style = (Style)Application.Current.Resources["Pill"],
                    Content = device.DpiSlots[i].ToString(),
                    Background = active ? Res("Accent") : Res("Border"),
                    Foreground = active ? Brushes.White : Res("Text"),
                    ToolTip = active ? "Current DPI slot" : "Switch to this DPI",
                };
                pill.Click += async (_, _) => await SelectSlot(slot);
                _slots.Children.Add(pill);
            }
        }

        _apply.IsEnabled = device.Awake && device.Dpi != null;
        if (device.DpiCapabilities is { } caps && _dpiHint.Tag == null)
        {
            var where = device.Mode == OnboardMode.Onboard ? "Saved to the highlighted slot on the mouse" : "Applied right away";
            _dpiHint.Text = $"{caps.Min}-{caps.Max}. {where}.";
        }
    }

    private async Task SelectSlot(int slot)
    {
        try
        {
            await _monitor.SelectDpiSlotAsync(_device.Id, slot);
        }
        catch (Exception ex)
        {
            ShowHint($"Couldn't switch DPI slot: {ex.Message}");
        }
    }

    private async void Apply()
    {
        if (!int.TryParse(_dpiInput.Text.Trim(), out var dpi))
        {
            ShowHint("Type a number, for example 1600.");
            return;
        }
        if (_device.DpiCapabilities is { } caps && !caps.Accepts(dpi))
        {
            ShowHint($"{dpi} isn't supported. Pick a value from {caps.Min} to {caps.Max}; above 1000 it goes in steps, such as 1600 or 1650.");
            return;
        }

        _apply.IsEnabled = false;
        ShowHint("Saving…");
        try
        {
            await _monitor.SetDpiAsync(_device.Id, dpi);
            _dpiInput.Clear();
            ShowHint($"Set to {dpi} DPI.");
        }
        catch (Exception ex)
        {
            ShowHint($"Couldn't change the DPI: {ex.Message}");
        }
        finally
        {
            _apply.IsEnabled = true;
        }
    }

    private void ShowHint(string text)
    {
        _dpiHint.Text = text;
        _dpiHint.Tag = "message"; // keep it until the page is reopened
    }
}

/// <summary>App settings.</summary>
internal static class SettingsPage
{
    public static UIElement Build(AppSettings settings, Func<Task<string>> checkForUpdates)
    {
        var panel = new StackPanel { MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };

        var general = new StackPanel();
        general.Children.Add(Ui.Label("GENERAL"));
        general.Children.Add(Toggle("Start with Windows (in the tray)", StartWithWindows.IsEnabled, on => StartWithWindows.IsEnabled = on));
        panel.Children.Add(Ui.Card(general));

        var notifications = new StackPanel();
        notifications.Children.Add(Ui.Label("NOTIFICATIONS"));
        notifications.Children.Add(Toggle("Warn me when a battery runs low (20%, 10% and 5%)", settings.LowBatteryNotifications, on => { settings.LowBatteryNotifications = on; settings.Save(); }));
        notifications.Children.Add(Toggle("Tell me when the DPI changes on the mouse", settings.DpiNotifications, on => { settings.DpiNotifications = on; settings.Save(); }));
        panel.Children.Add(Ui.Card(notifications));

        var updates = new StackPanel();
        updates.Children.Add(Ui.Label("UPDATES"));
        updates.Children.Add(Toggle("Install updates automatically", settings.AutoUpdate, on => { settings.AutoUpdate = on; settings.Save(); }));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var check = new Button { Content = "Check now" };
        var result = Ui.Text($"You have version {Updater.CurrentVersion}.", 12, "Muted");
        result.VerticalAlignment = VerticalAlignment.Center;
        result.Margin = new Thickness(12, 0, 0, 0);
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            result.Text = "Checking…";
            try { result.Text = await checkForUpdates(); }
            catch (Exception ex) { result.Text = $"Couldn't check for updates: {ex.Message}"; }
            finally { check.IsEnabled = true; }
        };
        row.Children.Add(check);
        row.Children.Add(result);
        updates.Children.Add(row);
        panel.Children.Add(Ui.Card(updates));

        var about = new StackPanel();
        about.Children.Add(Ui.Label("PICTURES"));
        about.Children.Add(Ui.Text("To show your own picture of a device, put a PNG named after it (for example \"PRO X 2.png\") in an \"images\" folder next to MiniMouseHub.exe.", 12, "Muted"));
        panel.Children.Add(Ui.Card(about));
        return panel;
    }

    private static CheckBox Toggle(string text, bool value, Action<bool> changed)
    {
        var box = new CheckBox { Content = text, IsChecked = value };
        box.Checked += (_, _) => changed(true);
        box.Unchecked += (_, _) => changed(false);
        return box;
    }
}
