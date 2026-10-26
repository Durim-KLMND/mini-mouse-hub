namespace MiniMouseHub.Core.Via;

/// <param name="System">"Mac" or "Windows" on keyboards with a layer pair for each (such as Keychron's), otherwise null.</param>
/// <param name="BaseLayer">The layer whose key labels this layer's keys are pressed with.</param>
/// <param name="Prefix">How this layer is reached, such as "Fn", or null for a base layer.</param>
public record KeyLayer(int Layer, string? System, int BaseLayer, string? Prefix)
{
    public bool IsBase => Prefix == null;
}

/// <summary>A VIA keyboard's keymap, so keys can be turned off.</summary>
/// <param name="Model">Identifies the keyboard model (USB vendor and product ID), for remembering what a key did before it was turned off.</param>
public record KeymapState(string Model, Keymap Keymap, IReadOnlyList<KeyLayer> Layers);

public record KeyPosition(int Layer, int Row, int Column)
{
    /// <summary>For storing in settings, such as "2:0:15".</summary>
    public override string ToString() => $"{Layer}:{Row}:{Column}";

    public static KeyPosition? Parse(string text)
    {
        var parts = text.Split(':');
        return parts.Length == 3 && int.TryParse(parts[0], out var l) && int.TryParse(parts[1], out var r) && int.TryParse(parts[2], out var c)
            ? new KeyPosition(l, r, c)
            : null;
    }
}

/// <summary>
/// A key that can be turned off, such as the lighting key. A key that sits on both a Mac and a
/// Windows layer is one entry with a position on each.
/// </summary>
/// <param name="Title">How to find the key, such as "Lighting key" or "Fn + Q".</param>
/// <param name="Detail">What it does and where, such as "Lighting on/off · Windows layer".</param>
/// <param name="Keycode">What the key does when it's on.</param>
/// <param name="Enabled">False when the app turned it off.</param>
public record HardwareKey(string Title, string Detail, bool IsLighting, ushort Keycode, bool Enabled, IReadOnlyList<KeyPosition> Positions);

/// <summary>Finds the keys worth offering to turn off: lighting keys on any layer, and keys such as the Windows key on base layers.</summary>
public static class HardwareKeys
{
    /// <summary>Works out what each layer is for. Keychron boards with four layers are Mac, Mac Fn, Windows, Windows Fn.</summary>
    public static IReadOnlyList<KeyLayer> DescribeLayers(Keymap keymap, bool macWindowsPairs)
    {
        if (macWindowsPairs && keymap.Layers == 4)
            return [new(0, "Mac", 0, null), new(1, "Mac", 0, "Fn"), new(2, "Windows", 2, null), new(3, "Windows", 2, "Fn")];

        // A layer that a key on layer 0 switches to while held is the Fn layer.
        var momentary = keymap.Codes.Take(keymap.Rows * keymap.Columns)
            .Select(QmkKeycodes.MomentaryLayer).Where(l => l > 0).Select(l => l!.Value).Distinct().ToList();
        return Enumerable.Range(0, keymap.Layers).Select(layer => new KeyLayer(layer, null, 0,
            layer == 0 ? null
            : momentary.Count == 1 && momentary[0] == layer ? "Fn"
            : $"Layer {layer}")).ToArray();
    }

    /// <param name="disabled">Keys the app turned off, with what they did before.</param>
    public static IReadOnlyList<HardwareKey> Find(KeymapState state, IReadOnlyDictionary<KeyPosition, ushort> disabled)
    {
        var keymap = state.Keymap;
        ushort Original(int layer, int row, int column)
        {
            var code = keymap[layer, row, column];
            return code == QmkKeycodes.None && disabled.TryGetValue(new KeyPosition(layer, row, column), out var original) ? original : code;
        }

        var found = new List<(KeyLayer Layer, int Row, int Column, ushort Code, bool Enabled)>();
        foreach (var layer in state.Layers.Where(l => l.Layer < keymap.Layers))
        {
            for (var row = 0; row < keymap.Rows; row++)
            {
                for (var column = 0; column < keymap.Columns; column++)
                {
                    var code = Original(layer.Layer, row, column);
                    if (QmkKeycodes.IsLighting(code) || (layer.IsBase && QmkKeycodes.IsCommonlyDisabled(code)))
                        found.Add((layer, row, column, code, keymap[layer.Layer, row, column] != QmkKeycodes.None));
                }
            }
        }

        return found
            .GroupBy(k => (k.Row, k.Column, k.Layer.Prefix, k.Code, k.Enabled))
            .Select(group =>
            {
                var first = group.First();
                var systems = group.Select(k => k.Layer.System).OfType<string>().Distinct().ToList();
                var lighting = QmkKeycodes.IsLighting(first.Code);
                string title, what;
                if (first.Layer.IsBase)
                {
                    title = lighting ? "Lighting key" : $"{KeyName(first.Code, systems)} key";
                    what = lighting ? QmkKeycodes.LightingName(first.Code) : "";
                }
                else
                {
                    var under = QmkKeycodes.KeyName(Original(first.Layer.BaseLayer, first.Row, first.Column));
                    title = under != null ? $"{first.Layer.Prefix} + {under}" : $"{first.Layer.Prefix} + the key at row {first.Row + 1}, column {first.Column + 1}";
                    what = QmkKeycodes.LightingName(first.Code);
                }
                var where = systems.Count switch
                {
                    0 => "",
                    1 => $"{systems[0]} layer",
                    _ => $"{string.Join(" and ", systems)} layers",
                };
                var detail = string.Join(" · ", new[] { what, where }.Where(s => s != ""));
                return new HardwareKey(title, detail, lighting, first.Code, first.Enabled,
                    group.Select(k => new KeyPosition(k.Layer.Layer, k.Row, k.Column)).ToArray());
            })
            .OrderBy(k => k.IsLighting ? 0 : 1)
            .ThenBy(k => k.Positions[0].Layer == state.Layers.FirstOrDefault(l => l.Layer == k.Positions[0].Layer)?.BaseLayer ? 0 : 1)
            .ThenBy(k => k.Positions[0].Row).ThenBy(k => k.Positions[0].Column)
            .ToArray();
    }

    private static string? KeyName(ushort code, IReadOnlyList<string> systems)
    {
        var name = QmkKeycodes.KeyName(code);
        if (code is 0xE3 or 0xE7 && systems.Contains("Mac"))
            name = systems.Count == 1 ? name!.Replace("Windows", "Command") : name + " / Command";
        return name;
    }
}
