namespace MiniMouseHub.Core.Hidpp;

/// <summary>A device paired with a receiver, whether or not it is switched on.</summary>
/// <param name="Name">The short name the receiver keeps for it, such as "MX Master 3S", when it has one.</param>
public record PairedDevice(byte Index, string? Name, DeviceKind Kind);

/// <summary>
/// Reads which devices are paired with a receiver from its pairing registers (HID++ 1.0 long register
/// 0xB5). The receiver knows these even while the devices are switched off.
/// </summary>
public static class ReceiverPairings
{
    // Unifying and Lightspeed: sub-register 0x2N holds pairing information and 0x4N the name, for
    // slot N + 1. Bolt: 0x5N holds pairing information and 0x6N the name (asked for with a 0x01
    // parameter), for slot N.
    private const byte UnifyingPairingInfo = 0x20;
    private const byte UnifyingName = 0x40;
    private const byte BoltPairingInfo = 0x50;
    private const byte BoltName = 0x60;

    /// <summary>Returns null when the channel isn't a receiver that has pairing registers, or it answers in a way this doesn't understand.</summary>
    public static List<PairedDevice>? Read(HidppChannel channel)
    {
        try
        {
            return ReadSlots(channel);
        }
        catch (Exception e) when (e is not IOException)
        {
            DiagnosticLog.Add($"Couldn't read the pairings of {channel.ProductName}: {e.Message}");
            return null;
        }
    }

    private static List<PairedDevice>? ReadSlots(HidppChannel channel)
    {
        var bolt = channel.ProductId == HidppConstants.BoltReceiverProductId;
        var paired = new List<PairedDevice>();
        var answered = false;
        for (byte index = 1; index <= 6; index++)
        {
            byte[] info;
            try
            {
                info = Read(channel, bolt ? (byte)(BoltPairingInfo + index) : (byte)(UnifyingPairingInfo + index - 1));
                answered = true;
            }
            catch (HidppErrorException)
            {
                answered = true; // an empty slot
                continue;
            }
            catch (HidppException)
            {
                if (!answered) return null; // no answer at all: not a receiver with these registers
                continue;
            }

            if (info.Length < 8 || info[0] != (bolt ? (byte)(BoltPairingInfo + index) : (byte)(UnifyingPairingInfo + index - 1)))
                return null; // not the answer this expects

            // Unifying: byte 7 holds the device kind; Bolt: byte 1. An empty Bolt slot reads as zeros.
            var kindByte = bolt ? info[1] : info[7];
            if (bolt && info.Skip(1).Take(7).All(b => b == 0))
                continue;
            paired.Add(new PairedDevice(index, ReadName(channel, index, bolt), KindOf(kindByte & 0x0F)));
        }
        return answered ? paired : null;
    }

    private static string? ReadName(HidppChannel channel, byte index, bool bolt)
    {
        try
        {
            if (bolt)
            {
                // [sub-register, 0x01, length, name...]
                var r = channel.ReadLongRegister(HidppConstants.WiredDeviceIndex, HidppConstants.ReceiverInfoRegister, [(byte)(BoltName + index), 0x01]);
                return Text(r, 3, Math.Min((int)r[2], 13));
            }
            // [sub-register, length, name...]
            var u = channel.ReadLongRegister(HidppConstants.WiredDeviceIndex, HidppConstants.ReceiverInfoRegister, [(byte)(UnifyingName + index - 1)]);
            return Text(u, 2, Math.Min((int)u[1], 14));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Text(byte[] data, int start, int length)
    {
        length = Math.Min(length, data.Length - start);
        if (length <= 0) return null;
        var text = System.Text.Encoding.ASCII.GetString(data, start, length).TrimEnd('\0', ' ');
        return text.Length > 0 ? text : null;
    }

    private static byte[] Read(HidppChannel channel, byte subRegister) =>
        channel.ReadLongRegister(HidppConstants.WiredDeviceIndex, HidppConstants.ReceiverInfoRegister, [subRegister], timeoutMs: 500);

    /// <summary>HID++ 1.0 device kinds, as receivers report them.</summary>
    private static DeviceKind KindOf(int kind) => kind switch
    {
        1 => DeviceKind.Keyboard,
        2 => DeviceKind.Mouse,
        3 => DeviceKind.Numpad,
        4 => DeviceKind.Presenter,
        8 => DeviceKind.Trackball,
        9 => DeviceKind.Trackpad,
        7 or 14 => DeviceKind.RemoteControl,
        11 => DeviceKind.Gamepad,
        12 => DeviceKind.Joystick,
        13 => DeviceKind.Headset,
        _ => DeviceKind.Unknown,
    };
}
