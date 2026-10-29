using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PackCheck.App.Infrastructure;
using PackCheck.Core;
using PackCheck.Providers;
using PackCheck.Reporting;
using PackCheck.Servers;
using PackCheck.Updater;
using PackCheck.Validation;

namespace PackCheck.App.ViewModels;

/// <summary>A platform tile in the create wizard.</summary>
public sealed record PlatformOption(ServerPlatform Platform, string Group)
{
    public string Name => Platform.ToString();

    public string Description => Loc.T("Plat_" + Platform);

    public string GroupText => Loc.T("PlatGroup_" + Group);

    public static string GroupOf(ServerPlatform p) => p switch
    {
        ServerPlatform.Velocity or ServerPlatform.BungeeCord => "Proxy",
        ServerPlatform.Vanilla => "Vanilla",
        ServerPlatform.Mohist or ServerPlatform.Youer or ServerPlatform.Arclight or ServerPlatform.SpongeVanilla => "Hybrid",
        _ when PlatformInfo.UsesMods(p) => "Mods",
        _ => "Plugins",
    };
}

/// <summary>A starting point that fills platform, resources and game rules in one click.</summary>
public sealed record WizardPreset(string Key, ServerPlatform Platform, int MemoryGb, string GameMode, string Difficulty, int MaxPlayers, int ViewDistance, bool Pvp, bool Whitelist)
{
    public string Title => Loc.T("Preset_" + Key);

    public string Hint => Loc.T("Preset_" + Key + "_Hint");
}

public sealed partial class CreateServerViewModel : ObservableObject
{
    private readonly ServerSources _sources;
    private readonly ServerCreator _creator;
    private readonly JavaRuntimes _java;
    private readonly AppState _state;
    private readonly Action<ServerDefinition> _done;
    private readonly Action _cancel;
    private CancellationTokenSource? _cts;

    public CreateServerViewModel(ServerSources sources, ServerCreator creator, JavaRuntimes java, AppState state, AppSettings settings, Action<ServerDefinition> done, Action cancel)
    {
        _sources = sources;
        _creator = creator;
        _java = java;
        _state = state;
        _done = done;
        _cancel = cancel;
        _folder = settings.DefaultServerFolder;
        foreach (var r in state.JavaRuntimes)
        {
            Runtimes.Add(r);
        }

        _selectedPlatform = PlatformOptions.First(o => o.Platform == _platform);
        _memoryGb = Math.Min(4, MaxRamGb);
        _port = FreePort(new ServerRegistry().Load().Select(d => d.Port));
        _ = LoadVersionsAsync();
    }

    public IReadOnlyList<ServerPlatform> Platforms { get; } = Enum.GetValues<ServerPlatform>();

    public IReadOnlyList<PlatformOption> PlatformOptions { get; } =
        Enum.GetValues<ServerPlatform>().Select(p => new PlatformOption(p, PlatformOption.GroupOf(p)))
            .OrderBy(o => Array.IndexOf(["Plugins", "Mods", "Vanilla", "Hybrid", "Proxy"], o.Group)).ToList();

    public IReadOnlyList<WizardPreset> Presets { get; } =
    [
        new("Friends", ServerPlatform.Paper, 4, "survival", "normal", 10, 10, true, true),
        new("Modded", ServerPlatform.NeoForge, 8, "survival", "normal", 8, 10, true, true),
        new("Creative", ServerPlatform.Paper, 4, "creative", "peaceful", 20, 12, false, true),
        new("Minigames", ServerPlatform.Purpur, 6, "adventure", "easy", 50, 8, true, false),
        new("Hardcore", ServerPlatform.Paper, 4, "survival", "hard", 10, 10, true, true),
        new("LowEnd", ServerPlatform.Paper, 2, "survival", "normal", 5, 6, true, true),
    ];

    public IReadOnlyList<string> GameModes { get; } = ["survival", "creative", "adventure", "spectator"];

    public IReadOnlyList<string> Difficulties { get; } = ["peaceful", "easy", "normal", "hard"];

    /// <summary>Installed physical memory, for the RAM slider's limit and warning.</summary>
    public double SystemRamGb => SystemLimits.RamGb;

    /// <summary>Upper end of the RAM slider: the RAM this PC has (at most 256 GB).</summary>
    public int MaxRamGb => SystemLimits.MaxRamGb;

    /// <summary>Upper end of the CPU slider: this PC's logical cores (at most 64).</summary>
    public int MaxCores => SystemLimits.MaxCores;

