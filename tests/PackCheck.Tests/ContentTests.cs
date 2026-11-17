using System.IO.Compression;
using System.Text.Json.Nodes;
using PackCheck.Core;
using PackCheck.Servers;
using Xunit;

namespace PackCheck.Tests;

public class ContentTests
{
    [Theory]
    [InlineData("https://modrinth.com/plugin/chunky", LinkSource.Modrinth, "chunky", null)]
    [InlineData("https://modrinth.com/mod/lithium/versions", LinkSource.Modrinth, "lithium", null)]
    [InlineData("lithium", LinkSource.Modrinth, "lithium", null)]
    [InlineData("https://hangar.papermc.io/HelpChat/PlaceholderAPI", LinkSource.Hangar, "PlaceholderAPI", "HelpChat")]
    [InlineData("https://www.spigotmc.org/resources/luckperms.28140/", LinkSource.Spigot, "28140", null)]
    [InlineData("https://github.com/EssentialsX/Essentials/releases", LinkSource.GitHub, "Essentials", "EssentialsX")]
    [InlineData("EssentialsX/Essentials", LinkSource.GitHub, "Essentials", "EssentialsX")]
    [InlineData("https://www.curseforge.com/minecraft/mc-mods/jei", LinkSource.CurseForge, "jei", null)]
    [InlineData("https://example.org/files/thing.jar", LinkSource.DirectJar, "https://example.org/files/thing.jar", null)]
    public void Links_AreRecognized(string input, LinkSource source, string id, string? owner)
    {
        var link = ContentLinks.Parse(input);

        Assert.NotNull(link);
        Assert.Equal((source, id, owner), (link!.Source, link.Id, link.Owner));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://example.org/x.jar")]
    [InlineData("not a link at all")]
    public void Garbage_IsNotALink(string input) => Assert.Null(ContentLinks.Parse(input));

    [Fact]
    public void GitHubAsset_SkipsSourcesAndPrefersTheLoader()
    {
        var assets = (JsonNode.Parse("""[{"name":"Mod-1.0-sources.jar"},{"name":"Mod-1.0-forge.jar"},{"name":"Mod-1.0-fabric.jar"},{"name":"notes.txt"}]""") as JsonArray)!;

        Assert.Equal("Mod-1.0-fabric.jar", ContentLinks.PickGitHubAsset(assets, "fabric")!["name"]!.GetValue<string>());
        Assert.Equal("Mod-1.0-forge.jar", ContentLinks.PickGitHubAsset(assets, null)!["name"]!.GetValue<string>());
    }

    [Fact]
    public void OnlyPluginOrModJarsAreAccepted()
    {
        static byte[] Zip(string entry)
        {
            var ms = new MemoryStream();
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                z.CreateEntry(entry);
            }

            return ms.ToArray();
        }

        Assert.True(ContentLinks.IsAddonJar(Zip("plugin.yml")));
        Assert.True(ContentLinks.IsAddonJar(Zip("fabric.mod.json")));
        Assert.False(ContentLinks.IsAddonJar(Zip("readme.txt")));
        Assert.False(ContentLinks.IsAddonJar([1, 2, 3]));
    }

