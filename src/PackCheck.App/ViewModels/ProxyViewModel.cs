using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackCheck.App.Infrastructure;
using PackCheck.Servers;

namespace PackCheck.App.ViewModels;

public sealed partial class BackendRow(ServerItemViewModel server, bool selected) : ObservableObject
{
    public ServerItemViewModel Server { get; } = server;

    [ObservableProperty]
    private bool _selected = selected;

    [ObservableProperty]
    private bool _isDefault;
}

/// <summary>Connects a Velocity or BungeeCord server to backend servers and keeps their settings consistent.</summary>
public sealed partial class ProxyViewModel(ServersViewModel servers) : ObservableObject
{
    public ServersViewModel Servers { get; } = servers;

    public IEnumerable<ServerItemViewModel> Proxies => Servers.Items.Where(s => ProxyConfigurator.IsProxy(s.Definition.Platform));

    public ObservableCollection<BackendRow> Backends { get; } = [];

    public ObservableCollection<ProxyIssue> Issues { get; } = [];

    public bool HasProxy => Proxy is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProxy))]
    private ServerItemViewModel? _proxy;

    [ObservableProperty]
    private string _motd = "A PackCheck network";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _checked;

    public void Activate()
    {
        OnPropertyChanged(nameof(Proxies));
        Proxy ??= Proxies.FirstOrDefault();
        LoadBackends();
    }

    partial void OnProxyChanged(ServerItemViewModel? value) => LoadBackends();

    private void LoadBackends()
    {
        Backends.Clear();
        Issues.Clear();
        Checked = false;
        if (Proxy is null)
        {
            return;
        }

        string[] saved = [];
        string? savedDefault = null;
        var file = Path.Combine(Proxy.Definition.Directory, "packcheck-proxy.json");
        if (File.Exists(file))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            saved = doc.RootElement.GetProperty("backends").EnumerateArray().Select(e => e.GetString()!).ToArray();
            savedDefault = doc.RootElement.TryGetProperty("defaultBackend", out var d) ? d.GetString() : null;
        }

        foreach (var s in Servers.Items.Where(s => !ProxyConfigurator.IsProxy(s.Definition.Platform)))
        {
            Backends.Add(new BackendRow(s, saved.Contains(s.Name)) { IsDefault = s.Name == savedDefault });
        }

        if (!Backends.Any(b => b.IsDefault) && Backends.FirstOrDefault(b => b.Selected) is { } first)
        {
            first.IsDefault = true;
        }
    }

    private List<ProxyBackend> Selected() =>
        Backends.Where(b => b.Selected).Select(b => new ProxyBackend(b.Server.Name, b.Server.Definition.Directory, b.Server.Definition.Port, b.Server.Definition.Platform)).ToList();

    [RelayCommand]
    private void MakeDefault(BackendRow? row)
    {
        if (row is null)
        {
            return;
        }

        foreach (var b in Backends)
        {
            b.IsDefault = b == row;
        }

        row.Selected = true;
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (Proxy is null || MainViewModel.Shell is not { } shell)
        {
            return;
        }

        var backends = Selected();
        if (backends.Count == 0)
        {
            shell.Toast(Loc.T("Proxy_NoBackends"), isError: true);
            return;
        }

        if (Backends.Where(b => b.Selected).Any(b => b.Server.IsBusy) || Proxy.IsBusy)
        {
            shell.Toast(Loc.T("Proxy_StopFirst"), isError: true);
            return;
        }

        if (!await shell.ConfirmAsync(Loc.T("Proxy_Apply"), Loc.F("Proxy_ApplyBody", string.Join(", ", backends.Select(b => b.Name))), Loc.T("Proxy_Apply")).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var def = Backends.FirstOrDefault(b => b.IsDefault && b.Selected)?.Server.Name ?? backends[0].Name;
            ProxyConfigurator.Apply(Proxy.Definition.Directory, Proxy.Definition.Platform, Proxy.Definition.Port, Motd, backends, def);
            shell.Toast(Loc.T("Proxy_Applied"));
            Check();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            shell.Toast(ex.Message, isError: true);
        }
    }

    [RelayCommand]
    private void Check()
    {
        if (Proxy is null)
        {
            return;
        }

        Issues.Clear();
        var secretFile = Path.Combine(Proxy.Definition.Directory, "forwarding.secret");
        var secret = File.Exists(secretFile) ? File.ReadAllText(secretFile).Trim() : null;
        foreach (var i in ProxyConfigurator.Check(Proxy.Definition.Platform, Proxy.Definition.Port, Selected(), secret))
        {
            Issues.Add(i);
        }

        Checked = true;
    }

    /// <summary>Backends first (they must be ready before players arrive), then the proxy.</summary>
    [RelayCommand]
    private async Task StartAllAsync()
    {
        if (Proxy is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            foreach (var b in Backends.Where(b => b.Selected && !b.Server.IsBusy))
            {
                await b.Server.StartCommand.ExecuteAsync(null).ConfigureAwait(true);
            }

            if (!Proxy.IsBusy)
            {
                await Proxy.StartCommand.ExecuteAsync(null).ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StopAllAsync()
    {
        if (Proxy is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (Proxy.IsBusy)
            {
                await Proxy.StopCommand.ExecuteAsync(null).ConfigureAwait(true);
            }

            foreach (var b in Backends.Where(b => b.Selected && b.Server.IsBusy))
            {
                await b.Server.StopCommand.ExecuteAsync(null).ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CreateProxy()
    {
        if (MainViewModel.Shell is { } shell)
        {
            shell.Navigate(shell.Servers);
            shell.Servers.NewServerCommand.Execute(null);
        }
    }
}
