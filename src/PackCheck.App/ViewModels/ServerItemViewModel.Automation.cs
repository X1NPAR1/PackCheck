using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackCheck.App.Infrastructure;
using PackCheck.Servers;

namespace PackCheck.App.ViewModels;

/// <summary>Editable view of one automation rule; every change is saved at once.</summary>
public sealed partial class AutomationRuleRow : ObservableObject
{
    private readonly Action _changed;

    public AutomationRuleRow(AutomationRule rule, Action changed)
    {
        _changed = changed;
        Id = rule.Id;
        Kind = rule.Kind;
        _enabled = rule.Enabled;
        _useTime = rule.At is not null;
        _at = rule.At ?? "04:00";
        _intervalMinutes = rule.IntervalMinutes;
        _command = rule.Command ?? string.Empty;
        _message = rule.Message ?? string.Empty;
        _threshold = rule.Threshold;
        _forMinutes = rule.ForMinutes;
        _maxRetries = rule.MaxRetries;
        _warningSeconds = rule.WarningSeconds;
        _restart = rule.Reaction == AutomationReaction.Restart;
        // Keep the saved source even when that server is not loaded (yet), so saving never changes it silently.
        _sourceServer = ServerOptions.FirstOrDefault(o => o.Id == rule.SourceServerId)
            ?? (rule.SourceServerId is null ? ServerOptions[0] : new ServerOption(rule.SourceServerId, rule.SourceServerId));
        _sourceEvent = rule.SourceEvent;
        _action = rule.Action;
        _delaySeconds = rule.DelaySeconds;
        foreach (var d in DayOrder)
        {
            var day = new DayToggle(d, rule.Days.Contains(d), Changed);
            Days.Add(day);
        }
    }

    private static readonly DayOfWeek[] DayOrder = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public string Id { get; }

    public AutomationKind Kind { get; }

    public string Title => Loc.T("Auto_" + Kind);

    public string Description => Loc.T("Auto_" + Kind + "_Hint");

    public ObservableCollection<DayToggle> Days { get; } = [];

    public bool HasSchedule => Kind is AutomationKind.ScheduledRestart or AutomationKind.ScheduledBackup or AutomationKind.ScheduledCommand;

    public bool CanUseInterval => Kind is AutomationKind.ScheduledCommand;

    public bool HasInterval => Kind is AutomationKind.Announcement || (Kind is AutomationKind.ScheduledCommand && !UseTime);

    public bool HasTime => HasSchedule && (UseTime || Kind is not AutomationKind.ScheduledCommand);

    public bool HasCommand => Kind is AutomationKind.ScheduledCommand;

    public bool HasMessage => Kind is AutomationKind.Announcement;

    public bool HasThreshold => Kind is AutomationKind.LowTps or AutomationKind.HighMemory or AutomationKind.WhenPlayers;

    public bool HasReaction => Kind is AutomationKind.LowTps or AutomationKind.HighMemory;

    public bool HasDuration => Kind is AutomationKind.LowTps or AutomationKind.HighMemory or AutomationKind.StopWhenEmpty;

    public bool HasRetries => Kind is AutomationKind.RestartOnCrash;

    public bool HasWarning => Kind is AutomationKind.ScheduledRestart;

    public string ThresholdLabel => Loc.T(Kind switch { AutomationKind.LowTps => "Auto_TpsBelow", AutomationKind.WhenPlayers => "Auto_PlayersAtLeast", _ => "Auto_MemoryAbove" });

    public bool IsTrigger => Kind is AutomationKind.WhenServer or AutomationKind.WhenPlayers;

    public bool HasSource => Kind is AutomationKind.WhenServer;

    /// <summary>Command / message boxes for trigger rules depend on the chosen action.</summary>
    public bool ShowsCommand => HasCommand || (IsTrigger && Action == RuleAction.Command);

    public bool ShowsMessage => HasMessage || (IsTrigger && Action is RuleAction.Say or RuleAction.Notify or RuleAction.Discord);

    /// <summary>"This server" first, then every other server PackCheck manages.</summary>
    public static IReadOnlyList<ServerOption> ServerOptions =>
        [new(null, Loc.T("Auto_ThisServer")), .. (MainViewModel.Shell?.Servers.Items ?? []).Select(s => new ServerOption(s.Definition.Id, s.Name))];

