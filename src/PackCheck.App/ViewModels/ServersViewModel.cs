using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PackCheck.App.Infrastructure;
using PackCheck.Core;
using PackCheck.Servers;
using PackCheck.Validation;

namespace PackCheck.App.ViewModels;

public sealed partial class ServersViewModel(ServerRegistry registry, AppSettings settings, ServerSources sources, ServerCreator creator, JavaRuntimes java, AppState state) : ObservableObject
{
    public ObservableCollection<ServerItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private ServerItemViewModel? _selected;

    [ObservableProperty]
    private CreateServerViewModel? _wizard;

    public bool IsEmpty => Items.Count == 0;

    public void Load()
    {
        Items.Clear();
        foreach (var def in registry.Load().OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Items.Add(new ServerItemViewModel(def, registry, settings));
        }

        Selected = Items.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void NewServer() => Wizard = new CreateServerViewModel(sources, creator, java, state, settings, OnCreated, () => Wizard = null);

    private void OnCreated(ServerDefinition def)
    {
        registry.Upsert(def);
        var vm = new ServerItemViewModel(def, registry, settings);
        Items.Add(vm);
        Selected = vm;
        Wizard = null;
        OnPropertyChanged(nameof(IsEmpty));
        MainViewModel.Shell?.Toast(Loc.T("Wizard_Step_done"));
    }

    [RelayCommand]
    private void ImportServer()
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        var dir = dlg.FolderName;
        var jar = Directory.EnumerateFiles(dir, "*.jar").Select(Path.GetFileName).FirstOrDefault();
        var mc = PackManifest.TryReadVersion(dir)?.ToString() ?? "unknown";
        var props = File.Exists(Path.Combine(dir, "server.properties")) ? ServerProperties.Parse(File.ReadAllText(Path.Combine(dir, "server.properties"))) : [];
        var def = new ServerDefinition
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = Path.GetFileName(dir),
            Directory = dir,
            Platform = jar?.Contains("paper", StringComparison.OrdinalIgnoreCase) == true ? ServerPlatform.Paper : ServerPlatform.Vanilla,
            MinecraftVersion = mc,
            JarFile = jar ?? "server.jar",
            JavaPath = state.JavaRuntimes.FirstOrDefault()?.JavaExe ?? "java.exe",
            Port = props.TryGetValue("server-port", out var p) && int.TryParse(p, out var port) ? port : 25565,
        };
        OnCreated(def);
    }

    [RelayCommand]
    private async Task RemoveAsync(ServerItemViewModel? item)
    {
        if (item is null || item.IsBusy)
        {
            return;
        }

        if (MainViewModel.Shell is { } shell && !await shell.ConfirmAsync(Loc.T("Servers_Remove"), item.Name, Loc.T("Servers_Remove"), danger: true).ConfigureAwait(true))
        {
            return;
        }

        registry.Remove(item.Definition.Id);
        Items.Remove(item);
        Selected = Items.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    public async Task StopAllAsync()
    {
        foreach (var s in Items)
        {
            await s.ShutdownAsync().ConfigureAwait(true);
        }
    }
}

public sealed record ConsoleLine(string Text, string Level);

public sealed record PluginRow(string File, string? Name, string? Version, bool Enabled);

public sealed partial class ServerItemViewModel : ObservableObject
{
    private const int MaxConsoleLines = 5000;
    private readonly ServerRegistry _registry;
    private readonly AppSettings _settings;
    private readonly ServerProcess _process;
    private readonly DispatcherTimer _statsTimer;
    private readonly List<ConsoleLine> _allLines = [];
    private readonly List<string> _history = [];
    private int _historyIndex;
    private TimeSpan _lastCpu;
    private DateTime _lastSample;

