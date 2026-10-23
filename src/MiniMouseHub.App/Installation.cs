using System.IO;
using Microsoft.Win32;

namespace MiniMouseHub.App;

/// <summary>
/// Knows about the copy that the installer (installer/MiniMouseHub.iss) puts in %LOCALAPPDATA%\Programs.
/// The app updates itself without the installer, so it keeps the version shown in Windows' installed
/// apps list up to date.
/// </summary>
public static class Installation
{
    // Inno Setup names the uninstall key after the installer's AppId, plus "_is1".
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{60C6DDED-CE8E-4927-A75C-03870D332825}_is1";

    public static void RefreshInstalledVersion()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, writable: true);
            if (key?.GetValue("InstallLocation") is not string location || Environment.ProcessPath is not { } exe)
                return;
            // Only the installed copy speaks for the installation, not a loose copy run from elsewhere.
            if (!string.Equals(Path.GetFullPath(location).TrimEnd('\\'), Path.GetDirectoryName(exe), StringComparison.OrdinalIgnoreCase))
                return;
            var version = Updater.CurrentVersion.ToString();
            if (key.GetValue("DisplayVersion") as string != version)
                key.SetValue("DisplayVersion", version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Not worth bothering anyone about.
        }
    }
}
