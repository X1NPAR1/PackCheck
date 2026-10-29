using PackCheck.Servers;

namespace PackCheck.Tests;

public class AutomationTests
{
    private static readonly DateTimeOffset Day = new(2026, 10, 29, 0, 0, 0, TimeSpan.FromHours(3));

    private static AutomationSnapshot Running(double? tps = 20, double? mem = 50, int players = 1) => new(ServerState.Running, tps, mem, players);

    private static AutomationEngine Engine(params AutomationRule[] rules) => new() { Rules = rules };

    [Fact]
    public void StartWithApp_StartsAStoppedServerOnce()
    {
        var e = Engine(new AutomationRule { Kind = AutomationKind.StartWithApp });
        Assert.Equal(AutomationActionKind.Start, Assert.Single(e.OnAppStarted(new(ServerState.Stopped, null, null, 0))).Kind);
        Assert.Empty(e.OnAppStarted(Running()));
        Assert.Empty(Engine(new AutomationRule { Kind = AutomationKind.StartWithApp, Enabled = false }).OnAppStarted(new(ServerState.Stopped, null, null, 0)));
    }

    [Fact]
    public void RestartOnCrash_StopsAfterTheLimit()
    {
        var e = Engine(new AutomationRule { Kind = AutomationKind.RestartOnCrash, MaxRetries = 2 });
        var t = Day.AddHours(10);
        var kinds = new List<AutomationActionKind>();
        for (var i = 0; i < 3; i++)
        {
            e.Tick(t, Running());
            kinds.AddRange(e.Tick(t.AddSeconds(5), new(ServerState.Crashed, null, null, 0)).Select(a => a.Kind));
            t = t.AddMinutes(5);
        }

        Assert.Equal([AutomationActionKind.Start, AutomationActionKind.Start, AutomationActionKind.Notify], kinds);
    }

    [Fact]
    public void RestartOnCrash_DoesNotFightAUserStop()
    {
        var e = Engine(new AutomationRule { Kind = AutomationKind.RestartOnCrash });
        e.Tick(Day, Running());
        e.UserStopped();
        Assert.Empty(e.Tick(Day.AddSeconds(5), new(ServerState.Crashed, null, null, 0)));
    }

    [Fact]
    public void ScheduledRestart_FiresOncePerDayAtTheTime_WithWarning()
    {
        var e = Engine(AutomationRule.Default(AutomationKind.ScheduledRestart) with { At = "04:00", WarningSeconds = 60 });
        Assert.Empty(e.Tick(Day.AddHours(3).AddMinutes(59), Running()));
        var a = Assert.Single(e.Tick(Day.AddHours(4).AddSeconds(10), Running()));
        Assert.Equal((AutomationActionKind.Restart, 60), (a.Kind, a.WarningSeconds));
        Assert.Empty(e.Tick(Day.AddHours(4).AddMinutes(1), Running()));
        Assert.Single(e.Tick(Day.AddDays(1).AddHours(4).AddMinutes(2), Running()));
    }

    [Fact]
    public void Schedule_RespectsTheChosenDays()
    {
        // 29 Oct 2026 is a Thursday.
        var e = Engine(new AutomationRule { Kind = AutomationKind.ScheduledBackup, At = "03:00", Days = [DayOfWeek.Sunday] });
        Assert.Empty(e.Tick(Day.AddHours(3).AddMinutes(1), Running()));
        Assert.Single(e.Tick(Day.AddDays(3).AddHours(3).AddMinutes(1), Running()));
    }

    [Fact]
    public void Schedule_IsSkippedWhenThePcWasAsleepLongerThanTenMinutes()
    {
        var e = Engine(new AutomationRule { Kind = AutomationKind.ScheduledBackup, At = "03:00" });
        Assert.Empty(e.Tick(Day.AddHours(3).AddMinutes(25), Running()));
    }

    [Fact]
    public void IntervalCommand_WaitsOneIntervalThenRepeats()
    {
        var e = Engine(new AutomationRule { Kind = AutomationKind.ScheduledCommand, IntervalMinutes = 30, Command = "/save-all" });
        Assert.Empty(e.Tick(Day, Running()));
        Assert.Empty(e.Tick(Day.AddMinutes(29), Running()));
        var a = Assert.Single(e.Tick(Day.AddMinutes(30), Running()));
        Assert.Equal("save-all", a.Text);
        Assert.Single(e.Tick(Day.AddMinutes(60), Running()));
    }

