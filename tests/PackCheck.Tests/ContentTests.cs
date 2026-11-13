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

    private static HttpClient NetworkClient() => new() { Timeout = TimeSpan.FromSeconds(15) };

    private static ServerDefinition PaperServer(string mc) => new()
    {
        Id = "s", Name = "s", Directory = Path.GetTempPath(), Platform = ServerPlatform.Paper, MinecraftVersion = mc, JarFile = "paper.jar", JavaPath = "java",
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
