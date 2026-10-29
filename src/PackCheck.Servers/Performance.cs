using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PackCheck.Servers;

/// <summary>Milliseconds per tick from the platform's own commands (Paper "mspt", Forge/NeoForge "forge tps"/"neoforge tps").</summary>
public static partial class MsptParser
{
    // Paper "mspt" second line: "◴ 1.2/0.8/3.4, 1.1/0.7/4.0, 1.3/0.7/9.9" (5s, 10s, 1m) → take the 1m average when present.
    [GeneratedRegex(@"(?<a>[0-9]+(?:[.,][0-9]+)?)/[0-9]+(?:[.,][0-9]+)?/[0-9]+(?:[.,][0-9]+)?(?:,\s+(?<b>[0-9]+(?:[.,][0-9]+)?)/[0-9]+(?:[.,][0-9]+)?/[0-9]+(?:[.,][0-9]+)?)?(?:,\s+(?<c>[0-9]+(?:[.,][0-9]+)?)/[0-9]+(?:[.,][0-9]+)?/[0-9]+(?:[.,][0-9]+)?)?")]
    private static partial Regex Paper();

    // Forge: "Overall: Mean tick time: 2.345 ms. Mean TPS: 20.000"
    [GeneratedRegex(@"Overall\s*:\s*Mean tick time:\s*(?<v>[0-9]+(?:[.,][0-9]+)?) ms")]
    private static partial Regex Forge();

    // NeoForge: "Overall: 20,000 TPS (1,035 ms/tick)"
    [GeneratedRegex(@"Overall\s*:\s*[0-9.,]+ TPS \((?<v>[0-9]+(?:[.,][0-9]+)?) ms/tick\)")]
    private static partial Regex NeoForge();

    [GeneratedRegex(@"§.|\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex ColorCodes();

    public static bool IsPaperHeader(string line) => line.Contains("Server tick times", StringComparison.Ordinal);

    public static double? TryParse(string line)
    {
        var clean = ColorCodes().Replace(line, string.Empty);
        foreach (var r in new[] { Forge(), NeoForge() })
        {
            var m = r.Match(clean);
            if (m.Success)
            {
                return Number(m.Groups["v"].Value);
            }
        }

        if (!clean.Contains("TPS", StringComparison.Ordinal) && Paper().Match(clean) is { Success: true } p)
        {
            return Number(p.Groups["c"].Success ? p.Groups["c"].Value : p.Groups["b"].Success ? p.Groups["b"].Value : p.Groups["a"].Value);
        }

        return null;
    }

    private static double? Number(string s) => double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Paper-family servers have a separate "mspt" command; Forge/NeoForge print it with tps.</summary>
    public static string? CommandFor(ServerPlatform p) => p is ServerPlatform.Paper or ServerPlatform.Purpur or ServerPlatform.Leaf or ServerPlatform.Leaves ? "mspt" : null;
}

/// <summary>One minute of server health. Null when the value was not available.</summary>
public sealed record MetricSample(DateTimeOffset Time, double? Tps, double? Mspt, double? Cpu, double? RamMb, int Players);

/// <summary>Per-minute metrics in .packcheck/metrics/yyyy-MM-dd.csv, kept for <see cref="Days"/> days.</summary>
public sealed class MetricsStore(string serverDirectory)
{
    public const int Days = 7;
    private const string Header = "time,tps,mspt,cpu,ram_mb,players";

    public string Folder { get; } = Path.Combine(serverDirectory, ".packcheck", "metrics");

    public void Append(MetricSample s)
    {
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, s.Time.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv");
        var line = string.Join(',', s.Time.ToString("o", CultureInfo.InvariantCulture), F(s.Tps), F(s.Mspt), F(s.Cpu), F(s.RamMb), s.Players.ToString(CultureInfo.InvariantCulture));
        File.AppendAllText(file, (File.Exists(file) ? string.Empty : Header + "\n") + line + "\n", Encoding.UTF8);
    }

    public IReadOnlyList<MetricSample> Load(DateTimeOffset from)
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        var list = new List<MetricSample>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.csv").Order(StringComparer.Ordinal))
        {
            if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < from.LocalDateTime.Date)
            {
                continue;
            }

            foreach (var line in File.ReadLines(file).Skip(1))
            {
                if (Parse(line) is { } s && s.Time >= from)
                {
                    list.Add(s);
                }
            }
        }

        return list;
    }

    /// <summary>Deletes day files older than <see cref="Days"/>.</summary>
    public void Prune(DateTimeOffset now)
    {
        if (!Directory.Exists(Folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(Folder, "*.csv"))
        {
            if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < now.LocalDateTime.Date.AddDays(-Days))
            {
                File.Delete(file);
            }
        }
    }

    public static MetricSample? Parse(string line)
    {
        var p = line.Split(',');
        return p.Length == 6 && DateTimeOffset.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
            ? new MetricSample(t, N(p[1]), N(p[2]), N(p[3]), N(p[4]), int.TryParse(p[5], CultureInfo.InvariantCulture, out var pl) ? pl : 0)
            : null;
    }

