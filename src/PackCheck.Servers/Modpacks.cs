using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace PackCheck.Servers;

public enum ModpackFormat
{
    Modrinth,
    CurseForge,
}

/// <summary>A file the pack downloads into the server folder. <see cref="Path"/> is relative and already checked.</summary>
public sealed record ModpackFile(string Path, IReadOnlyList<Uri> Downloads, string? Hash, HashAlgorithmName Algorithm, long Size);

/// <summary>A CurseForge manifest entry; it only becomes a <see cref="ModpackFile"/> after the CurseForge API resolves it.</summary>
public sealed record CurseForgeFileRef(int ProjectId, int FileId, bool Required);

/// <summary>What a modpack archive asks for: the loader server, the files to download and where its overrides live.</summary>
public sealed record ModpackManifest(
    ModpackFormat Format,
    string Name,
    string? PackVersion,
    ServerPlatform Platform,
    string MinecraftVersion,
    string? LoaderVersion,
    IReadOnlyList<ModpackFile> Files,
    IReadOnlyList<CurseForgeFileRef> CurseForgeFiles,
    IReadOnlyList<string> OverrideFolders);

/// <summary>Reads Modrinth (.mrpack) and CurseForge (manifest.json) modpacks. Pure parsing plus path and host checks.</summary>
public static class Modpacks
{
    /// <summary>Hosts the Modrinth modpack format allows for file downloads.</summary>
    public static readonly IReadOnlyList<string> ModrinthHosts = ["cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com"];

    /// <summary>Hosts the CurseForge API hands out for file downloads.</summary>
    public static readonly IReadOnlyList<string> CurseForgeHosts = ["edge.forgecdn.net", "mediafilez.forgecdn.net"];

    /// <summary>Largest single pack file accepted.</summary>
    public const long MaxFileBytes = 512L * 1024 * 1024;

    /// <summary>A stand-in folder for checking relative paths while parsing.</summary>
    private static readonly string CheckRoot = Path.Combine(Path.GetTempPath(), "packcheck-modpack-check");

    private static List<JsonElement> Items(JsonElement root, string name) =>
        root.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : [];

    public static bool IsAllowedHost(Uri url, ModpackFormat format = ModpackFormat.Modrinth) =>
        url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps
        && (format == ModpackFormat.Modrinth ? ModrinthHosts : CurseForgeHosts).Contains(url.IdnHost, StringComparer.OrdinalIgnoreCase);

    /// <summary>Maps the modrinth.index.json dependencies to a server platform and loader version.</summary>
    public static (ServerPlatform Platform, string? LoaderVersion) MapDependencies(IReadOnlyDictionary<string, string> deps)
    {
        foreach (var (key, platform) in new[] { ("neoforge", ServerPlatform.NeoForge), ("forge", ServerPlatform.Forge), ("quilt-loader", ServerPlatform.Quilt), ("fabric-loader", ServerPlatform.Fabric) })
        {
            if (deps.TryGetValue(key, out var v))
            {
                return (platform, v);
            }
        }

        return (ServerPlatform.Vanilla, null);
    }

    /// <summary>Maps a CurseForge modLoaders id such as "fabric-0.16.9" or "forge-47.3.0".</summary>
    public static (ServerPlatform Platform, string? LoaderVersion) MapCurseForgeLoader(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return (ServerPlatform.Vanilla, null);
        }

