using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using PackCheck.Servers;
using Xunit;

namespace PackCheck.Tests;

public sealed class UpgradeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pc-upgrade").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string Jar(string sub, string name, string entry, string content)
    {
        var path = Path.Combine(_dir, sub, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry(entry).Open());
        w.Write(content);
        return path;
    }

    private ServerDefinition Def(ServerPlatform p, string version) => new()
    {
        Id = "u", Name = "U", Directory = _dir, Platform = p, MinecraftVersion = version, JarFile = "server.jar", JavaPath = "java",
    };

    [Theory]
    [InlineData("1.20.4", "1.21.1", true)]
    [InlineData("1.21.1", "1.21.1", false)]
    [InlineData("1.21.1", "1.20.6", false)]
    public void Only_upgrades_are_allowed(string from, string to, bool ok) => Assert.Equal(ok, UpgradeAdvisor.CanUpgrade(from, to));

    [Fact]
    public void Targets_filters_older_versions() =>
        Assert.Equal(["1.21.4", "1.21.1"], UpgradeAdvisor.Targets("1.20.6", ["1.21.4", "1.21.1", "1.20.6", "1.20.1"]));

    [Fact]
    public void Downgrade_targets_are_the_older_versions() =>
        Assert.Equal(["1.20.1", "1.16.5"], UpgradeAdvisor.Targets("1.20.6", ["1.21.4", "1.20.6", "1.20.1", "1.16.5"], downgrade: true));

    [Fact]
    public void World_backup_is_the_newest_one_from_the_target_version()
    {
        var now = DateTimeOffset.Now;
        BackupInfo B(string v, int hoursAgo, BackupKind k = BackupKind.Full) => new($"{v}-{hoursAgo}.zip", now.AddHours(-hoursAgo), 1) { Version = v, Kind = k };
        var list = new[] { B("1.21.4", 1), B("1.20.1", 30), B("1.20.1", 5), B("1.20.1", 2, BackupKind.Config) };

        Assert.Equal("1.20.1-5.zip", UpgradeAdvisor.WorldBackupFor(list, "1.20.1")!.Path);
        Assert.Null(UpgradeAdvisor.WorldBackupFor(list, "1.19.4"));
    }

    [Fact]
    public void A_world_that_was_never_saved_still_counts_as_a_world()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pc-w-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "world", "players", "data"));
        File.WriteAllText(Path.Combine(dir, "world", "session.lock"), "");
        Directory.CreateDirectory(Path.Combine(dir, "plugins", "x"));

        Assert.Equal(["world"], Backups.WorldFolders(dir));
    }

    [Fact]
    public void Worlds_are_moved_aside_not_deleted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pc-down-" + Guid.NewGuid().ToString("N"));
        foreach (var w in new[] { "world", "world_nether" })
        {
            Directory.CreateDirectory(Path.Combine(dir, w));
            File.WriteAllText(Path.Combine(dir, w, "level.dat"), "x");
        }

        Directory.CreateDirectory(Path.Combine(dir, "plugins"));

        var aside = UpgradeAdvisor.MoveWorldsAside(dir, "1.21.4", new DateTime(2026, 10, 29, 20, 0, 0));

        Assert.Equal(Path.Combine(dir, "old-worlds", "1.21.4-20261029-200000"), aside);
        Assert.True(File.Exists(Path.Combine(aside!, "world", "level.dat")));
        Assert.True(File.Exists(Path.Combine(aside!, "world_nether", "level.dat")));
        Assert.False(Directory.Exists(Path.Combine(dir, "world")));
        Assert.True(Directory.Exists(Path.Combine(dir, "plugins")));
        Assert.Null(UpgradeAdvisor.MoveWorldsAside(dir, "1.21.4", DateTime.Now));
    }

    [Fact]
    public async Task Plugins_are_checked_by_api_version()
    {
        Jar("plugins", "Old.jar", "plugin.yml", "name: Old\nversion: 1\nmain: a.B\napi-version: '1.13'\n");
        Jar("plugins", "Future.jar", "plugin.yml", "name: Future\nversion: 1\nmain: a.B\napi-version: '1.22'\n");
        Jar("plugins", "NoApi.jar", "plugin.yml", "name: NoApi\nversion: 1\nmain: a.B\n");

        var r = await UpgradeAdvisor.CheckAsync(Def(ServerPlatform.Paper, "1.20.4"), "1.21.4", null, CancellationToken.None);
        Assert.Equal(AddonStatus.Incompatible, r.Addons.Single(a => a.Name == "Future").Status);
        Assert.Equal(AddonStatus.Likely, r.Addons.Single(a => a.Name == "Old").Status);
        Assert.Equal(AddonStatus.Unknown, r.Addons.Single(a => a.Name == "NoApi").Status);
        Assert.Equal(1, r.Blocking);
        Assert.Equal("Future", r.Addons[0].Name); // blocking first
        Assert.True(r.RequiredJava >= 21);
    }

    [Fact]
    public async Task Mods_are_checked_by_declared_range()
    {
        Jar("mods", "a.jar", "fabric.mod.json", """{"schemaVersion":1,"id":"a","version":"1","depends":{"minecraft":"~1.20.4"}}""");
        Jar("mods", "b.jar", "fabric.mod.json", """{"schemaVersion":1,"id":"b","version":"1","depends":{"minecraft":">=1.20"}}""");

        var r = await UpgradeAdvisor.CheckAsync(Def(ServerPlatform.Fabric, "1.20.4"), "1.21.1", null, CancellationToken.None);
        Assert.Equal(AddonStatus.Incompatible, r.Addons.Single(a => a.Name == "a").Status);
        Assert.Equal(AddonStatus.Compatible, r.Addons.Single(a => a.Name == "b").Status);
    }

    private sealed class ModrinthStub(Func<HttpRequestMessage, string> answer) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(request), Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Modrinth_update_is_attached_and_applied_with_hash_check()
    {
        var jar = Jar("mods", "a.jar", "fabric.mod.json", """{"schemaVersion":1,"id":"a","version":"1","depends":{"minecraft":"~1.20.4"}}""");
        var sha = Convert.ToHexStringLower(SHA512.HashData(File.ReadAllBytes(jar)));
        var newBytes = Encoding.UTF8.GetBytes("new jar");
        var newSha = Convert.ToHexStringLower(SHA512.HashData(newBytes));
        var json = $$$"""{"{{{sha}}}":{"version_number":"2.0","files":[{"primary":true,"url":"https://cdn.modrinth.com/data/x/a-2.0.jar","filename":"a-2.0.jar","hashes":{"sha512":"{{{newSha}}}"}}]}}""";
        var stub = new ModrinthStub(_ => json);
        var http = new HttpClient(stub);

        var r = await UpgradeAdvisor.CheckAsync(Def(ServerPlatform.Fabric, "1.20.4"), "1.21.1", http, CancellationToken.None);
        var a = Assert.Single(r.Addons);
        Assert.Equal(AddonStatus.UpdateAvailable, a.Status);
        Assert.Equal("2.0", a.UpdateVersion);
        Assert.Contains("\"game_versions\":[\"1.21.1\"]", stub.Body);
        Assert.Contains("\"loaders\":[\"fabric\"]", stub.Body);

        var download = new HttpClient(new BytesStub(newBytes));
        await UpgradeAdvisor.ApplyUpdateAsync(download, a, CancellationToken.None);
        Assert.False(File.Exists(jar));
        Assert.Equal("new jar", File.ReadAllText(Path.Combine(_dir, "mods", "a-2.0.jar")));

        // A tampered download is rejected and the old jar stays.
        var jar2 = Jar("mods", "c.jar", "x.txt", "c");
        var bad = a with { FilePath = jar2, FileName = "c.jar", UpdateFileName = "c-2.jar" };
        await Assert.ThrowsAsync<InvalidDataException>(() => UpgradeAdvisor.ApplyUpdateAsync(new HttpClient(new BytesStub([1, 2, 3])), bad, CancellationToken.None));
        Assert.True(File.Exists(jar2));
    }

    private sealed class BytesStub(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }

    [Fact]
    public async Task Modrinth_url_outside_cdn_is_ignored()
    {
        var jar = Jar("mods", "a.jar", "fabric.mod.json", """{"schemaVersion":1,"id":"a","version":"1","depends":{"minecraft":"~1.20.4"}}""");
        var sha = Convert.ToHexStringLower(SHA512.HashData(File.ReadAllBytes(jar)));
        var json = $$$"""{"{{{sha}}}":{"version_number":"2.0","files":[{"primary":true,"url":"https://evil.example/a.jar","filename":"a.jar","hashes":{"sha512":"00"}}]}}""";
        var r = await UpgradeAdvisor.CheckAsync(Def(ServerPlatform.Fabric, "1.20.4"), "1.21.1", new HttpClient(new ModrinthStub(_ => json)), CancellationToken.None);
        Assert.Equal(AddonStatus.Incompatible, Assert.Single(r.Addons).Status);
    }
}
