using System.IO.Compression;
using PackCheck.Servers;
using Xunit;

namespace PackCheck.Tests;

public sealed class ServerClonerTests : IDisposable
{
    private readonly string _root;
    private readonly ServerDefinition _def;

    public ServerClonerTests()
    {
        _root = Directory.CreateTempSubdirectory("pc-clone").FullName;
        var dir = Path.Combine(_root, "srv");
        Write(dir, "server.properties", "# keep me\nmotd=hi\nserver-port=25565\n");
        Write(dir, "paper.jar", "jar");
        Write(dir, "eula.txt", "eula=true\n");
        Write(dir, "world/level.dat", "w1");
        Write(dir, "world/region/r.0.0.mca", "region");
        Write(dir, "world/session.lock", "lock");
        Write(dir, "world_nether/level.dat", "n1");
        Write(dir, "plugins/Foo.jar", "foo");
        Write(dir, "plugins/Foo/config.yml", "a: 1\n");
        Write(dir, "logs/latest.log", "log");
        Write(dir, "crash-reports/c.txt", "crash");
        Write(dir, "cache/mojang.jar", "cache");
        Write(dir, ".packcheck/metrics/m.csv", "1");
        Write(dir, ".packcheck/notes.json", "{}");
        Write(dir, ".packcheck-backups/old.zip", "zip");
        Write(dir, "plugins/Foo/data.lock", "x");
        Write(dir, "libraries/lib.jar", "lib");
        _def = new ServerDefinition
        {
            Id = "orig",
            Name = "srv",
            Directory = dir,
            Platform = ServerPlatform.Paper,
            MinecraftVersion = "1.21.4",
            JarFile = "paper.jar",
            JavaPath = "java",
            MaxMemoryMb = 3072,
            Port = 25565,
        };
    }

    public void Dispose() => Directory.Delete(_root, true);

