using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackCheck.App.Infrastructure;
using PackCheck.Servers;

namespace PackCheck.App.ViewModels;

/// <summary>A jar that was updated and can be put back.</summary>
public sealed record RevertRow(string File, string Previous);

/// <summary>Plugins/Mods tab extras: add by link (Modrinth, Hangar, SpigotMC, GitHub, direct .jar), bulk updates with revert, and mclo.gs crash sharing.</summary>
public sealed partial class ServerItemViewModel
{
    public ObservableCollection<AddonCheck> AddonUpdatesList { get; } = [];

    public ObservableCollection<RevertRow> RevertRows { get; } = [];

    [ObservableProperty]
    private string _addLink = string.Empty;

    [ObservableProperty]
    private string? _contentStatus;

    [ObservableProperty]
    private bool _contentBusy;

    [ObservableProperty]
    private string? _mclogsUrl;

    [ObservableProperty]
    private string? _mclogsInsights;

    public bool HasAddonUpdates => AddonUpdatesList.Count > 0;

    private void RefreshReverts()
    {
        RevertRows.Clear();
        var dir = Path.Combine(Definition.Directory, PlatformInfo.UsesMods(Definition.Platform) ? "mods" : "plugins");
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (var jar in Directory.EnumerateFiles(dir, "*.jar"))
        {
            if (AddonUpdates.PreviousOf(Definition, jar) is { } prev)
            {
                RevertRows.Add(new RevertRow(jar, Path.GetFileName(prev)));
            }
        }
    }

    [RelayCommand]
    private async Task AddFromLinkAsync()
    {
        if (ContentLinks.Parse(AddLink) is not { } link)
        {
            ContentStatus = Loc.T("Content_LinkUnknown");
            return;
        }

        if (IsBusy)
        {
            ContentStatus = Loc.T("Content_StopFirst");
            return;
        }

        ContentBusy = true;
        try
        {
            ContentStatus = Loc.F("Content_Resolving", link.Source);
            var d = await ContentLinks.ResolveAsync(AppHttp, link, Definition, CancellationToken.None).ConfigureAwait(true);
            var path = await ContentLinks.InstallAsync(AppHttp, d, Definition, CancellationToken.None).ConfigureAwait(true);
            ContentStatus = Loc.F("Content_Added", Path.GetFileName(path), d.Version ?? "?") + (d.Sha512 is null && d.Sha256 is null ? " " + Loc.T("Content_NoHash") : string.Empty);
            AddLink = string.Empty;
            RefreshPlugins();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or IOException or JsonException or TaskCanceledException or UriFormatException or UnauthorizedAccessException)
        {
            ContentStatus = ex.Message;
        }
        finally
        {
            ContentBusy = false;
        }
    }

    [RelayCommand]
    private async Task CheckAddonUpdatesAsync()
    {
        ContentBusy = true;
        try
        {
            ContentStatus = Loc.T("Content_Checking");
            var list = await AddonUpdates.CheckAsync(AppHttp, Definition, CancellationToken.None).ConfigureAwait(true);
            AddonUpdatesList.Clear();
            foreach (var u in list)
            {
                AddonUpdatesList.Add(u);
            }

            ContentStatus = list.Count == 0 ? Loc.T("Content_UpToDate") : Loc.F("Content_UpdatesFound", list.Count);
            OnPropertyChanged(nameof(HasAddonUpdates));
            RefreshReverts();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or ArgumentException)
        {
            ContentStatus = ex.Message;
        }
        finally
        {
            ContentBusy = false;
        }
    }

    private async Task UpdateAsync(IReadOnlyList<AddonCheck> items)
    {
        if (IsBusy)
        {
            ContentStatus = Loc.T("Content_StopFirst");
            return;
        }

        ContentBusy = true;
        var done = 0;
        try
        {
            foreach (var u in items)
            {
                ContentStatus = Loc.F("Content_Updating", u.Name, done + 1, items.Count);
                await AddonUpdates.ApplyAsync(AppHttp, Definition, u, CancellationToken.None).ConfigureAwait(true);
                done++;
                AddonUpdatesList.Remove(u);
            }

            ContentStatus = Loc.F("Content_Updated", done);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or TaskCanceledException or InvalidOperationException)
        {
            ContentStatus = Loc.F("Content_Updated", done) + " " + ex.Message;
        }
        finally
        {
            ContentBusy = false;
            OnPropertyChanged(nameof(HasAddonUpdates));
            RefreshPlugins();
            RefreshReverts();
        }
    }

    [RelayCommand]
    private Task UpdateAddonAsync(AddonCheck? u) => u is null ? Task.CompletedTask : UpdateAsync([u]);

    [RelayCommand]
    private Task UpdateAllAddonsAsync() => UpdateAsync([.. AddonUpdatesList]);

    [RelayCommand]
    private void RevertAddon(RevertRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (IsBusy)
        {
            ContentStatus = Loc.T("Content_StopFirst");
            return;
        }

        try
        {
            var restored = AddonUpdates.Revert(Definition, row.File);
            ContentStatus = Loc.F("Content_Reverted", Path.GetFileName(restored));
            RefreshPlugins();
            RefreshReverts();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ContentStatus = ex.Message;
        }
    }

    // ----- mclo.gs -----
    [RelayCommand]
    private async Task ShareCrashAsync()
    {
        var crashDir = Path.Combine(Definition.Directory, "crash-reports");
        var file = Directory.Exists(crashDir) ? Directory.EnumerateFiles(crashDir, "*.txt").OrderByDescending(File.GetLastWriteTime).FirstOrDefault() : null;
        file ??= Path.Combine(Definition.Directory, "logs", "latest.log");
        if (!File.Exists(file) || MainViewModel.Shell is not { } shell)
        {
            return;
        }

        if (!await shell.ConfirmAsync(Loc.T("Mclogs_Title"), Loc.F("Mclogs_Body", Path.GetFileName(file)), Loc.T("Mclogs_Share")).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var text = await File.ReadAllTextAsync(file).ConfigureAwait(true);
            var r = await MclogsShare.ShareAsync(AppHttp, text, CancellationToken.None).ConfigureAwait(true);
            MclogsUrl = r.Url;
            MclogsInsights = r.Problems.Count == 0 ? Loc.T("Mclogs_NoInsights")
                : string.Join("\n", r.Problems.Select(p => "• " + p.Problem + (p.Fixes.Count > 0 ? "\n   → " + string.Join("\n   → ", p.Fixes) : string.Empty)));
            System.Windows.Clipboard.SetText(r.Url);
            if (await shell.ConfirmAsync(Loc.F("Mclogs_Copied", r.Url), MclogsInsights, Loc.T("Mclogs_Open")).ConfigureAwait(true))
            {
                OpenMclogs();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException or System.Runtime.InteropServices.COMException)
        {
            shell.Toast(ex.Message, isError: true);
        }
    }

    [RelayCommand]
    private void OpenMclogs()
    {
        if (MclogsUrl is { } url && url.StartsWith("https://mclo.gs/", StringComparison.Ordinal))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
    }
}
