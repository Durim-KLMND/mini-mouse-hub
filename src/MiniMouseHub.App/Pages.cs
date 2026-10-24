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
        var track = new Border { CornerRadius = new CornerRadius(height / 2), Background = Res("Track") };
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

    /// <summary>For example "Mouse · Wireless" or "Keyboard · connected by cable".</summary>
    public static string Describe(DeviceState device, bool longForm = false)
    {
        var connection = (device.Connection, device.IsWired) switch
        {
            (ConnectionKind.Bluetooth, _) => longForm ? "connected by Bluetooth" : "Bluetooth",
            (_, true) => longForm ? "connected by cable" : "Cable",
            _ => longForm ? "wireless" : "Wireless",
        };
        return $"{KindName(device.Kind)} · {connection}";
    }
}

/// <summary>The start screen: a tile for each connected device.</summary>
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
                ? "No devices found. Plug in a Lightspeed, Bolt or Unifying receiver, a cable or a VIA keyboard, or pair a Logitech mouse by Bluetooth. " +
                  "Move the mouse to wake it, and make sure G HUB and Logi Options+ are closed."
                : "Looking for devices…", 14, "Muted"));
            return panel;
        }

        var tiles = new WrapPanel();
        foreach (var device in devices.OrderBy(d => d.Kind).ThenBy(d => d.DisplayName))
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
        info.Children.Add(Ui.Text(device.DisplayName, 15, weight: FontWeights.SemiBold));
        var status = device.Awake ? Ui.Describe(device) : "Asleep. Move it to wake it";
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
        content.Children.Add(info);

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
    private readonly Button _apply = new() { Content = "Apply", Margin = new Thickness(8, 0, 0, 0), Style = (Style)Application.Current.Resources["PrimaryButton"] };
    private readonly TextBlock _dpiHint = Ui.Text("", 12, "Muted");
    private readonly Border _dpiCard;
    private readonly Border _batteryCard;
    private readonly TextBlock _note = Ui.Text("", 13);
    private readonly Border _noteCard;
    private readonly LightingCard _lighting;
    private readonly ScrollCard _scroll;
    private readonly ButtonsCard _buttons;
    private readonly Border _testerCard;

    public UIElement Root { get; }

    public DevicePage(DeviceMonitor monitor, DeviceState device, Action openKeyTester)
    {
        _monitor = monitor;
        _device = device;

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
        _noteCard = Ui.Card(_note);
        right.Children.Add(_noteCard);
        _batteryCard = Ui.Card(battery);
        right.Children.Add(_batteryCard);

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
        _scroll = new ScrollCard(monitor, device);
        right.Children.Add(_scroll.Root);
        _buttons = new ButtonsCard(monitor, device);
        right.Children.Add(_buttons.Root);
        _lighting = new LightingCard(monitor, device);
        right.Children.Add(_lighting.Root);

        var tester = new StackPanel();
        tester.Children.Add(Ui.Label("KEY TESTER"));
        var testerRow = new Grid();
        testerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        testerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var testerText = Ui.Text("Check that every key and the knob register, and spot keys that chatter.", 13, "Muted");
        testerText.VerticalAlignment = VerticalAlignment.Center;
        testerRow.Children.Add(testerText);
        var openTester = new Button { Content = "Open key tester", Margin = new Thickness(12, 0, 0, 0), Style = (Style)Application.Current.Resources["PrimaryButton"] };
        openTester.Click += (_, _) => openKeyTester();
        Grid.SetColumn(openTester, 1);
        testerRow.Children.Add(openTester);
        tester.Children.Add(testerRow);
        _testerCard = Ui.Card(tester);
        right.Children.Add(_testerCard);

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
            ? Ui.Describe(device, longForm: true)
            : "Asleep or switched off. Move it to wake it.";

        _noteCard.Visibility = device.Note != null ? Visibility.Visible : Visibility.Collapsed;
        _note.Text = device.Note ?? "";
        _lighting.Update(device);
        _scroll.Update(device);
        _buttons.Update(device);
        _testerCard.Visibility = device.Kind == DeviceKind.Keyboard ? Visibility.Visible : Visibility.Collapsed;

        // A wired device without a battery reading has no battery; a wireless one may just be asleep.
        _batteryCard.Visibility = device.Battery != null || !device.IsWired ? Visibility.Visible : Visibility.Collapsed;

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
                    Background = active ? Res("Accent") : Res("Background"),
                    Foreground = active ? Res("AccentText") : Res("Text"),
                    BorderBrush = active ? Res("Accent") : Res("Border"),
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

/// <summary>RGB controls for a keyboard. Changes are sent shortly after the user stops dragging, then saved on the keyboard.</summary>
internal sealed class LightingCard
{
    private static readonly TimeSpan SendDelay = TimeSpan.FromMilliseconds(300);

    private readonly DeviceMonitor _monitor;
    private DeviceState _device;
    private readonly CheckBox _on = new() { Content = "Lights on" };
    private readonly ComboBox _effect = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly Slider _brightness = NewSlider();
    private readonly Slider _speed = NewSlider();
    private readonly ColorPicker _color = new();
    private readonly TextBlock _hint = Ui.Text("", 12, "Muted");
    private readonly System.Windows.Threading.DispatcherTimer _sendTimer = new() { Interval = SendDelay };
    private IReadOnlyList<string>? _effectNames;
    private bool _updating;
    private bool _pending;

    public Border Root { get; }

    public LightingCard(DeviceMonitor monitor, DeviceState device)
    {
        _monitor = monitor;
        _device = device;

        var panel = new StackPanel();
        panel.Children.Add(Ui.Label("LIGHTING"));
        panel.Children.Add(_on);
        panel.Children.Add(Row("Effect", _effect));
        panel.Children.Add(Row("Brightness", _brightness));
        panel.Children.Add(Row("Speed", _speed));
        panel.Children.Add(Row("Color", _color.Root));
        _hint.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(_hint);
        Root = Ui.Card(panel);

        _on.Checked += (_, _) => Changed();
        _on.Unchecked += (_, _) => Changed();
        _effect.SelectionChanged += (_, _) => Changed();
        _speed.ValueChanged += (_, _) => Changed();
        _brightness.ValueChanged += (_, _) =>
        {
            // Brightness is the color's value, so the hex code follows it.
            _color.SetColor(_color.Hue, _color.Saturation, (int)Math.Round(_brightness.Value));
            Changed();
        };
        _color.Changed += valueChanged =>
        {
            if (valueChanged && (int)Math.Round(_brightness.Value) != _color.Value)
                _brightness.Value = _color.Value; // raises Changed itself
            else
                Changed();
        };
        _sendTimer.Tick += async (_, _) => await Send();

        Update(device);
    }

    private static Slider NewSlider() => new() { Minimum = 0, Maximum = 255, SmallChange = 1, LargeChange = 16, IsMoveToPointEnabled = true };

    private static StackPanel Row(string label, FrameworkElement control)
    {
        var row = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(Ui.Text(label, 12, "Muted"));
        row.Children.Add(control);
        return row;
    }

    public void Update(DeviceState device)
    {
        _device = device;
        Root.Visibility = device.Lighting != null ? Visibility.Visible : Visibility.Collapsed;
        if (device.Lighting is not { } lighting || _pending)
            return; // don't fight the user while their change is on its way

        _updating = true;
        if (!ReferenceEquals(_effectNames, lighting.EffectNames))
        {
            _effectNames = lighting.EffectNames;
            _effect.ItemsSource = lighting.EffectNames;
        }
        var settings = lighting.Settings;
        _on.IsChecked = !settings.IsOff;
        if (!settings.IsOff)
            _effect.SelectedIndex = Math.Clamp(settings.Effect - 1, 0, lighting.EffectNames.Count - 1);
        else if (_effect.SelectedIndex < 0)
            _effect.SelectedIndex = 0;
        _brightness.Value = settings.Brightness;
        _speed.Value = settings.Speed;
        _color.SetColor(settings.Hue, settings.Saturation, settings.Brightness);
        _updating = false;
        RefreshControls();
    }

    private void Changed()
    {
        RefreshControls();
        if (_updating) return;
        _pending = true;
        _sendTimer.Stop();
        _sendTimer.Start();
    }

    private void RefreshControls()
    {
        var on = _on.IsChecked == true;
        foreach (var control in new FrameworkElement[] { _effect, _brightness, _speed, _color.Root })
            control.IsEnabled = on;
        _color.Root.Opacity = on ? 1 : 0.55;
    }

    private LightingSettings Current() => new(
        Effect: _on.IsChecked == true ? Math.Max(_effect.SelectedIndex, 0) + 1 : 0,
        Brightness: (int)Math.Round(_brightness.Value),
        Speed: (int)Math.Round(_speed.Value),
        Hue: _color.Hue,
        Saturation: _color.Saturation);

    private async Task Send()
    {
        _sendTimer.Stop();
        try
        {
            await _monitor.SetLightingAsync(_device.Id, Current());
            _hint.Text = "Saved on the keyboard.";
        }
        catch (Exception ex)
        {
            _hint.Text = $"Couldn't change the lighting: {ex.Message}";
        }
        finally
        {
            if (!_sendTimer.IsEnabled)
                _pending = false;
        }
    }
}

/// <summary>
/// Scroll wheel settings for mice such as the MX Master: SmartShift (when the wheel switches from
/// clicking to spinning freely), click strength, and scroll direction.
/// </summary>
internal sealed class ScrollCard
{
    private static readonly TimeSpan SendDelay = TimeSpan.FromMilliseconds(350);
    private static int _instances;

    private readonly DeviceMonitor _monitor;
    private DeviceState _device;
    private readonly RadioButton _smartShift;
    private readonly RadioButton _ratchet;
    private readonly RadioButton _freeSpin;
    private readonly StackPanel _modePanel = new();
    private readonly Slider _threshold = new() { Minimum = 1, Maximum = 50, SmallChange = 1, LargeChange = 5, IsMoveToPointEnabled = true };
    private readonly StackPanel _thresholdRow;
    private readonly Slider _torque = new() { Minimum = 1, Maximum = 100, SmallChange = 1, LargeChange = 10, IsMoveToPointEnabled = true };
    private readonly StackPanel _torqueRow;
    private readonly CheckBox _wheelInverted = new() { Content = "Reverse the scroll direction", Margin = new Thickness(0, 12, 0, 0) };
    private readonly CheckBox _thumbInverted = new() { Content = "Reverse the thumb wheel direction" };
    private readonly TextBlock _hint = Ui.Text("", 12, "Muted");
    private readonly System.Windows.Threading.DispatcherTimer _sendTimer = new() { Interval = SendDelay };
    private bool _updating;
    private bool _pending;

    public Border Root { get; }

    public ScrollCard(DeviceMonitor monitor, DeviceState device)
    {
        _monitor = monitor;
        _device = device;
        var group = $"WheelMode{++_instances}";
        _smartShift = new RadioButton { GroupName = group, Content = "SmartShift: clicks when you scroll slowly, spins freely when you flick it" };
        _ratchet = new RadioButton { GroupName = group, Content = "Always click" };
        _freeSpin = new RadioButton { GroupName = group, Content = "Always spin freely" };

        var panel = new StackPanel();
        panel.Children.Add(Ui.Label("SCROLL WHEEL"));
        _modePanel.Children.Add(_smartShift);
        _modePanel.Children.Add(_ratchet);
        _modePanel.Children.Add(_freeSpin);
        panel.Children.Add(_modePanel);
        _thresholdRow = Row("How fast a flick switches to free spin (left: gentle, right: hard)", _threshold);
        panel.Children.Add(_thresholdRow);
        _torqueRow = Row("Click strength", _torque);
        panel.Children.Add(_torqueRow);
        panel.Children.Add(_wheelInverted);
        panel.Children.Add(_thumbInverted);
        _hint.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(_hint);
        Root = Ui.Card(panel);

        foreach (var option in new[] { _smartShift, _ratchet, _freeSpin })
            option.Checked += (_, _) => WheelChanged(immediately: true);
        _threshold.ValueChanged += (_, _) => WheelChanged(immediately: false);
        _torque.ValueChanged += (_, _) => WheelChanged(immediately: false);
        _sendTimer.Tick += async (_, _) => await SendWheel();
        _wheelInverted.Click += async (_, _) => await Run(() => _monitor.SetWheelInvertedAsync(_device.Id, _wheelInverted.IsChecked == true), "scroll direction");
        _thumbInverted.Click += async (_, _) => await Run(() => _monitor.SetThumbWheelInvertedAsync(_device.Id, _thumbInverted.IsChecked == true), "thumb wheel direction");

        Update(device);
    }

    private static StackPanel Row(string label, FrameworkElement control)
    {
        var row = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(Ui.Text(label, 12, "Muted"));
        row.Children.Add(control);
        return row;
    }

    public void Update(DeviceState device)
    {
        _device = device;
        Root.Visibility = device.Scroll != null ? Visibility.Visible : Visibility.Collapsed;
        if (device.Scroll is not { } scroll || _pending)
            return;

        _updating = true;
        _modePanel.Visibility = scroll.HasSmartShift ? Visibility.Visible : Visibility.Collapsed;
        _smartShift.IsChecked = scroll.Mode == WheelMode.SmartShift;
        _ratchet.IsChecked = scroll.Mode == WheelMode.AlwaysRatchet;
        _freeSpin.IsChecked = scroll.Mode == WheelMode.AlwaysFreeSpin;
        if (scroll.SmartShiftThreshold is { } threshold)
            _threshold.Value = threshold;
        _torqueRow.Visibility = scroll.Torque != null ? Visibility.Visible : Visibility.Collapsed;
        if (scroll.Torque is { } torque)
            _torque.Value = torque;
        _wheelInverted.Visibility = scroll.WheelInverted != null ? Visibility.Visible : Visibility.Collapsed;
        _wheelInverted.IsChecked = scroll.WheelInverted == true;
        _thumbInverted.Visibility = scroll.ThumbWheelInverted != null ? Visibility.Visible : Visibility.Collapsed;
        _thumbInverted.IsChecked = scroll.ThumbWheelInverted == true;
        _updating = false;
        RefreshControls();
    }

    private void RefreshControls()
    {
        _thresholdRow.Visibility = _device.Scroll?.HasSmartShift == true && _smartShift.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Root.IsEnabled = _device.Awake;
    }

    private void WheelChanged(bool immediately)
    {
        RefreshControls();
        if (_updating) return;
        _pending = true;
        _sendTimer.Stop();
        if (immediately)
            _ = SendWheel();
        else
            _sendTimer.Start();
    }

    private async Task SendWheel()
    {
        _sendTimer.Stop();
        var mode = _freeSpin.IsChecked == true ? WheelMode.AlwaysFreeSpin
            : _ratchet.IsChecked == true ? WheelMode.AlwaysRatchet
            : WheelMode.SmartShift;
        int? torque = _device.Scroll?.Torque != null ? (int)Math.Round(_torque.Value) : null;
        try
        {
            await Run(() => _monitor.SetWheelModeAsync(_device.Id, mode, (int)Math.Round(_threshold.Value), torque), "scroll wheel");
        }
        finally
        {
            if (!_sendTimer.IsEnabled)
                _pending = false;
        }
    }

    private async Task Run(Func<Task> change, string what)
    {
        try
        {
            await change();
            _hint.Text = "Saved. The app sets it again whenever the mouse reconnects.";
        }
        catch (Exception ex)
        {
            _hint.Text = $"Couldn't change the {what}: {ex.Message}";
        }
    }
}

/// <summary>
/// Button assignments for mice without onboard profiles (such as the MX Master). A button can take on
/// another button's job inside the mouse, or send its presses to the app, which runs an action.
/// </summary>
internal sealed class ButtonsCard
{
    private record Option(string Label, ButtonAssignment Assignment)
    {
        public override string ToString() => Label;
    }

    private readonly DeviceMonitor _monitor;
    private DeviceState _device;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _hint = Ui.Text("", 12, "Muted");
    private string? _shown;

    public Border Root { get; }

    public ButtonsCard(DeviceMonitor monitor, DeviceState device)
    {
        _monitor = monitor;
        _device = device;
        var panel = new StackPanel();
        panel.Children.Add(Ui.Label("BUTTONS"));
        panel.Children.Add(_rows);
        _hint.Margin = new Thickness(0, 10, 0, 0);
        _hint.Text = "Actions that start with \"App:\" only work while Mini mouse hub is running. The app sets the buttons again whenever the mouse reconnects.";
        panel.Children.Add(_hint);
        Root = Ui.Card(panel);
        Update(device);
    }

    public void Update(DeviceState device)
    {
        _device = device;
        Root.Visibility = device.Buttons is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        Root.IsEnabled = device.Awake;
        if (device.Buttons is not { } buttons)
            return;

        // Rebuild only when something changed, so an open dropdown isn't closed by a battery update.
        var shown = string.Join(";", buttons.Select(b => $"{b.Cid}:{b.MappedTo}:{b.Action}"));
        if (shown == _shown) return;
        _shown = shown;

        _rows.Children.Clear();
        foreach (var button in buttons)
            _rows.Children.Add(Row(button));
    }

    private Grid Row(ButtonControl button)
    {
        var options = button.Targets
            .Select(t => new Option(t.Cid == button.Cid ? $"{t.Name} (default)" : t.Name, t.Cid == button.Cid ? ButtonAssignment.Default : new ButtonAssignment(t.Cid, null)))
            .ToList();
        if (button.CanDivert)
            options.AddRange(ButtonActions.All.Select(a => new Option($"App: {a.Name}", new ButtonAssignment(null, a.Id))));

        var current = button.Action != null
            ? options.FindIndex(o => o.Assignment.Action == button.Action)
            : options.FindIndex(o => (o.Assignment.RemapTo ?? button.Cid) == button.MappedTo && o.Assignment.Action == null);

        var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var name = Ui.Text(button.Name, 13);
        name.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(name);
        var choice = new ComboBox { ItemsSource = options, SelectedIndex = Math.Max(current, 0) };
        choice.SelectionChanged += async (_, _) =>
        {
            if (choice.SelectedItem is not Option option) return;
            try
            {
                await _monitor.SetButtonAsync(_device.Id, button.Cid, option.Assignment);
                _hint.Text = $"{button.Name} now does: {option.Label}.";
            }
            catch (Exception ex)
            {
                _hint.Text = $"Couldn't change {button.Name}: {ex.Message}";
            }
        };
        Grid.SetColumn(choice, 1);
        row.Children.Add(choice);
        return row;
    }
}

/// <summary>App settings.</summary>
internal static class SettingsPage
{
    public static UIElement Build(AppSettings settings, Func<Task<string>> checkForUpdates, SettingsSync sync, DeviceMonitor monitor)
    {
        var panel = new StackPanel { MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };

        var general = new StackPanel();
        general.Children.Add(Ui.Label("GENERAL"));
        general.Children.Add(Toggle("Start with Windows (in the tray)", StartWithWindows.IsEnabled, on => StartWithWindows.IsEnabled = on));
        panel.Children.Add(Ui.Card(general));

        var appearance = new StackPanel();
        appearance.Children.Add(Ui.Label("APPEARANCE"));
        foreach (var (choice, text) in new[] { (ThemeChoice.System, "Match Windows"), (ThemeChoice.Light, "Light"), (ThemeChoice.Dark, "Dark") })
        {
            var option = new RadioButton { Content = text, GroupName = "Theme", IsChecked = settings.Theme == choice };
            option.Checked += (_, _) =>
            {
                if (settings.Theme == choice) return;
                settings.Theme = choice;
                settings.Save();
                Theme.Set(choice);
            };
            appearance.Children.Add(option);
        }
        var trayBattery = Toggle("Show the battery level in the tray icon", settings.TrayShowsBattery, on => { settings.TrayShowsBattery = on; settings.Save(); });
        trayBattery.Margin = new Thickness(0, 8, 0, 0);
        appearance.Children.Add(trayBattery);
        appearance.Children.Add(Ui.Text("When this is off, the tray shows the Mini mouse hub icon. Hover it to see the battery.", 12, "Muted"));
        panel.Children.Add(Ui.Card(appearance));

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

        panel.Children.Add(new GitHubSyncCard(settings, sync).Root);
        panel.Children.Add(TroubleshootingCard(monitor));
        return panel;
    }

    /// <summary>Saves a diagnostic report someone testing a new device can send back.</summary>
    private static Border TroubleshootingCard(DeviceMonitor monitor)
    {
        var card = new StackPanel();
        card.Children.Add(Ui.Label("TROUBLESHOOTING"));
        card.Children.Add(Ui.Text("If a device doesn't show up or a setting doesn't work, save a diagnostic report and send it to the developer. " +
            "It lists your Logitech devices, what they support and the app's recent messages with them. It contains no personal files.", 12, "Muted"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var save = new Button { Content = "Save diagnostic report" };
        var result = Ui.Text("", 12, "Muted");
        result.VerticalAlignment = VerticalAlignment.Center;
        result.Margin = new Thickness(12, 0, 0, 0);
        save.Click += async (_, _) =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"Mini mouse hub report {DateTime.Now:yyyy-MM-dd HHmm}.txt",
                DefaultExt = ".txt",
                Filter = "Text file|*.txt",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            };
            if (dialog.ShowDialog() != true) return;
            save.IsEnabled = false;
            result.Text = "Asking the devices…";
            try
            {
                var report = await monitor.CreateDiagnosticReportAsync($"Mini mouse hub {Updater.CurrentVersion} diagnostic report");
                await System.IO.File.WriteAllTextAsync(dialog.FileName, report);
                result.Text = "Saved. Send that file to the developer.";
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\"");
            }
            catch (Exception ex)
            {
                result.Text = $"Couldn't save the report: {ex.Message}";
            }
            finally
            {
                save.IsEnabled = true;
            }
        };
        row.Children.Add(save);
        row.Children.Add(result);
        card.Children.Add(row);
        return Ui.Card(card);
    }

    private static CheckBox Toggle(string text, bool value, Action<bool> changed)
    {
        var box = new CheckBox { Content = text, IsChecked = value };
        box.Checked += (_, _) => changed(true);
        box.Unchecked += (_, _) => changed(false);
        return box;
    }
}
