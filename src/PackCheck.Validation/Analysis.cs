using PackCheck.Core;

namespace PackCheck.Validation;

/// <summary>Everything analyzers may read. Built once, never mutated by analyzers.</summary>
public sealed class PackContext
{
    public required string Root { get; init; }
    public required IReadOnlyList<PluginInfo> Plugins { get; init; }
    public IReadOnlyList<ModInfo> Mods { get; init; } = [];
    public MinecraftVersion? ServerVersion { get; init; }
    public int? JavaVersion { get; init; }
    public IReadOnlyDictionary<string, string> ServerProperties { get; init; } = new Dictionary<string, string>();
    public bool ServerPropertiesExists { get; init; }
    public string? ServerJar { get; init; }
}

/// <summary>One independent check. Add a class implementing this to add a rule; existing rules stay untouched.</summary>
public interface IAnalyzer
{
    string Id { get; }
    IEnumerable<CheckResult> Analyze(PackContext context);
}
