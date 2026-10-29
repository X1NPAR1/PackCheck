using System.Text.Json;
using System.Text.Json.Serialization;

namespace PackCheck.Servers;

[JsonConverter(typeof(JsonStringEnumConverter<AutomationKind>))]
public enum AutomationKind
{
    /// <summary>Start the server when PackCheck starts (PackCheck itself can start with Windows).</summary>
    StartWithApp,

    /// <summary>Start the server again after a crash, at most N times within an hour.</summary>
    RestartOnCrash,

    /// <summary>Restart at a time of day, warning players before.</summary>
    ScheduledRestart,

    /// <summary>Zip backup at a time of day.</summary>
    ScheduledBackup,

    /// <summary>Run a console command at a time of day or every N minutes.</summary>
    ScheduledCommand,

    /// <summary>Send a chat message every N minutes.</summary>
    Announcement,

    /// <summary>TPS below a threshold for N minutes: warn or restart.</summary>
    LowTps,

    /// <summary>Memory above a threshold (percent of max heap) for N minutes: warn or restart.</summary>
    HighMemory,

    /// <summary>Stop the server when nobody has been online for N minutes.</summary>
    StopWhenEmpty,

    /// <summary>When this or another server starts, stops or crashes: do something (after an optional delay).</summary>
    WhenServer,

    /// <summary>When the player count reaches a number: do something once, again after it drops below.</summary>
    WhenPlayers,
}

[JsonConverter(typeof(JsonStringEnumConverter<ServerEvent>))]
public enum ServerEvent
{
    Started,
    Stopped,
    Crashed,
}

/// <summary>What a trigger rule does.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RuleAction>))]
public enum RuleAction
{
    Start,
    Stop,
    Restart,
    Backup,
    Command,
    Say,
    Notify,
    Discord,
}

[JsonConverter(typeof(JsonStringEnumConverter<AutomationReaction>))]
public enum AutomationReaction
{
    Notify,
    Restart,
}

/// <summary>One rule. Unused fields are ignored for kinds that do not need them.</summary>
public sealed record AutomationRule
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public required AutomationKind Kind { get; init; }
    public bool Enabled { get; init; } = true;

    /// <summary>Time of day ("04:00") for scheduled kinds; null means "every <see cref="IntervalMinutes"/>".</summary>
    public string? At { get; init; }

    /// <summary>Days the scheduled rule runs; empty = every day.</summary>
    public IReadOnlyList<DayOfWeek> Days { get; init; } = [];
    public int IntervalMinutes { get; init; } = 30;
    public string? Command { get; init; }
    public string? Message { get; init; }
    public double Threshold { get; init; }
    public int ForMinutes { get; init; } = 5;
    public int MaxRetries { get; init; } = 3;
    public int WarningSeconds { get; init; } = 60;
    public AutomationReaction Reaction { get; init; } = AutomationReaction.Notify;

    /// <summary>Trigger rules: which server to watch (null = this one) and for what.</summary>
    public string? SourceServerId { get; init; }
    public ServerEvent SourceEvent { get; init; } = ServerEvent.Started;

    /// <summary>Trigger rules: what to do, and how long to wait first.</summary>
    public RuleAction Action { get; init; } = RuleAction.Start;
    public int DelaySeconds { get; init; }

    public static AutomationRule Default(AutomationKind kind) => kind switch
    {
        AutomationKind.ScheduledRestart => new() { Kind = kind, At = "04:00", WarningSeconds = 60 },
        AutomationKind.ScheduledBackup => new() { Kind = kind, At = "03:30" },
        AutomationKind.ScheduledCommand => new() { Kind = kind, At = null, IntervalMinutes = 60, Command = "save-all" },
        AutomationKind.Announcement => new() { Kind = kind, IntervalMinutes = 30, Message = "Welcome! Type /help for commands." },
        AutomationKind.LowTps => new() { Kind = kind, Threshold = 15, ForMinutes = 5, Reaction = AutomationReaction.Notify },
        AutomationKind.HighMemory => new() { Kind = kind, Threshold = 90, ForMinutes = 10, Reaction = AutomationReaction.Notify },
        AutomationKind.StopWhenEmpty => new() { Kind = kind, ForMinutes = 30 },
        AutomationKind.RestartOnCrash => new() { Kind = kind, MaxRetries = 3 },
        AutomationKind.WhenServer => new() { Kind = kind, SourceEvent = ServerEvent.Started, Action = RuleAction.Start, DelaySeconds = 10 },
        AutomationKind.WhenPlayers => new() { Kind = kind, Threshold = 10, Action = RuleAction.Say, Message = "The server is busy – thanks for playing!" },
        _ => new() { Kind = kind },
    };
}

