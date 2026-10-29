using PackCheck.Core;
using PackCheck.Servers;
using Xunit.Abstractions;

namespace PackCheck.Tests;

/// <summary>
/// Creates, starts and stops real servers. Opt-in: set PACKCHECK_E2E_DIR (e.g. D:\MCSERVER). Each platform gets its
/// own folder "pc-e2e-&lt;platform&gt;" which is deleted afterwards, also when the test fails.
/// </summary>
public class EndToEndServerTests(ITestOutputHelper output)
{
    [Theory]
    [Trait("Category", "EndToEnd")]
    [InlineData(ServerPlatform.Paper)]
    [InlineData(ServerPlatform.Purpur)]
    [InlineData(ServerPlatform.Vanilla)]
    [InlineData(ServerPlatform.Fabric)]
    [InlineData(ServerPlatform.Forge)]
    [InlineData(ServerPlatform.NeoForge)]
    [InlineData(ServerPlatform.Quilt)]
    [InlineData(ServerPlatform.Spigot)]
    [InlineData(ServerPlatform.Folia)]
    [InlineData(ServerPlatform.Mohist)]
    [InlineData(ServerPlatform.CraftBukkit)]
    [InlineData(ServerPlatform.Leaf)]
    [InlineData(ServerPlatform.Leaves)]
    [InlineData(ServerPlatform.SpongeVanilla)]
    [InlineData(ServerPlatform.Youer)]
    [InlineData(ServerPlatform.Arclight)]
    [InlineData(ServerPlatform.BungeeCord)]
    public async Task CreateStartCommandStop(ServerPlatform platform)
    {
        var root = Environment.GetEnvironmentVariable("PACKCHECK_E2E_DIR");
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PackCheck-tests/2.0");
        var sources = new ServerSources(http);
        var versions = await sources.GetVersionsAsync(platform, CancellationToken.None);
        var version = versions[0];
        var java = new JavaRuntimes(http);
        var runtime = await java.EnsureAsync(PlatformInfo.RequiredJava(platform, version), await java.DetectAsync(CancellationToken.None), null, CancellationToken.None);
        var name = $"pc-e2e-{platform.ToString().ToLowerInvariant()}";
        var dir = Path.Combine(root, name);
        PackCheck.Core.FileSystem.DeleteDirectory(dir);

        var started = DateTime.Now;
        try
        {
            var creator = new ServerCreator(sources, http, java);
            var port = 25600 + (int)platform;
            var def = await creator.CreateAsync(new ServerCreator.Request(name, root, platform, version, runtime.JavaExe, 2048, port, null, AcceptEula: true), null, CancellationToken.None);
            output.WriteLine($"{platform} {version}: created in {(DateTime.Now - started).TotalSeconds:0}s with {runtime.Display} -> jar {def.JarFile}, args {def.ArgsFile ?? "-"}");

            var lines = new List<string>();
            var running = new TaskCompletionSource();
            var listReply = new TaskCompletionSource();
            await using var server = new ServerProcess(def with { MaxMemoryMb = 2048, UseAikarFlags = false });
            server.Output += (_, l) =>
            {
                lock (lines)
                {
                    lines.Add(l);
                }

                if (l.Contains("players online", StringComparison.OrdinalIgnoreCase))
                {
                    listReply.TrySetResult();
                }
            };
            server.StateChanged += (_, s) =>
            {
                if (s == ServerState.Running)
                {
                    running.TrySetResult();
                }
            };

            var t0 = DateTime.Now;
            await server.StartAsync();
            var ok = await Task.WhenAny(running.Task, Task.Delay(TimeSpan.FromMinutes(6))) == running.Task;
            lock (lines)
            {
                output.WriteLine($"{platform}: {(ok ? "Done" : "NOT started")} after {(DateTime.Now - t0).TotalSeconds:0}s, {lines.Count} console lines");
                if (!ok)
                {
                    foreach (var l in lines.TakeLast(30))
                    {
                        output.WriteLine("  " + l);
                    }
                }
            }

            Assert.True(ok, $"{platform} {version} did not finish starting.");

            PingResult? ping = null;
            for (var attempt = 0; attempt < 10 && ping is null; attempt++)
            {
                await Task.Delay(1000);
                ping = await ServerListPing.QueryAsync("127.0.0.1", port, TimeSpan.FromSeconds(3), CancellationToken.None);
            }
            output.WriteLine($"{platform}: status ping {(ping is null ? "no answer" : $"{ping.Online}/{ping.Max} players, {ping.LatencyMs} ms, version '{ping.Version}'")}");
            Assert.NotNull(ping);

            await server.SendCommandAsync(ProxyConfigurator.IsProxy(platform) ? "glist" : "list");
            Assert.True(await Task.WhenAny(listReply.Task, Task.Delay(TimeSpan.FromSeconds(30))) == listReply.Task, "No reply to 'list'.");

            if (TpsParser.CommandFor(platform, hasSpark: false) is { } tpsCommand)
            {
                var before = lines.Count;
                await server.SendCommandAsync(tpsCommand);
                await Task.Delay(3000);
                double? tps;
                lock (lines)
                {
                    foreach (var l in lines.Skip(before))
                    {
                        output.WriteLine("  tps> " + l);
                    }

                    tps = lines.Skip(before).Select(TpsParser.TryParse).FirstOrDefault(v => v is not null);
                }

                output.WriteLine($"{platform}: '{tpsCommand}' -> {(tps is null ? "no TPS line" : tps.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))}");
            }

            await server.StopAsync(TimeSpan.FromSeconds(90));
            Assert.Equal(ServerState.Stopped, server.State);
            output.WriteLine($"{platform}: stopped cleanly");
        }
        finally
        {
            try
            {
                PackCheck.Core.FileSystem.DeleteDirectory(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                output.WriteLine($"{platform}: cleanup failed: {ex.Message}");
            }

            output.WriteLine($"{platform}: folder removed: {!Directory.Exists(dir)}");
        }
    }
}
