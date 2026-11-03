using System.IO.Compression;
using System.Security.Cryptography;
using PackCheck.Servers;

namespace PackCheck.Tests;

public class ModpackTests
{
    private const string Index = """
        {
          "formatVersion": 1,
          "game": "minecraft",
          "versionId": "1.2.0",
          "name": "Tiny Pack",
          "files": [
            { "path": "mods/lithium.jar", "hashes": { "sha1": "aa", "sha512": "bb" }, "env": { "client": "required", "server": "required" },
              "downloads": [ "https://cdn.modrinth.com/data/x/lithium.jar" ], "fileSize": 123 },
            { "path": "mods/sodium.jar", "hashes": { "sha512": "cc" }, "env": { "client": "required", "server": "unsupported" },
              "downloads": [ "https://cdn.modrinth.com/data/y/sodium.jar" ], "fileSize": 5 },
            { "path": "config/x.toml", "hashes": { "sha512": "dd" },
              "downloads": [ "https://evil.example.com/x.toml", "https://github.com/a/b/releases/download/1/x.toml" ], "fileSize": 7 }
          ],
          "dependencies": { "minecraft": "1.21.1", "fabric-loader": "0.16.9" }
        }
        """;

    [Fact]
    public void ParsesModrinthIndex()
    {
        var m = Modpacks.ParseModrinthIndex(Index);
        Assert.Equal(ModpackFormat.Modrinth, m.Format);
        Assert.Equal("Tiny Pack", m.Name);
        Assert.Equal("1.2.0", m.PackVersion);
        Assert.Equal(ServerPlatform.Fabric, m.Platform);
        Assert.Equal("1.21.1", m.MinecraftVersion);
        Assert.Equal("0.16.9", m.LoaderVersion);
        var lithium = m.Files[0];
        Assert.Equal("mods/lithium.jar", lithium.Path);
        Assert.Equal("bb", lithium.Hash);
        Assert.Equal(HashAlgorithmName.SHA512, lithium.Algorithm);
        Assert.Equal(123, lithium.Size);
    }

    [Fact]
    public void SkipsServerUnsupportedFiles()
    {
        var m = Modpacks.ParseModrinthIndex(Index);
        Assert.DoesNotContain(m.Files, f => f.Path.Contains("sodium", StringComparison.Ordinal));
        Assert.Equal(2, m.Files.Count);
    }

    [Fact]
    public void KeepsOnlyAllowedDownloadHosts()
    {
        var config = Modpacks.ParseModrinthIndex(Index).Files.Single(f => f.Path == "config/x.toml");
        Assert.Equal("github.com", Assert.Single(config.Downloads).Host);
    }

    [Theory]
    [InlineData("https://cdn.modrinth.com/data/a.jar", true)]
    [InlineData("https://github.com/a/b.jar", true)]
    [InlineData("https://raw.githubusercontent.com/a/b.jar", true)]
    [InlineData("https://gitlab.com/a/b.jar", true)]
    [InlineData("http://cdn.modrinth.com/data/a.jar", false)]
    [InlineData("https://cdn.modrinth.com.evil.net/a.jar", false)]
    [InlineData("https://evil.example.com/a.jar", false)]
    public void HostAllowlist(string url, bool allowed) => Assert.Equal(allowed, Modpacks.IsAllowedHost(new Uri(url)));

    [Fact]
    public void RejectsFileWithoutAllowedHost()
    {
        var json = Index.Replace("https://cdn.modrinth.com/data/x/lithium.jar", "https://evil.example.com/lithium.jar", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => Modpacks.ParseModrinthIndex(json));
    }

    [Theory]
    [InlineData("../evil.jar")]
    [InlineData("mods/../../evil.jar")]
    [InlineData("..\\evil.jar")]
    [InlineData("C:\\Windows\\evil.jar")]
    [InlineData("/etc/evil")]
    [InlineData("")]
    public void RejectsPathTraversal(string path)
    {
        var root = Path.Combine(Path.GetTempPath(), "pc-mp-root");
        Assert.Throws<InvalidDataException>(() => Modpacks.SafePath(root, path));
        var json = Index.Replace("mods/lithium.jar", path.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => Modpacks.ParseModrinthIndex(json));
    }

