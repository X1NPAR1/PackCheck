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
    public bool MinimizeToTray { get; set; } = true;
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
