using System.Net;
using System.Text.Json.Nodes;
using PackCheck.Servers;
using Xunit;

namespace PackCheck.Tests;

public class DiscordTests
{
    [Theory]
    [InlineData("https://discord.com/api/webhooks/123456/abcDEF-token", true)]
    [InlineData("https://discordapp.com/api/webhooks/1/x", true)]
    [InlineData("https://ptb.discord.com/api/webhooks/1/x", true)]
    [InlineData("http://discord.com/api/webhooks/1/x", false)]
    [InlineData("https://discord.com.evil.io/api/webhooks/1/x", false)]
    [InlineData("https://discord.com/api/webhooks/1", false)]
    [InlineData("https://example.com/api/webhooks/1/x", false)]
    [InlineData("", false)]
    public void Url_validation(string url, bool ok) => Assert.Equal(ok, DiscordWebhook.IsValidUrl(url));

    [Fact]
    public void Payload_has_embed_fields_and_blocks_mentions_by_default()
    {
        var m = new DiscordMessage(DiscordEvent.Crashed, "SMP", "Server crashed", "Broken config", [("Cause", "paper-global.yml")]);
        var body = DiscordWebhook.Payload(m, new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));
        var embed = body["embeds"]![0]!;
        Assert.Equal("Server crashed", embed["title"]!.GetValue<string>());
        Assert.Equal(0xD9534F, embed["color"]!.GetValue<int>());
        Assert.Equal("2026-10-29T12:00:00.000Z", embed["timestamp"]!.GetValue<string>());
        Assert.Equal("Cause", embed["fields"]![0]!["name"]!.GetValue<string>());
        Assert.Empty(body["allowed_mentions"]!["parse"]!.AsArray());
        Assert.Null(body["content"]);
    }

    [Fact]
    public void Mention_is_sent_as_content()
    {
        var body = DiscordWebhook.Payload(new DiscordMessage(DiscordEvent.Crashed, "SMP", "x", Mention: "@here"), DateTimeOffset.Now);
        Assert.Equal("@here", body["content"]!.GetValue<string>());
    }

    [Fact]
    public void Long_text_is_cut_to_discord_limits()
    {
        var body = DiscordWebhook.Payload(new DiscordMessage(DiscordEvent.Started, "s", new string('a', 400), new string('b', 5000)), DateTimeOffset.Now);
        Assert.Equal(256, body["embeds"]![0]!["title"]!.GetValue<string>().Length);
        Assert.Equal(4000, body["embeds"]![0]!["description"]!.GetValue<string>().Length);
    }

    private sealed class FakeHandler(params HttpStatusCode[] codes) : HttpMessageHandler
    {
        private int _i;
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var code = codes[Math.Min(_i++, codes.Length - 1)];
            var r = new HttpResponseMessage(code);
            if (code == HttpStatusCode.TooManyRequests)
            {
                r.Content = new StringContent("""{"retry_after": 0.1}""");
            }

            return r;
        }
    }

    [Fact]
    public async Task Notifier_retries_after_429_and_reports_success()
    {
        var handler = new FakeHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.NoContent);
        await using var n = new DiscordNotifier(new HttpClient(handler), TimeSpan.Zero);
        var ok = await n.SendAsync("https://discord.com/api/webhooks/1/x", new DiscordMessage(DiscordEvent.Started, "s", "Started"));
        Assert.True(ok);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal("Started", JsonNode.Parse(handler.Bodies[1])!["embeds"]![0]!["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Notifier_gives_up_on_404_and_keeps_error()
    {
        var handler = new FakeHandler(HttpStatusCode.NotFound);
        await using var n = new DiscordNotifier(new HttpClient(handler), TimeSpan.Zero);
        Assert.False(await n.SendAsync("https://discord.com/api/webhooks/1/x", new DiscordMessage(DiscordEvent.Started, "s", "x")));
        Assert.Single(handler.Bodies);
        Assert.Contains("404", n.LastError);
    }

    [Fact]
    public void Settings_round_trip()
    {
        var dir = Directory.CreateTempSubdirectory("pc-discord").FullName;
        var s = new DiscordSettings { WebhookUrl = "https://discord.com/api/webhooks/1/x", Events = [DiscordEvent.PlayerJoined] };
        s.Save(dir);
        var again = DiscordSettings.Load(dir);
        Assert.True(again.Enabled);
        Assert.Equal([DiscordEvent.PlayerJoined], again.Events);
    }
}
