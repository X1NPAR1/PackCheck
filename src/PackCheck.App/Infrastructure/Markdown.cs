using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using RichTextBox = System.Windows.Controls.RichTextBox;
using List = System.Windows.Documents.List;
using System.Windows.Documents;
using System.Windows.Media;

namespace PackCheck.App.Infrastructure;

/// <summary>
/// Small Markdown renderer for assistant answers: headings, paragraphs, bullet and numbered lists,
/// fenced code blocks, inline code, bold, italic and links. Unknown syntax stays as plain text.
/// </summary>
public static partial class Markdown
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(string), typeof(Markdown), new PropertyMetadata(null, (d, e) => Render((RichTextBox)d, e.NewValue as string ?? string.Empty)));

    public static string? GetSource(DependencyObject d) => (string?)d.GetValue(SourceProperty);

    public static void SetSource(DependencyObject d, string? v) => d.SetValue(SourceProperty, v);

    private static void Render(RichTextBox box, string text)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0), FontFamily = box.FontFamily, FontSize = box.FontSize };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "TextBrush");
        foreach (var block in Parse(text))
        {
            doc.Blocks.Add(block);
        }

        box.Document = doc;
    }

    public static IEnumerable<Block> Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        List? list = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (list is not null)
                {
                    yield return list;
                    list = null;
                }

                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal); i++)
                {
                    code.Add(lines[i]);
                }

                var p = new Paragraph(new Run(string.Join("\n", code))) { Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 4, 0, 8), FontSize = 12 };
                p.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
                p.SetResourceReference(TextElement.BackgroundProperty, "ConsoleBgBrush");
                p.SetResourceReference(TextElement.ForegroundProperty, "ConsoleTextBrush");
                yield return p;
                continue;
            }

            var bullet = BulletLine().Match(line);
            if (bullet.Success)
            {
                var numbered = char.IsDigit(bullet.Groups["mark"].Value[0]);
                if (list is null || (list.MarkerStyle == TextMarkerStyle.Decimal) != numbered)
                {
                    if (list is not null)
                    {
                        yield return list;
                    }

                    list = new List { MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(18, 0, 0, 0) };
                }

                var item = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
                Inline(item.Inlines, bullet.Groups["text"].Value);
                list.ListItems.Add(new ListItem(item));
                continue;
            }

            if (list is not null)
            {
                yield return list;
                list = null;
            }

            if (line.Trim().Length == 0)
            {
                continue;
            }

            var heading = Heading().Match(line);
            if (heading.Success)
            {
                var h = new Paragraph { FontWeight = FontWeights.SemiBold, FontSize = heading.Groups[1].Value.Length <= 2 ? 15 : 14, Margin = new Thickness(0, 6, 0, 4) };
                Inline(h.Inlines, heading.Groups[2].Value);
                yield return h;
                continue;
            }

            // Consecutive plain lines form one paragraph.
            var para = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            Inline(para.Inlines, line);
            while (i + 1 < lines.Length && lines[i + 1].Trim().Length > 0 && !IsSpecial(lines[i + 1]))
            {
                para.Inlines.Add(new LineBreak());
                Inline(para.Inlines, lines[++i]);
            }

            yield return para;
        }

        if (list is not null)
        {
            yield return list;
        }
    }

    private static bool IsSpecial(string line) =>
        line.TrimStart().StartsWith("```", StringComparison.Ordinal) || BulletLine().IsMatch(line) || Heading().IsMatch(line);

    private static void Inline(InlineCollection target, string text)
    {
        var pos = 0;
        foreach (Match m in InlineToken().Matches(text))
        {
            if (m.Index > pos)
            {
                target.Add(new Run(text[pos..m.Index]));
            }

            if (m.Groups["code"].Success)
            {
                var r = new Run(m.Groups["code"].Value) { FontSize = 12 };
                r.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
                r.SetResourceReference(TextElement.BackgroundProperty, "Surface2Brush");
                target.Add(r);
            }
            else if (m.Groups["bold"].Success)
            {
                target.Add(new Bold(new Run(m.Groups["bold"].Value)));
            }
            else if (m.Groups["italic"].Success)
            {
                target.Add(new Italic(new Run(m.Groups["italic"].Value)));
            }
            else if (m.Groups["label"].Success && Uri.TryCreate(m.Groups["url"].Value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                var link = new Hyperlink(new Run(m.Groups["label"].Value)) { NavigateUri = uri };
                link.SetResourceReference(TextElement.ForegroundProperty, "PrimaryBrush");
                link.RequestNavigate += (_, e) => ViewModels.Shell32.Open(e.Uri.ToString());
                target.Add(link);
            }
            else
            {
                target.Add(new Run(m.Value));
            }

            pos = m.Index + m.Length;
        }

        if (pos < text.Length)
        {
            target.Add(new Run(text[pos..]));
        }
    }

    [GeneratedRegex(@"^\s*(?<mark>[-*+]|\d+[.)])\s+(?<text>.*)$")]
    private static partial Regex BulletLine();

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"`(?<code>[^`]+)`|\*\*(?<bold>[^*]+)\*\*|(?<![*\w])\*(?<italic>[^*\s][^*]*)\*(?![*\w])|\[(?<label>[^\]]+)\]\((?<url>[^)\s]+)\)")]
    private static partial Regex InlineToken();
}
