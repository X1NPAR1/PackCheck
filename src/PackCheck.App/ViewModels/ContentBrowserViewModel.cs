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

/// <summary>One row of the browse list with its install state for the target server.</summary>
public sealed partial class CatalogRow(CatalogItem item, bool compatible, string? reason, string? installedFile) : ObservableObject
{
    /// <summary>The jar in plugins/ or mods/ that belongs to this project, when it is installed.</summary>
    public string? InstalledFile { get; set; } = installedFile;

    public string Loaders => string.Join(", ", Item.Loaders);

    public bool CanUpdate => HasUpdate && !IsInstalling;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpdate))]
    private bool _hasUpdate;

    [ObservableProperty]
    private string? _latestFile;

    public CatalogItem Item { get; } = item;

    public bool Compatible { get; } = compatible;

    public string? IncompatibleReason { get; } = reason;

    public bool CanInstall => Compatible && !Installed && !IsInstalling;

    public string Downloads => Loc.F("Plugins_Downloads", Item.Downloads);

    public string VersionRange => Compatibility.Range(Item.GameVersions);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _installed = installedFile is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall), nameof(CanUpdate))]
    private bool _isInstalling;
}

public sealed record PageButton(int Index, string Label, bool IsCurrent, bool IsGap);

/// <summary>Shared browser for plugins, mods and modpacks: paged list, sources, sorting, compatibility.</summary>
public abstract partial class ContentBrowserViewModel : ObservableObject
{
    private const int PageSize = 20;
    private readonly IReadOnlyList<ICatalog> _catalogs;
    private readonly IReadOnlyList<IPluginProvider> _pluginProviders;
    private readonly IReadOnlyList<IModProvider> _modProviders;
    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private CancellationTokenSource? _loadCts;
    private bool _loadedOnce;

    protected ContentBrowserViewModel(IEnumerable<ICatalog> catalogs, IEnumerable<IPluginProvider> pluginProviders, IEnumerable<IModProvider> modProviders, ServersViewModel servers, AppSettings settings, HttpClient http)
    {
        _catalogs = catalogs.ToList();
        _pluginProviders = pluginProviders.ToList();
        _modProviders = modProviders.ToList();
        Servers = servers;
        _settings = settings;
        _http = http;
    }

    public ServersViewModel Servers { get; }

    public ObservableCollection<CatalogRow> Rows { get; } = [];

    public ObservableCollection<PageButton> Pages { get; } = [];

    public abstract bool IsModPage { get; }

    public IEnumerable<ServerItemViewModel> Targets => Servers.Items.Where(s => IsModPage ? s.UsesMods : s.UsesPlugins);

    public IReadOnlyList<string> Sources => _catalogs.Where(c => c.Supports(Kind)).Select(c => c.Name).ToList();

    public IReadOnlyList<string> SortKeys { get; } = ["Browse_Sort_Downloads", "Browse_Sort_Updated", "Browse_Sort_Newest", "Browse_Sort_Relevance"];

    public CatalogKind Kind => !IsModPage ? CatalogKind.Plugin : ShowModpacks ? CatalogKind.Modpack : CatalogKind.Mod;

    public bool HasTarget => TargetServer is not null;

    public bool HasRows => Rows.Count > 0;

    public string ResultsText => Loc.F("Browse_Results", Total);

    public string TargetInfo => TargetServer is null ? string.Empty : $"{TargetServer.Definition.Platform} {TargetServer.Definition.MinecraftVersion}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTarget), nameof(TargetInfo))]
    private ServerItemViewModel? _targetServer;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private string _source = "Modrinth";

    [ObservableProperty]
    private int _sortIndex;

    [ObservableProperty]
    private bool _showModpacks;

    [ObservableProperty]
    private bool _compatibleOnly;

