using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PackCheck.Servers;

/// <summary>One official playit release asset, pinned by version and the SHA-256 that GitHub publishes for it.</summary>
public sealed record PlayitAsset(string FileName, string Sha256)
{
    public Uri Url => new($"https://github.com/playit-cloud/playit-agent/releases/download/{PlayitAgent.Version}/{FileName}");
}

/// <summary>
/// The official playit agent (github.com/playit-cloud/playit-agent). Since 1.2 it is two programs:
/// <c>playitd</c> runs the tunnels and <c>playit</c> is the front end that prints the claim (setup) link.
/// PackCheck runs its own playitd with a private pipe and secret file, so an installed playit service is never touched.
/// </summary>
public static class PlayitAgent
{
    public const string Version = "v1.2.0";

    public static readonly PlayitAsset Daemon = new("playitd-windows-x86_64-signed.exe", "e8dff30a9269a10d2c6786c95a0618567772e441d07dd7ebe3c1a37d3f35534d");

    public static readonly PlayitAsset Cli = new("playit-windows-x86_64-signed.exe", "cb935ac28affee1ceea7b79c88c96ff023b7a1493eb6f9d0f4c7580baf0c0238");

    public const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>Downloads both programs into <paramref name="dir"/> unless a copy with the right hash is already there.</summary>
    public static async Task EnsureDownloadedAsync(HttpClient http, string dir, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        foreach (var asset in new[] { Daemon, Cli })
        {
            var path = Path.Combine(dir, asset.FileName);
            if (File.Exists(path) && await HashMatchesAsync(path, asset.Sha256, ct).ConfigureAwait(false))
            {
                continue;
            }

            File.Delete(path);
            await VerifiedDownloader.DownloadAsync(http, asset.Url, path, asset.Sha256, HashAlgorithmName.SHA256, MaxBytes, null, ct).ConfigureAwait(false);
        }
    }

