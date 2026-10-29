using PackCheck.Servers;
using Xunit;

namespace PackCheck.Tests;

public class CrashAnalyzerTests
{
    private static CrashDiagnosis Run(params string[] lines) => CrashAnalyzer.Analyze(lines);

    [Fact]
    public void Eula()
    {
        var d = Run("[12:00:00] [main/INFO]: You need to agree to the EULA in order to run the server. Go to eula.txt for more info.");
        Assert.Equal(CrashKind.Eula, d.Kind);
        Assert.Equal(CrashFix.AcceptEula, d.Fix);
    }

    [Fact]
    public void Port_in_use()
    {
        var d = Run("[12:00:00 WARN]: **** FAILED TO BIND TO PORT!", "[12:00:00 WARN]: The exception was: java.net.BindException: Address already in use: bind");
        Assert.Equal(CrashKind.PortInUse, d.Kind);
        Assert.Equal(CrashFix.ChangePort, d.Fix);
    }

    [Fact]
    public void Wrong_java_from_class_version()
    {
        var d = Run("Error: LinkageError occurred while loading main class io.papermc.paperclip.Main",
            "java.lang.UnsupportedClassVersionError: io/papermc/paperclip/Main has been compiled by a more recent version of the Java Runtime (class file version 65.0), this version of the Java Runtime only recognizes class file versions up to 61.0");
        Assert.Equal(CrashKind.WrongJava, d.Kind);
        Assert.Equal(21, d.RequiredJava);
    }

    [Fact]
    public void Out_of_memory()
    {
        var d = Run("java.lang.OutOfMemoryError: Java heap space");
        Assert.Equal(CrashKind.OutOfMemory, d.Kind);
        Assert.Equal(CrashFix.IncreaseMemory, d.Fix);
    }

    [Fact]
    public void Paper_missing_plugin_dependency()
    {
        var d = Run("[12:00:01 ERROR]: Could not load 'plugins/EssentialsChat-2.20.jar' in folder 'plugins'",
            "org.bukkit.plugin.UnknownDependencyException: Unknown/missing dependency plugins: [Essentials]. Please download and install these plugins to run 'EssentialsChat'.");
        Assert.Equal(CrashKind.MissingDependency, d.Kind);
        Assert.Equal("EssentialsChat-2.20", d.Culprit);
        Assert.Equal("Essentials", d.Missing);
        Assert.Equal(2, d.Evidence.Count);
    }

    [Fact]
    public void Fabric_missing_dependency()
    {
        var d = Run("[12:00:00] [main/ERROR]: Incompatible mods found!",
            " - Mod 'Sodium Extra' (sodium-extra) 0.5.4 requires any version of sodium, which is missing!");
        Assert.Equal(CrashKind.MissingDependency, d.Kind);
        Assert.Equal("sodium-extra", d.Culprit);
        Assert.Equal("sodium", d.Missing);
    }

    [Fact]
    public void Forge_missing_dependency()
    {
        var d = Run("Missing or unsupported mandatory dependencies:", "\tMod ID: 'geckolib', Requested by: 'alexsmobs', Expected range: '[4.4,)', Actual version: '[MISSING]'");
        Assert.Equal(CrashKind.MissingDependency, d.Kind);
        Assert.Equal("alexsmobs", d.Culprit);
        Assert.Equal("geckolib", d.Missing);
    }

    [Fact]
    public void Mixin_failure_names_mod()
    {
        var d = Run("org.spongepowered.asm.mixin.transformer.throwables.MixinTransformerError: An unexpected critical error was encountered",
            "Caused by: org.spongepowered.asm.mixin.throwables.MixinApplyError: Mixin [create.mixins.json:accessor.ServerLevelAccessor] from mod create failed injection check");
        Assert.Equal(CrashKind.MixinFailure, d.Kind);
        Assert.Equal("create", d.Culprit);
    }

    [Fact]
    public void Plugin_enable_error()
    {
        var d = Run("[12:00:03 ERROR]: Error occurred while enabling WorldGuard v7.0.9 (Is it up to date?)");
        Assert.Equal(CrashKind.PluginError, d.Kind);
        Assert.Equal("WorldGuard", d.Culprit);
        Assert.Equal(CrashFix.DisableAddon, d.Fix);
    }

