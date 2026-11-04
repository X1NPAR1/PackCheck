using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PackCheck.Servers;

/// <summary>Where a pasted link points.</summary>
public enum LinkSource
{
    Modrinth,
    Hangar,
    Spigot,
    GitHub,
    CurseForge,
    DirectJar,
}

public sealed record ContentLink(LinkSource Source, string Id, string? Owner = null);

/// <summary>A resolved download: one jar file, with a hash when the source publishes one.</summary>
public sealed record ContentDownload(string Url, string FileName, string? Sha512 = null, string? Sha256 = null, string? Version = null);

/// <summary>Turns a pasted project page, repository, slug or .jar link into a download for the server's loader and version.</summary>
public static partial class ContentLinks
{
    [GeneratedRegex(@"^https?://(?:www\.)?modrinth\.com/(?:mod|plugin|datapack|shader|resourcepack)/([A-Za-z0-9_.-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ModrinthRx();

    [GeneratedRegex(@"^https?://hangar\.papermc\.io/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex HangarRx();

    [GeneratedRegex(@"^https?://(?:www\.)?spigotmc\.org/resources/(?:[^/]*\.)?(\d+)/?", RegexOptions.IgnoreCase)]
    private static partial Regex SpigotRx();

    [GeneratedRegex(@"^(?:https?://(?:www\.)?github\.com/)?([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+?)(?:\.git)?(?:/.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubRx();

    [GeneratedRegex(@"^https?://(?:www\.)?curseforge\.com/minecraft/[a-z-]+/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CurseForgeRx();

    [GeneratedRegex(@"^[a-z0-9_-]{2,64}$")]
    private static partial Regex SlugRx();

    public static ContentLink? Parse(string? input)
    {
        var s = input?.Trim() ?? string.Empty;
        if (s.Length == 0)
        {
            return null;
        }

        if (ModrinthRx().Match(s) is { Success: true } m)
        {
            return new ContentLink(LinkSource.Modrinth, m.Groups[1].Value);
        }

        if (HangarRx().Match(s) is { Success: true } h)
        {
            return new ContentLink(LinkSource.Hangar, h.Groups[2].Value, h.Groups[1].Value);
        }

        if (SpigotRx().Match(s) is { Success: true } sp)
        {
            return new ContentLink(LinkSource.Spigot, sp.Groups[1].Value);
        }

        if (CurseForgeRx().Match(s) is { Success: true } cf)
        {
            return new ContentLink(LinkSource.CurseForge, cf.Groups[1].Value);
        }

        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(s, UriKind.Absolute, out var u)
            && u.AbsolutePath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !u.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return new ContentLink(LinkSource.DirectJar, s);
        }

        if ((s.Contains("github.com", StringComparison.OrdinalIgnoreCase) || (s.Count(c => c == '/') == 1 && !s.Contains(':', StringComparison.Ordinal)))
            && GitHubRx().Match(s) is { Success: true } g)
        {
            return new ContentLink(LinkSource.GitHub, g.Groups[2].Value, g.Groups[1].Value);
        }

        return SlugRx().IsMatch(s) ? new ContentLink(LinkSource.Modrinth, s) : null;
    }

    /// <summary>The release asset to use: a .jar that is not a sources/javadoc/api side file; prefers one naming the loader.</summary>
    public static JsonObject? PickGitHubAsset(JsonArray assets, string? loader)
    {
        var jars = assets.OfType<JsonObject>()
            .Where(a => a["name"]?.GetValue<string>() is { } n && n.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                        && !Regex.IsMatch(n, @"-(sources|javadoc|api|dev)\.jar$", RegexOptions.IgnoreCase))
            .ToList();
        return (loader is null ? null : jars.FirstOrDefault(a => a["name"]!.GetValue<string>().Contains(loader, StringComparison.OrdinalIgnoreCase))) ?? jars.FirstOrDefault();
    }

    /// <summary>Resolves the link for the server's platform and Minecraft version.</summary>
    public static async Task<ContentDownload> ResolveAsync(HttpClient http, ContentLink link, ServerDefinition server, CancellationToken ct)
    {
        var loaders = UpgradeAdvisor.Loaders(server.Platform);
        var mc = server.MinecraftVersion;
        switch (link.Source)
        {
            case LinkSource.Modrinth:
                {
                    var q = loaders.Length == 0 ? string.Empty : "loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(loaders)) + "&";
                    var arr = await Get<JsonArray>(http, $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(link.Id)}/version?{q}game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { mc })), ct).ConfigureAwait(false);
                    var v = arr.OfType<JsonObject>().FirstOrDefault() ?? throw new InvalidOperationException($"No build of {link.Id} for {server.Platform} {mc} on Modrinth.");
                    var files = (v["files"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
                    if (files.Count == 0)
                    {
                        throw new InvalidOperationException($"The Modrinth build of {link.Id} has no files.");
                    }

                    var f = files.FirstOrDefault(x => x["primary"]?.GetValue<bool>() == true) ?? files[0];
                    var fileUrl = f["url"]?.GetValue<string>() ?? throw new InvalidOperationException("Modrinth lists no download link for this file.");
                    var fileName = f["filename"]?.GetValue<string>() ?? throw new InvalidOperationException("Modrinth lists no file name for this file.");
                    return new ContentDownload(fileUrl, fileName, Sha512: f["hashes"]?["sha512"]?.GetValue<string>(), Version: v["version_number"]?.GetValue<string>());
                }

            case LinkSource.Hangar:
                {
                    var data = await Get<JsonObject>(http, $"https://hangar.papermc.io/api/v1/projects/{Uri.EscapeDataString(link.Owner!)}/{Uri.EscapeDataString(link.Id)}/versions?limit=25&platform=PAPER&platformVersion={Uri.EscapeDataString(mc)}", ct).ConfigureAwait(false);
                    var v = data["result"]?.AsArray().OfType<JsonObject>().FirstOrDefault() ?? throw new InvalidOperationException($"No Paper build of {link.Id} for {mc} on Hangar.");
                    var d = v["downloads"]?["PAPER"] ?? throw new InvalidOperationException("Hangar lists no Paper download.");
                    var url = d["downloadUrl"]?.GetValue<string>() ?? d["externalUrl"]?.GetValue<string>() ?? throw new InvalidOperationException("Hangar lists no download link.");
                    return new ContentDownload(url, d["fileInfo"]?["name"]?.GetValue<string>() ?? link.Id + ".jar", Sha256: d["fileInfo"]?["sha256Hash"]?.GetValue<string>(), Version: v["name"]?.GetValue<string>());
                }

            case LinkSource.Spigot:
                {
                    var r = await Get<JsonObject>(http, $"https://api.spiget.org/v2/resources/{link.Id}", ct).ConfigureAwait(false);
                    if (r["external"]?.GetValue<bool>() == true || r["premium"]?.GetValue<bool>() == true)
                    {
                        throw new InvalidOperationException("This SpigotMC resource is premium or hosted elsewhere; download it from its page.");
                    }

                    var name = Regex.Replace(r["name"]?.GetValue<string>() ?? "plugin", @"[^A-Za-z0-9_.-]+", "-").Trim('-');
                    return new ContentDownload($"https://api.spiget.org/v2/resources/{link.Id}/download", name + ".jar", Version: r["version"]?["id"]?.ToString());
                }

            case LinkSource.GitHub:
                {
                    var rel = await Get<JsonObject>(http, $"https://api.github.com/repos/{Uri.EscapeDataString(link.Owner!)}/{Uri.EscapeDataString(link.Id)}/releases/latest", ct).ConfigureAwait(false);
                    var asset = PickGitHubAsset(rel["assets"]?.AsArray() ?? [], loaders.FirstOrDefault()) ?? throw new InvalidOperationException("The latest release has no .jar file.");
                    var digest = asset["digest"]?.GetValue<string>();
                    return new ContentDownload(asset["browser_download_url"]!.GetValue<string>(), asset["name"]!.GetValue<string>(),
                        Sha256: digest is not null && digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest[7..] : null, Version: rel["tag_name"]?.GetValue<string>());
                }

            case LinkSource.DirectJar:
                return new ContentDownload(link.Id, Path.GetFileName(new Uri(link.Id).AbsolutePath));

            default:
                throw new InvalidOperationException("CurseForge links: search the project on the Plugins or Mods page (needs a CurseForge key).");
        }
    }

    private static async Task<T> Get<T>(HttpClient http, string url, CancellationToken ct)
        where T : JsonNode
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("PackCheck (github.com/X1NPAR1/PackCheck)");
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)res.StatusCode} from {new Uri(url).Host}");
        }

        return (await res.Content.ReadFromJsonAsync<JsonNode>(ct).ConfigureAwait(false) as T) ?? throw new InvalidOperationException("Unexpected answer from " + new Uri(url).Host);
    }

    /// <summary>Downloads into plugins/ or mods/, checks the hash and that the file is a real plugin or mod jar.</summary>
    public static async Task<string> InstallAsync(HttpClient http, ContentDownload d, ServerDefinition server, CancellationToken ct)
    {
        var uri = new Uri(d.Url);
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Only HTTPS downloads are allowed.");
        }

        var bytes = await http.GetByteArrayAsync(uri, ct).ConfigureAwait(false);
        if (d.Sha512 is { } s512 && !Convert.ToHexStringLower(SHA512.HashData(bytes)).Equals(s512, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The download does not match its SHA-512 hash.");
        }

        if (d.Sha256 is { } s256 && !Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(s256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The download does not match its SHA-256 hash.");
        }

        var (plugin, mod) = AddonKinds(bytes);
        var hybrid = PlatformInfo.IsHybrid(server.Platform);
        var usesMods = PlatformInfo.UsesMods(server.Platform);
        if (!(plugin || mod))
        {
            throw new InvalidDataException("The download is not a plugin or mod jar.");
        }

        if (!hybrid && (usesMods ? !mod : !plugin))
        {
            throw new InvalidDataException(usesMods ? "This is a plugin, but the server runs mods." : "This is a mod, but the server runs plugins.");
        }

        var name = string.Concat(Path.GetFileName(d.FileName).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        name = name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? name : name + ".jar";
        var dir = Path.Combine(server.Directory, PlatformInfo.UsesMods(server.Platform) ? "mods" : "plugins");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, name);
        await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);
        return target;
    }

    private static readonly string[] PluginMarkers = ["plugin.yml", "paper-plugin.yml", "bungee.yml", "velocity-plugin.json"];

    private static readonly string[] ModMarkers = ["fabric.mod.json", "quilt.mod.json", "META-INF/mods.toml", "META-INF/neoforge.mods.toml", "mcmod.info"];

    /// <summary>Whether the jar carries plugin markers, mod markers, both or neither.</summary>
    public static (bool Plugin, bool Mod) AddonKinds(byte[] bytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var names = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return (PluginMarkers.Any(names.Contains), ModMarkers.Any(names.Contains));
        }
        catch (InvalidDataException)
        {
            return (false, false);
        }
    }

    public static bool IsAddonJar(byte[] bytes) => AddonKinds(bytes) is (true, _) or (_, true);
}

/// <summary>Updates installed plugins/mods to their newest Modrinth build for the server's version, keeping the old jar so it can be restored.</summary>
public static class AddonUpdates
{
    public static string HistoryDir(ServerDefinition s) => Path.Combine(s.Directory, ".packcheck", "addon-history");

