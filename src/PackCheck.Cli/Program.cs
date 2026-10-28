using PackCheck.Core;
using PackCheck.Reporting;
using PackCheck.Validation;

namespace PackCheck.Cli;

public static class Program
{
    private const string Usage = """
        PackCheck - validate Minecraft server packs before they go live.

        Usage:
          packcheck validate <folder|pack.zip> [options]
          packcheck doctor
          packcheck version

        Options for validate:
          --format json|html|text   Output format (default: text)
          --output <file>           Write the report to a file instead of stdout
          --mc-version <x.y.z>      Minecraft version, if the pack has no packcheck.json
          --java <n>                Java feature version used by the server (e.g. 21)
          --fail-on-warnings        Exit with 1 when warnings are found
          --no-runtime-test         Static analysis only (default; runtime tests run in the desktop app)
          --timeout <seconds>       Abort analysis after the given time (default 300)
          --verbose                 Print every check, including passes

        Exit codes: 0 pass/pass with warnings, 1 failed, 2 incomplete, 64 usage error, 70 internal error.
        """;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            return args.FirstOrDefault() switch
            {
                "validate" => await ValidateAsync(args[1..]).ConfigureAwait(false),
                "doctor" => Doctor(),
                "version" or "--version" => PrintVersion(),
                "help" or "--help" or "-h" or null => PrintUsage(ExitCodes.Success),
                _ => PrintUsage(ExitCodes.UsageError),
            };
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Timed out.").ConfigureAwait(false);
            return ExitCodes.Incomplete;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync("Internal error: " + ex.Message).ConfigureAwait(false);
            return ExitCodes.InternalError;
        }
    }

    private static int PrintUsage(int code)
    {
        (code == 0 ? Console.Out : Console.Error).WriteLine(Usage);
        return code;
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"packcheck {PackValidator.ToolVersion}");
        return ExitCodes.Success;
    }

    private static int Doctor()
    {
        Console.WriteLine($"packcheck {PackValidator.ToolVersion}");
        Console.WriteLine($"runtime   {Environment.Version} ({System.Runtime.InteropServices.RuntimeInformation.OSDescription})");
        Console.WriteLine($"data dir  {AppPaths.DataRoot}");
        var java = Environment.GetEnvironmentVariable("JAVA_HOME");
        Console.WriteLine($"JAVA_HOME {(string.IsNullOrEmpty(java) ? "(not set)" : java)}");
        Console.WriteLine($"analyzers {PackValidator.DefaultAnalyzers.Count}: {string.Join(", ", PackValidator.DefaultAnalyzers.Select(a => a.Id))}");
        return ExitCodes.Success;
    }

    private static async Task<int> ValidateAsync(string[] args)
    {
        string? target = null, format = "text", output = null;
        var options = new ValidationOptions();
        var failOnWarnings = false;
        var verbose = false;
        var timeout = TimeSpan.FromSeconds(300);

        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {args[i]}");
            switch (args[i])
            {
                case "--format": format = Next(); break;
                case "--output": output = Next(); break;
                case "--mc-version":
                    options = MinecraftVersion.TryParse(Next(), out var mv) ? options with { MinecraftVersion = mv } : throw new ArgumentException("Invalid --mc-version");
                    break;
                case "--java":
                    options = int.TryParse(Next(), out var j) ? options with { JavaVersion = j } : throw new ArgumentException("Invalid --java");
                    break;
                case "--timeout":
                    timeout = int.TryParse(Next(), out var t) && t > 0 ? TimeSpan.FromSeconds(t) : throw new ArgumentException("Invalid --timeout");
                    break;
                case "--fail-on-warnings": failOnWarnings = true; break;
                case "--no-runtime-test": break;
                case "--verbose": verbose = true; break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal) || target is not null)
                    {
                        await Console.Error.WriteLineAsync($"Unknown argument: {args[i]}").ConfigureAwait(false);
                        return ExitCodes.UsageError;
                    }

                    target = args[i];
                    break;
            }
        }

        if (target is null || format is not ("json" or "html" or "text"))
        {
            return PrintUsage(ExitCodes.UsageError);
        }

        using var cts = new CancellationTokenSource(timeout);
        var report = await new PackValidator().ValidateAsync(target, options, null, cts.Token).ConfigureAwait(false);

        var text = format switch
        {
            "json" => JsonReporter.Write(report),
            "html" => HtmlReporter.Write(report),
            _ => TextReport(report, verbose),
        };

        if (output is null)
        {
            Console.WriteLine(text);
        }
        else
        {
            await File.WriteAllTextAsync(output, text).ConfigureAwait(false);
            Console.WriteLine($"{HtmlReporter.Label(report.Overall)} - report written to {output}");
        }

        return ExitCodes.For(report.Overall, failOnWarnings);
    }

    private static string TextReport(ValidationReport r, bool verbose)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"PackCheck {r.ToolVersion} - {r.Target}");
        foreach (var c in r.Checks.Where(c => verbose || c.Status != CheckStatus.Pass))
        {
            sb.AppendLine($"  [{c.Status.ToString().ToUpperInvariant(),-5}] {c.Id} {c.Name}: {c.Description}");
            foreach (var e in c.Evidence)
            {
                sb.AppendLine($"           {e}");
            }

            if (c.Fix is not null)
            {
                sb.AppendLine($"           fix{(c.Fix.IsCertain ? string.Empty : " (estimated)")}: {c.Fix.Text}");
            }
        }

        sb.AppendLine($"Result: {HtmlReporter.Label(r.Overall)}  (pass {r.Count(CheckStatus.Pass)}, warn {r.Count(CheckStatus.Warn)}, fail {r.Count(CheckStatus.Fail)}, skip {r.Count(CheckStatus.Skip)}, error {r.Count(CheckStatus.Error)})");
        return sb.ToString();
    }
}