    public int LogicalCores { get; } = Environment.ProcessorCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryText), nameof(MemoryWarning), nameof(Capacity), nameof(SummaryText))]
    private double _memoryGb = 4;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoresText), nameof(SummaryText))]
    private double _cpuCores = Environment.ProcessorCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private PlatformOption? _selectedPlatform;

    [ObservableProperty]
    private WizardPreset? _preset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private string _gameMode = "survival";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private string _difficulty = "normal";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Capacity), nameof(SummaryText))]
    private double _maxPlayers = 10;

    [ObservableProperty]
    private double _viewDistance = 10;

    [ObservableProperty]
    private bool _pvp = true;

    [ObservableProperty]
    private bool _whitelist = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OfflineWarning))]
    private bool _onlineMode = true;

    [ObservableProperty]
    private bool _hardcore;

    [ObservableProperty]
    private string _motd = "A PackCheck server";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private int _port = 25565;

    public bool IsProxy => ProxyConfigurator.IsProxy(Platform);

    public string MemoryText => Loc.F("Wizard_MemoryValue", MemoryGb, SystemRamGb);

    public string CoresText => CoresLabel((int)CpuCores, LogicalCores);

    public static string CoresLabel(int cores, int logical) =>
        cores == logical ? Loc.F("Wizard_CoresAll", logical) : cores > logical ? Loc.F("Wizard_CoresOver", cores, logical) : Loc.F("Wizard_CoresValue", cores, logical);

    /// <summary>Warns when the server would take most of the PC's memory (Windows and Java need headroom).</summary>
    public string? MemoryWarning => MemoryGb > SystemRamGb ? Loc.F("Wizard_MemoryOverPhysical", SystemRamGb)
        : MemoryGb > SystemRamGb * 0.75 ? Loc.F("Wizard_MemoryTooHigh", SystemRamGb)
        : MemoryGb < RecommendedGb ? Loc.F("Wizard_MemoryLow", RecommendedGb) : null;

    public string? OfflineWarning => OnlineMode ? null : Loc.T("Wizard_OfflineWarn");

    /// <summary>Rough rule of thumb, shown as an estimate: mods need far more memory per player than plugins.</summary>
    public string Capacity
    {
        get
        {
            var perGb = IsProxy ? 250 : PlatformInfo.UsesMods(Platform) ? 3 : 8;
            var players = Math.Max(1, (int)((MemoryGb - (PlatformInfo.UsesMods(Platform) ? 2 : 1)) * perGb));
            return Loc.F("Wizard_Capacity", players);
        }
    }

    public int RecommendedGb => IsProxy ? 1 : PlatformInfo.UsesMods(Platform) ? 6 : Platform == ServerPlatform.Vanilla ? 3 : 4;

    public string SummaryText =>
        $"{Platform} {Version ?? "…"} · {MemoryGb:0.#} GB · {CoresText}\n:{Port} · {(IsProxy ? "proxy" : $"{GameMode} · {Difficulty} · {(int)MaxPlayers} {Loc.T("Card_Players").ToLower(Loc.Instance.Culture)}")}";

    partial void OnSelectedPlatformChanged(PlatformOption? value)
    {
        if (value is not null && value.Platform != Platform)
        {
            Platform = value.Platform;
        }
    }

    [RelayCommand]
    private void ApplyPreset(WizardPreset? preset) => Preset = preset;

    partial void OnPresetChanged(WizardPreset? value)
    {
        if (value is null)
        {
            return;
        }

        SelectedPlatform = PlatformOptions.First(o => o.Platform == value.Platform);
        MemoryGb = Math.Min(value.MemoryGb, MaxRamGb);
        GameMode = value.GameMode;
        Difficulty = value.Difficulty;
        MaxPlayers = value.MaxPlayers;
        ViewDistance = value.ViewDistance;
        Pvp = value.Pvp;
        Whitelist = value.Whitelist;
        Hardcore = value.Key == "Hardcore";
        CpuCores = value.Key == "LowEnd" ? Math.Max(1, LogicalCores / 2) : LogicalCores;
    }

    /// <summary>First port from 25565 that no registered server uses and nothing listens on.</summary>
    private static int FreePort(IEnumerable<int> taken)
    {
        var used = taken.ToHashSet();
        for (var p = 25565; p < 25700; p++)
        {
            if (used.Contains(p))
            {
                continue;
            }

            try
            {
                var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, p);
                l.Start();
                l.Stop();
                return p;
            }
            catch (System.Net.Sockets.SocketException)
            {
            }
        }

        return 25565;
    }

    private Dictionary<string, string> WizardProperties() => IsProxy ? [] : new()
    {
        ["gamemode"] = GameMode,
        ["difficulty"] = Difficulty,
        ["max-players"] = ((int)MaxPlayers).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["view-distance"] = ((int)ViewDistance).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["simulation-distance"] = Math.Min((int)ViewDistance, 10).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["pvp"] = Pvp ? "true" : "false",
        ["white-list"] = Whitelist ? "true" : "false",
        ["enforce-whitelist"] = Whitelist ? "true" : "false",
        ["online-mode"] = OnlineMode ? "true" : "false",
        ["hardcore"] = Hardcore ? "true" : "false",
        ["motd"] = Motd.Replace("\n", " ", StringComparison.Ordinal).Trim(),
    };

    public ObservableCollection<string> Versions { get; } = [];

    public ObservableCollection<JavaRuntime> Runtimes { get; } = [];

    [ObservableProperty]
    private string _name = "My Server";

    [ObservableProperty]
    private string _folder;

    [ObservableProperty]
    private ServerPlatform _platform = ServerPlatform.Paper;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(JavaHint), nameof(NeedsJava), nameof(RequiredJava), nameof(SummaryText))]
    private string? _version;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(JavaHint), nameof(NeedsJava))]
    private JavaRuntime? _runtime;

    [ObservableProperty]
    private int _memoryMb = 4096;

    [ObservableProperty]
    private string _seed = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private bool _acceptEula;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private bool _isWorking;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _versionsFailed;

    [ObservableProperty]
    private string? _supportedRange;

    public int RequiredJava => Version is null ? 21 : PlatformInfo.RequiredJava(Platform, Version);

    public bool NeedsJava => Runtime is null || Runtime.FeatureVersion < RequiredJava;

    public string JavaHint => Loc.F("Wizard_JavaNeeded", Version ?? "?", RequiredJava);

    partial void OnPlatformChanged(ServerPlatform value)
    {
        SelectedPlatform = PlatformOptions.First(o => o.Platform == value);
        OnPropertyChanged(nameof(IsProxy));
        OnPropertyChanged(nameof(Capacity));
        OnPropertyChanged(nameof(MemoryWarning));
        OnPropertyChanged(nameof(SummaryText));
        _ = LoadVersionsAsync();
    }

    partial void OnVersionChanged(string? value) => Runtime = Runtimes.FirstOrDefault(r => r.FeatureVersion >= RequiredJava) ?? Runtime;

    private async Task LoadVersionsAsync()
    {
        VersionsFailed = false;
        SupportedRange = null;
        Versions.Clear();
        Status = Loc.T("Wizard_LoadingVersions");
        try
        {
            var list = await _sources.GetVersionsAsync(Platform, CancellationToken.None).ConfigureAwait(true);
            Versions.Clear();
            foreach (var v in list.Take(60))
            {
                Versions.Add(v);
            }

            Version = Versions.FirstOrDefault();
            SupportedRange = Versions.Count == 0 ? null : Loc.F("Wizard_Supports", Versions[^1], Versions[0], Versions.Count);
            Status = null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            VersionsFailed = true;
            Status = Loc.T("Wizard_Offline");
        }
    }

    [RelayCommand]
    private void Browse()
    {
        var dlg = new OpenFolderDialog { InitialDirectory = Directory.Exists(Folder) ? Folder : null };
        if (dlg.ShowDialog() == true)
        {
            Folder = dlg.FolderName;
        }
    }

    [RelayCommand]
    private async Task InstallJavaAsync()
    {
        IsWorking = true;
        try
        {
            var rt = await _java.EnsureAsync(RequiredJava, _state.JavaRuntimes, new Progress<double>(p => { Progress = p; Status = $"Java {RequiredJava} … {p:P0}"; }), CancellationToken.None).ConfigureAwait(true);
            _state.JavaRuntimes = [rt, .. _state.JavaRuntimes];
            Runtimes.Insert(0, rt);
            Runtime = rt;
            Status = null;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or System.Text.Json.JsonException)
        {
            Status = ex.Message;
        }
        finally
        {
            IsWorking = false;
        }
    }

    private bool CanCreate() => AcceptEula && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        if (Version is null)
        {
            return;
        }

        if (Runtime is null || NeedsJava)
        {
            await InstallJavaAsync().ConfigureAwait(true);
            if (Runtime is null || NeedsJava)
            {
                return;
            }
        }

        IsWorking = true;
        _cts = new CancellationTokenSource();
        try
        {
            var req = new ServerCreator.Request(Name, Folder, Platform, Version, Runtime.JavaExe, (int)(MemoryGb * 1024), Port, Seed, AcceptEula)
            {
                CpuCores = (int)CpuCores == LogicalCores ? 0 : (int)CpuCores,
                Properties = WizardProperties(),
            };
            var def = await _creator.CreateAsync(req, new Progress<(double Progress, string Step)>(p => { Progress = p.Progress; Status = Loc.T("Wizard_Step_" + p.Step); }), _cts.Token).ConfigureAwait(true);
            _done(def);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or ArgumentException or OperationCanceledException or UnauthorizedAccessException)
        {
            Status = ex.Message;
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        _cancel();
    }
}

