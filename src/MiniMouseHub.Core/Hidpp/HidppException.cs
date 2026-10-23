namespace MiniMouseHub.Core.Hidpp;

public class HidppException(string message) : Exception(message);

/// <summary>The device answered with a HID++ error code.</summary>
public class HidppErrorException(byte errorCode, bool isHidpp10)
    : HidppException($"HID++ {(isHidpp10 ? "1.0" : "2.0")} error 0x{errorCode:X2} ({Describe(errorCode, isHidpp10)})")
{
    public byte ErrorCode { get; } = errorCode;
    public bool IsHidpp10 { get; } = isHidpp10;

    private static string Describe(byte code, bool isHidpp10) => isHidpp10
        ? code switch
        {
            0x01 => "invalid sub-ID",
            0x02 => "invalid address",
            0x03 => "invalid value",
            0x04 => "connection failed",
            0x05 => "too many devices",
            0x06 => "already exists",
            0x07 => "busy",
            0x08 => "unknown device",
            0x09 => "resource error",
            0x0A => "request unavailable",
            0x0B => "invalid parameter",
            0x0C => "wrong PIN",
            _ => "unknown",
        }
        : code switch
        {
            0x01 => "unknown",
            0x02 => "invalid argument",
            0x03 => "out of range",
            0x04 => "hardware error",
            0x05 => "Logitech internal",
            0x06 => "invalid feature index",
            0x07 => "invalid function",
            0x08 => "busy",
            0x09 => "unsupported",
            _ => "unknown",
        };
}

/// <summary>No response arrived in time; usually means the device is asleep, off, or not paired at that index.</summary>
public class HidppTimeoutException(string message) : HidppException(message);
