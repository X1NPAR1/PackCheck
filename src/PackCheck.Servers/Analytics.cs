using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PackCheck.Servers;

public enum ActivityKind
{
    Chat,
    Join,
    Leave,
    Death,
    Advancement,
    Command,
    Server,
}

/// <summary>One classified log line.</summary>
public sealed record ActivityEvent(DateTime Time, ActivityKind Kind, string? Player, string Text);

/// <summary>Reads logs/latest.log and the gzipped older logs into a searchable timeline (chat, joins, leaves, deaths, advancements).</summary>
public static partial class ActivityLog
{
    [GeneratedRegex(@"^\[(\d{2}:\d{2}:\d{2})\] \[[^\]]+/INFO\]: (?:\[[^\]]+\]: )?(.*)$")]
    private static partial Regex LineRx();

    [GeneratedRegex(@"^(?:\[Not Secure\] )?<([A-Za-z0-9_]{1,16})> (.*)$")]
    private static partial Regex ChatRx();

    [GeneratedRegex(@"^([A-Za-z0-9_]{1,16}) joined the game$")]
    private static partial Regex JoinRx();

    [GeneratedRegex(@"^([A-Za-z0-9_]{1,16}) left the game$")]
    private static partial Regex LeaveRx();

    [GeneratedRegex(@"^([A-Za-z0-9_]{1,16}) has (?:made the advancement|completed the challenge|reached the goal) \[(.+)\]$")]
    private static partial Regex AdvancementRx();

    [GeneratedRegex(@"^([A-Za-z0-9_]{1,16}) issued server command: (.*)$")]
    private static partial Regex CommandRx();

    [GeneratedRegex(@"^([A-Za-z0-9_]{1,16}) (was |died|drowned|fell|blew up|burned|hit the ground|went up in flames|walked into|tried to swim|starved|suffocated|froze|withered|experienced kinetic|discovered the floor|was killed|didn't want to live)")]
    private static partial Regex DeathRx();

    [GeneratedRegex(@"^(Done \(|Stopping server|Starting minecraft server)")]
    private static partial Regex ServerRx();

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2})-\d+\.log\.gz$")]
    private static partial Regex GzNameRx();

    public static ActivityEvent? Classify(string line, DateTime day)
    {
        var m = LineRx().Match(line);
        if (!m.Success)
        {
            return null;
        }

        var time = day.Date + TimeSpan.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var msg = m.Groups[2].Value.Trim();
        if (ChatRx().Match(msg) is { Success: true } c)
        {
            return new(time, ActivityKind.Chat, c.Groups[1].Value, c.Groups[2].Value);
        }

        if (JoinRx().Match(msg) is { Success: true } j)
        {
            return new(time, ActivityKind.Join, j.Groups[1].Value, msg);
        }

        if (LeaveRx().Match(msg) is { Success: true } l)
        {
            return new(time, ActivityKind.Leave, l.Groups[1].Value, msg);
        }

        if (AdvancementRx().Match(msg) is { Success: true } a)
        {
            return new(time, ActivityKind.Advancement, a.Groups[1].Value, msg);
        }

        if (CommandRx().Match(msg) is { Success: true } cmd)
        {
            return new(time, ActivityKind.Command, cmd.Groups[1].Value, cmd.Groups[2].Value);
        }

        if (DeathRx().Match(msg) is { Success: true } d)
        {
            return new(time, ActivityKind.Death, d.Groups[1].Value, msg);
        }

        return ServerRx().IsMatch(msg) ? new(time, ActivityKind.Server, null, msg) : null;
    }

    /// <summary>Newest first, from up to <paramref name="days"/> days of logs.</summary>
    public static IReadOnlyList<ActivityEvent> Read(string serverDir, int days = 7)
    {
        var logs = Path.Combine(serverDir, "logs");
        var events = new List<ActivityEvent>();
        if (!Directory.Exists(logs))
        {
            return events;
        }

        var since = DateTime.Today.AddDays(-days);
        foreach (var gz in Directory.EnumerateFiles(logs, "*.log.gz"))
        {
            if (GzNameRx().Match(Path.GetFileName(gz)) is { Success: true } n
                && DateTime.TryParse(n.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day) && day >= since)
            {
                try
                {
                    using var r = new StreamReader(new GZipStream(File.OpenRead(gz), CompressionMode.Decompress));
                    ReadLines(r, day, events);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    // A damaged archive is skipped.
                }
            }
        }

        var latest = Path.Combine(logs, "latest.log");
        if (File.Exists(latest))
        {
            using var r = new StreamReader(new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            ReadLines(r, File.GetLastWriteTime(latest).Date, events);
        }

        return events.OrderByDescending(e => e.Time).ToList();
    }

    private static void ReadLines(StreamReader r, DateTime day, List<ActivityEvent> into)
    {
        while (r.ReadLine() is { } line)
        {
            if (Classify(line, day) is { } e)
            {
                into.Add(e);
            }
        }
    }
}

