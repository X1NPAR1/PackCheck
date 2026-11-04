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
    private List<string> _pendingMods = [];

    [ObservableProperty]
    private string _modList = string.Empty;

    [ObservableProperty]
    private string? _modSolveStatus;

    private static HttpClient Http => (HttpClient)App.Services.GetService(typeof(HttpClient))!;

    [RelayCommand]
    private async Task SolveModsAsync()
    {
        var slugs = ModList.Split([',', '\n', '\r', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(ContentLinks.Parse).Where(l => l?.Source == LinkSource.Modrinth).Select(l => l!.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (slugs.Count == 0)
        {
            ModSolveStatus = Loc.T("ModFirst_NoMods");
            return;
        }

        try
        {
            ModSolveStatus = Loc.F("ModFirst_Checking", slugs.Count);
            var support = new List<IReadOnlyCollection<(string, string)>>();
            foreach (var slug in slugs)
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(new Uri($"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(slug)}/version")).ConfigureAwait(true));
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

            if (Versions.Contains(solution.MinecraftVersion))
            {
                Version = solution.MinecraftVersion;
            }

            _pendingMods = slugs;
            ModSolveStatus = Loc.F("ModFirst_Solved", Platform, solution.MinecraftVersion, slugs.Count);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException)
        {
            ModSolveStatus = ex.Message;
        }
    }

    /// <summary>After the server exists, the chosen mods are installed for its loader and version.</summary>
    private async Task InstallPendingModsAsync(ServerDefinition def)
    {
        if (_pendingMods.Count == 0 || def.Platform != Platform)
        {
            return;
        }

        var done = 0;
        foreach (var slug in _pendingMods)
        {
            Status = Loc.F("ModFirst_Installing", slug, ++done, _pendingMods.Count);
            try
            {
                var d = await ContentLinks.ResolveAsync(Http, new ContentLink(LinkSource.Modrinth, slug), def, CancellationToken.None).ConfigureAwait(true);
                await ContentLinks.InstallAsync(Http, d, def, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.IO.InvalidDataException or System.IO.IOException or JsonException or TaskCanceledException)
            {
                Serilog.Log.Warning(ex, "Mod {Slug} could not be installed", slug);
            }
        }
    }
}
