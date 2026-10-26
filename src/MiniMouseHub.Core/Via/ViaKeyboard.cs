namespace MiniMouseHub.Core.Via;

public class ViaException(string message) : Exception(message);

/// <summary>
/// Talks to a keyboard running QMK firmware with VIA enabled. Every command is a 32-byte report
/// on the raw HID interface (usage page 0xFF60, usage 0x61), and the keyboard answers each one
/// with a 32-byte report that starts with the same command byte.
/// See quantum/via.h in QMK for the command list.
/// </summary>
public sealed class ViaKeyboard(Func<byte[], byte[]> exchange)
{
    public const int ReportLength = 32;

    /// <summary>The first protocol version with the channel-based lighting commands used here.</summary>
    public const int MinLightingProtocol = 0x000C;

    private const byte GetProtocolVersionCommand = 0x01;
    private const byte GetKeycodeCommand = 0x04;
    private const byte SetKeycodeCommand = 0x05;
    private const byte ResetKeymapCommand = 0x06;
    private const byte CustomSetValueCommand = 0x07;
    private const byte CustomGetValueCommand = 0x08;
    private const byte CustomSaveCommand = 0x09;
    private const byte GetLayerCountCommand = 0x11;
    private const byte GetKeymapBufferCommand = 0x12;
    private const byte Unhandled = 0xFF;

    /// <summary>The most keymap bytes one buffer read can return (32 minus the 4-byte header).</summary>
    private const int BufferChunk = 28;

    /// <summary>QMK keeps a matrix row in a 32-bit word, so no keyboard has more columns than this.</summary>
    private const int MaxColumns = 32;
    private const int MaxRows = 32;

    private const byte BrightnessValue = 1;
    private const byte EffectValue = 2;
    private const byte EffectSpeedValue = 3;
    private const byte ColorValue = 4;

    public int GetProtocolVersion()
    {
        var reply = Send(GetProtocolVersionCommand);
        return (reply[1] << 8) | reply[2];
    }

    /// <summary>Reads the lighting. Single-color (LED matrix) keyboards report hue and saturation as 0.</summary>
    public LightingSettings GetLighting(LightingChannel channel = LightingChannel.RgbMatrix)
    {
        var id = (byte)channel;
        var color = channel == LightingChannel.RgbMatrix ? GetValue(id, ColorValue) : [0, 0];
        return new LightingSettings(
            Effect: GetValue(id, EffectValue)[0],
            Brightness: GetValue(id, BrightnessValue)[0],
            Speed: GetValue(id, EffectSpeedValue)[0],
            Hue: color[0],
            Saturation: color[1]);
    }

    /// <summary>Applies the settings and saves them, so they survive unplugging the keyboard.</summary>
    public void SetLighting(LightingSettings settings, LightingChannel channel = LightingChannel.RgbMatrix)
    {
        var id = (byte)channel;
        SetValue(id, EffectValue, Byte(settings.Effect));
        SetValue(id, BrightnessValue, Byte(settings.Brightness));
        SetValue(id, EffectSpeedValue, Byte(settings.Speed));
        if (channel == LightingChannel.RgbMatrix)
            SetValue(id, ColorValue, Byte(settings.Hue), Byte(settings.Saturation));
        Send(CustomSaveCommand, id);
    }

    /// <summary>
    /// Finds how many effects the firmware was built with. QMK clamps an effect number that is too
    /// high to the last effect, so this asks for effect 255, reads back where it landed and puts the
    /// current effect back. Nothing is saved. Returns null while the lights are off, because turning
    /// them on just to count would make them flash.
    /// </summary>
    public int? CountEffects(LightingChannel channel = LightingChannel.RgbMatrix)
    {
        var id = (byte)channel;
        var current = GetValue(id, EffectValue)[0];
        if (current == 0)
            return null;
        SetValue(id, EffectValue, 0xFF);
        var last = GetValue(id, EffectValue)[0];
        SetValue(id, EffectValue, current);
        return last;
    }

    public int GetLayerCount() => Send(GetLayerCountCommand)[1];

    /// <summary>Reads the QMK keycode at a matrix position on a layer.</summary>
    public ushort GetKeycode(int layer, int row, int column)
    {
        var reply = Send(GetKeycodeCommand, Byte(layer), Byte(row), Byte(column));
        return (ushort)((reply[4] << 8) | reply[5]);
    }

    /// <summary>Changes the QMK keycode at a matrix position on a layer. The keyboard saves it right away.</summary>
    public void SetKeycode(int layer, int row, int column, ushort keycode) =>
        Send(SetKeycodeCommand, Byte(layer), Byte(row), Byte(column), (byte)(keycode >> 8), (byte)keycode);

    /// <summary>Puts every key on every layer back to the firmware's default keymap.</summary>
    public void ResetKeymap() => Send(ResetKeymapCommand);