/// <summary>A folder or file and its size, for the storage view.</summary>
public sealed record StorageEntry(string Path, long Size, bool IsCleanable);

public sealed record StorageReport(long Total, IReadOnlyList<StorageEntry> Folders, IReadOnlyList<StorageEntry> LargestFiles, long Cleanable);

/// <summary>Where a server's disk space goes, and what can be removed safely (old logs, crash reports, caches).</summary>
public static class StorageAnalyzer
{
    private static readonly string[] CleanableFolders = ["logs", "crash-reports", "cache", ".paper-remapped", "debug"];

    public static StorageReport Analyze(string serverDir, int topFiles = 10)
    {
        var root = Path.GetFullPath(serverDir);
        var folders = new List<StorageEntry>();
        var files = new List<StorageEntry>();
        long total = 0;
        long cleanable = 0;
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            var rel = entry.Name;
            var isClean = CleanableFolders.Contains(rel, StringComparer.OrdinalIgnoreCase);
            long size;
            if (entry is DirectoryInfo d)
            {
                size = 0;
                foreach (var f in d.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    size += f.Length;
                    files.Add(new StorageEntry(Path.GetRelativePath(root, f.FullName).Replace('\\', '/'), f.Length, isClean));
                }

                if (isClean)
                {
                    cleanable += CleanableSize(d);
                }
            }
            else
            {
                size = ((FileInfo)entry).Length;
                files.Add(new StorageEntry(rel, size, false));
            }

            total += size;
            folders.Add(new StorageEntry(rel, size, isClean));
        }

