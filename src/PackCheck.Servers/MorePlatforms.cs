using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PackCheck.Core;

namespace PackCheck.Servers;

/// <summary>
/// Version lists and downloads for platforms beyond Paper/Purpur/Vanilla/Fabric/Forge/NeoForge.
/// Every list comes from the project's own API, so unsupported Minecraft versions never appear.
/// </summary>
internal sealed partial class MorePlatforms(HttpClient http)
{
    private const string Spigot = "https://hub.spigotmc.org/versions/";
    private const string BuildToolsUrl = "https://hub.spigotmc.org/jenkins/job/BuildTools/lastSuccessfulBuild/artifact/target/BuildTools.jar";

    [GeneratedRegex(@"href=""(?<v>[0-9]+\.[0-9]+(?:\.[0-9]+)?)\.json""")]
    private static partial Regex SpigotVersion();

    [GeneratedRegex(@"^arclight-neoforge-(?<mc>[0-9]+\.[0-9]+(?:\.[0-9]+)?)-.*\.jar$")]
    private static partial Regex ArclightAsset();

    private async Task<JsonDocument> JsonAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("PackCheck", "2.0"));
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
    }

    private static List<string> Releases(IEnumerable<string> versions) =>
        versions.Where(v => MinecraftVersion.TryParse(v, out _)).Distinct()
            .OrderByDescending(v => MinecraftVersion.TryParse(v, out var mv) ? mv : default).ToList();

    public async Task<IReadOnlyList<string>?> VersionsAsync(ServerPlatform p, CancellationToken ct)
    {
        switch (p)
        {
            case ServerPlatform.Folia:
            {
                using var d = await JsonAsync("https://fill.papermc.io/v3/projects/folia", ct).ConfigureAwait(false);
                return Releases(d.RootElement.GetProperty("versions").EnumerateObject().SelectMany(g => g.Value.EnumerateArray().Select(v => v.GetString()!)));
            }

            case ServerPlatform.Leaf:
            case ServerPlatform.Leaves:
            {
                using var d = await JsonAsync(p == ServerPlatform.Leaf ? "https://api.leafmc.one/v2/projects/leaf" : "https://api.leavesmc.org/v2/projects/leaves", ct).ConfigureAwait(false);
                return Releases(d.RootElement.GetProperty("versions").EnumerateArray().Select(v => v.GetString()!));
            }

            case ServerPlatform.Spigot:
            case ServerPlatform.CraftBukkit:
            {
                var html = await http.GetStringAsync(new Uri(Spigot), ct).ConfigureAwait(false);
                return Releases(SpigotVersion().Matches(html).Select(m => m.Groups["v"].Value));
            }

            case ServerPlatform.Quilt:
            {
                using var d = await JsonAsync("https://meta.quiltmc.org/v3/versions/game", ct).ConfigureAwait(false);
                return Releases(d.RootElement.EnumerateArray().Where(v => v.GetProperty("stable").GetBoolean()).Select(v => v.GetProperty("version").GetString()!));
            }

            case ServerPlatform.SpongeVanilla:
            {
                using var d = await JsonAsync("https://dl-api.spongepowered.org/v2/groups/org.spongepowered/artifacts/spongevanilla", ct).ConfigureAwait(false);
                return Releases(d.RootElement.GetProperty("tags").GetProperty("minecraft").EnumerateArray().Select(v => v.GetString()!));
            }

            case ServerPlatform.Mohist:
            case ServerPlatform.Youer:
            {
                var project = p.ToString().ToLowerInvariant();
                using var d = await JsonAsync($"https://api.mohistmc.com/project/{project}/versions", ct).ConfigureAwait(false);
                var all = d.RootElement.EnumerateArray().Select(v => v.GetProperty("name").GetString()!).ToList();

                // The API lists versions that have no downloadable build; only offer the ones that do.
                var checks = all.Select(async v =>
                {
                    try
                    {
                        using var b = await JsonAsync($"https://api.mohistmc.com/project/{project}/{Uri.EscapeDataString(v)}/builds", ct).ConfigureAwait(false);
                        return b.RootElement.ValueKind == JsonValueKind.Array && b.RootElement.GetArrayLength() > 0 ? v : null;
                    }
                    catch (HttpRequestException)
                    {
                        return null;
                    }
                });
                return Releases((await Task.WhenAll(checks).ConfigureAwait(false)).OfType<string>());
            }

            case ServerPlatform.Velocity:
            {
                // Proxy versions, not Minecraft versions; snapshots are skipped.
                using var d = await JsonAsync("https://fill.papermc.io/v3/projects/velocity", ct).ConfigureAwait(false);
                return d.RootElement.GetProperty("versions").EnumerateObject().SelectMany(g => g.Value.EnumerateArray().Select(v => v.GetString()!))
                    .Where(v => !v.Contains("SNAPSHOT", StringComparison.OrdinalIgnoreCase)).ToList();
            }

            case ServerPlatform.BungeeCord:
                return ["latest"];

            case ServerPlatform.Arclight:
            {
                using var d = await JsonAsync("https://api.github.com/repos/IzzelAliz/Arclight/releases?per_page=40", ct).ConfigureAwait(false);
                return Releases(d.RootElement.EnumerateArray().SelectMany(r => r.GetProperty("assets").EnumerateArray())
                    .Select(a => ArclightAsset().Match(a.GetProperty("name").GetString()!)).Where(m => m.Success).Select(m => m.Groups["mc"].Value));
            }
        }

        return null;
    }

    public async Task<ServerDownload?> ResolveAsync(ServerPlatform p, string version, CancellationToken ct)
    {
        var v = Uri.EscapeDataString(version);
        switch (p)
        {
            case ServerPlatform.Folia:
            {
                using var d = await JsonAsync($"https://fill.papermc.io/v3/projects/folia/versions/{v}/builds/latest", ct).ConfigureAwait(false);
                var dl = d.RootElement.GetProperty("downloads").GetProperty("server:default");
                return new ServerDownload("folia.jar", new Uri(dl.GetProperty("url").GetString()!), dl.GetProperty("checksums").GetProperty("sha256").GetString(), HashAlgorithmName.SHA256, 0);
            }

            case ServerPlatform.Leaf:
            {
                using var d = await JsonAsync($"https://api.leafmc.one/v2/projects/leaf/versions/{v}/builds", ct).ConfigureAwait(false);
                var b = d.RootElement.GetProperty("builds").EnumerateArray().Last();
                var file = b.GetProperty("downloads").GetProperty("primary");
                var name = file.GetProperty("name").GetString()!;
                return new ServerDownload("leaf.jar", new Uri($"https://api.leafmc.one/v2/projects/leaf/versions/{v}/builds/{b.GetProperty("build").GetInt32()}/downloads/{Uri.EscapeDataString(name)}"),
                    file.GetProperty("sha256").GetString(), HashAlgorithmName.SHA256, 0);
            }

            case ServerPlatform.Leaves:
            {
                using var d = await JsonAsync($"https://api.leavesmc.org/v2/projects/leaves/versions/{v}/builds/latest", ct).ConfigureAwait(false);
                var build = d.RootElement.GetProperty("build").GetInt32();
                var file = d.RootElement.GetProperty("downloads").GetProperty("application");
                return new ServerDownload("leaves.jar", new Uri($"https://api.leavesmc.org/v2/projects/leaves/versions/{v}/builds/{build}/downloads/application"),
                    file.GetProperty("sha256").GetString(), HashAlgorithmName.SHA256, 0);
            }

            case ServerPlatform.Spigot:
            case ServerPlatform.CraftBukkit:
                // Spigot does not distribute server jars; BuildTools compiles them locally from Mojang's server.
                return new ServerDownload("BuildTools.jar", new Uri(BuildToolsUrl), null, HashAlgorithmName.SHA256, 0, IsInstaller: true);

            case ServerPlatform.Quilt:
            {
                using var d = await JsonAsync("https://meta.quiltmc.org/v3/versions/installer", ct).ConfigureAwait(false);
                var url = d.RootElement[0].GetProperty("url").GetString()!;

                // The meta API's hash fields can lag behind the artifact; the Maven .sha1 next to the jar is authoritative.
                var sha1 = (await http.GetStringAsync(new Uri(url + ".sha1"), ct).ConfigureAwait(false)).Trim().Split(' ')[0];
                return new ServerDownload("quilt-installer.jar", new Uri(url), sha1, HashAlgorithmName.SHA1, 0, IsInstaller: true);
            }

            case ServerPlatform.SpongeVanilla:
            {
                using var list = await JsonAsync($"https://dl-api.spongepowered.org/v2/groups/org.spongepowered/artifacts/spongevanilla/versions?tags=minecraft:{v}&limit=10", ct).ConfigureAwait(false);
                var versions = list.RootElement.GetProperty("artifacts").EnumerateObject().ToList();
                var pick = versions.FirstOrDefault(a => a.Value.TryGetProperty("recommended", out var r) && r.GetBoolean());
                var name = (pick.Value.ValueKind == JsonValueKind.Undefined ? versions.First() : pick).Name;
                using var art = await JsonAsync($"https://dl-api.spongepowered.org/v2/groups/org.spongepowered/artifacts/spongevanilla/versions/{Uri.EscapeDataString(name)}", ct).ConfigureAwait(false);
                var assets = art.RootElement.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("extension").GetString() == "jar").ToList();
                var asset = assets.FirstOrDefault(a => a.GetProperty("classifier").GetString() == "universal");
                if (asset.ValueKind == JsonValueKind.Undefined)
                {
                    asset = assets.First(a => string.IsNullOrEmpty(a.GetProperty("classifier").GetString()));
                }
                return new ServerDownload("spongevanilla.jar", new Uri(asset.GetProperty("downloadUrl").GetString()!), asset.GetProperty("sha1").GetString(), HashAlgorithmName.SHA1, 0);
            }

            case ServerPlatform.Mohist:
            case ServerPlatform.Youer:
            {
                var project = p.ToString().ToLowerInvariant();
                using var d = await JsonAsync($"https://api.mohistmc.com/project/{project}/{v}/builds", ct).ConfigureAwait(false);
                var b = d.RootElement.EnumerateArray().First();
                return new ServerDownload($"{project}.jar", new Uri($"https://api.mohistmc.com/project/{project}/{v}/builds/{b.GetProperty("id").GetInt32()}/download"),
                    b.GetProperty("file_sha256").GetString(), HashAlgorithmName.SHA256, 0);
            }

            case ServerPlatform.Velocity:
            {
                using var d = await JsonAsync($"https://fill.papermc.io/v3/projects/velocity/versions/{v}/builds/latest", ct).ConfigureAwait(false);
                var dl = d.RootElement.GetProperty("downloads").GetProperty("server:default");
                return new ServerDownload("velocity.jar", new Uri(dl.GetProperty("url").GetString()!), dl.GetProperty("checksums").GetProperty("sha256").GetString(), HashAlgorithmName.SHA256, 0);
            }

            case ServerPlatform.BungeeCord:
                // md_5's Jenkins publishes no checksum; HTTPS only.
                return new ServerDownload("BungeeCord.jar", new Uri("https://hub.spigotmc.org/jenkins/job/BungeeCord/lastSuccessfulBuild/artifact/bootstrap/target/BungeeCord.jar"), null, HashAlgorithmName.SHA256, 0);

            case ServerPlatform.Arclight:
            {
                using var d = await JsonAsync("https://api.github.com/repos/IzzelAliz/Arclight/releases?per_page=40", ct).ConfigureAwait(false);
                var asset = d.RootElement.EnumerateArray().SelectMany(r => r.GetProperty("assets").EnumerateArray())
                    .First(a => ArclightAsset().Match(a.GetProperty("name").GetString()!) is { Success: true } m && m.Groups["mc"].Value == version);
                var url = new Uri(asset.GetProperty("browser_download_url").GetString()!);
                var digest = asset.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String && dg.GetString()!.StartsWith("sha256:", StringComparison.Ordinal) ? dg.GetString()![7..] : null;
                return new ServerDownload("arclight.jar", url, digest, HashAlgorithmName.SHA256, 0);
            }
        }

        return null;
    }
}
