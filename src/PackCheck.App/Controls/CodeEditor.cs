using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Rendering;
using PackCheck.App.Infrastructure;

namespace PackCheck.App.Controls;

/// <summary>AvalonEdit with line numbers, theme-aware colors and highlighting for server config formats.</summary>
public sealed class CodeEditor : TextEditor
{
    public static readonly DependencyProperty BoundDocumentProperty = DependencyProperty.Register(
        nameof(BoundDocument), typeof(TextDocument), typeof(CodeEditor), new PropertyMetadata(null, (d, e) => ((CodeEditor)d).OnDocument()));

    public static readonly DependencyProperty FileExtensionProperty = DependencyProperty.Register(
        nameof(FileExtension), typeof(string), typeof(CodeEditor), new PropertyMetadata(string.Empty, (d, _) => ((CodeEditor)d).ApplyHighlighting()));

    public static readonly DependencyProperty ErrorLineProperty = DependencyProperty.Register(
        nameof(ErrorLine), typeof(int), typeof(CodeEditor), new PropertyMetadata(0, (d, _) => ((CodeEditor)d).TextArea.TextView.Redraw()));

    private static readonly Dictionary<string, string> Definitions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".yml"] = "YAML",
        [".yaml"] = "YAML",
        [".toml"] = "TOML",
        [".properties"] = "Properties",
        [".cfg"] = "Properties",
        [".ini"] = "Properties",
        [".conf"] = "Properties",
        [".json"] = "JSON",
        [".json5"] = "JSON",
        [".mcmeta"] = "JSON",
        [".log"] = "Log",
        [".txt"] = "Properties",
        [".xml"] = "XML",
        [".js"] = "JavaScript",
        [".bat"] = "Batch",
        [".cmd"] = "Batch",
        [".sh"] = "Batch",
    };

    private static readonly Dictionary<string, IHighlightingDefinition> Cache = [];

    public CodeEditor()
    {
        ShowLineNumbers = true;
        FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("MonoFont");
        FontSize = 13;
        WordWrap = false;
        HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
        Options.ConvertTabsToSpaces = true;
        Options.IndentationSize = 2;
        Options.HighlightCurrentLine = true;
        Options.EnableHyperlinks = false;
        TextArea.TextView.BackgroundRenderers.Add(new ErrorLineRenderer(this));
        ApplyTheme();
        ThemeManager.ThemeChanged += (_, _) =>
        {
            ApplyTheme();
            ApplyHighlighting();
        };
    }

    public TextDocument? BoundDocument
    {
        get => (TextDocument?)GetValue(BoundDocumentProperty);
        set => SetValue(BoundDocumentProperty, value);
    }

    public string FileExtension
    {
        get => (string)GetValue(FileExtensionProperty);
        set => SetValue(FileExtensionProperty, value);
    }

    public int ErrorLine
    {
        get => (int)GetValue(ErrorLineProperty);
        set => SetValue(ErrorLineProperty, value);
    }

    private void OnDocument()
    {
        Document = BoundDocument ?? new TextDocument();
        ApplyHighlighting();
    }

    private void ApplyTheme()
    {
        var dark = ThemeManager.IsDark;
        Background = new SolidColorBrush(dark ? Color.FromRgb(0x16, 0x16, 0x16) : Color.FromRgb(0xFC, 0xFC, 0xFA));
        Foreground = new SolidColorBrush(dark ? Color.FromRgb(0xD8, 0xD8, 0xD2) : Color.FromRgb(0x22, 0x24, 0x22));
        LineNumbersForeground = new SolidColorBrush(dark ? Color.FromRgb(0x5F, 0x5F, 0x5A) : Color.FromRgb(0xA0, 0xA0, 0x98));
        TextArea.TextView.CurrentLineBackground = new SolidColorBrush(dark ? Color.FromArgb(0x30, 0x40, 0x40, 0x40) : Color.FromArgb(0x30, 0xD0, 0xD0, 0xC8));
        TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        TextArea.SelectionBrush = new SolidColorBrush(dark ? Color.FromArgb(0x90, 0x3F, 0x6B, 0x2A) : Color.FromArgb(0x70, 0x9A, 0xCD, 0x78));
        TextArea.SelectionForeground = null;
        TextArea.Caret.CaretBrush = Foreground;
    }

    private void ApplyHighlighting()
    {
        if (!Definitions.TryGetValue(FileExtension ?? string.Empty, out var name))
        {
            SyntaxHighlighting = null;
            return;
        }

        var key = name + (ThemeManager.IsDark ? "-dark" : "-light");
        if (!Cache.TryGetValue(key, out var def))
        {
            def = name is "XML" or "JavaScript" ? HighlightingManager.Instance.GetDefinition(name) : Build(name, ThemeManager.IsDark);
            Cache[key] = def;
        }

        SyntaxHighlighting = def;
    }

    /// <summary>Highlighting rules written for this app (YAML, TOML, properties, JSON, logs, scripts).</summary>
    private static IHighlightingDefinition Build(string name, bool dark)
    {
        string C(string darkHex, string lightHex) => dark ? darkHex : lightHex;
        var key = C("#8FCB6B", "#3F7A2A");
        var str = C("#E1C27A", "#8A6A0E");
        var num = C("#B7A3E0", "#6A4FB0");
        var comment = C("#6E6E68", "#8A8A82");
        var keyword = C("#E08A5A", "#B4552A");
        var section = C("#7FB3AD", "#2F6F68");
        var warn = C("#E6C24D", "#8A6A0E");
        var error = C("#F06B61", "#B0342C");

        var rules = name switch
        {
            "YAML" => $$"""
                <Span color="Comment" begin="\#" />
                <Span color="String" begin="&quot;" end="&quot;"><RuleSet><Span begin="\\" end="." /></RuleSet></Span>
                <Span color="String" begin="'" end="'" />
                <Rule color="Key">^[ \t]*-?[ \t]*[A-Za-z0-9_\-\.\$\[\]]+(?=[ \t]*:)</Rule>
                <Rule color="Keyword">\b(true|false|yes|no|on|off|null|~)\b</Rule>
                <Rule color="Number">\b-?[0-9]+(\.[0-9]+)?\b</Rule>
                <Rule color="Section">&amp;[0-9a-fk-or]</Rule>
                """,
            "TOML" => $$"""
                <Span color="Comment" begin="\#" />
                <Span color="String" begin="&quot;" end="&quot;"><RuleSet><Span begin="\\" end="." /></RuleSet></Span>
                <Span color="String" begin="'" end="'" />
                <Rule color="Section">^[ \t]*\[\[?[^\]]+\]\]?</Rule>
                <Rule color="Key">^[ \t]*[A-Za-z0-9_\-\."]+(?=[ \t]*=)</Rule>
                <Rule color="Keyword">\b(true|false)\b</Rule>
                <Rule color="Number">\b-?[0-9]+(\.[0-9]+)?\b</Rule>
                """,
            "Properties" => $$"""
                <Span color="Comment" begin="^[ \t]*[\#!;]" />
                <Rule color="Section">^[ \t]*\[[^\]]+\]</Rule>
                <Rule color="Key">^[ \t]*[^=:\#!\s]+(?=[ \t]*[=:])</Rule>
                <Rule color="Keyword">\b(true|false)\b</Rule>
                <Rule color="Number">\b-?[0-9]+(\.[0-9]+)?\b</Rule>
                """,
            "JSON" => $$"""
                <Span color="Comment" begin="//" />
                <Rule color="Key">"(\\.|[^"\\])*"(?=\s*:)</Rule>
                <Span color="String" begin="&quot;" end="&quot;"><RuleSet><Span begin="\\" end="." /></RuleSet></Span>
                <Rule color="Keyword">\b(true|false|null)\b</Rule>
                <Rule color="Number">-?\b[0-9]+(\.[0-9]+)?([eE][+-]?[0-9]+)?\b</Rule>
                """,
            "Log" => $$"""
                <Rule color="Comment">^\[[0-9:\. ]+\]</Rule>
                <Rule color="Error">.*(ERROR|SEVERE|FATAL|Exception).*</Rule>
                <Rule color="Warn">.*(WARN|WARNING).*</Rule>
                <Rule color="Section">\[[A-Za-z0-9 _\-/]+(/INFO)?\]</Rule>
                """,
            _ => $$"""
                <Span color="Comment" begin="^[ \t]*(rem |::|\#)" />
                <Span color="String" begin="&quot;" end="&quot;" />
                <Rule color="Keyword">\b(echo|set|if|else|goto|call|exit|java|cd|for|in|do)\b</Rule>
                <Rule color="Section">%[A-Za-z0-9_]+%|\$[A-Za-z0-9_]+</Rule>
                """,
        };

        var xml = $"""
            <SyntaxDefinition name="{name}" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Comment" foreground="{comment}" fontStyle="italic" />
              <Color name="String" foreground="{str}" />
              <Color name="Key" foreground="{key}" />
              <Color name="Keyword" foreground="{keyword}" />
              <Color name="Number" foreground="{num}" />
              <Color name="Section" foreground="{section}" fontWeight="bold" />
              <Color name="Warn" foreground="{warn}" />
              <Color name="Error" foreground="{error}" />
              <RuleSet ignoreCase="true">
              {rules}
              </RuleSet>
            </SyntaxDefinition>
            """;
        using var reader = XmlReader.Create(new StringReader(xml));
        return HighlightingLoader.Load(HighlightingLoader.LoadXshd(reader), HighlightingManager.Instance);
    }

    /// <summary>Paints the line reported by the syntax check.</summary>
    private sealed class ErrorLineRenderer(CodeEditor editor) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var line = editor.ErrorLine;
            if (line <= 0 || editor.Document is null || line > editor.Document.LineCount)
            {
                return;
            }

            var docLine = editor.Document.GetLineByNumber(line);
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, docLine))
            {
                drawingContext.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x40, 0xE5, 0x52, 0x4A)), null, new Rect(rect.Location, new Size(textView.ActualWidth, rect.Height)));
            }
        }
    }
}
