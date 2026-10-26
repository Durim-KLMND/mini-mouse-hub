using System.Windows;
using System.Windows.Controls;
using MiniMouseHub.Core;
using MiniMouseHub.Core.Via;

namespace MiniMouseHub.App;

/// <summary>
/// Turns keys on a VIA keyboard off and on again, such as the lighting key or the Windows key. A key
/// is turned off by setting it to do nothing in the keyboard's own keymap, and what it did before is
/// kept in the settings so it can be turned back on.
/// </summary>
internal sealed class HardwareKeysCard
{
    private readonly DeviceMonitor _monitor;
    private readonly AppSettings _settings;
    private DeviceState _device;
    private KeymapState? _shown;
    private readonly StackPanel _list = new();
    private readonly Button _lightingOff = new() { Content = "Turn off all lighting keys" };
    private readonly Button _restore = new() { Content = "Restore default keys", Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _hint = Ui.Text("", 12, "Muted");
    private IReadOnlyList<HardwareKey> _keys = [];
    private bool _confirmRestore;
    private bool _busy;

    public Border Root { get; }

    public HardwareKeysCard(DeviceMonitor monitor, DeviceState device, AppSettings settings)
    {
        _monitor = monitor;
        _settings = settings;
        _device = device;

        var panel = new StackPanel();
        panel.Children.Add(Ui.Label("HARDWARE KEYS"));
        panel.Children.Add(Ui.Text("Untick a key to turn it off. The keyboard saves this itself, so the key stays off even when the app isn't running.", 13, "Muted"));
        _list.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(_list);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(_lightingOff);
        buttons.Children.Add(_restore);
        panel.Children.Add(buttons);
        _hint.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(_hint);
        Root = Ui.Card(panel);

        _lightingOff.Click += async (_, _) => await Run(async () =>
        {
            foreach (var key in _keys.Where(k => k.IsLighting && k.Enabled))
                await TurnOff(key);
            return "Lighting keys turned off.";
        });
        _restore.Click += async (_, _) => await Restore();

        Update(device);
    }

    public void Update(DeviceState device)
    {
        _device = device;
        Root.Visibility = device.Keymap != null ? Visibility.Visible : Visibility.Collapsed;
        if (device.Keymap is not { } keymap || ReferenceEquals(keymap, _shown) || _busy)
            return;
        _shown = keymap;

        var disabled = Disabled(keymap);
        _keys = HardwareKeys.Find(keymap, disabled);
        _list.Children.Clear();
        foreach (var key in _keys)
            _list.Children.Add(Row(key));
        if (_keys.Count == 0)
            _list.Children.Add(Ui.Text("This keyboard has no lighting key or Windows key in its keymap.", 13));
        _lightingOff.Visibility = _keys.Any(k => k.IsLighting) ? Visibility.Visible : Visibility.Collapsed;
        _lightingOff.IsEnabled = _keys.Any(k => k.IsLighting && k.Enabled);
    }

    private CheckBox Row(HardwareKey key)
    {
        var content = new StackPanel();
        content.Children.Add(Ui.Text(key.Title, 14));
        if (key.Detail != "")
            content.Children.Add(Ui.Text(key.Detail, 12, "Muted"));
        var box = new CheckBox
        {
            Content = content,
            IsChecked = key.Enabled,
            Margin = new Thickness(0, 4, 0, 4),
            VerticalContentAlignment = VerticalAlignment.Top,
            ToolTip = key.Enabled ? "Untick to turn this key off" : "Tick to turn this key back on",
        };
        box.Click += async (_, _) => await Run(async () =>
        {
            if (box.IsChecked == true)
            {
                await TurnOn(key);
                return $"{key.Title} turned back on.";
            }
            await TurnOff(key);
            return $"{key.Title} turned off.";
        });
        return box;
    }

    /// <summary>What the app turned off on this model, as position to the keycode it had, dropping keys that were changed since.</summary>
    private Dictionary<KeyPosition, ushort> Disabled(KeymapState keymap)
    {
        var result = new Dictionary<KeyPosition, ushort>();
        if (!_settings.DisabledKeys.TryGetValue(keymap.Model, out var stored))
            return result;
        var stale = new List<string>();
        foreach (var (text, code) in stored)
        {
            if (KeyPosition.Parse(text) is { } key && key.Layer < keymap.Keymap.Layers && key.Row < keymap.Keymap.Rows &&
                key.Column < keymap.Keymap.Columns && keymap.Keymap[key.Layer, key.Row, key.Column] == QmkKeycodes.None)
                result[key] = code;
            else
                stale.Add(text); // turned back on somewhere else, such as in VIA
        }
        if (stale.Count > 0)
        {
            foreach (var text in stale)
                stored.Remove(text);
            if (stored.Count == 0)
                _settings.DisabledKeys.Remove(keymap.Model);
            _settings.Save();
        }
        return result;
    }

    private async Task TurnOff(HardwareKey key)
    {
        // Remember what it did first, so it can always be turned back on.
        var model = _shown!.Model;
        if (!_settings.DisabledKeys.TryGetValue(model, out var stored))
            _settings.DisabledKeys[model] = stored = new Dictionary<string, ushort>();
        foreach (var position in key.Positions)
            stored[position.ToString()] = key.Keycode;
        _settings.Save();
        foreach (var position in key.Positions)
            await _monitor.SetKeycodeAsync(_device.Id, position, QmkKeycodes.None);
    }

    private async Task TurnOn(HardwareKey key)
    {
        foreach (var position in key.Positions)
            await _monitor.SetKeycodeAsync(_device.Id, position, key.Keycode);
        if (_settings.DisabledKeys.TryGetValue(_shown!.Model, out var stored))
        {
            foreach (var position in key.Positions)
                stored.Remove(position.ToString());
            if (stored.Count == 0)
                _settings.DisabledKeys.Remove(_shown.Model);
        }
        _settings.Save();
    }

    private async Task Restore()
    {
        if (!_confirmRestore)
        {
            _confirmRestore = true;
            _restore.Content = "Click again to restore";
            _hint.Text = "This puts every key back to the keyboard's factory keymap, including changes made in VIA or Keychron Launcher.";
            return;
        }
        _confirmRestore = false;
        _restore.Content = "Restore default keys";
        await Run(async () =>
        {
            var model = _shown?.Model;
            await _monitor.ResetKeymapAsync(_device.Id);
            if (model != null && _settings.DisabledKeys.Remove(model))
                _settings.Save();
            return "Every key is back to the keyboard's default.";
        });
    }

    private async Task Run(Func<Task<string>> change)
    {
        _busy = true;
        Root.IsEnabled = false;
        _hint.Text = "Saving on the keyboard…";
        try
        {
            _hint.Text = await change();
        }
        catch (Exception ex)
        {
            _hint.Text = $"Couldn't change the key: {ex.Message}";
        }
        finally
        {
            _busy = false;
            Root.IsEnabled = true;
            _shown = null; // show what the keyboard has now
            Update(_device);
        }
    }
}
