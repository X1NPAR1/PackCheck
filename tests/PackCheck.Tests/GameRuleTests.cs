using PackCheck.App.ViewModels;
using Xunit;

namespace PackCheck.Tests;

public sealed class GameRuleTests
{
    [Theory]
    [InlineData("keepInventory is enabled", "keepInventory", true)]
    [InlineData("keepInventory is disabled", "keepInventory", false)]
    [InlineData("doDaylightCycle = true", "doDaylightCycle", true)]
    [InlineData("doMobSpawning = false", "doMobSpawning", false)]
    [InlineData("announceAdvancements is Enabled", "announceAdvancements", true)]
    public void Parses_gamerule_reply(string line, string expectedRule, bool expectedIsOn)
    {
        Assert.True(ServerItemViewModel.TryParseGameRuleReply(line, out var rule, out var isOn));
        Assert.Equal(expectedRule, rule);
        Assert.Equal(expectedIsOn, isOn);
    }

    [Theory]
    [InlineData("Done (1.20.4) For help, type \"help\"")]
    [InlineData("[Server thread/INFO]: Preparing spawn area")]
    [InlineData("")]
    [InlineData("random console line")]
    public void Returns_false_for_non_gamerule_lines(string line)
    {
        Assert.False(ServerItemViewModel.TryParseGameRuleReply(line, out var rule, out var _));
        Assert.Null(rule);
    }
}
