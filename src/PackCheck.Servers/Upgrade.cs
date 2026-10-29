using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PackCheck.Core;
using PackCheck.Validation;

namespace PackCheck.Servers;

public enum AddonStatus
{
    /// <summary>The jar declares support for the target version.</summary>
    Compatible,

    /// <summary>A Bukkit plugin built for an older API; these usually keep working.</summary>
    Likely,

    /// <summary>A newer build for the target version exists on Modrinth.</summary>
    UpdateAvailable,

    /// <summary>The jar declares it does not support the target version.</summary>
    Incompatible,

    /// <summary>The jar does not say which versions it supports.</summary>
    Unknown,
}

/// <summary>Result of checking one plugin/mod against the target Minecraft version.</summary>
public sealed record AddonCheck(string FileName, string FilePath, string Name, bool IsMod, AddonStatus Status, string? Declared)
{
    public string? UpdateUrl { get; init; }
    public string? UpdateFileName { get; init; }
    public string? UpdateVersion { get; init; }
    public string? UpdateSha512 { get; init; }
}

public sealed record UpgradeReport(string From, string To, int RequiredJava, IReadOnlyList<AddonCheck> Addons)
{
    public int Blocking => Addons.Count(a => a.Status == AddonStatus.Incompatible);
}

/// <summary>Checks a server's plugins and mods against a newer Minecraft version before an in-place upgrade.</summary>
public static class UpgradeAdvisor
{
    private const string ModrinthUpdate = "https://api.modrinth.com/v2/version_files/update";

    /// <summary>Worlds cannot be opened by an older game version, so only upgrades are offered.</summary>
    public static bool CanUpgrade(string current, string target) =>
        MinecraftVersion.TryParse(current, out var c) && MinecraftVersion.TryParse(target, out var t) && t > c;

    public static IReadOnlyList<string> Targets(string current, IEnumerable<string> available) =>
        [.. available.Where(v => CanUpgrade(current, v))];

    /// <summary>Modrinth loader names for a platform (the first is the best match).</summary>
    public static string[] Loaders(ServerPlatform p) => p switch
    {
        ServerPlatform.Fabric => ["fabric"],
        ServerPlatform.Quilt => ["quilt", "fabric"],
        ServerPlatform.Forge => ["forge"],
        ServerPlatform.NeoForge => ["neoforge"],
        ServerPlatform.Purpur => ["purpur", "paper", "spigot", "bukkit"],
        ServerPlatform.Paper or ServerPlatform.Leaf or ServerPlatform.Leaves => ["paper", "spigot", "bukkit"],
        ServerPlatform.Spigot or ServerPlatform.CraftBukkit => ["spigot", "bukkit"],
        _ => [],
    };

