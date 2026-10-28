using System.Windows.Media;
using System.Xml.Linq;

namespace PackCheck.Tests;

/// <summary>Every character used in the UI strings (all five languages) must exist in the bundled fonts, or it renders as a fallback box.</summary>
public class FontCoverageTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "PackCheck.slnx")) && !File.Exists(Path.Combine(dir, "PackCheck.sln")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new DirectoryNotFoundException("repository root");
        }

        return dir;
    }

    public static TheoryData<string> Fonts() => new(Directory.GetFiles(Path.Combine(Root, "src", "PackCheck.App", "Assets", "Fonts"), "*.ttf").Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(Fonts))]
    public void BundledFont_CoversEveryUiCharacter(string fontFile)
    {
        var glyphs = new GlyphTypeface(new Uri(Path.Combine(Root, "src", "PackCheck.App", "Assets", "Fonts", fontFile)));
        var used = Directory.GetFiles(Path.Combine(Root, "src", "PackCheck.App", "Localization"), "Strings*.resx")
            .SelectMany(f => XDocument.Load(f).Descendants("value").Select(v => v.Value))
            .SelectMany(s => s.EnumerateRunes())
            .Where(r => r.Value >= 0x20 && !IsSymbolFallback(r.Value))
            .Select(r => r.Value)
            .ToHashSet();

        var missing = used.Where(c => !glyphs.CharacterToGlyphMap.ContainsKey(c)).Select(c => $"U+{c:X4} '{char.ConvertFromUtf32(c)}'").ToList();
        Assert.True(missing.Count == 0, $"{fontFile} lacks: {string.Join(", ", missing)}");
    }

    /// <summary>Arrows, check marks and similar symbols are drawn by Windows' Segoe UI Symbol fallback on purpose.</summary>
    private static bool IsSymbolFallback(int c) => c is >= 0x2190 and <= 0x2BFF or >= 0x1F000;
}
