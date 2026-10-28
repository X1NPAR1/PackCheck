using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PackCheck.Core;

namespace PackCheck.Reporting;

public static class JsonReporter
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Default,
    };

    public static string Write(ValidationReport report) => JsonSerializer.Serialize(report, Options);

    public static ValidationReport? Read(string json) => JsonSerializer.Deserialize<ValidationReport>(json, Options);
}

/// <summary>Self-contained HTML report. All untrusted values are HTML-encoded; strict CSP; no remote resources.</summary>
public static class HtmlReporter
{
    public static string Write(ValidationReport report)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src data:\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>PackCheck report - ").Append(E(report.Target)).Append("</title><style>");
        sb.Append("""
            :root{--bg:#1b1d1c;--card:#232624;--line:#3a3f3b;--text:#eceae3;--muted:#9c9d96;--pass:#73b04f;--warn:#c9a43a;--fail:#c4574d;--skip:#8a8b84;--err:#b5483f}
            @media (prefers-color-scheme: light){:root{--bg:#f3f1ea;--card:#fbfaf6;--line:#d9d6cc;--text:#232624;--muted:#5d5f59;--pass:#3f6b2a;--warn:#8a6d12;--fail:#9e3a32;--skip:#6b6c66;--err:#9e3a32}}
            *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.5 system-ui,sans-serif;padding:24px}
            main{max-width:960px;margin:auto}h1{margin:0 0 4px;font-size:26px}.muted{color:var(--muted)}
            .summary{display:flex;gap:12px;flex-wrap:wrap;margin:20px 0}.pill{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:10px 16px}
            .pill b{font-size:20px;display:block}.check{background:var(--card);border:1px solid var(--line);border-left:4px solid var(--skip);border-radius:8px;padding:12px 16px;margin:10px 0}
            .Pass{border-left-color:var(--pass)}.Warn{border-left-color:var(--warn)}.Fail{border-left-color:var(--fail)}.Error{border-left-color:var(--err)}
            .tag{font:600 12px ui-monospace,monospace;padding:2px 8px;border-radius:4px;border:1px solid var(--line);margin-right:8px}
            code,pre{font-family:ui-monospace,Consolas,monospace;font-size:13px}pre{white-space:pre-wrap;margin:6px 0 0;color:var(--muted)}
            table{width:100%;border-collapse:collapse;margin-top:10px}td,th{border-bottom:1px solid var(--line);padding:6px;text-align:left;font-size:13px}
            footer{margin-top:32px;font-size:12px}
            """);
        sb.Append("</style></head><body><main>");
        sb.Append("<h1>").Append(E(report.Target)).Append("</h1>");
        sb.Append("<div class=\"muted\">PackCheck ").Append(E(report.ToolVersion)).Append(" &middot; ")
          .Append(E(report.StartedAt.ToString("u", CultureInfo.InvariantCulture))).Append(" &middot; ")
          .Append(report.Duration.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)).Append(" s</div>");

        sb.Append("<div class=\"summary\"><div class=\"pill\"><b>").Append(E(Label(report.Overall))).Append("</b>Overall</div>");
        foreach (var s in Enum.GetValues<CheckStatus>())
        {
            sb.Append("<div class=\"pill\"><b>").Append(report.Count(s)).Append("</b>").Append(s).Append("</div>");
        }

        sb.Append("</div>");

        foreach (var c in report.Checks.OrderBy(c => Severity(c.Status)))
        {
            sb.Append("<section class=\"check ").Append(c.Status).Append("\"><span class=\"tag\">").Append(c.Status.ToString().ToUpperInvariant())
              .Append("</span><b>").Append(E(c.Name)).Append("</b> <span class=\"muted\">").Append(E(c.Id)).Append("</span>");
            sb.Append("<div>").Append(E(c.Description)).Append("</div>");
            if (c.AffectedFile is not null)
            {
                sb.Append("<div class=\"muted\">File: <code>").Append(E(c.AffectedFile)).Append("</code></div>");
            }

            if (c.Evidence.Count > 0)
            {
                sb.Append("<pre>").Append(E(string.Join('\n', c.Evidence))).Append("</pre>");
            }

            if (c.Fix is not null)
            {
                sb.Append("<div>").Append(c.Fix.IsCertain ? "Fix: " : "Suggested (estimated): ").Append(E(c.Fix.Text)).Append("</div>");
            }

            if (c.NotRunReason is not null)
            {
                sb.Append("<div class=\"muted\">Not run: ").Append(E(c.NotRunReason)).Append("</div>");
            }

            sb.Append("<div class=\"muted\">Confidence: ").Append(c.Confidence).Append("</div></section>");
        }

        if (report.Plugins.Count > 0)
        {
            sb.Append("<h2>Plugins</h2><table><tr><th>File</th><th>Name</th><th>Version</th><th>API</th><th>SHA-256</th></tr>");
            foreach (var p in report.Plugins)
            {
                sb.Append("<tr><td>").Append(E(p.FileName)).Append("</td><td>").Append(E(p.Name)).Append("</td><td>").Append(E(p.Version))
                  .Append("</td><td>").Append(E(p.ApiVersion)).Append("</td><td><code>").Append(E(p.Sha256[..Math.Min(16, p.Sha256.Length)])).Append("&hellip;</code></td></tr>");
            }

            sb.Append("</table>");
        }

        sb.Append("<footer class=\"muted\">Static analysis only reports what can be proven from files. Not an official Minecraft product. Not approved by or associated with Mojang or Microsoft.</footer>");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    public static string Label(OverallStatus s) => s switch
    {
        OverallStatus.Pass => "PASS",
        OverallStatus.PassWithWarnings => "PASS WITH WARNINGS",
        OverallStatus.Fail => "FAIL",
        _ => "INCOMPLETE",
    };

    private static int Severity(CheckStatus s) => s switch
    {
        CheckStatus.Fail => 0,
        CheckStatus.Error => 1,
        CheckStatus.Warn => 2,
        CheckStatus.Skip => 3,
        _ => 4,
    };
}

