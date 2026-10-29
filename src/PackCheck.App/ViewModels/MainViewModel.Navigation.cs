using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PackCheck.App.Infrastructure;

namespace PackCheck.App.ViewModels;

/// <summary>A place in the app: side menu page, open server and its tab.</summary>
public sealed record NavState(int NavIndex, ServerItemViewModel? Server, int Tab);

/// <summary>Back/forward like a browser (Alt+arrows, mouse buttons 4 and 5) and closing the open panel (middle click, Ctrl+W).</summary>
public sealed partial class MainViewModel
{
    /// <summary>Side menu pages in the order they are shown (Ctrl+1…9).</summary>
    public static IReadOnlyList<int> MenuOrder { get; } = [0, 1, 2, 5, 7, 6, 8, 3, 4];

    /// <summary>Server tabs in the order of the tab bar (Ctrl+Tab); 3 is the plugins/mods tab and 10 the upgrade wizard (not in the bar).</summary>
    private static readonly int[] TabOrder = [0, 1, 2, 5, 3, 4, 6, 9, 8, 7];

    private readonly List<NavState> _back = [];
    private readonly List<NavState> _forward = [];
    private NavState? _here;
    private bool _restoring;
    private ServerItemViewModel? _watched;

    [ObservableProperty]
    private bool _isFullScreen;

    private void InitNavigation()
    {
        _here = Capture();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NavIndex))
            {
                Record();
            }
        };
        Servers.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ServersViewModel.Selected))
            {
                Watch(Servers.Selected);
                Record();
            }
        };
        Watch(Servers.Selected);
    }

    private void Watch(ServerItemViewModel? server)
    {
        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnServerChanged;
        }

        _watched = server;
        if (server is not null)
        {
            server.PropertyChanged += OnServerChanged;
        }
    }

    private void OnServerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Tab")
        {
            Record();
        }
    }

    private NavState Capture() => new(NavIndex, Servers.Selected, Servers.Selected?.Tab ?? 0);

    private void Record()
    {
        if (_restoring)
        {
            return;
        }

        var now = Capture();
        if (_here is not null && _here != now)
        {
            _back.Add(_here);
            if (_back.Count > 100)
            {
                _back.RemoveAt(0);
            }

            _forward.Clear();
        }

        _here = now;
    }

    public bool GoBack() => Move(_back, _forward);

    public bool GoForward() => Move(_forward, _back);

    private bool Move(List<NavState> from, List<NavState> to)
    {
        while (from.Count > 0)
        {
            var target = from[^1];
            from.RemoveAt(from.Count - 1);
            if (target.Server is not null && !Servers.Items.Contains(target.Server))
            {
                continue; // the server was removed meanwhile
            }

            if (_here is not null)
            {
                to.Add(_here);
            }

            _restoring = true;
            try
            {
                if (target.Server is not null || target.NavIndex == 0)
                {
                    Servers.Selected = target.Server ?? Servers.Selected;
                }

                if (Servers.Selected is { } s && s == target.Server)
                {
                    s.Tab = target.Tab;
                }

                NavIndex = target.NavIndex;
            }
            finally
            {
                _restoring = false;
            }

            _here = Capture();
            return true;
        }

        return false;
    }

    /// <summary>Closes what is open on top: a dialog, the new-server wizard, the assistant history, then the open server tab (back to Overview).</summary>
    public bool CloseTopmost()
    {
        if (Confirm is { } c)
        {
            c.Result.TrySetResult(false);
            return true;
        }

        if (Servers.Wizard is not null && NavIndex == 0)
        {
            Servers.Wizard = null;
            return true;
        }

        if (NavIndex == 8 && Assistant.IsHistoryOpen)
        {
            Assistant.IsHistoryOpen = false;
            return true;
        }

        if (NavIndex == 0 && Servers.Selected is { Tab: not 0 } s)
        {
            s.Tab = 0;
            return true;
        }

        return false;
    }

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab through the open server's tabs.</summary>
    public bool CycleServerTab(int direction)
    {
        if (NavIndex != 0 || Servers.Selected is not { } s)
        {
            return false;
        }

        var tabs = TabOrder.Where(t => t != 3 || s.HasContentTab).ToList();
        var i = tabs.IndexOf(s.Tab);
        s.Tab = tabs[((i < 0 ? 0 : i) + direction + tabs.Count) % tabs.Count];
        return true;
    }

    public void GoToMenu(int position)
    {
        if (position >= 0 && position < MenuOrder.Count)
        {
            var index = MenuOrder[position];
            if ((index == 2 && !PluginsEnabled) || (index == 5 && !ModsEnabled))
            {
                return;
            }

            NavIndex = index;
        }
    }

    public void ShowShortcuts() => _ = ConfirmAsync(Loc.T("Shortcuts_Title"), Loc.T("Shortcuts_Body"), "OK");
}