/// <summary>What the server looks like right now, as far as the rules care.</summary>
public sealed record AutomationSnapshot(ServerState State, double? Tps, double? MemoryPercent, int Players);

public enum AutomationActionKind
{
    Start,
    Stop,
    Restart,
    Backup,
    Command,
    Say,
    Notify,
    Discord,
}

/// <summary>Something the app should do now because of a rule.</summary>
public sealed record AutomationAction(AutomationActionKind Kind, string RuleId, string Reason, string? Text = null, int WarningSeconds = 0)
{
    /// <summary>Wait this long before doing it (trigger rules).</summary>
    public int DelaySeconds { get; init; }
}

/// <summary>
/// Decides, from the rules and the server's state, what to do at a given moment. Pure: no timers or I/O,
/// so every rule can be tested with a fake clock. The app calls <see cref="Tick"/> every few seconds and
/// <see cref="OnAppStarted"/> once.
/// </summary>
public sealed class AutomationEngine
{
    private readonly Dictionary<string, DateTimeOffset> _lastFired = [];
    private readonly Dictionary<string, DateTimeOffset> _conditionSince = [];
    private readonly List<DateTimeOffset> _crashRestarts = [];
    private ServerState _lastState = ServerState.Stopped;
    private DateTimeOffset? _emptySince;
    private bool _stoppedByUser;

    public IReadOnlyList<AutomationRule> Rules { get; set; } = [];

    /// <summary>When the user presses Stop, crash and idle rules must not fight them.</summary>
    public void UserStopped() => _stoppedByUser = true;

    public void UserStarted() => _stoppedByUser = false;

    private readonly HashSet<string> _playersFired = [];

    /// <summary>
    /// Another server (or this one, <paramref name="selfId"/>) started, stopped or crashed.
    /// Returns the actions of matching "when server" rules.
    /// </summary>
    public IReadOnlyList<AutomationAction> OnServerEvent(string sourceId, string selfId, ServerEvent ev) =>
        Rules.Where(r => r.Enabled && r.Kind == AutomationKind.WhenServer && r.SourceEvent == ev && (r.SourceServerId ?? selfId) == sourceId)
            // "This server started -> start this server" would do nothing; skip it.
            .Where(r => !(sourceId == selfId && ev == ServerEvent.Started && r.Action == RuleAction.Start))
            .Select(r => ToAction(r, $"{ev.ToString().ToLowerInvariant()}:{sourceId}"))
            .ToList();

    public static AutomationAction ToAction(AutomationRule r, string reason) => new(r.Action switch
    {
        RuleAction.Start => AutomationActionKind.Start,
        RuleAction.Stop => AutomationActionKind.Stop,
        RuleAction.Restart => AutomationActionKind.Restart,
        RuleAction.Backup => AutomationActionKind.Backup,
        RuleAction.Command => AutomationActionKind.Command,
        RuleAction.Say => AutomationActionKind.Say,
        RuleAction.Discord => AutomationActionKind.Discord,
        _ => AutomationActionKind.Notify,
    }, r.Id, reason, r.Action switch
    {
        RuleAction.Command => r.Command?.Trim().TrimStart('/'),
        RuleAction.Say or RuleAction.Notify or RuleAction.Discord => r.Message,
        _ => null,
    }, r.Action == RuleAction.Restart ? Math.Max(0, r.WarningSeconds) : 0)
    { DelaySeconds = Math.Max(0, r.DelaySeconds) };