    private static void Write(string dir, string rel, string text)
    {
        var f = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, text);
    }

    private static HashSet<string> Files(string dir) =>
        [.. Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))];

    [Theory]
    [InlineData("logs/latest.log", true)]
    [InlineData("crash-reports/c.txt", true)]
    [InlineData("cache/mojang.jar", true)]
    [InlineData(".packcheck/metrics/m.csv", true)]
    [InlineData(".packcheck-backups/old.zip", true)]
    [InlineData("world/session.lock", true)]
    [InlineData("plugins/Foo/data.lock", true)]
    [InlineData(".packcheck/notes.json", false)]
    [InlineData("plugins/Foo/config.yml", false)]
    [InlineData("server.properties", false)]
    public void Skips_rebuilt_and_locked_files(string rel, bool skipped) => Assert.Equal(skipped, ServerCloner.IsSkipped(rel));

    [Fact]
    public async Task Clone_with_worlds_copies_worlds_and_skips_excluded()
    {
        var copy = await ServerCloner.CloneAsync(_def, "Copy", includeWorlds: true, port: 25570, CancellationToken.None);
        var files = Files(copy.Directory);
        Assert.Contains("world/level.dat", files);
        Assert.Contains("world/region/r.0.0.mca", files);
        Assert.Contains("world_nether/level.dat", files);
        Assert.Contains("plugins/Foo/config.yml", files);
        Assert.Contains("libraries/lib.jar", files);
        Assert.DoesNotContain("world/session.lock", files);
        Assert.DoesNotContain(files, f => f.StartsWith("logs/", StringComparison.Ordinal) || f.StartsWith("cache/", StringComparison.Ordinal)
            || f.StartsWith("crash-reports/", StringComparison.Ordinal) || f.StartsWith(".packcheck/metrics", StringComparison.Ordinal)
            || f.StartsWith(".packcheck-backups", StringComparison.Ordinal) || f.EndsWith(".lock", StringComparison.Ordinal));
        Assert.Equal(Path.Combine(_root, "Copy"), copy.Directory);
        Assert.NotEqual(_def.Id, copy.Id);
        Assert.Equal("Copy", copy.Name);
        Assert.Equal(25570, copy.Port);
        Assert.Equal(3072, copy.MaxMemoryMb);
    }

    [Fact]
    public async Task Clone_without_worlds_leaves_worlds_out()
    {
        var copy = await ServerCloner.CloneAsync(_def, "NoWorlds", includeWorlds: false, port: 25571, CancellationToken.None);
        var files = Files(copy.Directory);
        Assert.DoesNotContain(files, f => f.StartsWith("world", StringComparison.Ordinal));
        Assert.Contains("paper.jar", files);
        Assert.Contains("plugins/Foo.jar", files);
    }

    [Fact]
    public async Task Clone_rewrites_port_and_keeps_other_lines()
    {
        var copy = await ServerCloner.CloneAsync(_def, "Port", includeWorlds: false, port: 25599, CancellationToken.None);
        var text = File.ReadAllText(Path.Combine(copy.Directory, "server.properties"));
        Assert.Contains("server-port=25599", text, StringComparison.Ordinal);
        Assert.Contains("# keep me", text, StringComparison.Ordinal);
        Assert.Contains("motd=hi", text, StringComparison.Ordinal);
        Assert.Contains("server-port=25565", File.ReadAllText(Path.Combine(_def.Directory, "server.properties")), StringComparison.Ordinal);
    }

    [Fact]
    public void SetPort_updates_query_port_when_present()
    {
        var dir = Path.Combine(_root, "q");
        Write(dir, "server.properties", "server-port=1\nquery.port=1\n");
        ServerCloner.SetPort(dir, 25600);
        var text = File.ReadAllText(Path.Combine(dir, "server.properties"));
        Assert.Contains("server-port=25600", text, StringComparison.Ordinal);
        Assert.Contains("query.port=25600", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unique_folder_names_do_not_overwrite()
    {
        var a = await ServerCloner.CloneAsync(_def, "Same", false, 25580, CancellationToken.None);
        var b = await ServerCloner.CloneAsync(_def, "Same", false, 25581, CancellationToken.None);
        Assert.Equal(Path.Combine(_root, "Same"), a.Directory);
        Assert.Equal(Path.Combine(_root, "Same (2)"), b.Directory);
        Assert.Equal("Same (2)", b.Name);
        Assert.Equal(Path.Combine(_root, "srv (2)"), ServerCloner.UniqueFolder(_root, "srv"));
        Assert.Equal(Path.Combine(_root, "ab"), ServerCloner.UniqueFolder(_root, "a<b"));
    }

    [Fact]
    public async Task Template_round_trip_without_worlds()
    {
        var store = new ServerTemplates(Path.Combine(_root, "templates"));
        var t = await store.SaveAsync(_def, "My template", CancellationToken.None);

        var listed = Assert.Single(store.List());
        Assert.Equal(t.Id, listed.Id);
        Assert.Equal("My template", listed.Name);
        Assert.Equal(ServerPlatform.Paper, listed.Platform);
        Assert.Equal("1.21.4", listed.MinecraftVersion);
        Assert.Equal(3072, listed.MaxMemoryMb);

        using (var zip = ZipFile.OpenRead(Path.Combine(store.Root, t.Id, ServerTemplates.ArchiveName)))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains(ServerTemplates.ManifestName, names);
            Assert.DoesNotContain(names, n => n.StartsWith("world", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.StartsWith("libraries/", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.StartsWith("logs/", StringComparison.Ordinal));
        }

        var parent = Path.Combine(_root, "new");
        var def = await store.CreateServerAsync(listed, parent, "FromTemplate", 25590, "java.exe", CancellationToken.None);
        var files = Files(def.Directory);
        Assert.Contains("paper.jar", files);
        Assert.Contains("plugins/Foo/config.yml", files);
        Assert.DoesNotContain(ServerTemplates.ManifestName, files);
        Assert.DoesNotContain(files, f => f.StartsWith("world", StringComparison.Ordinal));
        Assert.Contains("server-port=25590", File.ReadAllText(Path.Combine(def.Directory, "server.properties")), StringComparison.Ordinal);
        Assert.Equal("FromTemplate", def.Name);
        Assert.Equal("paper.jar", def.JarFile);
        Assert.Equal(25590, def.Port);
        Assert.Equal("java.exe", def.JavaPath);

        store.Delete(t.Id);
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Template_keeps_libraries_when_launch_needs_them()
    {
        var store = new ServerTemplates(Path.Combine(_root, "templates"));
        var t = await store.SaveAsync(_def with { ArgsFile = "libraries/win_args.txt" }, "Forge", CancellationToken.None);
        using var zip = ZipFile.OpenRead(Path.Combine(store.Root, t.Id, ServerTemplates.ArchiveName));
        Assert.Contains(zip.Entries, e => e.FullName == "libraries/lib.jar");
    }

    [Fact]
    public void Delete_rejects_path_ids()
    {
        var store = new ServerTemplates(Path.Combine(_root, "templates"));
        Assert.Throws<ArgumentException>(() => store.Delete(".."));
        Assert.Throws<ArgumentException>(() => store.Delete("a/b"));
    }
}
