using PackCheck.Servers;

namespace PackCheck.Tests;

public class PlatformTests
{
    [Theory]
    [InlineData("21.1.77", "1.21.1")]
    [InlineData("21.0.5", "1.21")]
    [InlineData("20.4.237", "1.20.4")]
    [InlineData("26.1.2.123", "26.1.2")]
    [InlineData("26.3.0.99", "26.3")]
    public void NeoForgeVersionMapsToMinecraft(string neo, string mc) => Assert.Equal(mc, PlatformInfo.NeoForgeToMinecraft(neo));

    [Fact]
    public void ModernForgeStartsWithArgsFile()
    {
        var def = new ServerDefinition
        {
            Id = "x",
            Name = "x",
            Directory = ".",
            Platform = ServerPlatform.NeoForge,
            MinecraftVersion = "1.21.1",
            JarFile = "run.bat",
            ArgsFile = "libraries/net/neoforged/neoforge/21.1.77/win_args.txt",
            JavaPath = "java",
            UseAikarFlags = false,
        };
        var args = ServerProcess.BuildArguments(def);
        Assert.Equal(["-Xms1024M", "-Xmx4096M", "-Dstdout.encoding=UTF-8", "-Dstderr.encoding=UTF-8", "-Dterminal.jline=false",
            "@libraries/net/neoforged/neoforge/21.1.77/win_args.txt", "nogui"], args);
    }

    /// <summary>Hits the real APIs. Excluded in CI with --filter Category!=Network.</summary>
    [Theory]
    [Trait("Category", "Network")]
    [InlineData(ServerPlatform.Paper)]
    [InlineData(ServerPlatform.Purpur)]
    [InlineData(ServerPlatform.Vanilla)]
    [InlineData(ServerPlatform.Fabric)]
    [InlineData(ServerPlatform.Forge)]
    [InlineData(ServerPlatform.NeoForge)]
    [InlineData(ServerPlatform.Folia)]
    [InlineData(ServerPlatform.Leaf)]
    [InlineData(ServerPlatform.Leaves)]
    [InlineData(ServerPlatform.Spigot)]
    [InlineData(ServerPlatform.CraftBukkit)]
    [InlineData(ServerPlatform.Quilt)]
    [InlineData(ServerPlatform.SpongeVanilla)]
    [InlineData(ServerPlatform.Mohist)]
    [InlineData(ServerPlatform.Youer)]
    [InlineData(ServerPlatform.Arclight)]
    public async Task ResolvesNewestAndOldestVersion(ServerPlatform platform)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PackCheck-tests/2.0");
        var sources = new ServerSources(http);
        var versions = await sources.GetVersionsAsync(platform, CancellationToken.None);
        Assert.NotEmpty(versions);
        foreach (var v in new[] { versions[0], versions[^1] }.Distinct())
        {
            var dl = await sources.ResolveAsync(platform, v, CancellationToken.None);
            Assert.Equal(Uri.UriSchemeHttps, dl.Url.Scheme);
            using var res = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, dl.Url), HttpCompletionOption.ResponseHeadersRead);
            Assert.True(res.IsSuccessStatusCode, $"{platform} {v} -> {dl.Url} {(int)res.StatusCode}");
        }
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task NeoForgeVersionListIncludesLegacy1201()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PackCheck-tests/2.0");
        var versions = await new ServerSources(http).GetVersionsAsync(ServerPlatform.NeoForge, CancellationToken.None);
        Assert.Contains("1.20.1", versions);
    }

    [Theory]
    [Trait("Category", "Network")]
    [InlineData("47.1.106")]
    [InlineData("1.20.1-47.1.106")]
    public async Task NeoForge1201UsesLegacyForgeCoordinate(string requested)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PackCheck-tests/2.0");
        var dl = await new ServerSources(http).ResolveAsync(ServerPlatform.NeoForge, "1.20.1", CancellationToken.None, requested);
        Assert.Equal("47.1.106", dl.LoaderVersion);
        Assert.Contains("/net/neoforged/forge/1.20.1-47.1.106/", dl.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Null(dl.LoaderWarning);
    }
}
