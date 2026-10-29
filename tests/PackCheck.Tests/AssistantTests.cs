using System.Net;
using System.Text.Json.Nodes;
using PackCheck.App.Ai;
using PackCheck.App.Infrastructure;
using PackCheck.App.ViewModels;
using PackCheck.Servers;

namespace PackCheck.Tests;

public class AssistantTests
{
    internal static (AssistantTools Tools, ServerItemViewModel Server, PackBuilder Pack) Setup()
    {
        var pack = new PackBuilder().Text("server.properties", "motd=Old\n").Text("plugins/Shop/messages.yml", "greeting: '&aHello %player%'\n");
        var def = new ServerDefinition
        {
            Id = "t",
            Name = "Test",
            Directory = pack.Root,
            Platform = ServerPlatform.Paper,
            MinecraftVersion = "1.21.1",
            JarFile = "paper.jar",
            JavaPath = "java.exe",
        };
        var server = new ServerItemViewModel(def, new ServerRegistry(Path.Combine(pack.Root, "servers.json")), new AppSettings());
        var list = new ServersViewModel(new ServerRegistry(Path.Combine(pack.Root, "servers.json")), new AppSettings(), null!, null!, null!, new App.AppState());
        return (new AssistantTools(list, () => server), server, pack);
    }

    internal static AiToolCall Call(string name, string json) => new("id-1", name, (JsonNode.Parse(json) as JsonObject)!);

