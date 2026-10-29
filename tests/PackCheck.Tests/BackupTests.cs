using System.IO.Compression;
using PackCheck.Servers;
using Xunit;

namespace PackCheck.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly string _root;
    private readonly ServerDefinition _def;

    public BackupTests()
    {
        _root = Directory.CreateTempSubdirectory("pc-backup").FullName;
        var dir = Path.Combine(_root, "srv");
        Write(dir, "server.properties", "motd=hi\n");
        Write(dir, "paper.jar", "jar");
        Write(dir, "world/level.dat", "w1");
        Write(dir, "world/region/r.0.0.mca", "region");
        Write(dir, "world/session.lock", "lock");
        Write(dir, "world_nether/level.dat", "n1");
        Write(dir, "plugins/Foo.jar", "foo");
        Write(dir, "plugins/Foo/config.yml", "a: 1\n");
        Write(dir, "config/paper-global.yml", "x: 1\n");
        Write(dir, "logs/latest.log", "log");
        Write(dir, "libraries/lib.json", "{}");
        _def = new ServerDefinition
        {
            Id = "t", Name = "Test", Directory = dir, Platform = ServerPlatform.Paper, MinecraftVersion = "1.21.4", JarFile = "paper.jar", JavaPath = "java",
        };
    }

    public void Dispose() => Directory.Delete(_root, true);

    private static void Write(string dir, string rel, string text)
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    private static HashSet<string> Entries(string zip)
    {
        using var z = ZipFile.OpenRead(zip);
        return [.. z.Entries.Select(e => e.FullName)];
    }

    [Fact]
    public async Task Full_backup_skips_logs_and_locks_and_writes_manifest()
    {
        var b = await Backups.CreateAsync(_def, BackupKind.Full, "before update", new RetentionPolicy(5), null, CancellationToken.None);
        var e = Entries(b.Path);
        Assert.Contains("world/level.dat", e);
        Assert.Contains("plugins/Foo.jar", e);
        Assert.Contains(Backups.ManifestName, e);
        Assert.DoesNotContain("logs/latest.log", e);
        Assert.DoesNotContain("world/session.lock", e);

        var listed = Assert.Single(Backups.List(_def));
        Assert.Equal(BackupKind.Full, listed.Kind);
        Assert.Equal("before update", listed.Note);
    }

    [Fact]
    public async Task Worlds_and_config_backups_select_the_right_files()
    {
        var w = Entries((await Backups.CreateAsync(_def, BackupKind.Worlds, null, new RetentionPolicy(5), null, CancellationToken.None)).Path);
        Assert.Contains("world/region/r.0.0.mca", w);
        Assert.Contains("world_nether/level.dat", w);
        Assert.DoesNotContain("server.properties", w);
        Assert.DoesNotContain("plugins/Foo.jar", w);

        await Task.Delay(1100);
        var c = Entries((await Backups.CreateAsync(_def, BackupKind.Config, null, new RetentionPolicy(5), null, CancellationToken.None)).Path);
        Assert.Contains("server.properties", c);
        Assert.Contains("plugins/Foo/config.yml", c);
        Assert.Contains("config/paper-global.yml", c);
        Assert.DoesNotContain("plugins/Foo.jar", c);
        Assert.DoesNotContain("world/level.dat", c);
        Assert.DoesNotContain("libraries/lib.json", c);
    }

    [Fact]
    public async Task Locked_file_is_skipped_not_fatal()
    {
        var path = Path.Combine(_def.Directory, "world", "region", "r.0.0.mca");
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var b = await Backups.CreateAsync(_def, BackupKind.Full, null, new RetentionPolicy(5), null, CancellationToken.None);
            Assert.Equal(1, b.Skipped);
            Assert.DoesNotContain("world/region/r.0.0.mca", Entries(b.Path));
        }
    }

    [Fact]
    public async Task Selective_restore_replaces_only_the_chosen_world()
    {
        var b = await Backups.CreateAsync(_def, BackupKind.Full, null, new RetentionPolicy(5), null, CancellationToken.None);
        File.WriteAllText(Path.Combine(_def.Directory, "world", "level.dat"), "broken");
        File.WriteAllText(Path.Combine(_def.Directory, "world", "new.dat"), "new");
        File.WriteAllText(Path.Combine(_def.Directory, "server.properties"), "motd=changed\n");

        var contents = Backups.Contents(b.Path);
        Assert.Contains(contents, c => c.Name == "world/" && c.IsWorld);

        await Backups.RestoreAsync(_def, b, ["world/"], CancellationToken.None);
        Assert.Equal("w1", File.ReadAllText(Path.Combine(_def.Directory, "world", "level.dat")));
        Assert.False(File.Exists(Path.Combine(_def.Directory, "world", "new.dat")));
        Assert.Equal("motd=changed\n", File.ReadAllText(Path.Combine(_def.Directory, "server.properties")));
        Assert.True(File.Exists(Path.Combine(_def.Directory, "logs", "latest.log")));
        Assert.False(Directory.Exists(Path.Combine(_def.Directory, ".restore-staging")));
    }

    [Fact]
    public async Task Mirror_copy_and_pin()
    {
        var mirror = Path.Combine(_root, "mirror");
        var b = await Backups.CreateAsync(_def, BackupKind.Worlds, null, new RetentionPolicy(5), mirror, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(mirror, "srv", Path.GetFileName(b.Path))));

        var pinned = Backups.SetPinned(b, true);
        Assert.True(pinned.Pinned);
        Assert.EndsWith("-pinned.zip", pinned.Path);
        Assert.True(Assert.Single(Backups.List(_def)).Pinned);
        Assert.False(Backups.SetPinned(pinned, false).Pinned);
    }

    [Fact]
    public void Retention_keeps_last_daily_weekly_and_pinned()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        // two backups per day for 30 days
        var items = Enumerable.Range(0, 60).Select(i => (Time: now.AddHours(-12 * i), Pinned: i == 59)).ToList();
        var keep = new RetentionPolicy(KeepLast: 3, KeepDaily: 7, KeepWeekly: 4).Keep(items, x => x.Time, x => x.Pinned);

        Assert.Contains(items[0], keep);
        Assert.Contains(items[2], keep);
        Assert.Contains(items[59], keep); // pinned, 30 days old
        Assert.Equal(7, keep.Where(k => !k.Pinned).Select(k => k.Time.LocalDateTime.Date).Distinct().Count(d => d > now.LocalDateTime.Date.AddDays(-7)));
        Assert.True(keep.Count < 15);
    }
}
