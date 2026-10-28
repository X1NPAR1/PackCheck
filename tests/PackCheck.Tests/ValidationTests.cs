using System.IO.Compression;
using PackCheck.Core;
using PackCheck.Reporting;
using PackCheck.Validation;

namespace PackCheck.Tests;

public class ValidationTests
{
    private static readonly ValidationOptions Mc1204 = new() { MinecraftVersion = new MinecraftVersion(1, 20, 4), JavaVersion = 21 };

    private static Task<ValidationReport> Run(string path, ValidationOptions? o = null) => new PackValidator().ValidateAsync(path, o ?? Mc1204);

    [Fact]
    public async Task ValidPack_Passes()
    {
        using var pack = new PackBuilder().Simple("a.jar", "Alpha").Simple("b.jar", "Beta", depend: "Alpha").Text("server.properties", "server-port=25565\nonline-mode=true\n").Text("paper.jar", "x");
        var r = await Run(pack.Root);
        Assert.Equal(OverallStatus.Pass, r.Overall);
        Assert.Equal(2, r.Plugins.Count);
    }

    [Fact]
    public async Task MissingDependency_Fails_WithEvidenceAndCertainFix()
    {
        using var pack = new PackBuilder().Simple("b.jar", "Beta", depend: "Vault").Text("server.properties", "server-port=25565\n");
        var r = await Run(pack.Root);
        var c = Assert.Single(r.Checks, c => c.Id == "PC040" && c.Status == CheckStatus.Fail);
        Assert.Contains("Vault", c.Description, StringComparison.Ordinal);
        Assert.True(c.Fix!.IsCertain);
        Assert.Equal(OverallStatus.Fail, r.Overall);
    }

