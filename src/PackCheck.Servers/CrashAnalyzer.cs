using System.Text.RegularExpressions;

namespace PackCheck.Servers;

public enum CrashKind
{
    Eula,
    PortInUse,
    OutOfMemory,
    WrongJava,
    MissingDependency,
    IncompatibleMods,
    MixinFailure,
    PluginError,
    BrokenConfig,
    WorldCorrupt,
    Unknown,
}

public enum CrashFix
{
    None,
    AcceptEula,
    ChangePort,
    IncreaseMemory,
    ChangeJava,
    DisableAddon,
    OpenFile,
    RestoreBackup,
}

/// <summary>What went wrong, who caused it, the log lines that prove it and the one-click fix.</summary>
/// <param name="Culprit">Plugin/mod name, missing dependency, config file or Java version, depending on <see cref="Kind"/>.</param>
/// <param name="CulpritFile">Path of the jar or file to disable/open, when it could be found.</param>
public sealed record CrashDiagnosis(CrashKind Kind, string? Culprit, string? CulpritFile, IReadOnlyList<string> Evidence, CrashFix Fix, int? RequiredJava = null, string? Missing = null, int? Line = null);

/// <summary>
/// Rule-based crash diagnosis from the console, logs/latest.log, the newest crash-reports/*.txt and hs_err_pid*.log.
/// Rules are ordered by how specific they are; the first match wins.
/// </summary>
public static partial class CrashAnalyzer
{
    /// <summary>Reads the server's log files (recent ones only) and analyzes them together with the console lines.</summary>
    public static CrashDiagnosis Analyze(string serverDirectory, IEnumerable<string> consoleLines, DateTime? since = null)
    {
        var lines = new List<string>(consoleLines);
        var after = since ?? DateTime.Now.AddHours(-1);
        foreach (var file in RecentFiles(serverDirectory, after))
        {
            try
            {
                lines.AddRange(Tail(file, 600));
            }
            catch (IOException)
            {
            }
        }

        var diagnosis = Analyze(lines);
        return diagnosis.Kind is CrashKind.PluginError or CrashKind.MixinFailure or CrashKind.IncompatibleMods or CrashKind.MissingDependency && diagnosis.Culprit is { } name
            ? diagnosis with { CulpritFile = FindAddon(serverDirectory, name) }
            : diagnosis.Kind == CrashKind.BrokenConfig && diagnosis.Culprit is { } cfg
                ? diagnosis with { CulpritFile = ResolveFile(serverDirectory, cfg) }
                : diagnosis;
    }

    /// <summary>Pure analysis of log lines.</summary>
    public static CrashDiagnosis Analyze(IReadOnlyList<string> lines)
    {
        string? Find(Regex rx, out Match match)
        {
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                var m = rx.Match(lines[i]);
                if (m.Success)
                {
                    match = m;
                    return lines[i];
                }
            }

            match = Match.Empty;
            return null;
        }

        static IReadOnlyList<string> Ev(params string?[] l) => [.. l.Where(x => x is not null).Select(x => x!.Trim())];

        if (Find(EulaRx(), out _) is { } eula)
        {
            return new(CrashKind.Eula, null, null, Ev(eula), CrashFix.AcceptEula);
        }

        if (Find(PortRx(), out _) is { } port)
        {
            return new(CrashKind.PortInUse, null, null, Ev(port), CrashFix.ChangePort);
        }

