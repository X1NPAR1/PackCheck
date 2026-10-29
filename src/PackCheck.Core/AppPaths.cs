namespace PackCheck.Core;

public static class AppPaths
{
    /// <summary>Settings, server list, runtimes and logs. PACKCHECK_DATA points it elsewhere (isolated test runs, portable use).</summary>
    public static string DataRoot { get; } = Environment.GetEnvironmentVariable("PACKCHECK_DATA") is { Length: > 0 } custom
        ? Path.GetFullPath(custom)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackCheck");

    public static string Logs => Path.Combine(DataRoot, "logs");
    public static string Cache => Path.Combine(DataRoot, "cache");
    public static string Runtimes => Path.Combine(DataRoot, "runtimes");
    public static string History => Path.Combine(DataRoot, "history");
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string ServersFile => Path.Combine(DataRoot, "servers.json");
    public static string UpdateStateFile => Path.Combine(DataRoot, "update-state.json");
}
