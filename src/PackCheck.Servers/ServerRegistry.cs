using System.IO.Compression;
using System.Text.Json;
using PackCheck.Core;

namespace PackCheck.Servers;

public sealed record ServerDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Directory { get; init; }
    public required ServerPlatform Platform { get; init; }
    public required string MinecraftVersion { get; init; }
    public required string JarFile { get; init; }

    /// <summary>Modern Forge/NeoForge start with "@libraries/.../win_args.txt" instead of -jar.</summary>
    public string? ArgsFile { get; init; }
    public required string JavaPath { get; init; }
    public int MinMemoryMb { get; init; } = 1024;
    public int MaxMemoryMb { get; init; } = 4096;
    public bool UseAikarFlags { get; init; } = true;
    public int Port { get; init; } = 25565;

    /// <summary>CPU cores the server may use; 0 = all. Applied as JVM processor count and process affinity.</summary>
    public int CpuCores { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>Persists the list of servers created or imported in PackCheck.</summary>
public sealed class ServerRegistry(string? file = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file = file ?? AppPaths.ServersFile;
    private readonly Lock _gate = new();

    public IReadOnlyList<ServerDefinition> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_file))
            {
                return [];
            }

            try
            {
                return JsonSerializer.Deserialize<List<ServerDefinition>>(File.ReadAllText(_file), Json) ?? [];
            }
            catch (JsonException)
            {
                File.Copy(_file, _file + ".corrupt", overwrite: true);
                return [];
            }
        }
    }

    public void Save(IEnumerable<ServerDefinition> servers)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(servers.ToList(), Json));
            File.Move(tmp, _file, overwrite: true);
        }
    }

    public void Upsert(ServerDefinition def) => Save(Load().Where(s => s.Id != def.Id).Append(def));

    public void Remove(string id) => Save(Load().Where(s => s.Id != id));
}

public sealed record BackupInfo(string Path, DateTimeOffset CreatedAt, long Size);

/// <summary>Zip backups of a server folder with retention. Restore replaces the folder only after confirmation in the UI.</summary>
public static class Backups
{
    public static string BackupDir(ServerDefinition s) => Path.Combine(s.Directory, "..", ".packcheck-backups", Path.GetFileName(s.Directory));

    public static async Task<BackupInfo> CreateAsync(ServerDefinition s, int keep, CancellationToken ct)
    {
        var dir = Path.GetFullPath(BackupDir(s));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        await Task.Run(() => ZipFile.CreateFromDirectory(s.Directory, file, CompressionLevel.Fastest, includeBaseDirectory: false), ct).ConfigureAwait(false);
        ApplyRetention(dir, keep);
        var fi = new FileInfo(file);
        return new BackupInfo(file, fi.CreationTime, fi.Length);
    }

    public static IReadOnlyList<BackupInfo> List(ServerDefinition s)
    {
        var dir = Path.GetFullPath(BackupDir(s));
        return !Directory.Exists(dir)
            ? []
            : Directory.EnumerateFiles(dir, "*.zip").Select(f => new FileInfo(f)).OrderByDescending(f => f.Name)
                .Select(f => new BackupInfo(f.FullName, f.CreationTime, f.Length)).ToList();
    }

    public static void ApplyRetention(string dir, int keep)
    {
        foreach (var old in Directory.EnumerateFiles(dir, "*.zip").Order(StringComparer.Ordinal).Reverse().Skip(Math.Max(1, keep)))
        {
            File.Delete(old);
        }
    }

    public static async Task RestoreAsync(ServerDefinition s, BackupInfo backup, CancellationToken ct)
    {
        var staging = s.Directory + ".restore";
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, true);
        }

        await Task.Run(() => ZipFile.ExtractToDirectory(backup.Path, staging), ct).ConfigureAwait(false);
        var old = s.Directory + ".before-restore";
        if (Directory.Exists(old))
        {
            Directory.Delete(old, true);
        }

        Directory.Move(s.Directory, old);
        Directory.Move(staging, s.Directory);
        Directory.Delete(old, true);
    }
}

public sealed partial class ServerCreator
{
    private static async Task<string> RunJavaAsync(string javaPath, string workDir, IEnumerable<string> args, string logFile, TimeSpan timeout, IProgress<(double, string)>? progress, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(javaPath)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = System.Diagnostics.Process.Start(psi)!;
        var log = new System.Text.StringBuilder();
        var lines = 0;
        void OnLine(string? l)
        {
            if (l is null)
            {
                return;
            }

            lock (log)
            {
                log.AppendLine(l);
                lines++;
            }

            // BuildTools prints a few thousand lines; use them as a rough progress signal.
            progress?.Report((0.72 + (Math.Min(lines, 4000) / 4000d * 0.18), "install"));
        }

        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        finally
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }

