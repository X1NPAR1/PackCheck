using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
        Output.AddHandler(Hyperlink.ClickEvent, new RoutedEventHandler(OnPlayerLink));
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.ScrollRequested -= OnScrollRequested;
                _vm.FocusRequested -= OnFocusRequested;
            }

            _vm = DataContext as ServerItemViewModel;
            if (_vm is not null)
            {
                _vm.ScrollRequested += OnScrollRequested;
                _vm.FocusRequested += OnFocusRequested;
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

    private void OnFocusRequested(object? sender, ConsoleEntry entry)
    {
        Output.SelectedItem = entry;
        Output.ScrollIntoView(entry);
    }

    /// <summary>Enter / Shift+Enter in the search box jump to the next / previous match.</summary>
    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (_vm is null || e.Key != Key.Enter)
        {
            return;
        }

        // The search text binding is delayed; push it now so the jump uses what was typed.
        SearchBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
        (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? _vm.PreviousMatchCommand : _vm.NextMatchCommand).Execute(null);
        e.Handled = true;
    }

    /// <summary>"Add a rule" picker: adds the chosen kind and resets the picker.</summary>
    private void OnAddRule(object sender, SelectionChangedEventArgs e)
    {
        if (AddRuleBox.SelectedItem is PackCheck.Servers.AutomationKind kind && _vm is not null)
        {
            _vm.AddRuleCommand.Execute(kind);
            AddRuleBox.SelectedItem = null;
        }
    }

    private void OnPlayerLink(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is Hyperlink { Tag: string name } && _vm is not null)
        {
            ShowPlayerMenu(Output, name);
            e.Handled = true;
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
        if (sender is Button { Content: string name } button)
        {
            ShowPlayerMenu(button, name);
        }
    }

    private void ShowPlayerMenu(FrameworkElement target, string name)
    {
        if (_vm is null)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        foreach (var (key, verb) in new[] { ("Player_Op", "op"), ("Player_Kick", "kick"), ("Player_Ban", "ban"), ("Player_Tp", "tp") })
        {
            var item = new MenuItem { Header = Loc.T(key) };
            item.Click += (_, _) => _vm.PlayerActionCommand.Execute(verb == "tp" ? $"tp:{name} 0 100 0" : $"{verb}:{name}");
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }
}