        return new StorageReport(total, [.. folders.OrderByDescending(f => f.Size)], [.. files.OrderByDescending(f => f.Size).Take(topFiles)], cleanable);
    }

    /// <summary>Everything in the cleanable folders except latest.log (the running server writes it).</summary>
    private static long CleanableSize(DirectoryInfo d) =>
        d.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(f => !f.Name.Equals("latest.log", StringComparison.OrdinalIgnoreCase)).Sum(f => f.Length);

    /// <summary>Deletes old logs, crash reports and caches; returns the bytes freed. latest.log is kept.</summary>
    public static long Clean(string serverDir)
    {
        long freed = 0;
        foreach (var name in CleanableFolders)
        {
            var dir = new DirectoryInfo(Path.Combine(serverDir, name));
            if (!dir.Exists)
            {
                continue;
            }

            foreach (var f in dir.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                         .Where(f => !f.Name.Equals("latest.log", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                try
                {
                    var len = f.Length;
                    f.Delete();
                    freed += len;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use; left for next time.
                }
            }
        }

        return freed;
    }
}

/// <summary>One player's numbers from world/stats/{uuid}.json.</summary>
public sealed record PlayerStats(string Uuid, string Name, double HoursPlayed, int Deaths, int MobKills, int PlayerKills, int DiamondsMined, double KmWalked, int Jumps);

/// <summary>Reads the vanilla statistics files (written by every server type) into a scoreboard.</summary>
public static class Scoreboard
{
    private const string Custom = "minecraft:custom";

    public static PlayerStats Parse(string uuid, string name, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var stats = doc.RootElement.TryGetProperty("stats", out var s) ? s : default;
        int Get(string group, string key) =>
            stats.ValueKind == JsonValueKind.Object && stats.TryGetProperty(group, out var g) && g.TryGetProperty(key, out var v) && v.TryGetInt64(out var n) ? (int)Math.Min(n, int.MaxValue) : 0;
        long GetLong(string group, string key) =>
            stats.ValueKind == JsonValueKind.Object && stats.TryGetProperty(group, out var g) && g.TryGetProperty(key, out var v) && v.TryGetInt64(out var n) ? n : 0;

        // play_time is in ticks (20 per second); older versions call it play_one_minute (also ticks).
        var ticks = Math.Max(GetLong(Custom, "minecraft:play_time"), GetLong(Custom, "minecraft:play_one_minute"));
        var cm = GetLong(Custom, "minecraft:walk_one_cm") + GetLong(Custom, "minecraft:sprint_one_cm");
        var diamonds = Get("minecraft:mined", "minecraft:diamond_ore") + Get("minecraft:mined", "minecraft:deepslate_diamond_ore");
        return new PlayerStats(uuid, name, ticks / 20d / 3600d, Get(Custom, "minecraft:deaths"), Get(Custom, "minecraft:mob_kills"), Get(Custom, "minecraft:player_kills"),
            diamonds, cm / 100_000d, Get(Custom, "minecraft:jump"));
    }

    /// <summary>All players with a stats file; names come from usercache.json (else the UUID is shown).</summary>
    public static IReadOnlyList<PlayerStats> Read(string serverDir)
    {
        var lists = new PlayerLists(serverDir);
        var list = new List<PlayerStats>();
        foreach (var w in Backups.WorldFolders(serverDir).OrderBy(w => w.Length).Take(1))
        {
            var dir = Path.Combine(serverDir, w, "stats");
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
            {
                var uuid = Path.GetFileNameWithoutExtension(f);
                try
                {
                    list.Add(Parse(uuid, lists.NameFor(uuid) ?? uuid[..Math.Min(8, uuid.Length)], File.ReadAllText(f)));
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    // Skip a file being written.
                }
            }
        }

        return list.OrderByDescending(p => p.HoursPlayed).ToList();
    }
}

/// <summary>For a set of Modrinth projects, the newest Minecraft version and loader that all of them support.</summary>
public static class ModSolver
{
    public sealed record Solution(string Loader, string MinecraftVersion);

    /// <summary>
    /// <paramref name="support"/>: per project, the (loader, game version) pairs it has builds for. Versions are compared as
    /// Minecraft versions; snapshots are ignored. Loader preference breaks ties (fabric, neoforge, quilt, forge).
    /// </summary>
    public static Solution? Solve(IReadOnlyList<IReadOnlyCollection<(string Loader, string Version)>> support)
    {
        if (support.Count == 0)
        {
            return null;
        }

        string[] preference = ["fabric", "neoforge", "quilt", "forge"];
        Solution? best = null;
        Core.MinecraftVersion? bestVersion = null;
        foreach (var loader in preference)
        {
            var common = support
                .Select(s => s.Where(p => p.Loader == loader || (loader == "quilt" && p.Loader == "fabric")).Select(p => p.Version).ToHashSet())
                .Aggregate((a, b) => { a.IntersectWith(b); return a; });
            foreach (var v in common)
            {
                if (Core.MinecraftVersion.TryParse(v, out var mv) && !v.Contains('w', StringComparison.Ordinal) && !v.Contains('-', StringComparison.Ordinal)
                    && (bestVersion is null || mv > bestVersion.Value))
                {
                    bestVersion = mv;
                    best = new Solution(loader, v);
                }
            }
        }

        return best;
    }

    public static ServerPlatform Platform(string loader) => loader switch
    {
        "fabric" => ServerPlatform.Fabric,
        "quilt" => ServerPlatform.Quilt,
        "forge" => ServerPlatform.Forge,
        _ => ServerPlatform.NeoForge,
    };
}
