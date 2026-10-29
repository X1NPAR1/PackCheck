using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using PackCheck.Core;

namespace PackCheck.Servers;

public sealed record RuntimeTestOptions
{
    public required string JavaPath { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(3);
    public int MaxMemoryMb { get; init; } = 2048;
    public bool KeepDiagnostics { get; init; } = true;
}

/// <summary>
/// Opt-in startup test. Runs the server on a throw-away copy, bound to 127.0.0.1 on a free port, with a sanitized
/// environment, inside a Job Object (memory cap, process-tree kill). The original folder is never modified.
/// Plugins still execute with the user's file-system rights; see docs/security.md for residual risk.
/// </summary>
public sealed partial class RuntimeTestRunner
{
    [GeneratedRegex(@"Done \((?<t>[0-9.,]+)s\)!")]
    private static partial Regex DoneLine();

    [GeneratedRegex(@"\[(?<plugin>[^\]]+)\] Enabling (?<name>\S+)")]
    private static partial Regex EnablingLine();

    [GeneratedRegex(@"(Error occurred while enabling|Could not load '|Unknown/missing dependency plugins|Ambiguous plugin name)")]
    private static partial Regex PluginErrorLine();

    private static readonly string[] AllowedEnv = ["SystemRoot", "windir", "TEMP", "TMP", "PATH", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS", "ComSpec"];

    public async Task<IReadOnlyList<CheckResult>> RunAsync(string packRoot, string serverJar, RuntimeTestOptions options, IProgress<string>? log, CancellationToken ct)
    {
        var results = new List<CheckResult>();
        var work = Path.Combine(Path.GetTempPath(), "packcheck-run-" + Guid.NewGuid().ToString("N"));
        var lines = new List<string>();
        try
        {
            CopyDirectory(packRoot, work);
            File.WriteAllText(Path.Combine(work, "eula.txt"), "eula=true\n");
            var propsFile = Path.Combine(work, "server.properties");
            var original = File.Exists(propsFile) ? File.ReadAllText(propsFile) : string.Empty;
            var port = FreePort();
            File.WriteAllText(propsFile, PackCheck.Validation.ServerProperties.Update(original, new Dictionary<string, string>
            {
                ["server-ip"] = "127.0.0.1",
                ["server-port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["enable-query"] = "false",
                ["enable-rcon"] = "false",
                ["online-mode"] = "false",
                ["level-type"] = "minecraft\\:flat",
                ["generate-structures"] = "false",
            }));

            var psi = new ProcessStartInfo(options.JavaPath)
            {
                WorkingDirectory = work,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.Environment.Clear();
            foreach (var key in AllowedEnv)
            {
                var v = Environment.GetEnvironmentVariable(key);
                if (v is not null)
                {
                    psi.Environment[key] = v;
                }
            }

            foreach (var a in new[] { $"-Xmx{options.MaxMemoryMb}M", "-Dpaper.disableChannelLimit=true", "-jar", serverJar, "--nogui" })
            {
                psi.ArgumentList.Add(a);
            }

            using var job = new JobObject(memoryLimitBytes: (options.MaxMemoryMb + 768L) * 1024 * 1024, maxProcesses: 8);
            using var p = new Process { StartInfo = psi };
            var done = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnLine(string? l)
            {
                if (l is null)
                {
                    return;
                }

                lock (lines)
                {
                    lines.Add(l);
                }

                log?.Report(l);
                var m = DoneLine().Match(l);
                if (m.Success)
                {
                    done.TrySetResult(double.Parse(m.Groups["t"].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            p.OutputDataReceived += (_, e) => OnLine(e.Data);
            p.ErrorDataReceived += (_, e) => OnLine(e.Data);
            var sw = Stopwatch.StartNew();
            p.Start();
            job.Assign(p);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            var exited = p.WaitForExitAsync(ct);
            var timeout = Task.Delay(options.Timeout, ct);
            var first = await Task.WhenAny(done.Task, exited, timeout).ConfigureAwait(false);

            if (first == done.Task)
            {
                results.Add(new CheckResult
                {
                    Id = "RT001",
                    Name = "Server startup",
                    Status = CheckStatus.Pass,
                    Description = $"The server finished starting in {done.Task.Result:0.0}s (wall clock {sw.Elapsed.TotalSeconds:0.0}s).",
                });
                await p.StandardInput.WriteLineAsync("stop").ConfigureAwait(false);
                await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(30), ct)).ConfigureAwait(false);
            }
            else if (first == exited)
            {
                results.Add(new CheckResult
                {
                    Id = "RT001",
                    Name = "Server startup",
                    Status = CheckStatus.Fail,
                    Description = $"The server process exited with code {p.ExitCode} before finishing startup.",
                    Evidence = Tail(lines, 15),
                });
            }
            else
            {
                results.Add(new CheckResult
                {
                    Id = "RT001",
                    Name = "Server startup",
                    Status = CheckStatus.Fail,
                    Description = $"The server did not finish starting within {options.Timeout.TotalSeconds:0}s.",
                    Evidence = Tail(lines, 15),
                    Fix = new SuggestedFix("Check the log for a plugin that hangs during enable, or raise the timeout for large worlds.", false),
                });
            }

            job.Terminate();

            List<string> snapshot;
            lock (lines)
            {
                snapshot = [.. lines];
            }

            var pluginErrors = snapshot.Where(l => PluginErrorLine().IsMatch(l)).ToList();
            results.Add(new CheckResult
            {
                Id = "RT002",
                Name = "Plugin enable",
                Status = pluginErrors.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail,
                Description = pluginErrors.Count == 0
                    ? $"{snapshot.Count(l => EnablingLine().IsMatch(l))} plugin(s) enabled without reported errors."
                    : $"{pluginErrors.Count} plugin load/enable error(s) in the log.",
                Evidence = pluginErrors.Take(20).ToList(),
            });

            if (options.KeepDiagnostics && results.Any(r => r.Status == CheckStatus.Fail))
            {
                var diag = Path.Combine(AppPaths.Logs, $"runtime-test-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                Directory.CreateDirectory(AppPaths.Logs);
                await File.WriteAllLinesAsync(diag, snapshot, CancellationToken.None).ConfigureAwait(false);
                results.Add(new CheckResult { Id = "RT003", Name = "Diagnostics", Status = CheckStatus.Pass, Description = "Full log preserved.", AffectedFile = diag });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            results.Add(new CheckResult { Id = "RT000", Name = "Runtime test", Status = CheckStatus.Error, Description = "The runtime test could not run.", NotRunReason = ex.Message });
        }
        finally
        {
            for (var i = 0; i < 5 && Directory.Exists(work); i++)
            {
                try
                {
                    Directory.Delete(work, true);
                }
                catch (IOException)
                {
                    await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException)
                {
                    await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        return results;
    }

    private static List<string> Tail(List<string> lines, int n)
    {
        lock (lines)
        {
            return lines.Skip(Math.Max(0, lines.Count - n)).ToList();
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }

        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, file);
            if (rel.StartsWith("logs", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("cache", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(file, Path.Combine(to, rel));
        }
    }
}