public sealed partial class CheckItemViewModel(CheckResult result) : ObservableObject
{
    public CheckResult Result { get; } = result;

    public string StatusText => Loc.T("Status_" + Result.Status);

    public string Status => Result.Status.ToString();

    public bool HasEvidence => Result.Evidence.Count > 0;

    public string EvidenceText => string.Join(Environment.NewLine, Result.Evidence);

    public string? FixLabel => Result.Fix is null ? null : Loc.T(Result.Fix.IsCertain ? "Validate_Fix" : "Validate_FixEstimated");

    public string Category => CategoryOf(Result.Id);

    public string CategoryText => Loc.T("CheckCat_" + Category);

    /// <summary>File or plugin the finding is about; "" for pack-wide checks.</summary>
    public string Location => (Result.AffectedFile ?? Result.AffectedPlugin ?? string.Empty).Replace('\\', '/');

    public bool IsProblem => Result.Status is not CheckStatus.Pass;

    public string ConfidenceText => Loc.T("Confidence_" + Result.Confidence);

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Rule families by id range (see docs/rule-authoring.md).</summary>
    public static string CategoryOf(string id) => id switch
    {
        _ when id.StartsWith("RT", StringComparison.Ordinal) => "Runtime",
        "PC060" or "PC061" or "PC062" => "Integrity",
        "PC063" => "Duplicates",
        "PC064" => "Dependencies",
        "PC065" or "PC066" => "Versions",
        _ when id.StartsWith("PC00", StringComparison.Ordinal) => "Inventory",
        _ when id.StartsWith("PC01", StringComparison.Ordinal) => "Config",
        _ when id.StartsWith("PC02", StringComparison.Ordinal) => "Integrity",
        _ when id.StartsWith("PC03", StringComparison.Ordinal) => "Duplicates",
        _ when id.StartsWith("PC04", StringComparison.Ordinal) => "Dependencies",
        _ when id.StartsWith("PC05", StringComparison.Ordinal) => "Versions",
        _ => "Other",
    };