        if (Find(JavaRx(), out var jm) is { } java)
        {
            var classVersion = int.Parse(jm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var need = classVersion - 44;
            return new(CrashKind.WrongJava, need.ToString(System.Globalization.CultureInfo.InvariantCulture), null, Ev(java), CrashFix.ChangeJava, need);
        }

        if (Find(JavaNeedRx(), out var jn) is { } javaNeed)
        {
            var need = int.Parse(jn.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            return new(CrashKind.WrongJava, need.ToString(System.Globalization.CultureInfo.InvariantCulture), null, Ev(javaNeed), CrashFix.ChangeJava, need);
        }

        if (Find(OomRx(), out _) is { } oom)
        {
            return new(CrashKind.OutOfMemory, null, null, Ev(oom), CrashFix.IncreaseMemory);
        }

        // Bukkit/Paper: "Could not load 'plugins/Foo.jar' ... Unknown/missing dependency plugins: [Vault]"
        if (Find(PluginDepRx(), out var pd) is { } pluginDep)
        {
            var load = Find(CouldNotLoadRx(), out var cl);
            var who = cl.Success ? Path.GetFileNameWithoutExtension(cl.Groups[1].Value) : null;
            return new(CrashKind.MissingDependency, who, null, Ev(load, pluginDep), CrashFix.DisableAddon, Missing: pd.Groups[1].Value.Trim());
        }

        // Fabric/Quilt: "Mod 'Foo' (foo) 1.0 requires any version of fabric-api, which is missing!"
        if (Find(FabricReqRx(), out var fr) is { } fabricReq)
        {
            return new(CrashKind.MissingDependency, fr.Groups[2].Value, null, Ev(fabricReq), CrashFix.DisableAddon, Missing: fr.Groups[3].Value);
        }

        // Forge/NeoForge: "Missing or unsupported mandatory dependencies:" followed by "Mod ID: 'x', Requested by: 'y'"
        if (Find(ForgeDepRx(), out var fd) is { } forgeDep)
        {
            return new(CrashKind.MissingDependency, fd.Groups[2].Value, null, Ev(forgeDep), CrashFix.DisableAddon, Missing: fd.Groups[1].Value);
        }

        if (Find(IncompatRx(), out var ic) is { } incompat)
        {
            return new(CrashKind.IncompatibleMods, ic.Groups[1].Value, null, Ev(incompat), CrashFix.DisableAddon);
        }

        if (Find(MixinRx(), out var mx) is { } mixin)
        {
            var mod = mx.Groups[1].Success ? mx.Groups[1].Value : mx.Groups[2].Value;
            return new(CrashKind.MixinFailure, mod, null, Ev(mixin), CrashFix.DisableAddon);
        }

        if (Find(EnableRx(), out var en) is { } enable)
        {
            return new(CrashKind.PluginError, en.Groups[1].Value, null, Ev(enable), CrashFix.DisableAddon);
        }

        // Paper (configurate) / SnakeYAML syntax errors: the file is only named through the loader in the stack trace.
        if (Find(YamlSyntaxRx(), out _) is { } yaml)
        {
            var file = lines.Any(l => l.Contains("initializeGlobalConfiguration", StringComparison.Ordinal)) ? "config/paper-global.yml"
                : lines.Any(l => l.Contains("WorldDefaults", StringComparison.Ordinal) || l.Contains("initializeWorldDefaults", StringComparison.Ordinal)) ? "config/paper-world-defaults.yml"
                : lines.Any(l => l.Contains("SpigotConfig", StringComparison.Ordinal)) ? "spigot.yml"
                : lines.Any(l => l.Contains("CraftServer", StringComparison.Ordinal) && l.Contains("loadConfig", StringComparison.Ordinal)) ? "bukkit.yml"
                : null;
            // The first "in 'reader', line N" is where the syntax error starts.
            var lineHit = lines.FirstOrDefault(l => YamlLineRx().IsMatch(l));
            var ln = lineHit is null ? Match.Empty : YamlLineRx().Match(lineHit);
            int? line = ln.Success ? int.Parse(ln.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
            if (file is not null || Find(ConfigRx(), out _) is null)
            {
                return new(CrashKind.BrokenConfig, file, null, Ev(yaml, lineHit), file is null ? CrashFix.None : CrashFix.OpenFile, Line: line);
            }
        }

        if (Find(ConfigRx(), out var cf) is { } config)
        {
            return new(CrashKind.BrokenConfig, cf.Groups[1].Value, null, Ev(config), CrashFix.OpenFile);
        }

        if (Find(WorldRx(), out _) is { } world)
        {
            return new(CrashKind.WorldCorrupt, null, null, Ev(world), CrashFix.RestoreBackup);
        }

        var last = lines.LastOrDefault(l => l.Contains("ERROR", StringComparison.Ordinal) || l.Contains("Exception", StringComparison.Ordinal));
        return new(CrashKind.Unknown, null, null, Ev(last), CrashFix.None);
    }

    /// <summary>Finds the jar in plugins/ or mods/ that belongs to a plugin or mod name/id.</summary>
    public static string? FindAddon(string serverDirectory, string name)
    {
        var norm = Normalize(name);
        foreach (var dir in new[] { "plugins", "mods" })
        {
            var full = Path.Combine(serverDirectory, dir);
            if (!Directory.Exists(full))
            {
                continue;
            }

            var jars = Directory.GetFiles(full, "*.jar");
            var hit = jars.FirstOrDefault(j => Normalize(Path.GetFileNameWithoutExtension(j)) == norm)
                ?? jars.FirstOrDefault(j => Normalize(Path.GetFileNameWithoutExtension(j)).StartsWith(norm, StringComparison.Ordinal))
                ?? jars.FirstOrDefault(j => norm.Length >= 4 && Normalize(Path.GetFileNameWithoutExtension(j)).Contains(norm, StringComparison.Ordinal));
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    private static string Normalize(string s) => new([.. s.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    private static string? ResolveFile(string dir, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar)));
        return path.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }

    private static IEnumerable<string> RecentFiles(string dir, DateTime after)
    {
        var latest = Path.Combine(dir, "logs", "latest.log");
        if (File.Exists(latest))
        {
            yield return latest;
        }

        var reports = Path.Combine(dir, "crash-reports");
        if (Directory.Exists(reports) && new DirectoryInfo(reports).GetFiles("*.txt").OrderByDescending(f => f.LastWriteTime).FirstOrDefault() is { } report && report.LastWriteTime >= after)
        {
            yield return report.FullName;
        }

        if (new DirectoryInfo(dir).GetFiles("hs_err_pid*.log").OrderByDescending(f => f.LastWriteTime).FirstOrDefault() is { } hs && hs.LastWriteTime >= after)
        {
            yield return hs.FullName;
        }
    }

    private static Queue<string> Tail(string path, int count)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        var queue = new Queue<string>(count);
        while (reader.ReadLine() is { } line)
        {
            if (queue.Count == count)
            {
                queue.Dequeue();
            }

            queue.Enqueue(line);
        }

        return queue;
    }

    [GeneratedRegex(@"You need to agree to the EULA", RegexOptions.IgnoreCase)]
    private static partial Regex EulaRx();

    [GeneratedRegex(@"FAILED TO BIND TO PORT|Address already in use|BindException", RegexOptions.IgnoreCase)]
    private static partial Regex PortRx();

    [GeneratedRegex(@"UnsupportedClassVersionError.*class file version (\d+)")]
    private static partial Regex JavaRx();

    [GeneratedRegex(@"(?:requires|needs) Java (\d+)|Java (?:version )?(\d+) or (?:newer|above|later) is required", RegexOptions.IgnoreCase)]
    private static partial Regex JavaNeedRx();

    [GeneratedRegex(@"java\.lang\.OutOfMemoryError|Out of memory|There is insufficient memory for the Java Runtime", RegexOptions.IgnoreCase)]
    private static partial Regex OomRx();

    [GeneratedRegex(@"(?:Unknown/missing dependency plugins|UnknownDependencyException).*?\[([^\]]+)\]")]
    private static partial Regex PluginDepRx();

    [GeneratedRegex(@"Could not load '(?:plugins[/\\])?([^']+)'")]
    private static partial Regex CouldNotLoadRx();

    [GeneratedRegex(@"Mod '([^']+)' \((\S+)\)[^\n]*?requires [^\n]*?(?:of |mod )?(\S+?),? (?:which is missing|but only the wrong version is present)")]
    private static partial Regex FabricReqRx();

    [GeneratedRegex(@"Mod ID: '([^']+)', Requested by: '([^']+)'")]
    private static partial Regex ForgeDepRx();

    [GeneratedRegex(@"Mod '([^']+)' \(\S+\)[^\n]*is incompatible with", RegexOptions.IgnoreCase)]
    private static partial Regex IncompatRx();

    [GeneratedRegex(@"Mixin apply(?:ing)? for mod (\S+) failed|mixins?\.json:[\w.$]+\]? from mod (\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex MixinRx();

    [GeneratedRegex(@"Error occurred while enabling (\S+)")]
    private static partial Regex EnableRx();

    [GeneratedRegex(@"(?:Cannot load|Could not load|Failed to load|Error loading|Unable to parse)\s+(?:configuration\s+|config\s+)?(?:file\s+)?'?([\w./\\-]+\.(?:yml|yaml|toml|json|json5|properties|conf|cfg))'?", RegexOptions.IgnoreCase)]
    private static partial Regex ConfigRx();

    [GeneratedRegex(@"ParsingException|ScannerException|ParserException|InvalidConfigurationException|while (?:parsing|scanning) a")]
    private static partial Regex YamlSyntaxRx();

    [GeneratedRegex(@"in 'reader', line (\d+), column")]
    private static partial Regex YamlLineRx();

    [GeneratedRegex(@"Failed to load level|Exception reading .*\.mca|Couldn't load chunk|Chunk file at .* is in the wrong location|level\.dat.*(?:corrupt|EOFException)", RegexOptions.IgnoreCase)]
    private static partial Regex WorldRx();
}
