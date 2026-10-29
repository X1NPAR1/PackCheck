using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackCheck.App.Infrastructure;
using PackCheck.Servers;
using PackCheck.Validation;
using LogLevel = PackCheck.Servers.LogLevel;

namespace PackCheck.App.ViewModels;

public sealed record PluginRow(string File, string? Name, string? Version, bool Enabled);

/// <summary>One parsed console line as shown in the console view.</summary>
public sealed record ConsoleEntry(string? Time, string Level, string? Source, string Message, string Raw);

/// <summary>A live metric shown as a ring gauge.</summary>
public sealed partial class Gauge(string labelKey) : ObservableObject
{
    public string LabelKey { get; } = labelKey;

    public string Label => Loc.T(LabelKey);

    [ObservableProperty]
    private double _fraction;

    [ObservableProperty]
    private string _text = "–";

    [ObservableProperty]
    private string? _detail;

    /// <summary>0 excellent … 4 critical, -1 = no data.</summary>
    [ObservableProperty]
    private int _health = -1;

    [ObservableProperty]
    private string? _unavailableReason;

    public ObservableCollection<double> History { get; } = [];

    public void Set(double fraction, string text, int health, string? detail = null, double? historyValue = null)
    {
        Fraction = Math.Clamp(fraction, 0, 1);
        Text = text;
        Health = health;
        Detail = detail;
        UnavailableReason = null;
        History.Add(historyValue ?? fraction);
        if (History.Count > 150)
        {
            History.RemoveAt(0);
        }
    }

    public void Unavailable(string reason)
    {
        Fraction = 0;
        Text = "n/a";
        Health = -1;
        Detail = null;
        UnavailableReason = reason;
    }

    public void Reset()
    {
        Fraction = 0;
        Text = "–";
        Health = -1;
        Detail = null;
        UnavailableReason = null;
        History.Clear();
    }
}

public sealed partial class ServerItemViewModel : ObservableObject
{
    private const int MaxConsoleLines = 10_000;
    private static readonly TimeSpan TpsWindow = TimeSpan.FromSeconds(3);

    private readonly ServerRegistry _registry;
    private readonly AppSettings _settings;
    private readonly ServerProcess _process;
    private readonly DispatcherTimer _statsTimer;
    private readonly List<ConsoleEntry> _all = [];
    private readonly List<string> _history = [];
    private readonly HashSet<string> _players = new(StringComparer.OrdinalIgnoreCase);
    private int _historyIndex;
    private TimeSpan _lastCpu;
    private DateTime _lastSample;
    private DateTime _startedAt;
    private DateTime _tpsRequestedAt = DateTime.MinValue;
    private int _tick;

