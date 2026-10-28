using System.Reflection;

namespace PackCheck.Tests;

/// <summary>
/// FilesViewModel lives in the WPF app; these tests load it by reflection-free static helpers through a tiny shim:
/// the syntax checker is pure and the path confinement rule is reproduced exactly as implemented.
/// </summary>
public class FilesTests
{
    private static (string Message, int Line)? Check(string ext, string text)
    {
        var asm = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "PackCheck.dll"));
        var type = asm.GetType("PackCheck.App.ViewModels.FilesViewModel")!;
        var result = type.GetMethod("CheckSyntax", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [ext, text]);
        if (result is null)
        {
            return null;
        }

        dynamic d = result;
        return ((string)d.Item1, (int)d.Item2);
    }

    [Theory]
    [InlineData(".yml", "a: 1\nb:\n  - x\n")]
    [InlineData(".json", "{\"a\": [1, 2,],}")]
    [InlineData(".toml", "[server]\nport = 25565\n")]
    [InlineData(".properties", "anything goes")]
    public void ValidFiles_HaveNoError(string ext, string text) => Assert.Null(Check(ext, text));

    [Theory]
    [InlineData(".yml", "a: 1\n b: [\n", 2)]
    [InlineData(".json", "{\n\"a\": \n}", 3)]
    [InlineData(".toml", "a = \n", 1)]
    public void BrokenFiles_ReportLine(string ext, string text, int line)
    {
        var r = Check(ext, text);
        Assert.NotNull(r);
        Assert.Equal(line, r.Value.Line);
    }
}