    /// <summary>Same Modrinth lookup the version wizard uses, for the current version.</summary>
    public static async Task<IReadOnlyList<AddonCheck>> CheckAsync(HttpClient http, ServerDefinition s, CancellationToken ct) =>
        (await UpgradeAdvisor.CheckAsync(s, s.MinecraftVersion, http, ct).ConfigureAwait(false)).Addons.Where(a => a.Status == AddonStatus.UpdateAvailable).ToList();

    /// <summary>
    /// Downloads and verifies the update first; only then is the old jar kept in the history (with a marker naming it)
    /// and replaced. A failed download, hash check or cancellation leaves the history and the live jar untouched.
    /// </summary>
    public static async Task ApplyAsync(HttpClient http, ServerDefinition s, AddonCheck update, CancellationToken ct)
    {
        var newName = update.UpdateFileName is { } n ? Path.GetFileName(n) : null;
        if (update.UpdateUrl is null || string.IsNullOrEmpty(newName))
        {
            return;
        }

        var uri = new Uri(update.UpdateUrl);
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Only HTTPS downloads are allowed.");
        }

        var bytes = await http.GetByteArrayAsync(uri, ct).ConfigureAwait(false);
        if (update.UpdateSha512 is { } want && !string.Equals(Convert.ToHexStringLower(SHA512.HashData(bytes)), want, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Downloaded file does not match Modrinth's hash: " + newName);
        }

        ct.ThrowIfCancellationRequested();
        var dir = Path.GetDirectoryName(update.FilePath)!;
        var temp = Path.Combine(dir, newName + ".download");
        await File.WriteAllBytesAsync(temp, bytes, CancellationToken.None).ConfigureAwait(false);

        var hist = HistoryDir(s);
        Directory.CreateDirectory(hist);
        File.Copy(update.FilePath, Path.Combine(hist, Path.GetFileName(update.FilePath)), overwrite: true);
        File.WriteAllText(Path.Combine(hist, newName + ".previous"), Path.GetFileName(update.FilePath));
        File.Delete(update.FilePath);
        File.Move(temp, Path.Combine(dir, newName), overwrite: true);
    }

