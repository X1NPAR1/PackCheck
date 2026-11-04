using PackCheck.Core;
using PackCheck.Servers;
using Xunit;

namespace PackCheck.Tests;

public class AnalyticsTests
{
    private static readonly DateTime Day = new(2026, 11, 4);

    [Theory]
    [InlineData("[10:00:01] [Server thread/INFO]: <Steve> hello all", ActivityKind.Chat, "Steve")]
    [InlineData("[10:00:02] [Server thread/INFO]: Alex joined the game", ActivityKind.Join, "Alex")]
    [InlineData("[10:00:03] [Server thread/INFO]: Alex left the game", ActivityKind.Leave, "Alex")]
    [InlineData("[10:00:04] [Server thread/INFO]: Steve was slain by Zombie", ActivityKind.Death, "Steve")]
    [InlineData("[10:00:05] [Server thread/INFO]: Steve has made the advancement [Stone Age]", ActivityKind.Advancement, "Steve")]
    [InlineData("[10:00:06] [Server thread/INFO]: Steve issued server command: /home", ActivityKind.Command, "Steve")]
    [InlineData("[10:00:07] [Server thread/INFO]: Done (3.2s)! For help, type \"help\"", ActivityKind.Server, null)]
    [InlineData("[10:00:08] [Async Chat Thread - #0/INFO]: [Not Secure] <Bob> hi", ActivityKind.Chat, "Bob")]
    public void LogLines_AreClassified(string line, ActivityKind kind, string? player)
    {
        var e = ActivityLog.Classify(line, Day);

        Assert.NotNull(e);
        Assert.Equal((kind, player), (e!.Kind, e.Player));
    }

    [Fact]
    public void NoiseIsIgnored() =>
        Assert.Null(ActivityLog.Classify("[10:00:00] [Server thread/WARN]: Can't keep up!", Day));

    [Fact]
    public void Timeline_ReadsLatestLog_NewestFirst()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pc-act-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "logs", "latest.log"), "[09:00:00] [Server thread/INFO]: Alex joined the game\n[09:05:00] [Server thread/INFO]: <Alex> hi\n");

        var events = ActivityLog.Read(dir);

        Assert.Equal([ActivityKind.Chat, ActivityKind.Join], events.Select(e => e.Kind));
    }

    [Fact]
    public void Storage_FindsCleanableFolders_AndCleanKeepsLatestLog()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pc-sto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        Directory.CreateDirectory(Path.Combine(dir, "world"));
        File.WriteAllBytes(Path.Combine(dir, "logs", "2026-11-01-1.log.gz"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(dir, "logs", "latest.log"), new byte[10]);
        File.WriteAllBytes(Path.Combine(dir, "world", "level.dat"), new byte[5000]);

        var r = StorageAnalyzer.Analyze(dir);

        Assert.Equal(6010, r.Total);
        Assert.Equal(1000, r.Cleanable);
        Assert.Equal("world", r.Folders[0].Path);
        Assert.Equal(1000, StorageAnalyzer.Clean(dir));
        Assert.True(File.Exists(Path.Combine(dir, "logs", "latest.log")));
    }

    [Fact]
    public void Stats_AreRead()
    {
        const string json = """
            {"stats":{"minecraft:custom":{"minecraft:play_time":144000,"minecraft:deaths":3,"minecraft:mob_kills":40,"minecraft:walk_one_cm":250000,"minecraft:sprint_one_cm":250000,"minecraft:jump":99},
            "minecraft:mined":{"minecraft:diamond_ore":2,"minecraft:deepslate_diamond_ore":5}},"DataVersion":4189}
            """;

        var s = Scoreboard.Parse("u", "Steve", json);

        Assert.Equal(2, s.HoursPlayed, 3);
        Assert.Equal((3, 40, 7, 99), (s.Deaths, s.MobKills, s.DiamondsMined, s.Jumps));
        Assert.Equal(5, s.KmWalked, 3);
    }

    [Fact]
    public void Solver_PicksNewestCommonVersion()
    {
        IReadOnlyCollection<(string, string)> a = [("fabric", "1.21.1"), ("fabric", "1.21.4"), ("neoforge", "1.21.1"), ("fabric", "25w10a")];
        IReadOnlyCollection<(string, string)> b = [("fabric", "1.21.1"), ("fabric", "1.20.1"), ("neoforge", "1.21.1")];

        var s = ModSolver.Solve([a, b]);

        Assert.Equal(new ModSolver.Solution("fabric", "1.21.1"), s);
        Assert.Equal(ServerPlatform.Fabric, ModSolver.Platform(s!.Loader));
    }

    [Fact]
    public void Solver_ReturnsNullWithoutCommonVersion()
    {
        IReadOnlyCollection<(string, string)> a = [("fabric", "1.21.4")];
        IReadOnlyCollection<(string, string)> b = [("forge", "1.20.1")];

        Assert.Null(ModSolver.Solve([a, b]));
    }
}