    public IReadOnlyList<ServerOption> SourceChoices
    {
        get
        {
            var list = ServerOptions.ToList();
            if (SourceServer is { Id: not null } current && list.All(o => o.Id != current.Id))
            {
                list.Add(current);
            }

            // Same instance as SourceServer for the one that is selected, so the ComboBox shows it.
            return list.Select(o => o.Id == SourceServer?.Id ? SourceServer! : o).ToList();
        }
    }

    public IReadOnlyList<ServerEvent> Events { get; } = Enum.GetValues<ServerEvent>();

    public IReadOnlyList<RuleAction> Actions { get; } = Enum.GetValues<RuleAction>();

    [ObservableProperty]
    private ServerOption _sourceServer;

    [ObservableProperty]
    private ServerEvent _sourceEvent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsCommand), nameof(ShowsMessage))]
    private RuleAction _action;

    [ObservableProperty]
    private double _delaySeconds;

    partial void OnSourceServerChanged(ServerOption value) => Changed();

    partial void OnSourceEventChanged(ServerEvent value) => Changed();

    partial void OnActionChanged(RuleAction value) => Changed();

    partial void OnDelaySecondsChanged(double value) => Changed();

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInterval), nameof(HasTime))]
    private bool _useTime;

    [ObservableProperty]
    private string _at;

    [ObservableProperty]
    private double _intervalMinutes;

    [ObservableProperty]
    private string _command;

    [ObservableProperty]
    private string _message;

    [ObservableProperty]
    private double _threshold;

    [ObservableProperty]
    private double _forMinutes;

    [ObservableProperty]
    private double _maxRetries;

    [ObservableProperty]
    private double _warningSeconds;

    [ObservableProperty]
    private bool _restart;

    [ObservableProperty]
    private string? _lastRun;

    [ObservableProperty]
    private string? _nextRun;

    /// <summary>"04:00" style; invalid input is shown in red and not saved.</summary>
    public bool TimeValid => TimeOnly.TryParse(At, System.Globalization.CultureInfo.InvariantCulture, out _);

    partial void OnAtChanged(string value)
    {
        OnPropertyChanged(nameof(TimeValid));
        Changed();
    }

    partial void OnEnabledChanged(bool value) => Changed();

    partial void OnUseTimeChanged(bool value) => Changed();

    partial void OnIntervalMinutesChanged(double value) => Changed();

    partial void OnCommandChanged(string value) => Changed();

    partial void OnMessageChanged(string value) => Changed();

    partial void OnThresholdChanged(double value) => Changed();

    partial void OnForMinutesChanged(double value) => Changed();

    partial void OnMaxRetriesChanged(double value) => Changed();

    partial void OnWarningSecondsChanged(double value) => Changed();

    partial void OnRestartChanged(bool value) => Changed();

    private void Changed() => _changed();

    public AutomationRule ToRule() => new()
    {
        Id = Id,
        Kind = Kind,
        Enabled = Enabled,
        At = HasSchedule && (UseTime || Kind != AutomationKind.ScheduledCommand) && TimeValid ? TimeOnly.Parse(At, System.Globalization.CultureInfo.InvariantCulture).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) : null,
        Days = Days.Where(d => d.On).Select(d => d.Day).ToList(),
        IntervalMinutes = (int)Math.Max(1, IntervalMinutes),
        Command = Command,
        Message = Message,
        Threshold = Threshold,
        ForMinutes = (int)Math.Max(1, ForMinutes),
        MaxRetries = (int)Math.Max(1, MaxRetries),
        WarningSeconds = (int)Math.Max(0, WarningSeconds),
        Reaction = Restart ? AutomationReaction.Restart : AutomationReaction.Notify,
        SourceServerId = SourceServer?.Id,
        SourceEvent = SourceEvent,
        Action = Action,
        DelaySeconds = (int)Math.Max(0, DelaySeconds),
    };
}

public sealed partial class DayToggle(DayOfWeek day, bool on, Action changed) : ObservableObject
{
    public DayOfWeek Day { get; } = day;

    public string Label => Loc.Instance.Culture.DateTimeFormat.GetShortestDayName(Day);

    [ObservableProperty]
    private bool _on = on;

    partial void OnOnChanged(bool value) => changed();
}

