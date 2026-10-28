using PackCheck.Servers;
using PackCheck.Validation;
using Xunit.Abstractions;

namespace PackCheck.Tests;

public class ProxyTests(ITestOutputHelper output)
{
    [Fact]
    public void Apply_WritesVelocityConfigAndSecuresBackends()
    {
        using var proxy = new PackBuilder();
        using var lobby = new PackBuilder();
        using var survival = new PackBuilder();
        lobby.Text("server.properties", "online-mode=true\nserver-port=25565\nmotd=Lobby\n");
        var backends = new List<ProxyBackend>
        {
            new("Lobby", lobby.Root, 30066, ServerPlatform.Paper),
            new("Survival World", survival.Root, 30067, ServerPlatform.Paper),
        };

        var secret = ProxyConfigurator.Apply(proxy.Root, ServerPlatform.Velocity, 25577, "Net", backends, "Lobby");

        var toml = File.ReadAllText(Path.Combine(proxy.Root, "velocity.toml"));
        Assert.Contains("bind = \"0.0.0.0:25577\"", toml, StringComparison.Ordinal);
        Assert.Contains("lobby = \"127.0.0.1:30066\"", toml, StringComparison.Ordinal);
        Assert.Contains("survival-world = \"127.0.0.1:30067\"", toml, StringComparison.Ordinal);
        Assert.Contains("try = [\"lobby\", \"survival-world\"]", toml, StringComparison.Ordinal);
        Assert.Contains("player-info-forwarding-mode = \"modern\"", toml, StringComparison.Ordinal);
        Assert.Equal(secret, File.ReadAllText(Path.Combine(proxy.Root, "forwarding.secret")));
        Assert.Null(Tomlyn.Parsing.SyntaxParser.Parse(toml, "velocity.toml", false).Diagnostics.FirstOrDefault(d => d.Kind == Tomlyn.Syntax.DiagnosticMessageKind.Error));

        var props = ServerProperties.Parse(File.ReadAllText(Path.Combine(lobby.Root, "server.properties")));
        Assert.Equal("false", props["online-mode"]);
        Assert.Equal("127.0.0.1", props["server-ip"]);
        Assert.Equal("30066", props["server-port"]);
        Assert.Equal("Lobby", props["motd"]);
        Assert.Contains(secret, File.ReadAllText(Path.Combine(lobby.Root, "config", "paper-global.yml")), StringComparison.Ordinal);

        Assert.Empty(ProxyConfigurator.Check(ServerPlatform.Velocity, 25577, backends, secret));
    }