    /// <summary>The jar this file replaced, if it came from an update.</summary>
    public static string? PreviousOf(ServerDefinition s, string jarPath)
    {
        var marker = Path.Combine(HistoryDir(s), Path.GetFileName(jarPath) + ".previous");
        if (!File.Exists(marker))
        {
            return null;
        }

        var old = Path.Combine(HistoryDir(s), File.ReadAllText(marker).Trim());
        return File.Exists(old) ? old : null;
    }

    /// <summary>Puts the previous jar back and removes the updated one.</summary>
    public static string Revert(ServerDefinition s, string jarPath)
    {
        var old = PreviousOf(s, jarPath) ?? throw new InvalidOperationException("No earlier version is kept for this file.");
        var dir = Path.GetDirectoryName(jarPath)!;
        var restored = Path.Combine(dir, Path.GetFileName(old));
        File.Copy(old, restored, overwrite: true);
        if (!string.Equals(restored, jarPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(jarPath);
        }

        File.Delete(Path.Combine(HistoryDir(s), Path.GetFileName(jarPath) + ".previous"));
        return restored;
    }
}

/// <summary>Shares a log on mclo.gs (public paste) and reads its automatic analysis.</summary>
public static class MclogsShare
{
    public sealed record Result(string Url, IReadOnlyList<(string Problem, IReadOnlyList<string> Fixes)> Problems);