    [Fact]
    public async Task ReadFile_ReturnsContent()
    {
        var (tools, _, pack) = Setup();
        using (pack)
        {
            var (result, pending) = await tools.RunAsync(Call("read_file", """{"path":"plugins/Shop/messages.yml"}"""));
            Assert.Null(pending);
            Assert.Contains("%player%", result, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("../../Windows/win.ini")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("plugins/../../outside.txt")]
    public async Task PathsOutsideTheServer_AreRejected(string path)
    {
        var (tools, _, pack) = Setup();
        using (pack)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.RunAsync(Call("read_file", $$"""{"path":"{{path}}"}""")));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.RunAsync(Call("write_file", $$"""{"path":"{{path}}","content":"x"}""")));
        }
    }

    [Fact]
    public async Task WriteFile_NeedsApproval_AndKeepsABackup()
    {
        var (tools, _, pack) = Setup();
        using (pack)
        {
            var (result, pending) = await tools.RunAsync(Call("write_file", """{"path":"server.properties","content":"motd=New\n"}"""));
            Assert.Null(result);
            Assert.NotNull(pending);
            Assert.Contains("- motd=Old", pending.Preview, StringComparison.Ordinal);
            Assert.Contains("+ motd=New", pending.Preview, StringComparison.Ordinal);

            // Nothing is written before approval.
            Assert.Equal("motd=Old\n", await File.ReadAllTextAsync(Path.Combine(pack.Root, "server.properties")));
            await pending.Execute();
            Assert.Equal("motd=New\n", await File.ReadAllTextAsync(Path.Combine(pack.Root, "server.properties")));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(pack.Root, ".packcheck", "edits"), "*", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public async Task CommandsAndServerChanges_AlwaysNeedApproval()
    {
        var (tools, _, pack) = Setup();
        using (pack)
        {
            foreach (var call in new[] { Call("send_command", """{"command":"op Steve"}"""), Call("start_server", "{}"), Call("stop_server", "{}"),
                Call("create_servers", """{"servers":[{"name":"lobby","platform":"Paper","version":"latest"}]}""") })
            {
                var (result, pending) = await tools.RunAsync(call);
                Assert.Null(result);
                Assert.NotNull(pending);
            }
        }
    }

    [Fact]
    public void ToolSchemas_AreValidJsonSchemaObjects()
    {
        Assert.All(AssistantTools.Specs, t =>
        {
            Assert.Equal("object", t.Schema["type"]!.GetValue<string>());
            Assert.NotNull(t.Schema["properties"]);
        });
        Assert.Contains(AssistantTools.Specs, t => t.Name == "create_servers");
    }

    /// <summary>OpenAI-compatible loop: tool call -> our result is sent back -> final answer.</summary>
    [Fact]
    public async Task OpenAiCompatible_ToolLoop()
    {
        var turn = 0;
        string? secondRequest = null;
        var handler = new StubHandler(req =>
        {
            turn++;
            if (turn == 2)
            {
                secondRequest = req.Content!.ReadAsStringAsync().Result;
            }

            return StubHandler.Json(turn == 1
                ? """{"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"list_servers","arguments":"{}"}}]}}],"usage":{"prompt_tokens":10,"completion_tokens":5}}"""
                : """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"You have one server."}}],"usage":{"prompt_tokens":20,"completion_tokens":6}}""");
        });
        using var http = new HttpClient(handler);
        var convo = AiProviders.Start(AiProviderKind.LmStudio, null, "local-model", "system", AssistantTools.Specs, http);

        var step = await convo.SendAsync("how many servers?", [], CancellationToken.None);
        var call = Assert.Single(step.ToolCalls);
        Assert.Equal("list_servers", call.Name);

        var final = await convo.ContinueAsync([(call, "- Test: Paper", false)], CancellationToken.None);
        Assert.Equal("You have one server.", final.Text);
        Assert.Contains("\"tool_call_id\":\"c1\"", secondRequest, StringComparison.Ordinal);
        Assert.Contains("- Test: Paper", secondRequest, StringComparison.Ordinal);
        Assert.Equal("http://localhost:1234/v1/chat/completions", handler.Requests[0].RequestUri!.ToString());
    }

    /// <summary>Server-sent chunks: text arrives piece by piece, tool call arguments are split across chunks.</summary>
    [Fact]
    public async Task OpenAiCompatible_StreamIsRebuilt()
    {
        var sse = string.Join("\n",
            """data: {"choices":[{"delta":{"role":"assistant","content":"Hel"}}]}""",
            """data: {"choices":[{"delta":{"content":"lo"}}]}""",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c9","function":{"name":"read_file","arguments":"{\"pa"}}]}}]}""",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"a.yml\"}"}}]},"finish_reason":"tool_calls"}]}""",
            """data: {"choices":[],"usage":{"prompt_tokens":7,"completion_tokens":3}}""",
            "data: [DONE]", "");
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream"),
        }));
        var convo = AiProviders.Start(AiProviderKind.Grok, "k", "grok-4", "s", AssistantTools.Specs, http);
        var pieces = new List<string>();
        convo.TextDelta = pieces.Add;

        var step = await convo.SendAsync("hi", [], CancellationToken.None);

        Assert.Equal(["Hel", "lo"], pieces);
        Assert.Equal("Hello", step.Text);
        var call = Assert.Single(step.ToolCalls);
        Assert.Equal(("c9", "read_file", "a.yml"), (call.Id, call.Name, call.Arguments["path"]!.GetValue<string>()));
        Assert.Equal(10, step.InputTokens + step.OutputTokens);
        Assert.Equal("https://api.x.ai/v1/chat/completions", ((StubHandler)GetHandler(http)).Requests[0].RequestUri!.ToString());
    }

    /// <summary>Gemini sends a thought signature with each tool call; it must come back unchanged in the next request.</summary>
    [Fact]
    public async Task Gemini_ThoughtSignature_IsSentBack()
    {
        var turn = 0;
        string? second = null;
        var sse = string.Join("\n",
            """data: {"choices":[{"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"g1","type":"function","function":{"name":"list_servers","arguments":"{}"},"extra_content":{"google":{"thought_signature":"SIG123"}}}]},"finish_reason":"tool_calls"}]}""",
            "data: [DONE]", "");
        var handler = new StubHandler(req =>
        {
            turn++;
            if (turn == 2)
            {
                second = req.Content!.ReadAsStringAsync().Result;
                return StubHandler.Json("""{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"ok"}}]}""");
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream") };
        });
        using var http = new HttpClient(handler);
        var convo = AiProviders.Start(AiProviderKind.Gemini, "k", "gemini-2.5-pro", "s", AssistantTools.Specs, http);

        var step = await convo.SendAsync("hi", [], CancellationToken.None);
        await convo.ContinueAsync([(Assert.Single(step.ToolCalls), "- Test", false)], CancellationToken.None);

        Assert.Contains("\"thought_signature\":\"SIG123\"", second, StringComparison.Ordinal);
    }

    private static HttpMessageHandler GetHandler(HttpClient c) =>
        (HttpMessageHandler)typeof(HttpMessageInvoker).GetField("_handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(c)!;

    [Fact]
    public async Task ProviderError_IsReported()
    {
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Json("""{"error":"bad key"}""", HttpStatusCode.Unauthorized)));
        var convo = AiProviders.Start(AiProviderKind.OpenAI, "k", "m", "s", AssistantTools.Specs, http);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => convo.SendAsync("hi", [], CancellationToken.None));
        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
    }
}
