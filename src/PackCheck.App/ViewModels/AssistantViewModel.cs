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

    /// <summary>Mode the message was sent in (shown under user messages that were not sent in Normal mode).</summary>
    public string? ModeTag { get; init; }

    /// <summary>Tool rows: what was asked (arguments) and what came back, shown when the row is expanded.</summary>
    public string? Arguments { get; init; }

    public string? Result { get; init; }

    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Files sent with a user message (stored next to the chat).</summary>
    public IReadOnlyList<ChatFile>? Files { get; init; }

    public bool HasFiles => Files is { Count: > 0 };

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;
}

/// <summary>An attachment shown in a sent message; clicking it opens the file.</summary>
public sealed partial class ChatFile(string name, string fullPath, bool isImage)
{
    public string Name { get; } = name;

    public string FullPath { get; } = fullPath;

    public bool IsImage { get; } = isImage;

    public System.Windows.Media.Imaging.BitmapSource? Thumbnail
    {
        get
        {
            try
            {
                return IsImage && File.Exists(FullPath) ? AttachmentLoader.Decode(File.ReadAllBytes(FullPath), 240) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    [RelayCommand]
    private void Open()
    {
        if (File.Exists(FullPath))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(FullPath) { UseShellExecute = true });
        }
    }
}

/// <summary>An entry of the assistant's server picker; <see cref="Server"/> is null for "no server".</summary>
public sealed record ServerChoice(ServerItemViewModel? Server, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A row in the chat history list.</summary>
public sealed class ChatHistoryRow(ChatSummary summary, string serverName, bool isCurrent)
{
    public ChatSummary Summary { get; } = summary;

    public string Title => Summary.Title.Length > 0 ? Summary.Title : Loc.T("Assistant_Untitled");

    public string ServerName { get; } = serverName;

    public bool IsCurrent { get; } = isCurrent;

    public string Detail => $"{ServerName} · {Summary.Updated.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentUICulture)} · {Loc.F("Assistant_MessageCount", Summary.Count)}";
}

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

        Work like a careful agent that finishes the job:
        - For any task with several parts (for example "translate all plugins"), first find every item with find_files / list_addons / search_files, then call update_todo with one item per file or plugin, and work through ALL of them one by one, updating the list as you go. Never stop after the first item and never leave work half done.
        - Read a file completely before changing it (read_file returns large files in parts; continue with start_line). Change small parts with edit_file; write whole files with write_file only when they are small enough to write out in full.
        - When translating, translate every user-visible message in the file, not a sample. Keep keys, placeholders (%player%, {0}, <player>), MiniMessage tags and & or § color codes unchanged. Skip files that are not messages (permissions, database, numeric settings).
        - After changing files, check your work (read the file again or run validation) and fix mistakes.
        - When everything is done, call task_complete with a short summary. Only stop earlier to ask the user when you really cannot continue without them.
        """;

    private readonly ChatStore _store = new(Path.Combine(Core.AppPaths.DataRoot, "assistant"));
    private AssistantTools? _tools;
    private IAiConversation? _conversation;
    private CancellationTokenSource? _cts;
    private string? _chatId;
    private bool _migrated;
    private bool _quietServerChange;

    private AssistantTools Tools
    {
        get
        {
            if (_tools is null)
            {
                _tools = new AssistantTools(servers, () => Target);
                _tools.TodoChanged += items => System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    Todo.Clear();
                    foreach (var t in items)
                    {
                        Todo.Add(t);
                    }

                    OnPropertyChanged(nameof(TodoProgress));
                    OnPropertyChanged(nameof(HasTodo));
                });
            }

            return _tools;
        }
    }

    /// <summary>The assistant's own task list for long jobs, shown above the input.</summary>
    public ObservableCollection<TodoItem> Todo { get; } = [];

    public bool HasTodo => Todo.Count > 0;

    public string TodoProgress => Loc.F("Assistant_TodoProgress", Todo.Count(t => t.IsDone), Todo.Count);

    [ObservableProperty]
    private string _statusText = string.Empty;

    private void ClearTodo()
    {
        Todo.Clear();
        OnPropertyChanged(nameof(TodoProgress));
        OnPropertyChanged(nameof(HasTodo));
    }

    /// <summary>The server the assistant works on, chosen in its own picker (independent of the Servers page).</summary>
    public ServerItemViewModel? Target => SelectedServer?.Server;

    public ObservableCollection<ServerChoice> ServerChoices { get; } = [];

    [ObservableProperty]
    private ServerChoice? _selectedServer;

    partial void OnSelectedServerChanged(ServerChoice? oldValue, ServerChoice? newValue)
    {
        OnPropertyChanged(nameof(ServerText));
        OnPropertyChanged(nameof(Target));
        AttachConsoleCommand.NotifyCanExecuteChanged();
        if (_quietServerChange || oldValue is null || newValue is null || oldValue.Server == newValue.Server)
        {
            return;
        }

        // The next message starts a model conversation that knows the new server; earlier lines go along as context.
        _conversation = null;
        if (Messages.Count > 0)
        {
            Messages.Add(new ChatItem("system", Loc.F("Assistant_ServerSwitched", newValue.Name)));
            SaveChat();
        }
    }

    // ----- attachments -----
    public ObservableCollection<ChatAttachment> PendingFiles { get; } = [];

    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                AddAttachment(AttachmentLoader.FromFile(path));
            }
            catch (InvalidDataException ex)
            {
                MainViewModel.Shell?.Toast(Loc.F(ex.Message, Path.GetFileName(path)), isError: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MainViewModel.Shell?.Toast(Loc.F("Attach_ReadFailed", Path.GetFileName(path), ex.Message), isError: true);
            }
        }
    }

    /// <summary>A pasted screenshot.</summary>
    public void AddImage(System.Windows.Media.Imaging.BitmapSource image)
    {
        var name = Loc.T("Attach_ScreenshotName") + " " + DateTime.Now.ToString("HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture) + ".png";
        AddAttachment(AttachmentLoader.FromBitmap(name, image));
    }

    private void AddAttachment(ChatAttachment a)
    {
        if (PendingFiles.Count >= AttachmentLoader.MaxAttachments)
        {
            MainViewModel.Shell?.Toast(Loc.F("Attach_Limit", AttachmentLoader.MaxAttachments), isError: true);
            return;
        }

        PendingFiles.Add(a);
    }

    [RelayCommand]
    private void AttachFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = Loc.T("Attach_Title"),
            Filter = $"{Loc.T("Attach_Filter")}|{AttachmentLoader.FileFilter}",
        };
        if (Target is { } s && Directory.Exists(s.Definition.Directory))
        {
            dialog.InitialDirectory = s.Definition.Directory;
        }

        if (dialog.ShowDialog() == true)
        {
            AddFiles(dialog.FileNames);
        }
    }

    [RelayCommand]
    private void RemoveFile(ChatAttachment? a)
    {
        if (a is not null)
        {
            PendingFiles.Remove(a);
        }
    }

    // ----- history -----
    public ObservableCollection<ChatHistoryRow> History { get; } = [];

    [ObservableProperty]
    private bool _isHistoryOpen;

    [ObservableProperty]
    private string _historySearch = string.Empty;

    partial void OnHistorySearchChanged(string value) => RefreshHistory();

    partial void OnIsHistoryOpenChanged(bool value)
    {
        if (value)
        {
            RefreshHistory();
        }
    }

    [RelayCommand]
    private void ToggleHistory() => IsHistoryOpen = !IsHistoryOpen;

    private string ServerName(string? id) =>
        id is null ? Loc.T("Assistant_General") : servers.Items.FirstOrDefault(s => s.Definition.Id == id)?.Name ?? Loc.T("Assistant_RemovedServer");

    /// <summary>Search looks at titles, server names and the messages themselves.</summary>
    private void RefreshHistory()
    {
        History.Clear();
        var q = HistorySearch.Trim();
        foreach (var c in _store.List())
        {
            var row = new ChatHistoryRow(c, ServerName(c.ServerId), c.Id == _chatId);
            if (q.Length == 0 || row.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || row.ServerName.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || (_store.Load(c.Id)?.Lines.Any(l => l.Text.Contains(q, StringComparison.CurrentCultureIgnoreCase)) ?? false))
            {
                History.Add(row);
            }
        }
    }

    [RelayCommand]
    private void OpenChat(ChatHistoryRow? row)
    {
        if (row is null || IsBusy)
        {
            return;
        }

        LoadChat(row.Summary.Id);
        IsHistoryOpen = false;
    }

    [RelayCommand]
    private async Task DeleteChatAsync(ChatHistoryRow? row)
    {
        if (row is null || MainViewModel.Shell is not { } shell)
        {
            return;
        }

        if (!await shell.ConfirmAsync(Loc.T("Assistant_DeleteTitle"), Loc.F("Assistant_DeleteBody", row.Title), Loc.T("Assistant_DeleteConfirm"), danger: true).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            _store.Delete(row.Summary.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            shell.Toast(ex.Message, isError: true);
        }

        if (row.Summary.Id == _chatId && !IsBusy)
        {
            StartNewChat();
        }

        RefreshHistory();
    }

    public ObservableCollection<ChatItem> Messages { get; } = [];

    public IReadOnlyList<AssistantMode> Modes { get; } = [AssistantMode.Normal, AssistantMode.Plan, AssistantMode.Auto, AssistantMode.Bypass];

    public AssistantMode Mode
    {
        get => settings.AssistantMode;
        set
        {
            if (value == settings.AssistantMode)
            {
                return;
            }

            if (value == AssistantMode.Bypass)
            {
                _ = ConfirmBypassAsync();
                return;
            }

            SetMode(value);
        }
    }

    public bool IsBypass => Mode == AssistantMode.Bypass;

    public string ModeHint => Loc.T("Mode_" + Mode + "_Hint");

    private void SetMode(AssistantMode value)
    {
        settings.AssistantMode = value;
        settings.Save();
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsBypass));
        OnPropertyChanged(nameof(ModeHint));
    }

    /// <summary>Bypass applies everything without asking, so switching to it is confirmed once.</summary>
    private async Task ConfirmBypassAsync()
    {
        if (MainViewModel.Shell is { } shell && await shell.ConfirmAsync(Loc.T("Mode_BypassTitle"), Loc.T("Mode_BypassBody"), Loc.T("Mode_BypassConfirm"), danger: true).ConfigureAwait(true))
        {
            SetMode(AssistantMode.Bypass);
        }
        else
        {
            OnPropertyChanged(nameof(Mode)); // put the picker back
        }
    }

    /// <summary>Plan answers get buttons: run it with approvals, or run it automatically.</summary>
    [RelayCommand]
    private async Task RunPlanAsync(string? how)
    {
        SetMode(how == "auto" ? AssistantMode.Auto : AssistantMode.Normal);
        Input = Loc.T("Mode_RunPlanMessage");
        await SendAsync().ConfigureAwait(true);
    }

    public bool IsConfigured => settings.AiModel is not null && (AiProviders.IsLocal(settings.AiProvider) || Secrets.Unprotect(settings.AiKey) is not null);

    public string ModelText => $"{AiProviders.DisplayName(settings.AiProvider)} · {settings.AiModel ?? "–"}";

    public string ServerText => Target is { } s ? s.Name : Loc.T("Assistant_NoServer");

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
        RefreshServers();
        if (!_migrated)
        {
            _migrated = true;
            _store.MigrateLegacy();
        }

        if (_chatId is null)
        {
            // Continue this server's last chat, or start a fresh one.
            var key = Target?.Definition.Id;
            if (_store.List().FirstOrDefault(c => c.ServerId == key) is { } last)
            {
                LoadChat(last.Id);
            }
            else
            {
                StartNewChat();
            }
        }

        if (IsHistoryOpen)
        {
            RefreshHistory();
        }
    }

    /// <summary>Keeps the picker in step with the server list; the first time it follows the Servers page selection.</summary>
    private void RefreshServers()
    {
        var keep = SelectedServer is null ? servers.Selected : SelectedServer.Server;
        _quietServerChange = true;
        ServerChoices.Clear();
        ServerChoices.Add(new ServerChoice(null, Loc.T("Assistant_General")));
        foreach (var s in servers.Items)
        {
            ServerChoices.Add(new ServerChoice(s, s.Name));
        }

        SelectedServer = ServerChoices.FirstOrDefault(c => keep is not null && c.Server == keep) ?? ServerChoices[0];
        _quietServerChange = false;
    }

    private void SelectServer(string? id)
    {
        _quietServerChange = true;
        SelectedServer = ServerChoices.FirstOrDefault(c => c.Server?.Definition.Id == id) ?? ServerChoices.FirstOrDefault();
        _quietServerChange = false;
    }

    private void LoadChat(string id)
    {
        StartNewChat();
        _chatId = id;
        if (_store.Load(id) is { } chat)
        {
            SelectServer(chat.ServerId);
            var dir = _store.AttachmentDir(id);
            foreach (var l in chat.Lines)
            {
                Messages.Add(new ChatItem(l.Role, l.Text)
                {
                    Files = l.Files?.Select(f => new ChatFile(f, Path.Combine(dir, f), AttachmentLoader.IsImageName(f))).ToList(),
                });
            }
        }
    }

    private void StartNewChat()
    {
        _cts?.Cancel();
        _conversation = null;
        Messages.Clear();
        ClearTodo();
        Tokens = 0;
        TokensEstimated = false;
        _chatId = ChatStore.NewId();
    }

    /// <summary>Saves the open chat; a chat without a user message is not written, so "New chat" leaves no empty entries.</summary>
    private void SaveChat()
    {
        if (_chatId is null || !Messages.Any(m => m.Role == "user"))
        {
            return;
        }

        try
        {
            var lines = Messages.Where(m => m.Role is "user" or "assistant" or "system" or "done")
                .Select(m => new SavedChatLine(m.Role, m.Text, m.Files?.Select(f => f.Name).ToList())).ToList();
            var title = _store.Load(_chatId) is { Title.Length: > 0 } existing ? existing.Title : ChatStore.MakeTitle(lines);
            _store.Save(new SavedChat(_chatId, title, Target?.Definition.Id, DateTimeOffset.Now, lines));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        if (IsHistoryOpen)
        {
            RefreshHistory();
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
        if (!IsBusy)
        {
            if (Messages.Count > 0)
            {
                StartNewChat();
            }

            if (servers.Selected is { } selected)
            {
                SelectServer(selected.Definition.Id);
            }
        }

        Input = text;
        if (IsConfigured && SendCommand.CanExecute(null))
        {
            SendCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void NewChat()
    {
        StartNewChat();
        PendingFiles.Clear();
        IsHistoryOpen = false;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>One-click context: puts the selected server's recent console into the prompt.</summary>
    private bool CanAttachConsole() => Target is not null;

    [RelayCommand(CanExecute = nameof(CanAttachConsole))]
    private void AttachConsole()
    {
        if (Target is not { } s)
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
        var files = PendingFiles.ToList();
        if (text.Length == 0 && files.Count == 0)
        {
            return;
        }

        if (text.Length == 0)
        {
            text = Loc.T("Attach_DefaultPrompt");
        }

        if (!IsConfigured)
        {
            MainViewModel.Shell?.Toast(Loc.T("Assistant_NotConfigured"), isError: true);
            return;
        }

        _chatId ??= ChatStore.NewId();
        Input = string.Empty;
        PendingFiles.Clear();
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
                _conversation.Retrying = (attempt, wait) => dispatcher.BeginInvoke(() => StatusText = Loc.F("Assistant_Retrying", attempt, (int)Math.Ceiling(wait.TotalSeconds)));
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

            var stored = new List<ChatFile>();
            foreach (var f in files)
            {
                try
                {
                    var name = _store.SaveAttachment(_chatId, f.Name, f.Data);
                    stored.Add(new ChatFile(name, Path.Combine(_store.AttachmentDir(_chatId), name), f.IsImage));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    stored.Add(new ChatFile(f.Name, string.Empty, f.IsImage));
                }
            }

            Messages.Add(new ChatItem("user", text) { ModeTag = Mode == AssistantMode.Normal ? null : Loc.T("Mode_" + Mode), Files = stored.Count > 0 ? stored : null });
            var prompt = AssistantPolicy.Instruction(Mode) + "\n\n" + text;
            foreach (var f in files.Where(f => !f.IsImage))
            {
                prompt += $"\n\nAttached file {f.Name}:\n```\n{f.Text}\n```";
            }

            var images = files.Where(f => f.IsImage).Select(f => new AiImage(f.MediaType, f.Data)).ToList();
            var step = await _conversation.SendAsync(prompt, images, _cts.Token).ConfigureAwait(true);
            var nudges = 0;
            var completed = false;
            var maxRounds = AssistantPolicy.MaxRounds(Mode);
            var round = 0;
            for (; round < maxRounds; round++)
            {
                StatusText = Loc.F("Assistant_Working", round + 1);
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
                    // Like Cline: an answer that stops while the task list still has open items (or that was cut off) gets a nudge to carry on.
                    var open = Todo.Where(t => !t.IsDone).Select(t => t.Text).ToList();
                    var cut = step.StopReason is "max_tokens" or "length";
                    if (!completed && Mode != AssistantMode.Plan && nudges < 3 && (open.Count > 0 || cut))
                    {
                        nudges++;
                        Messages.Add(new ChatItem("system", Loc.T("Assistant_Continuing")));
                        var nudge = cut
                            ? "Your last answer was cut off. Continue exactly where you stopped."
                            : "Continue the task without stopping. Open items in your task list: " + string.Join("; ", open.Take(30)) + ". Work through them, update the list, and call task_complete when everything is done. If you truly need the user's input, say so clearly and ask one question.";
                        step = await _conversation.SendAsync(nudge, [], _cts.Token).ConfigureAwait(true);
                        continue;
                    }

                    if (Mode == AssistantMode.Plan && step.Text.Length > 0)
                    {
                        Messages.Add(new ChatItem("plan", string.Empty));
                    }

                    break;
                }

                nudges = 0;
                var results = new List<(AiToolCall, string, bool)>();
                foreach (var call in step.ToolCalls)
                {
                    if (call.Name == "task_complete")
                    {
                        completed = true;
                        Messages.Add(new ChatItem("done", call.Arguments["summary"]?.ToString() ?? string.Empty));
                    }

                    results.Add(await RunToolAsync(call).ConfigureAwait(true));
                }

                step = await _conversation.ContinueAsync(results, _cts.Token).ConfigureAwait(true);
            }

            if (round >= maxRounds)
            {
                Messages.Add(new ChatItem("system", Loc.F("Assistant_RoundLimit", maxRounds)));
            }
        }
        catch (OperationCanceledException)
        {
            Messages.Add(new ChatItem("system", Loc.T("Assistant_Cancelled")));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or Anthropic.Exceptions.AnthropicApiException or System.Text.Json.JsonException)
        {
            Messages.Add(new ChatItem("system", Loc.F("Assistant_Error", ex.Message)));
            if (files.Any(f => f.IsImage))
            {
                Messages.Add(new ChatItem("system", Loc.T("Attach_VisionHint")));
            }
        }
        finally
        {
            if (live is not null)
            {
                live.IsStreaming = false;
            }

            IsBusy = false;
            StatusText = string.Empty;
            SaveChat();
        }
    }

    private async Task<(AiToolCall, string, bool)> RunToolAsync(AiToolCall call)
    {
        try
        {
            var decision0 = AssistantPolicy.Decide(Mode, call.Name);
            if (decision0 == ToolDecision.Block)
            {
                Messages.Add(new ChatItem("tool", Loc.F("Mode_Blocked", call.Name)) { Result = Loc.T("Mode_BlockedDetail") });
                return (call, "Plan mode: this tool changes something and is not allowed. Describe the change in your plan instead.", true);
            }

            var (result, pending) = await Tools.RunAsync(call).ConfigureAwait(true);
            if (pending is null)
            {
                if (call.Name is not ("update_todo" or "task_complete"))
                {
                    Messages.Add(ToolRow(call, result ?? string.Empty));
                }

                return (call, result ?? string.Empty, false);
            }

            if (decision0 == ToolDecision.Run)
            {
                // Auto / Bypass: apply now, but still show exactly what changed.
                var output = await pending.Execute().ConfigureAwait(true);
                Messages.Add(new ChatItem("tool", Loc.F("Mode_Applied", pending.Title)) { Arguments = pending.Preview, Result = output });
                return (call, output, false);
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
            "find_files" => Loc.F("Tool_FindFiles", Arg("pattern")),
            "search_files" => Loc.F("Tool_SearchFiles", Arg("text")),
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