    public IReadOnlyList<AutomationAction> OnAppStarted(AutomationSnapshot s) =>
        Rules.Where(r => r.Enabled && r.Kind == AutomationKind.StartWithApp && s.State is ServerState.Stopped or ServerState.Crashed)
            .Take(1).Select(r => new AutomationAction(AutomationActionKind.Start, r.Id, "PackCheck started")).ToList();

    public IReadOnlyList<AutomationAction> Tick(DateTimeOffset now, AutomationSnapshot s)
    {
        var actions = new List<AutomationAction>();
        var crashedNow = s.State == ServerState.Crashed && _lastState != ServerState.Crashed;
        var running = s.State == ServerState.Running;
        _lastState = s.State;
        if (running)
        {
            _stoppedByUser = false;
        }

        foreach (var r in Rules.Where(r => r.Enabled))
        {
            switch (r.Kind)
            {
                case AutomationKind.RestartOnCrash when crashedNow && !_stoppedByUser:
                    _crashRestarts.RemoveAll(t => now - t > TimeSpan.FromHours(1));
                    if (_crashRestarts.Count < Math.Max(1, r.MaxRetries))
                    {
                        _crashRestarts.Add(now);
                        actions.Add(new(AutomationActionKind.Start, r.Id, $"crash restart {_crashRestarts.Count}/{r.MaxRetries}"));
                    }
                    else
                    {
                        actions.Add(new(AutomationActionKind.Notify, r.Id, "crash restart limit reached", $"Crashed {r.MaxRetries} times within an hour; not restarting again."));
                    }

                    break;

                case AutomationKind.ScheduledRestart when running && Due(r, now):
                    actions.Add(new(AutomationActionKind.Restart, r.Id, "scheduled restart", WarningSeconds: Math.Max(0, r.WarningSeconds)));
                    break;

                case AutomationKind.ScheduledBackup when Due(r, now):
                    actions.Add(new(AutomationActionKind.Backup, r.Id, "scheduled backup"));
                    break;

                case AutomationKind.ScheduledCommand when running && !string.IsNullOrWhiteSpace(r.Command) && Due(r, now):
                    actions.Add(new(AutomationActionKind.Command, r.Id, "scheduled command", r.Command.Trim().TrimStart('/')));
                    break;

                case AutomationKind.Announcement when running && !string.IsNullOrWhiteSpace(r.Message) && Due(r, now):
                    actions.Add(new(AutomationActionKind.Say, r.Id, "announcement", r.Message.Trim()));
                    break;

                case AutomationKind.LowTps:
                    Threshold(r, now, running && s.Tps is { } tps && tps < r.Threshold, $"TPS below {r.Threshold:0.#}", actions);
                    break;

                case AutomationKind.HighMemory:
                    Threshold(r, now, running && s.MemoryPercent is { } m && m > r.Threshold, $"memory above {r.Threshold:0}%", actions);
                    break;

                case AutomationKind.WhenPlayers:
                    if (running && s.Players >= Math.Max(1, (int)r.Threshold))
                    {
                        if (_playersFired.Add(r.Id))
                        {
                            actions.Add(ToAction(r, $"players {s.Players}"));
                        }
                    }
                    else
                    {
                        _playersFired.Remove(r.Id);
                    }

                    break;

                case AutomationKind.StopWhenEmpty:
                    if (!running || s.Players > 0)
                    {
                        _emptySince = running ? null : _emptySince;
                        if (s.Players > 0)
                        {
                            _emptySince = null;
                        }

                        break;
                    }

                    _emptySince ??= now;
                    if (now - _emptySince >= TimeSpan.FromMinutes(Math.Max(1, r.ForMinutes)))
                    {
                        _emptySince = null;
                        actions.Add(new(AutomationActionKind.Stop, r.Id, $"no players for {r.ForMinutes} min"));
                    }

                    break;
            }
        }

        return actions;
    }