    [Fact]
    public void Revert_RestoresThePreviousJar()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pc-rev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "plugins"));
        var s = new ServerDefinition
        {
            Id = "s",
            Name = "s",
            Directory = dir,
            Platform = ServerPlatform.Paper,
            MinecraftVersion = "1.21.4",
            JarFile = "p.jar",
            JavaPath = "java"
        };
        var hist = AddonUpdates.HistoryDir(s);
        Directory.CreateDirectory(hist);
        File.WriteAllText(Path.Combine(hist, "Shop-1.0.jar"), "old");
        File.WriteAllText(Path.Combine(hist, "Shop-2.0.jar.previous"), "Shop-1.0.jar");
        var current = Path.Combine(dir, "plugins", "Shop-2.0.jar");
        File.WriteAllText(current, "new");

        Assert.NotNull(AddonUpdates.PreviousOf(s, current));
        var restored = AddonUpdates.Revert(s, current);

        Assert.Equal("old", File.ReadAllText(restored));
        Assert.False(File.Exists(current));
        Assert.Null(AddonUpdates.PreviousOf(s, restored));
    }

    [Fact]
    public void MclogsInsights_AreRead()
    {
        var json = JsonNode.Parse("""{"analysis":{"problems":[{"message":"Missing dependency Vault","solutions":[{"message":"Install Vault"}]}]}}""") as JsonObject;

        var p = Assert.Single(MclogsShare.ParseInsights(json));

        Assert.Equal("Missing dependency Vault", p.Problem);
        Assert.Equal(["Install Vault"], p.Fixes);
        Assert.Empty(MclogsShare.ParseInsights(null));
    }

    [Fact]
    public void AddonSources_RememberMoveAndCompare()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pc-src-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            AddonSources.Remember(dir, "Direct.jar", new ContentLink(LinkSource.DirectJar, "https://x/Direct.jar"), null);
            AddonSources.Remember(dir, "PlaceholderAPI-2.11.5.jar", new ContentLink(LinkSource.Hangar, "PlaceholderAPI", "HelpChat"), "2.11.5");

            var map = AddonSources.Load(dir);
            Assert.Equal(new AddonSource(LinkSource.Hangar, "PlaceholderAPI", "HelpChat", "2.11.5"), Assert.Single(map).Value);

            AddonSources.Move(dir, "placeholderapi-2.11.5.jar", "PlaceholderAPI-2.11.6.jar", "2.11.6");
            var moved = Assert.Single(AddonSources.Load(dir));
            Assert.Equal(("PlaceholderAPI-2.11.6.jar", "2.11.6"), (moved.Key, moved.Value.Version));

            var jar = Path.Combine(dir, "a.jar");
            File.WriteAllBytes(jar, [1, 2, 3]);
            var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 }));
            var known = new AddonSource(LinkSource.Spigot, "1", null, "100");
            Assert.False(AddonSources.IsNewer(known, new ContentDownload("https://x", "a.jar", Version: "100"), jar));
            Assert.True(AddonSources.IsNewer(known, new ContentDownload("https://x", "a.jar", Version: "101"), jar));
            var unknown = known with { Version = null };
            Assert.False(AddonSources.IsNewer(unknown, new ContentDownload("https://x", "a.jar", Sha256: sha), jar));
            Assert.True(AddonSources.IsNewer(unknown, new ContentDownload("https://x", "a.jar", Sha256: new string('0', 64)), jar));
            Assert.False(AddonSources.IsNewer(unknown, new ContentDownload("https://x", "a.jar"), jar));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Live: a jar remembered as installed from Hangar at an old version is offered the current Hangar build.</summary>
    [Fact]
    [Trait("Category", "Network")]
    public async Task Hangar_RememberedSourceOffersUpdate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pc-hangar-" + Guid.NewGuid().ToString("N"));
        try
        {
            var plugins = Directory.CreateDirectory(Path.Combine(dir, "plugins")).FullName;
            using (var zip = System.IO.Compression.ZipFile.Open(Path.Combine(plugins, "PlaceholderAPI-0.0.1.jar"), System.IO.Compression.ZipArchiveMode.Create))
            {
                using var w = new StreamWriter(zip.CreateEntry("plugin.yml").Open());
                w.Write("name: PlaceholderAPI\nversion: 0.0.1\nmain: me.clip.placeholderapi.PlaceholderAPIPlugin\napi-version: '1.13'\n");
            }

            AddonSources.Remember(dir, "PlaceholderAPI-0.0.1.jar", ContentLinks.Parse("https://hangar.papermc.io/HelpChat/PlaceholderAPI")!, "0.0.1");
            using var http = NetworkClient();

            var updates = await AddonUpdates.CheckAsync(http, PaperServer("1.21.4") with { Directory = dir }, CancellationToken.None);

            var u = Assert.Single(updates, x => x.FileName == "PlaceholderAPI-0.0.1.jar");
            Assert.Equal(LinkSource.Hangar, u.UpdateSource);
            Assert.StartsWith("https://", u.UpdateUrl, StringComparison.Ordinal);
            Assert.NotEqual("0.0.1", u.UpdateVersion);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private sealed class CurseForgeHandler(string filesJson) : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _responses = [];

        public List<string> Requests { get; } = [];

        public string? Key { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            Key = request.Headers.TryGetValues("x-api-key", out var v) ? v.First() : null;
            var body = request.RequestUri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
                ? """{"data":[{"id":42,"slug":"other"},{"id":394468,"slug":"luckperms"}]}"""
                : filesJson;
            var response = new HttpResponseMessage { Content = new StringContent(body) };
            _responses.Add(response);
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var r in _responses)
                {
                    r.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task CurseForge_ResolvesTheFileForTheServersVersion()
    {
        var handler = new CurseForgeHandler("""{"data":[{"fileName":"LuckPerms.jar","displayName":"LuckPerms 5","downloadUrl":"https://edge.forgecdn.net/files/1/LuckPerms.jar","hashes":[{"algo":2,"value":"md5"},{"algo":1,"value":"abc123"}]}]}""");
        using var http = new HttpClient(handler);
        var d = await ContentLinks.ResolveAsync(http, ContentLinks.Parse("https://www.curseforge.com/minecraft/bukkit-plugins/luckperms")!, PaperServer("1.21.4"), CancellationToken.None, () => " key\n");

        Assert.Equal("https://edge.forgecdn.net/files/1/LuckPerms.jar", d.Url);
        Assert.Equal("LuckPerms.jar", d.FileName);
        Assert.Equal("abc123", d.Sha1);
        Assert.Equal("key", handler.Key);
        Assert.Contains("/v1/mods/394468/files?gameVersion=1.21.4", handler.Requests[1], StringComparison.Ordinal);
        Assert.DoesNotContain("modLoaderType", handler.Requests[1], StringComparison.Ordinal);
    }

    [Fact]
    public void CurseForgeSupport_PairsEachLoaderWithEachMinecraftVersion()
    {
        var files = JsonNode.Parse("""[{"gameVersions":["1.20.1","Forge","Java 17"]},{"gameVersions":["1.21.1","1.21","Fabric","NeoForge"]}]""")!.AsArray();
        var s = ContentLinks.CurseForgeSupport(files);

        Assert.Contains(("forge", "1.20.1"), s);
        Assert.Contains(("fabric", "1.21.1"), s);
        Assert.Contains(("neoforge", "1.21"), s);
        Assert.DoesNotContain(("forge", "1.21.1"), s);
        Assert.Equal(5, s.Count);
    }

    [Fact]
    public async Task CurseForge_WithoutKeyAsksForOne()
    {
        using var http = new HttpClient(new CurseForgeHandler("{}"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContentLinks.ResolveAsync(http, new ContentLink(LinkSource.CurseForge, "luckperms"), PaperServer("1.21.4"), CancellationToken.None));
        Assert.Contains("API key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurseForge_RefusesDisabledDownloadsAndForeignHosts()
    {
        using var disabled = new HttpClient(new CurseForgeHandler("""{"data":[{"fileName":"a.jar","downloadUrl":null,"hashes":[]}]}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContentLinks.ResolveAsync(disabled, new ContentLink(LinkSource.CurseForge, "luckperms"), PaperServer("1.21.4"), CancellationToken.None, () => "k"));

        using var foreign = new HttpClient(new CurseForgeHandler("""{"data":[{"fileName":"a.jar","downloadUrl":"https://evil.example/a.jar","hashes":[]}]}"""));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContentLinks.ResolveAsync(foreign, new ContentLink(LinkSource.CurseForge, "luckperms"), PaperServer("1.21.4"), CancellationToken.None, () => "k"));
        Assert.Contains("unexpected host", ex.Message, StringComparison.Ordinal);
    }

    private static HttpClient NetworkClient() => new() { Timeout = TimeSpan.FromSeconds(15) };

    private static ServerDefinition PaperServer(string mc) => new()
    {
        Id = "s",
        Name = "s",
        Directory = Path.GetTempPath(),
        Platform = ServerPlatform.Paper,
        MinecraftVersion = mc,
        JarFile = "paper.jar",
        JavaPath = "java"
    };

    /// <summary>Read-only: resolves a well-known plugin's download for a real Paper version against the live Modrinth API.</summary>
    [Fact]
    [Trait("Category", "Network")]
    public async Task Modrinth_ResolvesARealDownload()
    {
        using var http = NetworkClient();
        var d = await ContentLinks.ResolveAsync(http, ContentLinks.Parse("https://modrinth.com/plugin/chunky")!, PaperServer("1.21.4"), CancellationToken.None);

        Assert.StartsWith("https://", d.Url, StringComparison.Ordinal);
        Assert.EndsWith(".jar", d.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(d.Sha512);
    }

    /// <summary>Read-only: resolves a Hangar (PaperMC) project's Paper build against the live Hangar API.</summary>
    [Fact]
    [Trait("Category", "Network")]
    public async Task Hangar_ResolvesARealDownload()
    {
        using var http = NetworkClient();
        var d = await ContentLinks.ResolveAsync(http, ContentLinks.Parse("https://hangar.papermc.io/HelpChat/PlaceholderAPI")!, PaperServer("1.21.4"), CancellationToken.None);

        Assert.StartsWith("https://", d.Url, StringComparison.Ordinal);
        Assert.NotNull(d.FileName);
    }

    /// <summary>Read-only: resolves a SpigotMC resource through the live Spiget API. Spiget never publishes a hash, so none is asserted.</summary>
    [Fact]
    [Trait("Category", "Network")]
    public async Task Spiget_ResolvesARealDownload()
    {
        using var http = NetworkClient();
        var d = await ContentLinks.ResolveAsync(http, ContentLinks.Parse("https://www.spigotmc.org/resources/luckperms.28140/")!, PaperServer("1.21.4"), CancellationToken.None);

        Assert.Equal("https://api.spiget.org/v2/resources/28140/download", d.Url);
        Assert.EndsWith(".jar", d.FileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Read-only: resolves a GitHub-hosted plugin's latest release asset through the live GitHub API.</summary>
    [Fact]
    [Trait("Category", "Network")]
    public async Task GitHub_ResolvesARealDownload()
    {
        using var http = NetworkClient();
        var d = await ContentLinks.ResolveAsync(http, ContentLinks.Parse("EssentialsX/Essentials")!, PaperServer("1.21.4"), CancellationToken.None);

        Assert.StartsWith("https://", d.Url, StringComparison.Ordinal);
        Assert.EndsWith(".jar", d.FileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Shares a short log on the live mclo.gs API and reads back its paste URL.</summary>
    [Fact]
    [Trait("Category", "Network")]
    public async Task Mclogs_SharesALogAndReturnsAUrl()
    {
        using var http = NetworkClient();
        var result = await MclogsShare.ShareAsync(http, "[INFO]: PackCheck network test log " + Guid.NewGuid().ToString("N"), CancellationToken.None);

        Assert.StartsWith("https://mclo.gs/", result.Url, StringComparison.Ordinal);
    }
}
