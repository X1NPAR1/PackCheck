using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PackCheck.App.Infrastructure;
using PackCheck.Providers;
using PackCheck.Servers;

namespace PackCheck.App.ViewModels;

/// <summary>What the wizard needs to create a server from a modpack.</summary>
public sealed record ModpackServices(ICatalog Modrinth, ModrinthModpacks Versions, CurseForgeMods CurseForge, ModpackInstaller Installer, HttpClient Http);

/// <summary>The "Modpack" source of the create wizard: Modrinth search or a local .mrpack / CurseForge zip.</summary>
public sealed partial class CreateServerViewModel
{
    private ModpackServices? _modpacks;
    private CancellationTokenSource? _searchCts;

    public bool ModpacksAvailable => _modpacks is not null;

    public ObservableCollection<CatalogItem> PackResults { get; } = [];

    public ObservableCollection<ModpackVersion> PackVersions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlatformMode), nameof(SummaryText), nameof(IsProxy), nameof(Capacity), nameof(RecommendedGb), nameof(MemoryWarning))]
    private bool _isModpackMode;

    public bool IsPlatformMode
    {
        get => !IsModpackMode;
        set => IsModpackMode = !value;
    }

    [ObservableProperty]
    private string _packQuery = string.Empty;

    [ObservableProperty]
    private CatalogItem? _selectedPack;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PackSummary), nameof(SummaryText))]
    private ModpackVersion? _selectedPackVersion;

    /// <summary>A local .mrpack or CurseForge zip chosen with "From file…"; takes priority over the Modrinth pick.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PackSummary), nameof(SummaryText))]
    private string? _packFile;

    [ObservableProperty]
    private string? _packStatus;

    public string? PackSummary => PackFile is not null ? Path.GetFileName(PackFile)
        : SelectedPackVersion is not null && SelectedPack is not null ? $"{SelectedPack.Title} {SelectedPackVersion.VersionNumber}" : null;

    public void UseModpacks(ModpackServices services) => _modpacks = services;

    partial void OnIsModpackModeChanged(bool value)
    {
        if (value && PackResults.Count == 0)
        {
            _ = SearchPacksAsync();
        }
    }

    [RelayCommand]
    private async Task SearchPacksAsync()
    {
        if (_modpacks is null)
        {
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        PackStatus = Loc.T("Modpack_Searching");
        try
        {
            var page = await _modpacks.Modrinth.BrowseAsync(CatalogKind.Modpack, PackQuery.Trim(), PackQuery.Trim().Length > 0 ? CatalogSort.Relevance : CatalogSort.Downloads, 0, 20, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            PackResults.Clear();
            foreach (var item in page.Items)
            {
                PackResults.Add(item);
            }

            PackStatus = page.Error is not null ? Loc.F("Modpack_SearchFailed", page.Error) : PackResults.Count == 0 ? Loc.T("Modpack_NoResults") : null;
        }
        catch (OperationCanceledException)
        {
            // A newer search replaced this one; its results win.
            return;
        }
    }

    partial void OnSelectedPackChanged(CatalogItem? value)
    {
        PackVersions.Clear();
        SelectedPackVersion = null;
        if (value is not null)
        {
            PackFile = null;
            _ = LoadPackVersionsAsync(value);
        }

        OnPropertyChanged(nameof(PackSummary));
        OnPropertyChanged(nameof(SummaryText));
    }

    private async Task LoadPackVersionsAsync(CatalogItem pack)
    {
        if (_modpacks is null)
        {
            return;
        }

        PackStatus = Loc.T("Modpack_LoadingVersions");
        var r = await _modpacks.Versions.GetVersionsAsync(pack.Id, CancellationToken.None).ConfigureAwait(true);
        if (!ReferenceEquals(pack, SelectedPack))
        {
            return;
        }

        PackVersions.Clear();
        foreach (var v in r.Value ?? [])
        {
            PackVersions.Add(v);
        }

        SelectedPackVersion = PackVersions.FirstOrDefault();
        PackStatus = !r.Ok ? Loc.F("Modpack_SearchFailed", r.Error!) : PackVersions.Count == 0 ? Loc.T("Modpack_NoVersions") : null;
    }

    [RelayCommand]
    private void PickPackFile()
    {
        var dlg = new OpenFileDialog { Filter = Loc.T("Modpack_FileFilter") + "|*.mrpack;*.zip" };
        if (dlg.ShowDialog() == true)
        {
            PackFile = dlg.FileName;
            SelectedPack = null;
        }
    }

    /// <summary>Downloads the chosen Modrinth version (or uses the local file), installs the loader server and the pack.</summary>
    private async Task CreateFromModpackAsync()
    {
        if (_modpacks is null || (PackFile is null && SelectedPackVersion is null))
        {
            Status = Loc.T("Modpack_PickFirst");
            return;
        }

        IsWorking = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        string? temp = null;
        try
        {
            var archive = PackFile;
            if (archive is null)
            {
                var v = SelectedPackVersion!;
                Status = Loc.T("Modpack_DownloadingPack");
                temp = Path.Combine(Path.GetTempPath(), "packcheck-" + Guid.NewGuid().ToString("N") + ".mrpack");
                await VerifiedDownloader.DownloadAsync(_modpacks.Http, v.Url, temp, v.Sha512, HashAlgorithmName.SHA512, Modpacks.MaxFileBytes,
                    new Progress<double>(p => Progress = p * 0.1), ct).ConfigureAwait(true);
                archive = temp;
            }

            var manifest = Modpacks.Read(archive);
            IReadOnlyList<ModpackFile>? resolved = null;
            if (manifest.Format == ModpackFormat.CurseForge)
            {
                if (!_modpacks.CurseForge.IsAvailable)
                {
                    Status = Loc.T("Modpack_CurseForgeKeyNeeded");
                    return;
                }

                resolved = await ResolveCurseForgeAsync(manifest, ct).ConfigureAwait(true);
                if (resolved is null)
                {
                    return;
                }
            }

            var required = PlatformInfo.RequiredJava(manifest.Platform, manifest.MinecraftVersion);
            var runtime = Runtimes.FirstOrDefault(r => r.FeatureVersion >= required);
            if (runtime is null)
            {
                Status = $"Java {required} …";
                runtime = await _java.EnsureAsync(required, _state.JavaRuntimes, new Progress<double>(p => Progress = p), ct).ConfigureAwait(true);
                _state.JavaRuntimes = [runtime, .. _state.JavaRuntimes];
                Runtimes.Insert(0, runtime);
            }

            var req = new ServerCreator.Request(Name, Folder, manifest.Platform, manifest.MinecraftVersion, runtime.JavaExe, (int)(MemoryGb * 1024), Port, Seed, AcceptEula)
            {
                CpuCores = (int)CpuCores == LogicalCores ? 0 : (int)CpuCores,
                Properties = WizardProperties(),
            };
            var def = await _modpacks.Installer.InstallAsync(req, archive, manifest, resolved,
                new Progress<(double Progress, string Step)>(p =>
                {
                    Progress = p.Progress;
                    if (p.Step != "modpack_files")
                    {
                        Status = Loc.T("Wizard_Step_" + p.Step);
                    }
                }),
                new Progress<(int Done, int Total)>(f => Status = Loc.F("Modpack_Files", f.Done, f.Total)), ct).ConfigureAwait(true);
            _done(def);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or InvalidDataException or ArgumentException
            or OperationCanceledException or UnauthorizedAccessException or CryptographicException or System.Text.Json.JsonException)
        {
            Status = ex.Message;
        }
        finally
        {
            IsWorking = false;
            if (temp is not null && File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private async Task<IReadOnlyList<ModpackFile>?> ResolveCurseForgeAsync(ModpackManifest manifest, CancellationToken ct)
    {
        var list = new List<ModpackFile>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var done = 0;
        foreach (var f in manifest.CurseForgeFiles.Where(f => f.Required))
        {
            ct.ThrowIfCancellationRequested();
            Status = Loc.F("Modpack_Resolving", ++done, manifest.CurseForgeFiles.Count);
            var r = await _modpacks!.CurseForge.GetFileByIdAsync(f.ProjectId, f.FileId, ct).ConfigureAwait(true);
            if (!r.Ok)
            {
                Status = Loc.F("Modpack_ResolveFailed", f.ProjectId, r.Error!);
                return null;
            }

            var m = r.Value!;
            if (string.IsNullOrWhiteSpace(m.Hash))
            {
                // Without a hash the download could not be verified.
                Status = Loc.F("Modpack_ResolveFailed", f.ProjectId, "no file hash");
                return null;
            }

            var target = "mods/" + Path.GetFileName(m.FileName);
            if (!paths.Add(target))
            {
                // Two files with the same name would overwrite each other.
                Status = Loc.F("Modpack_ResolveFailed", f.ProjectId, "duplicate file name " + Path.GetFileName(m.FileName));
                return null;
            }

            list.Add(new ModpackFile(target, [m.Url], m.Hash, m.Algorithm, m.Size));
        }

        return list;
    }
}
