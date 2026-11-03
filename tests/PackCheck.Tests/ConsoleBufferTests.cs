using System.Collections.Specialized;
using System.Diagnostics;
using PackCheck.App.Infrastructure;
using PackCheck.App.ViewModels;

namespace PackCheck.Tests;

public class ConsoleBufferTests
{
    private static ConsoleEntry Line(int i, string level = "Info") => new(null, level, null, $"line {i}", $"[{level}] line {i}");

    private static List<ConsoleEntry> Lines(int from, int count, Func<int, string>? level = null) =>
        [.. Enumerable.Range(from, count).Select(i => Line(i, level?.Invoke(i) ?? "Info"))];

    private static (ConsoleBuffer Buffer, Func<string> Search, Action<string> SetSearch) WithSearch(bool filterSearch = false, Func<ConsoleEntry, bool>? level = null)
    {
        var buffer = new ConsoleBuffer();
        var search = string.Empty;
        bool IsMatch(ConsoleEntry e) => search.Length > 0 && e.Raw.Contains(search, StringComparison.OrdinalIgnoreCase);
        buffer.SetFilter(e => (level?.Invoke(e) ?? true) && (!filterSearch || IsMatch(e)), IsMatch);
        return (buffer, () => search, s => search = s);
    }

    [Fact]
    public void Enqueue_asks_for_one_flush_per_batch()
    {
        var buffer = new ConsoleBuffer();
        var schedules = Enumerable.Range(0, 500).Count(i => buffer.Enqueue($"line {i}"));
        Assert.Equal(1, schedules);

        var drained = buffer.DrainPending();
        Assert.Equal(500, drained.Count);
        Assert.Equal("line 0", drained[0]);
        Assert.Equal("line 499", drained[^1]);

        // After a flush the next line starts a new batch.
        Assert.True(buffer.Enqueue("again"));
        Assert.False(buffer.Enqueue("again 2"));
    }

    [Fact]
    public void Enqueue_from_many_threads_schedules_once_and_loses_nothing()
    {
        var buffer = new ConsoleBuffer();
        var schedules = 0;
        Parallel.For(0, 10_000, i =>
        {
            if (buffer.Enqueue($"l{i}"))
            {
                Interlocked.Increment(ref schedules);
            }
        });

        Assert.Equal(1, schedules);
        Assert.Equal(10_000, buffer.DrainPending().Count);
    }

    [Fact]
    public void Append_raises_one_collection_change_for_a_batch()
    {
        var buffer = new ConsoleBuffer();
        var events = new List<NotifyCollectionChangedAction>();
        buffer.View.CollectionChanged += (_, e) => events.Add(e.Action);

        var result = buffer.Append(Lines(0, 1000), paused: false);

        Assert.True(result.ViewChanged);
        Assert.Equal([NotifyCollectionChangedAction.Reset], events);
        Assert.Equal(1000, buffer.View.Count);
        Assert.Equal(1000, buffer.InfoCount);
    }

    [Fact]
    public void Single_line_uses_a_plain_add_event()
    {
        var buffer = new ConsoleBuffer();
        var events = new List<NotifyCollectionChangedAction>();
        buffer.View.CollectionChanged += (_, e) => events.Add(e.Action);

        buffer.Append([Line(1)], paused: false);

        Assert.Equal([NotifyCollectionChangedAction.Add], events);
    }

    [Fact]
    public void Passing_the_cap_trims_the_oldest_chunk_at_once()
    {
        var buffer = new ConsoleBuffer();
        buffer.Append(Lines(0, ConsoleBuffer.MaxLines), paused: false);
        Assert.Equal(ConsoleBuffer.MaxLines, buffer.View.Count);

        var events = 0;
        buffer.View.CollectionChanged += (_, _) => events++;
        buffer.Append([Line(ConsoleBuffer.MaxLines)], paused: false);

        Assert.Equal(ConsoleBuffer.MaxLines + 1 - ConsoleBuffer.TrimChunk, buffer.View.Count);
        Assert.Equal(buffer.View.Count, buffer.All.Count);
        Assert.Equal("line 1000", buffer.View[0].Message);
        Assert.Equal("line 10000", buffer.View[^1].Message);
        Assert.Equal(2, events); // the add and one range removal, not 1000 removals
        Assert.Equal(ConsoleBuffer.MaxLines + 1, buffer.InfoCount); // counters count every line ever seen
    }

    [Fact]
    public void A_huge_batch_is_trimmed_to_whole_chunks_below_the_cap()
    {
        var buffer = new ConsoleBuffer();
        buffer.Append(Lines(0, 9_999), paused: false);
        buffer.Append(Lines(9_999, 5_000), paused: false);

        Assert.Equal(9_999, buffer.View.Count);
        Assert.Equal(9_999, buffer.All.Count);
        Assert.Equal("line 5000", buffer.View[0].Message);
        Assert.Equal("line 14998", buffer.View[^1].Message);
    }

