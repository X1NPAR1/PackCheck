using System.Net;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackCheck.App.Infrastructure;
using PackCheck.Servers;

namespace PackCheck.App.ViewModels;

/// <summary>Makes a server reachable from the internet (firewall + UPnP) and back to local-only.</summary>
public sealed partial class OnlineViewModel(ServersViewModel servers, AppSettings settings, HttpClient http) : ObservableObject
{
    private readonly Upnp _upnp = new(http);

    public ServersViewModel Servers { get; } = servers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServer), nameof(Port))]
    private ServerItemViewModel? _server;

    [ObservableProperty]
    private bool _isOnline;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _publicIp;

    [ObservableProperty]
    private string? _localIp;

    [ObservableProperty]
    private string? _address;

    [ObservableProperty]
    private bool _firewallOk;

    [ObservableProperty]
    private bool? _upnpOk;

    [ObservableProperty]
    private string? _warning;

    [ObservableProperty]
    private string? _log;

    [ObservableProperty]
    private string _domain = string.Empty;

    [ObservableProperty]
    private string _duckDomain = string.Empty;

    [ObservableProperty]
    private string _duckToken = string.Empty;

    public bool HasServer => Server is not null;

    public int Port => Server?.Definition.Port ?? 25565;

    public string ARecord => Domain.Length == 0 ? string.Empty : $"{Domain.Trim()}.   A   {PublicIp ?? "<public IP>"}";

    public string SrvRecord => Domain.Length == 0 ? string.Empty : $"_minecraft._tcp.{Domain.Trim()}.   SRV   0 5 {Port} {Domain.Trim()}.";

    partial void OnDomainChanged(string value)
    {
        OnPropertyChanged(nameof(ARecord));
        OnPropertyChanged(nameof(SrvRecord));
        settings.OnlineDomain = value;
        settings.Save();
    }

    partial void OnServerChanged(ServerItemViewModel? value) => _ = RefreshAsync();

    public void Activate()
    {
        Domain = settings.OnlineDomain ?? string.Empty;
        DuckDomain = settings.DuckDnsDomain ?? string.Empty;
        DuckToken = Secrets.Unprotect(settings.DuckDnsToken) ?? string.Empty;
        Server ??= Servers.Selected ?? Servers.Items.FirstOrDefault();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (Server is null)
        {
            return;
        }

        LocalIp = PublicAddress.LocalAddress()?.ToString();
        var ip = await PublicAddress.GetAsync(http, CancellationToken.None).ConfigureAwait(true);
        PublicIp = ip?.ToString();
        FirewallOk = await Task.Run(() => Firewall.RuleExists(Port)).ConfigureAwait(true);
        IsOnline = settings.OnlinePorts.Contains(Port) && FirewallOk;
        Address = IsOnline && PublicIp is not null ? $"{PublicIp}:{Port}" : null;
        OnPropertyChanged(nameof(ARecord));
        OnPropertyChanged(nameof(SrvRecord));
    }

    private void Note(string line) => Log = (Log is null ? string.Empty : Log + Environment.NewLine) + line;

    [RelayCommand]
    private async Task GoOnlineAsync()
    {
        if (Server is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        Warning = null;
        Log = null;
        try
        {
            // 1) Windows firewall (UAC)
            Note(Loc.T("Online_StepFirewall"));
            FirewallOk = await Firewall.ChangeElevatedAsync(Port, add: true).ConfigureAwait(true) && Firewall.RuleExists(Port);
            if (!FirewallOk)
            {
                Warning = Loc.T("Online_UacDeclined");
                return;
            }

            // 2) Router port forwarding via UPnP
            Note(Loc.T("Online_StepUpnp"));
            var local = PublicAddress.LocalAddress();
            UpnpOk = local is not null && await _upnp.DiscoverAsync(CancellationToken.None).ConfigureAwait(true)
                && await _upnp.MapAsync(Port, "TCP", local, $"PackCheck {Server.Name}", CancellationToken.None).ConfigureAwait(true)
                && await _upnp.MapAsync(Port, "UDP", local, $"PackCheck {Server.Name}", CancellationToken.None).ConfigureAwait(true);

            // 3) Public address + CGNAT check
            Note(Loc.T("Online_StepAddress"));
            var pub = await PublicAddress.GetAsync(http, CancellationToken.None).ConfigureAwait(true);
            PublicIp = pub?.ToString();
            var routerWan = UpnpOk == true ? await _upnp.ExternalAddressAsync(CancellationToken.None).ConfigureAwait(true) : null;
            if (routerWan is not null && (PublicAddress.IsCarrierGradeOrPrivate(routerWan) || (pub is not null && !routerWan.Equals(pub))))
            {
                Warning = Loc.F("Online_Cgnat", routerWan);
            }
            else if (UpnpOk != true)
            {
                Warning = Loc.F("Online_UpnpManual", Port, LocalIp ?? "?");
            }

            if (!settings.OnlinePorts.Contains(Port))
            {
                settings.OnlinePorts.Add(Port);
                settings.Save();
            }

            IsOnline = true;
            Address = PublicIp is null ? null : $"{PublicIp}:{Port}";
            if (DuckDomain.Length > 0 && DuckToken.Length > 0)
            {
                await UpdateDuckAsync().ConfigureAwait(true);
            }

            Note(Loc.T("Online_Done"));
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException or InvalidOperationException or TaskCanceledException)
        {
            Warning = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task GoOfflineAsync()
    {
        if (Server is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        Warning = null;
        try
        {
            if (await _upnp.DiscoverAsync(CancellationToken.None).ConfigureAwait(true))
            {
                await _upnp.UnmapAsync(Port, "TCP", CancellationToken.None).ConfigureAwait(true);
                await _upnp.UnmapAsync(Port, "UDP", CancellationToken.None).ConfigureAwait(true);
            }

            var removed = await Firewall.ChangeElevatedAsync(Port, add: false).ConfigureAwait(true);
            if (!removed && Firewall.RuleExists(Port))
            {
                Warning = Loc.T("Online_UacDeclined");
                return;
            }

            settings.OnlinePorts.Remove(Port);
            settings.Save();
            IsOnline = false;
            FirewallOk = false;
            UpnpOk = null;
            Address = null;
            MainViewModel.Shell?.Toast(Loc.T("Online_NowLocal"));
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException or TaskCanceledException)
        {
            Warning = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>On exit: removes the router mappings and firewall rules of every port PackCheck opened.</summary>
    public async Task<bool> CloseAllAsync()
    {
        var ok = true;
        var upnp = false;
        try
        {
            upnp = await _upnp.DiscoverAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException or TaskCanceledException)
        {
        }

        foreach (var port in settings.OnlinePorts.ToList())
        {
            try
            {
                if (upnp)
                {
                    await _upnp.UnmapAsync(port, "TCP", CancellationToken.None).ConfigureAwait(true);
                    await _upnp.UnmapAsync(port, "UDP", CancellationToken.None).ConfigureAwait(true);
                }

                if (await Firewall.ChangeElevatedAsync(port, add: false).ConfigureAwait(true) || !Firewall.RuleExists(port))
                {
                    settings.OnlinePorts.Remove(port);
                }
                else
                {
                    ok = false;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException or TaskCanceledException)
            {
                ok = false;
            }
        }

        settings.Save();
        IsOnline = false;
        return ok;
    }

    [RelayCommand]
    private void CopyAddress()
    {
        var text = Domain.Length > 0 ? Domain.Trim() : Address;
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
            MainViewModel.Shell?.Toast(Loc.T("Online_Copied"));
        }
    }

    [RelayCommand]
    private void CopyRecord(string? record)
    {
        if (!string.IsNullOrEmpty(record))
        {
            Clipboard.SetText(record);
            MainViewModel.Shell?.Toast(Loc.T("Online_Copied"));
        }
    }

    [RelayCommand]
    private async Task UpdateDuckAsync()
    {
        settings.DuckDnsDomain = DuckDomain.Trim();
        settings.DuckDnsToken = Secrets.Protect(DuckToken.Trim());
        settings.Save();
        try
        {
            var ok = await PublicAddress.UpdateDuckDnsAsync(http, DuckDomain, DuckToken, CancellationToken.None).ConfigureAwait(true);
            MainViewModel.Shell?.Toast(ok ? Loc.F("Online_DuckOk", DuckDomain.Trim()) : Loc.T("Online_DuckFail"), isError: !ok);
        }
        catch (HttpRequestException ex)
        {
            MainViewModel.Shell?.Toast(ex.Message, isError: true);
        }
    }
}

/// <summary>Windows DPAPI (current user) for small secrets such as DNS tokens and API keys.</summary>
public static class Secrets
{
    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain))
        {
            return null;
        }

        var bytes = System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(plain), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        try
        {
            var bytes = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(stored), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
