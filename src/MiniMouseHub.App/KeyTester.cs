using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static MiniMouseHub.App.MainWindow;

namespace MiniMouseHub.App;

/// <summary>
/// Shows a 75% keyboard (the GMMK Pro's ANSI layout) and lights up each key as it's pressed, so
/// every switch can be checked. Keys that bounce (a second press within a few milliseconds of
/// letting go) are flagged as possible chatter. While this page is open, the window passes every
/// key press here instead of acting on it.
/// </summary>
internal sealed class KeyTesterPage
{
    private const double Unit = 40;
    private const double Gap = 4;

    /// <summary>A second press this soon after a release is almost certainly the switch bouncing, not a finger.</summary>
    private static readonly TimeSpan ChatterWindow = TimeSpan.FromMilliseconds(30);

    private sealed record KeySpec(string Label, double Width, params Key[] Keys);

    private static KeySpec K(string label, params Key[] keys) => new(label, 1, keys);
    private static KeySpec W(string label, double width, params Key[] keys) => new(label, width, keys);

    private static readonly KeySpec[][] Layout =
    [
        [K("Esc", Key.Escape), K("F1", Key.F1), K("F2", Key.F2), K("F3", Key.F3), K("F4", Key.F4), K("F5", Key.F5), K("F6", Key.F6),
         K("F7", Key.F7), K("F8", Key.F8), K("F9", Key.F9), K("F10", Key.F10), K("F11", Key.F11), K("F12", Key.F12),
         K("PrtSc", Key.Snapshot), K("Knob", Key.VolumeMute, Key.VolumeUp, Key.VolumeDown)],
        [K("`", Key.Oem3), K("1", Key.D1), K("2", Key.D2), K("3", Key.D3), K("4", Key.D4), K("5", Key.D5), K("6", Key.D6),
         K("7", Key.D7), K("8", Key.D8), K("9", Key.D9), K("0", Key.D0), K("-", Key.OemMinus), K("=", Key.OemPlus),
         W("Backspace", 2, Key.Back), K("Del", Key.Delete)],
        [W("Tab", 1.5, Key.Tab), K("Q", Key.Q), K("W", Key.W), K("E", Key.E), K("R", Key.R), K("T", Key.T), K("Y", Key.Y),
         K("U", Key.U), K("I", Key.I), K("O", Key.O), K("P", Key.P), K("[", Key.Oem4), K("]", Key.Oem6),
         W("\\", 1.5, Key.Oem5), K("PgUp", Key.PageUp)],
        [W("Caps", 1.75, Key.CapsLock), K("A", Key.A), K("S", Key.S), K("D", Key.D), K("F", Key.F), K("G", Key.G), K("H", Key.H),
         K("J", Key.J), K("K", Key.K), K("L", Key.L), K(";", Key.Oem1), K("'", Key.Oem7), W("Enter", 2.25, Key.Enter),
         K("PgDn", Key.PageDown)],
        [W("Shift", 2.25, Key.LeftShift), K("Z", Key.Z), K("X", Key.X), K("C", Key.C), K("V", Key.V), K("B", Key.B), K("N", Key.N),
         K("M", Key.M), K(",", Key.OemComma), K(".", Key.OemPeriod), K("/", Key.Oem2), W("Shift", 1.75, Key.RightShift),
         K("↑", Key.Up), K("End", Key.End)],
        [W("Ctrl", 1.25, Key.LeftCtrl), W("Win", 1.25, Key.LWin), W("Alt", 1.25, Key.LeftAlt), W("", 6.25, Key.Space),
         K("Alt", Key.RightAlt), K("Fn"), K("Ctrl", Key.RightCtrl), K("←", Key.Left), K("↓", Key.Down), K("→", Key.Right)],
    ];

    private sealed class KeyView(KeySpec spec, Border border, TextBlock label)
    {
        public KeySpec Spec { get; } = spec;
        public Border Border { get; } = border;
        public TextBlock Label { get; } = label;
        public bool Down { get; set; }
        public bool Tested { get; set; }
        public int Presses { get; set; }
        public int Chatter { get; set; }
        public DateTime LastRelease { get; set; } = DateTime.MinValue;
    }

    private readonly List<KeyView> _keys = new();
    private readonly Dictionary<Key, KeyView> _byKey = new();
    private readonly TextBlock _progress = Ui.Text("", 14, weight: FontWeights.SemiBold);
    private readonly TextBlock _last = Ui.Text("Press any key.", 13, "Muted");
    private readonly TextBlock _chatter = Ui.Text("", 13, "Warn");

    public UIElement Root { get; }