        var dash = id.IndexOf('-', StringComparison.Ordinal);
        var name = dash < 0 ? id : id[..dash];
        var version = dash < 0 ? null : id[(dash + 1)..];
        return name.ToLowerInvariant() switch
        {
            "neoforge" => (ServerPlatform.NeoForge, version),
            "forge" => (ServerPlatform.Forge, version),
            "fabric" => (ServerPlatform.Fabric, version),
            "quilt" => (ServerPlatform.Quilt, version),
            _ => throw new InvalidDataException($"Unsupported mod loader in modpack: {id}"),
        };
    }

    /// <summary>
    /// Returns the full path of <paramref name="relative"/> inside <paramref name="root"/>; throws for absolute paths,
    /// drive letters, ".." segments or anything else that would leave the folder.
    /// </summary>
    public static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':', StringComparison.Ordinal)
            || relative.Replace('\\', '/').Split('/').Any(s => s == ".."))
        {
            throw new InvalidDataException($"Unsafe path in modpack: {relative}");
        }

        var baseDir = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(baseDir, relative));
        return full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) && full.Length > baseDir.Length
            ? full
            : throw new InvalidDataException($"Unsafe path in modpack: {relative}");
    }

    /// <summary>Parses modrinth.index.json. Files the server does not use (env.server = unsupported) are left out.</summary>
    public static ModpackManifest ParseModrinthIndex(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("game", out var game) || game.GetString() != "minecraft")
        {
            throw new InvalidDataException("Not a Minecraft modpack.");
        }

        var deps = root.TryGetProperty("dependencies", out var d) && d.ValueKind == JsonValueKind.Object
            ? d.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal)
            : [];
        if (!deps.TryGetValue("minecraft", out var mc) || string.IsNullOrWhiteSpace(mc))
        {
            throw new InvalidDataException("The modpack does not name a Minecraft version.");
        }

        var (platform, loader) = MapDependencies(deps);
        var files = new List<ModpackFile>();
        foreach (var f in Items(root, "files"))
        {
            if (f.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object
                && env.TryGetProperty("server", out var server) && server.GetString() == "unsupported")
            {
                continue;
            }

            var path = f.GetProperty("path").GetString() ?? string.Empty;
            SafePath("C:\\pack", path);
            var urls = f.GetProperty("downloads").EnumerateArray()
                .Select(u => Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri) ? uri : null)
                .OfType<Uri>().Where(u => IsAllowedHost(u)).ToList();
            if (urls.Count == 0)
            {
                throw new InvalidDataException($"No download from an allowed host for {path}.");
            }

            var sha512 = f.TryGetProperty("hashes", out var h) && h.TryGetProperty("sha512", out var s) ? s.GetString() : null;
            if (string.IsNullOrWhiteSpace(sha512))
            {
                throw new InvalidDataException($"No SHA-512 hash for {path}.");
            }

            files.Add(new ModpackFile(path.Replace('\\', '/'), urls, sha512, HashAlgorithmName.SHA512, f.TryGetProperty("fileSize", out var size) ? size.GetInt64() : 0));
        }

        return new ModpackManifest(ModpackFormat.Modrinth,
            root.TryGetProperty("name", out var n) ? n.GetString() ?? "Modpack" : "Modpack",
            root.TryGetProperty("versionId", out var v) ? v.GetString() : null,
            platform, mc, loader, files, [], ["overrides", "server-overrides"]);
    }

    /// <summary>Parses a CurseForge manifest.json. Its files are project/file ids that still need the CurseForge API.</summary>
    public static ModpackManifest ParseCurseForgeManifest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("minecraft", out var mc) || !mc.TryGetProperty("version", out var ver) || string.IsNullOrWhiteSpace(ver.GetString()))
        {
            throw new InvalidDataException("Not a CurseForge modpack (minecraft.version missing).");
        }

        var loaders = mc.TryGetProperty("modLoaders", out var ml) && ml.ValueKind == JsonValueKind.Array ? ml.EnumerateArray().ToList() : [];
        var primary = loaders.FirstOrDefault(l => l.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True);
        if (primary.ValueKind == JsonValueKind.Undefined && loaders.Count > 0)
        {
            primary = loaders[0];
        }

        var (platform, loader) = MapCurseForgeLoader(primary.ValueKind == JsonValueKind.Object && primary.TryGetProperty("id", out var id) ? id.GetString() : null);
        var refs = Items(root, "files")
            .Select(f => new CurseForgeFileRef(f.GetProperty("projectID").GetInt32(), f.GetProperty("fileID").GetInt32(),
                !f.TryGetProperty("required", out var r) || r.ValueKind != JsonValueKind.False))
            .ToList();
        var overrides = root.TryGetProperty("overrides", out var o) && o.GetString() is { Length: > 0 } folder ? folder : "overrides";
        SafePath("C:\\pack", overrides);
        return new ModpackManifest(ModpackFormat.CurseForge,
            root.TryGetProperty("name", out var n) ? n.GetString() ?? "Modpack" : "Modpack",
            root.TryGetProperty("version", out var v) ? v.GetString() : null,
            platform, ver.GetString()!, loader, [], refs, [overrides.Trim('/')]);
    }

    /// <summary>Reads the manifest of a .mrpack or a CurseForge modpack zip.</summary>
    public static ModpackManifest Read(string archive)
    {
        try
        {
            return ReadUnchecked(archive);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or FormatException or InvalidOperationException or JsonException)
        {
            throw new InvalidDataException("The modpack manifest is malformed.", ex);
        }
    }

    private static ModpackManifest ReadUnchecked(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.GetEntry("modrinth.index.json") is { } mr)
        {
            using var r = new StreamReader(mr.Open());
            return ParseModrinthIndex(r.ReadToEnd());
        }

        if (zip.GetEntry("manifest.json") is { } cf)
        {
            using var r = new StreamReader(cf.Open());
            return ParseCurseForgeManifest(r.ReadToEnd());
        }

        throw new InvalidDataException("Not a modpack: neither modrinth.index.json nor manifest.json was found.");
    }

    /// <summary>
    /// The override files to copy, in order: everything in the first folder, then the next one, so a later folder
    /// (server-overrides) wins over an earlier one (overrides). Each target is checked against <paramref name="serverDir"/>.
    /// </summary>
    public static IReadOnlyList<(string Entry, string Target)> PlanOverrides(IEnumerable<string> entryNames, IReadOnlyList<string> folders, string serverDir)
    {
        var names = entryNames.ToList();
        var plan = new List<(string, string)>();
        foreach (var folder in folders)
        {
            var prefix = folder.TrimEnd('/') + "/";
            foreach (var e in names.Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && !n.EndsWith('/')))
            {
                plan.Add((e, SafePath(serverDir, e[prefix.Length..])));
            }
        }

        return plan;
    }

    /// <summary>Copies the pack's override folders into the server; returns how many files were written.</summary>
    public static int ApplyOverrides(string archive, ModpackManifest manifest, string serverDir)
    {
        using var zip = ZipFile.OpenRead(archive);
        var plan = PlanOverrides(zip.Entries.Select(e => e.FullName), manifest.OverrideFolders, serverDir);
        foreach (var (entry, target) in plan)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            zip.GetEntry(entry)!.ExtractToFile(target, overwrite: true);
        }

        return plan.Count;
    }

    /// <summary>Downloads the pack files four at a time, each verified against its hash. Reports (done, total).</summary>
    public static async Task DownloadFilesAsync(HttpClient http, IReadOnlyList<ModpackFile> files, string serverDir, ModpackFormat format, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var done = 0;
        progress?.Report((0, files.Count));
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (f, token) =>
        {
            var target = SafePath(serverDir, f.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Exception? last = null;
            foreach (var url in f.Downloads.Where(u => IsAllowedHost(u, format)))
            {
                try
                {
                    await VerifiedDownloader.DownloadAsync(http, url, target, f.Hash, f.Algorithm, MaxFileBytes, null, token).ConfigureAwait(false);
                    last = null;
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or CryptographicException)
                {
                    last = ex;
                }
            }

            if (last is not null || !File.Exists(target))
            {
                throw new InvalidOperationException($"Download failed for {f.Path}: {last?.Message ?? "no allowed source"}", last);
            }

            progress?.Report((Interlocked.Increment(ref done), files.Count));
        }).ConfigureAwait(false);
    }

    /// <summary>Puts the chosen port back in server.properties after an override replaced it.</summary>
    public static string WithPort(string properties, int port)
    {
        var lines = properties.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(l => !l.StartsWith("server-port=", StringComparison.Ordinal)).ToList();
        var insert = lines.Count > 0 && lines[^1].Length == 0 ? lines.Count - 1 : lines.Count;
        lines.Insert(insert, $"server-port={port.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        return string.Join('\n', lines);
    }
}