    private static async Task<bool> HashMatchesAsync(string path, string sha256, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(hash), sha256, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>What one line of agent output means.</summary>
public enum PlayitSignal
{
    None,
    ClaimUrl,
    Address,
    Claimed,
    Connected,
    Error,
}

public sealed record PlayitLine(PlayitSignal Signal, string? Value = null);

/// <summary>
/// Reads the playit agent's console output. The format is not a stable interface, so everything that depends on it lives here;
/// when nothing is recognized the UI falls back to the raw log.
/// </summary>
public static partial class PlayitOutput
{
    [GeneratedRegex(@"https://playit\.gg/claim/[0-9a-fA-F]+", RegexOptions.CultureInvariant)]
    private static partial Regex ClaimRegex();

    // Tunnel addresses look like name.joinmc.link, name.gl.joinmc.link or name.at.ply.gg:12345.
    [GeneratedRegex(@"\b(?:[a-z0-9-]+\.)+(?:joinmc\.link|ply\.gg)(?::\d{1,5})?\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AddressRegex();

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiRegex();

    public static string Clean(string line) => AnsiRegex().Replace(line, string.Empty).TrimEnd();

    public static PlayitLine Parse(string line)
    {
        var text = Clean(line);
        if (ClaimRegex().Match(text) is { Success: true } claim)
        {
            return new PlayitLine(PlayitSignal.ClaimUrl, claim.Value);
        }

        if (AddressRegex().Match(text) is { Success: true } address)
        {
            return new PlayitLine(PlayitSignal.Address, address.Value.ToLowerInvariant());
        }

        if (text.Contains("Program approved", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Secret provisioned successfully", StringComparison.OrdinalIgnoreCase)
            || text.Contains("setup is complete", StringComparison.OrdinalIgnoreCase))
        {
            return new PlayitLine(PlayitSignal.Claimed);
        }

        if (text.Contains("playit connected", StringComparison.OrdinalIgnoreCase))
        {
            return new PlayitLine(PlayitSignal.Connected);
        }

        if (text.Contains(" ERROR ", StringComparison.Ordinal) || text.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not approved", StringComparison.OrdinalIgnoreCase) || text.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return new PlayitLine(PlayitSignal.Error, text);
        }

        return new PlayitLine(PlayitSignal.None);
    }
}

/// <summary>A running child process as the tunnel sees it, so the lifecycle can be tested without real programs.</summary>
public interface IPlayitProcess : IDisposable
{
    event Action<string>? Output;

    event Action? Exited;

    bool HasExited { get; }

    void Kill();
}

public interface IPlayitLauncher
{
    IPlayitProcess Start(string exe, IReadOnlyList<string> args);

    /// <summary>True once the daemon's named pipe exists.</summary>
    bool PipeExists(string pipeName);
}

public enum PlayitState
{
    Stopped,
    Downloading,
    Starting,
    WaitingForClaim,
    Running,
    Failed,
}

/// <summary>Runs PackCheck's own playit agent: download, start (no window), read its output, stop.</summary>
public sealed class PlayitTunnel(string toolsDir, IPlayitLauncher launcher, Func<CancellationToken, Task>? download = null) : IDisposable
{
    public const int MaxLogLines = 400;

    private readonly Lock _gate = new();
    private readonly List<string> _log = [];
    private IPlayitProcess? _daemon;
    private IPlayitProcess? _cli;
    private bool _stopping;

    public PlayitState State { get; private set; } = PlayitState.Stopped;

    public string? ClaimUrl { get; private set; }

    public string? Address { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Raised on any change (state, link, address or a new log line); may come from a background thread.</summary>
    public event Action? Changed;

    public bool IsActive => State is PlayitState.Downloading or PlayitState.Starting or PlayitState.WaitingForClaim or PlayitState.Running;

    public string PipeName { get; } = $"packcheck-playit-{Environment.ProcessId}";

    public string SecretPath => Path.Combine(toolsDir, "playit-secret.toml");

    public IReadOnlyList<string> LogLines
    {
        get
        {
            lock (_gate)
            {
                return [.. _log];
            }
        }
    }

    /// <summary>True when the agent wrote something but nothing in it was recognized: the UI should show the raw log.</summary>
    public bool OutputUnrecognized { get; private set; }

    private bool _recognizedAny;

    public async Task StartAsync(TimeSpan pipeTimeout, CancellationToken ct)
    {
        if (IsActive)
        {
            return;
        }

        _stopping = false;
        ClaimUrl = null;
        Address = null;
        Error = null;
        OutputUnrecognized = false;
        _recognizedAny = false;
        lock (_gate)
        {
            _log.Clear();
        }

        try
        {
            if (download is not null)
            {
                SetState(PlayitState.Downloading);
                await download(ct).ConfigureAwait(false);
            }

            SetState(PlayitState.Starting);
            var pipe = $@"\\.\pipe\{PipeName}";
            _daemon = launcher.Start(Path.Combine(toolsDir, PlayitAgent.Daemon.FileName), ["--secret-path", SecretPath, "--socket-path", pipe]);
            Hook(_daemon);

            var deadline = DateTime.UtcNow + pipeTimeout;
            while (!launcher.PipeExists(PipeName))
            {
                if (_daemon.HasExited || _stopping)
                {
                    throw new InvalidOperationException("playitd exited before it was ready.");
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("playitd did not open its pipe in time.");
                }

                await Task.Delay(100, ct).ConfigureAwait(false);
            }

            _cli = launcher.Start(Path.Combine(toolsDir, PlayitAgent.Cli.FileName), ["--stdout", "--socket-path", pipe]);
            Hook(_cli);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or HttpRequestException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Error = ex.Message;
            KillAll();
            SetState(PlayitState.Failed);
        }
    }

    public void Stop()
    {
        _stopping = true;
        KillAll();
        SetState(PlayitState.Stopped);
    }

    private void KillAll()
    {
        foreach (var p in new[] { _cli, _daemon })
        {
            if (p is null)
            {
                continue;
            }

            try
            {
                if (!p.HasExited)
                {
                    p.Kill();
                }
            }
            catch (InvalidOperationException)
            {
            }

            p.Dispose();
        }

        _cli = null;
        _daemon = null;
    }

    private void Hook(IPlayitProcess process)
    {
        process.Output += OnOutput;
        process.Exited += () =>
        {
            if (!_stopping && IsActive)
            {
                Error ??= "playit exited.";
                KillAll();
                SetState(PlayitState.Failed);
            }
        };
    }

    /// <summary>Feeds one line of output into the tunnel state. Public for tests.</summary>
    public void OnOutput(string raw)
    {
        var line = PlayitOutput.Clean(raw);
        if (line.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            _log.Add(line);
            if (_log.Count > MaxLogLines)
            {
                _log.RemoveRange(0, _log.Count - MaxLogLines);
            }
        }

        var parsed = PlayitOutput.Parse(line);
        switch (parsed.Signal)
        {
            case PlayitSignal.ClaimUrl:
                ClaimUrl = parsed.Value;
                if (State is PlayitState.Starting)
                {
                    State = PlayitState.WaitingForClaim;
                }

                break;
            case PlayitSignal.Address:
                Address = parsed.Value;
                State = PlayitState.Running;
                break;
            case PlayitSignal.Claimed:
            case PlayitSignal.Connected:
                ClaimUrl = null;
                State = PlayitState.Running;
                break;
            case PlayitSignal.Error:
                Error = parsed.Value;
                break;
        }

        _recognizedAny |= parsed.Signal is not PlayitSignal.None;
        OutputUnrecognized = !_recognizedAny;
        Changed?.Invoke();
    }

    private void SetState(PlayitState state)
    {
        State = state;
        Changed?.Invoke();
    }

    public void Dispose() => Stop();
}

/// <summary>Real launcher: no console window, output captured, both programs in a kill-on-close job so nothing outlives PackCheck.</summary>
public sealed class PlayitLauncher : IPlayitLauncher, IDisposable
{
    private readonly JobObject _job = new();

    public IPlayitProcess Start(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["NO_COLOR"] = "1";
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var wrapper = new RealProcess(process);
        process.Start();
        try
        {
            _job.Assign(process);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Still killed explicitly on stop; the job is only a safety net for crashes.
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return wrapper;
    }

    public bool PipeExists(string pipeName) => File.Exists($@"\\.\pipe\{pipeName}");

    public void Dispose() => _job.Dispose();

    private sealed class RealProcess : IPlayitProcess
    {
        private readonly Process _process;

        public RealProcess(Process process)
        {
            _process = process;
            process.OutputDataReceived += (_, e) => Emit(e.Data);
            process.ErrorDataReceived += (_, e) => Emit(e.Data);
            process.Exited += (_, _) => Exited?.Invoke();
        }

        public event Action<string>? Output;

        public event Action? Exited;

        public bool HasExited => _process.HasExited;

        private void Emit(string? data)
        {
            if (data is not null)
            {
                Output?.Invoke(data);
            }
        }

        public void Kill()
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }

        public void Dispose() => _process.Dispose();
    }
}
