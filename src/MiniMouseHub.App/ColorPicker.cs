using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using static MiniMouseHub.App.MainWindow;

namespace MiniMouseHub.App;

/// <summary>
/// Picks a lighting color: a hue/saturation field, a row of presets and a hex box. Values are 0–255,
/// like the keyboard's own HSV. Value (brightness) only changes when a hex code is typed in. The # sits
/// beside the hex box rather than in it, so selecting the box copies just the six digits.
/// </summary>
internal sealed class ColorPicker
{
    private static readonly (string Name, string Hex)[] Presets =
    [
        ("Red", "#FF0000"), ("Orange", "#FF7A00"), ("Yellow", "#FFE000"), ("Green", "#00FF2A"), ("Cyan", "#00E5FF"),
        ("Blue", "#0040FF"), ("Purple", "#8C00FF"), ("Pink", "#FF00A8"), ("White", "#FFFFFF"),
    ];

    private readonly Canvas _field = new() { Height = 110, Cursor = Cursors.Cross, ClipToBounds = true };
    private readonly Ellipse _marker = new() { Width = 16, Height = 16, StrokeThickness = 2, Stroke = Brushes.White, IsHitTestVisible = false };
    private readonly Ellipse _markerRing = new() { Width = 18, Height = 18, StrokeThickness = 1, Stroke = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Border _swatch = new() { Width = 32, Height = 32, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
    private const int HexDigits = 6;

    private readonly TextBox _hex = new() { Width = 84, MaxLength = HexDigits, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hexError = Ui.Text("", 12, "Bad");

    public int Hue { get; private set; }
    public int Saturation { get; private set; }
    public int Value { get; private set; } = 255;

    /// <summary>Raised when the user picks a color. The flag says whether Value changed too.</summary>
    public event Action<bool>? Changed;

    public FrameworkElement Root { get; }

    public ColorPicker()
    {
        // Hues left to right, fading to white towards the bottom.
        var hues = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (var i = 0; i <= 6; i++)
            hues.GradientStops.Add(new GradientStop(FromHsv(i * 60 % 360, 1, 1), i / 6.0));
        var whites = new LinearGradientBrush(Colors.Transparent, Colors.White, 90);
        var fieldBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = Res("Border"),
            Margin = new Thickness(0, 4, 0, 0),
            Child = new Grid { Children = { new Border { Background = hues, CornerRadius = new CornerRadius(5) }, new Border { Background = whites, CornerRadius = new CornerRadius(5) }, _field } },
        };
        _field.Background = Brushes.Transparent;
        _field.Children.Add(_markerRing);
        _field.Children.Add(_marker);
        _field.SizeChanged += (_, _) => PlaceMarker();
        _field.MouseLeftButtonDown += (_, e) => { _field.CaptureMouse(); PickAt(e.GetPosition(_field)); };
        _field.MouseMove += (_, e) => { if (_field.IsMouseCaptured) PickAt(e.GetPosition(_field)); };
        _field.MouseLeftButtonUp += (_, _) => _field.ReleaseMouseCapture();

        var presets = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (name, hex) in Presets)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var button = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 6, 6),
                BorderThickness = new Thickness(1), Background = new SolidColorBrush(color), BorderBrush = Res("Border"),
                ToolTip = name, Cursor = Cursors.Hand,
            };
            button.MouseEnter += (_, _) => button.BorderBrush = Res("AccentInk");
            button.MouseLeave += (_, _) => button.BorderBrush = Res("Border");
            button.MouseLeftButtonUp += (_, _) =>
            {
                var (h, s, _) = ToHsv(color);
                SetColor(h, s, Value);
                Changed?.Invoke(false);
            };
            presets.Children.Add(button);
        }

        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        _swatch.BorderBrush = Res("Border");
        hexRow.Children.Add(_swatch);
        var hash = Ui.Text("#", 14, "Muted");
        hash.FontFamily = new FontFamily("Consolas");
        hash.VerticalAlignment = VerticalAlignment.Center;
        hash.Margin = new Thickness(10, 0, 4, 0);
        hexRow.Children.Add(hash);
        _hex.FontFamily = new FontFamily("Consolas");
        _hex.ToolTip = "Type a hex color like FF8800 and press Enter";
        _hex.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(Uri.IsHexDigit);
        DataObject.AddPastingHandler(_hex, OnPaste);
        _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyHex(); };
        _hex.LostKeyboardFocus += (_, _) => ApplyHex();
        hexRow.Children.Add(_hex);
        _hexError.VerticalAlignment = VerticalAlignment.Center;
        _hexError.Margin = new Thickness(10, 0, 0, 0);
        hexRow.Children.Add(_hexError);

        var panel = new StackPanel();
        panel.Children.Add(fieldBorder);
        panel.Children.Add(presets);
        panel.Children.Add(Ui.Text("Hex", 12, "Muted"));
        panel.Children.Add(hexRow);
        Root = panel;
        Refresh();
    }

    /// <summary>Shows a color without raising Changed.</summary>
    public void SetColor(int hue, int saturation, int value)
    {
        Hue = Math.Clamp(hue, 0, 255);
        Saturation = Math.Clamp(saturation, 0, 255);
        Value = Math.Clamp(value, 0, 255);
        Refresh();
    }

    private void PickAt(Point p)
    {
        var width = Math.Max(1, _field.ActualWidth);
        var height = Math.Max(1, _field.ActualHeight);
        var hue = (int)Math.Round(Math.Clamp(p.X / width, 0, 1) * 255);
        var saturation = (int)Math.Round((1 - Math.Clamp(p.Y / height, 0, 1)) * 255);
        SetColor(hue, saturation, Value);
        Changed?.Invoke(false);
    }

    /// <summary>Pastes only the digits, so "#FF8800" doesn't end up with a second #.</summary>
    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        e.CancelCommand();
        if (e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string pasted)
            return;
        var digits = CleanHex(pasted);
        var room = Math.Max(0, HexDigits - (_hex.Text.Length - _hex.SelectionLength));
        _hex.SelectedText = digits[..Math.Min(digits.Length, room)];
        _hex.CaretIndex = _hex.SelectionStart + _hex.SelectionLength;
    }

    /// <summary>The hex digits of a pasted or typed color, without a leading # or 0x.</summary>
    internal static string CleanHex(string text)
    {
        text = text.Trim();
        if (text.StartsWith('#'))
            text = text[1..];
        else if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        return string.Concat(text.Where(Uri.IsHexDigit));
    }

    private void ApplyHex()
    {
        var text = CleanHex(_hex.Text);
        if (text.Length == 3)
            text = string.Concat(text.Select(c => $"{c}{c}"));
        if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            _hexError.Text = "Use a code like FF8800";
            return;
        }
        _hexError.Text = "";
        var color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        if (color == CurrentColor()) return;
        var (h, s, v) = ToHsv(color);
        SetColor(h, s, v);
        _hex.Text = text.ToUpperInvariant(); // keep what was typed, the keyboard's HSV can round it slightly
        Changed?.Invoke(true);
    }

    private Color CurrentColor() => FromHsv(Hue / 255.0 * 360, Saturation / 255.0, Value / 255.0);

    private void Refresh()
    {
        var color = CurrentColor();
        _swatch.Background = new SolidColorBrush(color);
        if (!_hex.IsKeyboardFocused)
            _hex.Text = $"{color.R:X2}{color.G:X2}{color.B:X2}";
        _hexError.Text = "";
        PlaceMarker();
    }

    private void PlaceMarker()
    {
        var x = Hue / 255.0 * _field.ActualWidth;
        var y = (1 - Saturation / 255.0) * _field.ActualHeight;
        _marker.Fill = new SolidColorBrush(FromHsv(Hue / 255.0 * 360, Saturation / 255.0, 1));
        Canvas.SetLeft(_marker, x - _marker.Width / 2);
        Canvas.SetTop(_marker, y - _marker.Height / 2);
        Canvas.SetLeft(_markerRing, x - _markerRing.Width / 2);
        Canvas.SetTop(_markerRing, y - _markerRing.Height / 2);
    }

    public static Color FromHsv(double hue, double saturation, double value)
    {
        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;
        var (r, g, b) = (hue % 360) switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    /// <summary>A color as the keyboard's 0–255 hue, saturation and value.</summary>
    private static (int Hue, int Saturation, int Value) ToHsv(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var delta = max - Math.Min(r, Math.Min(g, b));
        double hue = 0;
        if (delta > 0)
        {
            hue = max == r ? 60 * ((g - b) / delta % 6)
                : max == g ? 60 * ((b - r) / delta + 2)
                : 60 * ((r - g) / delta + 4);
            if (hue < 0) hue += 360;
        }
        var saturation = max == 0 ? 0 : delta / max;
        return ((int)Math.Round(hue / 360 * 255) % 256, (int)Math.Round(saturation * 255), (int)Math.Round(max * 255));
    }
}