/// <summary>Compares two runs: what was added, removed, fixed or newly broken.</summary>
public static class ReportDiff
{
    public sealed record Change(string Kind, string Text);

    public static IReadOnlyList<Change> Compare(ValidationReport before, ValidationReport after)
    {
        var changes = new List<Change>();
        var oldPlugins = before.Plugins.ToDictionary(p => p.FileName, StringComparer.OrdinalIgnoreCase);
        var newPlugins = after.Plugins.ToDictionary(p => p.FileName, StringComparer.OrdinalIgnoreCase);
        changes.AddRange(newPlugins.Keys.Except(oldPlugins.Keys).Select(f => new Change("added", f)));
        changes.AddRange(oldPlugins.Keys.Except(newPlugins.Keys).Select(f => new Change("removed", f)));
        changes.AddRange(newPlugins.Where(kv => oldPlugins.TryGetValue(kv.Key, out var o) && o.Sha256 != kv.Value.Sha256)
            .Select(kv => new Change("changed", kv.Key)));

        static string Key(CheckResult c) => $"{c.Id}|{c.AffectedFile}|{c.AffectedPlugin}|{c.Description}";
        var oldBad = before.Checks.Where(c => c.Status is CheckStatus.Fail or CheckStatus.Warn).Select(Key).ToHashSet();
        var newBad = after.Checks.Where(c => c.Status is CheckStatus.Fail or CheckStatus.Warn).ToList();
        changes.AddRange(newBad.Where(c => !oldBad.Contains(Key(c))).Select(c => new Change("broke", $"{c.Name}: {c.Description}")));
        var newKeys = newBad.Select(Key).ToHashSet();
        changes.AddRange(before.Checks.Where(c => c.Status is CheckStatus.Fail or CheckStatus.Warn && !newKeys.Contains(Key(c)))
            .Select(c => new Change("fixed", $"{c.Name}: {c.Description}")));
        return changes;
    }
}
