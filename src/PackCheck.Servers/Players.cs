using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PackCheck.Servers;

/// <summary>What PackCheck remembers about one player, built from the server log.</summary>
public sealed class PlayerRecord
{
    public required string Name { get; set; }
    public string? Uuid { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public long PlaySeconds { get; set; }
    public int Sessions { get; set; }
    public List<string> Ips { get; set; } = [];

    /// <summary>Start of the current session while the player is online.</summary>
    public DateTimeOffset? OnlineSince { get; set; }
}

/// <summary>
/// Follows join/leave/login lines and keeps per-player history in .packcheck/players.json:
/// first/last seen, total play time, sessions and IP addresses (for spotting alt accounts).
/// </summary>
public sealed partial class PlayerTracker
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Dictionary<string, PlayerRecord> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _file;

    public PlayerTracker(string? file = null)
    {
        _file = file;
        if (file is not null && File.Exists(file))
        {
            try
            {
                foreach (var p in JsonSerializer.Deserialize<List<PlayerRecord>>(File.ReadAllText(file), Json) ?? [])
                {
                    p.OnlineSince = null;
                    _players[p.Name] = p;
                }
            }
            catch (JsonException)
            {
            }
        }
    }

    public static string FileFor(string serverDirectory) => Path.Combine(serverDirectory, ".packcheck", "players.json");

    public IReadOnlyCollection<PlayerRecord> All => _players.Values;

    public PlayerRecord? Find(string name) => _players.GetValueOrDefault(name);

    /// <summary>Feeds one console message (without the "[time INFO]:" prefix). Returns true when something changed.</summary>
    public bool OnMessage(string message, DateTimeOffset now)
    {
        if (LoginRx().Match(message) is { Success: true } login)
        {
            var p = Get(login.Groups["name"].Value, now);
            var ip = login.Groups["ip"].Value;
            if (!p.Ips.Contains(ip))
            {
                p.Ips.Add(ip);
            }

            return true;
        }

        if (UuidRx().Match(message) is { Success: true } uuid)
        {
            Get(uuid.Groups["name"].Value, now).Uuid = uuid.Groups["uuid"].Value;
            return true;
        }

        if (JoinRx().Match(message) is { Success: true } join)
        {
            var p = Get(join.Groups["name"].Value, now);
            p.OnlineSince ??= now;
            p.Sessions++;
            p.LastSeen = now;
            return true;
        }

        if (LeaveRx().Match(message) is { Success: true } leave && _players.TryGetValue(leave.Groups["name"].Value, out var left))
        {
            Close(left, now);
            return true;
        }

        return false;
    }

    /// <summary>Server stopped or crashed: everyone online is logged out now.</summary>
    public void CloseAll(DateTimeOffset now)
    {
        foreach (var p in _players.Values.Where(p => p.OnlineSince is not null))
        {
            Close(p, now);
        }
    }

    /// <summary>Other names that used one of this player's IP addresses.</summary>
    public IReadOnlyList<string> SameIp(string name) =>
        _players.TryGetValue(name, out var p)
            ? [.. _players.Values.Where(o => !o.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && o.Ips.Intersect(p.Ips).Any()).Select(o => o.Name).Order(StringComparer.OrdinalIgnoreCase)]
            : [];

    public void Save()
    {
        if (_file is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, JsonSerializer.Serialize(_players.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase), Json));
    }

    private static void Close(PlayerRecord p, DateTimeOffset now)
    {
        if (p.OnlineSince is { } since)
        {
            p.PlaySeconds += (long)Math.Max(0, (now - since).TotalSeconds);
        }

        p.OnlineSince = null;
        p.LastSeen = now;
    }

    private PlayerRecord Get(string name, DateTimeOffset now)
    {
        if (!_players.TryGetValue(name, out var p))
        {
            p = new PlayerRecord { Name = name, FirstSeen = now, LastSeen = now };
            _players[name] = p;
        }

        return p;
    }

    // "Steve[/127.0.0.1:51234] logged in with entity id 123 at ([world]0.5, 64.0, 0.5)"
    [GeneratedRegex(@"^(?<name>[A-Za-z0-9_.]{2,16})\[/(?<ip>[0-9a-fA-F.:]+?)(?::\d+)?\] logged in with entity id")]
    private static partial Regex LoginRx();

    // "UUID of player Steve is 8667ba71-b85a-4004-af54-457a9734eed7"
    [GeneratedRegex(@"UUID of player (?<name>[A-Za-z0-9_.]{2,16}) is (?<uuid>[0-9a-fA-F-]{32,36})")]
    private static partial Regex UuidRx();

    [GeneratedRegex(@"^(?<name>[A-Za-z0-9_.]{2,16}) joined the game")]
    private static partial Regex JoinRx();

    [GeneratedRegex(@"^(?<name>[A-Za-z0-9_.]{2,16}) left the game")]
    private static partial Regex LeaveRx();
}