    /// <summary>Condition must hold for <see cref="AutomationRule.ForMinutes"/>; then it fires once and waits for the condition to clear.</summary>
    private void Threshold(AutomationRule r, DateTimeOffset now, bool holds, string what, List<AutomationAction> actions)
    {
        if (!holds)
        {
            _conditionSince.Remove(r.Id);
            return;
        }

        if (!_conditionSince.TryGetValue(r.Id, out var since))
        {
            _conditionSince[r.Id] = now;
            return;
        }

        if (since == DateTimeOffset.MaxValue || now - since < TimeSpan.FromMinutes(Math.Max(1, r.ForMinutes)))
        {
            return;
        }

        _conditionSince[r.Id] = DateTimeOffset.MaxValue; // fired; re-arm only after the condition clears
        actions.Add(r.Reaction == AutomationReaction.Restart
            ? new(AutomationActionKind.Restart, r.Id, $"{what} for {r.ForMinutes} min", WarningSeconds: 30)
            : new(AutomationActionKind.Notify, r.Id, $"{what} for {r.ForMinutes} min", $"{what} for {r.ForMinutes} minutes."));
    }

    /// <summary>Fixed time of day (once per day, on the chosen days) or every N minutes.</summary>
    private bool Due(AutomationRule r, DateTimeOffset now)
    {
        _lastFired.TryGetValue(r.Id, out var last);
        if (r.At is { } at && TimeOnly.TryParse(at, System.Globalization.CultureInfo.InvariantCulture, out var time))
        {
            if (r.Days.Count > 0 && !r.Days.Contains(now.DayOfWeek))
            {
                return false;
            }

            var todayAt = new DateTimeOffset(now.Date + time.ToTimeSpan(), now.Offset);

            // Fire within 10 minutes after the set time (covers a sleeping PC or a slow tick), once per day.
            if (now < todayAt || now - todayAt > TimeSpan.FromMinutes(10) || last.Date == now.Date && last >= todayAt)
            {
                return false;
            }
        }
        else if (last != default && now - last < TimeSpan.FromMinutes(Math.Max(1, r.IntervalMinutes)))
        {
            return false;
        }
        else if (last == default)
        {
            // Interval rules wait one interval after start instead of firing immediately.
            _lastFired[r.Id] = now;
            return false;
        }

        _lastFired[r.Id] = now;
        return true;
    }

    /// <summary>When a rule will run next (for the UI), or null when it is not time based.</summary>
    public DateTimeOffset? NextRun(AutomationRule r, DateTimeOffset now)
    {
        if (r.Kind is not (AutomationKind.ScheduledRestart or AutomationKind.ScheduledBackup or AutomationKind.ScheduledCommand or AutomationKind.Announcement))
        {
            return null;
        }

        if (r.At is { } at && TimeOnly.TryParse(at, System.Globalization.CultureInfo.InvariantCulture, out var time))
        {
            for (var d = 0; d < 8; d++)
            {
                var day = now.Date.AddDays(d);
                var candidate = new DateTimeOffset(day + time.ToTimeSpan(), now.Offset);
                if (candidate > now && (r.Days.Count == 0 || r.Days.Contains(day.DayOfWeek)))
                {
                    return candidate;
                }
            }

            return null;
        }

        return _lastFired.TryGetValue(r.Id, out var last) ? last.AddMinutes(Math.Max(1, r.IntervalMinutes)) : now.AddMinutes(Math.Max(1, r.IntervalMinutes));
    }
}

/// <summary>Rules and the event log live next to the server, in .packcheck/automation.json.</summary>
public static class AutomationStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public sealed record Data(IReadOnlyList<AutomationRule> Rules, IReadOnlyList<AutomationEvent> Events);

    public static string FileOf(ServerDefinition s) => Path.Combine(s.Directory, ".packcheck", "automation.json");

    public static Data Load(ServerDefinition s)
    {
        try
        {
            var f = FileOf(s);
            return File.Exists(f) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(f), Json) ?? new([], []) : new([], []);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new([], []);
        }
    }

    public static void Save(ServerDefinition s, Data data)
    {
        var f = FileOf(s);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        var tmp = f + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data with { Events = data.Events.TakeLast(300).ToList() }, Json));
        File.Move(tmp, f, overwrite: true);
    }
}

public sealed record AutomationEvent(DateTimeOffset At, string RuleId, string Kind, string Text, bool Ok);
