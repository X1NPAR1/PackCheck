using System.Net;
using System.Text.Json.Nodes;
using PackCheck.App.Ai;
using static PackCheck.Tests.AssistantTests;

namespace PackCheck.Tests;

/// <summary>Agent tools (find, search, edit, todo), chat history storage and attachments.</summary>
public class AssistantAgentTests
{
    [Theory]
    [InlineData("plugins/**/*.yml", "plugins/Shop/messages.yml", true)]
    [InlineData("plugins/**/*.yml", "plugins/Shop/lang/tr.yml", true)]
    [InlineData("**/messages*.yml", "plugins/Shop/messages_en.yml", true)]
    [InlineData("**/messages*.yml", "messages.yml", true)]
    [InlineData("plugins/*/config.yml", "plugins/Shop/sub/config.yml", false)]
    [InlineData("*.properties", "server.properties", true)]
    [InlineData("plugins/*.jar", "plugins/Shop/x.jar", false)]
    public void Glob_MatchesLikeShells(string pattern, string path, bool expected) =>
        Assert.Equal(expected, AssistantTools.GlobRegex(pattern).IsMatch(path));

    [Fact]
    public async Task FindFiles_ListsEveryMatchingFile()
    {
        var (tools, _, pack) = Setup();
        pack.Text("plugins/Other/lang/en.yml", "a: b\n").Text("plugins/Other/config.yml", "x: 1\n");

        var (result, _) = await tools.RunAsync(Call("find_files", """{"pattern":"plugins/**/*.yml"}"""));

        Assert.Contains("plugins/Shop/messages.yml", result);
        Assert.Contains("plugins/Other/lang/en.yml", result);
        Assert.Contains("(3 files)", result);
    }

    [Fact]
    public async Task SearchFiles_ReturnsFileAndLine()
    {
        var (tools, _, _) = Setup();

        var (result, _) = await tools.RunAsync(Call("search_files", """{"text":"hello"}"""));

        Assert.Contains("plugins/Shop/messages.yml:1:", result);
    }

    [Fact]
    public async Task EditFile_ReplacesUniqueText_AndKeepsTheRest()
    {
        var (tools, _, pack) = Setup();
        pack.Text("plugins/Shop/messages.yml", "greeting: '&aHello %player%'\r\nbye: '&cBye'\r\n");

        var (_, pending) = await tools.RunAsync(Call("edit_file", """{"path":"plugins/Shop/messages.yml","old_text":"greeting: '&aHello %player%'","new_text":"greeting: '&aMerhaba %player%'"}"""));
        await pending!.Execute();

        Assert.Equal("greeting: '&aMerhaba %player%'\r\nbye: '&cBye'\r\n", File.ReadAllText(Path.Combine(pack.Root, "plugins/Shop/messages.yml")));
    }

    [Fact]
    public async Task EditFile_RefusesMissingOrAmbiguousText()
    {
        var (tools, _, pack) = Setup();
        pack.Text("a.yml", "x: 1\nx: 1\n");

        var (missing, p1) = await tools.RunAsync(Call("edit_file", """{"path":"a.yml","old_text":"nope","new_text":"y"}"""));
        var (twice, p2) = await tools.RunAsync(Call("edit_file", """{"path":"a.yml","old_text":"x: 1","new_text":"y"}"""));

        Assert.Null(p1);
        Assert.Contains("not found", missing);
        Assert.Null(p2);
        Assert.Contains("2 times", twice);
    }

    [Fact]
    public void ReadPart_SplitsLargeFiles()
    {
        var lines = Enumerable.Range(1, 2000).Select(i => "line " + i).ToArray();

        var first = AssistantTools.ReadPart(lines, 1);
        var second = AssistantTools.ReadPart(lines, 801);

        Assert.StartsWith("line 1\n", first);
        Assert.Contains("start_line 801", first);
        Assert.StartsWith("line 801\n", second);
    }

    [Fact]
    public async Task UpdateTodo_RaisesTheList()
    {
        var (tools, _, _) = Setup();
        IReadOnlyList<TodoItem>? seen = null;
        tools.TodoChanged += items => seen = items;

        var (result, _) = await tools.RunAsync(Call("update_todo", """{"items":[{"text":"Essentials","status":"done"},{"text":"Shop","status":"in_progress"},{"text":"Jobs","status":"pending"}]}"""));

        Assert.Equal(3, seen!.Count);
        Assert.True(seen[0].IsDone);
        Assert.True(seen[1].IsActive);
        Assert.Contains("1 done, 2 open", result);
    }

    [Theory]
    [InlineData(AssistantMode.Normal, "edit_file", ToolDecision.Ask)]
    [InlineData(AssistantMode.Auto, "edit_file", ToolDecision.Run)]
    [InlineData(AssistantMode.Plan, "edit_file", ToolDecision.Block)]
    [InlineData(AssistantMode.Plan, "find_files", ToolDecision.Run)]
    [InlineData(AssistantMode.Normal, "update_todo", ToolDecision.Run)]
    public void Policy_CoversNewTools(AssistantMode mode, string tool, ToolDecision expected) =>
        Assert.Equal(expected, AssistantPolicy.Decide(mode, tool));

    [Fact]
    public void LongJobs_GetManyRounds() => Assert.True(AssistantPolicy.MaxRounds(AssistantMode.Bypass) >= 200);

