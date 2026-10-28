using PackCheck.Servers;

namespace PackCheck.Tests;

public class TelemetryTests
{
    [Theory]
    [InlineData("[21:30:16] [Server thread/INFO]: Starting minecraft server version 26.3", "21:30:16", "Server thread", LogLevel.Info, "Starting minecraft server version 26.3")]
    [InlineData("[12:00:01] [Server thread/WARN]: Can't keep up!", "12:00:01", "Server thread", LogLevel.Warn, "Can't keep up!")]
    [InlineData("[12:00:01 ERROR]: Could not load 'plugins/x.jar'", "12:00:01", null, LogLevel.Error, "Could not load 'plugins/x.jar'")]
    [InlineData("[09:15:44] [main/INFO] [net.minecraftforge/]: Forge mod loading", "09:15:44", "main", LogLevel.Info, "Forge mod loading")]
    public void ParsesLogLines(string raw, string time, string? src, LogLevel level, string msg)
    {
        var l = LogParser.Parse(raw);
        Assert.Equal(time, l.Time);
        Assert.Equal(src, l.Source);
        Assert.Equal(level, l.Level);
        Assert.Equal(msg, l.Message);
    }

    [Fact]
    public void UnstructuredLines_AreClassified()
    {
        Assert.Equal(LogLevel.Warn, LogParser.Parse("WARNING: A terminally deprecated method").Level);
        Assert.Equal(LogLevel.Error, LogParser.Parse("\tat net.minecraft.Foo.bar(Foo.java:1)").Level);
        Assert.Equal(LogLevel.Plain, LogParser.Parse("Starting net.fabricmc.loader").Level);
    }

    [Theory]
    [InlineData("[12:00:00 INFO]: TPS from last 1m, 5m, 15m: 19.98, 20.0, 20.0", 19.98)]
    [InlineData("§6TPS from last 1m, 5m, 15m: §a*20.0, §a20.0, §a20.0", 20.0)]
    [InlineData("Overall: Mean tick time: 2.345 ms. Mean TPS: 17.500", 17.5)]
    [InlineData("[22:19:26] [Server thread/INFO] [minecraft/MinecraftServer]: System chat: Dim minecraft:overworld (minecraft:overworld): Mean tick time: 0,728 ms. Mean TPS: 20,00", 20.0)]
    [InlineData("[22:18:05] [Server thread/INFO] [minecraft/MinecraftServer]: System chat: Overworld: 19,500 TPS (1,035 ms/tick)", 19.5)]
    [InlineData("[22:18:37 INFO]: TPS from last 5s, 1m, 5m, 15m: 18,4, 20,0, 20,0, 20,0", 18.4)]
    [InlineData("[22:12:50 INFO]: \u001b[38;5;3mTPS from last 1m, 5m, 15m: \u001b[0m\u001b[38;5;10m20,0\u001b[0m, \u001b[38;5;10m19,8\u001b[0m", 20.0)]
    public void ParsesTps(string line, double tps) => Assert.Equal(tps, TpsParser.TryParse(line));

    [Fact]
    public void NonTpsLine_IsNull() => Assert.Null(TpsParser.TryParse("[12:00:00] [Server thread/INFO]: Done (3.2s)!"));

    [Theory]
    [InlineData(20, 0)]
    [InlineData(18.5, 1)]
    [InlineData(16, 2)]
    [InlineData(12, 3)]
    [InlineData(5, 4)]
    public void TpsHealth(double tps, int level) => Assert.Equal(level, Health.Tps(tps));

    [Theory]
    [InlineData(10, 0)]
    [InlineData(55, 1)]
    [InlineData(70, 2)]
    [InlineData(85, 3)]
    [InlineData(99, 4)]
    public void CpuHealth(double cpu, int level) => Assert.Equal(level, Health.Cpu(cpu));

    [Fact]
    public void ParsesStatusJson()
    {
        var r = ServerListPing.Parse("""{"version":{"name":"1.21.4","protocol":769},"players":{"max":20,"online":2,"sample":[{"name":"Steve","id":"x"},{"name":"Alex","id":"y"}]},"description":"hi"}""", 3)!;
        Assert.Equal(2, r.Online);
        Assert.Equal(20, r.Max);
        Assert.Equal(["Steve", "Alex"], r.Sample);
        Assert.Equal(3, r.LatencyMs);
    }

    [Fact]
    public void VarIntEncoding()
    {
        var b = new List<byte>();
        ServerListPing.WriteVarInt(b, 300);
        Assert.Equal([0xAC, 0x02], b);
        b.Clear();
        ServerListPing.WriteVarInt(b, -1);
        Assert.Equal([0xFF, 0xFF, 0xFF, 0xFF, 0x0F], b);
    }

    [Fact]
    public async Task ClosedPort_ReturnsNull() =>
        Assert.Null(await ServerListPing.QueryAsync("127.0.0.1", 1, TimeSpan.FromSeconds(2), CancellationToken.None));
}
