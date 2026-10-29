using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackCheck.App.Infrastructure;
using PackCheck.Servers;

namespace PackCheck.App.ViewModels;

/// <summary>One plugin/mod row in the upgrade report; updates are applied when <see cref="ApplyUpdate"/> is on.</summary>
public sealed partial class UpgradeAddonRow(AddonCheck check) : ObservableObject
{
    public AddonCheck Check { get; } = check;
    public string Name => Check.Name;
    public string FileName => Check.FileName;
    public string StatusText => Loc.T("UpgradeStatus_" + Check.Status);
    public bool IsBlocking => Check.Status == AddonStatus.Incompatible;
    public bool IsWarning => Check.Status is AddonStatus.Unknown;
    public bool IsGood => Check.Status is AddonStatus.Compatible or AddonStatus.Likely;
    public bool HasUpdate => Check.Status == AddonStatus.UpdateAvailable;
    public string Detail => HasUpdate ? Loc.F("Upgrade_UpdateTo", Check.UpdateVersion ?? "?")
        : Check.Declared is { } d ? Loc.F("Upgrade_Declared", d) : string.Empty;

    [ObservableProperty]
    private bool _applyUpdate = check.Status == AddonStatus.UpdateAvailable;

    /// <summary>Incompatible jars without an update can be disabled during the upgrade.</summary>
    [ObservableProperty]
    private bool _disable = check.Status == AddonStatus.Incompatible;
}

/// <summary>
/// In-place Minecraft version upgrade: compatibility report → pinned full backup → Java → new server jar →
/// plugin/mod updates → test start → done, with one-click rollback to the backup when the test start fails.
/// </summary>
public sealed partial class ServerItemViewModel
{
    public const int UpgradeTab = 10;

    public ObservableCollection<string> UpgradeVersions { get; } = [];

    public ObservableCollection<UpgradeAddonRow> UpgradeAddons { get; } = [];

    public ObservableCollection<string> UpgradeLog { get; } = [];

    [ObservableProperty]
    private string? _upgradeTarget;

    /// <summary>0 = choose version, 1 = report ready, 2 = running, 3 = done, 4 = failed (rollback possible).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpgradeIsChoosing), nameof(UpgradeHasReport), nameof(UpgradeIsRunning), nameof(UpgradeSucceeded), nameof(UpgradeFailed))]
    private int _upgradeStep;

    [ObservableProperty]
    private string? _upgradeMessage;

    [ObservableProperty]
    private UpgradeReport? _upgradeReport;

    private BackupInfo? _upgradeBackup;
    private ServerDefinition? _beforeUpgrade;

    public bool UpgradeIsChoosing => UpgradeStep <= 1;
    public bool UpgradeHasReport => UpgradeStep == 1;
    public bool UpgradeIsRunning => UpgradeStep == 2;
    public bool UpgradeSucceeded => UpgradeStep == 3;
    public bool UpgradeFailed => UpgradeStep == 4;

    public string UpgradeJavaText => UpgradeReport is { } r ? Loc.F("Upgrade_Java", r.RequiredJava) : string.Empty;

    public string UpgradeSummary => UpgradeReport is { } r
        ? Loc.F("Upgrade_Summary", r.Addons.Count, r.Addons.Count(a => a.Status == AddonStatus.UpdateAvailable), r.Blocking, r.Addons.Count(a => a.Status == AddonStatus.Unknown))
        : string.Empty;

    private static HttpClient AppHttp => (HttpClient)App.Services.GetService(typeof(HttpClient))!;