    [Theory]
    [InlineData("claude-opus-5-5", true)]
    [InlineData("claude-sonnet-4-6", true)]
    [InlineData("claude-fable-5-1", true)]
    [InlineData("claude-haiku-4-5", false)]
    [InlineData("claude-3-5-sonnet-latest", false)]
    public void AdaptiveThinking_OnlyOnModelsThatSupportIt(string model, bool expected) =>
        Assert.Equal(expected, AiProviders.SupportsAdaptiveThinking(model));

    [Fact]
    public async Task OpenAiCompatible_SendsImagesAsDataUrls()
    {
        string? body = null;
        var handler = new StubHandler(req =>
        {
            body = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"choices":[{"message":{"content":"ok"}}]}""", System.Text.Encoding.UTF8, "application/json") };
        });
        using var http = new HttpClient(handler);
        var convo = AiProviders.Start(AiProviderKind.OpenAI, "k", "gpt-5", "s", [], http);

        await convo.SendAsync("what is this?", [new AiImage("image/png", [1, 2, 3])], CancellationToken.None);

        var user = JsonNode.Parse(body!)!["messages"]![1]!;
        Assert.Equal("what is this?", user["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("data:image/png;base64,AQID", user["content"]![1]!["image_url"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task BusyProvider_IsRetried()
    {
        var calls = 0;
        var handler = new StubHandler(_ => ++calls < 3
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("""{"error":{"message":"high demand"}}""") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"choices":[{"message":{"content":"ok"}}]}""", System.Text.Encoding.UTF8, "application/json") });
        using var http = new HttpClient(handler);
        var old = AiProviders.RetryDelays;
        AiProviders.RetryDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];
        try
        {
            var convo = AiProviders.Start(AiProviderKind.Gemini, "k", "gemini-2.5-flash", "s", [], http);
            var retries = 0;
            convo.Retrying = (_, _) => retries++;

            var step = await convo.SendAsync("hi", [], CancellationToken.None);

            Assert.Equal(("ok", 2, 3), (step.Text, retries, calls));
        }
        finally
        {
            AiProviders.RetryDelays = old;
        }
    }

    [Fact]
    public void ChatStore_SavesListsLoadsAndDeletes()
    {
        var root = Path.Combine(Path.GetTempPath(), "pc-chats-" + Guid.NewGuid().ToString("N"));
        var store = new ChatStore(root);
        var a = new SavedChat("20260101000000-aaaaaaaa", "First", "srv", DateTimeOffset.Now.AddHours(-1), [new("user", "hi")]);
        var b = new SavedChat("20260101000001-bbbbbbbb", "Second", null, DateTimeOffset.Now, [new("user", "yo", ["shot.png"]), new("assistant", "hey")]);
        store.Save(a);
        store.Save(b);
        store.SaveAttachment(b.Id, "shot.png", [1]);

        var list = store.List();

        Assert.Equal(["Second", "First"], list.Select(c => c.Title));
        Assert.Equal(2, list[0].Count);
        Assert.Equal(["shot.png"], store.Load(b.Id)!.Lines[0].Files!);

        store.Delete(b.Id);
        Assert.Single(store.List());
        Assert.False(Directory.Exists(store.AttachmentDir(b.Id)));
        Assert.Null(store.Load("../evil"));
    }

    [Fact]
    public void ChatStore_MigratesOldPerServerHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "pc-chats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "srv1.json"), """[{"Role":"user","Text":"Why does it crash?\nhelp"},{"Role":"assistant","Text":"Look at the log"}]""");
        var store = new ChatStore(root);

        Assert.Equal(1, store.MigrateLegacy());

        var chat = Assert.Single(store.List());
        Assert.Equal(("Why does it crash? help", "srv1"), (chat.Title, chat.ServerId));
        Assert.True(File.Exists(Path.Combine(root, "srv1.json.migrated")));
        Assert.Equal(0, store.MigrateLegacy());
    }

    [Fact]
    public void ChatStore_AttachmentNamesStayUnique()
    {
        var store = new ChatStore(Path.Combine(Path.GetTempPath(), "pc-chats-" + Guid.NewGuid().ToString("N")));

        var one = store.SaveAttachment("c1", "a.png", [1]);
        var two = store.SaveAttachment("c1", "a.png", [2]);

        Assert.Equal(("a.png", "a (2).png"), (one, two));
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0, 0, 0, 0, 0 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 }, "image/jpeg")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', 0, 0, 0, 0, 0, 0, 0, 0 }, "image/gif")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, "image/webp")]
    [InlineData(new byte[] { (byte)'B', (byte)'M', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, null)]
    public void MediaType_FromFileHeader(byte[] head, string? expected) => Assert.Equal(expected, AttachmentLoader.MediaTypeOf(head));

    [Fact]
    public void TextAttachment_KeepsTheEndOfLongLogs()
    {
        var log = string.Join("\n", Enumerable.Range(0, 30000).Select(i => "line " + i));

        var a = AttachmentLoader.FromText("latest.log", System.Text.Encoding.UTF8.GetBytes(log));

        Assert.False(a.IsImage);
        Assert.EndsWith("line 29999", a.Text);
        Assert.StartsWith("… (", a.Text);
    }

    [Fact]
    public void UnsupportedFiles_AreRejected()
    {
        var file = Path.Combine(Path.GetTempPath(), "pc-" + Guid.NewGuid().ToString("N") + ".jar");
        File.WriteAllBytes(file, [1, 2]);

        var ex = Assert.Throws<InvalidDataException>(() => AttachmentLoader.FromFile(file));

        Assert.Equal("Attach_Unsupported", ex.Message);
    }
}