/// <summary>The vanilla list files: whitelist.json, ops.json, banned-players.json, banned-ips.json, usercache.json.</summary>
public sealed class PlayerLists
{
    private readonly string serverDirectory;

    public PlayerLists(string serverDirectory)
    {
        this.serverDirectory = serverDirectory;
        Whitelist = Names("whitelist.json");
        Ops = Names("ops.json");
        Banned = Names("banned-players.json");
    }

    public HashSet<string> Whitelist { get; }
    public HashSet<string> Ops { get; }
    public HashSet<string> Banned { get; }

    private HashSet<string> Names(string file) => new(Read(file).Select(o => o?["name"]?.GetValue<string>()).OfType<string>(), StringComparer.OrdinalIgnoreCase);

    private string PathOf(string file) => Path.Combine(serverDirectory, file);

    private JsonArray Read(string file)
    {
        try
        {
            return File.Exists(PathOf(file)) ? JsonNode.Parse(File.ReadAllText(PathOf(file))) as JsonArray ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Known name → UUID from usercache.json (written by the server for everyone who ever joined).</summary>
    public string? CachedUuid(string name) =>
        Read("usercache.json").FirstOrDefault(o => string.Equals(o?["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))?["uuid"]?.GetValue<string>();

    public IReadOnlyList<string> BannedIps => [.. Read("banned-ips.json").Select(o => o?["ip"]?.GetValue<string>()).OfType<string>()];

    /// <summary>Edits a list while the server is stopped (a running server would overwrite the file).</summary>
    public void Set(PlayerList list, string name, string uuid, bool present, string? reason = null)
    {
        var file = list switch { PlayerList.Whitelist => "whitelist.json", PlayerList.Ops => "ops.json", _ => "banned-players.json" };
        var arr = Read(file);
        foreach (var o in arr.Where(o => string.Equals(o?["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            arr.Remove(o);
        }

        if (present)
        {
            var entry = new JsonObject { ["uuid"] = uuid, ["name"] = name };
            if (list == PlayerList.Ops)
            {
                entry["level"] = 4;
                entry["bypassesPlayerLimit"] = false;
            }
            else if (list == PlayerList.Banned)
            {
                entry["created"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zz00", CultureInfo.InvariantCulture);
                entry["source"] = "PackCheck";
                entry["expires"] = "forever";
                entry["reason"] = string.IsNullOrWhiteSpace(reason) ? "Banned by an operator." : reason;
            }

            arr.Add(entry);
        }

        File.WriteAllText(PathOf(file), arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var set = list switch { PlayerList.Whitelist => Whitelist, PlayerList.Ops => Ops, _ => Banned };
        if (present)
        {
            set.Add(name);
        }
        else
        {
            set.RemoveWhere(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The UUID offline-mode servers use: a v3 UUID of "OfflinePlayer:&lt;name&gt;".</summary>
    public static string OfflineUuid(string name)
    {
#pragma warning disable CA5351 // Not security: the game defines offline UUIDs as MD5 (UUID v3).
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
#pragma warning restore CA5351
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    /// <summary>The command a running server needs for the same change.</summary>
    public static string Command(PlayerList list, string name, bool present, string? reason = null) => list switch
    {
        PlayerList.Whitelist => present ? $"whitelist add {name}" : $"whitelist remove {name}",
        PlayerList.Ops => present ? $"op {name}" : $"deop {name}",
        _ => present ? ("ban " + name + (string.IsNullOrWhiteSpace(reason) ? string.Empty : " " + reason.Trim())) : $"pardon {name}",
    };

    public static bool IsValidName(string name) => name.Length is >= 3 and <= 16 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}

public enum PlayerList
{
    Whitelist,
    Ops,
    Banned,
}