/// <summary>Creates a server from a modpack: loader server through <see cref="ServerCreator"/>, then pack files and overrides.</summary>
public sealed class ModpackInstaller(ServerCreator creator, HttpClient http)
{
    /// <summary>
    /// <paramref name="request"/> supplies name, folder, Java, memory, port and properties; platform and version come from
    /// the pack. For CurseForge packs <paramref name="resolvedFiles"/> holds the files the API resolved.
    /// Progress steps: the creator's steps, then "modpack_files" with (done, total), then "modpack_overrides".
    /// </summary>
    public async Task<ServerDefinition> InstallAsync(ServerCreator.Request request, string archive, ModpackManifest manifest, IReadOnlyList<ModpackFile>? resolvedFiles,
        IProgress<(double Progress, string Step)>? progress, IProgress<(int Done, int Total)>? files, CancellationToken ct)
    {
        if (manifest.Platform == ServerPlatform.Vanilla && manifest.Format == ModpackFormat.Modrinth && manifest.Files.Count > 0)
        {
            throw new InvalidOperationException("The modpack does not name a supported mod loader.");
        }

        var r = request with { Platform = manifest.Platform, Version = manifest.MinecraftVersion };
        var def = await creator.CreateAsync(r, new Progress<(double Progress, string Step)>(p =>
        {
            // The creator's "done" is only half of this job.
            if (p.Step != "done")
            {
                progress?.Report((p.Progress * 0.5, p.Step));
            }
        }), ct).ConfigureAwait(false);
        try
        {
            var list = manifest.Format == ModpackFormat.CurseForge ? resolvedFiles ?? [] : manifest.Files;
            progress?.Report((0.5, "modpack_files"));
            await Modpacks.DownloadFilesAsync(http, list, def.Directory, manifest.Format,
                new Progress<(int Done, int Total)>(p =>
                {
                    progress?.Report((0.5 + (0.45 * p.Done / Math.Max(1, p.Total)), "modpack_files"));
                    files?.Report(p);
                }), ct).ConfigureAwait(false);

            progress?.Report((0.96, "modpack_overrides"));
            Modpacks.ApplyOverrides(archive, manifest, def.Directory);
            var props = Path.Combine(def.Directory, "server.properties");
            if (File.Exists(props))
            {
                await File.WriteAllTextAsync(props, Modpacks.WithPort(await File.ReadAllTextAsync(props, ct).ConfigureAwait(false), r.Port), ct).ConfigureAwait(false);
            }

            progress?.Report((1, "done"));
            return def;
        }
        catch
        {
            // The folder was created empty by CreateAsync for this server; do not leave half a modpack behind.
            try
            {
                Core.FileSystem.DeleteDirectory(def.Directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leftovers are harmless; the original error is what the user needs to see.
                System.Diagnostics.Trace.WriteLine($"Modpack cleanup failed: {ex.Message}");
            }

            throw;
        }
    }
}
