using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MiniMouseHub.Core;

namespace MiniMouseHub.App;

public partial class MainWindow : Window
{
    private readonly MouseMonitor _monitor;
    private MouseState _state = MouseState.NoReceiver;
    private bool _allowClose;

    public MainWindow(MouseMonitor monitor)
    {
        _monitor = monitor;
        InitializeComponent();
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

    public void Render(MouseState state)
    {
        _state = state;

        DeviceName.Text = state.Name ?? (state.ReceiverFound ? "Mouse not responding" : "No receiver found");
        var (status, color) = state switch
        {
            { ReceiverFound: false } => ("Plug in the Lightspeed receiver, and make sure G HUB is closed", "Bad"),
            { MouseAwake: false } => ("Asleep or switched off. Move the mouse to wake it", "Warn"),
            _ => ("Connected", "Good"),
        };
        StatusText.Text = status;
        StatusDot.Fill = Brush(color);

        if (state.Battery is { } battery)
        {
            BatteryPercent.Text = battery.Percent is { } p ? $"{p}%" : battery.Level.ToString();
            ChargingText.Text = battery.Charging switch
            {
                ChargingState.Charging => "Charging",
                ChargingState.ChargingSlow => "Charging slowly",
                ChargingState.Full => "Fully charged",
                ChargingState.NotCharging => "Plugged in, not charging",
                ChargingState.Error => "Charging error",
                _ => "On battery",
            };
            SetBatteryBar(battery.Percent ?? 0);
            BatteryBar.Background = Brush(battery.Percent switch { <= 10 => "Bad", <= 20 => "Warn", _ => "Good" });
        }
        else
        {
            BatteryPercent.Text = "–";
            ChargingText.Text = "";
            SetBatteryBar(0);
        }

        DpiValue.Text = state.Dpi?.ToString() ?? "–";
        DpiSlots.Children.Clear();
        DpiSlotsLabel.Visibility = state.DpiSlots is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (state.DpiSlots != null)
        {
            for (var i = 0; i < state.DpiSlots.Count; i++)
            {
                var active = i == state.ActiveDpiSlot;
                DpiSlots.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(10, 3, 10, 3),
                    Margin = new Thickness(0, 0, 6, 6),
                    Background = active ? Brush("Accent") : Brush("Border"),
                    Child = new TextBlock { Text = state.DpiSlots[i].ToString(), Foreground = active ? Brushes.White : Brush("Text"), FontSize = 13 },
                });
            }
        }

        var canSet = state.MouseAwake && state.Dpi != null;
        ApplyDpi.IsEnabled = canSet;
        if (state.DpiCapabilities is { } caps)
        {
            var where = state.Mode == OnboardMode.Onboard ? "Saved to the highlighted slot on the mouse" : "Applied live";
            DpiHint.Text = $"{caps.Min}-{caps.Max}. {where}.";
        }
    }

    private void SetBatteryBar(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        BatteryFilled.Width = new GridLength(percent, GridUnitType.Star);
        BatteryEmpty.Width = new GridLength(100 - percent, GridUnitType.Star);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _monitor.Refresh();

    private void DpiInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            ApplyDpi_Click(sender, e);
    }

    private async void ApplyDpi_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DpiInput.Text.Trim(), out var dpi))
        {
            DpiHint.Text = "Type a number, for example 1600.";
            return;
        }
        if (_state.DpiCapabilities is { } caps && !caps.Accepts(dpi))
        {
            DpiHint.Text = $"{dpi} isn't supported. Pick a value from {caps.Min} to {caps.Max}; above 1000 it goes in larger steps, such as 1600 or 1650.";
            return;
        }

        ApplyDpi.IsEnabled = false;
        DpiHint.Text = "Saving…";
        try
        {
            await _monitor.SetDpiAsync(dpi);
            DpiInput.Clear();
        }
        catch (Exception ex)
        {
            DpiHint.Text = $"Couldn't change the DPI: {ex.Message}";
        }
        finally
        {
            ApplyDpi.IsEnabled = true;
        }
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
