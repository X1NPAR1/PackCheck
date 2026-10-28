using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Messages;

namespace PackCheck.App.Ai;

/// <summary>Providers the user can pick from (no free-text endpoints).</summary>
public enum AiProviderKind
{
    Anthropic,
    OpenAI,
    Gemini,
    OpenRouter,
    Ollama,
    LmStudio,

    // Added in 3.1.1; appended so settings saved as numbers keep their meaning.
    Grok,
    DeepSeek,
    Mistral,
    Groq,
}

public sealed record AiToolSpec(string Name, string Description, JsonObject Schema);

public sealed record AiToolCall(string Id, string Name, JsonObject Arguments);

/// <summary>What the model produced in one turn: text for the user and/or tool calls to run.</summary>
public sealed record AiStep(string Text, IReadOnlyList<AiToolCall> ToolCalls, int InputTokens, int OutputTokens, string? StopReason);

/// <summary>One chat with its native history; tool results are fed back with <see cref="ContinueAsync"/>.</summary>
public interface IAiConversation
{
    Task<AiStep> SendAsync(string userText, CancellationToken ct);

    Task<AiStep> ContinueAsync(IReadOnlyList<(AiToolCall Call, string Result, bool IsError)> results, CancellationToken ct);
}

public static class AiProviders
{
    public static bool IsLocal(AiProviderKind k) => k is AiProviderKind.Ollama or AiProviderKind.LmStudio;

    /// <summary>OpenAI-compatible base URLs (Gemini and the local servers expose the same chat API).</summary>
    public static string BaseUrl(AiProviderKind k) => k switch
    {
        AiProviderKind.OpenAI => "https://api.openai.com/v1",
        AiProviderKind.Gemini => "https://generativelanguage.googleapis.com/v1beta/openai",
        AiProviderKind.Grok => "https://api.x.ai/v1",
        AiProviderKind.DeepSeek => "https://api.deepseek.com/v1",
        AiProviderKind.Mistral => "https://api.mistral.ai/v1",
        AiProviderKind.Groq => "https://api.groq.com/openai/v1",
        AiProviderKind.OpenRouter => "https://openrouter.ai/api/v1",
        AiProviderKind.Ollama => "http://localhost:11434/v1",
        AiProviderKind.LmStudio => "http://localhost:1234/v1",
        _ => "https://api.anthropic.com",
    };

    /// <summary>Display name with the company, for the provider picker.</summary>
    public static string DisplayName(AiProviderKind k) => k switch
    {
        AiProviderKind.Anthropic => "Anthropic Claude",
        AiProviderKind.OpenAI => "OpenAI ChatGPT",
        AiProviderKind.Gemini => "Google Gemini",
        AiProviderKind.Grok => "xAI Grok",
        AiProviderKind.DeepSeek => "DeepSeek",
        AiProviderKind.Mistral => "Mistral AI",
        AiProviderKind.Groq => "Groq",
        AiProviderKind.OpenRouter => "OpenRouter",
        AiProviderKind.Ollama => "Ollama (local)",
        AiProviderKind.LmStudio => "LM Studio (local)",
        _ => k.ToString(),
    };

    /// <summary>Where the user gets an API key.</summary>
    public static string? KeyPage(AiProviderKind k) => k switch
    {
        AiProviderKind.Anthropic => "https://console.anthropic.com/settings/keys",
        AiProviderKind.OpenAI => "https://platform.openai.com/api-keys",
        AiProviderKind.Gemini => "https://aistudio.google.com/apikey",
        AiProviderKind.Grok => "https://console.x.ai",
        AiProviderKind.DeepSeek => "https://platform.deepseek.com/api_keys",
        AiProviderKind.Mistral => "https://console.mistral.ai/api-keys",
        AiProviderKind.Groq => "https://console.groq.com/keys",
        AiProviderKind.OpenRouter => "https://openrouter.ai/keys",
        _ => null,
    };

