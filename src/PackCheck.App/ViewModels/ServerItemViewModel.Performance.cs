using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackCheck.App.Controls;
using PackCheck.App.Infrastructure;
using PackCheck.Servers;
using PackCheck.Validation;

namespace PackCheck.App.ViewModels;

public sealed record AdviceRow(AdviceLevel Level, string Text)
{
    public bool IsCritical => Level == AdviceLevel.Critical;
    public bool IsWarning => Level == AdviceLevel.Warning;
}

/// <summary>Performance tab: per-minute history (1 h / 24 h / 7 d), summary, advice and spark profiling.</summary>
public sealed partial class ServerItemViewModel
{
    public const int PerformanceTab = 9;

    private double? _lastTps;
    private double? _lastMspt;
    private double? _lastCpuPct;
    private double? _lastRamMb;
    private DateTime _msptRequestedAt = DateTime.MinValue;
    private MetricsStore? _metrics;

    private MetricsStore Metrics => _metrics ??= new MetricsStore(Definition.Directory);

    public static IReadOnlyList<string> PerfRanges => [Loc.T("Perf_Range1h"), Loc.T("Perf_Range24h"), Loc.T("Perf_Range7d")];

    [ObservableProperty]
    private int _perfRange;

    [ObservableProperty]
    private IReadOnlyList<ChartPoint> _tpsPoints = [];

    [ObservableProperty]
    private IReadOnlyList<ChartPoint> _msptPoints = [];

    [ObservableProperty]
    private IReadOnlyList<ChartPoint> _cpuPoints = [];

    [ObservableProperty]
    private IReadOnlyList<ChartPoint> _ramPoints = [];

    [ObservableProperty]
    private IReadOnlyList<ChartPoint> _playerPoints = [];

    [ObservableProperty]
    private string _perfTps = "–";

    [ObservableProperty]
    private string _perfTpsMin = "–";

    [ObservableProperty]
    private string _perfMspt = "–";

    [ObservableProperty]
    private string _perfRamPeak = "–";

    [ObservableProperty]
    private string _perfPlayersPeak = "–";

    [ObservableProperty]
    private string _perfUptime = "–";