    [RelayCommand]
    private async Task OpenUpgradeAsync()
    {
        Tab = UpgradeTab;
        if (UpgradeStep is 2)
        {
            return;
        }

        UpgradeStep = 0;
        UpgradeReport = null;
        UpgradeAddons.Clear();
        UpgradeLog.Clear();
        UpgradeMessage = Loc.T("Upgrade_Loading");
        try
        {
            var sources = (ServerSources)App.Services.GetService(typeof(ServerSources))!;
            var all = await sources.GetVersionsAsync(Definition.Platform, CancellationToken.None).ConfigureAwait(true);
            UpgradeVersions.Clear();
            foreach (var v in UpgradeAdvisor.Targets(Definition.MinecraftVersion, all))
            {
                UpgradeVersions.Add(v);
            }

            UpgradeTarget = UpgradeVersions.FirstOrDefault();
            UpgradeMessage = UpgradeVersions.Count == 0 ? Loc.F("Upgrade_Latest", Definition.MinecraftVersion) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            UpgradeMessage = Loc.F("Upgrade_VersionsFailed", ex.Message);
        }
    }

    partial void OnUpgradeTargetChanged(string? value)
    {
        if (UpgradeStep == 1)
        {
            UpgradeStep = 0;
            UpgradeAddons.Clear();
            UpgradeReport = null;
        }
    }

    [RelayCommand]
    private async Task CheckUpgradeAsync()
    {
        if (UpgradeTarget is not { } target)
        {
            return;
        }

        UpgradeMessage = Loc.T("Upgrade_Checking");
        try
        {
            UpgradeReport = await UpgradeAdvisor.CheckAsync(Definition, target, _settings.AllowNetworkProviders ? AppHttp : null, CancellationToken.None).ConfigureAwait(true);
            UpgradeAddons.Clear();
            foreach (var a in UpgradeReport.Addons)
            {
                UpgradeAddons.Add(new UpgradeAddonRow(a));
            }

            OnPropertyChanged(nameof(UpgradeJavaText));
            OnPropertyChanged(nameof(UpgradeSummary));
            UpgradeMessage = null;
            UpgradeStep = 1;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            UpgradeMessage = ex.Message;
        }
    }

    private void Log(string line) => UpgradeLog.Add($"{DateTime.Now:HH:mm:ss}  {line}");

    [RelayCommand]
    private async Task RunUpgradeAsync()
    {
        if (UpgradeReport is not { } report || UpgradeTarget is not { } target || MainViewModel.Shell is not { } shell)
        {
            return;
        }

        if (IsBusy)
        {
            if (!await shell.ConfirmAsync(Loc.T("Upgrade_Title"), Loc.T("Upgrade_StopFirst"), Loc.T("Servers_Stop")).ConfigureAwait(true))
            {
                return;
            }

            await _process.StopAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(true);
        }

        if (!await shell.ConfirmAsync(Loc.T("Upgrade_Title"), Loc.F("Upgrade_Confirm", Definition.MinecraftVersion, target), Loc.T("Upgrade_Start"), danger: true).ConfigureAwait(true))
        {
            return;
        }

        UpgradeStep = 2;
        UpgradeLog.Clear();
        _beforeUpgrade = Definition;
        try
        {
            Log(Loc.T("Upgrade_StepBackup"));
            _upgradeBackup = await BackupNowAsync(BackupKind.Full, Loc.F("Upgrade_BackupNote", Definition.MinecraftVersion, target)).ConfigureAwait(true)
                ?? throw new InvalidOperationException(Loc.T("Upgrade_BackupBusy"));
            _upgradeBackup = Backups.SetPinned(_upgradeBackup, true);
            RefreshBackups();

            Log(Loc.F("Upgrade_StepJava", report.RequiredJava));
            await EnsureJavaAsync(report.RequiredJava).ConfigureAwait(true);

            Log(Loc.F("Upgrade_StepInstall", Definition.Platform, target));
            var creator = (ServerCreator)App.Services.GetService(typeof(ServerCreator))!;
            var request = new ServerCreator.Request(Name, Path.GetDirectoryName(Definition.Directory)!, Definition.Platform, target, Definition.JavaPath, Definition.MaxMemoryMb, Definition.Port, null, AcceptEula: true);
            var (jar, argsFile) = await creator.InstallAsync(request, Definition.Directory, null, CancellationToken.None).ConfigureAwait(true);
            if (!string.Equals(jar, Definition.JarFile, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(Definition.Directory, Definition.JarFile)) && Definition.ArgsFile is null)
            {
                File.Delete(Path.Combine(Definition.Directory, Definition.JarFile));
            }

            foreach (var row in UpgradeAddons.Where(r => r.ApplyUpdate && r.HasUpdate))
            {
                Log(Loc.F("Upgrade_StepUpdate", row.Name, row.Check.UpdateVersion ?? "?"));
                await UpgradeAdvisor.ApplyUpdateAsync(AppHttp, row.Check, CancellationToken.None).ConfigureAwait(true);
            }

            foreach (var row in UpgradeAddons.Where(r => r.Disable && !(r.ApplyUpdate && r.HasUpdate) && File.Exists(r.Check.FilePath)))
            {
                Log(Loc.F("Upgrade_StepDisable", row.Name));
                File.Move(row.Check.FilePath, row.Check.FilePath + ".disabled", overwrite: true);
            }

            Definition = Definition with { MinecraftVersion = target, JarFile = jar, ArgsFile = argsFile };
            _registry.Upsert(Definition);
            _process.UpdateDefinition(Definition);
            await File.WriteAllTextAsync(Path.Combine(Definition.Directory, "packcheck.json"), JsonSerializer.Serialize(new { minecraftVersion = target })).ConfigureAwait(true);
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(PlatformText));
            RefreshPlugins();

            Log(Loc.T("Upgrade_StepTest"));
            if (await TestStartAsync().ConfigureAwait(true))
            {
                Log(Loc.T("Upgrade_Done"));
                UpgradeMessage = Loc.F("Upgrade_Success", target);
                UpgradeStep = 3;
                PostDiscord(DiscordEvent.Automation, Loc.F("Upgrade_Success", target));
            }
            else
            {
                Log(Loc.T("Upgrade_TestFailed"));
                UpgradeMessage = Loc.T("Upgrade_TestFailed");
                UpgradeStep = 4;
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or InvalidDataException or TaskCanceledException or UnauthorizedAccessException)
        {
            Log("✖ " + ex.Message);
            UpgradeMessage = ex.Message;
            UpgradeStep = _upgradeBackup is null ? 0 : 4;
        }
    }