    public KeyTesterPage()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text(
            "Press every key. Keys turn teal once they've worked, and orange if one registers twice from a single press (switch chatter). "
            + "Turn and press the knob too. Fn doesn't send anything to Windows, so it can't be tested here.", 13, "Muted"));

        var board = new StackPanel { Margin = new Thickness(0, 16, 0, 16), HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var row in Layout)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, Gap) };
            foreach (var spec in row)
                line.Children.Add(BuildKey(spec));
            board.Children.Add(line);
        }
        // Shrinks to fit when the window is narrow.
        panel.Children.Add(Ui.Card(new Viewbox { Child = board, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left }));

        var status = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(_progress);
        text.Children.Add(_last);
        text.Children.Add(_chatter);
        status.Children.Add(text);
        var reset = new Button { Content = "Start over", VerticalAlignment = VerticalAlignment.Top, Focusable = false };
        reset.Click += (_, _) => Reset();
        Grid.SetColumn(reset, 1);
        status.Children.Add(reset);
        panel.Children.Add(status);

        Root = panel;
        RefreshSummary();
    }

    private Border BuildKey(KeySpec spec)
    {
        var label = Ui.Text(spec.Label, spec.Label.Length > 3 ? 10 : 12);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.TextWrapping = TextWrapping.NoWrap;
        var border = new Border
        {
            Width = spec.Width * Unit - Gap,
            Height = Unit - Gap,
            Margin = new Thickness(0, 0, Gap, 0),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            Child = label,
        };
        var view = new KeyView(spec, border, label);
        _keys.Add(view);
        foreach (var key in spec.Keys)
            _byKey[key] = view;
        Paint(view);
        return border;
    }

    /// <summary>Called by the window for every key press while this page is open.</summary>
    public void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = true; // don't let Tab, Space, Alt or Esc do anything else
        if (e.IsRepeat) return;
        var key = RealKey(e);
        _last.Text = $"Last key: {Describe(key)}";
        if (!_byKey.TryGetValue(key, out var view))
        {
            _last.Text += " (not on this layout)";
            return;
        }

        if (DateTime.UtcNow - view.LastRelease < ChatterWindow)
            view.Chatter++;
        view.Presses++;
        view.Tested = true;
        view.Down = true;
        if (view.Spec.Label == "Knob")
            view.Label.Text = key switch { Key.VolumeUp => "Knob ↻", Key.VolumeDown => "Knob ↺", _ => "Knob ●" };
        Paint(view);
        RefreshSummary();

        // The knob only sends a press, and Windows may never send PrintScreen's release.
        if (key is Key.VolumeUp or Key.VolumeDown or Key.VolumeMute or Key.Snapshot)
            Release(view);
    }

    public void OnKeyUp(KeyEventArgs e)
    {
        e.Handled = true;
        var key = RealKey(e);
        if (!_byKey.TryGetValue(key, out var view)) return;

        // PrintScreen often arrives only as a release.
        if (key == Key.Snapshot && !view.Tested)
        {
            view.Tested = true;
            view.Presses++;
            _last.Text = $"Last key: {Describe(key)}";
            RefreshSummary();
        }
        Release(view);
    }

    private void Release(KeyView view)
    {
        view.Down = false;
        view.LastRelease = DateTime.UtcNow;
        Paint(view);
    }

    private static Key RealKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey, // Alt, F10 and Alt combinations
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key,
    };

    private static string Describe(Key key) => key switch
    {
        Key.Snapshot => "Print Screen",
        Key.Oem3 => "`",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.Oem4 => "[",
        Key.Oem6 => "]",
        Key.Oem5 => "\\",
        Key.Oem1 => ";",
        Key.Oem7 => "'",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.Oem2 => "/",
        Key.VolumeUp => "Knob turned right (volume up)",
        Key.VolumeDown => "Knob turned left (volume down)",
        Key.VolumeMute => "Knob pressed (mute)",
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        _ => key.ToString(),
    };

    private void Paint(KeyView view)
    {
        if (view.Spec.Keys.Length == 0)
        {
            // Fn: nothing to test.
            view.Border.Background = Res("Background");
            view.Border.BorderBrush = Res("Border");
            view.Border.Opacity = 0.5;
            view.Border.ToolTip = "Fn is handled inside the keyboard and never reaches Windows.";
            return;
        }

        var (background, border, text) = view switch
        {
            { Down: true } => ("Accent", "Accent", "AccentText"),
            { Chatter: > 0 } => ("Warn", "Warn", "Background"),
            { Tested: true } => ("Hover", "AccentInk", "Text"),
            _ => ("Background", "Border", "Muted"),
        };
        view.Border.Background = Res(background);
        view.Border.BorderBrush = Res(border);
        view.Label.Foreground = Res(text);
        view.Border.ToolTip = view.Presses == 0
            ? "Not pressed yet"
            : $"Pressed {view.Presses} time{(view.Presses == 1 ? "" : "s")}" + (view.Chatter > 0 ? $", {view.Chatter} possible chatter" : "");
    }

    private void RefreshSummary()
    {
        var testable = _keys.Where(k => k.Spec.Keys.Length > 0).ToList();
        var tested = testable.Count(k => k.Tested);
        _progress.Text = tested == testable.Count
            ? $"All {testable.Count} keys work."
            : $"{tested} of {testable.Count} keys tested";
        var chattering = testable.Where(k => k.Chatter > 0).Select(k => k.Spec.Label.Length > 0 ? k.Spec.Label : "Space").ToList();
        _chatter.Text = chattering.Count == 0 ? "" : $"Possible chatter: {string.Join(", ", chattering)}";
    }

    private void Reset()
    {
        foreach (var view in _keys)
        {
            view.Down = view.Tested = false;
            view.Presses = view.Chatter = 0;
            view.LastRelease = DateTime.MinValue;
            if (view.Spec.Label == "Knob")
                view.Label.Text = "Knob";
            Paint(view);
        }
        _last.Text = "Press any key.";
        RefreshSummary();
    }
}
