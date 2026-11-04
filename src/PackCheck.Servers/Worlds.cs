using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace PackCheck.Servers;

/// <summary>A world saved in the library: the zip holds one or more world folders (world, world_nether, world_the_end).</summary>
public sealed record LibraryWorld(string Id, string Name, string? SourceServer, string Platform, string MinecraftVersion, DateTimeOffset Created, long Size)
{
    public string Compatibility => $"{Platform} {MinecraftVersion}";
}

/// <summary>
/// Worlds kept outside any server under <c>{root}/{id}/world.zip</c> plus <c>world.json</c>.
/// Saving copies a server's world folders; installing replaces a stopped server's worlds (the old ones go to old-worlds/, nothing is deleted).
/// </summary>
public sealed class WorldLibrary(string root)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Root { get; } = root;

    public IReadOnlyList<LibraryWorld> List()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        var list = new List<LibraryWorld>();
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            try
            {
                if (JsonSerializer.Deserialize<LibraryWorld>(File.ReadAllText(Path.Combine(dir, "world.json"))) is { } w)
                {
                    list.Add(w);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A broken entry is skipped, the rest still shows.
            }
        }

        return list.OrderByDescending(w => w.Created).ToList();
    }

    public string ZipPath(LibraryWorld w) => Path.Combine(Root, w.Id, "world.zip");

    /// <summary>Zips the server's world folders into the library. Files the running server holds open are read with shared access.</summary>
    public LibraryWorld Save(ServerDefinition server, string name)
    {
        var worlds = Backups.WorldFolders(server.Directory);
        if (worlds.Count == 0)
        {
            throw new InvalidOperationException("This server has no world yet.");
        }

        var id = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        var dir = Path.Combine(Root, id);
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, "world.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var w in worlds)
            {
                var src = Path.Combine(server.Directory, w);
                foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                {
                    if (file.EndsWith("session.lock", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var rel = w + "/" + Path.GetRelativePath(src, file).Replace('\\', '/');
                    try
                    {
                        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var output = zip.CreateEntry(rel, CompressionLevel.Fastest).Open();
                        input.CopyTo(output);
                    }
                    catch (IOException)
                    {
                        // A region file being written right now; the save is still usable without it.
                    }
                }
            }
        }

        var entry = new LibraryWorld(id, string.IsNullOrWhiteSpace(name) ? server.Name : name.Trim(), server.Name, server.Platform.ToString(), server.MinecraftVersion, DateTimeOffset.Now, new FileInfo(zipPath).Length);
        File.WriteAllText(Path.Combine(dir, "world.json"), JsonSerializer.Serialize(entry, Json));
        return entry;
    }

    /// <summary>Adds a world zip from disk (a zip of world folders, or of one world's contents with level.dat at the top).</summary>
    public LibraryWorld Import(string zipFile, string name)
    {
        using (var check = ZipFile.OpenRead(zipFile))
        {
            if (!check.Entries.Any(e => e.FullName.EndsWith("level.dat", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("The zip does not contain a Minecraft world (no level.dat).");
            }
        }

        var id = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        var dir = Path.Combine(Root, id);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "world.zip");
        File.Copy(zipFile, target);
        var entry = new LibraryWorld(id, string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(zipFile) : name.Trim(), null, "?", "?", DateTimeOffset.Now, new FileInfo(target).Length);
        File.WriteAllText(Path.Combine(dir, "world.json"), JsonSerializer.Serialize(entry, Json));
        return entry;
    }

    public void Delete(LibraryWorld w) => Directory.Delete(Path.Combine(Root, w.Id), recursive: true);

    /// <summary>
    /// Replaces a stopped server's worlds with a library world. Current worlds move to old-worlds/ first. A zip with
    /// level.dat at its top level is installed as "world". Returns the folder the old worlds went to (or null).
    /// </summary>
    public string? Install(LibraryWorld w, ServerDefinition server)
    {
        using var zip = ZipFile.OpenRead(ZipPath(w));
        var plan = PlanInstall(zip.Entries.Where(e => e.Name.Length > 0).Select(e => e.FullName).ToList(), server.Directory);

        // Only after the whole archive checked out are the current worlds moved aside.
        var aside = UpgradeAdvisor.MoveWorldsAside(server.Directory, server.MinecraftVersion, DateTime.Now);
        foreach (var (entry, dest) in plan)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            zip.GetEntry(entry)!.ExtractToFile(dest, overwrite: true);
        }

        return aside;
    }

    /// <summary>
    /// Maps zip entries to destinations. A zip with level.dat at its top is one world ("world"); otherwise every entry must
    /// sit in a top-level folder that holds a level.dat. Any other entry, or a path leaving its world folder, rejects the archive.
    /// </summary>
    public static IReadOnlyList<(string Entry, string Dest)> PlanInstall(IReadOnlyList<string> entries, string serverDir)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(serverDir));
        var flat = entries.Any(e => e.Equals("level.dat", StringComparison.OrdinalIgnoreCase));
        var worlds = flat
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "world" }
            : entries.Where(e => e.Count(c => c == '/') == 1 && e.EndsWith("/level.dat", StringComparison.OrdinalIgnoreCase))
                .Select(e => e[..e.IndexOf('/')]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (worlds.Count == 0)
        {
            throw new InvalidDataException("The zip does not contain a Minecraft world (no level.dat).");
        }

        var plan = new List<(string, string)>();
        foreach (var e in entries)
        {
            var rel = flat ? "world/" + e : e;
            var slash = rel.IndexOf('/');
            var folder = slash > 0 ? rel[..slash] : string.Empty;
            if (!worlds.Contains(folder))
            {
                throw new InvalidDataException("The zip holds a file outside its world folders: " + e);
            }

            var worldRoot = Path.Combine(root, folder);
            var dest = Path.GetFullPath(Path.Combine(root, rel));
            if (!dest.StartsWith(worldRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Unsafe path in world zip: " + e);
            }

            plan.Add((e, dest));
        }

        return plan;
    }

    /// <summary>Moves the current worlds aside and sets <c>level-seed</c> so the next start generates a fresh world.</summary>
    public static string? ResetWithSeed(ServerDefinition server, string? seed)
    {
        var aside = UpgradeAdvisor.MoveWorldsAside(server.Directory, server.MinecraftVersion, DateTime.Now);
        var props = Path.Combine(server.Directory, "server.properties");
        var text = File.Exists(props) ? PropertiesText.ReadFile(props) : string.Empty;
        File.WriteAllText(props, PackCheck.Validation.ServerProperties.Update(text, new Dictionary<string, string> { ["level-seed"] = seed?.Trim() ?? string.Empty }), new UTF8Encoding(false));
        return aside;
    }
}

