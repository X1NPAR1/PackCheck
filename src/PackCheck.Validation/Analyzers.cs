using PackCheck.Core;

namespace PackCheck.Validation.Analyzers;

public sealed class StructureAnalyzer : IAnalyzer
{
    public string Id => "PC001";

    public IEnumerable<CheckResult> Analyze(PackContext context)
    {
        var hasPlugins = Directory.Exists(Path.Combine(context.Root, "plugins")) || Directory.Exists(Path.Combine(context.Root, "mods"));
        yield return new CheckResult
        {
            Id = Id,
            Name = "Pack structure",
            Status = hasPlugins ? CheckStatus.Pass : CheckStatus.Warn,
            Description = hasPlugins ? "A plugins or mods directory is present." : "No plugins or mods directory was found.",
            Fix = hasPlugins ? null : new SuggestedFix("Create a 'plugins' folder next to the server JAR if this pack should run plugins.", false),
        };

        yield return new CheckResult
        {
            Id = "PC002",
            Name = "Server JAR",
            Status = context.ServerJar is null ? CheckStatus.Warn : CheckStatus.Pass,
            Description = context.ServerJar is null ? "No server JAR could be identified in the pack root." : $"Server JAR: {context.ServerJar}",
            Confidence = Confidence.Medium,
        };
    }
}

public sealed class ServerPropertiesAnalyzer : IAnalyzer
{
    public string Id => "PC010";

    public IEnumerable<CheckResult> Analyze(PackContext context)
    {
        if (!context.ServerPropertiesExists)
        {
            yield return new CheckResult
            {
                Id = Id,
                Name = "server.properties",
                Status = CheckStatus.Warn,
                Description = "server.properties is missing; the server will generate defaults on first start.",
            };
            yield break;
        }

        var props = context.ServerProperties;
        var problems = new List<string>();
        void Int(string key, int min, int max)
        {
            if (props.TryGetValue(key, out var v) && (!int.TryParse(v, out var n) || n < min || n > max))
            {
                problems.Add($"{key}={v} (expected {min}..{max})");
            }
        }

        void Bool(string key)
        {
            if (props.TryGetValue(key, out var v) && v is not ("true" or "false"))
            {
                problems.Add($"{key}={v} (expected true/false)");
            }
        }

        Int("server-port", 1, 65535);
        Int("max-players", 0, 100000);
        Int("view-distance", 2, 32);
        Int("simulation-distance", 2, 32);
        Bool("online-mode");
        Bool("pvp");
        Bool("enable-command-block");
        Bool("white-list");

        yield return new CheckResult
        {
            Id = Id,
            Name = "server.properties values",
            Status = problems.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail,
            Description = problems.Count == 0 ? "All known keys have valid values." : $"{problems.Count} invalid value(s).",
            Evidence = problems,
            AffectedFile = "server.properties",
            Fix = problems.Count == 0 ? null : new SuggestedFix("Correct the listed values.", true),
        };

        if (props.TryGetValue("online-mode", out var om) && om == "false")
        {
            yield return new CheckResult
            {
                Id = "PC011",
                Name = "Offline mode",
                Status = CheckStatus.Warn,
                AffectedFile = "server.properties",
                Description = "online-mode=false lets anyone join with any username unless a proxy authenticates players.",
                Fix = new SuggestedFix("Set online-mode=true, or ensure the server is only reachable through an authenticating proxy.", false),
            };
        }
    }
}

public sealed class JarIntegrityAnalyzer : IAnalyzer
{
    public string Id => "PC020";

    public IEnumerable<CheckResult> Analyze(PackContext context)
    {
        foreach (var p in context.Plugins.Where(p => p.IsCorrupt))
        {
            yield return new CheckResult
            {
                Id = Id,
                Name = "Corrupt JAR",
                Status = CheckStatus.Fail,
                AffectedFile = "plugins/" + p.FileName,
                Description = "The file could not be read as a ZIP/JAR archive.",
                Evidence = [$"sha256 {p.Sha256}", $"size {p.Size} bytes"],
                Fix = new SuggestedFix("Re-download the plugin from its official source.", true),
            };
        }

        foreach (var p in context.Plugins.Where(p => !p.IsCorrupt && p.Name is null))
        {
            yield return new CheckResult
            {
                Id = "PC021",
                Name = "Missing plugin metadata",
                Status = CheckStatus.Warn,
                Confidence = Confidence.Medium,
                AffectedFile = "plugins/" + p.FileName,
                Description = $"No usable plugin.yml / paper-plugin.yml ({p.MetadataSource}). Compatibility cannot be determined.",
                Fix = new SuggestedFix("Check that this file is a Bukkit/Paper plugin and not a library or mod.", false),
            };
        }

        if (!context.Plugins.Any(p => p.IsCorrupt))
        {
            yield return new CheckResult
            {
                Id = Id,
                Name = "JAR integrity",
                Status = CheckStatus.Pass,
                Description = $"{context.Plugins.Count} plugin archive(s) opened successfully.",
            };
        }
    }
}

public sealed class DuplicateAnalyzer : IAnalyzer
{
    public string Id => "PC030";

