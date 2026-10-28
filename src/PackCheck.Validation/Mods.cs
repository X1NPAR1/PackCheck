using System.IO.Compression;
using System.Text.Json;
using PackCheck.Core;
using Tomlyn.Syntax;

namespace PackCheck.Validation;

public sealed record ModDependency(string Id, bool Required, string? VersionRange);

/// <summary>Metadata read from a mod jar (Fabric, Quilt, Forge or NeoForge). Nothing is executed.</summary>
public sealed record ModInfo
{
    public required string FileName { get; init; }
    public required string Sha256 { get; init; }
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Version { get; init; }
    public string Loader { get; init; } = "unknown";
    public string? MinecraftRange { get; init; }
    public string Environment { get; init; } = "*";
    public IReadOnlyList<ModDependency> Dependencies { get; init; } = [];
    public bool IsCorrupt { get; init; }
}

public static class ModMetadataReader
{
    private const long MaxDescriptorBytes = 512 * 1024;

    /// <summary>Ids provided by the platform itself; never reported as missing.</summary>
    public static readonly HashSet<string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "java", "fabricloader", "fabric-loader", "quilt_loader", "quilt_base", "forge", "neoforge", "javafml", "lowcodefml", "mixinextras",
    };

    public static async Task<ModInfo> ReadAsync(string jar, CancellationToken ct)
    {
        var basic = new ModInfo { FileName = Path.GetFileName(jar), Sha256 = await Hashing.Sha256FileAsync(jar, ct).ConfigureAwait(false) };
        try
        {
            using var zip = ZipFile.OpenRead(jar);
            if (Text(zip, "fabric.mod.json") is { } fabric)
            {
                return ParseFabric(fabric, basic);
            }

            if (Text(zip, "quilt.mod.json") is { } quilt)
            {
                return ParseQuilt(quilt, basic);
            }

            if (Text(zip, "META-INF/neoforge.mods.toml") is { } neo)
            {
                return ParseToml(neo, basic with { Loader = "neoforge" });
            }

            if (Text(zip, "META-INF/mods.toml") is { } forge)
            {
                return ParseToml(forge, basic with { Loader = "forge" });
            }

            return basic;
        }
        catch (InvalidDataException)
        {
            return basic with { IsCorrupt = true };
        }
        catch (IOException)
        {
            return basic with { IsCorrupt = true };
        }
    }

    private static string? Text(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name);
        if (e is null || e.Length > MaxDescriptorBytes)
        {
            return null;
        }

        using var r = new StreamReader(e.Open());
        return r.ReadToEnd();
    }

    public static ModInfo ParseFabric(string json, ModInfo basic)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            string? S(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var deps = new List<ModDependency>();
            string? mc = null;
            foreach (var (key, required) in new[] { ("depends", true), ("recommends", false), ("suggests", false) })
            {
                if (!root.TryGetProperty(key, out var d) || d.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var p in d.EnumerateObject())
                {
                    var range = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()
                        : p.Value.ValueKind == JsonValueKind.Array ? string.Join(" || ", p.Value.EnumerateArray().Select(x => x.GetString())) : null;
                    if (p.Name == "minecraft" && required)
                    {
                        mc = range;
                    }

                    deps.Add(new ModDependency(p.Name, required, range));
                }
            }

            return basic with
            {
                Id = S("id"), Name = S("name") ?? S("id"), Version = S("version"), Loader = "fabric",
                Environment = S("environment") ?? "*", MinecraftRange = mc, Dependencies = deps,
            };
        }
        catch (JsonException)
        {
            return basic with { Loader = "fabric" };
        }
    }

    public static ModInfo ParseQuilt(string json, ModInfo basic)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var loader = doc.RootElement.GetProperty("quilt_loader");
            var meta = loader.TryGetProperty("metadata", out var m) ? m : default;
            var deps = new List<ModDependency>();
            string? mc = null;
            if (loader.TryGetProperty("depends", out var d) && d.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in d.EnumerateArray())
                {
                    var id = e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetProperty("id").GetString()!;
                    var range = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("versions", out var v) ? v.ToString() : null;
                    var optional = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("optional", out var o) && o.GetBoolean();
                    if (id == "minecraft")
                    {
                        mc = range;
                    }

                    deps.Add(new ModDependency(id, !optional, range));
                }
            }

            var env = doc.RootElement.TryGetProperty("minecraft", out var mcObj) && mcObj.TryGetProperty("environment", out var en) ? en.GetString() ?? "*" : "*";
            return basic with
            {
                Id = loader.GetProperty("id").GetString(), Version = loader.TryGetProperty("version", out var ver) ? ver.GetString() : null,
                Name = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("name", out var n) ? n.GetString() : null,
                Loader = "quilt", MinecraftRange = mc, Environment = env == "dedicated_server" ? "server" : env, Dependencies = deps,
            };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return basic with { Loader = "quilt" };
        }
    }

    /// <summary>Reads [[mods]] and [[dependencies.&lt;id&gt;]] from mods.toml / neoforge.mods.toml.</summary>
    public static ModInfo ParseToml(string toml, ModInfo basic)
    {
        var doc = Tomlyn.Parsing.SyntaxParser.Parse(toml, "mods.toml", false);
        if (doc.HasErrors)
        {
            return basic;
        }

        string? Value(KeyValueSyntax kv) => kv.Value?.ToString()?.Trim().Trim('"', '\'');
        string? id = null, version = null, name = null, mc = null;
        var deps = new List<ModDependency>();
        foreach (var table in doc.Tables)
        {
            var header = table.Name?.ToString().Trim() ?? string.Empty;
            var values = table.Items.OfType<KeyValueSyntax>().ToDictionary(k => k.Key?.ToString().Trim() ?? string.Empty, Value, StringComparer.OrdinalIgnoreCase);
            if (header == "mods" && id is null)
            {
                id = values.GetValueOrDefault("modId");
                version = values.GetValueOrDefault("version");
                name = values.GetValueOrDefault("displayName");
            }
            else if (header.StartsWith("dependencies.", StringComparison.Ordinal) && values.TryGetValue("modId", out var depId) && depId is not null)
            {
                var required = values.TryGetValue("mandatory", out var mand) ? mand == "true"
                    : !values.TryGetValue("type", out var type) || string.Equals(type, "required", StringComparison.OrdinalIgnoreCase);
                var range = values.GetValueOrDefault("versionRange");
                if (depId == "minecraft")
                {
                    mc = range;
                }

                deps.Add(new ModDependency(depId, required, range));
                if (values.TryGetValue("side", out var side) && depId == "minecraft" && side == "CLIENT")
                {
                    basic = basic with { Environment = "client" };
                }
            }
        }

        return basic with { Id = id, Version = version, Name = name ?? id, MinecraftRange = mc, Dependencies = deps };
    }
}

