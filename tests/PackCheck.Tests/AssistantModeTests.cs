using PackCheck.App.Ai;

namespace PackCheck.Tests;

public class AssistantModeTests
{
    [Theory]
    [InlineData(AssistantMode.Normal, "read_file", ToolDecision.Run)]
    [InlineData(AssistantMode.Normal, "write_file", ToolDecision.Ask)]
    [InlineData(AssistantMode.Plan, "read_file", ToolDecision.Run)]
    [InlineData(AssistantMode.Plan, "write_file", ToolDecision.Block)]
    [InlineData(AssistantMode.Plan, "send_command", ToolDecision.Block)]
    [InlineData(AssistantMode.Auto, "write_file", ToolDecision.Run)]
    [InlineData(AssistantMode.Auto, "send_command", ToolDecision.Ask)]
    [InlineData(AssistantMode.Auto, "create_servers", ToolDecision.Ask)]
    [InlineData(AssistantMode.Bypass, "stop_server", ToolDecision.Run)]
    [InlineData(AssistantMode.Bypass, "create_servers", ToolDecision.Run)]
    public void Decide_FollowsTheMode(AssistantMode mode, string tool, ToolDecision expected) =>
        Assert.Equal(expected, AssistantPolicy.Decide(mode, tool));

    [Fact]
    public void EveryChangingTool_IsKnown()
    {
        var changing = AssistantTools.Specs.Where(s => s.Description.Contains("approval", StringComparison.OrdinalIgnoreCase)).Select(s => s.Name);
        Assert.All(changing, n => Assert.True(AssistantPolicy.Changes(n), n));
    }
}
