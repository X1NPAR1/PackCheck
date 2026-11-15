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

    private int _scoreLoad;

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

    public IReadOnlyList<string> ActivityFilters { get; } = ["All", "Chat", "Join", "Leave", "Death", "Advancement", "Command", "Server", "PackCheck"];

    /// <summary>How many days back the timeline reads (0 = every log on disk); shown via Activity_Range_{n}.</summary>
    public IReadOnlyList<int> ActivityRanges { get; } = [1, 7, 14, 30, 90, 0];

    private const int ActivityPageSize = 500;

    private int _insightsRefresh;

    private List<ActivityEvent> _activityFiltered = [];

    [ObservableProperty]
    private int _activityRange = 14;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivityPageText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousActivityPageCommand), nameof(NextActivityPageCommand))]
    private int _activityPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivityPageText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousActivityPageCommand), nameof(NextActivityPageCommand), nameof(ExportActivityCommand))]
    private int _activityTotal;

    private int ActivityPageCount => Math.Max(1, (ActivityTotal + ActivityPageSize - 1) / ActivityPageSize);

    public string ActivityPageText => Loc.F("Activity_Page", ActivityPage + 1, ActivityPageCount, ActivityTotal);

    [ObservableProperty]
    private string _activityFilter = "All";

    [ObservableProperty]
    private string _activitySearch = string.Empty;

    [ObservableProperty]
    private string? _storageSummary;

    [ObservableProperty]
    private long _cleanableBytes;

    public bool CanCleanStorage => CleanableBytes > 0;

    partial void OnActivityRangeChanged(int value) => _ = RefreshInsightsAsync();

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
        var refresh = ++_insightsRefresh;
        (IReadOnlyList<ActivityEvent> activity, IReadOnlyList<string> worlds, StorageReport? storage) data;
        try
        {
            var from = ActivityRange > 0 ? DateTime.Now.AddDays(-ActivityRange) : DateTime.MinValue;
            data = await Task.Run(() => (ActivityLog.Read(dir, from, DateTime.MaxValue), Scoreboard.Worlds(dir), Directory.Exists(dir) ? StorageAnalyzer.Analyze(dir) : null)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            if (refresh == _insightsRefresh)
            {
                MainViewModel.Shell?.Toast(ex.Message, isError: true);
            }

            return;
        }

        if (refresh != _insightsRefresh)
        {
            return; // the range changed meanwhile; the newer refresh fills the tab
        }

        var (activity, worlds, storage) = data;
        _activity = activity;
        ApplyActivityFilter();
        var world = ScoreWorld; // read after the background work so a pick made meanwhile is kept
        _loadingScoreWorlds = true;
        ScoreWorlds.Clear();
        foreach (var w in worlds)
        {
            ScoreWorlds.Add(w);
        }

        ScoreWorld = world is not null && worlds.Contains(world) ? world : worlds.FirstOrDefault();
        _loadingScoreWorlds = false;
        await LoadScoresAsync().ConfigureAwait(true);
        if (refresh != _insightsRefresh)
        {
            return; // a newer refresh started while the scores loaded; it fills storage too
        }

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
        var load = ++_scoreLoad;
        IReadOnlyList<PlayerStats> scores;
        try
        {
            scores = world is null ? [] : await Task.Run(() => Scoreboard.Read(dir, world)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (load == _scoreLoad)
            {
                MainViewModel.Shell?.Toast(ex.Message, isError: true);
            }

            scores = [];
        }

        if (load != _scoreLoad)
        {
            return; // a newer world pick is loading; its result wins
        }

        _scores = scores;
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
        var q = ActivitySearch.Trim();
        _activityFiltered = _activity.Where(e => ActivityFilter == "All" || e.Kind.ToString() == ActivityFilter)
            .Where(e => q.Length == 0 || e.Text.Contains(q, StringComparison.OrdinalIgnoreCase) || (e.Player?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();
        ActivityTotal = _activityFiltered.Count;
        ActivityPage = 0;
        ShowActivityPage();
    }

    private void ShowActivityPage()
    {
        ActivityRows.Clear();
        foreach (var e in _activityFiltered.Skip(ActivityPage * ActivityPageSize).Take(ActivityPageSize))
        {
            ActivityRows.Add(e);
        }
    }

    private bool CanPreviousActivityPage() => ActivityPage > 0;

    [RelayCommand(CanExecute = nameof(CanPreviousActivityPage))]
    private void PreviousActivityPage()
    {
        ActivityPage--;
        ShowActivityPage();
    }

    private bool CanNextActivityPage() => ActivityPage + 1 < ActivityPageCount;

    [RelayCommand(CanExecute = nameof(CanNextActivityPage))]
    private void NextActivityPage()
    {
        ActivityPage++;
        ShowActivityPage();
    }

    private bool CanExportActivity() => ActivityTotal > 0;

    /// <summary>Saves every row that matches the current filter (all pages) as CSV.</summary>
    [RelayCommand(CanExecute = nameof(CanExportActivity))]
    private async Task ExportActivityAsync()
    {
        var dlg = new SaveFileDialog { FileName = $"{Name}-activity-{DateTime.Now:yyyyMMdd}.csv", Filter = "CSV (*.csv)|*.csv" };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        var rows = _activityFiltered;
        try
        {
            await File.WriteAllTextAsync(dlg.FileName, ActivityLog.ToCsv(rows), new System.Text.UTF8Encoding(true)).ConfigureAwait(true);
            MainViewModel.Shell?.Toast(Loc.F("Activity_Exported", rows.Count));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MainViewModel.Shell?.Toast(ex.Message, isError: true);
        }
    }

    /// <summary>Records a PackCheck action (start, stop, backup) for the timeline.</summary>
    private void Journal(string text, string? actor = null)
    {
        var dir = Definition.Directory;
        var who = actor ?? Environment.UserName;
        _ = Task.Run(() => ActionJournal.Append(dir, who, text));
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
