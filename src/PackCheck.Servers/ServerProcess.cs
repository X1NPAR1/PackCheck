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

    /// <summary>
    /// Console input is sent as UTF-8 without a byte order mark: current server consoles (Java 18+) read UTF-8,
    /// and a BOM or the Windows code page would turn Turkish, German or Russian letters into '?'.
    /// </summary>
    public static Encoding ConsoleInputEncoding { get; } = new UTF8Encoding(false);

    public static IReadOnlyList<string> BuildArguments(ServerDefinition def)
    {
        var args = new List<string>
        {
            $"-Xms{def.MinMemoryMb}M", $"-Xmx{def.MaxMemoryMb}M",

            // PackCheck reads the console as UTF-8; without these Java writes in the Windows code page
            // (Turkish, Russian, ... text turns into '?' characters).
            "-Dstdout.encoding=UTF-8", "-Dstderr.encoding=UTF-8",

            // There is no real terminal; JLine-based consoles otherwise may not read commands from the pipe.
            "-Dterminal.jline=false",
        };
        if (def.CpuCores > 0 && def.CpuCores != Environment.ProcessorCount)
        {
            // Sizes the JVM's GC and worker thread pools to the cores the server is allowed to use.
            args.Add($"-XX:ActiveProcessorCount={def.CpuCores}");
        }

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

        if (ProxyConfigurator.IsProxy(def.Platform))
        {
            args.AddRange(["-jar", def.JarFile]);
        }
        else if (def.ArgsFile is not null)
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
            StandardInputEncoding = ConsoleInputEncoding,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in BuildArguments(_def))
        {
            psi.ArgumentList.Add(a);
        }

        SetState(ServerState.Starting);
        _failureSeen = false;
        _job = new JobObject();
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => OnLine(e.Data);
        _process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        _process.Exited += (_, _) =>
        {
            var code = _process?.ExitCode ?? 0;
            SetState(ExitState(State, code, _failureSeen));
            _job?.Dispose();
            _job = null;
        };
        _process.Start();
        _job.Assign(_process);
        if (_def.CpuCores > 0 && _def.CpuCores < Environment.ProcessorCount && OperatingSystem.IsWindows())
        {
            try
            {
                // Limit the server to its first N logical processors so the rest of the PC stays responsive.
                var n = Math.Min(_def.CpuCores, 63);
                _process.ProcessorAffinity = (nint)((1L << n) - 1);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

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

        var ready = line.Contains("Done (", StringComparison.Ordinal) && (line.Contains("For help, type", StringComparison.Ordinal) || ProxyConfigurator.IsProxy(_def.Platform))
            || (_def.Platform == ServerPlatform.BungeeCord && line.Contains("Listening on", StringComparison.Ordinal));
        if (State == ServerState.Starting && ready)
        {
            SetState(ServerState.Running);
        }

        if (!_failureSeen && IsFailureLine(line))
        {
            _failureSeen = true;
        }

        Output?.Invoke(this, line);
    }

    private volatile bool _failureSeen;

    /// <summary>
    /// Paper/Vanilla often exit with code 0 after a crash (they save a crash report and shut down cleanly),
    /// and exit 0 when the EULA is missing. Those are crashes for the user, not normal stops.
    /// </summary>
    public static ServerState ExitState(ServerState stateBefore, int exitCode, bool failureSeen) =>
        stateBefore == ServerState.Stopping ? ServerState.Stopped
        : exitCode != 0 || failureSeen || stateBefore == ServerState.Starting ? ServerState.Crashed
        : ServerState.Stopped;

    public static bool IsFailureLine(string line) =>
        line.Contains("This crash report has been saved to", StringComparison.Ordinal)
        || line.Contains("Failed to start the minecraft server", StringComparison.Ordinal)
        || line.Contains("Encountered an unexpected exception", StringComparison.Ordinal)
        || line.Contains("FAILED TO BIND TO PORT", StringComparison.Ordinal)
        || line.Contains("You need to agree to the EULA", StringComparison.Ordinal);

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
        await SendCommandAsync(ProxyConfigurator.IsProxy(_def.Platform) ? "end" : "stop").ConfigureAwait(false);
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
        // A kill requested by the user is not a crash (no crash banner, no restart-on-crash).
        if (State is ServerState.Starting or ServerState.Running)
        {
            SetState(ServerState.Stopping);
        }

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
