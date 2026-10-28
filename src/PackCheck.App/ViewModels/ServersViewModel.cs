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

    /// <summary>Registers a server created elsewhere (assistant, scripts).</summary>
    public void Add(ServerDefinition def) => OnCreated(def);

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
