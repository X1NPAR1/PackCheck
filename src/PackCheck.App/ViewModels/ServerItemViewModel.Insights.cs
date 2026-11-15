using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackCheck.App.Infrastructure;
using PackCheck.Servers;

namespace PackCheck.App.ViewModels;

/// <summary>Insights tab: activity timeline from the logs, the player scoreboard from the stats files, and disk usage with a safe clean-up.</summary>
public sealed partial class ServerItemViewModel
{
    public const int InsightsTab = 12;

    private IReadOnlyList<ActivityEvent> _activity = [];

    public ObservableCollection<ActivityEvent> ActivityRows { get; } = [];

    private IReadOnlyList<PlayerStats> _scores = [];

    private bool _loadingScoreWorlds;

    public ObservableCollection<PlayerStats> ScoreRows { get; } = [];

    public ObservableCollection<string> ScoreWorlds { get; } = [];

    /// <summary>Sort keys; each maps to a column in <see cref="ScoreKey"/> and is shown via Score_Sort_{key}.</summary>
    public IReadOnlyList<string> ScoreSorts { get; } = ["Hours", "Name", "Deaths", "SinceDeath", "MobKills", "PvP", "Diamonds", "Km", "Jumps", "Used", "Fish"];

    [ObservableProperty]
    private string? _scoreWorld;

    [ObservableProperty]
    private string _scoreSort = "Hours";

    [ObservableProperty]
    private bool _scoreDescending = true;

    public ObservableCollection<StorageEntry> StorageFolders { get; } = [];

    public ObservableCollection<StorageEntry> StorageFiles { get; } = [];

    public IReadOnlyList<string> ActivityFilters { get; } = ["All", "Chat", "Join", "Leave", "Death", "Advancement", "Command", "Server"];

    [ObservableProperty]
    private string _activityFilter = "All";

    [ObservableProperty]
    private string _activitySearch = string.Empty;

    [ObservableProperty]
    private string? _storageSummary;

    [ObservableProperty]
    private long _cleanableBytes;

    public bool CanCleanStorage => CleanableBytes > 0;

    partial void OnActivityFilterChanged(string value) => ApplyActivityFilter();

    partial void OnActivitySearchChanged(string value) => ApplyActivityFilter();

    partial void OnScoreWorldChanged(string? value)
    {
        if (!_loadingScoreWorlds)
        {
            _ = LoadScoresAsync();
        }
    }

    partial void OnScoreSortChanged(string value) => ApplyScoreSort();

    partial void OnScoreDescendingChanged(bool value) => ApplyScoreSort();

    partial void OnCleanableBytesChanged(long value) => OnPropertyChanged(nameof(CanCleanStorage));

    [RelayCommand]
    private async Task RefreshInsightsAsync()
    {
        var dir = Definition.Directory;
        var world = ScoreWorld;
        (IReadOnlyList<ActivityEvent> activity, IReadOnlyList<string> worlds, StorageReport? storage) data;
        try
        {
            data = await Task.Run(() => (ActivityLog.Read(dir, 14), Scoreboard.Worlds(dir), Directory.Exists(dir) ? StorageAnalyzer.Analyze(dir) : null)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            MainViewModel.Shell?.Toast(ex.Message, isError: true);
            return;
        }

        var (activity, worlds, storage) = data;
        _activity = activity;
        ApplyActivityFilter();
        _loadingScoreWorlds = true;
        ScoreWorlds.Clear();
        foreach (var w in worlds)
        {
            ScoreWorlds.Add(w);
        }

        ScoreWorld = world is not null && worlds.Contains(world) ? world : worlds.FirstOrDefault();
        _loadingScoreWorlds = false;
        await LoadScoresAsync().ConfigureAwait(true);

        StorageFolders.Clear();
        StorageFiles.Clear();
        if (storage is not null)
        {
            foreach (var f in storage.Folders.Take(12))
            {
                StorageFolders.Add(f);
            }

            foreach (var f in storage.LargestFiles)
            {
                StorageFiles.Add(f);
            }

            CleanableBytes = storage.Cleanable;
            StorageSummary = Loc.F("Storage_Summary", Bytes.Format(storage.Total), Bytes.Format(storage.Cleanable));
        }
    }

    private async Task LoadScoresAsync()
    {
        var dir = Definition.Directory;
        var world = ScoreWorld;
        try
        {
            _scores = world is null ? [] : await Task.Run(() => Scoreboard.Read(dir, world)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MainViewModel.Shell?.Toast(ex.Message, isError: true);
            _scores = [];
        }

        ApplyScoreSort();
    }

    private static IComparable ScoreKey(PlayerStats p, string sort) => sort switch
    {
        "Name" => p.Name,
        "Deaths" => p.Deaths,
        "SinceDeath" => p.HoursSinceDeath,
        "MobKills" => p.MobKills,
        "PvP" => p.PlayerKills,
        "Diamonds" => p.DiamondsMined,
        "Km" => p.KmWalked,
        "Jumps" => p.Jumps,
        "Used" => p.ItemsUsed,
        "Fish" => p.FishCaught,
        _ => p.HoursPlayed,
    };

    private void ApplyScoreSort()
    {
        var sorted = ScoreDescending ? _scores.OrderByDescending(p => ScoreKey(p, ScoreSort)) : _scores.OrderBy(p => ScoreKey(p, ScoreSort));
        ScoreRows.Clear();
        foreach (var p in sorted.ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            ScoreRows.Add(p);
        }
    }

    private void ApplyActivityFilter()
    {
        ActivityRows.Clear();
        var q = ActivitySearch.Trim();
        foreach (var e in _activity.Where(e => ActivityFilter == "All" || e.Kind.ToString() == ActivityFilter)
                     .Where(e => q.Length == 0 || e.Text.Contains(q, StringComparison.OrdinalIgnoreCase) || (e.Player?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
                     .Take(500))
        {
            ActivityRows.Add(e);
        }
    }

    [RelayCommand]
    private async Task CleanStorageAsync()
    {
        if (MainViewModel.Shell is not { } shell
            || !await shell.ConfirmAsync(Loc.T("Storage_CleanTitle"), Loc.F("Storage_CleanBody", Bytes.Format(CleanableBytes)), Loc.T("Storage_Clean"), danger: true).ConfigureAwait(true))
        {
            return;
        }

        var dir = Definition.Directory;
        var freed = await Task.Run(() => StorageAnalyzer.Clean(dir)).ConfigureAwait(true);
        shell.Toast(Loc.F("Storage_Freed", Bytes.Format(freed)));
        await RefreshInsightsAsync().ConfigureAwait(true);
    }
}
