using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using PackCheck.Core;
using PackCheck.Validation;

namespace PackCheck.Servers;

/// <summary>Copies a server folder into a new server: new Id, name and port, worlds optional.</summary>
public static class ServerCloner
{
    /// <summary>Folders and files a copy never takes: rebuilt by the server, locked while it runs, or backups.</summary>
    public static bool IsSkipped(string rel)
    {
        rel = rel.Replace('\\', '/');
        return Backups.IsExcluded(rel)
            || rel.StartsWith(".packcheck-backups/", StringComparison.OrdinalIgnoreCase)
            || rel.Contains("/.packcheck-backups/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Relative paths (with '/') of the files a copy takes.</summary>
    public static IEnumerable<string> SelectFiles(string root, bool includeWorlds, bool includeLibraries = true)
    {
        var worlds = Backups.WorldFolders(root);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (IsSkipped(rel))
            {
                continue;
            }

            var top = rel.Contains('/') ? rel[..rel.IndexOf('/')] : null;
            if (top is not null && !includeWorlds && worlds.Contains(top))
            {
                continue;
            }

            if (top is not null && !includeLibraries && (top.Equals("libraries", StringComparison.OrdinalIgnoreCase) || top.Equals("versions", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            yield return rel;
        }
    }

    /// <summary>Strips characters Windows does not allow in a folder name.</summary>
    public static string SafeName(string name) =>
        string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim().TrimEnd('.');

    /// <summary>"<parent>/<name>", or "<name> (2)", "<name> (3)" ... when that folder already exists.</summary>
    public static string UniqueFolder(string parent, string name)
    {
        var safe = SafeName(name);
        if (safe.Length == 0)
        {
            throw new ArgumentException("Invalid server name.", nameof(name));
        }

        var dir = Path.Combine(parent, safe);
        for (var i = 2; Directory.Exists(dir) || File.Exists(dir); i++)
        {
            dir = Path.Combine(parent, $"{safe} ({i})");
        }

        return dir;
    }

    private static readonly Lock ReserveGate = new();

    /// <summary>Picks a unique folder and creates it under one lock, so two copies never get the same folder.</summary>
    public static string ReserveFolder(string parent, string name)
    {
        lock (ReserveGate)
        {
            var dir = UniqueFolder(parent, name);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>First port from <paramref name="start"/> that no registered server uses and nothing listens on.</summary>
    public static int FreePort(IEnumerable<int> taken, int start = 25565)
    {
        var used = taken.ToHashSet();
        for (var p = start; p < start + 200 && p <= 65535; p++)
        {
            if (used.Contains(p))
            {
                continue;
            }

            try
            {
                var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, p);
                l.Start();
                l.Stop();
                return p;
            }
            catch (System.Net.Sockets.SocketException)
            {
            }
        }

        return start;
    }

    /// <summary>Writes server-port (and query.port when it was set) into server.properties, keeping the rest.</summary>
    public static void SetPort(string dir, int port)
    {
        var file = Path.Combine(dir, "server.properties");
        var original = File.Exists(file) ? File.ReadAllText(file) : string.Empty;
        var value = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var values = new Dictionary<string, string> { ["server-port"] = value };
        if (ServerProperties.Parse(original).ContainsKey("query.port"))
        {
            values["query.port"] = value;
        }

        File.WriteAllText(file, ServerProperties.Update(original, values));
    }

    /// <summary>Copies <paramref name="source"/> into a new folder next to it and returns the new definition (not registered).</summary>
    public static async Task<ServerDefinition> CloneAsync(ServerDefinition source, string name, bool includeWorlds, int port, CancellationToken ct)
    {
        var src = Path.GetFullPath(source.Directory);
        var parent = Path.GetDirectoryName(src) ?? throw new InvalidOperationException("The server folder has no parent.");
        var target = ReserveFolder(parent, name);
        try
        {
            foreach (var rel in SelectFiles(src, includeWorlds))
            {
                ct.ThrowIfCancellationRequested();
                var to = Path.Combine(target, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);

                // Shared read so a running server's open files can still be copied.
                await using var input = new FileStream(Path.Combine(src, rel), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                await using var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }

            SetPort(target, port);
        }
        catch
        {
            TryDelete(target);
            throw;
        }

        return source with
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = Path.GetFileName(target),
            Directory = target,
            Port = port,
            CreatedAt = DateTimeOffset.Now,
        };
    }

    public static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                FileSystem.DeleteDirectory(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>What a saved template holds besides its files (template.json).</summary>
public sealed record ServerTemplate
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required ServerPlatform Platform { get; init; }
    public required string MinecraftVersion { get; init; }
    public required string JarFile { get; init; }
    public string? ArgsFile { get; init; }
    public int MinMemoryMb { get; init; } = 1024;
    public int MaxMemoryMb { get; init; } = 4096;
    public bool UseAikarFlags { get; init; } = true;
    public int CpuCores { get; init; }
    public DateTimeOffset Created { get; init; } = DateTimeOffset.Now;
}

/// <summary>Server templates: a zip of a server without its worlds plus template.json, under DataRoot/templates/&lt;id&gt;.</summary>
public sealed class ServerTemplates(string? root = null)
{
    public const string ManifestName = "template.json";
    public const string ArchiveName = "files.zip";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public string Root { get; } = root ?? Path.Combine(AppPaths.DataRoot, "templates");

    public IReadOnlyList<ServerTemplate> List()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        var result = new List<ServerTemplate>();
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            var file = Path.Combine(dir, ManifestName);
            if (!File.Exists(file) || !File.Exists(Path.Combine(dir, ArchiveName)))
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<ServerTemplate>(File.ReadAllText(file), Json) is { } t && t.Id == Path.GetFileName(dir))
                {
                    result.Add(t);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }

        return [.. result.OrderByDescending(t => t.Created)];
    }

    /// <summary>Saves <paramref name="server"/> without worlds. Libraries are kept only when the launch needs them (Forge/NeoForge args file).</summary>
    public async Task<ServerTemplate> SaveAsync(ServerDefinition server, string name, CancellationToken ct)
    {
        var clean = name.Trim();
        if (clean.Length == 0)
        {
            throw new ArgumentException("Invalid template name.", nameof(name));
        }

        var template = new ServerTemplate
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = clean,
            Platform = server.Platform,
            MinecraftVersion = server.MinecraftVersion,
            JarFile = server.JarFile,
            ArgsFile = server.ArgsFile,
            MinMemoryMb = server.MinMemoryMb,
            MaxMemoryMb = server.MaxMemoryMb,
            UseAikarFlags = server.UseAikarFlags,
            CpuCores = server.CpuCores,
        };
        var dir = Path.Combine(Root, template.Id);
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.GetFullPath(server.Directory);
            var keepLibraries = server.ArgsFile is not null || server.JarFile.StartsWith("libraries", StringComparison.OrdinalIgnoreCase);
            await using (var zipStream = new FileStream(Path.Combine(dir, ArchiveName), FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                foreach (var rel in ServerCloner.SelectFiles(src, includeWorlds: false, includeLibraries: keepLibraries))
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = zip.CreateEntry(rel, CompressionLevel.Fastest);
                    await using var input = new FileStream(Path.Combine(src, rel), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    await using var output = entry.Open();
                    await input.CopyToAsync(output, ct).ConfigureAwait(false);
                }

                var manifest = zip.CreateEntry(ManifestName);
                await using var m = manifest.Open();
                await JsonSerializer.SerializeAsync(m, template, Json, ct).ConfigureAwait(false);
            }

            await File.WriteAllTextAsync(Path.Combine(dir, ManifestName), JsonSerializer.Serialize(template, Json), ct).ConfigureAwait(false);
        }
        catch
        {
            ServerCloner.TryDelete(dir);
            throw;
        }

        return template;
    }

    public void Delete(string id)
    {
        if (id.Length == 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains(".."))
        {
            throw new ArgumentException("Invalid template id.", nameof(id));
        }

        ServerCloner.TryDelete(Path.Combine(Root, id));
    }

    /// <summary>Extracts a template into a new folder under <paramref name="parentFolder"/> and returns the definition (not registered).</summary>
    public async Task<ServerDefinition> CreateServerAsync(ServerTemplate template, string parentFolder, string name, int port, string javaPath, CancellationToken ct)
    {
        var archive = Path.Combine(Root, template.Id, ArchiveName);
        if (!File.Exists(archive))
        {
            throw new FileNotFoundException("The template archive is missing.", archive);
        }

        Directory.CreateDirectory(parentFolder);
        var target = ServerCloner.ReserveFolder(parentFolder, name);
        var full = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        try
        {
            await using var zipStream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FullName == ManifestName || entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                var to = Path.GetFullPath(Path.Combine(target, entry.FullName));
                if (!to.StartsWith(full, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"The template contains an unsafe path: {entry.FullName}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                await using var input = entry.Open();
                await using var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }

            ServerCloner.SetPort(target, port);
        }
        catch
        {
            ServerCloner.TryDelete(target);
            throw;
        }

        return new ServerDefinition
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = Path.GetFileName(target),
            Directory = target,
            Platform = template.Platform,
            MinecraftVersion = template.MinecraftVersion,
            JarFile = template.JarFile,
            ArgsFile = template.ArgsFile,
            JavaPath = javaPath,
            MinMemoryMb = template.MinMemoryMb,
            MaxMemoryMb = template.MaxMemoryMb,
            UseAikarFlags = template.UseAikarFlags,
            CpuCores = template.CpuCores,
            Port = port,
        };
    }
}