    /// <summary>
    /// Well-known chat models shown before connecting, so there is always something to pick.
    /// "Connect" replaces them with the provider's live list.
    /// </summary>
    public static IReadOnlyList<string> SuggestedModels(AiProviderKind k) => k switch
    {
        AiProviderKind.Anthropic => ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5"],
        AiProviderKind.OpenAI => ["gpt-5", "gpt-5-mini", "gpt-4.1", "gpt-4o-mini"],
        AiProviderKind.Gemini => ["gemini-2.5-pro", "gemini-2.5-flash", "gemini-2.5-flash-lite"],
        AiProviderKind.Grok => ["grok-4", "grok-3", "grok-3-mini"],
        AiProviderKind.DeepSeek => ["deepseek-chat", "deepseek-reasoner"],
        AiProviderKind.Mistral => ["mistral-large-latest", "mistral-medium-latest", "mistral-small-latest", "codestral-latest"],
        AiProviderKind.Groq => ["llama-3.3-70b-versatile", "openai/gpt-oss-120b", "qwen/qwen3-32b"],
        AiProviderKind.OpenRouter => ["anthropic/claude-sonnet-5-5", "openai/gpt-5", "google/gemini-2.5-pro", "x-ai/grok-4", "deepseek/deepseek-chat"],
        _ => [],
    };

    /// <summary>Drops embeddings, speech, image and moderation models from a provider's list; only chat models can drive the assistant.</summary>
    public static bool IsChatModel(string id)
    {
        string[] skip = ["embed", "whisper", "tts", "dall-e", "image", "moderation", "audio", "transcribe", "realtime", "search", "rerank", "ocr", "guard", "imagen", "veo", "aqa"];
        return !skip.Any(x => id.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The provider's own model list, so the user picks a model instead of typing one.</summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(AiProviderKind kind, string? apiKey, HttpClient http, CancellationToken ct)
    {
        if (kind == AiProviderKind.Anthropic)
        {
            var client = new AnthropicClient { ApiKey = apiKey };
            var page = await client.Models.List(cancellationToken: ct).ConfigureAwait(false);
            return page.Items.Select(m => m.ID).ToList();
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUrl(kind) + "/models"));
        if (!string.IsNullOrEmpty(apiKey))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()!)
            .Select(id => id.StartsWith("models/", StringComparison.Ordinal) ? id[7..] : id).Where(IsChatModel).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IAiConversation Start(AiProviderKind kind, string? apiKey, string model, string system, IReadOnlyList<AiToolSpec> tools, HttpClient http) =>
        kind == AiProviderKind.Anthropic
            ? new AnthropicConversation(apiKey, model, system, tools)
            : new OpenAiCompatibleConversation(BaseUrl(kind), apiKey, model, system, tools, http);
}

/// <summary>Anthropic models through the official SDK. Assistant turns are echoed back block by block (thinking signatures kept).</summary>
internal sealed class AnthropicConversation(string? apiKey, string model, string system, IReadOnlyList<AiToolSpec> tools) : IAiConversation
{
    private readonly AnthropicClient _client = new() { ApiKey = apiKey };
    private readonly List<MessageParam> _messages = [];

    private readonly List<ToolUnion> _tools = tools.Select(t => (ToolUnion)new Tool
    {
        Name = t.Name,
        Description = t.Description,
        InputSchema = new()
        {
            Properties = (t.Schema["properties"] as JsonObject ?? []).ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)),
            Required = (t.Schema["required"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToList() ?? [],
        },
    }).ToList();

    public Task<AiStep> SendAsync(string userText, CancellationToken ct)
    {
        _messages.Add(new UserMessageParam { Content = userText });
        return RunAsync(ct);
    }

    public Task<AiStep> ContinueAsync(IReadOnlyList<(AiToolCall Call, string Result, bool IsError)> results, CancellationToken ct)
    {
        List<ContentBlockParam> blocks = [];
        foreach (var (call, result, isError) in results)
        {
            blocks.Add(new ToolResultBlockParam { ToolUseID = call.Id, Content = result, IsError = isError });
        }

        _messages.Add(new UserMessageParam { Content = blocks });
        return RunAsync(ct);
    }

