using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using PackCheck.Core;

namespace PackCheck.Servers;

public enum ServerPlatform
{
    Paper,
    Purpur,
    Vanilla,
    Fabric,
    Forge,
    NeoForge,
    Folia,
    Leaf,
    Leaves,
    Spigot,
    CraftBukkit,
    Quilt,
    SpongeVanilla,
    Mohist,
    Youer,
    Arclight,
    Velocity,
    BungeeCord,
}

/// <summary>A file to fetch. Installers (Forge/NeoForge) are run with --installServer after download.</summary>
public sealed record ServerDownload(string FileName, Uri Url, string? Hash, HashAlgorithmName Algorithm, long Size, bool IsInstaller = false);

public static class PlatformInfo
{
    public static bool UsesPlugins(ServerPlatform p) => p is ServerPlatform.Paper or ServerPlatform.Purpur or ServerPlatform.Folia or ServerPlatform.Leaf
        or ServerPlatform.Leaves or ServerPlatform.Spigot or ServerPlatform.CraftBukkit or ServerPlatform.Mohist or ServerPlatform.Youer or ServerPlatform.Arclight;

    public static bool UsesMods(ServerPlatform p) => p is ServerPlatform.Fabric or ServerPlatform.Forge or ServerPlatform.NeoForge or ServerPlatform.Quilt
        or ServerPlatform.Mohist or ServerPlatform.Youer or ServerPlatform.Arclight;

    /// <summary>Java feature version a server needs. Proxies version independently of Minecraft.</summary>
    public static int RequiredJava(ServerPlatform p, string version) => p switch
    {
        ServerPlatform.Velocity => version.StartsWith("3.", StringComparison.Ordinal) ? 21 : 25,
        ServerPlatform.BungeeCord => 21,
        _ => MinecraftVersion.TryParse(version, out var mc) ? mc.RequiredJava() : 21,
    };

    public static bool IsHybrid(ServerPlatform p) => UsesPlugins(p) && UsesMods(p);

    /// <summary>Mod loader id used by mod catalogs.</summary>
    public static string Loader(ServerPlatform p) => p switch
    {
        ServerPlatform.Mohist => "forge",
        ServerPlatform.Youer or ServerPlatform.Arclight => "neoforge",
        _ => p.ToString().ToLowerInvariant(),
    };

    /// <summary>Folder that holds add-ons; hybrids keep plugins in "plugins" and mods in "mods".</summary>
    public static string ContentFolder(ServerPlatform p, bool mods) => mods ? "mods" : "plugins";

    /// <summary>NeoForge "21.1.77" -> "1.21.1", "21.0.5" -> "1.21", "26.1.2.123" -> "26.1.2".</summary>
    public static string? NeoForgeToMinecraft(string neo)
    {
        var parts = neo.Split('.', '-');
        if (parts.Length < 3 || !int.TryParse(parts[0], out var a) || !int.TryParse(parts[1], out var b))
        {
            return null;
        }

        if (a >= 25)
        {
            return parts[2] == "0" ? $"{a}.{b}" : $"{a}.{b}.{parts[2]}";
        }

        return b == 0 ? $"1.{a}" : $"1.{a}.{b}";
    }
}

/// <summary>Resolves server JARs from official sources only (PaperMC Fill API, Mojang piston-meta).</summary>
public sealed class ServerSources(HttpClient http)
{
    private static readonly Uri PaperVersions = new("https://fill.papermc.io/v3/projects/paper");
    private static readonly Uri MojangManifest = new("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");

    private static readonly Uri PurpurVersions = new("https://api.purpurmc.org/v2/purpur");
    private static readonly Uri FabricGame = new("https://meta.fabricmc.net/v2/versions/game");
    private static readonly Uri ForgePromotions = new("https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json");
    private static readonly Uri NeoForgeMetadata = new("https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml");

    private static List<string> Newest(IEnumerable<string> versions) =>
        versions.Where(v => MinecraftVersion.TryParse(v, out _)).Distinct()
            .OrderByDescending(v => MinecraftVersion.TryParse(v, out var mv) ? mv : default).ToList();

