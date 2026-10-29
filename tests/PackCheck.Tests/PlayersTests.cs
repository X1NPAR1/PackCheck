using System.Text.Json.Nodes;
using PackCheck.Servers;
using Xunit;

namespace PackCheck.Tests;

public class PlayersTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Tracks_sessions_playtime_ip_and_uuid()
    {
        var t = new PlayerTracker();
        t.OnMessage("UUID of player Steve is 8667ba71-b85a-4004-af54-457a9734eed7", T0);
        t.OnMessage("Steve[/192.168.1.5:51234] logged in with entity id 123 at ([world]0.5, 64.0, 0.5)", T0);
        t.OnMessage("Steve joined the game", T0);
        t.OnMessage("Steve left the game", T0.AddMinutes(30));
        t.OnMessage("Steve joined the game", T0.AddHours(1));
        t.CloseAll(T0.AddHours(1).AddMinutes(15));

        var p = t.Find("steve")!;
        Assert.Equal("8667ba71-b85a-4004-af54-457a9734eed7", p.Uuid);
        Assert.Equal(["192.168.1.5"], p.Ips);
        Assert.Equal(2, p.Sessions);
        Assert.Equal(45 * 60, p.PlaySeconds);
        Assert.Null(p.OnlineSince);
        Assert.Equal(T0, p.FirstSeen);
    }

    [Fact]
    public void Same_ip_finds_alt_accounts_and_save_round_trips()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("pc-players").FullName, ".packcheck", "players.json");
        var t = new PlayerTracker(file);
        t.OnMessage("Alex[/10.0.0.7:1] logged in with entity id 1 at (0, 0, 0)", T0);
        t.OnMessage("Alex_Alt[/10.0.0.7:2] logged in with entity id 2 at (0, 0, 0)", T0);
        t.OnMessage("Bob[/10.0.0.9:3] logged in with entity id 3 at (0, 0, 0)", T0);
        Assert.Equal(["Alex_Alt"], t.SameIp("Alex"));
        Assert.Empty(t.SameIp("Bob"));

        t.Save();
        var again = new PlayerTracker(file);
        Assert.Equal(3, again.All.Count);
        Assert.Equal(["Alex"], again.SameIp("Alex_Alt"));
    }

    [Fact]
    public void Lists_are_read_and_edited()
    {
        var dir = Directory.CreateTempSubdirectory("pc-lists").FullName;
        File.WriteAllText(Path.Combine(dir, "ops.json"), """[{"uuid":"u1","name":"Steve","level":4,"bypassesPlayerLimit":false}]""");
        File.WriteAllText(Path.Combine(dir, "usercache.json"), """[{"name":"Alex","uuid":"u2","expiresOn":"2026-11-29 12:00:00 +0000"}]""");
        var lists = new PlayerLists(dir);
        Assert.Contains("Steve", lists.Ops);
        Assert.Equal("u2", lists.CachedUuid("alex"));

        lists.Set(PlayerList.Banned, "Alex", "u2", true, "griefing");
        lists.Set(PlayerList.Ops, "Steve", "u1", false);
        var reread = new PlayerLists(dir);
        Assert.Contains("Alex", reread.Banned);
        Assert.Empty(reread.Ops);
        var ban = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "banned-players.json")))!.AsArray()[0]!;
        Assert.Equal("griefing", ban["reason"]!.GetValue<string>());
        Assert.Equal("forever", ban["expires"]!.GetValue<string>());

        lists.Set(PlayerList.Banned, "Alex", "u2", false);
        Assert.Empty(new PlayerLists(dir).Banned);
    }

    [Fact]
    public void Offline_uuid_matches_the_game()
    {
        // Known value: offline UUID of "Notch".
        Assert.Equal("b50ad385-829d-3141-a216-7e7d7539ba7f", PlayerLists.OfflineUuid("Notch"));
    }

    [Theory]
    [InlineData(PlayerList.Whitelist, true, "whitelist add Steve")]
    [InlineData(PlayerList.Ops, false, "deop Steve")]
    [InlineData(PlayerList.Banned, false, "pardon Steve")]
    public void Commands(PlayerList list, bool present, string expected) => Assert.Equal(expected, PlayerLists.Command(list, "Steve", present));

    [Theory]
    [InlineData("Steve", true)]
    [InlineData("a_b_9", true)]
    [InlineData("ab", false)]
    [InlineData("bad name", false)]
    [InlineData("x;stop", false)]
    public void Name_validation(string name, bool ok) => Assert.Equal(ok, PlayerLists.IsValidName(name));
}