    [Fact]
    public void Broken_config()
    {
        var d = Run("[12:00:00 ERROR]: Could not load config/paper-global.yml, please correct your syntax errors");
        Assert.Equal(CrashKind.BrokenConfig, d.Kind);
        Assert.Equal("config/paper-global.yml", d.Culprit);
    }

    [Fact]
    public void Paper_yaml_syntax_error_names_file_and_line()
    {
        var d = Run(
            "[16:30:50] [Server thread/ERROR]: Encountered an unexpected exception",
            "org.spongepowered.configurate.loader.ParsingException: []: Unknown error occurred while loading",
            "	at io.papermc.paper.configuration.Configurations.initializeGlobalConfiguration(Configurations.java:132) ~[paper-26.3.jar:26.3-227-7f9c797]",
            "Caused by: while parsing a flow sequence",
            " in 'reader', line 148, column 8:",
            "expected ',' or ']', but got <stream end>",
            " in 'reader', line 149, column 1:");
        Assert.Equal(CrashKind.BrokenConfig, d.Kind);
        Assert.Equal("config/paper-global.yml", d.Culprit);
        Assert.Equal(148, d.Line);
        Assert.Equal(CrashFix.OpenFile, d.Fix);
    }

    [Fact]
    public void Corrupt_world()
    {
        var d = Run("[12:00:00] [Server thread/ERROR]: Failed to load level, data is corrupt");
        Assert.Equal(CrashKind.WorldCorrupt, d.Kind);
        Assert.Equal(CrashFix.RestoreBackup, d.Fix);
    }

    [Fact]
    public void Unknown_keeps_last_error_as_evidence()
    {
        var d = Run("[12:00:00 INFO]: Starting", "[12:00:01 ERROR]: Something odd", "[12:00:02 INFO]: Stopping");
        Assert.Equal(CrashKind.Unknown, d.Kind);
        Assert.Equal("[12:00:01 ERROR]: Something odd", Assert.Single(d.Evidence));
    }

    [Theory]
    [InlineData(ServerState.Stopping, 1, true, ServerState.Stopped)]
    [InlineData(ServerState.Running, 0, false, ServerState.Stopped)]
    [InlineData(ServerState.Running, 1, false, ServerState.Crashed)]
    [InlineData(ServerState.Running, 0, true, ServerState.Crashed)]
    [InlineData(ServerState.Starting, 0, false, ServerState.Crashed)]
    public void Exit_state(ServerState before, int code, bool failure, ServerState expected) =>
        Assert.Equal(expected, ServerProcess.ExitState(before, code, failure));

    [Theory]
    [InlineData("[16:28:51 ERROR]: This crash report has been saved to: D:/x/crash.txt", true)]
    [InlineData("[16:28:51 INFO]: You need to agree to the EULA in order to run the server.", true)]
    [InlineData("[16:28:51 INFO]: Done (3.2s)! For help, type \"help\"", false)]
    public void Failure_lines(string line, bool expected) => Assert.Equal(expected, ServerProcess.IsFailureLine(line));

    [Fact]
    public void Finds_culprit_jar_and_reads_latest_log()
    {
        var dir = Directory.CreateTempSubdirectory("pc-crash").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "plugins"));
            File.WriteAllText(Path.Combine(dir, "plugins", "worldguard-bukkit-7.0.9.jar"), "x");
            File.WriteAllText(Path.Combine(dir, "plugins", "WorldEdit.jar"), "x");
            Directory.CreateDirectory(Path.Combine(dir, "logs"));
            File.WriteAllText(Path.Combine(dir, "logs", "latest.log"), "[12:00:03 ERROR]: Error occurred while enabling WorldGuard v7.0.9 (Is it up to date?)\n");

            var d = CrashAnalyzer.Analyze(dir, []);
            Assert.Equal(CrashKind.PluginError, d.Kind);
            Assert.EndsWith("worldguard-bukkit-7.0.9.jar", d.CulpritFile);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
