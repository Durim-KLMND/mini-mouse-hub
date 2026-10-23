using System.Text;
using MiniMouseHub.Core.Hidpp;

namespace MiniMouseHub.Core;

/// <summary>Layout of a device's onboard profile memory, from 0x8100 getDescription.</summary>
public record OnboardDescription(
    byte MemoryModel,
    byte ProfileFormat,
    byte MacroFormat,
    byte ProfileCount,
    byte ProfileCountOutOfBox,
    byte ButtonCount,
    byte SectorCount,
    int SectorSize,
    byte MechanicalLayout,
    byte VariousInfo)
{
    /// <summary>True when the mouse has a second set of buttons for G-Shift.</summary>
    public bool HasGShiftButtons => (MechanicalLayout & 0x03) == 0x02;
}

/// <summary>An entry in the profile directory: which sector holds a profile and whether it's enabled.</summary>
public record ProfileHeader(int Number, int Sector, bool Enabled)
{
    /// <summary>Sectors 0x01xx are read-only factory profiles in ROM.</summary>
    public bool IsReadOnly => Sector >= 0x100;
}

/// <summary>A button assignment, kept as its raw 4 bytes plus a readable description.</summary>
public record ButtonBinding(byte[] Raw)
{
    public override string ToString()
    {
        if (Raw.All(b => b == 0xFF)) return "default";
        var behavior = Raw[0] >> 4;
        return behavior switch
        {
            0x0 => $"macro (sector {((Raw[0] & 0x0F) << 8) | Raw[1]}, address {(Raw[2] << 8) | Raw[3]})",
            0x8 => Raw[1] switch
            {
                0x00 => "no action",
                0x01 => $"mouse button {MouseButtonName((Raw[2] << 8) | Raw[3])}",
                0x02 => $"key 0x{Raw[3]:X2}" + (Raw[2] != 0 ? $" with modifiers 0x{Raw[2]:X2}" : ""),
                0x03 => $"media key 0x{(Raw[2] << 8) | Raw[3]:X4}",
                _ => $"send type 0x{Raw[1]:X2}",
            },
            0x9 => FunctionName(Raw[1]),
            _ => $"raw {Convert.ToHexString(Raw)}",
        };
    }

    private static string MouseButtonName(int mask) => mask switch
    {
        0x0001 => "left",
        0x0002 => "right",
        0x0004 => "middle",
        0x0008 => "back",
        0x0010 => "forward",
        _ => $"0x{mask:X4}",
    };

    private static string FunctionName(byte function) => function switch
    {
        0x00 => "no action",
        0x01 => "tilt left",
        0x02 => "tilt right",
        0x03 => "next DPI",
        0x04 => "previous DPI",
        0x05 => "cycle DPI",
        0x06 => "default DPI",
        0x07 => "DPI shift",
        0x08 => "next profile",
        0x09 => "previous profile",
        0x0A => "cycle profile",
        0x0B => "G-Shift",
        0x0C => "show battery",
        0x0D => "select profile",
        0x0E => "mode switch",
        0x0F => "host button",
        0x10 => "scroll down",
        0x11 => "scroll up",
        _ => $"function 0x{function:X2}",
    };
}

/// <summary>
/// A decoded onboard profile. The raw sector bytes are kept so that fields this class doesn't
/// understand are written back unchanged.
/// </summary>
public class OnboardProfile
{
    public const int DpiSlotCount = 5;

    public ProfileHeader Header { get; }
    public byte[] Raw { get; }

    public OnboardProfile(ProfileHeader header, byte[] raw, int buttonCount, int gShiftButtonCount)
    {
        Header = header;
        Raw = raw;
        ButtonCount = buttonCount;
        GShiftButtonCount = gShiftButtonCount;
    }

    public int ButtonCount { get; }
    public int GShiftButtonCount { get; }

    /// <summary>Report interval in milliseconds (1 = 1000 Hz). Newer mice may use another encoding.</summary>
    public byte ReportRate => Raw[0];
    public int DefaultDpiIndex => Raw[1];
    public int ShiftDpiIndex => Raw[2];

    /// <summary>The 5 DPI slots the DPI button cycles through; 0 or 0xFFFF means the slot is unused.</summary>
    public int[] DpiSlots => Enumerable.Range(0, DpiSlotCount).Select(i => Raw[3 + i * 2] | (Raw[4 + i * 2] << 8)).ToArray();

    public byte PowerMode => Raw[16];
    public byte AngleSnapping => Raw[17];
    public int WriteCount => Raw[18] | (Raw[19] << 8);

