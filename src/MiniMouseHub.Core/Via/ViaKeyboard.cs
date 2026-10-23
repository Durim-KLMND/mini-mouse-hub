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
    private const byte CustomSetValueCommand = 0x07;
    private const byte CustomGetValueCommand = 0x08;
    private const byte CustomSaveCommand = 0x09;
    private const byte GetLayerCountCommand = 0x11;
    private const byte Unhandled = 0xFF;

    private const byte RgbMatrixChannel = 3;
    private const byte BrightnessValue = 1;
    private const byte EffectValue = 2;
    private const byte EffectSpeedValue = 3;
    private const byte ColorValue = 4;

    public int GetProtocolVersion()
    {
        var reply = Send(GetProtocolVersionCommand);
        return (reply[1] << 8) | reply[2];
    }

    public LightingSettings GetLighting()
    {
        var color = GetValue(RgbMatrixChannel, ColorValue);
        return new LightingSettings(
            Effect: GetValue(RgbMatrixChannel, EffectValue)[0],
            Brightness: GetValue(RgbMatrixChannel, BrightnessValue)[0],
            Speed: GetValue(RgbMatrixChannel, EffectSpeedValue)[0],
            Hue: color[0],
            Saturation: color[1]);
    }

    /// <summary>Applies the settings and saves them, so they survive unplugging the keyboard.</summary>
    public void SetLighting(LightingSettings settings)
    {
        SetValue(RgbMatrixChannel, EffectValue, Byte(settings.Effect));
        SetValue(RgbMatrixChannel, BrightnessValue, Byte(settings.Brightness));
        SetValue(RgbMatrixChannel, EffectSpeedValue, Byte(settings.Speed));
        SetValue(RgbMatrixChannel, ColorValue, Byte(settings.Hue), Byte(settings.Saturation));
        Send(CustomSaveCommand, RgbMatrixChannel);
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
