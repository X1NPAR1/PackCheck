using System.IO.Compression;
using System.Text.Json;

namespace PackCheck.Servers;

/// <summary>What a backup contains.</summary>
public enum BackupKind
{
    /// <summary>The whole server folder (minus logs, caches and lock files).</summary>
    Full,

    /// <summary>Only world folders (see <see cref="IsWorldFolder"/>).</summary>
    Worlds,

    /// <summary>Only settings: server.properties, *.yml/*.toml/*.json in the root, config/ and plugin configs (no jars, no worlds).</summary>
    Config,
}

/// <summary>Stored as packcheck-backup.json inside every zip.</summary>
public sealed record BackupManifest(BackupKind Kind, string? Note, DateTimeOffset CreatedAt, string Server, string Platform, string Version, int Files, int Skipped);

public sealed record BackupInfo(string Path, DateTimeOffset CreatedAt, long Size)
{
    public BackupKind Kind { get; init; } = BackupKind.Full;
    public string? Note { get; init; }
    public bool Pinned { get; init; }
    public int Skipped { get; init; }

    /// <summary>Minecraft version the server ran when the backup was taken (from the manifest).</summary>
    public string? Version { get; init; }
}

/// <summary>Grandfather-father-son retention: the newest <see cref="KeepLast"/>, plus the newest backup of each of the last
/// <see cref="KeepDaily"/> days and <see cref="KeepWeekly"/> ISO weeks. Pinned backups are never removed.</summary>
public sealed record RetentionPolicy(int KeepLast, int KeepDaily = 0, int KeepWeekly = 0)
{
    public IReadOnlySet<T> Keep<T>(IReadOnlyList<T> items, Func<T, DateTimeOffset> time, Func<T, bool> pinned)
        where T : notnull
    {
        var ordered = items.OrderByDescending(time).ToList();
        var keep = new HashSet<T>(ordered.Where(pinned));
        foreach (var i in ordered.Take(Math.Max(1, KeepLast)))
        {
            keep.Add(i);
        }

        foreach (var day in ordered.GroupBy(i => time(i).LocalDateTime.Date).Take(KeepDaily))
        {
            keep.Add(day.First());
        }

        foreach (var week in ordered.GroupBy(i => (System.Globalization.ISOWeek.GetYear(time(i).LocalDateTime), System.Globalization.ISOWeek.GetWeekOfYear(time(i).LocalDateTime))).Take(KeepWeekly))
        {
            keep.Add(week.First());
        }

        return keep;
    }
}

public static class Backups
{
    public const string ManifestName = "packcheck-backup.json";
    private const string PinnedSuffix = "-pinned";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    /// <summary>Never backed up: they are rebuilt by the server, locked while it runs, or are other backups.</summary>
    public static readonly string[] AlwaysExcluded = ["logs/", "cache/", "crash-reports/", "debug/", ".packcheck/metrics/", "session.lock", ".restore-staging/"];

    private static readonly string[] ConfigExtensions = [".properties", ".yml", ".yaml", ".toml", ".json", ".json5", ".conf", ".cfg", ".txt", ".ini"];

    public static string BackupDir(ServerDefinition s) => Path.Combine(s.Directory, "..", ".packcheck-backups", Path.GetFileName(s.Directory));