    /// <summary>Starts the server and waits until it is ready (then stops it) or fails. Up to 6 minutes for big modpacks.</summary>
    private async Task<bool> TestStartAsync()
    {
        await StartAsync().ConfigureAwait(true);
        var until = DateTime.Now.AddMinutes(6);
        while (DateTime.Now < until && State == ServerState.Starting)
        {
            await Task.Delay(1000).ConfigureAwait(true);
        }

        if (State != ServerState.Running)
        {
            if (IsBusy)
            {
                _process.Kill();
            }

            return false;
        }

        Log(Loc.T("Upgrade_TestOk"));
        await _process.StopAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(true);
        return true;
    }

    /// <summary>Puts everything back as it was before the upgrade: the whole backup is restored and the old settings come back.</summary>
    [RelayCommand]
    private async Task RollbackUpgradeAsync()
    {
        if (_upgradeBackup is null || _beforeUpgrade is null || MainViewModel.Shell is not { } shell)
        {
            return;
        }

        if (IsBusy)
        {
            _process.Kill();
            await Task.Delay(1500).ConfigureAwait(true);
        }

        try
        {
            Log(Loc.T("Upgrade_RollingBack"));
            await Backups.RestoreAsync(_beforeUpgrade, _upgradeBackup, null, CancellationToken.None).ConfigureAwait(true);
            Definition = _beforeUpgrade;
            _registry.Upsert(Definition);
            _process.UpdateDefinition(Definition);
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(PlatformText));
            LoadProperties();
            RefreshPlugins();
            Log(Loc.F("Upgrade_RolledBack", Definition.MinecraftVersion));
            UpgradeMessage = Loc.F("Upgrade_RolledBack", Definition.MinecraftVersion);
            UpgradeStep = 0;
            shell.Toast(UpgradeMessage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log("✖ " + ex.Message);
            shell.Toast(ex.Message, isError: true);
        }
    }
}