public sealed record AutomationEventRow(string Time, string Rule, string Text, bool Ok);

public sealed record ServerOption(string? Id, string Name)
{
    public override string ToString() => Name;
}

public sealed partial class ServerItemViewModel
{
    private readonly AutomationEngine _automation = new();
    private DispatcherTimer? _automationTimer;
    private bool _automationBusy;

    public ObservableCollection<AutomationRuleRow> AutomationRules { get; } = [];

    /// <summary>Every server announces start / stop / crash; every server's "when server" rules listen.</summary>
    private static event Action<string, ServerEvent>? ServerEventRaised;

    private ServerState _announcedState = ServerState.Stopped;

    /// <summary>Called on every state change; turns transitions into Started / Stopped / Crashed events.</summary>
    private void AnnounceState(ServerState value)
    {
        ServerEvent? ev = value switch
        {
            ServerState.Running when _announcedState != ServerState.Running => ServerEvent.Started,
            ServerState.Stopped when _announcedState is ServerState.Running or ServerState.Stopping => ServerEvent.Stopped,
            ServerState.Crashed when _announcedState != ServerState.Crashed => ServerEvent.Crashed,
            _ => null,
        };
        _announcedState = value;
        if (ev is { } e)
        {
            ServerEventRaised?.Invoke(Definition.Id, e);
        }
    }

    private async void OnServerEvent(string sourceId, ServerEvent ev)
    {
        foreach (var a in _automation.OnServerEvent(sourceId, Definition.Id, ev))
        {
            await RunAutomationAsync(a).ConfigureAwait(true);
        }
    }

    public ObservableCollection<AutomationEventRow> AutomationEvents { get; } = [];

    private List<AutomationEvent> _automationLog = [];

    public IReadOnlyList<AutomationKind> AutomationKinds { get; } = Enum.GetValues<AutomationKind>();

    public int ActiveRuleCount => AutomationRules.Count(r => r.Enabled);

    /// <summary>Loads rules and starts the 15-second automation tick (runs while the app runs, even when the server is stopped).</summary>
    private void InitAutomation()
    {
        var data = AutomationStore.Load(Definition);
        _automationLog = [.. data.Events];
        foreach (var r in data.Rules)
        {
            AutomationRules.Add(new AutomationRuleRow(r, SaveAutomation));
        }

        foreach (var e in _automationLog.TakeLast(100).Reverse())
        {
            AutomationEvents.Add(ToRow(e));
        }

        _automation.Rules = data.Rules;
        ServerEventRaised += OnServerEvent;
        _automationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _automationTimer.Tick += async (_, _) => await AutomationTickAsync().ConfigureAwait(true);
        _automationTimer.Start();
        RefreshNextRuns();
    }

    private AutomationEventRow ToRow(AutomationEvent e) =>
        new(e.At.LocalDateTime.ToString("g", Loc.Instance.Culture), AutomationRules.FirstOrDefault(r => r.Id == e.RuleId)?.Title ?? e.Kind, e.Text, e.Ok);

    private void SaveAutomation()
    {
        var rules = AutomationRules.Select(r => r.ToRule()).ToList();
        _automation.Rules = rules;
        try
        {
            AutomationStore.Save(Definition, new(rules, _automationLog));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MainViewModel.Shell?.Toast(ex.Message, isError: true);
        }

        OnPropertyChanged(nameof(ActiveRuleCount));
        RefreshNextRuns();
    }

    private void RefreshNextRuns()
    {
        var now = DateTimeOffset.Now;
        foreach (var row in AutomationRules)
        {
            var next = row.Enabled ? _automation.NextRun(row.ToRule(), now) : null;
            row.NextRun = next is { } n ? Loc.F("Auto_Next", n.LocalDateTime.ToString("g", Loc.Instance.Culture)) : null;
            var last = _automationLog.LastOrDefault(e => e.RuleId == row.Id);
            row.LastRun = last is null ? null : Loc.F("Auto_Last", last.At.LocalDateTime.ToString("g", Loc.Instance.Culture), last.Text);
        }
    }

    [RelayCommand]
    private void AddRule(AutomationKind kind)
    {
        AutomationRules.Add(new AutomationRuleRow(AutomationRule.Default(kind), SaveAutomation));
        SaveAutomation();
    }

