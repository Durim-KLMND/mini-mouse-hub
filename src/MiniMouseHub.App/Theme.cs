using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace MiniMouseHub.App;

public enum ThemeChoice { System, Light, Dark }

/// <summary>
/// The app's colors: warm greys with the deep green brand accent (#1D3D37, with #33554D and
/// #486960 for hover and pressed), light by default and dark when Windows (or the user) asks for it. Brushes are swapped in the application
/// resources, so XAML that uses DynamicResource updates by itself.
/// </summary>
public static class Theme
{
    private static readonly Dictionary<string, string> Light = new()
    {
        ["Background"] = "#FFFFFF",
        ["Surface"] = "#F6F6F5",
        ["Chrome"] = "#F6F6F5",
        ["Hover"] = "#ECECEA",
        ["Selected"] = "#E4ECE9",
        ["Border"] = "#E2E2DF",
        ["Text"] = "#1A1A19",
        ["Muted"] = "#5F625F",
        ["Accent"] = "#1D3D37",
        ["AccentHover"] = "#33554D",
        ["AccentPressed"] = "#486960",
        ["AccentText"] = "#FFFFFF",
        ["AccentInk"] = "#1D3D37",
        ["Good"] = "#15803D",
        ["Warn"] = "#B45309",
        ["Bad"] = "#B42318",
        ["Track"] = "#E2E2DF",
    };

    private static readonly Dictionary<string, string> Dark = new()
    {
        ["Background"] = "#111111",
        ["Surface"] = "#181817",
        ["Chrome"] = "#181817",
        ["Hover"] = "#232322",
        ["Selected"] = "#1D3D37",
        ["Border"] = "#2B2B29",
        ["Text"] = "#ECECEA",
        ["Muted"] = "#A3A39E",
        ["Accent"] = "#1D3D37",
        ["AccentHover"] = "#33554D",
        ["AccentPressed"] = "#486960",
        ["AccentText"] = "#FFFFFF",
        ["AccentInk"] = "#8FBFB1",
        ["Good"] = "#22A35A",
        ["Warn"] = "#E0A23B",
        ["Bad"] = "#F28B82",
        ["Track"] = "#2B2B29",
    };

    public static ThemeChoice Choice { get; private set; } = ThemeChoice.System;
    public static bool IsDark { get; private set; }

    /// <summary>Raised after the colors change, so pages built in code can redraw.</summary>
    public static event Action? Changed;

    public static void Initialize(ThemeChoice choice)
    {
        Choice = choice;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Choice == ThemeChoice.System)
                Application.Current.Dispatcher.BeginInvoke(Apply);
        };
        Apply();
    }

    public static void Set(ThemeChoice choice)
    {
        Choice = choice;
        Apply();
    }

    private static void Apply()
    {
        IsDark = Choice switch
        {
            ThemeChoice.Dark => true,
            ThemeChoice.Light => false,
            _ => WindowsUsesDarkApps(),
        };
        var resources = Application.Current.Resources;
        foreach (var (key, hex) in IsDark ? Dark : Light)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            resources[key] = brush;
        }
        Changed?.Invoke();
    }

    private static bool WindowsUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
