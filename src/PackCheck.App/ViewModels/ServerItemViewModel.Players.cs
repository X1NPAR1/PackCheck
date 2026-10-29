using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackCheck.App.Infrastructure;
using PackCheck.Servers;
using PackCheck.Validation;

namespace PackCheck.App.ViewModels;

/// <summary>One player in the Players tab: history from the log plus whitelist/op/ban state.</summary>
public sealed class PlayerRow
{
    public required string Name { get; init; }
    public bool IsOnline { get; init; }
    public bool IsOp { get; init; }
    public bool IsBanned { get; init; }
    public bool IsWhitelisted { get; init; }
    public PlayerRecord? Record { get; init; }
    public IReadOnlyList<string> Alts { get; init; } = [];

    public string Avatar => $"https://mc-heads.net/avatar/{Name}/40";
    public string Initial => Name[..1].ToUpperInvariant();
    public bool HasAlts => Alts.Count > 0;
    public string AltsText => Loc.F("Players_Alts", string.Join(", ", Alts));
    public string IpsText => Record is { Ips.Count: > 0 } r ? string.Join(", ", r.Ips) : "–";

    public string PlayTimeText
    {
        get
        {
            var seconds = (Record?.PlaySeconds ?? 0) + (Record?.OnlineSince is { } s ? (long)(DateTimeOffset.Now - s).TotalSeconds : 0);
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{t.Minutes} min";
        }
    }

    public string LastSeenText => IsOnline ? Loc.T("Players_OnlineNow")
        : Record is { } r ? r.LastSeen.LocalDateTime.ToString("g", Loc.Instance.Culture) : "–";
}

