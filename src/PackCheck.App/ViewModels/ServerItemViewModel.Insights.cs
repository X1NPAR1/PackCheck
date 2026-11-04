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

    partial void OnCleanableBytesChanged(long value) => OnPropertyChanged(nameof(CanCleanStorage));

    [RelayCommand]
    private async Task RefreshInsightsAsync()
    {
        var dir = Definition.Directory;
        (IReadOnlyList<ActivityEvent> activity, IReadOnlyList<PlayerStats> scores, StorageReport? storage) data;
        try
        {
            data = await Task.Run(() => (ActivityLog.Read(dir, 14), Scoreboard.Read(dir), Directory.Exists(dir) ? StorageAnalyzer.Analyze(dir) : null)).ConfigureAwait(true);
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
