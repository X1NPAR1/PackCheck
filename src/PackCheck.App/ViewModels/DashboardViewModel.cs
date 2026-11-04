using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackCheck.App.Infrastructure;
using PackCheck.Servers;

namespace PackCheck.App.ViewModels;

/// <summary>Every server at a glance: what runs, who plays, how much memory and CPU is in use, and recent crashes.</summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly ServersViewModel _servers;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    public DashboardViewModel(ServersViewModel servers)
    {
        _servers = servers;
        _timer.Tick += (_, _) => Refresh();
    }

    public ObservableCollection<ServerItemViewModel> Rows => _servers.Items;

    [ObservableProperty]
    private int _running;

    [ObservableProperty]
    private int _total;

    [ObservableProperty]
    private int _players;

    [ObservableProperty]
    private int _allottedMb;

    [ObservableProperty]
    private int _crashes24h;

    [ObservableProperty]
    private string _statusLine = string.Empty;

    public bool IsEmpty => Total == 0;

    public string RunningText => $"{Running} / {Total}";

    public string AllottedText => AllottedMb >= 1024 ? $"{AllottedMb / 1024d:0.#} GB" : $"{AllottedMb} MB";

    /// <summary>Called when the page opens; values refresh every two seconds while it is shown.</summary>
    public void Activate()
    {
        Refresh();
        _timer.Start();
    }

    public void Deactivate() => _timer.Stop();

    public void Refresh()
    {
        var items = _servers.Items.ToList();
        Total = items.Count;
        Running = items.Count(s => s.IsRunning);
        // The ping's count is authoritative; without a reply the names seen in the console are the best estimate.
        Players = items.Where(s => s.IsRunning).Sum(s => s.PingOnline ?? s.Players.Count);
        AllottedMb = items.Where(s => s.IsRunning).Sum(s => s.Definition.MaxMemoryMb);
        Crashes24h = items.Sum(s => CountRecentCrashes(s.Definition.Directory, TimeSpan.FromDays(1)));
        var crashed = items.Count(s => s.IsCrashed);
        StatusLine = Loc.F("Dash_Status", Running, Total - Running - crashed, crashed);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(RunningText));
        OnPropertyChanged(nameof(AllottedText));
    }

    /// <summary>Crash reports written in the last <paramref name="window"/> (Minecraft's crash-reports folder).</summary>
    public static int CountRecentCrashes(string serverDir, TimeSpan window)
    {
        var dir = Path.Combine(serverDir, "crash-reports");
        if (!Directory.Exists(dir))
        {
            return 0;
        }

        var since = DateTime.Now - window;
        try
        {
            return Directory.EnumerateFiles(dir, "*.txt").Count(f => File.GetLastWriteTime(f) >= since);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    [RelayCommand]
    private static void Open(ServerItemViewModel? server)
    {
        if (server is not null)
        {
            MainViewModel.Shell?.OpenServerCommand.Execute(server);
        }
    }

    [RelayCommand]
    private void NewServer()
    {
        MainViewModel.Shell?.Navigate(_servers);
        _servers.NewServerCommand.Execute(null);
    }
}
