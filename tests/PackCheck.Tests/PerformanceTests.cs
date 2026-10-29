using PackCheck.Servers;
using Xunit;

namespace PackCheck.Tests;

public class PerformanceTests
{
    [Theory]
    [InlineData("§6◴ §a1.2§7/§a0.8§7/§a3.4§7, §a1.1§7/§a0.7§7/§a4.0§7, §a1.3§7/§a0.7§7/§a9.9", 1.3)]
    [InlineData("[12:00:00 INFO]: ◴ 12.5/8.0/40.1", 12.5)]
    [InlineData("[17:32:25] [Server thread/INFO]: ◴ 0,6/0,2/16,2, 0,7/0,2/16,2, 0,8/0,2/16,2", 0.8)]
    [InlineData("[12:00:00] [Server thread/INFO]: Overall: Mean tick time: 2.345 ms. Mean TPS: 20.000", 2.345)]
    [InlineData("[12:00:00] [Server thread/INFO]: Overall: 20,000 TPS (1,035 ms/tick)", 1.035)]
    public void Parses_mspt(string line, double expected) => Assert.Equal(expected, MsptParser.TryParse(line)!.Value, 3);

    [Theory]
    [InlineData("TPS from last 1m, 5m, 15m: 20.0, 20.0, 20.0")]
    [InlineData("[12:00:00 INFO]: Done (3.2s)! For help, type \"help\"")]
    public void Ignores_other_lines(string line) => Assert.Null(MsptParser.TryParse(line));

    [Fact]
    public void Metrics_store_round_trips_and_prunes()
    {
        var dir = Directory.CreateTempSubdirectory("pc-metrics").FullName;
        var store = new MetricsStore(dir);
        var now = DateTimeOffset.Now;
        store.Append(new MetricSample(now.AddDays(-9), 20, null, 10, 1000, 0));
        store.Append(new MetricSample(now.AddMinutes(-2), 19.5, 12.3, 40.5, 2048, 3));
        store.Append(new MetricSample(now.AddMinutes(-1), null, null, 35, 2100, 4));

        var recent = store.Load(now.AddHours(-1));
        Assert.Equal(2, recent.Count);
        Assert.Equal(19.5, recent[0].Tps);
        Assert.Equal(12.3, recent[0].Mspt);
        Assert.Null(recent[1].Tps);
        Assert.Equal(4, recent[1].Players);

        store.Prune(now);
        Assert.Equal(2, store.Load(now.AddDays(-30)).Count);
    }

    [Fact]
    public void Downsample_averages_buckets()
    {
        var t = DateTimeOffset.Now;
        var samples = Enumerable.Range(0, 100).Select(i => new MetricSample(t.AddMinutes(i), i % 2 == 0 ? 20 : 10, null, null, null, i)).ToList();
        var d = MetricsStore.Downsample(samples, 10);
        Assert.Equal(10, d.Count);
        Assert.Equal(15, d[0].Tps);
        Assert.Equal(9, d[0].Players);
    }

    private static PerformanceFacts Facts(double tps, int cantKeepUp = 0, int? view = 10, double ram = 2000, int max = 4096, bool aikar = true, bool spark = true) =>
        new(Enumerable.Range(0, 30).Select(i => new MetricSample(DateTimeOffset.Now.AddMinutes(-i), tps, null, 50, ram, 2)).ToList(),
            ServerPlatform.Paper, max, view, 10, cantKeepUp, spark, aikar, 32);

    [Fact]
    public void Healthy_server_gets_all_good()
    {
        var a = Assert.Single(PerformanceAdvisor.Advise(Facts(20)));
        Assert.Equal("Perf_AllGood", a.Key);
    }

    [Fact]
    public void Lagging_server_gets_ranked_advice()
    {
        var keys = PerformanceAdvisor.Advise(Facts(13, cantKeepUp: 25, view: 16, spark: false)).Select(a => a.Key).ToList();
        Assert.Equal("Perf_LowTps", keys[0]);
        Assert.Contains("Perf_ViewDistance", keys);
        Assert.Contains("Perf_SimulationDistance", keys);
        Assert.Contains("Perf_CantKeepUp", keys);
        Assert.Contains("Perf_InstallSpark", keys);
        Assert.Contains("Perf_ActivationRange", keys);
        Assert.DoesNotContain("Perf_AllGood", keys);
    }

    [Fact]
    public void Memory_advice()
    {
        Assert.Contains(PerformanceAdvisor.Advise(Facts(20, ram: 4000, max: 4096)), a => a.Key == "Perf_RamFull");
        Assert.Contains(PerformanceAdvisor.Advise(Facts(20, ram: 2000, max: 16384)), a => a.Key == "Perf_RamOversized");
        Assert.Contains(PerformanceAdvisor.Advise(Facts(20, aikar: false)), a => a.Key == "Perf_Aikar");
    }
}