/// <summary>Version ranges used by Fabric (">=1.20 <1.21", "~1.20.1", "1.20.x") and Maven ("[1.20,1.21)").</summary>
public static class VersionRange
{
    /// <summary>True/false when the range can be evaluated, null when the syntax is unknown (never guessed).</summary>
    public static bool? Contains(string? range, MinecraftVersion v)
    {
        if (string.IsNullOrWhiteSpace(range) || range.Trim() is "*" or "\"*\"")
        {
            return true;
        }

        var r = range.Trim().Trim('"');
        if (r.Contains("||", StringComparison.Ordinal))
        {
            var parts = r.Split("||").Select(p => Contains(p, v)).ToList();
            return parts.Any(p => p == true) ? true : parts.All(p => p == false) ? false : null;
        }

        if (r.StartsWith('[') || r.StartsWith('('))
        {
            return Maven(r, v);
        }

        bool? result = true;
        foreach (var token in r.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var ok = Single(token, v);
            if (ok is null)
            {
                return null;
            }

            result &= ok;
        }

        return result;
    }

    private static bool? Single(string t, MinecraftVersion v)
    {
        string op = new(t.TakeWhile(c => c is '>' or '<' or '=' or '~' or '^').ToArray());
        var rest = t[op.Length..].TrimStart('v');
        if (rest.EndsWith(".x", StringComparison.Ordinal) || rest.EndsWith(".*", StringComparison.Ordinal))
        {
            var prefix = rest[..^2];
            return MinecraftVersion.TryParse(prefix, out var p) ? v.Major == p.Major && v.Minor == p.Minor : null;
        }

        var cut = rest.IndexOfAny(['-', '+']);
        if (cut > 0)
        {
            rest = rest[..cut];
        }

        if (!MinecraftVersion.TryParse(rest, out var x))
        {
            return null;
        }

        return op switch
        {
            ">=" => v >= x,
            ">" => v > x,
            "<=" => v <= x,
            "<" => v < x,
            "~" => v.Major == x.Major && v.Minor == x.Minor && v >= x,
            "^" => v.Major == x.Major && v >= x,
            _ => v.CompareTo(x) == 0,
        };
    }

    private static bool? Maven(string r, MinecraftVersion v)
    {
        var lowInclusive = r[0] == '[';
        var highInclusive = r[^1] == ']';
        var body = r[1..^1].Split(',');
        if (body.Length == 1)
        {
            return MinecraftVersion.TryParse(body[0], out var exact) ? v.CompareTo(exact) == 0 : null;
        }

        bool? Bound(string text, bool low)
        {
            text = text.Trim();
            if (text.Length == 0)
            {
                return true;
            }

            if (!MinecraftVersion.TryParse(text, out var b))
            {
                return null;
            }

            return low ? (lowInclusive ? v >= b : v > b) : (highInclusive ? v <= b : v < b);
        }

        var lowOk = Bound(body[0], low: true);
        var highOk = Bound(body[1], low: false);
        return lowOk is null || highOk is null ? null : lowOk.Value && highOk.Value;
    }
}
