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
    public required string JavaPath { get; init; }
    public int MinMemoryMb { get; init; } = 1024;
    public int MaxMemoryMb { get; init; } = 4096;
    public bool UseAikarFlags { get; init; } = true;
    public int Port { get; init; } = 25565;
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

/// <summary>Creates a ready-to-run server folder: verified JAR, eula.txt (only after explicit consent), server.properties.</summary>
public sealed class ServerCreator(ServerSources sources, HttpClient http)
{
    public sealed record Request(string Name, string ParentFolder, ServerPlatform Platform, string Version, string JavaPath, int MaxMemoryMb, int Port, string? Seed, bool AcceptEula);

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
        Directory.CreateDirectory(Path.Combine(dir, "plugins"));

        progress?.Report((0.05, "resolve"));
        var dl = await sources.ResolveAsync(r.Platform, r.Version, ct).ConfigureAwait(false);
        var jar = r.Platform == ServerPlatform.Paper ? "paper.jar" : "server.jar";
        await VerifiedDownloader.DownloadAsync(http, dl.Url, Path.Combine(dir, jar), dl.Hash, dl.Algorithm, 300L * 1024 * 1024,
            new Progress<double>(p => progress?.Report((0.1 + (p * 0.8), "download"))), ct).ConfigureAwait(false);

        progress?.Report((0.92, "configure"));
        await File.WriteAllTextAsync(Path.Combine(dir, "eula.txt"),
            $"# Accepted in PackCheck by the user on {DateTimeOffset.Now:u}\n# https://aka.ms/MinecraftEULA\neula=true\n", ct).ConfigureAwait(false);
        var props = new Dictionary<string, string> { ["server-port"] = r.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), ["motd"] = safeName };
        if (!string.IsNullOrWhiteSpace(r.Seed))
        {
            props["level-seed"] = r.Seed.Trim();
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
            JavaPath = r.JavaPath,
            MaxMemoryMb = r.MaxMemoryMb,
            MinMemoryMb = Math.Min(1024, r.MaxMemoryMb),
            Port = r.Port,
        };
    }
}