    [Fact]
    public void Level_filter_and_counters_stay_correct_across_batches()
    {
        var (buffer, _, _) = WithSearch(level: e => e.Level != "Warn");
        buffer.Append(Lines(0, 300, i => i % 3 == 0 ? "Warn" : i % 3 == 1 ? "Error" : "Info"), paused: false);
        buffer.Append(Lines(300, 300, i => i % 3 == 0 ? "Warn" : i % 3 == 1 ? "Error" : "Info"), paused: false);

        Assert.Equal(200, buffer.WarnCount);
        Assert.Equal(200, buffer.ErrorCount);
        Assert.Equal(200, buffer.InfoCount);
        Assert.Equal(400, buffer.View.Count);
        Assert.DoesNotContain(buffer.View, e => e.Level == "Warn");
        Assert.Equal(600, buffer.All.Count);
    }

    [Fact]
    public void Search_matches_follow_batches_and_trimming()
    {
        var (buffer, _, setSearch) = WithSearch();
        setSearch("line 1"); // line 1, 10-19, 100-199, 1000-1999, 10000-...
        buffer.Rebuild();
        buffer.Append(Lines(0, ConsoleBuffer.MaxLines), paused: false);
        Assert.Equal(1 + 10 + 100 + 1000, buffer.Matches.Count);

        // Step to a match, then trim the first chunk away (it holds line 1, 10-19 and 100-199).
        Assert.Equal("line 1", buffer.StepMatch(1)!.Message);
        for (var i = 0; i < 120; i++)
        {
            buffer.StepMatch(1);
        }

        Assert.Equal(120, buffer.MatchIndex);
        var result = buffer.Append([Line(ConsoleBuffer.MaxLines)], paused: false);

        Assert.True(result.MatchesChanged);
        Assert.Equal(1000 + 1, buffer.Matches.Count); // 1000-1999 plus line 10000
        Assert.Equal(9, buffer.MatchIndex); // index moved back by the 111 dropped matches
        Assert.All(buffer.Matches, m => Assert.Contains(m, buffer.View));
    }

    [Fact]
    public void Filter_search_shows_only_matching_lines()
    {
        var (buffer, _, setSearch) = WithSearch(filterSearch: true);
        setSearch("line 5");
        buffer.Rebuild();
        buffer.Append(Lines(0, 100), paused: false);

        Assert.Equal(11, buffer.View.Count); // 5 and 50-59
        Assert.Equal(11, buffer.Matches.Count);
        Assert.Equal(100, buffer.All.Count);
    }

    [Fact]
    public void Paused_keeps_lines_hidden_until_rebuild()
    {
        var buffer = new ConsoleBuffer();
        buffer.Append(Lines(0, 10), paused: false);
        var result = buffer.Append(Lines(10, 50), paused: true);

        Assert.False(result.ViewChanged);
        Assert.Equal(10, buffer.View.Count);
        Assert.Equal(60, buffer.All.Count);
        Assert.Equal(60, buffer.InfoCount);

        buffer.Rebuild(); // what un-pausing does
        Assert.Equal(60, buffer.View.Count);
    }

    [Fact]
    public void Clear_resets_everything()
    {
        var (buffer, _, setSearch) = WithSearch();
        setSearch("line");
        buffer.Append(Lines(0, 20, _ => "Error"), paused: false);
        buffer.StepMatch(1);
        buffer.Clear();

        Assert.Empty(buffer.View);
        Assert.Empty(buffer.All);
        Assert.Empty(buffer.Matches);
        Assert.Equal(-1, buffer.MatchIndex);
        Assert.Equal(0, buffer.ErrorCount);
    }

    [Fact]
    public void Fifty_thousand_lines_in_100_line_batches_stay_fast()
    {
        var (buffer, _, setSearch) = WithSearch();
        setSearch("line 4");
        buffer.Rebuild();
        var notifications = 0;
        buffer.View.CollectionChanged += (_, _) => notifications++;
        var lines = Lines(0, 50_000);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < lines.Count; i += 100)
        {
            buffer.Append(lines.GetRange(i, 100), paused: false);
        }

        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.ElapsedMilliseconds} ms");
        Assert.InRange(buffer.View.Count, ConsoleBuffer.MaxLines - ConsoleBuffer.TrimChunk, ConsoleBuffer.MaxLines);
        Assert.Equal("line 49999", buffer.View[^1].Message);
        Assert.Equal(50_000, buffer.InfoCount);
        Assert.Equal(buffer.View.Count(e => e.Raw.Contains("line 4", StringComparison.OrdinalIgnoreCase)), buffer.Matches.Count);
        Assert.InRange(notifications, 500, 600); // one per batch plus one per chunk trim
    }

    [Fact]
    public void BatchObservableCollection_ranges_keep_order()
    {
        var c = new BatchObservableCollection<int>();
        c.AddRange([1, 2, 3, 4, 5]);
        c.RemoveRange(1, 2);
        Assert.Equal([1, 4, 5], c);
        Assert.Throws<ArgumentOutOfRangeException>(() => c.RemoveRange(2, 5));
    }
}