    public ServerItemViewModel(ServerDefinition def, ServerRegistry registry, AppSettings settings)
    {
        Definition = def;
        _registry = registry;
        _settings = settings;
        _process = new ServerProcess(def);
        _process.Output += (_, line) => Application.Current?.Dispatcher.BeginInvoke(() => AppendLine(line));
        _process.StateChanged += (_, s) => Application.Current?.Dispatcher.BeginInvoke(() => State = s);
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) => SampleStats();
        LoadProperties();
        RefreshBackups();
        RefreshPlugins();
    }

    public ServerDefinition Definition { get; private set; }

    public string Name => Definition.Name;

    public string Subtitle => $"{Definition.Platform} {Definition.MinecraftVersion} · :{Definition.Port}";

    public ObservableCollection<ConsoleLine> Console { get; } = [];

    public ObservableCollection<PropertyRow> Properties { get; } = [];

    public ObservableCollection<BackupInfo> BackupList { get; } = [];

    public ObservableCollection<PluginRow> PluginList { get; } = [];

    public ObservableCollection<double> MemoryHistory { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsBusy), nameof(StateText), nameof(CanEdit))]
    private ServerState _state;

    [ObservableProperty]
    private string _command = string.Empty;

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private string _memoryText = "–";

    [ObservableProperty]
    private string _cpuText = "–";

    [ObservableProperty]
    private int _tab;

    public bool IsRunning => State is ServerState.Running or ServerState.Starting;

    public bool IsBusy => State is ServerState.Running or ServerState.Starting or ServerState.Stopping;

    public bool CanEdit => !IsBusy;

    public string StateText => Loc.T("State_" + State);

    partial void OnStateChanged(ServerState value)
    {
        if (IsBusy)
        {
            _lastCpu = TimeSpan.Zero;
            _statsTimer.Start();
        }
        else
        {
            _statsTimer.Stop();
            MemoryText = "–";
            CpuText = "–";
        }
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private static string LevelOf(string line) =>
        line.Contains("ERROR", StringComparison.Ordinal) || line.Contains("SEVERE", StringComparison.Ordinal) || line.Contains("Exception", StringComparison.Ordinal) ? "error"
        : line.Contains("WARN", StringComparison.Ordinal) ? "warn"
        : line.StartsWith('>') ? "cmd" : "info";

    private void AppendLine(string raw)
    {
        var text = AnsiStripper.Strip(raw);
        var line = new ConsoleLine(text, LevelOf(text));
        _allLines.Add(line);
        if (_allLines.Count > MaxConsoleLines)
        {
            _allLines.RemoveRange(0, _allLines.Count - MaxConsoleLines);
        }

        if (Filter.Length == 0 || text.Contains(Filter, StringComparison.OrdinalIgnoreCase))
        {
            Console.Add(line);
            if (Console.Count > MaxConsoleLines)
            {
                Console.RemoveAt(0);
            }
        }
    }

    private void ApplyFilter()
    {
        Console.Clear();
        foreach (var l in _allLines.Where(l => Filter.Length == 0 || l.Text.Contains(Filter, StringComparison.OrdinalIgnoreCase)))
        {
            Console.Add(l);
        }
    }

    private void SampleStats()
    {
        try
        {
            var p = _process.Process;
            if (p is null || p.HasExited)
            {
                return;
            }

            p.Refresh();
            var mb = p.WorkingSet64 / 1024d / 1024d;
            MemoryText = $"{mb:N0} MB";
            MemoryHistory.Add(mb);
            if (MemoryHistory.Count > 60)
            {
                MemoryHistory.RemoveAt(0);
            }

            var now = DateTime.UtcNow;
            var cpu = p.TotalProcessorTime;
            if (_lastCpu != TimeSpan.Zero)
            {
                var pct = (cpu - _lastCpu).TotalMilliseconds / (now - _lastSample).TotalMilliseconds / Environment.ProcessorCount * 100;
                CpuText = $"{Math.Clamp(pct, 0, 100):0}%";
            }

            _lastCpu = cpu;
            _lastSample = now;
        }
        catch (InvalidOperationException)
        {
        }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        try
        {
            Console.Clear();
            _allLines.Clear();
            await _process.StartAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MainViewModel.Shell?.Toast(ex.Message, isError: true);
        }
    }

    [RelayCommand]
    private Task StopAsync() => _process.StopAsync(TimeSpan.FromSeconds(45));

    [RelayCommand]
    private async Task RestartAsync()
    {
        await _process.StopAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(true);
        await StartAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task KillAsync()
    {
        if (MainViewModel.Shell is { } shell && await shell.ConfirmAsync(Loc.T("Servers_Kill"), Name, Loc.T("Servers_Kill"), danger: true).ConfigureAwait(true))
        {
            _process.Kill();
        }
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var cmd = Command.Trim();
        if (cmd.Length == 0)
        {
            return;
        }

        _history.Add(cmd);
        _historyIndex = _history.Count;
        AppendLine("> " + cmd);
        Command = string.Empty;
        await _process.SendCommandAsync(cmd.TrimStart('/')).ConfigureAwait(true);
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

    private void LoadProperties()
    {
        Properties.Clear();
        var file = Path.Combine(Definition.Directory, "server.properties");
        if (!File.Exists(file))
        {
            return;
        }

        foreach (var (k, v) in ServerProperties.Parse(File.ReadAllText(file)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Properties.Add(new PropertyRow(k, v));
        }
    }

    [RelayCommand]
    private void SaveProperties()
    {
        if (!CanEdit)
        {
            MainViewModel.Shell?.Toast(Loc.T("Servers_SaveWhileRunning"), isError: true);
            return;
        }

        var file = Path.Combine(Definition.Directory, "server.properties");
        var original = File.Exists(file) ? File.ReadAllText(file) : string.Empty;
        File.WriteAllText(file, ServerProperties.Update(original, Properties.ToDictionary(p => p.Key, p => p.Value.Trim())));
        if (Properties.FirstOrDefault(p => p.Key == "server-port") is { } port && int.TryParse(port.Value, out var n))
        {
            Definition = Definition with { Port = n };
            _registry.Upsert(Definition);
            OnPropertyChanged(nameof(Subtitle));
        }

        MainViewModel.Shell?.Toast(Loc.T("Servers_Saved"));
    }

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

    public string PluginsDir => Path.Combine(Definition.Directory, "plugins");

    public string DisabledDir => Path.Combine(Definition.Directory, "plugins", ".disabled");

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
        if (!await Preflight.ConfirmChangeAsync(Definition, () => File.Move(from, to), () => File.Move(to, from)).ConfigureAwait(true))
        {
            return;
        }

        RefreshPlugins();
    }

    [RelayCommand]
    private async Task AddPluginFileAsync()
    {
        var dlg = new OpenFileDialog { Filter = "Plugin (*.jar)|*.jar", Multiselect = true };
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

public sealed partial class PropertyRow(string key, string value) : ObservableObject
{
    public string Key { get; } = key;

    [ObservableProperty]
    private string _value = value;
}

/// <summary>Runs validation around a change: snapshot → apply → validate → offer rollback if new problems appear.</summary>
public static class Preflight
{
    public static async Task<bool> ConfirmChangeAsync(ServerDefinition server, Action apply, Action rollback)
    {
        var validator = (PackValidator)App.Services.GetService(typeof(PackValidator))!;
        var mc = MinecraftVersion.TryParse(server.MinecraftVersion, out var v) ? v : (MinecraftVersion?)null;
        var options = new ValidationOptions { MinecraftVersion = mc };
        var before = await validator.ValidateAsync(server.Directory, options).ConfigureAwait(true);
        try
        {
            apply();
        }
        catch (IOException ex)
        {
            MainViewModel.Shell?.Toast(ex.Message, isError: true);
            return false;
        }

        var after = await validator.ValidateAsync(server.Directory, options).ConfigureAwait(true);
        var broke = Reporting.ReportDiff.Compare(before, after).Where(c => c.Kind == "broke").Select(c => "• " + c.Text).ToList();
        if (broke.Count == 0 || MainViewModel.Shell is not { } shell)
        {
            return true;
        }

        if (await shell.ConfirmAsync(Loc.T("Preflight_Title"), Loc.F("Preflight_Body", string.Join("\n", broke)), Loc.T("Preflight_Apply"), danger: true).ConfigureAwait(true))
        {
            return true;
        }

        rollback();
        return false;
    }
}

public static partial class AnsiStripper
{
    [System.Text.RegularExpressions.GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial System.Text.RegularExpressions.Regex Ansi();

    public static string Strip(string s) => Ansi().Replace(s, string.Empty);
}