    public static async Task<Result> ShareAsync(HttpClient http, string log, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent([new KeyValuePair<string, string>("content", log.Length > 9_000_000 ? log[^9_000_000..] : log)]);
        using var res = await http.PostAsync(new Uri("https://api.mclo.gs/1/log"), form, ct).ConfigureAwait(false);
        var json = await res.Content.ReadFromJsonAsync<JsonObject>(ct).ConfigureAwait(false);
        if (json?["success"]?.GetValue<bool>() != true || json["id"]?.GetValue<string>() is not { } id)
        {
            throw new HttpRequestException("mclo.gs did not accept the log: " + json?["error"]);
        }

        var url = json["url"]?.GetValue<string>() ?? "https://mclo.gs/" + id;
        try
        {
            var insights = await http.GetFromJsonAsync<JsonObject>(new Uri("https://api.mclo.gs/1/insights/" + Uri.EscapeDataString(id)), ct).ConfigureAwait(false);
            return new Result(url, ParseInsights(insights));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or JsonException or TaskCanceledException or NotSupportedException)
        {
            // The log is shared even when the analysis is unavailable.
            return new Result(url, []);
        }
    }

    public static IReadOnlyList<(string Problem, IReadOnlyList<string> Fixes)> ParseInsights(JsonObject? insights) =>
        insights?["analysis"]?["problems"] is JsonArray problems
            ? [.. problems.OfType<JsonObject>().Select(p => (p["message"]?.GetValue<string>() ?? "?",
                (IReadOnlyList<string>)[.. (p["solutions"] as JsonArray ?? []).OfType<JsonObject>().Select(x => x["message"]?.GetValue<string>() ?? string.Empty).Where(x => x.Length > 0)]))]
            : [];
}
