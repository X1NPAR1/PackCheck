using System.Text.Json;
using PackCheck.Servers;
using Xunit.Abstractions;

namespace PackCheck.Tests;

/// <summary>
/// Starts a real server through ServerProcess. Opt-in: set PACKCHECK_REAL_SERVER to a server folder that
/// contains packcheck-test.json ({"jar": "...", "java": "...", "platform": "Fabric", "argsFile": null}).
/// </summary>
public class RealServerTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Network")]
    public async Task ConsoleStreamsOutput_AcceptsCommands_AndStops()
    {
        var dir = Environment.GetEnvironmentVariable("PACKCHECK_REAL_SERVER");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        using var cfg = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "packcheck-test.json")));
        var r = cfg.RootElement;
        var def = new ServerDefinition
        {
            Id = "test",
            Name = Path.GetFileName(dir),
            Directory = dir,
            Platform = Enum.Parse<ServerPlatform>(r.GetProperty("platform").GetString()!),
            MinecraftVersion = "test",
            JarFile = r.GetProperty("jar").GetString()!,
            ArgsFile = r.TryGetProperty("argsFile", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null,
            JavaPath = r.GetProperty("java").GetString()!,
            MaxMemoryMb = 2048,
        };

        var lines = new List<string>();
        var running = new TaskCompletionSource();
        var listReply = new TaskCompletionSource();
        await using var server = new ServerProcess(def);
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

        await server.StartAsync();
        var started = await Task.WhenAny(running.Task, Task.Delay(TimeSpan.FromMinutes(4))) == running.Task;
        lock (lines)
        {
            output.WriteLine($"--- {lines.Count} lines, state {server.State}");
            foreach (var l in lines.TakeLast(25))
            {
                output.WriteLine(l);
            }
        }

        Assert.True(started, "Server never reported Done.");
        await server.SendCommandAsync("list");
        Assert.True(await Task.WhenAny(listReply.Task, Task.Delay(TimeSpan.FromSeconds(20))) == listReply.Task, "No reply to 'list'.");
        await server.StopAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(ServerState.Stopped, server.State);
    }
}