    /// <summary>Local check of every jar in plugins/ and mods/, then (when <paramref name="http"/> is given) a Modrinth lookup for updates.</summary>
    public static async Task<UpgradeReport> CheckAsync(ServerDefinition def, string target, HttpClient? http, CancellationToken ct)
    {
        if (!MinecraftVersion.TryParse(target, out var to))
        {
            throw new ArgumentException("Unknown Minecraft version: " + target, nameof(target));
        }

        var checks = new List<AddonCheck>();
        foreach (var jar in Jars(def.Directory, "plugins"))
        {
            var p = await PluginMetadataReader.ReadAsync(jar, ct).ConfigureAwait(false);
            var status = p.ApiVersion is null ? AddonStatus.Unknown
                : MinecraftVersion.TryParse(p.ApiVersion, out var api) ? (api > to ? AddonStatus.Incompatible : AddonStatus.Likely)
                : AddonStatus.Unknown;
            checks.Add(new AddonCheck(Path.GetFileName(jar), jar, p.Name ?? Path.GetFileNameWithoutExtension(jar), false, status, p.ApiVersion));
        }

        foreach (var jar in Jars(def.Directory, "mods"))
        {
            var m = await ModMetadataReader.ReadAsync(jar, ct).ConfigureAwait(false);
            var status = VersionRange.Contains(m.MinecraftRange, to) switch
            {
                true => AddonStatus.Compatible,
                false => AddonStatus.Incompatible,
                null => AddonStatus.Unknown,
            };
            checks.Add(new AddonCheck(Path.GetFileName(jar), jar, m.Name ?? m.Id ?? Path.GetFileNameWithoutExtension(jar), true, status, m.MinecraftRange));
        }

        if (http is not null && checks.Count > 0 && Loaders(def.Platform) is { Length: > 0 } loaders)
        {
            checks = await WithModrinthUpdatesAsync(http, checks, loaders, target, ct).ConfigureAwait(false);
        }

        var java = Math.Max(PlatformInfo.RequiredJava(def.Platform, target), to.RequiredJava());
        return new UpgradeReport(def.MinecraftVersion, target, java, [.. checks.OrderBy(c => c.Status switch { AddonStatus.Incompatible => 0, AddonStatus.UpdateAvailable => 1, AddonStatus.Unknown => 2, _ => 3 }).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    private static IEnumerable<string> Jars(string dir, string sub)
    {
        var full = Path.Combine(dir, sub);
        return Directory.Exists(full) ? Directory.EnumerateFiles(full, "*.jar") : [];
    }

    /// <summary>One batch request: for every jar Modrinth knows (by SHA-512), the newest version for the target game version and loader.</summary>
    private static async Task<List<AddonCheck>> WithModrinthUpdatesAsync(HttpClient http, List<AddonCheck> checks, string[] loaders, string target, CancellationToken ct)
    {
        var bySha = new Dictionary<string, AddonCheck>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in checks)
        {
            await using var fs = File.OpenRead(c.FilePath);
            bySha[Convert.ToHexStringLower(await SHA512.HashDataAsync(fs, ct).ConfigureAwait(false))] = c;
        }

        try
        {
            var body = new JsonObject
            {
                ["hashes"] = new JsonArray([.. bySha.Keys.Select(k => (JsonNode)k)]),
                ["algorithm"] = "sha512",
                ["loaders"] = new JsonArray([.. loaders.Select(l => (JsonNode)l)]),
                ["game_versions"] = new JsonArray(target),
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, ModrinthUpdate) { Content = JsonContent.Create(body) };
            request.Headers.UserAgent.ParseAdd("PackCheck (github.com/X1NPAR1/PackCheck)");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return checks;
            }

            var result = await response.Content.ReadFromJsonAsync<JsonObject>(ct).ConfigureAwait(false);
            if (result is null)
            {
                return checks;
            }

            var updated = new List<AddonCheck>();
            foreach (var (sha, check) in bySha)
            {
                if (result[sha] is JsonObject version
                    && version["files"] is JsonArray files
                    && (files.FirstOrDefault(f => f?["primary"]?.GetValue<bool>() == true) ?? files.FirstOrDefault()) is JsonObject file
                    && file["url"]?.GetValue<string>() is { } url
                    && url.StartsWith("https://cdn.modrinth.com/", StringComparison.Ordinal))
                {
                    var newSha = file["hashes"]?["sha512"]?.GetValue<string>();
                    var same = string.Equals(newSha, sha, StringComparison.OrdinalIgnoreCase);
                    updated.Add(same
                        ? check with { Status = AddonStatus.Compatible }
                        : check with
                        {
                            Status = AddonStatus.UpdateAvailable,
                            UpdateUrl = url,
                            UpdateFileName = file["filename"]?.GetValue<string>(),
                            UpdateVersion = version["version_number"]?.GetValue<string>(),
                            UpdateSha512 = newSha,
                        });
                }
                else
                {
                    updated.Add(check);
                }
            }

            return updated;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return checks;
        }
    }

    /// <summary>Replaces a jar with its Modrinth update (verified by SHA-512); the old jar is removed.</summary>
    public static async Task ApplyUpdateAsync(HttpClient http, AddonCheck c, CancellationToken ct)
    {
        if (c.UpdateUrl is null || c.UpdateFileName is null || c.UpdateFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return;
        }

        var dir = Path.GetDirectoryName(c.FilePath)!;
        var temp = Path.Combine(dir, c.UpdateFileName + ".download");
        var bytes = await http.GetByteArrayAsync(new Uri(c.UpdateUrl), ct).ConfigureAwait(false);
        if (c.UpdateSha512 is { } want && !string.Equals(Convert.ToHexStringLower(SHA512.HashData(bytes)), want, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Downloaded file does not match Modrinth's hash: " + c.UpdateFileName);
        }

        await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
        File.Delete(c.FilePath);
        File.Move(temp, Path.Combine(dir, c.UpdateFileName), overwrite: true);
    }
}
