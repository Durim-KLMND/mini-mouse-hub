namespace MiniMouseHub.Core.Via;

/// <summary>
/// Names for the QMK keycodes the app needs to recognise, in the numbering VIA protocol 12 and
/// later use (quantum/keycodes.h in QMK 0.19 and later).
/// </summary>
public static class QmkKeycodes
{
    /// <summary>KC_NO: the key does nothing.</summary>
    public const ushort None = 0x0000;

    /// <summary>KC_TRNS: the key does what it does on the layer below.</summary>
    public const ushort Transparent = 0x0001;

    private const ushort LightingFirst = 0x7800;
    private const ushort LightingLast = 0x78FF;
    private const ushort MomentaryFirst = 0x5220; // MO(0)
    private const ushort MomentaryLast = 0x523F;

    public static bool IsLighting(ushort code) => code is >= LightingFirst and <= LightingLast;

    /// <summary>The layer an MO(layer) key switches to while it's held, or null for other keys.</summary>
    public static int? MomentaryLayer(ushort code) => code is >= MomentaryFirst and <= MomentaryLast ? code - MomentaryFirst : null;

    /// <summary>
    /// Keys people often want to turn off on the base layer: the Windows (Command) key, which
    /// interrupts games, and keys that are easy to hit by accident.
    /// </summary>
    public static bool IsCommonlyDisabled(ushort code) => code is 0x39 or 0x49 or 0x65 or 0xE3 or 0xE7;

    /// <summary>What a lighting key does, such as "Lighting on/off".</summary>
    public static string LightingName(ushort code) => code switch
    {
        // Single-color backlight (BL_*).
        0x7800 => "Backlight on",
        0x7801 => "Backlight off",
        0x7802 => "Backlight on/off",
        0x7803 => "Backlight dimmer",
        0x7804 => "Backlight brighter",
        0x7805 => "Backlight level",
        0x7806 => "Backlight breathing",
        // LED matrix (LM_*).
        0x7810 => "Lighting on",
        0x7811 => "Lighting off",
        0x7812 => "Lighting on/off",
        0x7813 => "Next lighting effect",
        0x7814 => "Previous lighting effect",
        0x7815 => "Lighting brighter",
        0x7816 => "Lighting dimmer",
        0x7817 => "Lighting faster",
        0x7818 => "Lighting slower",
        // RGB (RGB_*), which also drives RGB matrix keyboards such as Keychron's.
        0x7820 => "Lighting on/off",
        0x7821 => "Next lighting effect",
        0x7822 => "Previous lighting effect",
        0x7823 => "Lighting hue up",
        0x7824 => "Lighting hue down",
        0x7825 => "Lighting saturation up",
        0x7826 => "Lighting saturation down",
        0x7827 => "Lighting brighter",
        0x7828 => "Lighting dimmer",
        0x7829 => "Lighting faster",
        0x782A => "Lighting slower",
        // RGB matrix (RM_*).
        0x7840 => "Lighting on",
        0x7841 => "Lighting off",
        0x7842 => "Lighting on/off",
        0x7843 => "Next lighting effect",
        0x7844 => "Previous lighting effect",
        0x7845 => "Lighting hue up",
        0x7846 => "Lighting hue down",
        0x7847 => "Lighting saturation up",
        0x7848 => "Lighting saturation down",
        0x7849 => "Lighting brighter",
        0x784A => "Lighting dimmer",
        0x784B => "Lighting faster",
        0x784C => "Lighting slower",
        _ => "Lighting key",
    };

    /// <summary>The label printed on a key, such as "Q" or "Caps Lock", or null when it isn't a plain key.</summary>
    public static string? KeyName(ushort code) => code switch
    {
        >= 0x04 and <= 0x1D => ((char)('A' + code - 0x04)).ToString(),
        >= 0x1E and <= 0x26 => ((char)('1' + code - 0x1E)).ToString(),
        0x27 => "0",
        0x28 => "Enter",
        0x29 => "Esc",
        0x2A => "Backspace",
        0x2B => "Tab",
        0x2C => "Space",
        0x2D => "-",
        0x2E => "=",
        0x2F => "[",
        0x30 => "]",
        0x31 => "\\",
        0x32 => "#",
        0x33 => ";",
        0x34 => "'",
        0x35 => "`",
        0x36 => ",",
        0x37 => ".",
        0x38 => "/",
        0x39 => "Caps Lock",
        >= 0x3A and <= 0x45 => $"F{code - 0x3A + 1}",
        0x46 => "Print Screen",
        0x47 => "Scroll Lock",
        0x48 => "Pause",
        0x49 => "Insert",
        0x4A => "Home",
        0x4B => "Page Up",
        0x4C => "Delete",
        0x4D => "End",
        0x4E => "Page Down",
        0x4F => "Right arrow",
        0x50 => "Left arrow",
        0x51 => "Down arrow",
        0x52 => "Up arrow",
        0x53 => "Num Lock",
        0x64 => "\\",
        0x65 => "Menu",
        >= 0x68 and <= 0x73 => $"F{code - 0x68 + 13}",
        0xA8 => "Mute",
        0xA9 => "Volume up",
        0xAA => "Volume down",
        0xE0 => "Left Ctrl",
        0xE1 => "Left Shift",
        0xE2 => "Left Alt",
        0xE3 => "Left Windows",
        0xE4 => "Right Ctrl",
        0xE5 => "Right Shift",
        0xE6 => "Right Alt",
        0xE7 => "Right Windows",
        _ => null,
    };
}