    [Fact]
    public async Task PaperPluginYml_RequiredDependency_IsDetected()
    {
        using var pack = new PackBuilder().Plugin("p.jar", "name: P\nversion: 1\nmain: x\ndependencies:\n  server:\n    Need:\n      required: true\n    Maybe:\n      required: false\n", "paper-plugin.yml");
        var r = await Run(pack.Root);
        Assert.Contains(r.Checks, c => c.Id == "PC040" && c.Description.Contains("'Need'", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Checks, c => c.Id == "PC040" && c.Description.Contains("'Maybe'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CorruptJar_Fails()
    {
        using var pack = new PackBuilder().Raw("plugins/broken.jar", [1, 2, 3, 4, 5]);
        var r = await Run(pack.Root);
        Assert.Contains(r.Checks, c => c.Id == "PC020" && c.Status == CheckStatus.Fail && c.AffectedFile == "plugins/broken.jar");
    }

    [Fact]
    public async Task DuplicatePlugins_Fail()
    {
        using var pack = new PackBuilder().Simple("a1.jar", "Alpha", "1.0").Simple("a2.jar", "Alpha", "2.0");
        var r = await Run(pack.Root);
        Assert.Contains(r.Checks, c => c.Id == "PC030" && c.Status == CheckStatus.Fail);
    }

    [Fact]
    public async Task UnsupportedApiVersion_Fails()
    {
        using var pack = new PackBuilder().Simple("n.jar", "New", apiVersion: "1.21");
        var r = await Run(pack.Root);
        Assert.Contains(r.Checks, c => c.Id == "PC050" && c.Status == CheckStatus.Fail);
    }

    [Fact]
    public async Task OldJava_Fails()
    {
        using var pack = new PackBuilder();
        var r = await Run(pack.Root, Mc1204 with { JavaVersion = 16 });
        Assert.Contains(r.Checks, c => c.Id == "PC051" && c.Status == CheckStatus.Fail);
    }

    [Fact]
    public async Task UnknownVersion_IsSkip_NotFail()
    {
        using var pack = new PackBuilder().Simple("a.jar", "A");
        var r = await Run(pack.Root, new ValidationOptions());
        var c = Assert.Single(r.Checks, c => c.Id == "PC050");
        Assert.Equal(CheckStatus.Skip, c.Status);
        Assert.NotNull(c.NotRunReason);
    }

    [Fact]
    public async Task MissingMetadata_Warns_AndNeverGuessesFromFileName()
    {
        using var pack = new PackBuilder().Plugin("EssentialsX-2.20.jar", "not: a plugin", "readme.yml");
        var r = await Run(pack.Root);
        Assert.Contains(r.Checks, c => c.Id == "PC021" && c.Status == CheckStatus.Warn);
        Assert.Null(r.Plugins[0].Name);
    }

    [Fact]
    public async Task InvalidServerProperties_Fail()
    {
        using var pack = new PackBuilder().Text("server.properties", "server-port=99999\nonline-mode=maybe\n");
        var r = await Run(pack.Root);
        var c = Assert.Single(r.Checks, c => c.Id == "PC010");
        Assert.Equal(CheckStatus.Fail, c.Status);
        Assert.Equal(2, c.Evidence.Count);
    }

    [Fact]
    public async Task MissingServerProperties_Warns()
    {
        using var pack = new PackBuilder();
        var r = await Run(pack.Root);
        Assert.Contains(r.Checks, c => c.Id == "PC010" && c.Status == CheckStatus.Warn);
    }

    [Fact]
    public async Task InvalidPath_IsError_NotFail()
    {
        var r = await Run(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid()));
        Assert.Equal(OverallStatus.Incomplete, r.Overall);
    }

    [Fact]
    public async Task ZipPack_IsValidated_AndTempCleaned()
    {
        using var pack = new PackBuilder().Simple("a.jar", "Alpha");
        var zip = pack.Zip();
        var before = Directory.GetDirectories(Path.GetTempPath(), "packcheck-*").Length;
        var r = await Run(zip);
        Assert.Single(r.Plugins);
        Assert.Equal(before, Directory.GetDirectories(Path.GetTempPath(), "packcheck-*").Length);
    }

    [Fact]
    public async Task PathTraversalZip_IsRejected()
    {
        var zip = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(z.CreateEntry("../../evil.txt").Open());
            w.Write("x");
        }

        try
        {
            var r = await Run(zip);
            Assert.Contains(r.Checks, c => c.Id == "PC000" && c.Status == CheckStatus.Fail);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public void ZipBomb_IsRejected()
    {
        var zip = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var s = z.CreateEntry("big.bin", CompressionLevel.SmallestSize).Open();
            var block = new byte[1024 * 1024];
            for (var i = 0; i < 64; i++)
            {
                s.Write(block);
            }
        }

        try
        {
            Assert.Throws<UnsafeArchiveException>(() => SafeZip.Extract(zip, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), maxTotalBytes: 16 * 1024 * 1024));
            Assert.Throws<UnsafeArchiveException>(() => SafeZip.Extract(zip, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())));
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task OversizedFile_IsSkippedWithReason()
    {
        using var pack = new PackBuilder().Simple("a.jar", "A");
        var r = await Run(pack.Root, Mc1204 with { MaxFileBytes = 10 });
        Assert.Contains(r.Checks, c => c.Id == "PC022" && c.NotRunReason == "Size limit");
    }

    [Fact]
    public async Task YamlTags_AreNotInstantiated()
    {
        using var pack = new PackBuilder().Plugin("t.jar", "name: !!python/object:os.system x\nversion: 1\n");
        var r = await Run(pack.Root);
        Assert.Single(r.Plugins);
    }

    [Fact]
    public async Task CrashingAnalyzer_BecomesError()
    {
        using var pack = new PackBuilder();
        var r = await new PackValidator([new Boom()]).ValidateAsync(pack.Root);
        Assert.Equal(CheckStatus.Error, Assert.Single(r.Checks).Status);
        Assert.Equal(OverallStatus.Incomplete, r.Overall);
    }

    [Fact]
    public void NoChecks_IsIncomplete_NeverPass() => Assert.Equal(OverallStatus.Incomplete, ValidationReport.Aggregate([]));

    [Fact]
    public async Task EmptyAnalyzerList_FallsBackToDefaults()
    {
        using var pack = new PackBuilder().Simple("a.jar", "A");
        var r = await new PackValidator([]).ValidateAsync(pack.Root);
        Assert.NotEmpty(r.Checks);
    }

    private sealed class Boom : IAnalyzer
    {
        public string Id => "X";

        public IEnumerable<CheckResult> Analyze(PackContext context) => throw new InvalidOperationException("boom");
    }

    [Theory]
    [InlineData(OverallStatus.Pass, false, 0)]
    [InlineData(OverallStatus.PassWithWarnings, false, 0)]
    [InlineData(OverallStatus.PassWithWarnings, true, 1)]
    [InlineData(OverallStatus.Fail, false, 1)]
    [InlineData(OverallStatus.Incomplete, false, 2)]
    public void ExitCodes_AreStable(OverallStatus s, bool strict, int code) => Assert.Equal(code, ExitCodes.For(s, strict));

    [Theory]
    [InlineData("1.20.4", "1.20.10", -1)]
    [InlineData("1.21", "1.20.6", 1)]
    [InlineData("1.20", "1.20.0", 0)]
    public void MinecraftVersion_Compares(string a, string b, int sign)
    {
        Assert.True(MinecraftVersion.TryParse(a, out var va));
        Assert.True(MinecraftVersion.TryParse(b, out var vb));
        Assert.Equal(sign, Math.Sign(va.CompareTo(vb)));
    }

    [Theory]
    [InlineData("1.16.5", 8)]
    [InlineData("1.17.1", 16)]
    [InlineData("1.20.4", 17)]
    [InlineData("1.20.5", 21)]
    public void RequiredJava(string v, int java)
    {
        Assert.True(MinecraftVersion.TryParse(v, out var mv));
        Assert.Equal(java, mv.RequiredJava());
    }

    [Fact]
    public void ServerProperties_UpdateKeepsCommentsAndOrder()
    {
        var updated = ServerProperties.Update("#c\nmotd=a\nserver-port=1\n", new Dictionary<string, string> { ["server-port"] = "2", ["pvp"] = "false" });
        Assert.Equal("#c\nmotd=a\nserver-port=2\npvp=false\n", updated);
    }

    [Fact]
    public async Task Diff_ReportsAddedPluginAndNewBreakage()
    {
        using var pack = new PackBuilder().Simple("a.jar", "Alpha");
        var before = await Run(pack.Root);
        pack.Simple("b.jar", "Beta", depend: "Vault");
        var after = await Run(pack.Root);
        var diff = ReportDiff.Compare(before, after);
        Assert.Contains(diff, d => d.Kind == "added" && d.Text == "b.jar");
        Assert.Contains(diff, d => d.Kind == "broke" && d.Text.Contains("Vault", StringComparison.Ordinal));
    }
}
