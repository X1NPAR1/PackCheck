using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackCheck.App.Infrastructure;
using PackCheck.Providers;
using PackCheck.Servers;
using PackCheck.Validation;

namespace PackCheck.App.ViewModels;

/// <summary>Search and install mods / modpacks that match the selected server's Minecraft version and loader.</summary>
public sealed partial class ModsViewModel(IEnumerable<IModProvider> providers, ServersViewModel servers, AppSettings settings, HttpClient http) : ObservableObject
{
    public ObservableCollection<ProjectHit> Results { get; } = [];

    public ObservableCollection<string> Errors { get; } = [];

    public ServersViewModel Servers { get; } = servers;

    public IEnumerable<ServerItemViewModel> ModdedServers => Servers.Items.Where(s => PlatformInfo.UsesMods(s.Definition.Platform));

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private ServerItemViewModel? _targetServer;

    [ObservableProperty]
    private int _kindIndex;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string? _status;

    public bool HasModdedServer => ModdedServers.Any();

    private ModKind Kind => KindIndex == 1 ? ModKind.Modpack : ModKind.Mod;

    public void Refresh()
    {
        OnPropertyChanged(nameof(ModdedServers));
        OnPropertyChanged(nameof(HasModdedServer));
        TargetServer ??= ModdedServers.FirstOrDefault();
    }

    partial void OnKindIndexChanged(int value) => Results.Clear();

    [RelayCommand]
    private async Task SearchAsync()
    {
        Refresh();
        if (TargetServer is not { } server || !settings.AllowNetworkProviders)
        {
            Status = settings.AllowNetworkProviders ? Loc.T("Mods_NeedServer") : Loc.T("Plugins_NetworkOff");
            return;
        }

        IsBusy = true;
        Status = null;
        Results.Clear();
        Errors.Clear();
        var mc = server.Definition.MinecraftVersion;
        var loader = PlatformInfo.Loader(server.Definition.Platform);
        foreach (var p in providers)
        {
            if (!p.IsAvailable)
            {
                Errors.Add(Loc.F("Plugins_ProviderFailed", p.Name, Loc.T("Mods_CurseForgeKey")));
                continue;
            }

            var r = await p.SearchAsync(Query.Trim(), Kind, mc, loader, CancellationToken.None).ConfigureAwait(true);
            if (r.Ok)
            {
                foreach (var hit in r.Value!)
                {
                    Results.Add(hit);
                }
            }
            else
            {
                Errors.Add(Loc.F("Plugins_ProviderFailed", p.Name, r.Error!));
            }
        }

        IsBusy = false;
    }

    [RelayCommand]
    private async Task InstallAsync(ProjectHit? hit)
    {
        if (hit is null || TargetServer is not { } server)
        {
            return;
        }

        if (!server.CanEdit)
        {
            MainViewModel.Shell?.Toast(Loc.T("Servers_SaveWhileRunning"), isError: true);
            return;
        }

        var provider = providers.First(p => p.Name == hit.Provider);
        var def = server.Definition;
        IsBusy = true;
        Progress = 0;
        try
        {
            var file = await provider.GetFileAsync(hit.Id, Kind, def.MinecraftVersion, PlatformInfo.Loader(def.Platform), CancellationToken.None).ConfigureAwait(true);
            if (!file.Ok)
            {
                MainViewModel.Shell?.Toast(Loc.F("Plugins_ProviderFailed", provider.Name, file.Error!), isError: true);
                return;
            }

            var f = file.Value!;
            var temp = Path.Combine(Path.GetTempPath(), "packcheck-mod-" + Guid.NewGuid().ToString("N") + Path.GetExtension(f.FileName));
            Status = f.FileName;
            await VerifiedDownloader.DownloadAsync(http, f.Url, temp, f.Hash, f.Algorithm, 2L * 1024 * 1024 * 1024, new Progress<double>(p => Progress = p * 0.5), CancellationToken.None).ConfigureAwait(true);
            try
            {
                if (Kind == ModKind.Mod)
                {
                    var mods = Path.Combine(def.Directory, "mods");
                    Directory.CreateDirectory(mods);
                    File.Copy(temp, Path.Combine(mods, Path.GetFileName(f.FileName)), overwrite: true);
                }
                else if (f.FileName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
                {
                    await MrpackInstaller.InstallAsync(http, temp, def.Directory,
                        (h, url, target, hash, alg) => VerifiedDownloader.DownloadAsync(h, url, target, hash, alg, 512L * 1024 * 1024, null, CancellationToken.None),
                        new Progress<double>(p => Progress = 0.5 + (p * 0.5)), CancellationToken.None).ConfigureAwait(true);
                }
                else
                {
                    // CurseForge server pack: a zip of the server folder.
                    var staging = Path.Combine(Path.GetTempPath(), "packcheck-pack-" + Guid.NewGuid().ToString("N"));
                    SafeZip.Extract(temp, staging);
                    var root = Directory.GetDirectories(staging).Length == 1 && Directory.GetFiles(staging).Length == 0 ? Directory.GetDirectories(staging)[0] : staging;
                    foreach (var src in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        var dest = Path.Combine(def.Directory, Path.GetRelativePath(root, src));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.Copy(src, dest, overwrite: true);
                    }

                    Directory.Delete(staging, true);
                }
            }
            finally
            {
                File.Delete(temp);
            }

            Progress = 1;
            Status = null;
            server.RefreshPlugins();
            MainViewModel.Shell?.Toast(Loc.F("Plugins_Installed", hit.Title));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or InvalidDataException or UnsafeArchiveException or CryptographicException)
        {
            MainViewModel.Shell?.Toast(ex.Message, isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
