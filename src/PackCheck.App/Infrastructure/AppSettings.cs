using System.IO;
using System.Text.Json;
using PackCheck.Core;

namespace PackCheck.App.Infrastructure;

public enum ThemeMode
{
    Dark,
    Light,
    System,
}

public enum AnimationLevel
{
    Full,
    Reduced,
    Off,
}

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Language { get; set; } = "en";
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    public AnimationLevel Animations { get; set; } = AnimationLevel.Full;
    public bool AutoUpdate { get; set; }
    public bool AllowNetworkProviders { get; set; } = true;
    public int BackupsToKeep { get; set; } = 5;

    /// <summary>Also keep the newest backup of each of the last N days / ISO weeks (0 = off).</summary>
    public int BackupKeepDaily { get; set; } = 7;
    public int BackupKeepWeekly { get; set; } = 4;

    /// <summary>Second folder every backup is copied to (e.g. a OneDrive/Google Drive synced folder or a NAS share).</summary>
    public string? BackupMirrorDir { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool FirstRunDone { get; set; }
    public List<int> OnlinePorts { get; set; } = [];
    public string? OnlineDomain { get; set; }
    public string? DuckDnsDomain { get; set; }
    public string? DuckDnsToken { get; set; }
    public PackCheck.App.Ai.AiProviderKind AiProvider { get; set; } = PackCheck.App.Ai.AiProviderKind.Anthropic;
    public string? AiModel { get; set; }

    /// <summary>How the assistant applies changes: Normal (ask each time), Plan, Auto, Bypass.</summary>
    public PackCheck.App.Ai.AssistantMode AssistantMode { get; set; } = PackCheck.App.Ai.AssistantMode.Normal;

    /// <summary>API key, encrypted with DPAPI for the current Windows user.</summary>
    public string? AiKey { get; set; }
    public string? CurseForgeApiKey { get; set; }
    public string DefaultServerFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PackCheck Servers");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json) ?? new AppSettings();
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }

        // English is the default UI language; others are chosen in Settings.
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataRoot);
        var tmp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
    }
}

/// <summary>Per-user autostart via the HKCU Run key (no admin rights needed).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "PackCheck";

    public static bool IsEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Name) is string;
    }

    public static void Set(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(Name, $"\"{Environment.ProcessPath}\" --minimized");
        }
        else
        {
            key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
}
