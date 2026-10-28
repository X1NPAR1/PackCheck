using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace PackCheck.App.Infrastructure;

/// <summary>Swaps the color dictionary at runtime and keeps the native title bar in sync.</summary>
public static partial class ThemeManager
{
    public static bool IsDark { get; private set; } = true;

    public static event EventHandler? ThemeChanged;

    public static void Apply(ThemeMode mode)
    {
        IsDark = mode switch
        {
            ThemeMode.Light => false,
            ThemeMode.System => SystemPrefersDark(),
            _ => true,
        };

        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/PackCheck;component/Themes/{(IsDark ? "Dark" : "Light")}.xaml") };
        // Remove every color dictionary (including the one App.xaml loads at startup),
        // otherwise the old theme keeps winning because later dictionaries take precedence.
        var merged = Application.Current.Resources.MergedDictionaries;
        foreach (var old in merged.Where(d => d.Source is { } src && src.OriginalString.Contains("Themes/Dark.xaml", StringComparison.Ordinal) || d.Source?.OriginalString.Contains("Themes/Light.xaml", StringComparison.Ordinal) == true).ToList())
        {
            merged.Remove(old);
        }

        merged.Insert(0, dict);

        foreach (Window w in Application.Current.Windows)
        {
            ApplyTitleBar(w);
        }

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static bool SystemPrefersDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0)
        {
            return;
        }

        var dark = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        var round = 2;
        _ = DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}

/// <summary>Central place that decides how long animations run (or whether they run at all).</summary>
public static class Motion
{
    public static AnimationLevel Level { get; set; } = AnimationLevel.Full;

    /// <summary>The in-app setting decides; Off disables every animation.</summary>
    public static bool Enabled => Level != AnimationLevel.Off;

    public static Duration D(int ms) => new(TimeSpan.FromMilliseconds(!Enabled ? 0 : Level == AnimationLevel.Reduced ? ms / 2 : ms));
}
