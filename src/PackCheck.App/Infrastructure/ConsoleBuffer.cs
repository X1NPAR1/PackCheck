using System.Collections.Concurrent;
using PackCheck.App.ViewModels;

namespace PackCheck.App.Infrastructure;

/// <summary>What one <see cref="ConsoleBuffer.Append"/> changed, so the caller raises each notification once.</summary>
public readonly record struct ConsoleAppendResult(bool ViewChanged, bool MatchesChanged);

/// <summary>
/// The console's lines without any dispatcher: raw output is queued from any thread, then appended on the
/// UI thread in batches. Holds every line (<see cref="All"/>), the filtered view, the search matches and the counters.
/// Both lists are trimmed in chunks so a flood does not shift 10,000 items for every new line.
/// </summary>
public sealed class ConsoleBuffer
{
    public const int MaxLines = 10_000;
    public const int TrimChunk = 1_000;

    private readonly ConcurrentQueue<string> _pending = new();
    private readonly List<ConsoleEntry> _all = [];
    private readonly List<ConsoleEntry> _matches = [];
    private Func<ConsoleEntry, bool> _visible = static _ => true;
    private Func<ConsoleEntry, bool> _isMatch = static _ => false;
    private int _flushScheduled;

    public IReadOnlyList<ConsoleEntry> All => _all;

    public BatchObservableCollection<ConsoleEntry> View { get; } = [];

    public IReadOnlyList<ConsoleEntry> Matches => _matches;

    /// <summary>Index of the current search match, -1 when none is selected.</summary>
    public int MatchIndex { get; private set; } = -1;

    public int InfoCount { get; private set; }

    public int WarnCount { get; private set; }

    public int ErrorCount { get; private set; }

    /// <summary>Sets the filter and search predicates used by <see cref="Append"/> and <see cref="Rebuild"/>.</summary>
    public void SetFilter(Func<ConsoleEntry, bool> visible, Func<ConsoleEntry, bool> isMatch)
    {
        _visible = visible;
        _isMatch = isMatch;
    }

    /// <summary>Queues a raw line from any thread. True when the caller must schedule a flush (once per batch).</summary>
    public bool Enqueue(string raw)
    {
        _pending.Enqueue(raw);
        return Interlocked.Exchange(ref _flushScheduled, 1) == 0;
    }

    /// <summary>Takes every queued line; lines queued afterwards schedule a new flush.</summary>
    public List<string> DrainPending()
    {
        Volatile.Write(ref _flushScheduled, 0);
        var lines = new List<string>();
        while (_pending.TryDequeue(out var line))
        {
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>Adds a batch of lines. While paused they are kept but not shown.</summary>
    public ConsoleAppendResult Append(IReadOnlyList<ConsoleEntry> entries, bool paused)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return default;
        }

        _all.AddRange(entries);
        foreach (var e in entries)
        {
            switch (e.Level)
            {
                case "Warn":
                    WarnCount++;
                    break;
                case "Error":
                    ErrorCount++;
                    break;
                default:
                    InfoCount++;
                    break;
            }
        }

        if (_all.Count > MaxLines)
        {
            _all.RemoveRange(0, ChunkFor(_all.Count));
        }

        if (paused)
        {
            return default;
        }

        var shown = new List<ConsoleEntry>(entries.Count);
        var matchesChanged = false;
        foreach (var e in entries)
        {
            if (!_visible(e))
            {
                continue;
            }

            shown.Add(e);
            if (_isMatch(e))
            {
                _matches.Add(e);
                matchesChanged = true;
            }
        }

        if (shown.Count == 0)
        {
            return default;
        }

        View.AddRange(shown);
        if (View.Count > MaxLines)
        {
            var remove = ChunkFor(View.Count);
            matchesChanged |= DropLeadingMatches(remove);
            View.RemoveRange(0, remove);
        }

        return new ConsoleAppendResult(true, matchesChanged);
    }

    /// <summary>Rebuilds the view and matches from all lines after a filter, search or pause change.</summary>
    public void Rebuild()
    {
        var shown = new List<ConsoleEntry>();
        _matches.Clear();
        MatchIndex = -1;
        foreach (var e in _all)
        {
            if (!_visible(e))
            {
                continue;
            }

            shown.Add(e);
            if (_isMatch(e))
            {
                _matches.Add(e);
            }
        }

        View.Clear();
        View.AddRange(shown);
    }

    /// <summary>Moves to the next (+1) or previous (-1) match, wrapping around.</summary>
    public ConsoleEntry? StepMatch(int delta)
    {
        if (_matches.Count == 0)
        {
            return null;
        }

        MatchIndex = MatchIndex < 0
            ? (delta > 0 ? 0 : _matches.Count - 1)
            : (MatchIndex + delta + _matches.Count) % _matches.Count;
        return _matches[MatchIndex];
    }

    public void Clear()
    {
        _all.Clear();
        _matches.Clear();
        MatchIndex = -1;
        View.Clear();
        InfoCount = WarnCount = ErrorCount = 0;
    }

    /// <summary>How many of the oldest lines to remove: whole chunks, enough to get back under the cap.</summary>
    private static int ChunkFor(int count) => Math.Min(count, (count - MaxLines + TrimChunk - 1) / TrimChunk * TrimChunk);

    /// <summary>Drops the matches among the first <paramref name="count"/> view lines (matches are an ordered subsequence of the view).</summary>
    private bool DropLeadingMatches(int count)
    {
        var dropped = 0;
        for (var i = 0; i < count && dropped < _matches.Count; i++)
        {
            if (ReferenceEquals(_matches[dropped], View[i]))
            {
                dropped++;
            }
        }

        if (dropped == 0)
        {
            return false;
        }

        _matches.RemoveRange(0, dropped);
        MatchIndex = MatchIndex < 0 ? -1 : Math.Max(-1, MatchIndex - dropped);
        return true;
    }
}