    [Fact]
    public void Check_FindsUnsafeOrInconsistentBackends()
    {
        using var a = new PackBuilder();
        using var b = new PackBuilder();
        a.Text("server.properties", "online-mode=true\n");
        b.Text("server.properties", "online-mode=false\nserver-ip=127.0.0.1\n");
        b.Text("config/paper-global.yml", "proxies:\n  velocity:\n    secret: 'other'\n");
        var issues = ProxyConfigurator.Check(ServerPlatform.Velocity, 25577,
            [new("A", a.Root, 30001, ServerPlatform.Paper), new("B", b.Root, 30001, ServerPlatform.Paper)], "the-secret");
        Assert.Contains(issues, i => i.Message.Contains("Port 30001", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Message.StartsWith("A: online-mode", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Message.StartsWith("A: not bound", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Message.StartsWith("B: the Velocity secret", StringComparison.Ordinal));
    }

    [Fact]
    public void ExistingPaperGlobal_KeepsOtherSections()
    {
        var yaml = "_version: 29\nproxies:\n  velocity:\n    enabled: false\n    secret: ''\nunsupported-settings:\n  allow-headless-pistons: false\n";
        var updated = ProxyConfigurator.SetVelocityBlock(yaml, "abc");
        Assert.Contains("_version: 29", updated, StringComparison.Ordinal);
        Assert.Contains("allow-headless-pistons: false", updated, StringComparison.Ordinal);
        Assert.Contains("secret: 'abc'", updated, StringComparison.Ordinal);
        Assert.Contains("enabled: true", updated, StringComparison.Ordinal);
        new YamlDotNet.RepresentationModel.YamlStream().Load(new StringReader(updated));
    }

    [Fact]
    public void Bungee_ConfigAndSpigotFlag()
    {
        var yaml = ProxyConfigurator.BungeeYaml(25577, "Net", [new("Lobby", ".", 30066, ServerPlatform.Spigot)], ["Lobby"]);
        new YamlDotNet.RepresentationModel.YamlStream().Load(new StringReader(yaml));
        Assert.Contains("ip_forward: true", yaml, StringComparison.Ordinal);
        Assert.Contains("address: 127.0.0.1:30066", yaml, StringComparison.Ordinal);
        Assert.Contains("bungeecord: true", ProxyConfigurator.SetBungeeFlag("settings:\n  bungeecord: false\n"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ServerPlatform.Velocity, "4.3.1", 25)]
    [InlineData(ServerPlatform.Velocity, "3.4.0", 21)]
    [InlineData(ServerPlatform.Paper, "1.16.5", 8)]
    public void RequiredJava_ByPlatform(ServerPlatform p, string v, int java) => Assert.Equal(java, PlatformInfo.RequiredJava(p, v));

    /// <summary>Real network: Velocity in front of Paper; a status query on the proxy port must reach Paper.</summary>
    [Fact]
    [Trait("Category", "EndToEnd")]
    public async Task Velocity_InFrontOfPaper_EndToEnd()
    {
        var root = Environment.GetEnvironmentVariable("PACKCHECK_E2E_DIR");
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var sources = new ServerSources(http);
        var java = new JavaRuntimes(http);
        var known = await java.DetectAsync(CancellationToken.None);
        var creator = new ServerCreator(sources, http, java);
        var proxyDir = Path.Combine(root, "pc-e2e-velocity");
        var paperDir = Path.Combine(root, "pc-e2e-backend");
        PackCheck.Core.FileSystem.DeleteDirectory(proxyDir);
        PackCheck.Core.FileSystem.DeleteDirectory(paperDir);
        try
        {
            var paperVersion = (await sources.GetVersionsAsync(ServerPlatform.Paper, CancellationToken.None))[0];
            var velocityVersion = (await sources.GetVersionsAsync(ServerPlatform.Velocity, CancellationToken.None))[0];
            var paperJava = await java.EnsureAsync(PlatformInfo.RequiredJava(ServerPlatform.Paper, paperVersion), known, null, CancellationToken.None);
            var proxyJava = await java.EnsureAsync(PlatformInfo.RequiredJava(ServerPlatform.Velocity, velocityVersion), known, null, CancellationToken.None);
            var paper = await creator.CreateAsync(new("pc-e2e-backend", root, ServerPlatform.Paper, paperVersion, paperJava.JavaExe, 2048, 30170, null, true), null, CancellationToken.None);
            var velocity = await creator.CreateAsync(new("pc-e2e-velocity", root, ServerPlatform.Velocity, velocityVersion, proxyJava.JavaExe, 512, 30177, null, true), null, CancellationToken.None);

            ProxyConfigurator.Apply(velocity.Directory, ServerPlatform.Velocity, 30177, "PackCheck test", [new("lobby", paper.Directory, 30170, ServerPlatform.Paper)], "lobby");

            await using var backend = new ServerProcess(paper with { UseAikarFlags = false });
            await using var proxy = new ServerProcess(velocity with { UseAikarFlags = false, MinMemoryMb = 256 });
            var lines = new List<string>();
            backend.Output += (_, l) => { lock (lines) { lines.Add("[paper] " + l); } };
            proxy.Output += (_, l) => { lock (lines) { lines.Add("[velocity] " + l); } };
            var backendUp = new TaskCompletionSource();
            var proxyUp = new TaskCompletionSource();
            backend.StateChanged += (_, s) => { if (s == ServerState.Running) { backendUp.TrySetResult(); } };
            proxy.StateChanged += (_, s) => { if (s == ServerState.Running) { proxyUp.TrySetResult(); } };

            await backend.StartAsync();
            Assert.True(await Task.WhenAny(backendUp.Task, Task.Delay(TimeSpan.FromMinutes(4))) == backendUp.Task, "Paper did not start");
            await proxy.StartAsync();
            var ok = await Task.WhenAny(proxyUp.Task, Task.Delay(TimeSpan.FromMinutes(2))) == proxyUp.Task;
            lock (lines)
            {
                foreach (var l in lines.Where(l => l.StartsWith("[velocity]", StringComparison.Ordinal)).TakeLast(12))
                {
                    output.WriteLine(l);
                }
            }

            Assert.True(ok, "Velocity did not start");

            PingResult? viaProxy = null;
            for (var i = 0; i < 10 && viaProxy is null; i++)
            {
                await Task.Delay(1000);
                viaProxy = await ServerListPing.QueryAsync("127.0.0.1", 30177, TimeSpan.FromSeconds(3), CancellationToken.None);
            }

            output.WriteLine($"via proxy: {(viaProxy is null ? "no answer" : $"{viaProxy.Online}/{viaProxy.Max}, version '{viaProxy.Version}', {viaProxy.LatencyMs} ms")}");
            Assert.NotNull(viaProxy);

            // The backend must not be reachable from other machines: it listens on loopback only.
            var props = ServerProperties.Parse(File.ReadAllText(Path.Combine(paper.Directory, "server.properties")));
            Assert.Equal("127.0.0.1", props["server-ip"]);

            await proxy.StopAsync(TimeSpan.FromSeconds(30));
            await backend.StopAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(ServerState.Stopped, proxy.State);
            Assert.Equal(ServerState.Stopped, backend.State);
        }
        finally
        {
            PackCheck.Core.FileSystem.DeleteDirectory(proxyDir);
            PackCheck.Core.FileSystem.DeleteDirectory(paperDir);
            output.WriteLine($"cleaned: {!Directory.Exists(proxyDir) && !Directory.Exists(paperDir)}");
        }
    }
}