    /// <summary>
    /// One click for the usual home-server setup: start with PackCheck, restart after crashes,
    /// back up at 03:30 and restart at 04:00. Kinds that already exist are not added twice.
    /// </summary>
    [RelayCommand]
    private void AddRecommended()
    {
        foreach (var kind in new[] { AutomationKind.StartWithApp, AutomationKind.RestartOnCrash, AutomationKind.ScheduledBackup, AutomationKind.ScheduledRestart })
        {
            if (AutomationRules.All(r => r.Kind != kind))
            {
                AutomationRules.Add(new AutomationRuleRow(AutomationRule.Default(kind), SaveAutomation));
            }
        }

        SaveAutomation();
    }

    [RelayCommand]
    private void RemoveRule(AutomationRuleRow? row)
    {
        if (row is not null && AutomationRules.Remove(row))
        {
            SaveAutomation();
        }
    }

    /// <summary>"Run now" on a rule: performs its action once, the same way the schedule would.</summary>
    [RelayCommand]
    private Task RunRuleNowAsync(AutomationRuleRow? row)
    {
        if (row is null)
        {
            return Task.CompletedTask;
        }

        var r = row.ToRule();
        var action = r.Kind switch
        {
            AutomationKind.StartWithApp or AutomationKind.RestartOnCrash => new AutomationAction(AutomationActionKind.Start, r.Id, "run now"),
            AutomationKind.ScheduledRestart => new AutomationAction(AutomationActionKind.Restart, r.Id, "run now", WarningSeconds: r.WarningSeconds),
            AutomationKind.ScheduledBackup => new AutomationAction(AutomationActionKind.Backup, r.Id, "run now"),
            AutomationKind.ScheduledCommand => new AutomationAction(AutomationActionKind.Command, r.Id, "run now", r.Command?.Trim().TrimStart('/')),
            AutomationKind.Announcement => new AutomationAction(AutomationActionKind.Say, r.Id, "run now", r.Message),
            AutomationKind.StopWhenEmpty => new AutomationAction(AutomationActionKind.Stop, r.Id, "run now"),
            AutomationKind.WhenServer or AutomationKind.WhenPlayers => AutomationEngine.ToAction(r, "run now") with { DelaySeconds = 0 },
            _ => new AutomationAction(AutomationActionKind.Notify, r.Id, "run now", Loc.T("Auto_TestNotify")),
        };
        return RunAutomationAsync(action);
    }

    [RelayCommand]
    private void ClearAutomationLog()
    {
        _automationLog.Clear();
        AutomationEvents.Clear();
        SaveAutomation();
    }

    /// <summary>Called once by the server list after PackCheck started.</summary>
    public async Task OnAppStartedAsync()
    {
        foreach (var a in _automation.OnAppStarted(Snapshot()))
        {
            await RunAutomationAsync(a).ConfigureAwait(true);
        }
    }

    private AutomationSnapshot Snapshot()
    {
        double? tps = State == ServerState.Running && Tps.History.Count > 0 ? Tps.History[^1] : null;
        double? mem = State == ServerState.Running && Ram.History.Count > 0 ? Ram.History[^1] / Math.Max(1, Definition.MaxMemoryMb) * 100 : null;
        return new AutomationSnapshot(State, tps, mem, Players.Count);
    }

    private async Task AutomationTickAsync()
    {
        if (_automationBusy || AutomationRules.Count == 0)
        {
            return;
        }

        _automationBusy = true;
        try
        {
            foreach (var a in _automation.Tick(DateTimeOffset.Now, Snapshot()))
            {
                await RunAutomationAsync(a).ConfigureAwait(true);
            }
        }
        finally
        {
            _automationBusy = false;
        }
    }

