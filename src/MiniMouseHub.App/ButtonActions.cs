using System.Runtime.InteropServices;

namespace MiniMouseHub.App;

/// <summary>
/// Actions a mouse button can run through the app, for buttons the mouse sends to the app instead of
/// handling itself (such as the MX Master's gesture button). Mouse clicks follow the button, so they
/// can be held; shortcuts fire once when the button goes down.
/// </summary>
internal static class ButtonActions
{
    public record ButtonAction(string Id, string Name);

    public static IReadOnlyList<ButtonAction> All { get; } =
    [
        new("middle-click", "Middle click"),
        new("back", "Back"),
        new("forward", "Forward"),
        new("copy", "Copy (Ctrl+C)"),
        new("paste", "Paste (Ctrl+V)"),
        new("undo", "Undo (Ctrl+Z)"),
        new("task-view", "Task view (Win+Tab)"),
        new("show-desktop", "Show desktop (Win+D)"),
        new("desktop-left", "Previous virtual desktop"),
        new("desktop-right", "Next virtual desktop"),
        new("screenshot", "Screenshot (Win+Shift+S)"),
        new("play-pause", "Play / pause"),
        new("next-track", "Next track"),
        new("previous-track", "Previous track"),
        new("volume-up", "Volume up"),
        new("volume-down", "Volume down"),
        new("mute", "Mute"),
        new("nothing", "Do nothing"),
    ];

    public static string NameOf(string id) => All.FirstOrDefault(a => a.Id == id)?.Name ?? id;

    private const ushort VkShift = 0x10, VkControl = 0x11, VkLWin = 0x5B, VkTab = 0x09, VkLeft = 0x25, VkRight = 0x27;
    private const ushort VkVolumeMute = 0xAD, VkVolumeDown = 0xAE, VkVolumeUp = 0xAF;
    private const ushort VkMediaNext = 0xB0, VkMediaPrevious = 0xB1, VkMediaPlayPause = 0xB3;

    /// <summary>Runs an action for a press (<paramref name="pressed"/> true) or a release. Safe to call from any thread.</summary>
    public static void Run(string id, bool pressed)
    {
        switch (id)
        {
            case "middle-click":
                Mouse(pressed ? MouseMiddleDown : MouseMiddleUp, 0);
                return;
            case "back":
                Mouse(pressed ? MouseXDown : MouseXUp, XButton1);
                return;
            case "forward":
                Mouse(pressed ? MouseXDown : MouseXUp, XButton2);
                return;
        }
        if (!pressed) return;
        switch (id)
        {
            case "copy": Keys(VkControl, 'C'); break;
            case "paste": Keys(VkControl, 'V'); break;
            case "undo": Keys(VkControl, 'Z'); break;
            case "task-view": Keys(VkLWin, VkTab); break;
            case "show-desktop": Keys(VkLWin, 'D'); break;
            case "desktop-left": Keys(VkControl, VkLWin, VkLeft); break;
            case "desktop-right": Keys(VkControl, VkLWin, VkRight); break;
            case "screenshot": Keys(VkLWin, VkShift, 'S'); break;
            case "play-pause": Keys(VkMediaPlayPause); break;
            case "next-track": Keys(VkMediaNext); break;
            case "previous-track": Keys(VkMediaPrevious); break;
            case "volume-up": Keys(VkVolumeUp); break;
            case "volume-down": Keys(VkVolumeDown); break;
            case "mute": Keys(VkVolumeMute); break;
        }
    }

    /// <summary>Presses the keys in order, then releases them in reverse, like a person typing a shortcut.</summary>
    private static void Keys(params ushort[] keys)
    {
        var inputs = new List<Input>();
        foreach (var key in keys)
            inputs.Add(Key(key, up: false));
        foreach (var key in keys.Reverse())
            inputs.Add(Key(key, up: true));
        Send(inputs.ToArray());
    }

    private static Input Key(ushort vk, bool up) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = vk,
                // Arrow keys are "extended" keys; without the flag they come out as the number pad's.
                Flags = (up ? KeyEventKeyUp : 0) | (vk is VkLeft or VkRight or VkLWin ? KeyEventExtendedKey : 0),
            },
        },
    };

    private static void Mouse(uint flags, uint data) =>
        Send([new Input { Type = InputMouse, Data = new InputUnion { Mouse = new MouseInput { Flags = flags, MouseData = data } } }]);

    private static void Send(Input[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());

    private const uint InputMouse = 0, InputKeyboard = 1;
    private const uint KeyEventExtendedKey = 0x0001, KeyEventKeyUp = 0x0002;
    private const uint MouseMiddleDown = 0x0020, MouseMiddleUp = 0x0040, MouseXDown = 0x0080, MouseXUp = 0x0100;
    private const uint XButton1 = 0x0001, XButton2 = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
