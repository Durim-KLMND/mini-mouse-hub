using Microsoft.Win32;

namespace MiniMouseHub.App;

/// <summary>Adds or removes the app from the current user's Windows startup list.</summary>
public static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MiniMouseHub";

    private static string Command => $"\"{Environment.ProcessPath}\" --minimized";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
                key.SetValue(ValueName, Command);
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>Keeps the startup entry pointing at this copy of the app, in case it was moved.</summary>
    public static void RefreshPath()
    {
        if (IsEnabled)
            IsEnabled = true;
    }
}