    /// <summary>Averages samples into at most <paramref name="buckets"/> points so long ranges stay readable.</summary>
    public static IReadOnlyList<MetricSample> Downsample(IReadOnlyList<MetricSample> samples, int buckets)
    {
        if (samples.Count <= buckets || buckets <= 0)
        {
            return samples;
        }

        var size = (int)Math.Ceiling(samples.Count / (double)buckets);
        return [.. samples.Chunk(size).Select(c => new MetricSample(
            c[0].Time,
            Avg(c.Select(x => x.Tps)), Avg(c.Select(x => x.Mspt)), Avg(c.Select(x => x.Cpu)), Avg(c.Select(x => x.RamMb)),
            c.Max(x => x.Players)))];
    }

    private static double? Avg(IEnumerable<double?> v)
    {
        var l = v.OfType<double>().ToList();
        return l.Count == 0 ? null : l.Average();
    }

    private static string F(double? v) => v is { } d ? d.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty;

    private static double? N(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}

public enum AdviceLevel
{
    Info,
    Warning,
    Critical,
}

/// <summary>One recommendation; <see cref="Key"/> maps to a localized text, <see cref="Args"/> fill it in.</summary>
public sealed record Advice(AdviceLevel Level, string Key, params object[] Args);

/// <summary>Inputs for <see cref="PerformanceAdvisor"/>, gathered from metrics, config files and the log.</summary>
public sealed record PerformanceFacts(
    IReadOnlyList<MetricSample> Samples,
    ServerPlatform Platform,
    int MaxMemoryMb,
    int? ViewDistance,
    int? SimulationDistance,
    int CantKeepUp,
    bool HasSpark,
    bool AikarFlags,
    int? EntityActivationMonsters = null);

/// <summary>Turns measurements and settings into concrete, ranked advice.</summary>
public static class PerformanceAdvisor
{
    public static IReadOnlyList<Advice> Advise(PerformanceFacts f)
    {
        var list = new List<Advice>();
        var tps = f.Samples.Select(s => s.Tps).OfType<double>().ToList();
        var mspt = f.Samples.Select(s => s.Mspt).OfType<double>().ToList();
        var ram = f.Samples.Select(s => s.RamMb).OfType<double>().ToList();
        var cpu = f.Samples.Select(s => s.Cpu).OfType<double>().ToList();
        var lagging = tps.Count > 0 && (tps.Average() < 18 || tps.Count(t => t < 15) > tps.Count / 10);

        if (lagging)
        {
            list.Add(new Advice(tps.Average() < 15 ? AdviceLevel.Critical : AdviceLevel.Warning, "Perf_LowTps", Math.Round(tps.Average(), 1), Math.Round(tps.Min(), 1)));
        }

        if (mspt.Count > 0 && mspt.Max() > 50)
        {
            list.Add(new Advice(AdviceLevel.Warning, "Perf_MsptSpikes", Math.Round(mspt.Max(), 0)));
        }

        if (f.CantKeepUp > 0)
        {
            list.Add(new Advice(f.CantKeepUp > 10 ? AdviceLevel.Warning : AdviceLevel.Info, "Perf_CantKeepUp", f.CantKeepUp));
        }

        if (f.ViewDistance is > 10 && (lagging || f.CantKeepUp > 0))
        {
            list.Add(new Advice(AdviceLevel.Warning, "Perf_ViewDistance", f.ViewDistance.Value));
        }

        if (f.SimulationDistance is > 8 && (lagging || f.CantKeepUp > 0))
        {
            list.Add(new Advice(AdviceLevel.Warning, "Perf_SimulationDistance", f.SimulationDistance.Value));
        }

        if (f.EntityActivationMonsters is > 24 && lagging)
        {
            list.Add(new Advice(AdviceLevel.Info, "Perf_ActivationRange", f.EntityActivationMonsters.Value));
        }

        if (ram.Count > 0 && ram.Max() > f.MaxMemoryMb * 0.95)
        {
            list.Add(new Advice(AdviceLevel.Warning, "Perf_RamFull", Math.Round(ram.Max() / 1024, 1), Math.Round(f.MaxMemoryMb / 1024d, 1)));
        }
        else if (ram.Count > 10 && ram.Max() < f.MaxMemoryMb * 0.4 && f.MaxMemoryMb > 6144)
        {
            list.Add(new Advice(AdviceLevel.Info, "Perf_RamOversized", Math.Round(ram.Max() / 1024, 1), Math.Round(f.MaxMemoryMb / 1024d, 1)));
        }

        if (cpu.Count > 0 && cpu.Average() > 85)
        {
            list.Add(new Advice(AdviceLevel.Warning, "Perf_CpuHigh", Math.Round(cpu.Average(), 0)));
        }

        if (!f.AikarFlags)
        {
            list.Add(new Advice(AdviceLevel.Info, "Perf_Aikar"));
        }

        if (!f.HasSpark && (lagging || f.CantKeepUp > 0))
        {
            list.Add(new Advice(AdviceLevel.Info, "Perf_InstallSpark"));
        }

        if (list.Count == 0 && tps.Count > 0)
        {
            list.Add(new Advice(AdviceLevel.Info, "Perf_AllGood"));
        }

        return [.. list.OrderByDescending(a => a.Level)];
    }
}