    [ObservableProperty]
    private string? _sparkStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSparkReport))]
    private string? _sparkReportUrl;

    public bool HasSparkReport => SparkReportUrl is not null;

    public ObservableCollection<AdviceRow> Advice { get; } = [];

    /// <summary>spark as a jar, or built into Paper (1.21+), which creates plugins/spark on first start.</summary>
    public bool HasSpark => SparkJar() is not null
        || (Definition.Platform is ServerPlatform.Paper or ServerPlatform.Purpur or ServerPlatform.Leaf or ServerPlatform.Leaves
            && Directory.Exists(Path.Combine(Definition.Directory, "plugins", "spark")));

    public double RamLimitMb => Definition.MaxMemoryMb;

    partial void OnPerfRangeChanged(int value) => RefreshPerformance();

    /// <summary>Called every second while running; writes one sample per minute.</summary>
    private void RecordMetrics()
    {
        if (_tick % 60 != 30)
        {
            return;
        }

        try
        {
            Metrics.Append(new MetricSample(DateTimeOffset.Now, _lastTps, _lastMspt, _lastCpuPct, _lastRamMb, Players.Count));
            CheckLowTpsForDiscord();
            if (_tick % 3600 == 30)
            {
                Metrics.Prune(DateTimeOffset.Now);
            }

            if (Tab == PerformanceTab)
            {
                RefreshPerformance();
            }
        }
        catch (IOException)
        {
        }
    }

    [RelayCommand]
    public void RefreshPerformance()
    {
        var hours = PerfRange switch { 1 => 24, 2 => 24 * 7, _ => 1 };
        var all = Metrics.Load(DateTimeOffset.Now.AddHours(-hours));
        var shown = MetricsStore.Downsample(all, 240);
        TpsPoints = [.. shown.Select(s => new ChartPoint(s.Time, s.Tps))];
        MsptPoints = [.. shown.Select(s => new ChartPoint(s.Time, s.Mspt))];
        CpuPoints = [.. shown.Select(s => new ChartPoint(s.Time, s.Cpu))];
        RamPoints = [.. shown.Select(s => new ChartPoint(s.Time, s.RamMb is { } r ? r / 1024 : null))];
        PlayerPoints = [.. shown.Select(s => new ChartPoint(s.Time, s.Players))];

        var c = Loc.Instance.Culture;
        var tps = all.Select(s => s.Tps).OfType<double>().ToList();
        var mspt = all.Select(s => s.Mspt).OfType<double>().ToList();
        var ram = all.Select(s => s.RamMb).OfType<double>().ToList();
        PerfTps = tps.Count > 0 ? tps.Average().ToString("0.0", c) : "–";
        PerfTpsMin = tps.Count > 0 ? tps.Min().ToString("0.0", c) : "–";
        PerfMspt = mspt.Count > 0 ? mspt.Average().ToString("0.0", c) + " ms" : "–";
        PerfRamPeak = ram.Count > 0 ? (ram.Max() / 1024).ToString("0.0", c) + " GB" : "–";
        PerfPlayersPeak = all.Count > 0 ? all.Max(s => s.Players).ToString(c) : "–";
        PerfUptime = Loc.F("Perf_Minutes", all.Count);

        var props = ReadProperties();
        var facts = new PerformanceFacts(
            all,
            Definition.Platform,
            Definition.MaxMemoryMb,
            props.TryGetValue("view-distance", out var vd) && int.TryParse(vd, out var v) ? v : null,
            props.TryGetValue("simulation-distance", out var sd) && int.TryParse(sd, out var sim) ? sim : null,
            CountCantKeepUp(),
            HasSpark,
            Definition.UseAikarFlags,
            SpigotActivationMonsters());

        Advice.Clear();
        foreach (var a in PerformanceAdvisor.Advise(facts))
        {
            Advice.Add(new AdviceRow(a.Level, Loc.F(a.Key, a.Args)));
        }

        OnPropertyChanged(nameof(HasSpark));
        OnPropertyChanged(nameof(RamLimitMb));
    }

    private Dictionary<string, string> ReadProperties()
    {
        var file = Path.Combine(Definition.Directory, "server.properties");
        return File.Exists(file) ? ServerProperties.Parse(File.ReadAllText(file)) : [];
    }

    private int? SpigotActivationMonsters()
    {
        var file = Path.Combine(Definition.Directory, "spigot.yml");
        return File.Exists(file) && int.TryParse(YamlPatcher.Get(File.ReadAllText(file), "world-settings.default.entity-activation-range.monsters"), out var n) ? n : null;
    }

    /// <summary>"Can't keep up!" warnings in the current log, plus those seen in the console since the app started.</summary>
    private int CountCantKeepUp()
    {
        var log = Path.Combine(Definition.Directory, "logs", "latest.log");
        try
        {
            if (File.Exists(log))
            {
                using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                var n = 0;
                while (reader.ReadLine() is { } line)
                {
                    if (line.Contains("Can't keep up!", StringComparison.Ordinal))
                    {
                        n++;
                    }
                }

                return n;
            }
        }
        catch (IOException)
        {
        }

        return _all.Count(e => e.Message.Contains("Can't keep up!", StringComparison.Ordinal));
    }

    private string? SparkJar()
    {
        foreach (var dir in new[] { "plugins", "mods" })
        {
            var full = Path.Combine(Definition.Directory, dir);
            if (Directory.Exists(full) && Directory.EnumerateFiles(full, "spark*.jar").FirstOrDefault() is { } jar)
            {
                return jar;
            }
        }

        return null;
    }

    /// <summary>Runs spark's 30-second profiler and catches the report link it prints.</summary>
    [RelayCommand]
    private async Task RunProfilerAsync()
    {
        if (!IsRunning || !HasSpark)
        {
            return;
        }

        SparkReportUrl = null;
        SparkStatus = Loc.T("Perf_Profiling");
        var link = new TaskCompletionSource<string>();
        void Watch(object? sender, string line)
        {
            var i = line.IndexOf("https://spark.lucko.me/", StringComparison.Ordinal);
            if (i >= 0)
            {
                link.TrySetResult(new string([.. line[i..].TakeWhile(ch => !char.IsWhiteSpace(ch))]));
            }
        }

        _process.Output += Watch;
        try
        {
            await _process.SendCommandAsync("spark profiler start --timeout 30").ConfigureAwait(true);
            var done = await Task.WhenAny(link.Task, Task.Delay(TimeSpan.FromSeconds(75))).ConfigureAwait(true);
            if (done == link.Task)
            {
                SparkReportUrl = link.Task.Result;
                SparkStatus = Loc.T("Perf_ProfileReady");
            }
            else
            {
                SparkStatus = Loc.T("Perf_ProfileNoLink");
            }
        }
        finally
        {
            _process.Output -= Watch;
        }
    }

    [RelayCommand]
    private void OpenSparkReport()
    {
        if (SparkReportUrl is { } url && url.StartsWith("https://spark.lucko.me/", StringComparison.Ordinal))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
    }
}