/// <summary>Minimal reader for Minecraft's NBT format (gzip or raw), enough to read player data.</summary>
public static class Nbt
{
    /// <summary>Reads a compound root. Values: byte/short/int/long/float/double, string, byte/int/long arrays, List&lt;object&gt;, Dictionary.</summary>
    public static Dictionary<string, object?> Read(Stream input)
    {
        var buffered = new MemoryStream();
        input.CopyTo(buffered);
        buffered.Position = 0;
        Stream s = buffered;
        if (buffered.Length > 2 && buffered.GetBuffer()[0] == 0x1F && buffered.GetBuffer()[1] == 0x8B)
        {
            s = new GZipStream(buffered, CompressionMode.Decompress);
        }

        using var r = new BinaryReader(s);
        var type = r.ReadByte();
        if (type != 10)
        {
            throw new InvalidDataException("Not an NBT compound.");
        }

        ReadString(r);
        return ReadCompound(r, 0);
    }

    /// <summary>Reads exactly <paramref name="n"/> bytes; truncated data is an error, never silently short.</summary>
    private static byte[] Exact(BinaryReader r, int n)
    {
        var b = r.ReadBytes(n);
        return b.Length == n ? b : throw new EndOfStreamException("The NBT data ends early.");
    }

    private static short I16(BinaryReader r) => System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(Exact(r, 2));

    private static int I32(BinaryReader r) => System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(Exact(r, 4));

    private static long I64(BinaryReader r) => System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(Exact(r, 8));

    private static string ReadString(BinaryReader r) => Encoding.UTF8.GetString(Exact(r, (ushort)I16(r)));

    private static Dictionary<string, object?> ReadCompound(BinaryReader r, int depth)
    {
        if (depth > 64)
        {
            throw new InvalidDataException("NBT nesting is too deep.");
        }

        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (true)
        {
            var type = r.ReadByte();
            if (type == 0)
            {
                return map;
            }

            var name = ReadString(r);
            map[name] = ReadPayload(r, type, depth + 1);
        }
    }

    private static object? ReadPayload(BinaryReader r, byte type, int depth) => type switch
    {
        1 => (sbyte)r.ReadByte(),
        2 => I16(r),
        3 => I32(r),
        4 => I64(r),
        5 => BitConverter.Int32BitsToSingle(I32(r)),
        6 => BitConverter.Int64BitsToDouble(I64(r)),
        7 => Exact(r, Count(I32(r))),
        8 => ReadString(r),
        9 => ReadList(r, depth),
        10 => ReadCompound(r, depth),
        11 => Enumerable.Range(0, Count(I32(r))).Select(_ => I32(r)).ToArray(),
        12 => Enumerable.Range(0, Count(I32(r))).Select(_ => I64(r)).ToArray(),
        _ => throw new InvalidDataException("Unknown NBT tag " + type),
    };

    private static int Count(int n) => n is >= 0 and <= 16_000_000 ? n : throw new InvalidDataException("Bad NBT length.");

    private static List<object?> ReadList(BinaryReader r, int depth)
    {
        if (depth > 64)
        {
            throw new InvalidDataException("NBT nesting is too deep.");
        }

        var type = r.ReadByte();
        var n = Count(I32(r));
        var list = new List<object?>(Math.Min(n, 4096));
        for (var i = 0; i < n; i++)
        {
            list.Add(ReadPayload(r, type, depth + 1));
        }

        return list;
    }
}