    public IEnumerable<CheckResult> Analyze(PackContext context)
    {
        var any = false;
        var byName = context.Plugins.Where(p => p.Name is not null).GroupBy(p => p.Name!, StringComparer.OrdinalIgnoreCase);
        foreach (var g in byName.Where(g => g.Count() > 1))
        {
            any = true;
            yield return new CheckResult
            {
                Id = Id,
                Name = "Duplicate plugin",
                Status = CheckStatus.Fail,
                AffectedPlugin = g.Key,
                Description = $"'{g.Key}' is installed {g.Count()} times; the server loads only one of them.",
                Evidence = g.Select(p => $"{p.FileName} (version {p.Version ?? "?"})").ToList(),
                Fix = new SuggestedFix("Keep one copy, usually the newest version.", true),
            };
        }

        foreach (var g in context.Plugins.GroupBy(p => p.Sha256).Where(g => g.Count() > 1))
        {
            any = true;
            yield return new CheckResult
            {
                Id = "PC031",
                Name = "Identical files",
                Status = CheckStatus.Warn,
                Description = "The same file content exists under several names.",
                Evidence = g.Select(p => p.FileName).ToList(),
                Fix = new SuggestedFix("Remove the extra copies.", true),
            };
        }

        if (!any)
        {
            yield return new CheckResult { Id = Id, Name = "Duplicates", Status = CheckStatus.Pass, Description = "No duplicate plugins found." };
        }
    }
}

public sealed class DependencyAnalyzer : IAnalyzer
{
    public string Id => "PC040";

    public IEnumerable<CheckResult> Analyze(PackContext context)
    {
        var names = new HashSet<string>(context.Plugins.Select(p => p.Name).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var missing = 0;
        foreach (var p in context.Plugins.Where(p => p.Name is not null))
        {
            foreach (var dep in p.Depends.Where(d => !names.Contains(d)))
            {
                missing++;
                yield return new CheckResult
                {
                    Id = Id,
                    Name = "Missing dependency",
                    Status = CheckStatus.Fail,
                    AffectedPlugin = p.Name,
                    AffectedFile = "plugins/" + p.FileName,
                    Description = $"'{p.Name}' requires '{dep}', which is not installed. The plugin will not enable.",
                    Evidence = [$"{p.MetadataSource}: depend: [{string.Join(", ", p.Depends)}]"],
                    Fix = new SuggestedFix($"Install '{dep}' or remove '{p.Name}'.", true),
                };
            }

            var softMissing = p.SoftDepends.Where(d => !names.Contains(d)).ToList();
            if (softMissing.Count > 0)
            {
                yield return new CheckResult
                {
                    Id = "PC041",
                    Name = "Optional dependency absent",
                    Status = CheckStatus.Pass,
                    AffectedPlugin = p.Name,
                    Confidence = Confidence.Medium,
                    Description = $"'{p.Name}' can integrate with {string.Join(", ", softMissing)} (optional, not installed).",
                };
            }
        }

        if (missing == 0)
        {
            yield return new CheckResult { Id = Id, Name = "Dependencies", Status = CheckStatus.Pass, Description = "All required dependencies are present." };
        }
    }
}

public sealed class VersionAnalyzer : IAnalyzer
{
    public string Id => "PC050";

    public IEnumerable<CheckResult> Analyze(PackContext context)
    {
        if (context.ServerVersion is not { } server)
        {
            yield return new CheckResult
            {
                Id = Id,
                Name = "Version compatibility",
                Status = CheckStatus.Skip,
                Description = "Minecraft version unknown.",
                NotRunReason = "Pass --mc-version or validate a server created by PackCheck.",
            };
            yield break;
        }

        var tooNew = 0;
        foreach (var p in context.Plugins.Where(p => p.ApiVersion is not null))
        {
            if (!MinecraftVersion.TryParse(p.ApiVersion, out var api) || api <= server)
            {
                continue;
            }

            tooNew++;
            yield return new CheckResult
            {
                Id = Id,
                Name = "Plugin needs newer server",
                Status = CheckStatus.Fail,
                AffectedPlugin = p.Name,
                AffectedFile = "plugins/" + p.FileName,
                Description = $"'{p.Name}' declares api-version {p.ApiVersion}, newer than the server ({server}).",
                Evidence = [$"api-version: {p.ApiVersion}"],
                Fix = new SuggestedFix("Update the server, or install an older release of the plugin.", true),
            };
        }

        if (tooNew == 0)
        {
            yield return new CheckResult { Id = Id, Name = "Version compatibility", Status = CheckStatus.Pass, Description = $"No plugin declares an api-version newer than {server}." };
        }

        if (context.JavaVersion is { } java)
        {
            var need = server.RequiredJava();
            yield return new CheckResult
            {
                Id = "PC051",
                Name = "Java version",
                Status = java >= need ? CheckStatus.Pass : CheckStatus.Fail,
                Description = java >= need ? $"Java {java} satisfies Minecraft {server} (needs {need}+)." : $"Minecraft {server} needs Java {need}+, configured Java is {java}.",
                Fix = java >= need ? null : new SuggestedFix($"Install Java {need} or newer.", true),
            };
        }
        else
        {
            yield return new CheckResult
            {
                Id = "PC051",
                Name = "Java version",
                Status = CheckStatus.Skip,
                Description = "Java version unknown.",
                NotRunReason = "No Java runtime configured for this pack.",
            };
        }
    }
}