    public ButtonBinding[] Buttons => Enumerable.Range(0, ButtonCount).Select(i => new ButtonBinding(Raw[(32 + i * 4)..(36 + i * 4)])).ToArray();
    public ButtonBinding[] GShiftButtons => Enumerable.Range(0, GShiftButtonCount).Select(i => new ButtonBinding(Raw[(96 + i * 4)..(100 + i * 4)])).ToArray();

    public string Name
    {
        get
        {
            var bytes = Raw[160..208];
            if (bytes.All(b => b == 0xFF)) return "";
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0', '￿');
        }
    }

    public int StoredCrc => (Raw[^2] << 8) | Raw[^1];
    public bool CrcIsValid => StoredCrc == Crc16.Compute(Raw.AsSpan(0, Raw.Length - 2));
}

/// <summary>CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF), which onboard profile sectors end with.</summary>
public static class Crc16
{
    public static int Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b << 8;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF;
        }
        return crc;
    }
}

/// <summary>Reads the onboard profile memory of a device with feature 0x8100.</summary>
public sealed class OnboardProfileReader(LogitechDevice device)
{
    public OnboardDescription GetDescription()
    {
        // Function 0 = getDescription -> [memory model, profile format, macro format, profile count,
        // out-of-box profile count, button count, sector count, sector size (2 bytes), mechanical layout, various info]
        var r = device.Call(FeatureId.OnboardProfiles, 0);
        return new OnboardDescription(r[0], r[1], r[2], r[3], r[4], r[5], r[6], (r[7] << 8) | r[8], r[9], r[10]);
    }

    /// <summary>The number of the active profile (1-based), or 0x01xx for a read-only factory profile.</summary>
    public int GetCurrentProfile()
    {
        // Function 4 = getCurrentProfile -> [profile (2 bytes)]
        var r = device.Call(FeatureId.OnboardProfiles, 4);
        return (r[0] << 8) | r[1];
    }

    /// <summary>Which DPI slot of the active profile is in use (0-based).</summary>
    public int GetCurrentDpiIndex()
    {
        // Function 11 = getCurrentDpiIndex -> [index]
        return device.Call(FeatureId.OnboardProfiles, 11)[0];
    }

    /// <summary>Reads the profile directory from sector 0, or from ROM when the writable copy is blank.</summary>
    public List<ProfileHeader> GetProfileHeaders(OnboardDescription description)
    {
        var directorySector = 0x0000;
        var directory = ReadSector(directorySector, description.SectorSize);
        if (directory.Take(4).All(b => b == 0x00) || directory.Take(4).All(b => b == 0xFF))
        {
            directorySector = 0x0100;
            directory = ReadSector(directorySector, description.SectorSize);
        }

        var headers = new List<ProfileHeader>();
        for (var offset = 0; offset + 4 <= directory.Length - 2; offset += 4)
        {
            var sector = (directory[offset] << 8) | directory[offset + 1];
            if (sector == 0xFFFF) break;
            headers.Add(new ProfileHeader(headers.Count + 1, sector, directory[offset + 2] != 0));
        }
        return headers;
    }

    public OnboardProfile ReadProfile(ProfileHeader header, OnboardDescription description)
    {
        var raw = ReadSector(header.Sector, description.SectorSize);
        var gShift = description.HasGShiftButtons ? description.ButtonCount : 0;
        return new OnboardProfile(header, raw, Math.Min((int)description.ButtonCount, 16), Math.Min(gShift, 16));
    }

    /// <summary>
    /// Reads a whole sector, 16 bytes at a time. Reads can't run past the end of a sector,
    /// so the last chunk is read from (size - 16) and only its new tail is kept.
    /// </summary>
    public byte[] ReadSector(int sector, int size)
    {
        var data = new List<byte>(size);
        var offset = 0;
        while (offset < size - 15)
        {
            data.AddRange(ReadChunk(sector, offset));
            offset += 16;
        }
        if (offset < size)
        {
            var last = ReadChunk(sector, size - 16);
            data.AddRange(last.Skip(16 - (size - offset)));
        }
        return data.ToArray();
    }

    private byte[] ReadChunk(int sector, int offset)
    {
        // Function 5 = memoryRead(sector (2 bytes), offset (2 bytes)) -> 16 bytes
        return device.Call(FeatureId.OnboardProfiles, 5, (byte)(sector >> 8), (byte)sector, (byte)(offset >> 8), (byte)offset);
    }
}
