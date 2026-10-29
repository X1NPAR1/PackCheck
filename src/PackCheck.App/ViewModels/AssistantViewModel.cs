using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackCheck.App.Ai;
using PackCheck.App.Infrastructure;

namespace PackCheck.App.ViewModels;

public sealed partial class ChatItem(string role, string text) : ObservableObject
{
    public string Role { get; } = role;

    [ObservableProperty]
    private string _text = text;

    public PendingAction? Pending { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    private bool? _approved;

    public bool IsWaiting => Pending is not null && Approved is null;

    public TaskCompletionSource<bool>? Decision { get; init; }

    [ObservableProperty]
    private bool _isStreaming;

    /// <summary>Tool rows: what was asked (arguments) and what came back, shown when the row is expanded.</summary>
    public string? Arguments { get; init; }

    public string? Result { get; init; }

    [ObservableProperty]
    private bool _isOpen;

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;
}

/// <summary>What is kept on disk per server: user, assistant and status lines (pending approvals are not restored).</summary>
public sealed record SavedChatLine(string Role, string Text);

/// <summary>Chat with the chosen model; tool calls run through <see cref="AssistantTools"/> with approval for every change.</summary>
public sealed partial class AssistantViewModel(ServersViewModel servers, AppSettings settings, HttpClient http) : ObservableObject
{
    private const string SystemPrompt = """
        You are the assistant inside PackCheck, a Windows app that creates, runs and validates Minecraft Java servers.
        Help the user fix errors, configure servers, plugins and mods, translate plugin message files, and set up new servers.
        Use the tools to look at real data (console, files, validation) before answering; do not guess file contents.
        Every change (writing files, console commands, starting, stopping or creating servers) needs the user's approval, which the app asks for; describe briefly what you will change and why.
        When translating files keep keys, placeholders such as %player% or {0}, and color codes (& or §) unchanged.
        Answer in the language the user writes in. Keep answers short and concrete.
        """;

    private readonly AssistantTools _tools = new(servers, () => servers.Selected);
    private IAiConversation? _conversation;
    private CancellationTokenSource? _cts;
    private string? _historyKey;

    public ObservableCollection<ChatItem> Messages { get; } = [];

    public bool IsConfigured => settings.AiModel is not null && (AiProviders.IsLocal(settings.AiProvider) || Secrets.Unprotect(settings.AiKey) is not null);

    public string ModelText => $"{AiProviders.DisplayName(settings.AiProvider)} · {settings.AiModel ?? "–"}";

    public string ServerText => servers.Selected is { } s ? s.Name : Loc.T("Assistant_NoServer");

