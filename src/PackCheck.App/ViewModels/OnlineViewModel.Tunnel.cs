using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackCheck.App.Infrastructure;
using PackCheck.Servers;

namespace PackCheck.App.ViewModels;

/// <summary>playit.gg tunnel card: reach the server from the internet without port forwarding.</summary>
public sealed partial class OnlineViewModel
{
    private bool _tunnelHooked;

    [ObservableProperty]
    private PlayitState _tunnelState = PlayitState.Stopped;

    [ObservableProperty]
    private string? _tunnelClaimUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTunnelAddress))]
    private string? _tunnelAddress;

    [ObservableProperty]
    private string? _tunnelError;

    [ObservableProperty]
    private string _tunnelLog = string.Empty;

    [ObservableProperty]
    private bool _showTunnelLog;

    public bool TunnelActive => TunnelState is PlayitState.Downloading or PlayitState.Starting or PlayitState.WaitingForClaim or PlayitState.Running;

    public bool HasTunnelAddress => !string.IsNullOrEmpty(TunnelAddress);

    public string TunnelStatusText => TunnelState switch
    {
        PlayitState.Downloading => Loc.T("Tunnel_StateDownloading"),
        PlayitState.Starting => Loc.T("Tunnel_StateStarting"),
        PlayitState.WaitingForClaim => Loc.T("Tunnel_StateClaim"),
        PlayitState.Running => Loc.T("Tunnel_StateRunning"),
        PlayitState.Failed => Loc.T("Tunnel_StateFailed"),
        _ => Loc.T("Tunnel_StateStopped"),
    };

    public string TunnelTarget => $"127.0.0.1:{Port}";

    partial void OnTunnelStateChanged(PlayitState value)
    {
        OnPropertyChanged(nameof(TunnelActive));
        OnPropertyChanged(nameof(TunnelStatusText));
        StartTunnelCommand.NotifyCanExecuteChanged();
        StopTunnelCommand.NotifyCanExecuteChanged();
    }

    private void HookTunnel()
    {
        if (_tunnelHooked)
        {
            return;
        }

        _tunnelHooked = true;
        tunnel.Changed += () => Application.Current?.Dispatcher.BeginInvoke(SyncTunnel);
        SyncTunnel();
    }

    private void SyncTunnel()
    {
        TunnelState = tunnel.State;
        TunnelClaimUrl = tunnel.ClaimUrl;
        if (tunnel.Address is { } address)
        {
            TunnelAddress = address;
        }

        TunnelError = tunnel.Error;
        TunnelLog = string.Join(Environment.NewLine, tunnel.LogLines);
        if (tunnel.OutputUnrecognized || tunnel.State == PlayitState.Failed)
        {
            ShowTunnelLog = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartTunnel))]
    private async Task StartTunnelAsync()
    {
        HookTunnel();
        ShowTunnelLog = false;
        await tunnel.StartAsync(TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(true);
        SyncTunnel();
    }

    private bool CanStartTunnel() => !TunnelActive;

    [RelayCommand(CanExecute = nameof(TunnelActive))]
    private void StopTunnel()
    {
        tunnel.Stop();
        SyncTunnel();
    }

    [RelayCommand]
    private void OpenTunnelClaim()
    {
        if (TunnelClaimUrl is { } url && url.StartsWith("https://playit.gg/", StringComparison.Ordinal))
        {
            Shell32.Open(url);
        }
    }

    [RelayCommand]
    private static void OpenPlayitDashboard() => Shell32.Open("https://playit.gg/account/tunnels");

    [RelayCommand]
    private void CopyTunnelAddress() => CopyText(TunnelAddress);

    [RelayCommand]
    private void ToggleTunnelLog() => ShowTunnelLog = !ShowTunnelLog;
}