/// <summary>One stack in a player's inventory.</summary>
public sealed record InventoryItem(int Slot, string Id, int Count)
{
    /// <summary>"minecraft:diamond_sword" becomes "Diamond Sword".</summary>
    public string DisplayName => string.Join(' ', Id[(Id.IndexOf(':') + 1)..].Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    /// <summary>Where the item sits: hotbar 0-8, main 9-35, armor 100-103, offhand -106.</summary>
    public string Place => Slot switch
    {
        >= 0 and <= 8 => "hotbar",
        >= 9 and <= 35 => "inventory",
        >= 100 and <= 103 => "armor",
        -106 => "offhand",
        _ => "other",
    };
}

public sealed record PlayerInventory(IReadOnlyList<InventoryItem> Items, IReadOnlyList<InventoryItem> EnderChest, double? Health, int? XpLevel);

/// <summary>Reads world/playerdata/{uuid}.dat (works for offline players; the server writes it on save and on quit).</summary>
public static class PlayerData
{
    public static string? FileFor(string serverDir, string uuid)
    {
        foreach (var w in Backups.WorldFolders(serverDir).OrderBy(w => w.Length))
        {
            var f = Path.Combine(serverDir, w, "playerdata", uuid + ".dat");
            if (File.Exists(f))
            {
                return f;
            }
        }

        return null;
    }

    public static PlayerInventory Read(string datFile)
    {
        using var fs = new FileStream(datFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return FromNbt(Nbt.Read(fs));
    }

    public static PlayerInventory FromNbt(IReadOnlyDictionary<string, object?> root)
    {
        static List<InventoryItem> Items(object? list) => list is List<object?> l
            ? l.OfType<Dictionary<string, object?>>().Select(Item).OfType<InventoryItem>().OrderBy(i => i.Slot).ToList()
            : [];

        var health = root.TryGetValue("Health", out var h) && h is float hf ? hf : (double?)null;
        var level = root.TryGetValue("XpLevel", out var x) && x is int xi ? xi : (int?)null;
        return new PlayerInventory(Items(root.GetValueOrDefault("Inventory")), Items(root.GetValueOrDefault("EnderItems")), health, level);
    }

    /// <summary>Old saves use "Count" (byte), 1.20.5+ uses "count" (int).</summary>
    private static InventoryItem? Item(Dictionary<string, object?> c)
    {
        if (c.GetValueOrDefault("id") is not string id)
        {
            return null;
        }

        var slot = c.GetValueOrDefault("Slot") switch { sbyte b => b, int i => i, _ => -1 };
        var count = c.GetValueOrDefault("count") ?? c.GetValueOrDefault("Count");
        return new InventoryItem(slot, id, count switch { sbyte b => b, int i => i, short s => s, _ => 1 });
    }
}

/// <summary>BlueMap (live 3D map) install helpers: Modrinth project, loader names and the config switch it needs.</summary>
public static class BlueMapSetup
{
    public const string ModrinthProject = "bluemap";

    public const int DefaultPort = 8100;

    /// <summary>Modrinth loader name for the server, or null when BlueMap has no build for it.</summary>
    public static string? Loader(ServerPlatform p) => p switch
    {
        ServerPlatform.Paper or ServerPlatform.Purpur or ServerPlatform.Leaf or ServerPlatform.Leaves or ServerPlatform.Folia => "paper",
        ServerPlatform.Spigot or ServerPlatform.CraftBukkit => "spigot",
        ServerPlatform.Fabric or ServerPlatform.Quilt => "fabric",
        ServerPlatform.Forge => "forge",
        ServerPlatform.NeoForge => "neoforge",
        ServerPlatform.SpongeVanilla => "sponge",
        _ => null,
    };

    public static string ConfigDir(ServerDefinition s) =>
        PlatformInfo.UsesMods(s.Platform) ? Path.Combine(s.Directory, "config", "bluemap") : Path.Combine(s.Directory, "plugins", "BlueMap");

    /// <summary>BlueMap only renders after the user agrees to download Mojang's client resources (accept-download in core.conf).</summary>
    public static bool AcceptDownload(ServerDefinition s)
    {
        var core = Path.Combine(ConfigDir(s), "core.conf");
        if (!File.Exists(core))
        {
            return false;
        }

        var text = File.ReadAllText(core);
        var updated = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^(\s*accept-download\s*:\s*)false", "${1}true");
        File.WriteAllText(core, updated);
        return true;
    }

    public static int Port(ServerDefinition s)
    {
        var web = Path.Combine(ConfigDir(s), "webserver.conf");
        if (File.Exists(web) && System.Text.RegularExpressions.Regex.Match(File.ReadAllText(web), @"(?m)^\s*port\s*:\s*(\d+)") is { Success: true } m)
        {
            return int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        return DefaultPort;
    }
}
