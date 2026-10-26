using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MiniMouseHub.Core;

namespace MiniMouseHub.App;

/// <summary>
/// User settings, saved as JSON in %LocalAppData%\MiniMouseHub\settings.json. This is the only file the
/// app keeps; everything it needs to remember goes here.
/// </summary>
public class AppSettings
{
    public bool LowBatteryNotifications { get; set; } = true;
    public bool DpiNotifications { get; set; } = true;
    public bool AutoUpdate { get; set; } = true;
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    /// <summary>Draw the battery level as the tray icon; when off, the tray shows the app icon.</summary>
    public bool TrayShowsBattery { get; set; } = true;

    /// <summary>
    /// Settings for devices that forget them when they reconnect (such as the MX Master), by model
    /// name, so the app can set them again.
    /// </summary>
    public Dictionary<string, DeviceSettings> Devices { get; set; } = new();

    /// <summary>GitHub sign-in for syncing settings. Stays on this PC and is never uploaded.</summary>
    public GitHubAccount? GitHub { get; set; }

    /// <summary>Raised after the settings were written, so they can be synced.</summary>
    public static event Action? Saved;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniMouseHub", "settings.json");

    // Versions up to 0.3 kept the file in Roaming AppData.
    private static string OldFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniMouseHub", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            if (File.Exists(OldFilePath))
                return MoveFromRoaming();
        }
        catch (Exception) { }
        return new AppSettings();
    }

    private static AppSettings MoveFromRoaming()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(OldFilePath)) ?? new AppSettings();
        settings.Save();
        if (File.Exists(FilePath))
        {
            File.Delete(OldFilePath);
            var folder = Path.GetDirectoryName(OldFilePath)!;
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Indented));
        }
        catch (Exception) { }
        Saved?.Invoke();
    }

    /// <summary>The settings as uploaded to GitHub: everything except the sign-in.</summary>
    public string ToBackupJson()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.GitHub = null;
        return JsonSerializer.Serialize(copy, Indented);
    }

    /// <summary>Replaces these settings with a backup, keeping this PC's sign-in, and saves them.</summary>
    public void RestoreBackup(string json)
    {
        var backup = JsonSerializer.Deserialize<AppSettings>(json) ?? throw new InvalidDataException("The backup is empty.");
        foreach (var property in typeof(AppSettings).GetProperties())
        {
            if (property.CanWrite && property.Name != nameof(GitHub))
                property.SetValue(this, property.GetValue(backup));
        }
        Save();
    }
}

/// <summary>A GitHub sign-in. The token is encrypted with Windows DPAPI, so only this Windows user can read it.</summary>
public class GitHubAccount
{
    public string Login { get; set; } = "";
    public string ProtectedToken { get; set; } = "";
    public string? GistId { get; set; }
    public DateTimeOffset? LastSync { get; set; }

    /// <summary>Hash of the settings as last synced, to tell whether this PC has changes GitHub doesn't.</summary>
    public string? SyncedHash { get; set; }

    public static GitHubAccount Create(string login, string token) => new()
    {
        Login = login,
        ProtectedToken = Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser)),
    };

    public string Token => Encoding.UTF8.GetString(
        ProtectedData.Unprotect(Convert.FromBase64String(ProtectedToken), null, DataProtectionScope.CurrentUser));
}

/// <summary>
/// Keeps device settings in <see cref="AppSettings"/>. The device monitor calls this from its own
/// thread, so changes are handed to the UI thread, which owns the settings, and reads get a copy.
/// </summary>
internal sealed class DeviceSettingsStore(AppSettings settings, System.Windows.Threading.Dispatcher dispatcher) : IDeviceSettingsStore
{
    private readonly object _lock = new();

    public DeviceSettings? Load(string deviceKey)
    {
        lock (_lock)
            return settings.Devices.TryGetValue(deviceKey, out var stored) ? Copy(stored) : null;
    }

    public void Save(string deviceKey, DeviceSettings deviceSettings)
    {
        var copy = Copy(deviceSettings);
        lock (_lock)
            settings.Devices = new Dictionary<string, DeviceSettings>(settings.Devices) { [deviceKey] = copy };
        dispatcher.BeginInvoke(settings.Save);
    }

    private static DeviceSettings Copy(DeviceSettings s) =>
        JsonSerializer.Deserialize<DeviceSettings>(JsonSerializer.Serialize(s))!;
}