    /// <summary>Relative paths (with '/') of the files a backup of <paramref name="kind"/> includes.</summary>
    public static IEnumerable<string> SelectFiles(string root, BackupKind kind)
    {
        var worlds = WorldFolders(root);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (IsExcluded(rel))
            {
                continue;
            }

            var top = rel.Contains('/') ? rel[..rel.IndexOf('/')] : null;
            var include = kind switch
            {
                BackupKind.Worlds => top is not null && worlds.Contains(top),
                BackupKind.Config => (top is null || !worlds.Contains(top))
                    && ConfigExtensions.Contains(Path.GetExtension(rel).ToLowerInvariant())
                    && !rel.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase)
                    && !rel.StartsWith("versions/", StringComparison.OrdinalIgnoreCase)
                    && !rel.StartsWith("bundler/", StringComparison.OrdinalIgnoreCase),
                _ => true,
            };
            if (include)
            {
                yield return rel;
            }
        }
    }

    public static bool IsExcluded(string rel) =>
        AlwaysExcluded.Any(x => x.EndsWith('/') ? rel.StartsWith(x, StringComparison.OrdinalIgnoreCase) || rel.Contains("/" + x, StringComparison.OrdinalIgnoreCase) : rel.EndsWith(x, StringComparison.OrdinalIgnoreCase))
        || rel.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);

    /// <summary>Files and folders that only a world folder has; a world that was never saved yet has no level.dat but a session.lock.</summary>
    private static readonly string[] WorldMarkers = ["level.dat", "session.lock", "region", "DIM-1", "DIM1", "dimensions"];

    public static bool IsWorldFolder(string dir) =>
        WorldMarkers.Any(m => File.Exists(Path.Combine(dir, m)) || Directory.Exists(Path.Combine(dir, m)));

    /// <summary>Top-level folders that hold a world.</summary>
    public static HashSet<string> WorldFolders(string root) =>
        Directory.Exists(root)
            ? [.. Directory.EnumerateDirectories(root).Where(IsWorldFolder).Select(Path.GetFileName).OfType<string>()]
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Zips the selected files. Files the running server holds open are read with shared access; files that still
    /// cannot be read are skipped and counted, so a backup never fails because of one locked file.
    /// </summary>
    public static async Task<BackupInfo> CreateAsync(ServerDefinition s, BackupKind kind, string? note, RetentionPolicy retention, string? mirrorDir, CancellationToken ct)
    {
        var dir = Path.GetFullPath(BackupDir(s));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{kind.ToString().ToLowerInvariant()}.zip");
        var temp = file + ".part";
        var (count, skipped) = await Task.Run(() => Write(s, kind, note, temp, ct), ct).ConfigureAwait(false);
        File.Move(temp, file, overwrite: true);
        ApplyRetention(dir, retention);

        if (!string.IsNullOrWhiteSpace(mirrorDir))
        {
            var mirror = Path.Combine(mirrorDir, Path.GetFileName(s.Directory));
            Directory.CreateDirectory(mirror);
            File.Copy(file, Path.Combine(mirror, Path.GetFileName(file)), overwrite: true);
            ApplyRetention(mirror, retention);
        }

        var fi = new FileInfo(file);
        return new BackupInfo(file, fi.CreationTime, fi.Length) { Kind = kind, Note = note, Skipped = skipped };
    }

    /// <summary>Compatibility overload: full backup, keep the newest <paramref name="keep"/>.</summary>
    public static Task<BackupInfo> CreateAsync(ServerDefinition s, int keep, CancellationToken ct) =>
        CreateAsync(s, BackupKind.Full, null, new RetentionPolicy(keep), null, ct);

    private static (int Files, int Skipped) Write(ServerDefinition s, BackupKind kind, string? note, string zipPath, CancellationToken ct)
    {
        int files = 0, skipped = 0;
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var rel in SelectFiles(s.Directory, kind))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var src = new FileStream(Path.Combine(s.Directory, rel), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var entry = zip.CreateEntry(rel, CompressionLevel.Fastest);
                    entry.LastWriteTime = File.GetLastWriteTime(Path.Combine(s.Directory, rel));
                    using var dst = entry.Open();
                    src.CopyTo(dst);
                    files++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                }
            }

            var manifest = new BackupManifest(kind, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), DateTimeOffset.Now, s.Name, s.Platform.ToString(), s.MinecraftVersion, files, skipped);
            using var m = zip.CreateEntry(ManifestName).Open();
            JsonSerializer.Serialize(m, manifest, Json);
        }

        return (files, skipped);
    }

    public static IReadOnlyList<BackupInfo> List(ServerDefinition s)
    {
        var dir = Path.GetFullPath(BackupDir(s));
        return !Directory.Exists(dir)
            ? []
            : Directory.EnumerateFiles(dir, "*.zip").Select(f => new FileInfo(f)).OrderByDescending(f => f.Name, StringComparer.Ordinal).Select(Read).ToList();
    }

    private static BackupInfo Read(FileInfo f)
    {
        var info = new BackupInfo(f.FullName, f.CreationTime, f.Length) { Pinned = Path.GetFileNameWithoutExtension(f.Name).EndsWith(PinnedSuffix, StringComparison.Ordinal) };
        try
        {
            using var zip = ZipFile.OpenRead(f.FullName);
            if (zip.GetEntry(ManifestName) is { } e)
            {
                using var stream = e.Open();
                if (JsonSerializer.Deserialize<BackupManifest>(stream, Json) is { } m)
                {
                    return info with { CreatedAt = m.CreatedAt, Kind = m.Kind, Note = m.Note, Skipped = m.Skipped, Version = m.Version };
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException)
        {
        }

        return info;
    }

    public static void ApplyRetention(string dir, RetentionPolicy policy)
    {
        var all = Directory.EnumerateFiles(dir, "*.zip").Select(f => new FileInfo(f)).ToList();
        var keep = policy.Keep(all, f => f.CreationTime, f => Path.GetFileNameWithoutExtension(f.Name).EndsWith(PinnedSuffix, StringComparison.Ordinal));
        foreach (var f in all.Where(f => !keep.Contains(f)))
        {
            f.Delete();
        }
    }

    public static void ApplyRetention(string dir, int keep) => ApplyRetention(dir, new RetentionPolicy(keep));

    /// <summary>Pinned backups survive retention. Pinning renames the file; returns the new info.</summary>
    public static BackupInfo SetPinned(BackupInfo b, bool pinned)
    {
        var name = Path.GetFileNameWithoutExtension(b.Path);
        var bare = name.EndsWith(PinnedSuffix, StringComparison.Ordinal) ? name[..^PinnedSuffix.Length] : name;
        var target = Path.Combine(Path.GetDirectoryName(b.Path)!, bare + (pinned ? PinnedSuffix : string.Empty) + ".zip");
        if (!string.Equals(target, b.Path, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(b.Path, target);
        }

        return b with { Path = target, Pinned = pinned };
    }

    /// <summary>Top-level items of a backup (folders end with '/'), with their uncompressed size.</summary>
    public static IReadOnlyList<(string Name, long Size, bool IsWorld)> Contents(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var worlds = zip.Entries.Where(e => e.FullName.Contains('/') && WorldMarkers.Any(m =>
                e.FullName.Equals(e.FullName[..e.FullName.IndexOf('/')] + "/" + m, StringComparison.Ordinal)
                || e.FullName.StartsWith(e.FullName[..e.FullName.IndexOf('/')] + "/" + m + "/", StringComparison.Ordinal)))
            .Select(e => e.FullName[..e.FullName.IndexOf('/')]).ToHashSet(StringComparer.Ordinal);
        return zip.Entries.Where(e => e.FullName != ManifestName && e.Name.Length > 0)
            .GroupBy(e => e.FullName.Contains('/') ? e.FullName[..(e.FullName.IndexOf('/') + 1)] : e.FullName)
            .Select(g => (g.Key, g.Sum(e => e.Length), worlds.Contains(g.Key.TrimEnd('/'))))
            .OrderByDescending(x => x.Item3).ThenBy(x => !x.Key.EndsWith('/')).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Restores the given top-level items (null = everything in the backup). Each item is first extracted to a staging
    /// folder, then the old copy is replaced, so a failed extract never leaves a half-restored world.
    /// Files that are not in the backup (logs, excluded files, other worlds) are left alone.
    /// </summary>
    public static async Task RestoreAsync(ServerDefinition s, BackupInfo backup, IReadOnlyCollection<string>? items, CancellationToken ct)
    {
        var root = Path.GetFullPath(s.Directory);
        var staging = Path.Combine(root, ".restore-staging");
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, true);
        }

        await Task.Run(
            () =>
            {
                using var zip = ZipFile.OpenRead(backup.Path);
                var wanted = items?.ToHashSet(StringComparer.Ordinal);
                var restored = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (e.FullName == ManifestName || e.Name.Length == 0)
                    {
                        continue;
                    }

                    var top = e.FullName.Contains('/') ? e.FullName[..(e.FullName.IndexOf('/') + 1)] : e.FullName;
                    if (wanted is not null && !wanted.Contains(top))
                    {
                        continue;
                    }

                    var dest = Path.GetFullPath(Path.Combine(staging, e.FullName));
                    if (!dest.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Unsafe path in backup: " + e.FullName);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    e.ExtractToFile(dest, overwrite: true);
                    restored.Add(top);
                }

                foreach (var top in restored)
                {
                    var from = Path.Combine(staging, top.TrimEnd('/'));
                    var to = Path.Combine(root, top.TrimEnd('/'));
                    if (top.EndsWith('/'))
                    {
                        // Folders that hold more than the backup (e.g. plugins/ in a config backup) are merged, worlds are replaced.
                        if (backup.Kind == BackupKind.Config && Directory.Exists(to))
                        {
                            CopyOver(from, to);
                            continue;
                        }

                        if (Directory.Exists(to))
                        {
                            Directory.Delete(to, true);
                        }

                        Directory.Move(from, to);
                    }
                    else
                    {
                        File.Move(from, to, overwrite: true);
                    }
                }
            },
            ct).ConfigureAwait(false);

        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, true);
        }
    }

    public static Task RestoreAsync(ServerDefinition s, BackupInfo backup, CancellationToken ct) => RestoreAsync(s, backup, null, ct);

    private static void CopyOver(string from, string to)
    {
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, overwrite: true);
        }
    }
}
