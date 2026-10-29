using PackCheck.Core;

namespace PackCheck.Validation.Analyzers;

/// <summary>Corrupt mod jars and jars without readable mod metadata.</summary>
public sealed class ModIntegrityAnalyzer : IAnalyzer
{
    public string Id => "PC060";

    public IEnumerable<CheckResult> Analyze(PackContext context)
    {
        if (context.Mods.Count == 0)
        {
            yield break;
        }

        foreach (var m in context.Mods.Where(m => m.IsCorrupt))
        {
            yield return new CheckResult
            {
                Id = Id,
                Name = "Corrupt mod",
                Status = CheckStatus.Fail,
                AffectedFile = "mods/" + m.FileName,
                Description = "The file could not be read as a JAR archive.",
                Evidence = [$"sha256 {m.Sha256}"],
                Fix = new SuggestedFix("Download the mod again from its official page.", true),
            };
        }

        foreach (var m in context.Mods.Where(m => !m.IsCorrupt && m.Id is null))
        {
            yield return new CheckResult
            {
                Id = "PC061",
                Name = "Unknown mod metadata",
                Status = CheckStatus.Warn,
                Confidence = Confidence.Medium,
                AffectedFile = "mods/" + m.FileName,
                Description = "No fabric.mod.json, quilt.mod.json or mods.toml was found. PackCheck cannot tell what this jar needs.",
            };
        }

        var loaders = context.Mods.Where(m => m.Id is not null).Select(m => m.Loader).Distinct().ToList();
        if (loaders.Count > 1 && !(loaders.Contains("fabric") && loaders.Contains("quilt") && loaders.Count == 2))
        {
            yield return new CheckResult
            {
                Id = "PC062",
                Name = "Mixed mod loaders",
                Status = CheckStatus.Fail,
                Description = $"Mods for different loaders are installed together: {string.Join(", ", loaders)}. A server runs mods for one loader only.",
                Evidence = context.Mods.Where(m => m.Id is not null).GroupBy(m => m.Loader).Select(g => $"{g.Key}: {string.Join(", ", g.Take(5).Select(m => m.FileName))}").ToList(),
                Fix = new SuggestedFix("Remove the mods made for the other loader.", true),
            };
        }
        else if (!context.Mods.Any(m => m.IsCorrupt))
        {
            yield return new CheckResult { Id = Id, Name = "Mod files", Status = CheckStatus.Pass, Description = $"{context.Mods.Count} mod jar(s) opened successfully." };
        }
    }
}

public sealed class ModDuplicateAnalyzer : IAnalyzer
{
    public string Id => "PC063";

    public IEnumerable<CheckResult> Analyze(PackContext context)
    {
        foreach (var g in context.Mods.Where(m => m.Id is not null).GroupBy(m => m.Id!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            yield return new CheckResult
            {
                Id = Id,
                Name = "Duplicate mod",
                Status = CheckStatus.Fail,
                AffectedPlugin = g.Key,
                Description = $"Mod '{g.Key}' is installed {g.Count()} times; the loader refuses to start with duplicates.",
                Evidence = g.Select(m => $"{m.FileName} ({m.Version ?? "?"})").ToList(),
                Fix = new SuggestedFix("Keep one copy, usually the newest.", true),
            };
        }
    }
}

public sealed class ModDependencyAnalyzer : IAnalyzer
{
    public string Id => "PC064";

    public IEnumerable<CheckResult> Analyze(PackContext context)
    {
        if (context.Mods.Count == 0)
        {
            yield break;
        }

        var ids = new HashSet<string>(context.Mods.Select(m => m.Id).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var missing = 0;
        foreach (var m in context.Mods.Where(m => m.Id is not null))
        {
            foreach (var d in m.Dependencies.Where(d => d.Required && !ModMetadataReader.BuiltIn.Contains(d.Id) && !ids.Contains(d.Id)))
            {
                missing++;
                yield return new CheckResult
                {
                    Id = Id,
                    Name = "Missing mod dependency",
                    Status = CheckStatus.Fail,
                    AffectedPlugin = m.Id,
                    AffectedFile = "mods/" + m.FileName,
                    Description = $"'{m.Name ?? m.Id}' requires '{d.Id}'{(d.VersionRange is null ? string.Empty : $" ({d.VersionRange})")}, which is not installed.",
                    Fix = new SuggestedFix($"Install '{d.Id}' for this loader and Minecraft version.", true),
                };
            }
        }

        if (missing == 0)
        {
            yield return new CheckResult { Id = Id, Name = "Mod dependencies", Status = CheckStatus.Pass, Description = "All required mod dependencies are present." };
        }
    }
}

public sealed class ModCompatibilityAnalyzer : IAnalyzer
{
    public string Id => "PC065";

    public IEnumerable<CheckResult> Analyze(PackContext context)
    {
        foreach (var m in context.Mods.Where(m => m.Environment.Equals("client", StringComparison.OrdinalIgnoreCase)))
        {
            yield return new CheckResult
            {
                Id = "PC066",
                Name = "Client-only mod",
                Status = CheckStatus.Fail,
                AffectedFile = "mods/" + m.FileName,
                AffectedPlugin = m.Id,
                Description = $"'{m.Name ?? m.Id}' is marked as client-only and usually crashes a dedicated server.",
                Evidence = [$"environment: {m.Environment}"],
                Fix = new SuggestedFix("Remove it from the server's mods folder; players keep it on their client.", true),
            };
        }

        if (context.ServerVersion is not { } server)
        {
            if (context.Mods.Count > 0)
            {
                yield return new CheckResult { Id = Id, Name = "Mod version ranges", Status = CheckStatus.Skip, Description = "Minecraft version unknown.", NotRunReason = "Pass --mc-version or validate a server created by PackCheck." };
            }

            yield break;
        }

        var bad = 0;
        foreach (var m in context.Mods.Where(m => m.MinecraftRange is not null))
        {
            var ok = VersionRange.Contains(m.MinecraftRange, server);
            if (ok == false)
            {
                bad++;
                yield return new CheckResult
                {
                    Id = Id,
                    Name = "Mod made for another Minecraft version",
                    Status = CheckStatus.Fail,
                    AffectedFile = "mods/" + m.FileName,
                    AffectedPlugin = m.Id,
                    Description = $"'{m.Name ?? m.Id}' supports Minecraft {m.MinecraftRange}, the server runs {server}.",
                    Evidence = [$"minecraft: {m.MinecraftRange}"],
                    Fix = new SuggestedFix("Download the release of this mod that matches the server version.", true),
                };
            }
            else if (ok is null)
            {
                yield return new CheckResult
                {
                    Id = Id,
                    Name = "Unreadable version range",
                    Status = CheckStatus.Warn,
                    Confidence = Confidence.Low,
                    AffectedFile = "mods/" + m.FileName,
                    Description = $"The Minecraft range '{m.MinecraftRange}' of '{m.Name ?? m.Id}' could not be evaluated; it was not guessed.",
                };
            }
        }

        if (bad == 0 && context.Mods.Count > 0)
        {
            yield return new CheckResult { Id = Id, Name = "Mod version ranges", Status = CheckStatus.Pass, Description = $"No mod declares a Minecraft range that excludes {server}." };
        }
    }
}
