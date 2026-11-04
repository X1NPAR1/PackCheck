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
        var s = new ServerDefinition { Id = "s", Name = "s", Directory = dir, Platform = ServerPlatform.Paper, MinecraftVersion = "1.21.4", JarFile = "p.jar", JavaPath = "java" };
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
}