    [Fact]
    public void Commands_AreNotSentToAStoppedServer()
    {
        var e = Engine(new AutomationRule { Kind = AutomationKind.Announcement, IntervalMinutes = 5, Message = "hi" });
        e.Tick(Day, Running());
        Assert.Empty(e.Tick(Day.AddMinutes(10), new(ServerState.Stopped, null, null, 0)));
    }

    [Fact]
    public void LowTps_FiresAfterTheDuration_OnlyOnce_AndRearms()
    {
        var e = Engine(new AutomationRule { Kind = AutomationKind.LowTps, Threshold = 15, ForMinutes = 5, Reaction = AutomationReaction.Restart });
        Assert.Empty(e.Tick(Day, Running(tps: 12)));
        Assert.Empty(e.Tick(Day.AddMinutes(4), Running(tps: 12)));
        Assert.Equal(AutomationActionKind.Restart, Assert.Single(e.Tick(Day.AddMinutes(5), Running(tps: 12))).Kind);
        Assert.Empty(e.Tick(Day.AddMinutes(20), Running(tps: 12)));
        Assert.Empty(e.Tick(Day.AddMinutes(21), Running(tps: 20)));
        e.Tick(Day.AddMinutes(22), Running(tps: 10));
        Assert.Single(e.Tick(Day.AddMinutes(28), Running(tps: 10)));
    }

    [Fact]
    public void ShortDips_DoNotTrigger()
    {
        var e = Engine(new AutomationRule { Kind = AutomationKind.HighMemory, Threshold = 90, ForMinutes = 10 });
        e.Tick(Day, Running(mem: 95));
        e.Tick(Day.AddMinutes(5), Running(mem: 60));
        Assert.Empty(e.Tick(Day.AddMinutes(11), Running(mem: 95)));
    }

    [Fact]
    public void StopWhenEmpty_CountsFromTheLastPlayer()
    {
        var e = Engine(new AutomationRule { Kind = AutomationKind.StopWhenEmpty, ForMinutes = 30 });
        e.Tick(Day, Running(players: 2));
        e.Tick(Day.AddMinutes(10), Running(players: 0));
        Assert.Empty(e.Tick(Day.AddMinutes(30), Running(players: 0)));
        Assert.Equal(AutomationActionKind.Stop, Assert.Single(e.Tick(Day.AddMinutes(40), Running(players: 0))).Kind);
    }

    [Fact]
    public void NextRun_ShowsTheNextScheduledTime()
    {
        var e = new AutomationEngine();
        var r = new AutomationRule { Kind = AutomationKind.ScheduledRestart, At = "04:00" };
        Assert.Equal(Day.AddHours(4), e.NextRun(r, Day.AddHours(1)));
        Assert.Equal(Day.AddDays(1).AddHours(4), e.NextRun(r, Day.AddHours(5)));
        Assert.Null(e.NextRun(new AutomationRule { Kind = AutomationKind.LowTps }, Day));
    }

    [Fact]
    public void Store_RoundTrips()
    {
        using var pack = new PackBuilder();
        var def = new ServerDefinition { Id = "x", Name = "x", Directory = pack.Root, Platform = ServerPlatform.Paper, MinecraftVersion = "1.21.1", JarFile = "p.jar", JavaPath = "java" };
        var rules = new[] { AutomationRule.Default(AutomationKind.ScheduledRestart) with { Days = [DayOfWeek.Monday] }, AutomationRule.Default(AutomationKind.LowTps) };
        AutomationStore.Save(def, new(rules, [new(Day, rules[0].Id, "Restart", "ok", true)]));
        var back = AutomationStore.Load(def);
        Assert.Equal(rules.Select(r => (r.Kind, r.At, r.Days.Count)), back.Rules.Select(r => (r.Kind, r.At, r.Days.Count)));
        Assert.Single(back.Events);
    }
}
