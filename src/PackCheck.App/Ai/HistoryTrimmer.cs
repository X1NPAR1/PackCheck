using System.Text.Json.Nodes;

namespace PackCheck.App.Ai;

/// <summary>One tool result kept in a conversation's history, in the order it was added.</summary>
public sealed record ToolResultEntry(string Id, string Tool, string Arg, string Content, bool IsError);

/// <summary>
/// Keeps long agent runs inside the context window: once the history holds more than <see cref="KeepRecent"/> tool results,
/// older results longer than <see cref="MaxLength"/> characters are replaced with a short stub. Only the content changes,
/// so every tool call still has its result with the same id.
/// </summary>
public static class HistoryTrimmer
{
    public const int KeepRecent = 6;

    public const int MaxLength = 2000;

    /// <summary>
    /// Returns the entries with old, long results stubbed; ids, order and count stay the same.
    /// <paramref name="newCount"/> is the size of the batch just added: those results are always kept so the model sees each one at least once.
    /// </summary>
    public static IReadOnlyList<ToolResultEntry> Trim(IReadOnlyList<ToolResultEntry> entries, int newCount = 0, int keepRecent = KeepRecent, int maxLength = MaxLength)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var cutoff = entries.Count - Math.Max(keepRecent, newCount);
        return entries.Select((e, i) => i < cutoff && e.Content.Length > maxLength ? e with { Content = Stub(e.Tool, e.Arg) } : e).ToList();
    }

    public static string Stub(string tool, string arg) =>
        arg.Length == 0
            ? $"[earlier result of {tool} trimmed; call the tool again if needed]"
            : $"[earlier result of {tool} {arg} trimmed; call the tool again if needed]";

    /// <summary>A short label for the call (its first text argument, such as a path), used in the stub.</summary>
    public static string ArgSummary(JsonObject? args)
    {
        var first = args?.Select(p => p.Value).OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        if (first is null)
        {
            return string.Empty;
        }

        first = first.ReplaceLineEndings(" ").Trim();
        return first.Length > 80 ? first[..80] + "…" : first;
    }
}
