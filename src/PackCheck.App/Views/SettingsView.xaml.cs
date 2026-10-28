using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PackCheck.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>Sidebar: scrolls the chosen section to the top.</summary>
    private void OnSection(object sender, SelectionChangedEventArgs e)
    {
        if (Sections.SelectedItem is ListBoxItem { Tag: string name } && FindName(name) is FrameworkElement card && card.IsVisible)
        {
            var y = card.TransformToAncestor(Cards).Transform(new Point(0, 0)).Y;
            Scroller.ScrollToVerticalOffset(y);
        }
    }

    /// <summary>Hides sections whose text does not contain the query (labels, hints and option names are all searched).</summary>
    private void OnSearch(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        var any = false;
        foreach (var item in Sections.Items.OfType<ListBoxItem>())
        {
            if (FindName((string)item.Tag) is not FrameworkElement card)
            {
                continue;
            }

            var match = q.Length == 0 || Texts(card).Any(t => t.Contains(q, StringComparison.CurrentCultureIgnoreCase));
            card.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            item.Visibility = card.Visibility;
            any |= match;
        }

        NoMatch.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        Scroller.ScrollToTop();
    }

    private static IEnumerable<string> Texts(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            switch (child)
            {
                case TextBlock tb:
                    yield return tb.Text;
                    break;
                case ContentControl { Content: string s }:
                    yield return s;
                    break;
            }

            foreach (var t in Texts(child))
            {
                yield return t;
            }
        }
    }
}