    [Fact]
    public void SafePathAcceptsNestedPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "pc-mp-root");
        Assert.Equal(Path.Combine(root, "config", "a.toml"), Modpacks.SafePath(root, "config/a.toml"));
    }

    [Theory]
    [InlineData("fabric-loader", ServerPlatform.Fabric)]
    [InlineData("quilt-loader", ServerPlatform.Quilt)]
    [InlineData("forge", ServerPlatform.Forge)]
    [InlineData("neoforge", ServerPlatform.NeoForge)]
    public void MapsDependencyToPlatform(string key, ServerPlatform platform)
    {
        var (p, v) = Modpacks.MapDependencies(new Dictionary<string, string> { ["minecraft"] = "1.21.1", [key] = "1.0" });
        Assert.Equal(platform, p);
        Assert.Equal("1.0", v);
    }

    [Fact]
    public void NoLoaderMeansVanilla() =>
        Assert.Equal(ServerPlatform.Vanilla, Modpacks.MapDependencies(new Dictionary<string, string> { ["minecraft"] = "1.21.1" }).Platform);

    [Fact]
    public void ServerOverridesComeAfterOverrides()
    {
        var root = Path.Combine(Path.GetTempPath(), "pc-mp-root");
        var plan = Modpacks.PlanOverrides(
            ["server-overrides/config/a.toml", "overrides/config/a.toml", "overrides/", "client-overrides/options.txt", "overrides/mods/x.jar"],
            ["overrides", "server-overrides"], root);
        Assert.Equal(["overrides/config/a.toml", "overrides/mods/x.jar", "server-overrides/config/a.toml"], plan.Select(p => p.Entry));
        Assert.Equal(Path.Combine(root, "config", "a.toml"), plan[^1].Target);
    }

    [Fact]
    public void OverrideTraversalIsRejected() =>
        Assert.Throws<InvalidDataException>(() => Modpacks.PlanOverrides(["overrides/../../evil.bat"], ["overrides"], Path.Combine(Path.GetTempPath(), "pc-mp-root")));

    [Fact]
    public void AppliesOverridesFromArchive()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pc-mp-" + Guid.NewGuid().ToString("N"));
        var server = Path.Combine(dir, "server");
        Directory.CreateDirectory(server);
        var pack = Path.Combine(dir, "p.mrpack");
        try
        {
            using (var zip = ZipFile.Open(pack, ZipArchiveMode.Create))
            {
                Write(zip, "modrinth.index.json", Index);
                Write(zip, "overrides/config/a.toml", "client");
                Write(zip, "server-overrides/config/a.toml", "server");
                Write(zip, "client-overrides/options.txt", "x");
            }

            var m = Modpacks.Read(pack);
            Assert.Equal(2, Modpacks.ApplyOverrides(pack, m, server));
            Assert.Equal("server", File.ReadAllText(Path.Combine(server, "config", "a.toml")));
            Assert.False(File.Exists(Path.Combine(server, "options.txt")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ParsesCurseForgeManifest()
    {
        var m = Modpacks.ParseCurseForgeManifest("""
            {
              "minecraft": { "version": "1.20.1", "modLoaders": [ { "id": "forge-47.3.0", "primary": true } ] },
              "manifestType": "minecraftModpack", "name": "CF Pack", "version": "2.0",
              "files": [ { "projectID": 238222, "fileID": 4712345, "required": true }, { "projectID": 1, "fileID": 2, "required": false } ],
              "overrides": "overrides"
            }
            """);
        Assert.Equal(ModpackFormat.CurseForge, m.Format);
        Assert.Equal(ServerPlatform.Forge, m.Platform);
        Assert.Equal("47.3.0", m.LoaderVersion);
        Assert.Equal("1.20.1", m.MinecraftVersion);
        Assert.Equal(2, m.CurseForgeFiles.Count);
        Assert.Equal(new CurseForgeFileRef(238222, 4712345, true), m.CurseForgeFiles[0]);
        Assert.False(m.CurseForgeFiles[1].Required);
        Assert.Equal(["overrides"], m.OverrideFolders);
    }

    [Theory]
    [InlineData("fabric-0.16.9", ServerPlatform.Fabric)]
    [InlineData("neoforge-21.1.77", ServerPlatform.NeoForge)]
    [InlineData("quilt-0.27.1", ServerPlatform.Quilt)]
    public void MapsCurseForgeLoader(string id, ServerPlatform platform) => Assert.Equal(platform, Modpacks.MapCurseForgeLoader(id).Platform);

    [Fact]
    public void CurseForgeHostsAreSeparate()
    {
        Assert.True(Modpacks.IsAllowedHost(new Uri("https://edge.forgecdn.net/files/1/2/a.jar"), ModpackFormat.CurseForge));
        Assert.False(Modpacks.IsAllowedHost(new Uri("https://edge.forgecdn.net/files/1/2/a.jar")));
    }

    [Fact]
    public void WithPortReplacesThePort() =>
        Assert.Equal("motd=x\nserver-port=25570\n", Modpacks.WithPort("server-port=25565\nmotd=x\n", 25570));

    [Fact]
    public void RequestCarriesPackPlatformVersionAndLoader()
    {
        var wizard = new ServerCreator.Request("S", "D:/x", ServerPlatform.Paper, "1.21.4", "java", 4096, 25570, null, true) { CpuCores = 4 };
        var r = ModpackInstaller.RequestFor(wizard, Modpacks.ParseModrinthIndex(Index));
        Assert.Equal(ServerPlatform.Fabric, r.Platform);
        Assert.Equal("1.21.1", r.Version);
        Assert.Equal("0.16.9", r.LoaderVersion);
        Assert.Equal(25570, r.Port);
        Assert.Equal(4, r.CpuCores);
    }

    [Fact]
    public void RequestCarriesCurseForgeLoader()
    {
        var m = Modpacks.ParseCurseForgeManifest("""{ "minecraft": { "version": "1.21.1", "modLoaders": [ { "id": "neoforge-21.1.77", "primary": true } ] }, "files": [] }""");
        var r = ModpackInstaller.RequestFor(new ServerCreator.Request("S", "D:/x", ServerPlatform.Paper, "1.21.4", "java", 4096, 25565, null, true), m);
        Assert.Equal(ServerPlatform.NeoForge, r.Platform);
        Assert.Equal("21.1.77", r.LoaderVersion);
    }

    [Fact]
    public void PickLoaderUsesExactVersionOrFallsBack()
    {
        string[] published = ["0.16.10", "0.16.9", "0.15.11"];
        Assert.Equal("0.16.9", ServerSources.PickLoader(published, "0.16.9"));
        Assert.Null(ServerSources.PickLoader(published, "0.99.0"));
        Assert.Null(ServerSources.PickLoader(published, null));
        Assert.Null(ServerSources.PickLoader(published, " "));
    }

    [Theory]
    [InlineData("1.20.1", "47.3.0", "1.20.1-47.3.0")]
    [InlineData("1.20.1", "1.20.1-47.3.0", "1.20.1-47.3.0")]
    public void ForgeFullVersion(string mc, string loader, string full) => Assert.Equal(full, ServerSources.ForgeFullVersion(mc, loader));

    [Fact]
    public void FallbackWarningNamesBothVersions()
    {
        var w = ServerSources.LoaderFallbackWarning(ServerPlatform.Fabric, "0.99.0", "0.16.10");
        Assert.Contains("0.99.0", w, StringComparison.Ordinal);
        Assert.Contains("0.16.10", w, StringComparison.Ordinal);
    }

    private static void Write(ZipArchive zip, string name, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open());
        w.Write(text);
    }
}