    /// <summary>
    /// Reads the whole keymap. VIA doesn't say how big the key matrix is (the VIA app gets that from a
    /// definition file), so this works it out: the keymap buffer holds every layer row by row, while
    /// asking for one key outside the matrix answers 0. The column count is the row length at which
    /// the buffer lines up with the first two rows read key by key, and the rows end where the buffer
    /// stops matching layer 0 (because the next layer starts there).
    /// </summary>
    public Keymap ReadKeymap()
    {
        var layers = GetLayerCount();
        if (layers == 0)
            throw new ViaException("The keyboard has no keymap layers.");

        var row0 = Enumerable.Range(0, MaxColumns).Select(c => GetKeycode(0, 0, c)).ToArray();
        var row1 = Enumerable.Range(0, MaxColumns).Select(c => GetKeycode(0, 1, c)).ToArray();
        var start = ReadKeycodes(0, 2 * MaxColumns);
        var columns = Enumerable.Range(1, MaxColumns).FirstOrDefault(n => Enumerable.Range(0, MaxColumns).All(c =>
            c < n ? start[c] == row0[c] && start[n + c] == row1[c] : row0[c] == 0 && row1[c] == 0));
        if (columns == 0)
            throw new ViaException("Couldn't work out the size of the keyboard's key matrix.");

        // A key outside the matrix reads as 0, so the first buffer row whose first key doesn't match
        // layer 0 key by key belongs to layer 1. Rows of nothing but empty keys can't be told apart;
        // a few in a row means we've run past the end of a single-layer keymap.
        var rows = 1;
        var empty = 0;
        for (var r = 1; r < MaxRows && empty < 4; r++)
        {
            var row = ReadKeycodes(r * columns, columns);
            var c = Array.FindIndex(row, code => code != 0);
            if (c < 0)
            {
                empty++;
                continue;
            }
            empty = 0;
            if (GetKeycode(0, r, c) != row[c])
            {
                rows = r;
                break;
            }
            rows = r + 1;
        }

        return new Keymap(layers, rows, columns, ReadKeycodes(0, layers * rows * columns));
    }

    /// <summary>Reads keycodes from the keymap buffer, starting at key number <paramref name="first"/>.</summary>
    private ushort[] ReadKeycodes(int first, int count)
    {
        var bytes = new byte[count * 2];
        for (var done = 0; done < bytes.Length; done += BufferChunk)
        {
            var offset = first * 2 + done;
            var size = Math.Min(BufferChunk, bytes.Length - done);
            var reply = Send(GetKeymapBufferCommand, (byte)(offset >> 8), (byte)offset, (byte)size);
            Array.Copy(reply, 4, bytes, done, size);
        }
        return Enumerable.Range(0, count).Select(i => (ushort)((bytes[2 * i] << 8) | bytes[2 * i + 1])).ToArray();
    }

    private byte[] GetValue(byte channel, byte value)
    {
        // The reply repeats [command, channel, value id] and then the data.
        var reply = Send(CustomGetValueCommand, channel, value);
        return reply[3..];
    }

    private void SetValue(byte channel, byte value, params byte[] data) =>
        Send([CustomSetValueCommand, channel, value, .. data]);

    private byte[] Send(params byte[] command)
    {
        var report = new byte[ReportLength];
        command.CopyTo(report, 0);
        var reply = exchange(report);
        if (reply.Length < ReportLength)
            throw new ViaException($"The keyboard sent a short reply ({reply.Length} bytes).");
        if (reply[0] == Unhandled)
            throw new ViaException($"The keyboard doesn't support VIA command 0x{command[0]:X2}.");
        if (reply[0] != command[0])
            throw new ViaException($"Expected a reply to command 0x{command[0]:X2}, got 0x{reply[0]:X2}.");
        return reply;
    }

    private static byte Byte(int value) => (byte)Math.Clamp(value, 0, 255);
}

/// <summary>The VIA lighting channels this app can drive.</summary>
public enum LightingChannel : byte
{
    /// <summary>Per-key RGB.</summary>
    RgbMatrix = 3,
    /// <summary>Per-key single-color backlight, such as white-backlit Keychron boards.</summary>
    LedMatrix = 5,
}

/// <summary>A keyboard's keymap: a 16-bit QMK keycode for every layer, row and column.</summary>
public sealed record Keymap(int Layers, int Rows, int Columns, IReadOnlyList<ushort> Codes)
{
    public ushort this[int layer, int row, int column] => Codes[(layer * Rows + row) * Columns + column];

    public Keymap With(int layer, int row, int column, ushort keycode)
    {
        var codes = Codes.ToArray();
        codes[(layer * Rows + row) * Columns + column] = keycode;
        return this with { Codes = codes };
    }
}
