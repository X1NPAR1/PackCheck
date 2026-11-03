using PackCheck.Servers;

namespace PackCheck.Tests;

public class PlayitTests
{
    [Theory]
    [InlineData("Open this link to finish setting up playit:", PlayitSignal.None, null)]
    [InlineData("https://playit.gg/claim/1a2b3c4d5e", PlayitSignal.ClaimUrl, "https://playit.gg/claim/1a2b3c4d5e")]
    [InlineData("Approve this program in your browser: https://playit.gg/claim/abcdef0123", PlayitSignal.ClaimUrl, "https://playit.gg/claim/abcdef0123")]
    [InlineData("\u001b[32mhttps://playit.gg/claim/00ff00ff00\u001b[0m", PlayitSignal.ClaimUrl, "https://playit.gg/claim/00ff00ff00")]
    [InlineData("my-world.gl.joinmc.link => 127.0.0.1:25565 (minecraft-java)", PlayitSignal.Address, "my-world.gl.joinmc.link")]
    [InlineData("tunnel: Hello-There.at.ply.gg:41234", PlayitSignal.Address, "hello-there.at.ply.gg:41234")]
    [InlineData("Program approved. Finishing setup...", PlayitSignal.Claimed, null)]
    [InlineData("2026-11-03 10:00:00  INFO playitd::daemon: playit connected; tunnels loaded agent_id=x", PlayitSignal.Connected, null)]
    [InlineData("Setup was not approved in the browser.", PlayitSignal.Error, "Setup was not approved in the browser.")]
    [InlineData("2026-11-03 10:00:00  WARN something unrelated happened", PlayitSignal.None, null)]
    [InlineData("", PlayitSignal.None, null)]
    public void Parse_RecognizesAgentLines(string line, PlayitSignal signal, string? value)
    {
        var parsed = PlayitOutput.Parse(line);
        Assert.Equal(signal, parsed.Signal);
        Assert.Equal(value, parsed.Value);
    }

    [Fact]
    public void Assets_AreOfficialHttpsReleaseFilesWithSha256()
    {
        foreach (var asset in new[] { PlayitAgent.Daemon, PlayitAgent.Cli })
        {
            Assert.StartsWith("https://github.com/playit-cloud/playit-agent/releases/download/", asset.Url.ToString(), StringComparison.Ordinal);
            Assert.Equal(64, asset.Sha256.Length);
        }
    }

    [Fact]
    public async Task Lifecycle_StartsDaemonThenCli_DetectsClaim_AndStopKillsBoth()
    {
        var launcher = new FakeLauncher { PipeAfterChecks = 2 };
        var downloads = 0;
        using var tunnel = new PlayitTunnel("C:\\tools", launcher, _ => { downloads++; return Task.CompletedTask; });

        await tunnel.StartAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(1, downloads);
        Assert.Equal(2, launcher.Started.Count);
        Assert.EndsWith(PlayitAgent.Daemon.FileName, launcher.Started[0].Exe, StringComparison.Ordinal);
        Assert.Contains("--secret-path", launcher.Started[0].Args);
        Assert.EndsWith(PlayitAgent.Cli.FileName, launcher.Started[1].Exe, StringComparison.Ordinal);
        Assert.Contains("--stdout", launcher.Started[1].Args);
        Assert.Equal(PlayitState.Starting, tunnel.State);

        launcher.Started[1].Process.Emit("Open this link to finish setting up playit:");
        launcher.Started[1].Process.Emit("https://playit.gg/claim/0123456789");
        Assert.Equal(PlayitState.WaitingForClaim, tunnel.State);
        Assert.Equal("https://playit.gg/claim/0123456789", tunnel.ClaimUrl);
        Assert.False(tunnel.OutputUnrecognized);

        launcher.Started[1].Process.Emit("Program approved. Finishing setup...");
        launcher.Started[1].Process.Emit("name.joinmc.link => 127.0.0.1:25565");
        Assert.Equal(PlayitState.Running, tunnel.State);
        Assert.Equal("name.joinmc.link", tunnel.Address);

        tunnel.Stop();
        Assert.Equal(PlayitState.Stopped, tunnel.State);
        Assert.All(launcher.Started, s => Assert.True(s.Process.Killed && s.Process.Disposed));
    }

    [Fact]
    public async Task UnknownOutput_IsFlagged_AndKeptInLog()
    {
        var launcher = new FakeLauncher();
        using var tunnel = new PlayitTunnel("C:\\tools", launcher);
        await tunnel.StartAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        launcher.Started[1].Process.Emit("Something completely new in playit 9.0");
        Assert.True(tunnel.OutputUnrecognized);
        Assert.Contains("Something completely new in playit 9.0", tunnel.LogLines);
        Assert.Null(tunnel.ClaimUrl);
    }

    [Fact]
    public async Task DaemonExitBeforePipe_Fails_AndCleansUp()
    {
        var launcher = new FakeLauncher { PipeAfterChecks = int.MaxValue, ExitImmediately = true };
        using var tunnel = new PlayitTunnel("C:\\tools", launcher);
        await tunnel.StartAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(PlayitState.Failed, tunnel.State);
        Assert.NotNull(tunnel.Error);
        Assert.Single(launcher.Started);
        Assert.True(launcher.Started[0].Process.Disposed);
    }

    [Fact]
    public async Task UnexpectedExit_MarksFailed_AndKillsTheOther()
    {
        var launcher = new FakeLauncher();
        using var tunnel = new PlayitTunnel("C:\\tools", launcher);
        await tunnel.StartAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        launcher.Started[1].Process.Exit();
        Assert.Equal(PlayitState.Failed, tunnel.State);
        Assert.True(launcher.Started[0].Process.Killed);
    }

    [Fact]
    public async Task Log_IsBounded()
    {
        var launcher = new FakeLauncher();
        using var tunnel = new PlayitTunnel("C:\\tools", launcher);
        await tunnel.StartAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        for (var i = 0; i < PlayitTunnel.MaxLogLines + 50; i++)
        {
            launcher.Started[0].Process.Emit($"line {i}");
        }

        Assert.Equal(PlayitTunnel.MaxLogLines, tunnel.LogLines.Count);
        Assert.Equal($"line {PlayitTunnel.MaxLogLines + 49}", tunnel.LogLines[^1]);
    }

    private sealed class FakeLauncher : IPlayitLauncher
    {
        private int _checks;

        public int PipeAfterChecks { get; init; }

        public bool ExitImmediately { get; init; }

        public List<(string Exe, IReadOnlyList<string> Args, FakeProcess Process)> Started { get; } = [];

        public IPlayitProcess Start(string exe, IReadOnlyList<string> args)
        {
            var p = new FakeProcess { HasExited = ExitImmediately };
            Started.Add((exe, args, p));
            return p;
        }

        public bool PipeExists(string pipeName) => ++_checks > PipeAfterChecks;
    }

    private sealed class FakeProcess : IPlayitProcess
    {
        public event Action<string>? Output;

        public event Action? Exited;

        public bool HasExited { get; set; }

        public bool Killed { get; private set; }

        public bool Disposed { get; private set; }

        public void Emit(string line) => Output?.Invoke(line);

        public void Exit()
        {
            HasExited = true;
            Exited?.Invoke();
        }

        public void Kill()
        {
            Killed = true;
            Exit();
        }

        public void Dispose() => Disposed = true;
    }
}