    public static int Rank(CheckStatus s) => s switch { CheckStatus.Fail => 0, CheckStatus.Error => 1, CheckStatus.Warn => 2, CheckStatus.Skip => 3, _ => 4 };
}

/// <summary>A group of findings (by status, category or file).</summary>
public sealed partial class CheckGroup(string title, string status, IReadOnlyList<CheckItemViewModel> items) : ObservableObject
{
    public string Title { get; } = title;

    /// <summary>Worst status in the group, for the color strip.</summary>
    public string Status { get; } = status;

    public IReadOnlyList<CheckItemViewModel> Items { get; } = items;

    public string CountText => Loc.F("Validate_GroupCount", Items.Count, Items.Count(i => i.IsProblem));

    [ObservableProperty]
    private bool _isOpen = true;
}

/// <summary>Folder or file in the findings tree.</summary>
public sealed class FileNode(string name, string path, bool isFolder)
{
    public string Name { get; } = name;

    public string Path { get; } = path;

    public bool IsFolder { get; } = isFolder;

    public List<FileNode> Children { get; } = [];

    public string Status { get; set; } = "Pass";

    public int Problems { get; set; }
}

public sealed record HistoryEntry(string File, DateTimeOffset At, OverallStatus Overall)
{
    public string Display => $"{At.LocalDateTime:g} · {Loc.T("Overall_" + Overall)}";
}

public sealed partial class ValidateViewModel(PackValidator validator, RuntimeTestRunner runner, AppState state) : ObservableObject
{
    private ValidationReport? _report;

    public ObservableCollection<CheckItemViewModel> Checks { get; } = [];

    public ObservableCollection<CheckGroup> Groups { get; } = [];

    public ObservableCollection<FileNode> FileTree { get; } = [];

    public ObservableCollection<PluginInfo> Inventory { get; } = [];

    /// <summary>0 = by status, 1 = by category, 2 = by file.</summary>
    [ObservableProperty]
    private int _groupMode;

    /// <summary>0 = findings, 1 = inventory.</summary>
    [ObservableProperty]
    private int _resultsTab;

