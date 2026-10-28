using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PackCheck.App.Infrastructure;
using PackCheck.App.ViewModels;

namespace PackCheck.App.Views;

public partial class ServerDetailView : UserControl
{
    private ServerItemViewModel? _vm;

    public ServerDetailView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.ScrollRequested -= OnScrollRequested;
            }

            _vm = DataContext as ServerItemViewModel;
            if (_vm is not null)
            {
                _vm.ScrollRequested += OnScrollRequested;
                OnScrollRequested(this, EventArgs.Empty);
            }
        };
    }

    /// <summary>Keeps the newest line visible unless the user scrolled up to read.</summary>
    private void OnScrollRequested(object? sender, EventArgs e)
    {
        if (Output.Items.Count == 0 || _vm?.Paused == true)
        {
            return;
        }

        var sv = FindScrollViewer(Output);
        if (sv is null || sv.VerticalOffset >= sv.ScrollableHeight - 40)
        {
            Output.ScrollIntoView(Output.Items[^1]);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv)
        {
            return sv;
        }

        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void OnCommandKey(object sender, KeyEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                _vm.SendCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                _vm.HistoryStep(-1);
                CommandBox.CaretIndex = CommandBox.Text.Length;
                e.Handled = true;
                break;
            case Key.Down:
                _vm.HistoryStep(1);
                CommandBox.CaretIndex = CommandBox.Text.Length;
                e.Handled = true;
                break;
            case Key.Tab when _vm.Suggestions.Count > 0:
                _vm.AcceptSuggestionCommand.Execute(_vm.Suggestions[0]);
                CommandBox.CaretIndex = CommandBox.Text.Length;
                e.Handled = true;
                break;
            case Key.Escape:
                _vm.Suggestions.Clear();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Player name click: menu with common moderation commands.</summary>
    private void OnPlayerClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Content: string name } button || _vm is null)
        {
            return;
        }

        var menu = new ContextMenu();
        foreach (var (key, verb) in new[] { ("Player_Op", "op"), ("Player_Kick", "kick"), ("Player_Ban", "ban"), ("Player_Tp", "tp") })
        {
            var item = new MenuItem { Header = Loc.T(key) };
            item.Click += (_, _) => _vm.PlayerActionCommand.Execute(verb == "tp" ? $"tp:{name} 0 100 0" : $"{verb}:{name}");
            menu.Items.Add(item);
        }

        button.ContextMenu = menu;
        menu.IsOpen = true;
    }
}