    /// <summary>Engine reasons are English keys; show them in the UI language.</summary>
    private static string Reason(string reason) => reason switch
    {
        "PackCheck started" => Loc.T("AutoWhy_AppStarted"),
        "run now" => Loc.T("AutoWhy_RunNow"),
        "scheduled restart" => Loc.T("Auto_ScheduledRestart"),
        _ when reason.StartsWith("crash restart", StringComparison.Ordinal) => Loc.F("AutoWhy_Crash", reason[14..]),
        _ when reason.StartsWith("no players", StringComparison.Ordinal) => Loc.T("Auto_StopWhenEmpty"),
        _ when reason.StartsWith("TPS", StringComparison.Ordinal) => Loc.T("Auto_LowTps"),
        _ when reason.StartsWith("memory", StringComparison.Ordinal) => Loc.T("Auto_HighMemory"),
        _ when reason.StartsWith("players", StringComparison.Ordinal) => Loc.T("Auto_WhenPlayers"),
        _ when reason.Contains(':', StringComparison.Ordinal) => Loc.F("AutoWhy_Event",
            MainViewModel.Shell?.Servers.Items.FirstOrDefault(s => s.Definition.Id == reason[(reason.IndexOf(':') + 1)..])?.Name ?? "?",
            Loc.T("Event_" + char.ToUpperInvariant(reason[0]) + reason[1..reason.IndexOf(':')])),
        _ => reason,
    };

    private async Task RunAutomationAsync(AutomationAction a)
    {
        if (a.DelaySeconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(a.DelaySeconds)).ConfigureAwait(true);
        }

        var ok = true;
        string text;
        try
        {
            switch (a.Kind)
            {
                case AutomationActionKind.Start:
                    if (!IsRunning)
                    {
                        await StartAsync().ConfigureAwait(true);
                    }

                    text = Loc.F("AutoLog_Started", Reason(a.Reason));
                    break;

                case AutomationActionKind.Stop:
                    await _process.StopAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(true);
                    text = Loc.F("AutoLog_Stopped", Reason(a.Reason));
                    break;

                case AutomationActionKind.Restart:
                    if (IsRunning && a.WarningSeconds > 0)
                    {
                        // Warn players at the start, then at 10 seconds left.
                        await _process.SendCommandAsync($"say {Loc.F("AutoSay_Restart", a.WarningSeconds)}").ConfigureAwait(true);
                        if (a.WarningSeconds > 10)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(a.WarningSeconds - 10)).ConfigureAwait(true);
                            await _process.SendCommandAsync($"say {Loc.F("AutoSay_Restart", 10)}").ConfigureAwait(true);
                            await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
                        }
                        else
                        {
                            await Task.Delay(TimeSpan.FromSeconds(a.WarningSeconds)).ConfigureAwait(true);
                        }
                    }

                    await _process.StopAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(true);
                    await StartAsync().ConfigureAwait(true);
                    text = Loc.F("AutoLog_Restarted", Reason(a.Reason));
                    break;

                case AutomationActionKind.Backup:
                    await BackupNowAsync(BackupKind.Full, Loc.T("Backups_AutoNote")).ConfigureAwait(true);
                    text = Loc.T("AutoLog_Backup");
                    break;

                case AutomationActionKind.Command:
                case AutomationActionKind.Say:
                    if (!IsRunning)
                    {
                        ok = false;
                        text = Loc.T("AutoLog_NotRunning");
                        break;
                    }

                    var cmd = a.Kind == AutomationActionKind.Say ? "say " + a.Text : a.Text!;
                    await _process.SendCommandAsync(cmd).ConfigureAwait(true);
                    Add(new ConsoleEntry(DateTime.Now.ToString("HH:mm:ss", Loc.Instance.Culture), "Command", "PackCheck", "> " + cmd, "> " + cmd));
                    text = "/" + cmd;
                    break;

                case AutomationActionKind.Discord:
                    text = a.Text ?? a.Reason;
                    if (!PostDiscord(DiscordEvent.Automation, text, force: true))
                    {
                        throw new InvalidOperationException(Loc.T("Discord_NotSet"));
                    }

                    break;

                default:
                    text = a.Text ?? a.Reason;
                    MainViewModel.Shell?.Toast($"{Name}: {text}", isError: true);
                    App.Tray?.Notify($"{Name}: {text}");
                    break;
            }

            if (a.Kind != AutomationActionKind.Discord)
            {
                PostDiscord(DiscordEvent.Automation, text);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ok = false;
            text = ex.Message;
        }

        var e = new AutomationEvent(DateTimeOffset.Now, a.RuleId, a.Kind.ToString(), text, ok);
        _automationLog.Add(e);
        AutomationEvents.Insert(0, ToRow(e));
        while (AutomationEvents.Count > 100)
        {
            AutomationEvents.RemoveAt(AutomationEvents.Count - 1);
        }

        SaveAutomation();
    }
}
