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

    public ObservableCollection<PlayerStats> ScoreRows { get; } = [];

    public ObservableCollection<StorageEntry> StorageFolders { get; } = [];

    public ObservableCollection<StorageEntry> StorageFiles { get; } = [];

    public IReadOnlyList<string> ActivityFilters { get; } = ["All", "Chat", "Join", "Leave", "Death", "Advancement", "Command", "Server", "PackCheck"];

    /// <summary>How many days back the timeline reads (0 = every log on disk); shown via Activity_Range_{n}.</summary>
    public IReadOnlyList<int> ActivityRanges { get; } = [1, 7, 14, 30, 90, 0];

    private const int ActivityPageSize = 500;

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

    partial void OnCleanableBytesChanged(long value) => OnPropertyChanged(nameof(CanCleanStorage));

    [RelayCommand]
    private async Task RefreshInsightsAsync()
    {
        var dir = Definition.Directory;
        (IReadOnlyList<ActivityEvent> activity, IReadOnlyList<PlayerStats> scores, StorageReport? storage) data;
        try
        {
            var from = ActivityRange > 0 ? DateTime.Today.AddDays(-ActivityRange) : DateTime.MinValue;
            data = await Task.Run(() => (ActivityLog.Read(dir, from, DateTime.MaxValue), Scoreboard.Read(dir), Directory.Exists(dir) ? StorageAnalyzer.Analyze(dir) : null)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            MainViewModel.Shell?.Toast(ex.Message, isError: true);
            return;
        }

        var (activity, scores, storage) = data;
        _activity = activity;
        ApplyActivityFilter();
        ScoreRows.Clear();
        foreach (var s in scores)
        {
            ScoreRows.Add(s);
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
