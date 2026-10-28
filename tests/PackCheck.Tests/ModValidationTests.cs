using System.IO.Compression;
using System.Text;
using PackCheck.Core;
using PackCheck.Validation;

namespace PackCheck.Tests;

public class ModValidationTests
{
    private static readonly ValidationOptions Mc = new() { MinecraftVersion = new MinecraftVersion(1, 21, 1) };

    private static void Jar(string root, string file, string entry, string content)
    {
        Directory.CreateDirectory(Path.Combine(root, "mods"));
        using var zip = ZipFile.Open(Path.Combine(root, "mods", file), ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry(entry).Open(), Encoding.UTF8);
        w.Write(content);
    }

    private static string Fabric(string id, string mc = ">=1.21", string depends = "", string env = "*") =>
        "{\"schemaVersion\":1,\"id\":\"" + id + "\",\"version\":\"1.0\",\"name\":\"" + id + "\",\"environment\":\"" + env
        + "\",\"depends\":{\"minecraft\":\"" + mc + "\",\"fabricloader\":\">=0.15\"" + depends + "}}";

    private static Task<ValidationReport> Run(string root) => new PackValidator().ValidateAsync(root, Mc);

    [Fact]
    public async Task ValidFabricPack_Passes()
    {
        using var p = new PackBuilder();
        Jar(p.Root, "a.jar", "fabric.mod.json", Fabric("lithium"));
        Jar(p.Root, "b.jar", "fabric.mod.json", Fabric("sodium-extra", depends: ",\"lithium\":\"*\""));
        var r = await Run(p.Root);
        Assert.DoesNotContain(r.Checks, c => c.Status == CheckStatus.Fail);
    }

    [Fact]
    public async Task MissingModDependency_Fails()
    {
        using var p = new PackBuilder();
        Jar(p.Root, "a.jar", "fabric.mod.json", Fabric("mymod", depends: ",\"fabric-api\":\"*\""));
        var r = await Run(p.Root);
        Assert.Contains(r.Checks, c => c.Id == "PC064" && c.Status == CheckStatus.Fail && c.Description.Contains("fabric-api", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WrongMinecraftRange_Fails()
    {
        using var p = new PackBuilder();
        Jar(p.Root, "old.jar", "fabric.mod.json", Fabric("oldmod", mc: "~1.20.1"));
        var r = await Run(p.Root);
        Assert.Contains(r.Checks, c => c.Id == "PC065" && c.Status == CheckStatus.Fail);
    }

    [Fact]
    public async Task ClientOnlyMod_Fails()
    {
        using var p = new PackBuilder();
        Jar(p.Root, "c.jar", "fabric.mod.json", Fabric("minimap", env: "client"));
        var r = await Run(p.Root);
        Assert.Contains(r.Checks, c => c.Id == "PC066" && c.Status == CheckStatus.Fail);
    }

    [Fact]
    public async Task DuplicateMods_Fail()
    {
        using var p = new PackBuilder();
        Jar(p.Root, "a1.jar", "fabric.mod.json", Fabric("lithium"));
        Jar(p.Root, "a2.jar", "fabric.mod.json", Fabric("lithium"));
        var r = await Run(p.Root);
        Assert.Contains(r.Checks, c => c.Id == "PC063" && c.Status == CheckStatus.Fail);
    }

    [Fact]
    public async Task NeoForgeToml_IsRead()
    {
        using var p = new PackBuilder();
        Jar(p.Root, "n.jar", "META-INF/neoforge.mods.toml", """
            modLoader="javafml"
            loaderVersion="[4,)"
            [[mods]]
            modId="examplemod"
            version="2.0"
            displayName="Example"
            [[dependencies.examplemod]]
            modId="minecraft"
            type="required"
            versionRange="[1.21,1.21.2)"
            [[dependencies.examplemod]]
            modId="curios"
            type="required"
            versionRange="[9,)"
            """);
        var r = await Run(p.Root);
        Assert.Contains(r.Checks, c => c.Id == "PC064" && c.Description.Contains("curios", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Checks, c => c.Id == "PC065" && c.Status == CheckStatus.Fail);
    }

    [Fact]
    public async Task MixedLoaders_Fail()
    {
        using var p = new PackBuilder();
        Jar(p.Root, "f.jar", "fabric.mod.json", Fabric("fabricmod"));
        Jar(p.Root, "n.jar", "META-INF/mods.toml", "[[mods]]\nmodId=\"forgemod\"\nversion=\"1\"\n");
        var r = await Run(p.Root);
        Assert.Contains(r.Checks, c => c.Id == "PC062" && c.Status == CheckStatus.Fail);
    }

    [Theory]
    [InlineData(">=1.21 <1.22", "1.21.1", true)]
    [InlineData("~1.20.1", "1.21.1", false)]
    [InlineData("1.21.x", "1.21.4", true)]
    [InlineData("[1.21,1.21.2)", "1.21.1", true)]
    [InlineData("[1.21,1.21.2)", "1.21.2", false)]
    [InlineData("1.20.1 || 1.21.1", "1.21.1", true)]
    [InlineData("*", "1.21.1", true)]
    public void VersionRanges(string range, string version, bool expected)
    {
        Assert.True(MinecraftVersion.TryParse(version, out var v));
        Assert.Equal(expected, VersionRange.Contains(range, v));
    }

    [Fact]
    public void UnknownRangeSyntax_IsNotGuessed()
    {
        Assert.True(MinecraftVersion.TryParse("1.21.1", out var v));
        Assert.Null(VersionRange.Contains("latest-snapshot", v));
    }
}