    [ObservableProperty]
    private string _input = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokensText))]
    private int _tokens;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokensText))]
    private bool _tokensEstimated;

    public string TokensText => Loc.F(TokensEstimated ? "Assistant_TokensApprox" : "Assistant_Tokens", Tokens);

    public void Activate()
    {
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(ModelText));
        OnPropertyChanged(nameof(ServerText));
        LoadHistory();
    }

    // ----- history (one file per server, "general" without a server) -----
    private static string HistoryDir => Path.Combine(Core.AppPaths.DataRoot, "assistant");

    private string CurrentKey => servers.Selected?.Definition.Id ?? "general";

    private void LoadHistory()
    {
        if (_historyKey == CurrentKey || IsBusy)
        {
            return;
        }

        _historyKey = CurrentKey;
        _conversation = null;
        Messages.Clear();
        Tokens = 0;
        try
        {
            var file = Path.Combine(HistoryDir, _historyKey + ".json");
            if (File.Exists(file) && JsonSerializer.Deserialize<List<SavedChatLine>>(File.ReadAllText(file)) is { } lines)
            {
                foreach (var l in lines)
                {
                    Messages.Add(new ChatItem(l.Role, l.Text));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
    }

    private void SaveHistory()
    {
        try
        {
            Directory.CreateDirectory(HistoryDir);
            var lines = Messages.Where(m => m.Role is "user" or "assistant" or "system").TakeLast(200).Select(m => new SavedChatLine(m.Role, m.Text)).ToList();
            File.WriteAllText(Path.Combine(HistoryDir, (_historyKey ?? CurrentKey) + ".json"), JsonSerializer.Serialize(lines));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A new model conversation after a restart still knows what was said before.</summary>
    private string EarlierTranscript()
    {
        var earlier = Messages.Where(m => m.Role is "user" or "assistant").TakeLast(20).ToList();
        return earlier.Count == 0
            ? string.Empty
            : "\nEarlier conversation with this user (for context):\n" + string.Join("\n", earlier.Select(m => $"{m.Role}: {(m.Text.Length > 1500 ? m.Text[..1500] + " …" : m.Text)}"));
    }

    /// <summary>Opens the assistant with a prepared question (crash banner, validation) and sends it when a model is set up.</summary>
    public void Ask(string text)
    {
        MainViewModel.Shell?.Navigate(this);
        Input = text;
        if (IsConfigured && SendCommand.CanExecute(null))
        {
            SendCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void NewChat()
    {
        _cts?.Cancel();
        _conversation = null;
        Messages.Clear();
        Tokens = 0;
        SaveHistory();
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>One-click context: puts the selected server's recent console into the prompt.</summary>
    [RelayCommand]
    private void AttachConsole()
    {
        if (servers.Selected is not { } s)
        {
            return;
        }

        var tail = string.Join("\n", s.Console.Skip(Math.Max(0, s.Console.Count - 60)).Select(e => e.Raw));
        Input = (Input.Length > 0 ? Input + "\n\n" : string.Empty) + $"Console of {s.Name} (last lines):\n```\n{tail}\n```";
    }

    private bool CanSend() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Input.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (!IsConfigured)
        {
            MainViewModel.Shell?.Toast(Loc.T("Assistant_NotConfigured"), isError: true);
            return;
        }

        LoadHistory();
        Input = string.Empty;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        ChatItem? live = null;
        try
        {
            if (_conversation is null)
            {
                _conversation = AiProviders.Start(settings.AiProvider, Secrets.Unprotect(settings.AiKey), settings.AiModel!,
                    SystemPrompt + $"\nSelected server: {ServerText}." + EarlierTranscript(), AssistantTools.Specs, http);
                var dispatcher = System.Windows.Application.Current.Dispatcher;
                _conversation.TextDelta = piece => dispatcher.BeginInvoke(() =>
                {
                    if (live is null)
                    {
                        live = new ChatItem("assistant", string.Empty) { IsStreaming = true };
                        Messages.Add(live);
                    }

                    live.Text += piece;
                });
            }

            Messages.Add(new ChatItem("user", text));
            var step = await _conversation.SendAsync(text, _cts.Token).ConfigureAwait(true);
            for (var round = 0; round < 12; round++)
            {
                // Some OpenAI-compatible providers send no usage while streaming; estimate (~4 characters per token) instead of showing 0.
                Tokens += step.InputTokens + step.OutputTokens > 0
                    ? step.InputTokens + step.OutputTokens
                    : (text.Length + step.Text.Length + step.ToolCalls.Sum(c => c.Arguments.ToJsonString().Length)) / 4;
                TokensEstimated |= step.InputTokens + step.OutputTokens == 0;

                // Let queued deltas land, then make the streamed bubble the final text.
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                if (live is not null)
                {
                    live.Text = step.Text;
                    live.IsStreaming = false;
                    live = null;
                }
                else if (step.Text.Length > 0)
                {
                    Messages.Add(new ChatItem("assistant", step.Text));
                }

                if (step.ToolCalls.Count == 0)
                {
                    break;
                }

                var results = new List<(AiToolCall, string, bool)>();
                foreach (var call in step.ToolCalls)
                {
                    results.Add(await RunToolAsync(call).ConfigureAwait(true));
                }

                step = await _conversation.ContinueAsync(results, _cts.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            Messages.Add(new ChatItem("system", Loc.T("Assistant_Cancelled")));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or Anthropic.Exceptions.AnthropicApiException or System.Text.Json.JsonException)
        {
            Messages.Add(new ChatItem("system", Loc.F("Assistant_Error", ex.Message)));
        }
        finally
        {
            if (live is not null)
            {
                live.IsStreaming = false;
            }

            IsBusy = false;
            SaveHistory();
        }
    }

    private async Task<(AiToolCall, string, bool)> RunToolAsync(AiToolCall call)
    {
        try
        {
            var (result, pending) = await _tools.RunAsync(call).ConfigureAwait(true);
            if (pending is null)
            {
                Messages.Add(ToolRow(call, result ?? string.Empty));
                return (call, result ?? string.Empty, false);
            }

            var decision = new TaskCompletionSource<bool>();
            var card = new ChatItem("approval", pending.Title) { Pending = pending, Decision = decision };
            Messages.Add(card);
            var ok = await decision.Task.ConfigureAwait(true);
            card.Approved = ok;
            if (!ok)
            {
                return (call, "The user rejected this change.", true);
            }

            return (call, await pending.Execute().ConfigureAwait(true), false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException or InvalidOperationException or ArgumentException)
        {
            return (call, "Error: " + ex.Message, true);
        }
    }

    /// <summary>A readable title for a tool call ("Read plugins/Skin/config.yml") plus its details.</summary>
    private static ChatItem ToolRow(AiToolCall call, string result)
    {
        string Arg(string name) => call.Arguments[name]?.ToString() ?? string.Empty;
        var title = call.Name switch
        {
            "list_servers" => Loc.T("Tool_ListServers"),
            "read_console" => Loc.T("Tool_ReadConsole"),
            "list_files" => Loc.F("Tool_ListFiles", Arg("path") is { Length: > 0 } p ? p : "/"),
            "read_file" => Loc.F("Tool_ReadFile", Arg("path")),
            "list_addons" => Loc.T("Tool_ListAddons"),
            "run_validation" => Loc.T("Tool_Validate"),
            _ => call.Name,
        };
        var lines = result.Split('\n');
        var shown = lines.Length > 400 ? string.Join('\n', lines.Take(400)) + $"\n… ({lines.Length - 400} more lines)" : result;
        return new ChatItem("tool", title)
        {
            Arguments = call.Arguments.Count == 0 ? null : call.Arguments.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            Result = shown.Length > 0 ? shown : Loc.T("Tool_Empty"),
        };
    }

    [RelayCommand]
    private static void Approve(ChatItem? item) => item?.Decision?.TrySetResult(true);

    [RelayCommand]
    private static void Reject(ChatItem? item) => item?.Decision?.TrySetResult(false);

    [RelayCommand]
    private static void OpenSettings() => MainViewModel.Shell?.Navigate(MainViewModel.Shell.Settings);
}