    private async Task<AiStep> RunAsync(CancellationToken ct)
    {
        var response = await _client.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = 16000,
            System = system,
            Tools = _tools,
            Messages = [.. _messages],
        }, ct).ConfigureAwait(false);

        List<ContentBlockParam> echo = [];
        var text = new StringBuilder();
        var calls = new List<AiToolCall>();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var t))
            {
                echo.Add(new TextBlockParam { Text = t.Text });
                text.Append(t.Text);
            }
            else if (block.TryPickThinking(out var th))
            {
                echo.Add(new ThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
            }
            else if (block.TryPickRedactedThinking(out var rt))
            {
                echo.Add(new RedactedThinkingBlockParam { Data = rt.Data });
            }
            else if (block.TryPickToolUse(out var tu))
            {
                echo.Add(new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                var args = JsonNode.Parse(JsonSerializer.Serialize(tu.Input)) as JsonObject ?? [];
                calls.Add(new AiToolCall(tu.ID, tu.Name, args));
            }
        }

        _messages.Add(new AssistantMessageParam { Content = echo });
        var stop = response.StopReason?.ToString();
        if (stop is not null && stop.Contains("refusal", StringComparison.OrdinalIgnoreCase) && text.Length == 0)
        {
            text.Append("(The model declined this request.)");
        }

        return new AiStep(text.ToString(), calls, (int)response.Usage.InputTokens, (int)response.Usage.OutputTokens, stop);
    }
}

/// <summary>OpenAI chat completions with function tools (OpenAI, Gemini, OpenRouter, Ollama, LM Studio).</summary>
internal sealed class OpenAiCompatibleConversation : IAiConversation
{
    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly HttpClient _http;
    private readonly JsonArray _messages = [];
    private readonly JsonArray _tools = [];

    public OpenAiCompatibleConversation(string baseUrl, string? apiKey, string model, string system, IReadOnlyList<AiToolSpec> tools, HttpClient http)
    {
        _baseUrl = baseUrl;
        _apiKey = apiKey;
        _model = model;
        _http = http;
        _messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        foreach (var t in tools)
        {
            _tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Schema.DeepClone() },
            });
        }
    }

    public Task<AiStep> SendAsync(string userText, CancellationToken ct)
    {
        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = userText });
        return RunAsync(ct);
    }

    public Task<AiStep> ContinueAsync(IReadOnlyList<(AiToolCall Call, string Result, bool IsError)> results, CancellationToken ct)
    {
        foreach (var (call, result, _) in results)
        {
            _messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.Id, ["content"] = result });
        }

        return RunAsync(ct);
    }

    private async Task<AiStep> RunAsync(CancellationToken ct)
    {
        var body = new JsonObject { ["model"] = _model, ["messages"] = _messages.DeepClone(), ["tools"] = _tools.DeepClone() };
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUrl + "/chat/completions"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(_apiKey))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)res.StatusCode}: {json[..Math.Min(json.Length, 300)]}");
        }

        var root = JsonNode.Parse(json)!;
        var message = root["choices"]![0]!["message"]!;
        var text = message["content"]?.GetValue<string>() ?? string.Empty;
        var calls = new List<AiToolCall>();
        if (message["tool_calls"] is JsonArray tc)
        {
            foreach (var c in tc)
            {
                var fn = c!["function"]!;
                var argsText = fn["arguments"]?.GetValue<string>() ?? "{}";
                var args = JsonNode.Parse(argsText.Length == 0 ? "{}" : argsText) as JsonObject ?? [];
                calls.Add(new AiToolCall(c["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"), fn["name"]!.GetValue<string>(), args));
            }
        }

        _messages.Add(message.DeepClone());
        var usage = root["usage"];
        return new AiStep(text, calls, usage?["prompt_tokens"]?.GetValue<int>() ?? 0, usage?["completion_tokens"]?.GetValue<int>() ?? 0, root["choices"]![0]!["finish_reason"]?.GetValue<string>());
    }
}