            await File.WriteAllTextAsync(logFile, log.ToString(), CancellationToken.None).ConfigureAwait(false);
        }

        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"The build failed (exit code {p.ExitCode}). See {Path.GetFileName(logFile)} in the server folder.");
        }

        return log.ToString();
    }

    /// <summary>BuildTools compiles Java sources, so it needs a JDK even when the server itself runs on a JRE.</summary>
    private async Task<string> JdkForAsync(Request r, CancellationToken ct)
    {
        if (JavaRuntimes.IsJdk(r.JavaPath))
        {
            return r.JavaPath;
        }

        var tools = java ?? new JavaRuntimes(http);
        var required = PlatformInfo.RequiredJava(r.Platform, r.Version);
        var jdk = await tools.EnsureJdkAsync(required, await tools.DetectAsync(ct).ConfigureAwait(false), null, ct).ConfigureAwait(false);
        return jdk.JavaExe;
    }

    /// <summary>Compiles Spigot or CraftBukkit with the official BuildTools in a temporary folder, keeps only the server jar.</summary>
    public static async Task<string> RunBuildToolsAsync(string javaPath, string dir, string version, ServerPlatform platform, IProgress<(double, string)>? progress, CancellationToken ct)
    {
        var build = Path.Combine(dir, ".buildtools");
        Directory.CreateDirectory(build);
        File.Move(Path.Combine(dir, "BuildTools.jar"), Path.Combine(build, "BuildTools.jar"), overwrite: true);
        var target = platform == ServerPlatform.Spigot ? "spigot" : "craftbukkit";
        await RunJavaAsync(javaPath, build, ["-jar", "BuildTools.jar", "--rev", version, "--compile", target, "--output-dir", dir, "--disable-certificate-check"],
            Path.Combine(dir, "buildtools-output.log"), TimeSpan.FromMinutes(45), progress, ct).ConfigureAwait(false);
        var jar = Directory.EnumerateFiles(dir, $"{target}-*.jar").Select(Path.GetFileName).OfType<string>().FirstOrDefault()
            ?? throw new InvalidOperationException("BuildTools finished but produced no server jar.");
        try
        {
            Core.FileSystem.DeleteDirectory(build);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return jar;
    }

    /// <summary>Runs the Quilt installer in server mode; it downloads the vanilla server and writes quilt-server-launch.jar.</summary>
    public static async Task<string> RunQuiltInstallerAsync(string javaPath, string dir, string version, CancellationToken ct)
    {
        await RunJavaAsync(javaPath, dir, ["-jar", "quilt-installer.jar", "install", "server", version, "--download-server", $"--install-dir={dir}"],
            Path.Combine(dir, "installer-output.log"), TimeSpan.FromMinutes(20), null, ct).ConfigureAwait(false);
        File.Delete(Path.Combine(dir, "quilt-installer.jar"));
        return File.Exists(Path.Combine(dir, "quilt-server-launch.jar")) ? "quilt-server-launch.jar" : throw new InvalidOperationException("The Quilt installer produced no launcher.");
    }

    /// <summary>Runs a Forge/NeoForge installer and returns how the server must be launched.</summary>
    public static async Task<(string Jar, string? ArgsFile)> RunInstallerAsync(string javaPath, string dir, string installer, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(javaPath)
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installer);
        psi.ArgumentList.Add("--installServer");
        using var p = System.Diagnostics.Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync(ct);
        var error = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        var log = await output.ConfigureAwait(false) + await error.ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(dir, "installer-output.log"), log, CancellationToken.None).ConfigureAwait(false);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException("The platform installer failed. See installer-output.log in the server folder.");
        }

        foreach (var junk in new[] { installer, installer + ".log", "run.sh" })
        {
            var f = Path.Combine(dir, junk);
            if (File.Exists(f))
            {
                File.Delete(f);
            }
        }

        var libs = Path.Combine(dir, "libraries");
        var args = Directory.Exists(libs) ? Directory.EnumerateFiles(libs, "win_args.txt", SearchOption.AllDirectories).FirstOrDefault() : null;
        if (args is not null)
        {
            return ("run.bat", Path.GetRelativePath(dir, args).Replace('\\', '/'));
        }

        // Older Forge (before 1.17) produces a runnable forge-*.jar next to the installer.
        var runnable = Directory.EnumerateFiles(dir, "*.jar").Select(Path.GetFileName).OfType<string>()
            .FirstOrDefault(n => n.StartsWith("forge", StringComparison.OrdinalIgnoreCase) || n.StartsWith("neoforge", StringComparison.OrdinalIgnoreCase));
        return (runnable ?? throw new InvalidOperationException("The installer did not produce a server."), null);
    }
}