    [ObservableProperty]
    private int _page;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultsText))]
    private int _total;

    [ObservableProperty]
    private int _pageCount;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private double _installProgress;

    /// <summary>Category filter of the current source; "" means all.</summary>
    [ObservableProperty]
    private string _category = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private CatalogRow? _selectedRow;

    [ObservableProperty]
    private string? _description;

    [ObservableProperty]
    private bool _isDescribing;

    private CancellationTokenSource? _describeCts;

    public bool HasSelection => SelectedRow is not null;

    public IReadOnlyList<CategoryOption> Categories =>
        [new(string.Empty, Loc.T("Browse_AllCategories")), .. CurrentCatalog.Categories(Kind).Select(c => new CategoryOption(c, CategoryOption.Label(c)))];

    private ICatalog CurrentCatalog => _catalogs.FirstOrDefault(c => c.Name == Source && c.Supports(Kind)) ?? _catalogs.First(c => c.Supports(Kind));

    partial void OnCategoryChanged(string value) => Reload();

    partial void OnSelectedRowChanged(CatalogRow? value) => _ = DescribeAsync(value);

    private async Task DescribeAsync(CatalogRow? row)
    {
        _describeCts?.Cancel();
        Description = null;
        if (row is null)
        {
            return;
        }

        _describeCts = new CancellationTokenSource();
        var ct = _describeCts.Token;
        IsDescribing = true;
        try
        {
            var catalog = _catalogs.FirstOrDefault(c => c.Name == row.Item.Provider);
            var text = catalog is null ? null : await catalog.DescribeAsync(row.Item, ct).ConfigureAwait(true);
            if (!ct.IsCancellationRequested)
            {
                Description = string.IsNullOrWhiteSpace(text) ? row.Item.Description : text.Length > 6000 ? text[..6000] + " …" : text;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsDescribing = false;
            }
        }
    }

    [RelayCommand]
    private void CloseDetail() => SelectedRow = null;

    [RelayCommand]
    private void Select(CatalogRow? row) => SelectedRow = row;

    /// <summary>Called when the page is shown: preselect a target and load page 1 without a search.</summary>
    public void Activate()
    {
        OnPropertyChanged(nameof(Targets));
        var preferred = Servers.Selected is { } sel && Targets.Contains(sel) ? sel : null;
        if (TargetServer is null || !Targets.Contains(TargetServer))
        {
            TargetServer = preferred ?? Targets.FirstOrDefault();
        }
        else if (preferred is not null && preferred != TargetServer)
        {
            TargetServer = preferred;
        }

        if (!_loadedOnce && TargetServer is not null)
        {
            _loadedOnce = true;
            _ = LoadAsync();
        }
    }

    partial void OnTargetServerChanged(ServerItemViewModel? value)
    {
        if (_loadedOnce)
        {
            Page = 0;
            _ = LoadAsync();
        }
    }

    partial void OnSourceChanged(string value)
    {
        OnPropertyChanged(nameof(Categories));
        if (Category.Length > 0)
        {
            Category = string.Empty; // reloads
            return;
        }

        Reload();
    }

    partial void OnSortIndexChanged(int value) => Reload();

    partial void OnShowModpacksChanged(bool value)
    {
        OnPropertyChanged(nameof(Sources));
        OnPropertyChanged(nameof(Categories));
        Reload();
    }

    partial void OnCompatibleOnlyChanged(bool value) => Reload();

    private void Reload()
    {
        Page = 0;
        if (_loadedOnce)
        {
            _ = LoadAsync();
        }
    }

    [RelayCommand]
    private Task SearchAsync()
    {
        _loadedOnce = true;
        Page = 0;
        return LoadAsync();
    }

    [RelayCommand]
    private Task GoToPageAsync(int index)
    {
        if (index < 0 || index >= PageCount || index == Page)
        {
            return Task.CompletedTask;
        }

        Page = index;
        return LoadAsync();
    }

    [RelayCommand]
    private Task PrevAsync() => GoToPageAsync(Page - 1);

    [RelayCommand]
    private Task NextAsync() => GoToPageAsync(Page + 1);

    private async Task LoadAsync()
    {
        if (TargetServer is not { } server)
        {
            Rows.Clear();
            OnPropertyChanged(nameof(HasRows));
            return;
        }

        if (!_settings.AllowNetworkProviders)
        {
            Error = Loc.T("Plugins_NetworkOff");
            return;
        }

        var catalog = _catalogs.FirstOrDefault(c => c.Name == Source && c.Supports(Kind)) ?? _catalogs.First(c => c.Supports(Kind));
        if (catalog.Name != Source)
        {
            Source = catalog.Name;
            return;
        }

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        IsLoading = true;
        Error = null;
        try
        {
            var sort = (CatalogSort)SortIndex;
            if (sort == CatalogSort.Relevance && Query.Trim().Length == 0)
            {
                sort = CatalogSort.Downloads;
            }

            var category = catalog.Categories(Kind).Contains(Category) ? Category : null;
            var result = await catalog.BrowseAsync(Kind, Query.Trim(), sort, Page, PageSize, ct, category).ConfigureAwait(true);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (result.Error is not null)
            {
                Error = Loc.F("Plugins_ProviderFailed", catalog.Name, result.Error == "API key missing (Settings)" ? Loc.T("Mods_CurseForgeKey") : result.Error);
            }

            var mc = server.Definition.MinecraftVersion;
            var loader = PlatformInfo.Loader(server.Definition.Platform);

            Rows.Clear();
            foreach (var item in result.Items)
            {
                var (ok, reason) = Compatibility.Check(item, Kind, mc, loader);
                if (CompatibleOnly && !ok)
                {
                    continue;
                }

                var file = Kind == CatalogKind.Modpack ? null : server.PluginList.FirstOrDefault(p =>
                    (p.Name ?? string.Empty).Equals(item.Title, StringComparison.OrdinalIgnoreCase) || p.File.Contains(item.Slug, StringComparison.OrdinalIgnoreCase))?.File;
                Rows.Add(new CatalogRow(item, ok, Explain(reason, mc, loader), file));
            }

            Total = result.Total;
            PageCount = Math.Max(1, (int)Math.Ceiling(result.Total / (double)PageSize));
            BuildPages();
            OnPropertyChanged(nameof(HasRows));
            _ = CheckUpdatesAsync(Rows.Where(r => r.InstalledFile is not null).ToList(), server.Definition, ct);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    private static string? Explain(string? reason, string mc, string loader)
    {
        if (reason is null)
        {
            return null;
        }

        var (kind, detail) = (reason.Split(':', 2)[0], reason.Split(':', 2)[1]);
        return kind == "loader" ? Loc.F("Browse_IncompatibleLoader", loader, detail) : Loc.F("Browse_IncompatibleVersion", mc, detail);
    }

    private void BuildPages()
    {
        Pages.Clear();
        var shown = new SortedSet<int> { 0, PageCount - 1 };
        for (var i = Page - 2; i <= Page + 2; i++)
        {
            if (i >= 0 && i < PageCount)
            {
                shown.Add(i);
            }
        }

        var last = -1;
        foreach (var i in shown)
        {
            if (last >= 0 && i - last > 1)
            {
                Pages.Add(new PageButton(-1, "…", false, true));
            }

            Pages.Add(new PageButton(i, (i + 1).ToString(Loc.Instance.Culture), i == Page, false));
            last = i;
        }
    }

    [RelayCommand]
    private void OpenPage(CatalogRow? row)
    {
        if (row?.Item.Page is { } page)
        {
            Shell32.Open(page.ToString());
        }
    }

    private async Task<((string FileName, Uri Url, string? Hash, HashAlgorithmName Alg)? File, string? Error)> ResolveAsync(CatalogItem item, ServerDefinition def, CancellationToken ct)
    {
        var loader = PlatformInfo.Loader(def.Platform);
        if (Kind == CatalogKind.Plugin && _pluginProviders.FirstOrDefault(p => p.Name == item.Provider) is { } pp)
        {
            var r = await pp.GetLatestFileAsync(item.Id, def.MinecraftVersion, ct).ConfigureAwait(true);
            return (r.Ok ? (r.Value!.FileName, r.Value.Url, r.Value.Hash, r.Value.Algorithm) : null, r.Error);
        }

        if (_modProviders.FirstOrDefault(p => p.Name == item.Provider) is { } mp)
        {
            var r = await mp.GetFileAsync(item.Id, Kind == CatalogKind.Modpack ? ModKind.Modpack : ModKind.Mod, def.MinecraftVersion, Kind == CatalogKind.Plugin ? "paper" : loader, ct).ConfigureAwait(true);
            return (r.Ok ? (r.Value!.FileName, r.Value.Url, r.Value.Hash, r.Value.Algorithm) : null, r.Error);
        }

        return (null, "no provider");
    }

    /// <summary>An installed project has an update when the newest compatible file has a different name than the jar on disk.</summary>
    private async Task CheckUpdatesAsync(IReadOnlyList<CatalogRow> rows, ServerDefinition def, CancellationToken ct)
    {
        foreach (var row in rows)
        {
            try
            {
                var (file, _) = await ResolveAsync(row.Item, def, ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (file is { } f && !string.Equals(Path.GetFileName(f.FileName), row.InstalledFile, StringComparison.OrdinalIgnoreCase))
                {
                    row.LatestFile = Path.GetFileName(f.FileName);
                    row.HasUpdate = true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
            }
        }
    }

    [RelayCommand]
    private Task UpdateAsync(CatalogRow? row) => row is { CanUpdate: true } ? InstallCoreAsync(row, replace: true) : Task.CompletedTask;

    [RelayCommand]
    private Task InstallAsync(CatalogRow? row) => row is { CanInstall: true } ? InstallCoreAsync(row, replace: false) : Task.CompletedTask;

    private async Task InstallCoreAsync(CatalogRow row, bool replace)
    {
        if (TargetServer is not { } server)
        {
            return;
        }

        if (!server.CanEdit)
        {
            MainViewModel.Shell?.Toast(Loc.T("Servers_SaveWhileRunning"), isError: true);
            return;
        }

        row.IsInstalling = true;
        InstallProgress = 0;
        try
        {
            var def = server.Definition;
            var (file, error) = await ResolveAsync(row.Item, def, CancellationToken.None).ConfigureAwait(true);

            if (file is not { } f)
            {
                MainViewModel.Shell?.Toast(Loc.F("Plugins_ProviderFailed", row.Item.Provider, error ?? "?"), isError: true);
                return;
            }

            var temp = Path.Combine(Path.GetTempPath(), "packcheck-dl-" + Guid.NewGuid().ToString("N") + Path.GetExtension(f.FileName));
            try
            {
                await VerifiedDownloader.DownloadAsync(_http, f.Url, temp, f.Hash, f.Alg, 2L * 1024 * 1024 * 1024, new Progress<double>(p => InstallProgress = p * 0.6), CancellationToken.None).ConfigureAwait(true);
                bool ok;
                if (Kind == CatalogKind.Modpack)
                {
                    ok = await InstallModpackAsync(def, temp, f.FileName).ConfigureAwait(true);
                }
                else
                {
                    var folder = Kind == CatalogKind.Plugin ? server.PluginsDir : server.ModsDir;
                    Directory.CreateDirectory(folder);
                    var dest = Path.Combine(folder, Path.GetFileName(f.FileName));
                    var old = replace && row.InstalledFile is { } installed ? Path.Combine(folder, installed) : null;
                    var parked = old is null ? null : old + ".packcheck-old";
                    ok = await Preflight.ConfirmChangeAsync(def,
                        () =>
                        {
                            if (old is not null && File.Exists(old))
                            {
                                File.Move(old, parked!, overwrite: true);
                            }

                            File.Copy(temp, dest, overwrite: replace);
                        },
                        () =>
                        {
                            File.Delete(dest);
                            if (parked is not null && File.Exists(parked))
                            {
                                File.Move(parked, old!);
                            }
                        }).ConfigureAwait(true);
                    if (ok && parked is not null && File.Exists(parked))
                    {
                        File.Delete(parked);
                    }

                    if (ok)
                    {
                        row.InstalledFile = Path.GetFileName(f.FileName);
                        row.HasUpdate = false;
                    }
                }

                if (ok)
                {
                    row.Installed = true;
                    server.RefreshPlugins();
                    MainViewModel.Shell?.Toast(Loc.F(replace ? "Browse_Updated" : "Plugins_Installed", row.Item.Title));
                }
            }
            finally
            {
                File.Delete(temp);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or InvalidDataException or UnsafeArchiveException or CryptographicException)
        {
            MainViewModel.Shell?.Toast(ex.Message, isError: true);
        }
        finally
        {
            row.IsInstalling = false;
            InstallProgress = 0;
        }
    }

    private async Task<bool> InstallModpackAsync(ServerDefinition def, string file, string name)
    {
        if (name.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
        {
            await MrpackInstaller.InstallAsync(_http, file, def.Directory,
                (h, url, target, hash, alg) => VerifiedDownloader.DownloadAsync(h, url, target, hash, alg, 512L * 1024 * 1024, null, CancellationToken.None),
                new Progress<double>(p => InstallProgress = 0.6 + (p * 0.4)), CancellationToken.None).ConfigureAwait(true);
            return true;
        }

        // CurseForge server pack: a zip of the server folder.
        var staging = Path.Combine(Path.GetTempPath(), "packcheck-pack-" + Guid.NewGuid().ToString("N"));
        SafeZip.Extract(file, staging);
        var root = Directory.GetDirectories(staging).Length == 1 && Directory.GetFiles(staging).Length == 0 ? Directory.GetDirectories(staging)[0] : staging;
        foreach (var src in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(def.Directory, Path.GetRelativePath(root, src));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest, overwrite: true);
        }

        Directory.Delete(staging, true);
        return true;
    }
}

public sealed class PluginsViewModel(IEnumerable<ICatalog> catalogs, IEnumerable<IPluginProvider> plugins, IEnumerable<IModProvider> mods, ServersViewModel servers, AppSettings settings, HttpClient http)
    : ContentBrowserViewModel(catalogs, plugins, mods, servers, settings, http)
{
    public override bool IsModPage => false;
}

public sealed class ModsViewModel(IEnumerable<ICatalog> catalogs, IEnumerable<IPluginProvider> plugins, IEnumerable<IModProvider> mods, ServersViewModel servers, AppSettings settings, HttpClient http)
    : ContentBrowserViewModel(catalogs, plugins, mods, servers, settings, http)
{
    public override bool IsModPage => true;
}

/// <summary>An entry of the category filter.</summary>
public sealed record CategoryOption(string Key, string Text)
{
    public static string Label(string key) =>
        Loc.Instance.Has("Category_" + key) ? Loc.T("Category_" + key) : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key.Replace('_', ' ').Replace('-', ' '));
}