/// <summary>Players tab: history, search/filter and whitelist/op/ban/kick that work whether the server runs or not.</summary>
public sealed partial class ServerItemViewModel
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private PlayerTracker? _tracker;

    private PlayerTracker Tracker => _tracker ??= new PlayerTracker(PlayerTracker.FileFor(Definition.Directory));

    public ObservableCollection<PlayerRow> PlayerRows { get; } = [];

    public static IReadOnlyList<string> PlayerFilters => [Loc.T("Players_All"), Loc.T("Players_Online"), Loc.T("Players_Ops"), Loc.T("Players_Whitelisted"), Loc.T("Players_Banned")];

    [ObservableProperty]
    private int _playerFilter;

    [ObservableProperty]
    private string _playerSearch = string.Empty;

    [ObservableProperty]
    private string _playerNameInput = string.Empty;

    [ObservableProperty]
    private string _playerSummary = string.Empty;

    partial void OnPlayerFilterChanged(int value) => RefreshPlayers();

    partial void OnTabChanged(int value)
    {
        if (value == PlayersTab)
        {
            RefreshPlayers();
        }
        else if (value == PerformanceTab)
        {
            RefreshPerformance();
        }
    }

    partial void OnPlayerSearchChanged(string value) => RefreshPlayers();

    /// <summary>Called for every parsed console line.</summary>
    private void TrackPlayerHistory(LogLine line)
    {
        if (Tracker.OnMessage(line.Message, DateTimeOffset.Now)
            && (line.Message.EndsWith("left the game", StringComparison.Ordinal) || line.Message.EndsWith("joined the game", StringComparison.Ordinal)))
        {
            Tracker.Save();
            if (Tab == PlayersTab)
            {
                RefreshPlayers();
            }
        }
    }

    private void ClosePlayerSessions()
    {
        Tracker.CloseAll(DateTimeOffset.Now);
        Tracker.Save();
        RefreshPlayers();
    }

    public const int PlayersTab = 8;

    public void RefreshPlayers()
    {
        var lists = new PlayerLists(Definition.Directory);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        names.UnionWith(Tracker.All.Select(p => p.Name));
        names.UnionWith(lists.Whitelist);
        names.UnionWith(lists.Ops);
        names.UnionWith(lists.Banned);
        names.UnionWith(Players);

        var rows = names.Select(n => new PlayerRow
        {
            Name = Tracker.Find(n)?.Name ?? n,
            IsOnline = Players.Contains(n, StringComparer.OrdinalIgnoreCase),
            IsOp = lists.Ops.Contains(n),
            IsBanned = lists.Banned.Contains(n),
            IsWhitelisted = lists.Whitelist.Contains(n),
            Record = Tracker.Find(n),
            Alts = Tracker.SameIp(n),
        }).ToList();

        PlayerSummary = Loc.F("Players_Summary", rows.Count, rows.Count(r => r.IsOnline), rows.Count(r => r.IsOp), rows.Count(r => r.IsBanned));
        var shown = rows
            .Where(r => PlayerFilter switch { 1 => r.IsOnline, 2 => r.IsOp, 3 => r.IsWhitelisted, 4 => r.IsBanned, _ => true })
            .Where(r => PlayerSearch.Length == 0 || r.Name.Contains(PlayerSearch, StringComparison.OrdinalIgnoreCase) || r.IpsText.Contains(PlayerSearch, StringComparison.Ordinal))
            .OrderByDescending(r => r.IsOnline).ThenByDescending(r => r.Record?.LastSeen).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);

        PlayerRows.Clear();
        foreach (var r in shown)
        {
            PlayerRows.Add(r);
        }
    }

    [RelayCommand]
    private Task ToggleWhitelistAsync(PlayerRow? p) => p is null ? Task.CompletedTask : ChangeAsync(PlayerList.Whitelist, p.Name, !p.IsWhitelisted);

    [RelayCommand]
    private Task ToggleOpAsync(PlayerRow? p) => p is null ? Task.CompletedTask : ChangeAsync(PlayerList.Ops, p.Name, !p.IsOp);

    [RelayCommand]
    private async Task ToggleBanAsync(PlayerRow? p)
    {
        if (p is null || MainViewModel.Shell is not { } shell)
        {
            return;
        }

        if (!p.IsBanned && !await shell.ConfirmAsync(Loc.T("Players_Ban"), Loc.F("Players_BanConfirm", p.Name), Loc.T("Players_Ban"), danger: true).ConfigureAwait(true))
        {
            return;
        }

        await ChangeAsync(PlayerList.Banned, p.Name, !p.IsBanned).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task KickAsync(PlayerRow? p)
    {
        if (p is null || !IsRunning)
        {
            return;
        }

        await _process.SendCommandAsync("kick " + p.Name).ConfigureAwait(true);
        MainViewModel.Shell?.Toast(Loc.F("Players_Kicked", p.Name));
    }

    [RelayCommand]
    private Task AddToListAsync(string? list)
    {
        var name = PlayerNameInput.Trim();
        if (!PlayerLists.IsValidName(name))
        {
            MainViewModel.Shell?.Toast(Loc.T("Players_BadName"), isError: true);
            return Task.CompletedTask;
        }

        PlayerNameInput = string.Empty;
        return ChangeAsync(Enum.Parse<PlayerList>(list ?? nameof(PlayerList.Whitelist)), name, true);
    }

    /// <summary>
    /// Running server: the game's own command (it rewrites the json files itself).
    /// Stopped server: the json file is edited directly, with the player's UUID from usercache, Mojang or the offline formula.
    /// </summary>
    private async Task ChangeAsync(PlayerList list, string name, bool present)
    {
        if (!PlayerLists.IsValidName(name))
        {
            return;
        }

        try
        {
            if (IsRunning)
            {
                await _process.SendCommandAsync(PlayerLists.Command(list, name, present)).ConfigureAwait(true);
                await Task.Delay(800).ConfigureAwait(true);
            }
            else
            {
                var lists = new PlayerLists(Definition.Directory);
                var uuid = Tracker.Find(name)?.Uuid ?? lists.CachedUuid(name) ?? await LookupUuidAsync(name).ConfigureAwait(true);
                if (uuid is null)
                {
                    MainViewModel.Shell?.Toast(Loc.F("Players_NoUuid", name), isError: true);
                    return;
                }

                lists.Set(list, name, uuid, present);
            }

            MainViewModel.Shell?.Toast(Loc.T(present ? "Players_Added_" + list : "Players_Removed_" + list).Replace("{0}", name, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MainViewModel.Shell?.Toast(ex.Message, isError: true);
        }

        RefreshPlayers();
    }

    private async Task<string?> LookupUuidAsync(string name)
    {
        var props = Path.Combine(Definition.Directory, "server.properties");
        if (File.Exists(props) && ServerProperties.Parse(await File.ReadAllTextAsync(props).ConfigureAwait(true)).TryGetValue("online-mode", out var mode) && mode == "false")
        {
            return PlayerLists.OfflineUuid(name);
        }

        try
        {
            var json = await Http.GetStringAsync(new Uri("https://api.mojang.com/users/profiles/minecraft/" + Uri.EscapeDataString(name))).ConfigureAwait(true);
            var id = JsonNode.Parse(json)?["id"]?.GetValue<string>();
            return id is { Length: 32 } ? $"{id[..8]}-{id[8..12]}-{id[12..16]}-{id[16..20]}-{id[20..]}" : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