    public async Task<IReadOnlyList<string>> GetVersionsAsync(ServerPlatform platform, CancellationToken ct)
    {
        if (await new MorePlatforms(http).VersionsAsync(platform, ct).ConfigureAwait(false) is { } more)
        {
            return more;
        }

        switch (platform)
        {
            case ServerPlatform.Purpur:
                {
                    using var doc = await GetJsonAsync(PurpurVersions, ct).ConfigureAwait(false);
                    return Newest(doc.RootElement.GetProperty("versions").EnumerateArray().Select(v => v.GetString()!));
                }

            case ServerPlatform.Fabric:
                {
                    using var doc = await GetJsonAsync(FabricGame, ct).ConfigureAwait(false);
                    return Newest(doc.RootElement.EnumerateArray().Where(v => v.GetProperty("stable").GetBoolean()).Select(v => v.GetProperty("version").GetString()!));
                }

            case ServerPlatform.Forge:
                {
                    using var doc = await GetJsonAsync(ForgePromotions, ct).ConfigureAwait(false);
                    // Forge ships an installer from Minecraft 1.5.2 on; older builds cannot be installed as a server.
                    var minimum = new MinecraftVersion(1, 5, 2);
                    return Newest(doc.RootElement.GetProperty("promos").EnumerateObject().Select(p => p.Name[..p.Name.LastIndexOf('-')])
                        .Where(v => MinecraftVersion.TryParse(v, out var mv) && mv >= minimum));
                }

            case ServerPlatform.NeoForge:
                return Newest((await NeoForgeVersionsAsync(ct).ConfigureAwait(false)).Select(PlatformInfo.NeoForgeToMinecraft).OfType<string>());
        }

        if (platform == ServerPlatform.Paper)
        {
            using var doc = await GetJsonAsync(PaperVersions, ct).ConfigureAwait(false);
            var list = new List<string>();
            foreach (var group in doc.RootElement.GetProperty("versions").EnumerateObject())
            {
                list.AddRange(group.Value.EnumerateArray().Select(v => v.GetString()!).Where(v => MinecraftVersion.TryParse(v, out _)));
            }

            return list.OrderByDescending(v => MinecraftVersion.TryParse(v, out var mv) ? mv : default).ToList();
        }

        using var manifest = await GetJsonAsync(MojangManifest, ct).ConfigureAwait(false);

        // Mojang's metadata has no server jar for releases older than 1.2.5.
        var firstServer = new MinecraftVersion(1, 2, 5);
        return manifest.RootElement.GetProperty("versions").EnumerateArray()
            .Where(v => v.GetProperty("type").GetString() == "release")
            .Select(v => v.GetProperty("id").GetString()!)
            .Where(v => MinecraftVersion.TryParse(v, out var mv) && mv >= firstServer)
            .ToList();
    }

    private async Task<List<string>> NeoForgeVersionsAsync(CancellationToken ct)
    {
        var xml = await http.GetStringAsync(NeoForgeMetadata, ct).ConfigureAwait(false);
        return System.Xml.Linq.XDocument.Parse(xml).Descendants("version").Select(v => v.Value).ToList();
    }

    private async Task<string> Sha1Async(Uri file, CancellationToken ct) =>
        (await http.GetStringAsync(new Uri(file + ".sha1"), ct).ConfigureAwait(false)).Trim().Split(' ')[0];

