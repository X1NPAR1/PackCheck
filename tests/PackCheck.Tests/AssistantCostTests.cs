using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Messages;
using PackCheck.App.Ai;

namespace PackCheck.Tests;

public class AssistantCostTests
{
    private static ToolResultEntry Entry(int i, int length) =>
        new($"call_{i}", "read_file", $"logs/{i}.log", new string('x', length), false);

    [Fact]
    public void Trim_LeavesShortHistoryAlone()
    {
        var entries = Enumerable.Range(0, 6).Select(i => Entry(i, 5000)).ToList();
        Assert.Equal(entries, HistoryTrimmer.Trim(entries));
    }

    [Fact]
    public void Trim_StubsOnlyOldLongResults()
    {
        var entries = Enumerable.Range(0, 10).Select(i => Entry(i, i == 1 ? 2000 : 2001)).ToList();
        var trimmed = HistoryTrimmer.Trim(entries);

        Assert.Equal(10, trimmed.Count);
        Assert.Equal(entries.Select(e => e.Id), trimmed.Select(e => e.Id));
        Assert.Equal("[earlier result of read_file logs/0.log trimmed; call the tool again if needed]", trimmed[0].Content);
        Assert.Same(entries[1], trimmed[1]); // exactly at the limit: kept
        Assert.StartsWith("[earlier result", trimmed[2].Content);
        Assert.StartsWith("[earlier result", trimmed[3].Content);
        Assert.All(trimmed.Skip(4), e => Assert.Equal(2001, e.Content.Length)); // the last six stay whole
        Assert.Equal(3, trimmed.Count(e => e.Content.StartsWith("[earlier", StringComparison.Ordinal)));
    }

    [Fact]
    public void Trim_IsStableOnSecondPass()
    {
        var once = HistoryTrimmer.Trim(Enumerable.Range(0, 8).Select(i => Entry(i, 3000)).ToList());
        var twice = HistoryTrimmer.Trim(once);
        Assert.True(once.Zip(twice).All(p => ReferenceEquals(p.First, p.Second)));
    }

    [Fact]
    public void ArgSummary_UsesFirstTextArgument()
    {
        Assert.Equal("world/level.dat", HistoryTrimmer.ArgSummary(new JsonObject { ["lines"] = 5, ["path"] = "world/level.dat" }));
        Assert.Equal(string.Empty, HistoryTrimmer.ArgSummary([]));
        Assert.Equal(81, HistoryTrimmer.ArgSummary(new JsonObject { ["q"] = new string('a', 200) }).Length);
        Assert.Equal("[earlier result of list_servers trimmed; call the tool again if needed]", HistoryTrimmer.Stub("list_servers", string.Empty));
    }

    [Fact]
    public void AnthropicToolResultMessage_KeepsIdsAndOrder()
    {
        var trimmed = HistoryTrimmer.Trim(Enumerable.Range(0, 8).Select(i => Entry(i, 3000)).ToList());
        var json = JsonNode.Parse(JsonSerializer.Serialize(AnthropicRequests.ToolResultMessage(trimmed)))!;

        Assert.Equal("user", json["role"]!.GetValue<string>());
        var blocks = json["content"]!.AsArray();
        Assert.Equal(Enumerable.Range(0, 8).Select(i => $"call_{i}"), blocks.Select(b => b!["tool_use_id"]!.GetValue<string>()));
        Assert.All(blocks, b => Assert.Equal("tool_result", b!["type"]!.GetValue<string>()));
        Assert.StartsWith("[earlier result", blocks[0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnthropicRequest_EnablesAutomaticCaching()
    {
        var p = AnthropicRequests.Build("claude-sonnet-5-5", "system", [], [new UserMessageParam { Content = "hi" }]);
        Assert.NotNull(p.CacheControl);

        // The SDK writes the request through a stub handler, so the real JSON body is checked without any network call.
        var handler = new CaptureHandler();
        var client = new AnthropicClient { ApiKey = "test", MaxRetries = 0, HttpClient = new HttpClient(handler) };
        await Assert.ThrowsAnyAsync<Exception>(() => client.Messages.Create(p));

        var body = JsonNode.Parse(handler.Body!)!;
        Assert.Equal("ephemeral", body["cache_control"]!["type"]!.GetValue<string>());
        Assert.Equal("system", body["system"]!.GetValue<string>());
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"type":"error","error":{"type":"invalid_request_error","message":"stub"}}""", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
