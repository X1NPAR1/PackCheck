using System.Diagnostics;
using System.Text.Json;
using PackCheck.Core;
using PackCheck.Validation.Analyzers;

namespace PackCheck.Validation;

public sealed record ValidationOptions
{
    public MinecraftVersion? MinecraftVersion { get; init; }
    public int? JavaVersion { get; init; }
    public long MaxFileBytes { get; init; } = 256L * 1024 * 1024;
}

public sealed class PackValidator(IEnumerable<IAnalyzer>? analyzers = null)
{
    public static IReadOnlyList<IAnalyzer> DefaultAnalyzers { get; } =
    [
        new StructureAnalyzer(), new ServerPropertiesAnalyzer(), new JarIntegrityAnalyzer(),
        new DuplicateAnalyzer(), new DependencyAnalyzer(), new VersionAnalyzer(),
        new ModIntegrityAnalyzer(), new ModDuplicateAnalyzer(), new ModDependencyAnalyzer(), new ModCompatibilityAnalyzer(),
    ];

    private readonly IReadOnlyList<IAnalyzer> _analyzers = analyzers?.ToList() is { Count: > 0 } list ? list : DefaultAnalyzers;

    public static string ToolVersion => typeof(PackValidator).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Validates a folder or a .zip pack. Never executes pack content.</summary>
    public async Task<ValidationReport> ValidateAsync(string target, ValidationOptions? options = null, IProgress<CheckResult>? progress = null, CancellationToken ct = default)
    {
        options ??= new ValidationOptions();
        var started = DateTimeOffset.Now;
        var sw = Stopwatch.StartNew();
        string? tempDir = null;
        var results = new List<CheckResult>();
        var plugins = new List<PluginInfo>();

        try
        {
            var root = Path.GetFullPath(target);
            if (File.Exists(root) && root.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                tempDir = Path.Combine(Path.GetTempPath(), "packcheck-" + Guid.NewGuid().ToString("N"));
                try
                {
                    SafeZip.Extract(root, tempDir);
                }
                catch (Exception ex) when (ex is UnsafeArchiveException or InvalidDataException)
                {
                    results.Add(new CheckResult { Id = "PC000", Name = "Archive safety", Status = CheckStatus.Fail, Description = ex.Message, AffectedFile = Path.GetFileName(root) });
                    return Build(target, started, sw, results, plugins);
                }

                root = SingleChildOr(tempDir);
            }
            else if (!Directory.Exists(root))
            {
                results.Add(new CheckResult { Id = "PC000", Name = "Target", Status = CheckStatus.Error, Description = "Path does not exist or is not a folder/.zip.", NotRunReason = "Invalid path" });
                return Build(target, started, sw, results, plugins);
            }

            var pluginsDir = Path.Combine(root, "plugins");
            if (Directory.Exists(pluginsDir))
            {
                foreach (var jar in Directory.EnumerateFiles(pluginsDir, "*.jar").Order(StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    var len = new FileInfo(jar).Length;
                    if (len > options.MaxFileBytes)
                    {
                        results.Add(new CheckResult { Id = "PC022", Name = "Oversized file", Status = CheckStatus.Warn, AffectedFile = "plugins/" + Path.GetFileName(jar), Description = $"File is {len / 1024 / 1024} MB and was not inspected.", NotRunReason = "Size limit" });
                        continue;
                    }

                    plugins.Add(await PluginMetadataReader.ReadAsync(jar, ct).ConfigureAwait(false));
                }
            }

            var mods = new List<ModInfo>();
            var modsDir = Path.Combine(root, "mods");
            if (Directory.Exists(modsDir))
            {
                foreach (var jar in Directory.EnumerateFiles(modsDir, "*.jar").Order(StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    if (new FileInfo(jar).Length <= options.MaxFileBytes)
                    {
                        mods.Add(await ModMetadataReader.ReadAsync(jar, ct).ConfigureAwait(false));
                    }
                }
            }

            var propsPath = Path.Combine(root, "server.properties");
            var hasProps = File.Exists(propsPath);
            var props = hasProps ? ServerProperties.Parse(await File.ReadAllTextAsync(propsPath, ct).ConfigureAwait(false)) : [];
            var serverJar = Directory.EnumerateFiles(root, "*.jar").Select(Path.GetFileName).FirstOrDefault();

            var ctx = new PackContext
            {
                Root = root,
                Plugins = plugins,
                Mods = mods,
                ServerProperties = props,
                ServerPropertiesExists = hasProps,
                ServerJar = serverJar,
                ServerVersion = options.MinecraftVersion ?? PackManifest.TryReadVersion(root),
                JavaVersion = options.JavaVersion,
            };

            foreach (var analyzer in _analyzers)
            {
                ct.ThrowIfCancellationRequested();
                List<CheckResult> produced;
                try
                {
                    produced = analyzer.Analyze(ctx).ToList();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    produced = [new CheckResult { Id = analyzer.Id, Name = analyzer.GetType().Name, Status = CheckStatus.Error, Description = "The check crashed.", NotRunReason = ex.Message }];
                }

                foreach (var r in produced)
                {
                    results.Add(r);
                    progress?.Report(r);
                }
            }

            return Build(target, started, sw, results, plugins);
        }
        finally
        {
            if (tempDir is not null)
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static string SingleChildOr(string dir)
    {
        // Archives often wrap the pack in one top-level folder; descend into it, but never into a pack sub-folder.
        string[] packFolders = ["plugins", "world", "config", "logs", "libraries"];
        var entries = Directory.GetFileSystemEntries(dir);
        return entries.Length == 1 && Directory.Exists(entries[0]) && !packFolders.Contains(Path.GetFileName(entries[0]), StringComparer.OrdinalIgnoreCase)
            ? entries[0]
            : dir;
    }

    private static ValidationReport Build(string target, DateTimeOffset started, Stopwatch sw, List<CheckResult> results, List<PluginInfo> plugins) => new()
    {
        Tool = "PackCheck",
        ToolVersion = ToolVersion,
        Target = Path.GetFileName(Path.TrimEndingDirectorySeparator(target)),
        StartedAt = started,
        Duration = sw.Elapsed,
        Overall = ValidationReport.Aggregate(results),
        Checks = results,
        Plugins = plugins,
    };
}

/// <summary>Optional packcheck.json in a pack root that records the Minecraft version.</summary>
public static class PackManifest
{
    public const string FileName = "packcheck.json";

    public static MinecraftVersion? TryReadVersion(string root)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("minecraftVersion", out var v) && MinecraftVersion.TryParse(v.GetString(), out var mv) ? mv : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Write(string root, string minecraftVersion) =>
        File.WriteAllText(Path.Combine(root, FileName), JsonSerializer.Serialize(new { minecraftVersion }));
}