    public async Task<ServerDownload> ResolveAsync(ServerPlatform platform, string version, CancellationToken ct)
    {
        if (await new MorePlatforms(http).ResolveAsync(platform, version, ct).ConfigureAwait(false) is { } more)
        {
            return more;
        }

        var v = Uri.EscapeDataString(version);
        switch (platform)
        {
            case ServerPlatform.Purpur:
                {
                    using var doc = await GetJsonAsync(new Uri($"https://api.purpurmc.org/v2/purpur/{v}/latest"), ct).ConfigureAwait(false);
                    var build = doc.RootElement.GetProperty("build").GetString()!;
                    var md5 = doc.RootElement.TryGetProperty("md5", out var m) ? m.GetString() : null;
                    return new ServerDownload("purpur.jar", new Uri($"https://api.purpurmc.org/v2/purpur/{v}/{build}/download"), md5, HashAlgorithmName.MD5, 0);
                }

            case ServerPlatform.Fabric:
                {
                    using var loaders = await GetJsonAsync(new Uri($"https://meta.fabricmc.net/v2/versions/loader/{v}"), ct).ConfigureAwait(false);
                    var loader = loaders.RootElement.EnumerateArray().First(l => l.GetProperty("loader").GetProperty("stable").GetBoolean()).GetProperty("loader").GetProperty("version").GetString();
                    using var installers = await GetJsonAsync(new Uri("https://meta.fabricmc.net/v2/versions/installer"), ct).ConfigureAwait(false);
                    var installer = installers.RootElement.EnumerateArray().First(i => i.GetProperty("stable").GetBoolean()).GetProperty("version").GetString();

                    // The Fabric meta server builds a ready-to-run server launcher and publishes no checksum for it.
                    return new ServerDownload("fabric-server-launch.jar", new Uri($"https://meta.fabricmc.net/v2/versions/loader/{v}/{loader}/{installer}/server/jar"), null, HashAlgorithmName.SHA256, 0);
                }

            case ServerPlatform.Forge:
                {
                    using var doc = await GetJsonAsync(ForgePromotions, ct).ConfigureAwait(false);
                    var promos = doc.RootElement.GetProperty("promos");
                    var forge = (promos.TryGetProperty($"{version}-recommended", out var r) ? r : promos.GetProperty($"{version}-latest")).GetString()!;
                    var full = $"{version}-{forge}";
                    var url = new Uri($"https://maven.minecraftforge.net/net/minecraftforge/forge/{full}/forge-{full}-installer.jar");
                    return new ServerDownload($"forge-{full}-installer.jar", url, await Sha1Async(url, ct).ConfigureAwait(false), HashAlgorithmName.SHA1, 0, IsInstaller: true);
                }

            case ServerPlatform.NeoForge:
                {
                    var matching = (await NeoForgeVersionsAsync(ct).ConfigureAwait(false)).Where(n => PlatformInfo.NeoForgeToMinecraft(n) == version).ToList();
                    var neo = matching.LastOrDefault(n => !n.Contains("beta", StringComparison.OrdinalIgnoreCase)) ?? matching.LastOrDefault()
                        ?? throw new InvalidOperationException($"No NeoForge build for Minecraft {version}.");
                    var url = new Uri($"https://maven.neoforged.net/releases/net/neoforged/neoforge/{neo}/neoforge-{neo}-installer.jar");
                    return new ServerDownload($"neoforge-{neo}-installer.jar", url, await Sha1Async(url, ct).ConfigureAwait(false), HashAlgorithmName.SHA1, 0, IsInstaller: true);
                }
        }

        if (platform == ServerPlatform.Paper)
        {
            using var doc = await GetJsonAsync(new Uri($"https://fill.papermc.io/v3/projects/paper/versions/{Uri.EscapeDataString(version)}/builds/latest"), ct).ConfigureAwait(false);
            var d = doc.RootElement.GetProperty("downloads").GetProperty("server:default");
            return new ServerDownload(
                d.GetProperty("name").GetString()!,
                new Uri(d.GetProperty("url").GetString()!),
                d.GetProperty("checksums").GetProperty("sha256").GetString()!,
                HashAlgorithmName.SHA256,
                d.GetProperty("size").GetInt64());
        }

        using var manifest = await GetJsonAsync(MojangManifest, ct).ConfigureAwait(false);
        var entry = manifest.RootElement.GetProperty("versions").EnumerateArray()
            .FirstOrDefault(v => v.GetProperty("id").GetString() == version);
        if (entry.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException($"Unknown Minecraft version {version}.");
        }

        using var meta = await GetJsonAsync(new Uri(entry.GetProperty("url").GetString()!), ct).ConfigureAwait(false);
        var server = meta.RootElement.GetProperty("downloads").GetProperty("server");
        return new ServerDownload("server.jar", new Uri(server.GetProperty("url").GetString()!), server.GetProperty("sha1").GetString()!, HashAlgorithmName.SHA1, server.GetProperty("size").GetInt64());
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("PackCheck", "1.0"));
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        await using var s = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
    }
}

/// <summary>HTTPS-only download with size limit and hash verification. The file only appears once verified.</summary>
public static class VerifiedDownloader
{
    public static async Task DownloadAsync(HttpClient http, Uri url, string destination, string? expectedHash, HashAlgorithmName algorithm, long maxBytes, IProgress<double>? progress, CancellationToken ct)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Only HTTPS downloads are allowed.");
        }

        var temp = destination + ".part";
        using (var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? 0;
            if (total > maxBytes)
            {
                throw new InvalidOperationException("Download exceeds the size limit.");
            }

            using var hasher = IncrementalHash.CreateHash(algorithm);
            await using var input = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (var output = File.Create(temp))
            {
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    read += n;
                    if (read > maxBytes)
                    {
                        throw new InvalidOperationException("Download exceeds the size limit.");
                    }

                    hasher.AppendData(buffer, 0, n);
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    if (total > 0)
                    {
                        progress?.Report((double)read / total);
                    }
                }
            }

            var actual = Convert.ToHexStringLower(hasher.GetHashAndReset());
            if (expectedHash is not null && !string.Equals(actual, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temp);
                throw new InvalidOperationException($"Checksum mismatch for {Path.GetFileName(destination)}.");
            }
        }

        File.Move(temp, destination, overwrite: true);
    }
}