/// <summary>Creates a ready-to-run server folder: verified JAR, eula.txt (only after explicit consent), server.properties.</summary>
public sealed partial class ServerCreator(ServerSources sources, HttpClient http, JavaRuntimes? java = null)
{
    public sealed record Request(string Name, string ParentFolder, ServerPlatform Platform, string Version, string JavaPath, int MaxMemoryMb, int Port, string? Seed, bool AcceptEula)
    {
        public int CpuCores { get; init; }

        /// <summary>Extra server.properties values chosen in the wizard (game mode, difficulty, players, ...).</summary>
        public IReadOnlyDictionary<string, string>? Properties { get; init; }
    }

    public async Task<ServerDefinition> CreateAsync(Request r, IProgress<(double Progress, string Step)>? progress, CancellationToken ct)
    {
        if (!r.AcceptEula)
        {
            throw new InvalidOperationException("The Minecraft EULA must be accepted to create a server.");
        }

        var safeName = string.Concat(r.Name.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
        if (safeName.Length == 0)
        {
            throw new ArgumentException("Invalid server name.", nameof(r));
        }

        var dir = Path.Combine(r.ParentFolder, safeName);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
        {
            throw new InvalidOperationException("The target folder already exists and is not empty.");
        }

        Directory.CreateDirectory(dir);
        if (PlatformInfo.UsesMods(r.Platform))
        {
            Directory.CreateDirectory(Path.Combine(dir, "mods"));
        }

        if (PlatformInfo.UsesPlugins(r.Platform))
        {
            Directory.CreateDirectory(Path.Combine(dir, "plugins"));
        }

        progress?.Report((0.05, "resolve"));
        var dl = await sources.ResolveAsync(r.Platform, r.Version, ct).ConfigureAwait(false);
        var jar = dl.IsInstaller ? dl.FileName : r.Platform switch
        {
            ServerPlatform.Paper => "paper.jar",
            ServerPlatform.Purpur => "purpur.jar",
            ServerPlatform.Fabric => "fabric-server-launch.jar",
            ServerPlatform.Vanilla => "server.jar",
            ServerPlatform.Velocity => "velocity.jar",
            ServerPlatform.BungeeCord => "BungeeCord.jar",
            _ => dl.FileName,
        };
        await VerifiedDownloader.DownloadAsync(http, dl.Url, Path.Combine(dir, jar), dl.Hash, dl.Algorithm, 300L * 1024 * 1024,
            new Progress<double>(p => progress?.Report((0.1 + (p * 0.6), "download"))), ct).ConfigureAwait(false);

        string? argsFile = null;
        if (dl.IsInstaller)
        {
            progress?.Report((0.72, "install"));
            (jar, argsFile) = r.Platform switch
            {
                ServerPlatform.Spigot or ServerPlatform.CraftBukkit => (await RunBuildToolsAsync(await JdkForAsync(r, ct).ConfigureAwait(false), dir, r.Version, r.Platform, progress, ct).ConfigureAwait(false), null),
                ServerPlatform.Quilt => (await RunQuiltInstallerAsync(r.JavaPath, dir, r.Version, ct).ConfigureAwait(false), null),
                _ => await RunInstallerAsync(r.JavaPath, dir, jar, ct).ConfigureAwait(false),
            };
        }

        progress?.Report((0.92, "configure"));
        await File.WriteAllTextAsync(Path.Combine(dir, "eula.txt"),
            $"# Accepted in PackCheck by the user on {DateTimeOffset.Now:u}\n# https://aka.ms/MinecraftEULA\neula=true\n", ct).ConfigureAwait(false);
        var props = new Dictionary<string, string> { ["server-port"] = r.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), ["motd"] = safeName };
        if (!string.IsNullOrWhiteSpace(r.Seed))
        {
            props["level-seed"] = r.Seed.Trim();
        }

        foreach (var (key, value) in r.Properties ?? new Dictionary<string, string>())
        {
            // Keys and values come from the wizard's fixed choices; line breaks would corrupt the file.
            if (key.Length > 0 && !key.Contains('=') && !key.Contains('\n') && !value.Contains('\n'))
            {
                props[key] = value;
            }
        }

        await File.WriteAllTextAsync(Path.Combine(dir, "server.properties"), string.Concat(props.Select(kv => $"{kv.Key}={kv.Value}\n")), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(dir, "packcheck.json"), JsonSerializer.Serialize(new { minecraftVersion = r.Version }), ct).ConfigureAwait(false);

        progress?.Report((1, "done"));
        return new ServerDefinition
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = safeName,
            Directory = dir,
            Platform = r.Platform,
            MinecraftVersion = r.Version,
            JarFile = jar,
            ArgsFile = argsFile,
            JavaPath = r.JavaPath,
            MaxMemoryMb = r.MaxMemoryMb,
            MinMemoryMb = Math.Min(1024, r.MaxMemoryMb),
            Port = r.Port,
            CpuCores = r.CpuCores,
        };
    }
}
