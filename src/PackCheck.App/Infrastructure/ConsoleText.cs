using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace PackCheck.App.Infrastructure;

/// <summary>
/// Renders a console line into a TextBlock: search matches get a background, known player names become links.
/// A link click raises <see cref="Hyperlink.ClickEvent"/> with the player name in <see cref="FrameworkContentElement.Tag"/>.
/// </summary>
public static class ConsoleText
{
    public static readonly DependencyProperty TextProperty = Register("Text", typeof(string));
    public static readonly DependencyProperty SearchProperty = Register("Search", typeof(string));
    public static readonly DependencyProperty PlayersProperty = Register("Players", typeof(IReadOnlyList<string>));

    private static DependencyProperty Register(string name, Type type) =>
        DependencyProperty.RegisterAttached(name, type, typeof(ConsoleText), new PropertyMetadata(null, (d, _) => Render((TextBlock)d)));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);

    public static void SetText(DependencyObject d, string? v) => d.SetValue(TextProperty, v);

    public static string? GetSearch(DependencyObject d) => (string?)d.GetValue(SearchProperty);

    public static void SetSearch(DependencyObject d, string? v) => d.SetValue(SearchProperty, v);

    public static IReadOnlyList<string>? GetPlayers(DependencyObject d) => (IReadOnlyList<string>?)d.GetValue(PlayersProperty);

    public static void SetPlayers(DependencyObject d, IReadOnlyList<string>? v) => d.SetValue(PlayersProperty, v);

    private static void Render(TextBlock tb)
    {
        var text = GetText(tb) ?? string.Empty;
        var search = GetSearch(tb) ?? string.Empty;
        var players = GetPlayers(tb) ?? [];
        tb.Inlines.Clear();

        var names = players.Where(p => text.Contains(p, StringComparison.OrdinalIgnoreCase)).ToList();
        if (names.Count == 0 && (search.Length == 0 || !text.Contains(search, StringComparison.OrdinalIgnoreCase)))
        {
            tb.Text = text;
            return;
        }

        var pos = 0;
        if (names.Count > 0)
        {
            var pattern = @"\b(" + string.Join("|", names.OrderByDescending(n => n.Length).Select(Regex.Escape)) + @")\b";
            foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)))
            {
                AddPlain(tb, text[pos..m.Index], search);
                var link = new Hyperlink(new Run(m.Value)) { Tag = m.Value, TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand };
                link.SetResourceReference(TextElement.ForegroundProperty, "ConsoleLinkBrush");
                tb.Inlines.Add(link);
                pos = m.Index + m.Length;
            }
        }

        AddPlain(tb, text[pos..], search);
    }

    private static void AddPlain(TextBlock tb, string part, string search)
    {
        if (part.Length == 0)
        {
            return;
        }

        var pos = 0;
        while (search.Length > 0)
        {
            var i = part.IndexOf(search, pos, StringComparison.OrdinalIgnoreCase);
            if (i < 0)
            {
                break;
            }

            if (i > pos)
            {
                tb.Inlines.Add(new Run(part[pos..i]));
            }

            var hit = new Run(part.Substring(i, search.Length));
            hit.SetResourceReference(TextElement.BackgroundProperty, "ConsoleMatchBrush");
            tb.Inlines.Add(hit);
            pos = i + search.Length;
        }

        if (pos < part.Length)
        {
            tb.Inlines.Add(new Run(part[pos..]));
        }
    }
}