    [ObservableProperty]
    private string _findingSearch = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFileFilter))]
    private string? _fileFilter;

    [ObservableProperty]
    private string? _technicalText;

    public bool HasFileFilter => FileFilter is not null;

    partial void OnGroupModeChanged(int value) => Rebuild();

    partial void OnFindingSearchChanged(string value) => Rebuild();

    partial void OnFileFilterChanged(string? value) => Rebuild();

    [RelayCommand]
    private void FilterFile(FileNode? node) => FileFilter = node?.Path;

    [RelayCommand]
    private void ClearFileFilter() => FileFilter = null;

    [RelayCommand]
    private void ExpandAll(string? open)
    {
        var value = open != "false";
        foreach (var g in Groups)
        {
            g.IsOpen = value;
            foreach (var i in g.Items)
            {
                i.IsExpanded = value && i.IsProblem;
            }
        }
    }

    /// <summary>Hands one finding to the assistant with its evidence.</summary>
    [RelayCommand]
    private void AskAi(CheckItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var r = item.Result;
        MainViewModel.Shell?.Assistant.Ask(
            $"PackCheck reported {r.Status} for {r.Id} \"{r.Name}\" in {Target}.\n{r.Description}\n"
            + (item.Location.Length > 0 ? $"File: {item.Location}\n" : string.Empty)
            + (r.Evidence.Count > 0 ? $"Evidence:\n```\n{string.Join("\n", r.Evidence)}\n```\n" : string.Empty)
            + "Explain the cause and how to fix it.");
    }

    [RelayCommand]
    private void OpenTarget()
    {
        if (Target is { } t && (Directory.Exists(t) || File.Exists(t)))
        {
            Shell32.Open(Directory.Exists(t) ? t : Path.GetDirectoryName(t)!);
        }
    }

    public ObservableCollection<string> Diff { get; } = [];

    public ObservableCollection<HistoryEntry> History { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private string? _target;

    [ObservableProperty]
    private string _minecraftVersion = string.Empty;

    [ObservableProperty]
    private bool _runtimeTest;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private string? _overallText;

    [ObservableProperty]
    private string? _overall;

    [ObservableProperty]
    private int _passCount;

    [ObservableProperty]
    private int _warnCount;

    [ObservableProperty]
    private int _failCount;

    [ObservableProperty]
    private int _skipCount;

    [ObservableProperty]
    private bool _problemsOnly;

    [ObservableProperty]
    private bool _hasDiff;

    public bool HasReport => _report is not null;

    public void SetTarget(string path, string? mc)
    {
        Target = path;
        MinecraftVersion = mc is not null && Core.MinecraftVersion.TryParse(mc, out _) ? mc : string.Empty;
    }

    partial void OnProblemsOnlyChanged(bool value) => Rebuild();

    partial void OnTargetChanged(string? value) => LoadHistory();

    [RelayCommand]
    private void PickFolder()
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog() == true)
        {
            SetTarget(dlg.FolderName, PackManifest.TryReadVersion(dlg.FolderName)?.ToString());
        }
    }

    [RelayCommand]
    private void PickZip()
    {
        var dlg = new OpenFileDialog { Filter = "Server pack (*.zip)|*.zip" };
        if (dlg.ShowDialog() == true)
        {
            SetTarget(dlg.FileName, null);
        }
    }

    private bool CanRun() => !IsRunning && !string.IsNullOrWhiteSpace(Target);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsRunning = true;
        Checks.Clear();
        Groups.Clear();
        FileTree.Clear();
        Inventory.Clear();
        FileFilter = null;
        TechnicalText = null;
        Diff.Clear();
        _report = null;
        OnPropertyChanged(nameof(HasReport));
        try
        {
            var options = new ValidationOptions
            {
                MinecraftVersion = Core.MinecraftVersion.TryParse(MinecraftVersion, out var mv) ? mv : null,
                JavaVersion = state.JavaRuntimes.FirstOrDefault()?.FeatureVersion,
            };
            var report = await Task.Run(() => validator.ValidateAsync(Target!, options)).ConfigureAwait(true);

            if (RuntimeTest && Directory.Exists(Target) && state.JavaRuntimes.FirstOrDefault() is { } java)
            {
                var jar = Directory.EnumerateFiles(Target, "*.jar").Select(Path.GetFileName).FirstOrDefault();
                if (jar is not null)
                {
                    var rt = await runner.RunAsync(Target, jar, new RuntimeTestOptions { JavaPath = java.JavaExe }, null, CancellationToken.None).ConfigureAwait(true);
                    var all = report.Checks.Concat(rt).ToList();
                    report = report with { Checks = all, Overall = ValidationReport.Aggregate(all) };
                }
            }

            _report = report;
            var previous = History.FirstOrDefault();
            SaveHistory(report);
            if (previous is not null && JsonReporter.Read(await File.ReadAllTextAsync(previous.File).ConfigureAwait(true)) is { } old)
            {
                foreach (var c in ReportDiff.Compare(old, report))
                {
                    Diff.Add($"{c.Kind}: {c.Text}");
                }
            }

            HasDiff = previous is not null;
            Overall = report.Overall.ToString();
            OverallText = Loc.T("Overall_" + report.Overall);
            PassCount = report.Count(CheckStatus.Pass);
            WarnCount = report.Count(CheckStatus.Warn);
            FailCount = report.Count(CheckStatus.Fail);
            SkipCount = report.Count(CheckStatus.Skip) + report.Count(CheckStatus.Error);
            OnPropertyChanged(nameof(HasReport));
            TechnicalText = Loc.F("Validate_Technical", report.Checks.Count, report.Plugins.Count, report.Duration.TotalSeconds.ToString("0.00", Loc.Instance.Culture),
                report.ToolVersion, report.Schema, report.StartedAt.LocalDateTime.ToString("G", Loc.Instance.Culture));
            foreach (var plugin in report.Plugins.OrderBy(x => x.Name ?? x.FileName, StringComparer.OrdinalIgnoreCase))
            {
                Inventory.Add(plugin);
            }

            BuildTree(report);

            // Reveal groups one by one.
            foreach (var g in BuildGroups(report))
            {
                Groups.Add(g);
                await Task.Delay(Motion.Enabled ? 70 : 0).ConfigureAwait(true);
            }
        }
        finally
        {
            IsRunning = false;
        }
    }

    private IEnumerable<CheckResult> Ordered(ValidationReport r) =>
        r.Checks.Where(c => !ProblemsOnly || c.Status is not CheckStatus.Pass)
            .Where(c => FileFilter is null || (c.AffectedFile ?? c.AffectedPlugin ?? string.Empty).Replace('\\', '/').StartsWith(FileFilter, StringComparison.OrdinalIgnoreCase))
            .Where(c => FindingSearch.Length == 0 || $"{c.Id} {c.Name} {c.Description} {c.AffectedFile} {c.AffectedPlugin} {string.Join(' ', c.Evidence)}".Contains(FindingSearch, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => CheckItemViewModel.Rank(c.Status)).ThenBy(c => c.Id, StringComparer.Ordinal);

    private List<CheckGroup> BuildGroups(ValidationReport r)
    {
        Checks.Clear();
        foreach (var c in Ordered(r))
        {
            Checks.Add(new CheckItemViewModel(c) { IsExpanded = c.Status is CheckStatus.Fail or CheckStatus.Error });
        }

        Func<CheckItemViewModel, string> key = GroupMode switch
        {
            1 => i => i.CategoryText,
            2 => i => i.Location.Length == 0 ? Loc.T("Validate_PackWide") : i.Location,
            _ => i => i.StatusText,
        };
        return Checks.GroupBy(key)
            .Select(g => (Group: g, Worst: g.Min(i => CheckItemViewModel.Rank(i.Result.Status))))
            .OrderBy(x => GroupMode == 0 ? x.Worst : 0).ThenBy(x => x.Worst).ThenBy(x => x.Group.Key, StringComparer.CurrentCulture)
            .Select(x => new CheckGroup(x.Group.Key, x.Group.OrderBy(i => CheckItemViewModel.Rank(i.Result.Status)).First().Status, x.Group.ToList()))
            .ToList();
    }

    private void Rebuild()
    {
        if (_report is null)
        {
            return;
        }

        Groups.Clear();
        foreach (var g in BuildGroups(_report))
        {
            Groups.Add(g);
        }
    }

    /// <summary>Folder tree of every file that has a finding, with the worst status and problem count rolled up.</summary>
    private void BuildTree(ValidationReport r)
    {
        FileTree.Clear();
        var root = new FileNode(string.Empty, string.Empty, true);
        foreach (var c in r.Checks)
        {
            var loc = (c.AffectedFile ?? c.AffectedPlugin)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(loc))
            {
                continue;
            }

            var node = root;
            var parts = loc.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var path = string.Join('/', parts.Take(i + 1));
                var child = node.Children.FirstOrDefault(n => n.Name.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
                if (child is null)
                {
                    child = new FileNode(parts[i], path, i < parts.Length - 1);
                    node.Children.Add(child);
                }

                if (CheckItemViewModel.Rank(c.Status) < CheckItemViewModel.Rank(Enum.Parse<CheckStatus>(child.Status)))
                {
                    child.Status = c.Status.ToString();
                }

                if (c.Status is not CheckStatus.Pass)
                {
                    child.Problems++;
                }

                node = child;
            }
        }

        static void Sort(FileNode n)
        {
            n.Children.Sort((a, b) => a.IsFolder != b.IsFolder ? (a.IsFolder ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            n.Children.ForEach(Sort);
        }

        Sort(root);
        foreach (var n in root.Children)
        {
            FileTree.Add(n);
        }
    }

    private string HistoryDir => Path.Combine(AppPaths.History, Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(Target!).ToUpperInvariant())))[..16]);

    private void SaveHistory(ValidationReport r)
    {
        Directory.CreateDirectory(HistoryDir);
        File.WriteAllText(Path.Combine(HistoryDir, $"{DateTime.Now:yyyyMMdd-HHmmss}.json"), JsonReporter.Write(r));
        LoadHistory();
    }

    private void LoadHistory()
    {
        History.Clear();
        if (string.IsNullOrWhiteSpace(Target) || !Directory.Exists(HistoryDir))
        {
            return;
        }

        foreach (var f in Directory.EnumerateFiles(HistoryDir, "*.json").OrderDescending().Take(20))
        {
            try
            {
                if (JsonReporter.Read(File.ReadAllText(f)) is { } r)
                {
                    History.Add(new HistoryEntry(f, r.StartedAt, r.Overall));
                }
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }
    }

    [RelayCommand]
    private void Export(string? format)
    {
        if (_report is null)
        {
            return;
        }

        var html = format == "html";
        var dlg = new SaveFileDialog { FileName = $"packcheck-{_report.Target}.{(html ? "html" : "json")}", Filter = html ? "HTML (*.html)|*.html" : "JSON (*.json)|*.json" };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        File.WriteAllText(dlg.FileName, html ? HtmlReporter.Write(_report) : JsonReporter.Write(_report));
        MainViewModel.Shell?.Toast(Loc.T("Validate_Exported"));
        if (html)
        {
            Shell32.Open(dlg.FileName);
        }
    }
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly UpdateService _updates;
    private readonly AppState _state;

    public SettingsViewModel(AppSettings settings, UpdateService updates, AppState state)
    {
        _settings = settings;
        _updates = updates;
        _state = state;
        _language = Languages.First(l => l.Code == settings.Language);
        _themeIndex = (int)settings.Theme;
        _animationIndex = (int)settings.Animations;
        _autoUpdate = settings.AutoUpdate;
        _allowNetwork = settings.AllowNetworkProviders;
        _serverFolder = settings.DefaultServerFolder;
        _minimizeToTray = settings.MinimizeToTray;
        _startWithWindows = Autostart.IsEnabled();
        _aiProvider = settings.AiProvider;
        FillSuggested(settings.AiProvider);
        if (settings.AiModel is not null)
        {
            if (!AiModels.Contains(settings.AiModel))
            {
                AiModels.Insert(0, settings.AiModel);
            }

            _aiModel = settings.AiModel;
        }
        _curseForgeKey = settings.CurseForgeApiKey ?? string.Empty;
    }

    [ObservableProperty]
    private bool _minimizeToTray;

    [ObservableProperty]
    private bool _startWithWindows;

    // ----- AI -----
    public IReadOnlyList<Ai.AiProviderKind> AiProviders { get; } =
    [
        Ai.AiProviderKind.Anthropic, Ai.AiProviderKind.OpenAI, Ai.AiProviderKind.Gemini, Ai.AiProviderKind.Grok, Ai.AiProviderKind.DeepSeek,
        Ai.AiProviderKind.Mistral, Ai.AiProviderKind.Groq, Ai.AiProviderKind.OpenRouter, Ai.AiProviderKind.Ollama, Ai.AiProviderKind.LmStudio,
    ];

    public ObservableCollection<string> AiModels { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AiNeedsKey))]
    private Ai.AiProviderKind _aiProvider;

    [ObservableProperty]
    private string? _aiModel;

    [ObservableProperty]
    private string _aiKey = string.Empty;

    [ObservableProperty]
    private string? _aiStatus;

    [ObservableProperty]
    private bool _aiBusy;

    public bool AiNeedsKey => !Ai.AiProviders.IsLocal(AiProvider);

    public string? AiKeyPage => Ai.AiProviders.KeyPage(AiProvider);

    [RelayCommand]
    private void OpenAiKeyPage()
    {
        if (AiKeyPage is { } url)
        {
            Shell32.Open(url);
        }
    }

    private void FillSuggested(Ai.AiProviderKind kind)
    {
        AiModels.Clear();
        foreach (var m in Ai.AiProviders.SuggestedModels(kind))
        {
            AiModels.Add(m);
        }
    }

    partial void OnAiProviderChanged(Ai.AiProviderKind value)
    {
        FillSuggested(value);
        OnPropertyChanged(nameof(AiKeyPage));
        AiStatus = null;
        if (value != _settings.AiProvider)
        {
            AiKey = string.Empty;
        }
    }

    partial void OnAiModelChanged(string? value)
    {
        if (value is not null)
        {
            _settings.AiProvider = AiProvider;
            _settings.AiModel = value;
            _settings.Save();
            AiStatus = Loc.F("Ai_Selected", AiProvider, value);
        }
    }

    /// <summary>Saves the key (encrypted) and fetches the provider's models so the user can pick one.</summary>
    [RelayCommand]
    private async Task AiConnectAsync()
    {
        AiBusy = true;
        AiStatus = null;
        try
        {
            var key = AiNeedsKey ? AiKey.Trim() : null;
            if (AiNeedsKey && string.IsNullOrEmpty(key))
            {
                key = Secrets.Unprotect(_settings.AiKey);
            }

            var http = (System.Net.Http.HttpClient)App.Services.GetService(typeof(System.Net.Http.HttpClient))!;
            var models = await Ai.AiProviders.ListModelsAsync(AiProvider, key, http, CancellationToken.None).ConfigureAwait(true);
            if (AiNeedsKey && !string.IsNullOrEmpty(AiKey))
            {
                _settings.AiKey = Secrets.Protect(AiKey.Trim());
                AiKey = string.Empty;
            }

            _settings.AiProvider = AiProvider;
            _settings.Save();
            AiModels.Clear();
            foreach (var m in models)
            {
                AiModels.Add(m);
            }

            AiStatus = Loc.F("Ai_ModelsFound", models.Count);
            AiModel = models.Contains(_settings.AiModel ?? string.Empty) ? _settings.AiModel : null;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or Anthropic.Exceptions.AnthropicApiException or KeyNotFoundException)
        {
            AiStatus = Loc.F("Ai_ConnectFailed", ex.Message);
        }
        finally
        {
            AiBusy = false;
        }
    }

    partial void OnStartWithWindowsChanged(bool value) => Autostart.Set(value);

    [ObservableProperty]
    private string _curseForgeKey;

    partial void OnMinimizeToTrayChanged(bool value)
    {
        _settings.MinimizeToTray = value;
        _settings.Save();
    }

    partial void OnCurseForgeKeyChanged(string value)
    {
        // Saved as soon as it is typed or pasted (no Enter needed); spaces, quotes and line breaks are removed.
        _settings.CurseForgeApiKey = PackCheck.Providers.CurseForgeKey.Normalize(value);
        _settings.Save();
        CurseForgeStatus = _settings.CurseForgeApiKey is { } k && !PackCheck.Providers.CurseForgeKey.LooksValid(k) ? Loc.T("CurseForge_FormatHint") : null;
    }

    [ObservableProperty]
    private string? _curseForgeStatus;

    [ObservableProperty]
    private bool _curseForgeOk;

    /// <summary>Asks CurseForge whether it accepts the key and says so in plain words.</summary>
    [RelayCommand]
    private async Task VerifyCurseForgeAsync()
    {
        CurseForgeOk = false;
        CurseForgeStatus = Loc.T("CurseForge_Checking");
        var http = (System.Net.Http.HttpClient)App.Services.GetService(typeof(System.Net.Http.HttpClient))!;
        var (status, detail) = await PackCheck.Providers.CurseForgeKey.CheckAsync(http, _settings.CurseForgeApiKey, CancellationToken.None).ConfigureAwait(true);
        CurseForgeOk = status == PackCheck.Providers.CurseForgeKeyStatus.Valid;
        CurseForgeStatus = status switch
        {
            PackCheck.Providers.CurseForgeKeyStatus.Valid => Loc.T("CurseForge_Valid"),
            PackCheck.Providers.CurseForgeKeyStatus.Missing => Loc.T("CurseForge_Missing"),
            PackCheck.Providers.CurseForgeKeyStatus.Rejected => Loc.F("CurseForge_Rejected", detail ?? "403"),
            _ => Loc.F("CurseForge_Unreachable", detail ?? "?"),
        };
    }

    public IReadOnlyList<LanguageOption> Languages { get; } = Loc.Languages.Select(l => new LanguageOption(l.Code, l.NativeName)).ToList();

    public IReadOnlyList<JavaRuntime> Runtimes => _state.JavaRuntimes;

    public string CurrentVersion => UpdateService.CurrentVersion.ToString(3);

    [ObservableProperty]
    private LanguageOption _language;

    [ObservableProperty]
    private int _themeIndex;

    [ObservableProperty]
    private int _animationIndex;

    [ObservableProperty]
    private bool _autoUpdate;

    [ObservableProperty]
    private bool _allowNetwork;

    [ObservableProperty]
    private string _serverFolder;

    [ObservableProperty]
    private bool _isChecking;

    partial void OnLanguageChanged(LanguageOption value)
    {
        Loc.Instance.Culture = CultureInfo.GetCultureInfo(value.Code);
        _settings.Language = value.Code;
        _settings.Save();
    }

    partial void OnThemeIndexChanged(int value)
    {
        _settings.Theme = (ThemeMode)value;
        ThemeManager.Apply(_settings.Theme);
        _settings.Save();
    }

    partial void OnAnimationIndexChanged(int value)
    {
        _settings.Animations = (AnimationLevel)value;
        Motion.Level = _settings.Animations;
        _settings.Save();
    }

    partial void OnAutoUpdateChanged(bool value)
    {
        _settings.AutoUpdate = value;
        _settings.Save();
    }

    partial void OnAllowNetworkChanged(bool value)
    {
        _settings.AllowNetworkProviders = value;
        _settings.Save();
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog() == true)
        {
            ServerFolder = dlg.FolderName;
            _settings.DefaultServerFolder = ServerFolder;
            _settings.Save();
        }
    }

    [RelayCommand]
    private async Task CheckNowAsync()
    {
        IsChecking = true;
        var d = await _updates.CheckAsync(AutoUpdate, TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(true);
        IsChecking = false;
        if (d.Action == UpdateAction.None)
        {
            MainViewModel.Shell?.Toast(Loc.F("Settings_UpToDate", CurrentVersion));
        }
        else if (MainViewModel.Shell is { } shell)
        {
            await shell.HandleUpdateDecisionAsync(d).ConfigureAwait(true);
        }
    }
}

