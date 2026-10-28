using System.Text.Json.Serialization;

namespace PackCheck.Core;

/// <summary>Outcome of a single check. "Fail" and "could not run" (Error/Skip) are deliberately distinct.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CheckStatus>))]
public enum CheckStatus
{
    Pass,
    Warn,
    Fail,
    Skip,
    Error,
}

[JsonConverter(typeof(JsonStringEnumConverter<Confidence>))]
public enum Confidence
{
    Low,
    Medium,
    High,
}

public sealed record SuggestedFix(string Text, bool IsCertain);

public sealed record CheckResult
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required CheckStatus Status { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public string? AffectedFile { get; init; }
    public string? AffectedPlugin { get; init; }
    public Confidence Confidence { get; init; } = Confidence.High;
    public SuggestedFix? Fix { get; init; }
    public string? NotRunReason { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<OverallStatus>))]
public enum OverallStatus
{
    Pass,
    PassWithWarnings,
    Fail,
    Incomplete,
}

public sealed record PluginInfo
{
    public required string FileName { get; init; }
    public required string Sha256 { get; init; }
    public required long Size { get; init; }
    public string? Name { get; init; }
    public string? Version { get; init; }
    public string? ApiVersion { get; init; }
    public string? Main { get; init; }
    public string MetadataSource { get; init; } = "none";
    public IReadOnlyList<string> Depends { get; init; } = [];
    public IReadOnlyList<string> SoftDepends { get; init; } = [];
    public bool IsCorrupt { get; init; }
}

public sealed record ValidationReport
{
    public const string SchemaVersion = "1.0";
    public string Schema { get; init; } = SchemaVersion;
    public required string Tool { get; init; }
    public required string ToolVersion { get; init; }
    public required string Target { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required TimeSpan Duration { get; init; }
    public required OverallStatus Overall { get; init; }
    public required IReadOnlyList<CheckResult> Checks { get; init; }
    public IReadOnlyList<PluginInfo> Plugins { get; init; } = [];

    public int Count(CheckStatus status) => Checks.Count(c => c.Status == status);

    public static OverallStatus Aggregate(IEnumerable<CheckResult> checks)
    {
        var list = checks.ToList();
        if (list.Count == 0)
        {
            // Nothing was checked, so nothing can be claimed to pass.
            return OverallStatus.Incomplete;
        }

        if (list.Any(c => c.Status == CheckStatus.Fail))
        {
            return OverallStatus.Fail;
        }

        if (list.Any(c => c.Status == CheckStatus.Error))
        {
            return OverallStatus.Incomplete;
        }

        return list.Any(c => c.Status == CheckStatus.Warn) ? OverallStatus.PassWithWarnings : OverallStatus.Pass;
    }
}

/// <summary>Process exit codes used by the CLI. Stable public contract for CI.</summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int Warnings = 0;
    public const int ValidationFailed = 1;
    public const int Incomplete = 2;
    public const int UsageError = 64;
    public const int InternalError = 70;

    public static int For(OverallStatus status, bool failOnWarnings = false) => status switch
    {
        OverallStatus.Pass => Success,
        OverallStatus.PassWithWarnings => failOnWarnings ? ValidationFailed : Warnings,
        OverallStatus.Fail => ValidationFailed,
        _ => Incomplete,
    };
}