    public ServerItemViewModel(ServerDefinition def, ServerRegistry registry, AppSettings settings)
    {
        Definition = def;
        _editRamGb = Math.Clamp(Math.Round(def.MaxMemoryMb / 1024d), 1, 256);
        _editCores = def.CpuCores == 0 ? Environment.ProcessorCount : def.CpuCores;
        _editAikar = def.UseAikarFlags;
        _registry = registry;
        _settings = settings;
        _process = new ServerProcess(def);
        _process.Output += (_, line) => Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => OnOutput(line));
        _process.StateChanged += (_, s) => Application.Current?.Dispatcher.BeginInvoke(() => State = s);
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += async (_, _) => await SampleAsync().ConfigureAwait(true);
        LoadProperties();
        LoadIdentity();
        RefreshBackups();
        RefreshPlugins();
        _ = RefreshDiskAsync();
        InitAutomation();
    }

    public ServerDefinition Definition { get; private set; }

    public string Name => Definition.Name;

    // ----- resources (editable while stopped) -----
    public double SystemRamGb { get; } = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024 / 1024, 1);

    public int MaxRamGb => 256;

    public int MaxCores => 64;

    public int LogicalCores { get; } = Environment.ProcessorCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditRamText), nameof(ResourcesChanged), nameof(EditRamWarning))]
    private double _editRamGb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditCoresText), nameof(ResourcesChanged))]
    private double _editCores;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResourcesChanged))]
    private bool _editAikar;

    public string EditRamText => Loc.F("Wizard_MemoryValue", EditRamGb, SystemRamGb);

    public string EditCoresText => CreateServerViewModel.CoresLabel((int)EditCores, LogicalCores);

    public string? EditRamWarning => EditRamGb > SystemRamGb ? Loc.F("Wizard_MemoryOverPhysical", SystemRamGb) : EditRamGb > SystemRamGb * 0.75 ? Loc.F("Wizard_MemoryTooHigh", SystemRamGb) : null;

    public bool ResourcesChanged =>
        (int)(EditRamGb * 1024) != Definition.MaxMemoryMb || EffectiveCores((int)EditCores) != Definition.CpuCores || EditAikar != Definition.UseAikarFlags;

    private int EffectiveCores(int value) => value == LogicalCores ? 0 : value;

    [RelayCommand]
    private void SaveResources()
    {
        if (IsRunning)
        {
            MainViewModel.Shell?.Toast(Loc.T("Servers_SaveWhileRunning"), isError: true);
            return;
        }

        var mb = (int)(EditRamGb * 1024);
        Definition = Definition with { MaxMemoryMb = mb, MinMemoryMb = Math.Min(1024, mb), CpuCores = EffectiveCores((int)EditCores), UseAikarFlags = EditAikar };
        _registry.Upsert(Definition);
        _process.UpdateDefinition(Definition);
        OnPropertyChanged(nameof(ResourcesChanged));
        OnPropertyChanged(nameof(JvmCommand));
        MainViewModel.Shell?.Toast(Loc.T("Servers_Saved"));
    }

    // ----- technical details -----
    /// <summary>The exact command PackCheck runs, for support questions and scripts.</summary>
    public string JvmCommand => $"\"{Definition.JavaPath}\" {string.Join(' ', ServerProcess.BuildArguments(Definition).Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}";

    [ObservableProperty]
    private string _processText = "–";

    public IReadOnlyList<KeyValuePair<string, string>> TechnicalInfo =>
    [
        new(Loc.T("Tech_Platform"), $"{Definition.Platform} {Definition.MinecraftVersion}"),
        new(Loc.T("Tech_Jar"), Definition.ArgsFile is null ? Definition.JarFile : "@" + Definition.ArgsFile),
        new(Loc.T("Tech_Java"), Definition.JavaPath),
        new(Loc.T("Tech_Folder"), Definition.Directory),
        new(Loc.T("Tech_Port"), $"TCP {Definition.Port}"),
        new(Loc.T("Tech_Memory"), $"-Xms{Definition.MinMemoryMb}M -Xmx{Definition.MaxMemoryMb}M"),
        new(Loc.T("Tech_Cores"), CreateServerViewModel.CoresLabel(Definition.CpuCores == 0 ? LogicalCores : Definition.CpuCores, LogicalCores)),
        new(Loc.T("Tech_Created"), Definition.CreatedAt.LocalDateTime.ToString("g", Loc.Instance.Culture)),
        new("ID", Definition.Id),
    ];

    [RelayCommand]
    private void CopyTechnical()
    {
        var text = string.Join(Environment.NewLine, TechnicalInfo.Select(kv => $"{kv.Key}: {kv.Value}")) + Environment.NewLine + JvmCommand;
        System.Windows.Clipboard.SetText(text);
        MainViewModel.Shell?.Toast(Loc.T("About_Copied"));
    }

    public string Subtitle => $"{Definition.Platform} {Definition.MinecraftVersion} · :{Definition.Port}";

    public string PlatformText => $"{Definition.Platform} {Definition.MinecraftVersion}";

    public bool UsesPlugins => PlatformInfo.UsesPlugins(Definition.Platform);

    public bool UsesMods => PlatformInfo.UsesMods(Definition.Platform);

    public bool HasContentTab => UsesPlugins || UsesMods;

    public bool IsHybrid => PlatformInfo.IsHybrid(Definition.Platform);

    private FilesViewModel? _files;

    /// <summary>File manager, created the first time the Files tab is opened.</summary>
    public FilesViewModel Files => _files ??= new FilesViewModel(this);

    public string ContentTabKey => UsesMods ? "Nav_Mods" : "Nav_Plugins";

    // ----- console -----
    public ObservableCollection<ConsoleEntry> Console { get; } = [];

    public ObservableCollection<string> Suggestions { get; } = [];

    public ObservableCollection<string> Players { get; } = [];

    public ObservableCollection<PropertyRow> Properties { get; } = [];

    public ObservableCollection<BackupInfo> BackupList { get; } = [];

    public ObservableCollection<PluginRow> PluginList { get; } = [];

    public Gauge Tps { get; } = new("Gauge_Tps");

    public Gauge Cpu { get; } = new("Gauge_Cpu");

    public Gauge Ram { get; } = new("Gauge_Ram");

    public Gauge Ping { get; } = new("Gauge_Ping");

    public IReadOnlyList<Gauge> Gauges => [Tps, Cpu, Ram, Ping];

    public static IReadOnlyList<string> QuickCommands { get; } =
        ["list", "save-all", "say ", "time set day", "time set night", "weather clear", "difficulty normal", "whitelist on", "whitelist off", "whitelist add ", "op ", "deop ", "kick ", "ban ", "pardon ", "gamemode creative ", "gamemode survival "];

    private static readonly string[] KnownCommands =
    [
        "advancement", "ban", "ban-ip", "banlist", "clear", "difficulty", "effect", "enchant", "execute", "experience", "fill", "gamemode", "gamerule",
        "give", "help", "kick", "kill", "list", "locate", "me", "msg", "op", "deop", "pardon", "pardon-ip", "particle", "playsound", "reload", "save-all",
        "save-off", "save-on", "say", "seed", "setblock", "setworldspawn", "spawnpoint", "spreadplayers", "stop", "summon", "tell", "tellraw", "time",
        "title", "tp", "teleport", "weather", "whitelist", "worldborder", "xp", "plugins", "version", "tps", "mspt", "timings", "spark",
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsBusy), nameof(StateText), nameof(CanEdit), nameof(IsCrashed))]
    private ServerState _state;

    [ObservableProperty]
    private string _command = string.Empty;

    [ObservableProperty]
    private string _search = string.Empty;

    /// <summary>When off, search only highlights matches and Next/Previous jump between them.</summary>
    [ObservableProperty]
    private bool _filterSearch;

    [ObservableProperty]
    private string? _matchPosition;

    /// <summary>Player names seen in the log; the console turns them into links.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _knownPlayers = [];

    private readonly List<ConsoleEntry> _matches = [];
    private int _matchIndex = -1;
    private readonly HashSet<string> _seenPlayers = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    private bool _showInfo = true;

    [ObservableProperty]
    private bool _showWarn = true;

    [ObservableProperty]
    private bool _showError = true;

    [ObservableProperty]
    private bool _showTimestamps = true;

    [ObservableProperty]
    private bool _wrap = true;

    [ObservableProperty]
    private bool _paused;

    [ObservableProperty]
    private int _infoCount;

    [ObservableProperty]
    private int _warnCount;

    [ObservableProperty]
    private int _errorCount;

    [ObservableProperty]
    private int _matchCount;

    [ObservableProperty]
    private int _tab;

    [ObservableProperty]
    private string _uptime = "–";

    [ObservableProperty]
    private string _playersText = "–";

    [ObservableProperty]
    private string _diskText = "–";

    [ObservableProperty]
    private string _worldText = "–";

    [ObservableProperty]
    private string _javaText = "–";

    [ObservableProperty]
    private string? _crashSummary;

    public bool IsRunning => State is ServerState.Running or ServerState.Starting;

    public bool IsBusy => State is ServerState.Running or ServerState.Starting or ServerState.Stopping;

    public bool CanEdit => !IsBusy;

    public bool IsCrashed => State == ServerState.Crashed;

    public string StateText => Loc.T("State_" + State);

    public event EventHandler? ScrollRequested;

    /// <summary>Asks the view to select and reveal one line (search navigation).</summary>
    public event EventHandler<ConsoleEntry>? FocusRequested;

    partial void OnStateChanged(ServerState value)
    {
        AnnounceState(value);
        if (IsBusy)
        {
            if (_statsTimer.IsEnabled is false)
            {
                _startedAt = DateTime.Now;
                _lastCpu = TimeSpan.Zero;
                _tick = 0;
                _statsTimer.Start();
            }
        }
        else
        {
            _statsTimer.Stop();
            foreach (var g in Gauges)
            {
                g.Reset();
            }

            Uptime = "–";
            PlayersText = "–";
            _players.Clear();
            Players.Clear();
            _ = RefreshDiskAsync();
        }

        if (value == ServerState.Crashed)
        {
            CrashSummary = _all.LastOrDefault(e => e.Level == "Error")?.Message ?? Loc.T("Console_CrashNoDetail");
        }
        else if (value == ServerState.Starting)
        {
            CrashSummary = null;
        }
    }

    partial void OnSearchChanged(string value) => Rebuild();

    partial void OnShowInfoChanged(bool value) => Rebuild();

    partial void OnShowWarnChanged(bool value) => Rebuild();

    partial void OnShowErrorChanged(bool value) => Rebuild();

    partial void OnPausedChanged(bool value)
    {
        if (!value)
        {
            Rebuild();
        }
    }

    partial void OnCommandChanged(string value)
    {
        Suggestions.Clear();
        var word = value.TrimStart('/');
        if (word.Length == 0 || word.Contains(' ', StringComparison.Ordinal))
        {
            return;
        }

        foreach (var c in KnownCommands.Where(c => c.StartsWith(word, StringComparison.OrdinalIgnoreCase) && c != word).Take(8))
        {
            Suggestions.Add(c);
        }
    }

    private static string LevelName(LogLevel l) => l switch
    {
        LogLevel.Warn => "Warn",
        LogLevel.Error => "Error",
        LogLevel.Command => "Command",
        LogLevel.Plain => "Plain",
        _ => "Info",
    };

    [GeneratedRegex(@"^(?:UUID of player (?<name>[A-Za-z0-9_]{3,16}) is|<(?<name>[A-Za-z0-9_]{3,16})>|(?<name>[A-Za-z0-9_]{3,16})(?:\[/[^\]]+\] logged in| lost connection| (?:joined|left) the game))")]
    private static partial Regex PlayerMention();

    [GeneratedRegex(@"^(?<name>[A-Za-z0-9_]{3,16}) (joined|left) the game$")]
    private static partial Regex JoinLeave();

    private void OnOutput(string raw)
    {
        var text = AnsiStripper.Strip(raw);

        // Answer to our own background "tps" poll: read it, keep it out of the console.
        if (DateTime.Now - _tpsRequestedAt < TpsWindow)
        {
            if (TpsParser.TryParse(text) is { } tps)
            {
                Tps.Set(tps / 20, tps.ToString("0.0", Loc.Instance.Culture), Health.Tps(tps), historyValue: tps);
                _tpsRequestedAt = DateTime.MinValue;
                return;
            }

            if (text.Contains("Mean tick time", StringComparison.Ordinal) || text.Contains("TPS from last", StringComparison.Ordinal))
            {
                return;
            }
        }

        var parsed = LogParser.Parse(text);
        var entry = new ConsoleEntry(parsed.Time, LevelName(parsed.Level), parsed.Source, parsed.Message, text);
        Track(parsed);
        Add(entry);
    }

    private void Track(LogLine line)
    {
        var mention = PlayerMention().Match(line.Message);
        if (mention.Success && _seenPlayers.Add(mention.Groups["name"].Value))
        {
            KnownPlayers = [.. _seenPlayers];
        }

        var m = JoinLeave().Match(line.Message);
        if (m.Success)
        {
            var name = m.Groups["name"].Value;
            if (line.Message.EndsWith("joined the game", StringComparison.Ordinal))
            {
                _players.Add(name);
            }
            else
            {
                _players.Remove(name);
            }

            SyncPlayers(_players);
        }
    }

    private void SyncPlayers(IEnumerable<string> names)
    {
        var list = names.Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (!list.SequenceEqual(Players))
        {
            Players.Clear();
            foreach (var n in list)
            {
                Players.Add(n);
            }
        }
    }

    private void Add(ConsoleEntry entry)
    {
        _all.Add(entry);
        switch (entry.Level)
        {
            case "Warn":
                WarnCount++;
                break;
            case "Error":
                ErrorCount++;
                break;
            default:
                InfoCount++;
                break;
        }

        if (_all.Count > MaxConsoleLines)
        {
            _all.RemoveRange(0, _all.Count - MaxConsoleLines);
        }

        if (Paused || !Visible(entry))
        {
            return;
        }

        Console.Add(entry);
        if (Console.Count > MaxConsoleLines)
        {
            if (_matches.Count > 0 && ReferenceEquals(_matches[0], Console[0]))
            {
                _matches.RemoveAt(0);
                _matchIndex--;
            }

            Console.RemoveAt(0);
        }

        if (IsMatch(entry))
        {
            _matches.Add(entry);
            MatchCount = _matches.Count;
            UpdateMatchPosition();
        }

        ScrollRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool Visible(ConsoleEntry e)
    {
        var levelOk = e.Level switch
        {
            "Warn" => ShowWarn,
            "Error" => ShowError,
            _ => ShowInfo,
        };
        return levelOk && (!FilterSearch || IsMatch(e));
    }

    private bool IsMatch(ConsoleEntry e) => Search.Length > 0 && e.Raw.Contains(Search, StringComparison.OrdinalIgnoreCase);

    private void Rebuild()
    {
        Console.Clear();
        _matches.Clear();
        _matchIndex = -1;
        foreach (var e in _all.Where(Visible))
        {
            Console.Add(e);
            if (IsMatch(e))
            {
                _matches.Add(e);
            }
        }

        MatchCount = _matches.Count;
        UpdateMatchPosition();
        ScrollRequested?.Invoke(this, EventArgs.Empty);
    }

    partial void OnFilterSearchChanged(bool value) => Rebuild();

    private void UpdateMatchPosition() =>
        MatchPosition = Search.Length == 0 ? null : _matchIndex < 0 ? $"{_matches.Count}" : $"{_matchIndex + 1}/{_matches.Count}";

    [RelayCommand]
    private void NextMatch() => StepMatch(1);

    [RelayCommand]
    private void PreviousMatch() => StepMatch(-1);

    private void StepMatch(int delta)
    {
        if (_matches.Count == 0)
        {
            return;
        }

        _matchIndex = _matchIndex < 0
            ? (delta > 0 ? 0 : _matches.Count - 1)
            : (_matchIndex + delta + _matches.Count) % _matches.Count;
        UpdateMatchPosition();
        FocusRequested?.Invoke(this, _matches[_matchIndex]);
    }

    /// <summary>Crash banner: hands the error and the last console lines to the assistant.</summary>
    [RelayCommand]
    private void AskAi()
    {
        var tail = string.Join("\n", _all.Skip(Math.Max(0, _all.Count - 80)).Select(e => e.Raw));
        MainViewModel.Shell?.Assistant.Ask(
            $"My server \"{Name}\" ({Definition.Platform} {Definition.MinecraftVersion}) crashed. Find the cause and tell me how to fix it.\n\nLast console lines:\n```\n{tail}\n```");
    }

    private async Task SampleAsync()
    {
        _tick++;
        var p = _process.Process;
        if (p is null || p.HasExited)
        {
            return;
        }

        try
        {
            p.Refresh();
            var up = DateTime.Now - _startedAt;
            Uptime = up.TotalHours >= 1 ? $"{(int)up.TotalHours}:{up.Minutes:00}:{up.Seconds:00}" : $"{up.Minutes:00}:{up.Seconds:00}";

            var mb = p.WorkingSet64 / 1024d / 1024d;
            var ramPct = mb / Math.Max(1, Definition.MaxMemoryMb) * 100;
            Ram.Set(ramPct / 100, $"{ramPct:0}%", Health.Ram(ramPct), $"{mb:N0} / {Definition.MaxMemoryMb:N0} MB", mb);

            var now = DateTime.UtcNow;
            var cpu = p.TotalProcessorTime;
            if (_lastCpu != TimeSpan.Zero)
            {
                var pct = Math.Clamp((cpu - _lastCpu).TotalMilliseconds / (now - _lastSample).TotalMilliseconds / Environment.ProcessorCount * 100, 0, 100);
                Cpu.Set(pct / 100, $"{pct:0}%", Health.Cpu(pct), Loc.F("Gauge_Threads", p.Threads.Count));
            }

            _lastCpu = cpu;
            _lastSample = now;
            ProcessText = $"PID {p.Id} · {p.Threads.Count} threads · {p.HandleCount} handles · private {p.PrivateMemorySize64 / 1024 / 1024:N0} MB · started {p.StartTime:HH:mm:ss}";
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (State != ServerState.Running)
        {
            return;
        }

        if (_tick % 3 == 0)
        {
            var ping = await ServerListPing.QueryAsync("127.0.0.1", Definition.Port, TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(true);
            if (ping is null)
            {
                Ping.Unavailable(Loc.T("Gauge_PingNoReply"));
            }
            else
            {
                Ping.Set(1 - (Math.Min(ping.LatencyMs, 300) / 300d), $"{ping.LatencyMs} ms", Health.Ping(ping.LatencyMs), historyValue: ping.LatencyMs);
                PlayersText = $"{ping.Online} / {ping.Max}";
                if (ping.Sample.Count > 0)
                {
                    SyncPlayers(ping.Sample.Union(_players, StringComparer.OrdinalIgnoreCase));
                }
                else if (ping.Online == 0)
                {
                    _players.Clear();
                    SyncPlayers([]);
                }
            }
        }

        if (_tick % 10 == 2)
        {
            var hasSpark = File.Exists(Path.Combine(Definition.Directory, "mods", "spark.jar"))
                || (Directory.Exists(Path.Combine(Definition.Directory, "mods")) && Directory.EnumerateFiles(Path.Combine(Definition.Directory, "mods"), "spark*.jar").Any());
            if (TpsParser.CommandFor(Definition.Platform, hasSpark) is { } cmd)
            {
                _tpsRequestedAt = DateTime.Now;
                await _process.SendCommandAsync(cmd).ConfigureAwait(true);
            }
            else
            {
                Tps.Unavailable(Loc.F("Gauge_TpsUnsupported", Definition.Platform));
            }
        }
    }

    private async Task RefreshDiskAsync()
    {
        var dir = Definition.Directory;
        var (total, world) = await Task.Run(() =>
        {
            static long Size(string d) => Directory.Exists(d)
                ? new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => { try { return f.Length; } catch (IOException) { return 0; } })
                : 0;
            try
            {
                return (Size(dir), Size(Path.Combine(dir, "world")) + Size(Path.Combine(dir, "world_nether")) + Size(Path.Combine(dir, "world_the_end")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (0L, 0L);
            }
        }).ConfigureAwait(true);
        DiskText = (string)Bytes.Instance.Convert(total, typeof(string), null, Loc.Instance.Culture);
        WorldText = (string)Bytes.Instance.Convert(world, typeof(string), null, Loc.Instance.Culture);
        JavaText = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Definition.JavaPath))) ?? Definition.JavaPath;
    }

    // ----- commands -----
    [ObservableProperty]
    private string? _preparing;

    /// <summary>Makes sure the right Java exists before starting; downloads Temurin when it does not.</summary>
    private async Task EnsureJavaAsync()
    {
        var required = PlatformInfo.RequiredJava(Definition.Platform, Definition.MinecraftVersion);
        if (JarJava.RequiredFeature(Path.Combine(Definition.Directory, Definition.JarFile)) is { } fromJar && fromJar > required)
        {
            required = fromJar;
        }
        var current = File.Exists(Definition.JavaPath) ? await JavaRuntimes.ProbeAsync(Definition.JavaPath, CancellationToken.None).ConfigureAwait(true) : null;
        var ok = current is not null && (required <= 8 ? current.FeatureVersion == 8 : current.FeatureVersion >= required);
        if (ok)
        {
            return;
        }

        var java = (JavaRuntimes)App.Services.GetService(typeof(JavaRuntimes))!;
        var state = (AppState)App.Services.GetService(typeof(AppState))!;
        Preparing = Loc.F("Java_Installing", JavaRuntimes.InstallableFeature(required));
        Add(new ConsoleEntry(DateTime.Now.ToString("HH:mm:ss", Loc.Instance.Culture), "Command", "PackCheck", Preparing, Preparing));
        var runtime = await java.EnsureAsync(required, state.JavaRuntimes, new Progress<double>(p => Preparing = Loc.F("Java_Installing", JavaRuntimes.InstallableFeature(required)) + $" {p:P0}"), CancellationToken.None).ConfigureAwait(true);
        if (!state.JavaRuntimes.Any(r => r.JavaExe == runtime.JavaExe))
        {
            state.JavaRuntimes = [runtime, .. state.JavaRuntimes];
        }

        Definition = Definition with { JavaPath = runtime.JavaExe };
        _registry.Upsert(Definition);
        _process.UpdateDefinition(Definition);
        Add(new ConsoleEntry(DateTime.Now.ToString("HH:mm:ss", Loc.Instance.Culture), "Command", "PackCheck", runtime.Display, runtime.Display));
        Preparing = null;
        _ = RefreshDiskAsync();
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        _automation.UserStarted();
        try
        {
            ClearConsole();
            await EnsureJavaAsync().ConfigureAwait(true);
            await _process.StartAsync().ConfigureAwait(true);
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            Preparing = null;
            Add(new ConsoleEntry(DateTime.Now.ToString("HH:mm:ss", Loc.Instance.Culture), "Error", "PackCheck", Loc.F("Java_Failed", ex.Message), ex.Message));
            MainViewModel.Shell?.Toast(Loc.F("Java_Failed", ex.Message), isError: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Add(new ConsoleEntry(DateTime.Now.ToString("HH:mm:ss", Loc.Instance.Culture), "Error", "PackCheck", ex.Message, ex.Message));
            MainViewModel.Shell?.Toast(ex.Message, isError: true);
        }
    }

    [RelayCommand]
    private Task StopAsync()
    {
        _automation.UserStopped(); // a manual stop must not trigger crash or idle rules
        return _process.StopAsync(TimeSpan.FromSeconds(60));
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        await _process.StopAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(true);
        await StartAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task KillAsync()
    {
        if (MainViewModel.Shell is { } shell && await shell.ConfirmAsync(Loc.T("Servers_Kill"), Name, Loc.T("Servers_Kill"), danger: true).ConfigureAwait(true))
        {
            _automation.UserStopped(); // a forced stop is not a crash
            _process.Kill();
        }
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var cmd = Command.Trim().TrimStart('/');
        if (cmd.Length == 0)
        {
            return;
        }

        if (!IsRunning)
        {
            MainViewModel.Shell?.Toast(Loc.T("Console_NotRunning"), isError: true);
            return;
        }

        _history.Remove(cmd);
        _history.Add(cmd);
        _historyIndex = _history.Count;
        Add(new ConsoleEntry(DateTime.Now.ToString("HH:mm:ss", Loc.Instance.Culture), "Command", null, "> " + cmd, "> " + cmd));
        Command = string.Empty;
        await _process.SendCommandAsync(cmd).ConfigureAwait(true);
    }

    [RelayCommand]
    private void Quick(string? command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return;
        }

        Command = command;
        if (!command.EndsWith(' '))
        {
            SendCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void PlayerAction(string? action)
    {
        // "kick:Steve" -> fills the command box so the user can add a reason before sending.
        if (action?.Split(':', 2) is [var verb, var name])
        {
            Command = $"{verb} {name}";
        }
    }

    [RelayCommand]
    private void AcceptSuggestion(string? suggestion)
    {
        if (suggestion is not null)
        {
            Command = suggestion + " ";
        }
    }

    public void HistoryStep(int delta)
    {
        if (_history.Count == 0)
        {
            return;
        }

        _historyIndex = Math.Clamp(_historyIndex + delta, 0, _history.Count);
        Command = _historyIndex < _history.Count ? _history[_historyIndex] : string.Empty;
    }

    [RelayCommand]
    private void ClearConsole()
    {
        _all.Clear();
        Console.Clear();
        InfoCount = WarnCount = ErrorCount = MatchCount = 0;
    }

    [RelayCommand]
    private void CopyConsole()
    {
        var text = string.Join(Environment.NewLine, Console.Select(e => e.Raw));
        if (text.Length > 0)
        {
            Clipboard.SetText(text);
            MainViewModel.Shell?.Toast(Loc.T("Console_Copied"));
        }
    }

    [RelayCommand]
    private void SaveLog()
    {
        var dlg = new SaveFileDialog { FileName = $"{Name}-{DateTime.Now:yyyyMMdd-HHmmss}.log", Filter = "Log (*.log)|*.log|Text (*.txt)|*.txt" };
        if (dlg.ShowDialog() == true)
        {
            File.WriteAllLines(dlg.FileName, _all.Select(e => e.Raw));
            MainViewModel.Shell?.Toast(Loc.T("Console_Saved"));
        }
    }

    [RelayCommand]
    private void OpenLatestLog()
    {
        var log = Path.Combine(Definition.Directory, "logs", "latest.log");
        Shell32.Open(File.Exists(log) ? log : Definition.Directory);
    }

    [RelayCommand]
    private void OpenFolder() => Shell32.Open(Definition.Directory);

    [RelayCommand]
    private void ValidateServer()
    {
        if (App.Services.GetService(typeof(ValidateViewModel)) is ValidateViewModel v && MainViewModel.Shell is { } shell)
        {
            v.SetTarget(Definition.Directory, Definition.MinecraftVersion);
            shell.Navigate(v);
            v.RunCommand.Execute(null);
        }
    }

    // ----- properties -----
    public ObservableCollection<PropertyGroup> PropertyGroups { get; } = [];

    [ObservableProperty]
    private string _propertySearch = string.Empty;

    [ObservableProperty]
    private int _changedCount;

    partial void OnPropertySearchChanged(string value) => GroupProperties();

    /// <summary>Show only settings that differ from the default (or have unsaved edits).</summary>
    [ObservableProperty]
    private bool _propertyOnlyModified;

    partial void OnPropertyOnlyModifiedChanged(bool value) => GroupProperties();

    /// <summary>Every known key is shown, even when the file does not contain it yet; unknown keys go to "Other".</summary>
    private void LoadProperties()
    {
        Properties.Clear();
        var file = Path.Combine(Definition.Directory, "server.properties");
        var values = File.Exists(file) ? ServerProperties.Parse(File.ReadAllText(file)) : [];
        foreach (var info in PropertyCatalog.All)
        {
            Properties.Add(new PropertyRow(info.Key, values.TryGetValue(info.Key, out var v) ? v : info.Default, values.ContainsKey(info.Key)));
        }

        foreach (var (k, v) in values.Where(kv => PropertyCatalog.Find(kv.Key) is null).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Properties.Add(new PropertyRow(k, v, inFile: true));
        }

        // Bukkit/Spigot/Paper YAML settings, only those the server's files actually contain.
        foreach (var yamlGroup in PropertyCatalog.Yaml.GroupBy(y => y.File!))
        {
            var path = Path.Combine(Definition.Directory, yamlGroup.Key);
            if (!File.Exists(path))
            {
                continue;
            }

            var text = File.ReadAllText(path);
            foreach (var info in yamlGroup)
            {
                if (YamlPatcher.Get(text, info.Path!) is { } value)
                {
                    Properties.Add(new PropertyRow(info, value));
                }
            }
        }

        foreach (var row in Properties)
        {
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PropertyRow.Value))
                {
                    ChangedCount = Properties.Count(r => r.IsChanged);
                }
            };
        }

        ChangedCount = 0;
        GroupProperties();
    }

    private void GroupProperties()
    {
        PropertyGroups.Clear();
        foreach (var key in PropertyCatalog.Groups.Append("Other").Concat(PropertyCatalog.YamlGroups))
        {
            var rows = Properties.Where(r => r.GroupKey == key && r.Matches(PropertySearch) && (!PropertyOnlyModified || !r.IsDefault || r.IsChanged)).ToList();
            if (rows.Count > 0)
            {
                PropertyGroups.Add(new PropertyGroup(key, new ObservableCollection<PropertyRow>(rows)));
            }
        }
    }

    [RelayCommand]
    private void EditRawProperties()
    {
        Tab = 5;
        Files.OpenInEditor(Path.Combine(Definition.Directory, "server.properties"));
    }

    [RelayCommand]
    private void SaveProperties()
    {
        if (!CanEdit)
        {
            MainViewModel.Shell?.Toast(Loc.T("Servers_SaveWhileRunning"), isError: true);
            return;
        }

        if (Properties.FirstOrDefault(p => p.Error is not null) is { } bad)
        {
            MainViewModel.Shell?.Toast($"{bad.Key}: {bad.Error}", isError: true);
            return;
        }

        var file = Path.Combine(Definition.Directory, "server.properties");
        var original = File.Exists(file) ? File.ReadAllText(file) : string.Empty;
        File.WriteAllText(file, ServerProperties.Update(original, Properties.Where(p => p.Info?.IsYaml != true && p.ShouldWrite).ToDictionary(p => p.Key, p => p.Value.Trim())));
        foreach (var group in Properties.Where(p => p.Info?.IsYaml == true && p.IsChanged).GroupBy(p => p.Info!.File!))
        {
            var yamlFile = Path.Combine(Definition.Directory, group.Key);
            var text = File.ReadAllText(yamlFile);
            foreach (var row in group)
            {
                text = YamlPatcher.Set(text, row.Info!.Path!, row.Value.Trim()) ?? text;
            }

            File.WriteAllText(yamlFile, text);
        }
        if (Properties.FirstOrDefault(p => p.Key == "server-port") is { } port && int.TryParse(port.Value, out var n))
        {
            Definition = Definition with { Port = n };
            _registry.Upsert(Definition);
            OnPropertyChanged(nameof(Subtitle));
        }

        LoadProperties();
        LoadIdentity();
        MainViewModel.Shell?.Toast(Loc.T("Servers_Saved"));
    }

    // ----- backups -----
    public void RefreshBackups()
    {
        BackupList.Clear();
        foreach (var b in Backups.List(Definition))
        {
            BackupList.Add(b);
        }
    }

    [RelayCommand]
    private async Task CreateBackupAsync()
    {
        try
        {
            await Backups.CreateAsync(Definition, _settings.BackupsToKeep, CancellationToken.None).ConfigureAwait(true);
            RefreshBackups();
            MainViewModel.Shell?.Toast(Loc.T("Backups_Done"));
        }
        catch (IOException ex)
        {
            MainViewModel.Shell?.Toast(ex.Message, isError: true);
        }
    }

    [RelayCommand]
    private async Task RestoreBackupAsync(BackupInfo? backup)
    {
        if (backup is null || MainViewModel.Shell is not { } shell)
        {
            return;
        }

        if (!CanEdit)
        {
            shell.Toast(Loc.T("Servers_SaveWhileRunning"), isError: true);
            return;
        }

        if (!await shell.ConfirmAsync(Loc.T("Backups_Restore"), Loc.T("Backups_RestoreConfirm"), Loc.T("Backups_Restore"), danger: true).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await Backups.RestoreAsync(Definition, backup, CancellationToken.None).ConfigureAwait(true);
            LoadProperties();
            RefreshPlugins();
        }
        catch (IOException ex)
        {
            shell.Toast(ex.Message, isError: true);
        }
    }

    /// <summary>Deletes one backup zip after confirmation; the server itself is not touched.</summary>
    [RelayCommand]
    private async Task DeleteBackupAsync(BackupInfo? backup)
    {
        if (backup is null || MainViewModel.Shell is not { } shell)
        {
            return;
        }

        var when = backup.CreatedAt.LocalDateTime.ToString("g", Loc.Instance.Culture);
        if (!await shell.ConfirmAsync(Loc.T("Backups_DeleteTitle"), Loc.F("Backups_DeleteBody", when, Bytes.Format(backup.Size)), Loc.T("Backups_Delete"), danger: true).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            File.Delete(backup.Path);
            RefreshBackups();
            shell.Toast(Loc.T("Backups_Deleted"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            shell.Toast(ex.Message, isError: true);
        }
    }

    // ----- plugins / mods -----
    /// <summary>Hybrids keep plugins in "plugins"; pure mod platforms use "mods".</summary>
    public string PluginsDir => Path.Combine(Definition.Directory, UsesPlugins ? "plugins" : "mods");

    public string ModsDir => Path.Combine(Definition.Directory, "mods");

    public string DisabledDir => Path.Combine(PluginsDir, ".disabled");

    public void RefreshPlugins()
    {
        PluginList.Clear();
        foreach (var (dir, enabled) in new[] { (PluginsDir, true), (DisabledDir, false) })
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var jar in Directory.EnumerateFiles(dir, "*.jar").Order(StringComparer.OrdinalIgnoreCase))
            {
                var info = PluginMetadataReader.ReadAsync(jar, CancellationToken.None).GetAwaiter().GetResult();
                PluginList.Add(new PluginRow(Path.GetFileName(jar), info.Name, info.Version, enabled));
            }
        }
    }

    [RelayCommand]
    private async Task TogglePluginAsync(PluginRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (!CanEdit)
        {
            MainViewModel.Shell?.Toast(Loc.T("Servers_SaveWhileRunning"), isError: true);
            return;
        }

        var from = Path.Combine(row.Enabled ? PluginsDir : DisabledDir, row.File);
        var to = Path.Combine(row.Enabled ? DisabledDir : PluginsDir, row.File);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (!await Preflight.ConfirmChangeAsync(Definition, () => File.Move(from, to), () => File.Move(to, from)).ConfigureAwait(true))
        {
            return;
        }

        RefreshPlugins();
    }

    [RelayCommand]
    private async Task AddPluginFileAsync()
    {
        var dlg = new OpenFileDialog { Filter = "Java archive (*.jar)|*.jar", Multiselect = true };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        Directory.CreateDirectory(PluginsDir);
        var copied = new List<string>();
        var ok = await Preflight.ConfirmChangeAsync(
            Definition,
            () =>
            {
                foreach (var f in dlg.FileNames)
                {
                    var dest = Path.Combine(PluginsDir, Path.GetFileName(f));
                    File.Copy(f, dest, overwrite: false);
                    copied.Add(dest);
                }
            },
            () => copied.ForEach(File.Delete)).ConfigureAwait(true);
        if (ok)
        {
            RefreshPlugins();
        }
    }

    public async Task ShutdownAsync()
    {
        _statsTimer.Stop();
        await _process.DisposeAsync().ConfigureAwait(true);
    }
}
