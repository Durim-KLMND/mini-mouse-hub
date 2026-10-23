using System.IO;
using System.Text.Json;

namespace MiniMouseHub.App;

/// <summary>User settings, saved as JSON in %AppData%\MiniMouseHub.</summary>
public class AppSettings
{
    public bool LowBatteryNotifications { get; set; } = true;
    public bool DpiNotifications { get; set; } = true;
    public bool AutoUpdate { get; set; } = true;
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniMouseHub", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception) { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { }
    }
}
