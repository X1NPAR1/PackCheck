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

        _ = LoadVersionsAsync();
    }

    public IReadOnlyList<ServerPlatform> Platforms { get; } = Enum.GetValues<ServerPlatform>();

    public ObservableCollection<string> Versions { get; } = [];

    public ObservableCollection<JavaRuntime> Runtimes { get; } = [];

    [ObservableProperty]
    private string _name = "My Server";

    [ObservableProperty]
    private string _folder;

    [ObservableProperty]
    private ServerPlatform _platform = ServerPlatform.Paper;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(JavaHint), nameof(NeedsJava), nameof(RequiredJava))]
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

    public int RequiredJava => MinecraftVersion.TryParse(Version, out var v) ? v.RequiredJava() : 21;

    public bool NeedsJava => Runtime is null || Runtime.FeatureVersion < RequiredJava;

    public string JavaHint => Loc.F("Wizard_JavaNeeded", Version ?? "?", RequiredJava);

    partial void OnPlatformChanged(ServerPlatform value) => _ = LoadVersionsAsync();

    partial void OnVersionChanged(string? value) => Runtime = Runtimes.FirstOrDefault(r => r.FeatureVersion >= RequiredJava) ?? Runtime;

    private async Task LoadVersionsAsync()
    {
        VersionsFailed = false;
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
            var req = new ServerCreator.Request(Name, Folder, Platform, Version, Runtime.JavaExe, MemoryMb, 25565, Seed, AcceptEula);
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

    [ObservableProperty]
    private bool _isExpanded;
}

public sealed record HistoryEntry(string File, DateTimeOffset At, OverallStatus Overall)
{
    public string Display => $"{At.LocalDateTime:g} · {Loc.T("Overall_" + Overall)}";
}

public sealed partial class ValidateViewModel(PackValidator validator, RuntimeTestRunner runner, AppState state) : ObservableObject
{
    private ValidationReport? _report;

    public ObservableCollection<CheckItemViewModel> Checks { get; } = [];

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

            // Reveal results one by one.
            foreach (var c in Ordered(report))
            {
                Checks.Add(new CheckItemViewModel(c) { IsExpanded = c.Status is CheckStatus.Fail or CheckStatus.Error });
                await Task.Delay(Motion.Enabled ? 60 : 0).ConfigureAwait(true);
            }
        }
        finally
        {
            IsRunning = false;
        }
    }

    private IEnumerable<CheckResult> Ordered(ValidationReport r) =>
        r.Checks.Where(c => !ProblemsOnly || c.Status is not CheckStatus.Pass)
            .OrderBy(c => c.Status switch { CheckStatus.Fail => 0, CheckStatus.Error => 1, CheckStatus.Warn => 2, CheckStatus.Skip => 3, _ => 4 });

    private void Rebuild()
    {
        if (_report is null)
        {
            return;
        }

        Checks.Clear();
        foreach (var c in Ordered(_report))
        {
            Checks.Add(new CheckItemViewModel(c) { IsExpanded = c.Status is CheckStatus.Fail or CheckStatus.Error });
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
        _curseForgeKey = settings.CurseForgeApiKey ?? string.Empty;
    }

    [ObservableProperty]
    private bool _minimizeToTray;

    [ObservableProperty]
    private bool _startWithWindows;

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
        _settings.CurseForgeApiKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        _settings.Save();
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
}
