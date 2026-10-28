using System.Diagnostics;
using System.Text;

namespace PackCheck.Servers;

public enum ServerState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Crashed,
}

/// <summary>Runs one Minecraft server as a child process inside a Job Object with a live console.</summary>
public sealed class ServerProcess : IAsyncDisposable
{
    private ServerDefinition _def;
    private Process? _process;
    private JobObject? _job;

    public ServerProcess(ServerDefinition definition) => _def = definition;

    public event EventHandler<string>? Output;

    /// <summary>Applies changed settings (e.g. a newly installed Java) for the next start.</summary>
    public void UpdateDefinition(ServerDefinition definition) => _def = definition;

    public event EventHandler<ServerState>? StateChanged;

    public ServerState State { get; private set; } = ServerState.Stopped;

    public int? ProcessId => _process?.HasExited == false ? _process.Id : null;

    public Process? Process => _process;

    public static IReadOnlyList<string> BuildArguments(ServerDefinition def)
    {
        var args = new List<string> { $"-Xms{def.MinMemoryMb}M", $"-Xmx{def.MaxMemoryMb}M" };
        if (def.UseAikarFlags)
        {
            args.AddRange(
            [
                "-XX:+UseG1GC", "-XX:+ParallelRefProcEnabled", "-XX:MaxGCPauseMillis=200", "-XX:+UnlockExperimentalVMOptions",
                "-XX:+DisableExplicitGC", "-XX:+AlwaysPreTouch", "-XX:G1NewSizePercent=30", "-XX:G1MaxNewSizePercent=40",
                "-XX:G1HeapRegionSize=8M", "-XX:G1ReservePercent=20", "-XX:G1HeapWastePercent=5", "-XX:G1MixedGCCountTarget=4",
                "-XX:InitiatingHeapOccupancyPercent=15", "-XX:G1MixedGCLiveThresholdPercent=90", "-XX:G1RSetUpdatingPauseTimePercent=5",
                "-XX:SurvivorRatio=32", "-XX:+PerfDisableSharedMem", "-XX:MaxTenuringThreshold=1",
            ]);
        }

        if (def.ArgsFile is not null)
        {
            // Modern Forge / NeoForge: java [jvm args] @libraries/.../win_args.txt nogui
            args.Add("@" + def.ArgsFile);
            args.Add("nogui");
        }
        else
        {
            args.AddRange(["-jar", def.JarFile, PlatformInfo.UsesMods(def.Platform) ? "nogui" : "--nogui"]);
        }
        return args;
    }

    public Task StartAsync()
    {
        if (State is ServerState.Running or ServerState.Starting)
        {
            return Task.CompletedTask;
        }

        if (!File.Exists(_def.JavaPath))
        {
            throw new InvalidOperationException("Java runtime not found: " + _def.JavaPath);
        }

        var psi = new ProcessStartInfo(_def.JavaPath)
        {
            WorkingDirectory = _def.Directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in BuildArguments(_def))
        {
            psi.ArgumentList.Add(a);
        }

        SetState(ServerState.Starting);
        _job = new JobObject();
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => OnLine(e.Data);
        _process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        _process.Exited += (_, _) =>
        {
            var code = _process?.ExitCode ?? 0;
            SetState(State == ServerState.Stopping || code == 0 ? ServerState.Stopped : ServerState.Crashed);
            _job?.Dispose();
            _job = null;
        };
        _process.Start();
        _job.Assign(_process);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        return Task.CompletedTask;
    }

    private void OnLine(string? line)
    {
        if (line is null)
        {
            return;
        }

        if (State == ServerState.Starting && line.Contains("Done (", StringComparison.Ordinal) && line.Contains("For help, type", StringComparison.Ordinal))
        {
            SetState(ServerState.Running);
        }

        Output?.Invoke(this, line);
    }

    public async Task SendCommandAsync(string command)
    {
        if (_process is { HasExited: false })
        {
            await _process.StandardInput.WriteLineAsync(command.ReplaceLineEndings(" ")).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Graceful "stop", then kills the whole process tree if it does not exit in time.</summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        if (_process is null || _process.HasExited)
        {
            SetState(ServerState.Stopped);
            return;
        }

        SetState(ServerState.Stopping);
        await SendCommandAsync("stop").ConfigureAwait(false);
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill();
        }
    }

    public void Kill()
    {
        _job?.Terminate();
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
        }
    }

    private void SetState(ServerState s)
    {
        if (State != s)
        {
            State = s;
            StateChanged?.Invoke(this, s);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            await StopAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }

        _process?.Dispose();
        _job?.Dispose();
    }
}
