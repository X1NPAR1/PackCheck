using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace PackCheck.Servers;

[JsonConverter(typeof(JsonStringEnumConverter<DiscordEvent>))]
public enum DiscordEvent
{
    Started,
    Stopped,
    Crashed,
    PlayerJoined,
    PlayerLeft,
    LowTps,
    BackupDone,
    BackupFailed,
    Automation,
}

/// <summary>Per-server Discord settings, stored in .packcheck/discord.json.</summary>
public sealed class DiscordSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string? WebhookUrl { get; set; }

    /// <summary>Text put in front of important messages (crash, low TPS), e.g. "@here" or "&lt;@&amp;roleId&gt;".</summary>
    public string? Mention { get; set; }

    public HashSet<DiscordEvent> Events { get; set; } = [DiscordEvent.Started, DiscordEvent.Stopped, DiscordEvent.Crashed, DiscordEvent.BackupFailed, DiscordEvent.LowTps];

    [JsonIgnore]
    public bool Enabled => DiscordWebhook.IsValidUrl(WebhookUrl);

    public static string FileFor(string serverDirectory) => Path.Combine(serverDirectory, ".packcheck", "discord.json");

    public static DiscordSettings Load(string serverDirectory)
    {
        try
        {
            var f = FileFor(serverDirectory);
            return File.Exists(f) ? JsonSerializer.Deserialize<DiscordSettings>(File.ReadAllText(f), Json) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Save(string serverDirectory)
    {
        var f = FileFor(serverDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, JsonSerializer.Serialize(this, Json));
    }
}

/// <summary>One message to post; <see cref="Title"/> and <see cref="Description"/> are already localized.</summary>
public sealed record DiscordMessage(DiscordEvent Event, string Server, string Title, string? Description = null, IReadOnlyList<(string Name, string Value)>? Fields = null, string? Mention = null);

public static class DiscordWebhook
{
    private static readonly string[] Hosts = ["discord.com", "discordapp.com", "canary.discord.com", "ptb.discord.com"];

    /// <summary>Only https Discord webhook URLs, so a typo can never post server data anywhere else.</summary>
    public static bool IsValidUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && Hosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase)
        && u.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal)
        && u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length >= 4;

    public static int ColorFor(DiscordEvent e) => e switch
    {
        DiscordEvent.Started or DiscordEvent.BackupDone or DiscordEvent.PlayerJoined => 0x5E9A3E,
        DiscordEvent.Crashed or DiscordEvent.BackupFailed => 0xD9534F,
        DiscordEvent.LowTps => 0xE0A030,
        _ => 0x7A7A7A,
    };

    /// <summary>The JSON body Discord expects: one embed, no @everyone unless the user's own mention asks for it.</summary>
    public static JsonObject Payload(DiscordMessage m, DateTimeOffset now)
    {
        var embed = new JsonObject
        {
            ["title"] = Trim(m.Title, 256),
            ["color"] = ColorFor(m.Event),
            ["timestamp"] = now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["footer"] = new JsonObject { ["text"] = Trim(m.Server, 100) + " · PackCheck" },
        };
        if (!string.IsNullOrWhiteSpace(m.Description))
        {
            embed["description"] = Trim(m.Description, 4000);
        }

        if (m.Fields is { Count: > 0 })
        {
            embed["fields"] = new JsonArray([.. m.Fields.Take(25).Select(f => (JsonNode)new JsonObject { ["name"] = Trim(f.Name, 256), ["value"] = Trim(f.Value, 1024), ["inline"] = true })]);
        }

        var body = new JsonObject
        {
            ["username"] = "PackCheck",
            ["embeds"] = new JsonArray(embed),
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray("roles", "users", "everyone") },
        };
        if (!string.IsNullOrWhiteSpace(m.Mention))
        {
            body["content"] = Trim(m.Mention, 200);
        }
        else
        {
            body["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() };
        }

        return body;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>
/// Posts messages in order on a background queue: at most one request per second, waits on HTTP 429
/// (retry_after), retries network errors twice, and never blocks the caller.
/// </summary>
public sealed class DiscordNotifier : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly Channel<(string Url, DiscordMessage Message, TaskCompletionSource<bool>? Done)> _queue = Channel.CreateBounded<(string, DiscordMessage, TaskCompletionSource<bool>?)>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly TimeSpan _minGap;

    public DiscordNotifier(HttpClient http, TimeSpan? minGap = null)
    {
        _http = http;
        _minGap = minGap ?? TimeSpan.FromSeconds(1);
        _worker = Task.Run(RunAsync);
    }

    /// <summary>Last error text, for showing in the UI.</summary>
    public string? LastError { get; private set; }

    public void Post(string url, DiscordMessage message) => _queue.Writer.TryWrite((url, message, null));

    /// <summary>Posts and waits for the result (used by "Send test").</summary>
    public Task<bool> SendAsync(string url, DiscordMessage message)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Writer.TryWrite((url, message, done));
        return done.Task;
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var (url, message, done) in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                var ok = await SendOneAsync(url, message).ConfigureAwait(false);
                done?.TrySetResult(ok);
                await Task.Delay(_minGap, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<bool> SendOneAsync(string url, DiscordMessage message)
    {
        if (!DiscordWebhook.IsValidUrl(url))
        {
            LastError = "Invalid webhook URL";
            return false;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var response = await _http.PostAsJsonAsync(url.Trim(), DiscordWebhook.Payload(message, DateTimeOffset.Now), _cts.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    LastError = null;
                    return true;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var wait = 2.0;
                    var text = await response.Content.ReadAsStringAsync(_cts.Token).ConfigureAwait(false);
                    try
                    {
                        wait = JsonNode.Parse(text)?["retry_after"]?.GetValue<double>() ?? wait;
                    }
                    catch (JsonException)
                    {
                    }

                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait, 0.1, 30)), _cts.Token).ConfigureAwait(false);
                    continue;
                }

                // 4xx other than 429: the webhook was deleted or the URL is wrong; retrying will not help.
                LastError = $"Discord: {(int)response.StatusCode} {response.ReasonPhrase}";
                return false;
            }
            catch (HttpRequestException ex)
            {
                LastError = ex.Message;
                await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!_cts.IsCancellationRequested)
            {
                LastError = "Discord did not answer in time";
            }
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }
}