public sealed record LanguageOption(string Code, string Name)
{
    public string Flag => $"pack://application:,,,/PackCheck;component/Assets/Flags/flag-{Code}.png";

    public override string ToString() => Name;
}

public sealed partial class AboutViewModel : ObservableObject
{
    public string Version => Loc.F("About_Version", UpdateService.CurrentVersion.ToString(3));

    [RelayCommand]
    private static void OpenRepo() => Shell32.Open("https://github.com/X1NPAR1/PackCheck");

    [RelayCommand]
    private static void OpenLicenses() => Shell32.Open(Path.Combine(AppContext.BaseDirectory, "licenses"));

    [RelayCommand]
    private static void OpenReleases() => Shell32.Open("https://github.com/X1NPAR1/PackCheck/releases");

    [RelayCommand]
    private static void ReportIssue() => Shell32.Open("https://github.com/X1NPAR1/PackCheck/issues/new");

    [RelayCommand]
    private static void OpenData() => Shell32.Open(AppPaths.DataRoot);

    [RelayCommand]
    private static void OpenLogs()
    {
        Directory.CreateDirectory(AppPaths.Logs);
        Shell32.Open(AppPaths.Logs);
    }

    /// <summary>Technical facts shown on the page and copied for bug reports.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> SystemInfo { get; } = BuildInfo();

    private static List<KeyValuePair<string, string>> BuildInfo()
    {
        var marker = Path.Combine(AppContext.BaseDirectory, "install-type.txt");
        var install = File.Exists(marker) ? File.ReadAllText(marker).Trim().ToUpperInvariant() switch { "MSI" => "MSI", "EXE" => "Setup (EXE)", var o => o } : Loc.T("About_Portable");
        return
        [
            new(Loc.T("About_AppVersion"), UpdateService.CurrentVersion.ToString(3)),
            new(Loc.T("About_Install"), install),
            new("Windows", $"{Environment.OSVersion.Version} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})"),
            new(".NET", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription),
            new(Loc.T("About_Language"), Loc.Instance.Culture.Name),
            new(Loc.T("About_AppFolder"), AppContext.BaseDirectory.TrimEnd('\\')),
            new(Loc.T("About_DataFolder"), AppPaths.DataRoot),
        ];
    }

    [RelayCommand]
    private void CopyInfo()
    {
        System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, SystemInfo.Select(kv => $"{kv.Key}: {kv.Value}")));
        MainViewModel.Shell?.Toast(Loc.T("About_Copied"));
    }
}
