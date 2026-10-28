using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackCheck.App.Infrastructure;
using PackCheck.Updater;

namespace PackCheck.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly UpdateService _updates;
    private CancellationTokenSource? _toastCts;

    public MainViewModel(ServersViewModel servers, ValidateViewModel validate, PluginsViewModel plugins, ModsViewModel mods, SettingsViewModel settings, AboutViewModel about, UpdateService updates)
    {
        Servers = servers;
        Validate = validate;
        Plugins = plugins;
        Mods = mods;
        Settings = settings;
        About = about;
        _updates = updates;
        _currentPage = servers;
        Shell = this;
        servers.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ServersViewModel.Selected) or nameof(ServersViewModel.IsEmpty))
            {
                UpdateNavRules();
            }
        };
        UpdateNavRules();
    }

    [ObservableProperty]
    private bool _pluginsEnabled;

    [ObservableProperty]
    private bool _modsEnabled;

    [ObservableProperty]
    private string? _pluginsDisabledReason;

    [ObservableProperty]
    private string? _modsDisabledReason;

    /// <summary>Plugins/Mods pages follow the selected server's platform; disabled items explain why.</summary>
    public void UpdateNavRules()
    {
        var s = Servers.Selected;
        PluginsEnabled = s?.UsesPlugins == true;
        ModsEnabled = s?.UsesMods == true;
        string Reason(bool forMods) => s is null ? Loc.T("Nav_NeedServer")
            : !s.UsesPlugins && !s.UsesMods ? Loc.F("Nav_VanillaNoContent", s.Definition.Platform)
            : forMods ? Loc.F("Nav_ModsOff", s.Definition.Platform) : Loc.F("Nav_PluginsOff", s.Definition.Platform);
        PluginsDisabledReason = PluginsEnabled ? null : Reason(false);
        ModsDisabledReason = ModsEnabled ? null : Reason(true);
        if ((NavIndex == 2 && !PluginsEnabled) || (NavIndex == 5 && !ModsEnabled))
        {
            NavIndex = 0;
        }
    }

    public static MainViewModel? Shell { get; private set; }

    public ServersViewModel Servers { get; }
    public ValidateViewModel Validate { get; }
    public PluginsViewModel Plugins { get; }
    public ModsViewModel Mods { get; }
    public SettingsViewModel Settings { get; }
    public AboutViewModel About { get; }

    [ObservableProperty]
    private object _currentPage;

    [ObservableProperty]
    private int _navIndex;

    [ObservableProperty]
    private string? _toastText;

    [ObservableProperty]
    private bool _toastIsError;

    [ObservableProperty]
    private ReleaseInfo? _pendingRelease;

    [ObservableProperty]
    private bool _isUpdateDialogOpen;

    [ObservableProperty]
    private string? _updateDialogHint;

    [ObservableProperty]
    private bool _isInstallingUpdate;

    [ObservableProperty]
    private string? _updateProgressText;

    [ObservableProperty]
    private double _updateProgress;

    [ObservableProperty]
    private ConfirmRequest? _confirm;

    public bool HasPendingRelease => PendingRelease is not null;

    partial void OnPendingReleaseChanged(ReleaseInfo? value) => OnPropertyChanged(nameof(HasPendingRelease));

    partial void OnNavIndexChanged(int value) => CurrentPage = value switch
    {
        1 => Validate,
        2 => ActivatePlugins(),
        3 => Settings,
        4 => About,
        5 => RefreshMods(),
        _ => Servers,
    };

    private ModsViewModel RefreshMods()
    {
        Mods.Activate();
        return Mods;
    }

    private PluginsViewModel ActivatePlugins()
    {
        Plugins.Activate();
        return Plugins;
    }

    public void Navigate(object page)
    {
        NavIndex = page switch
        {
            ValidateViewModel => 1,
            PluginsViewModel => 2,
            SettingsViewModel => 3,
            AboutViewModel => 4,
            ModsViewModel => 5,
            _ => 0,
        };
    }

    public async void Toast(string text, bool isError = false)
    {
        _toastCts?.Cancel();
        _toastCts = new CancellationTokenSource();
        var token = _toastCts.Token;
        ToastIsError = isError;
        ToastText = text;
        try
        {
            await Task.Delay(isError ? 6000 : 3500, token).ConfigureAwait(true);
            ToastText = null;
        }
        catch (TaskCanceledException)
        {
        }
    }

    /// <summary>Shows an in-app confirmation dialog and returns the choice.</summary>
    public Task<bool> ConfirmAsync(string title, string body, string confirmText, bool danger = false)
    {
        var req = new ConfirmRequest(title, body, confirmText, danger);
        Confirm = req;
        return req.Result.Task.ContinueWith(t => { Confirm = null; return t.Result; }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    [RelayCommand]
    private void ConfirmYes() => Confirm?.Result.TrySetResult(true);

    [RelayCommand]
    private void ConfirmNo() => Confirm?.Result.TrySetResult(false);

    public async Task HandleUpdateDecisionAsync(UpdateDecision decision)
    {
        if (decision.Release is null || decision.Action == UpdateAction.None)
        {
            return;
        }

        PendingRelease = decision.Release;
        switch (decision.Action)
        {
            case UpdateAction.AutoInstall:
                UpdateDialogHint = Loc.F("Update_Auto", decision.Release.Tag);
                IsUpdateDialogOpen = true;
                await InstallUpdateAsync().ConfigureAwait(true);
                break;
            case UpdateAction.ForceInstall:
                UpdateDialogHint = Loc.F("Update_Forced", decision.Release.Tag);
                IsUpdateDialogOpen = true;
                await InstallUpdateAsync().ConfigureAwait(true);
                break;
            case UpdateAction.Prompt:
                _updates.MarkPrompted(decision.Release);
                UpdateDialogHint = decision.DaysLeft <= 1 ? Loc.T("Update_LastDay") : Loc.F("Update_DaysLeft", decision.DaysLeft);
                IsUpdateDialogOpen = true;
                break;
        }
    }

    [RelayCommand]
    private void ShowUpdate()
    {
        if (PendingRelease is not null)
        {
            IsUpdateDialogOpen = true;
        }
    }

    [RelayCommand]
    private void PostponeUpdate() => IsUpdateDialogOpen = false;

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (PendingRelease is null || IsInstallingUpdate)
        {
            return;
        }

        IsInstallingUpdate = true;
        try
        {
            var progress = new Progress<double>(p =>
            {
                UpdateProgress = p;
                UpdateProgressText = Loc.F("Update_Downloading", p);
            });
            var staging = await _updates.DownloadAndStageAsync(PendingRelease, progress, CancellationToken.None).ConfigureAwait(true);
            await Servers.StopAllAsync().ConfigureAwait(true);
            UpdateService.LaunchInstaller(staging);
            App.Quit();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Serilog.Log.Error(ex, "Update failed");
            IsInstallingUpdate = false;
            UpdateProgressText = null;
            Toast(Loc.F("Update_Failed", ex.Message), isError: true);
        }
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        if (PendingRelease is not null)
        {
            Shell32.Open(PendingRelease.PageUrl.ToString());
        }
    }
}

public sealed record ConfirmRequest(string Title, string Body, string ConfirmText, bool Danger)
{
    public TaskCompletionSource<bool> Result { get; } = new();
}

public static class Shell32
{
    public static void Open(string target) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
}
