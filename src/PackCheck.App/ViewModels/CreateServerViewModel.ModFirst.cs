using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackCheck.App.Infrastructure;
using PackCheck.Servers;

namespace PackCheck.App.ViewModels;

/// <summary>Pick the mods first: the wizard finds the newest Minecraft version and loader they all support, then installs them with the server.</summary>
public sealed partial class CreateServerViewModel
{
    private List<ContentLink> _pendingMods = [];

    [ObservableProperty]
    private string _modList = string.Empty;

    [ObservableProperty]
    private string? _modSolveStatus;

    private static string? CurseForgeKey => ((AppSettings?)App.Services.GetService(typeof(AppSettings)))?.CurseForgeApiKey;

    private static HttpClient Http => (HttpClient)App.Services.GetService(typeof(HttpClient))!;

    /// <summary>A changed list must be solved again before it is installed.</summary>
    partial void OnModListChanged(string value)
    {
        _pendingMods = [];
        ModSolveStatus = null;
    }

    [RelayCommand]
    private async Task SolveModsAsync()
    {
        _pendingMods = [];
        var links = ModList.Split([',', '\n', '\r', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(ContentLinks.Parse).OfType<ContentLink>().Where(l => l.Source is LinkSource.Modrinth or LinkSource.CurseForge)
            .DistinctBy(l => (l.Source, l.Id.ToLowerInvariant())).ToList();
        if (links.Count == 0)
        {
            ModSolveStatus = Loc.T("ModFirst_NoMods");
            return;
        }

        try
        {
            ModSolveStatus = Loc.F("ModFirst_Checking", links.Count);
            var support = new List<IReadOnlyCollection<(string, string)>>();
            foreach (var link in links)
            {
                if (link.Source == LinkSource.CurseForge)
                {
                    support.Add(await ContentLinks.CurseForgeSupportAsync(Http, link.Id, CurseForgeKey, CancellationToken.None).ConfigureAwait(true));
                    continue;
                }

                using var doc = JsonDocument.Parse(await Http.GetStringAsync(new Uri($"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(link.Id)}/version")).ConfigureAwait(true));
                support.Add(doc.RootElement.EnumerateArray()
                    .SelectMany(v => v.GetProperty("loaders").EnumerateArray().Select(l => l.GetString()!)
                        .SelectMany(l => v.GetProperty("game_versions").EnumerateArray().Select(g => (l, g.GetString()!))))
                    .ToHashSet());
            }

            if (ModSolver.Solve(support) is not { } solution)
            {
                ModSolveStatus = Loc.T("ModFirst_NoCommon");
                return;
            }

            Platform = ModSolver.Platform(solution.Loader);
            for (var i = 0; i < 100 && !Versions.Contains(solution.MinecraftVersion); i++)
            {
                await Task.Delay(100).ConfigureAwait(true);
            }

            if (!Versions.Contains(solution.MinecraftVersion))
            {
                ModSolveStatus = Loc.F("ModFirst_VersionMissing", Platform, solution.MinecraftVersion);
                return;
            }

            Version = solution.MinecraftVersion;
            _pendingMods = links;
            ModSolveStatus = Loc.F("ModFirst_Solved", Platform, solution.MinecraftVersion, links.Count);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException)
        {
            ModSolveStatus = ex.Message;
        }
    }

    /// <summary>After the server exists, the chosen mods are installed for its loader and version.</summary>
    private async Task<IReadOnlyList<string>> InstallPendingModsAsync(ServerDefinition def, CancellationToken ct)
    {
        var failed = new List<string>();
        if (_pendingMods.Count == 0 || def.Platform != Platform)
        {
            return failed;
        }

        var done = 0;
        foreach (var link in _pendingMods)
        {
            var slug = link.Id;
            Status = Loc.F("ModFirst_Installing", slug, ++done, _pendingMods.Count);
            try
            {
                var d = await ContentLinks.ResolveAsync(Http, link, def, ct, () => CurseForgeKey).ConfigureAwait(true);
                await ContentLinks.InstallAsync(Http, d, def, ct).ConfigureAwait(true);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or InvalidOperationException or System.IO.InvalidDataException or System.IO.IOException or JsonException or TaskCanceledException)
            {
                Serilog.Log.Warning(ex, "Mod {Slug} could not be installed", slug);
                failed.Add(slug);
            }
        }

        return failed;
    }
}
