using System.Net;
using System.Text.Json;
using PackCheck.Core;
using PackCheck.Reporting;
using PackCheck.Updater;

namespace PackCheck.Tests;

public class ReportingTests
{
    private static ValidationReport Sample(string payload) => new()
    {
        Tool = "PackCheck",
        ToolVersion = "1.0.0",
        Target = payload,
        StartedAt = DateTimeOffset.UnixEpoch,
        Duration = TimeSpan.FromSeconds(1),
        Overall = OverallStatus.Fail,
        Checks = [new CheckResult { Id = "PC040", Name = payload, Status = CheckStatus.Fail, Description = payload, Evidence = [payload], Fix = new SuggestedFix(payload, true) }],
        Plugins = [new PluginInfo { FileName = payload, Sha256 = new string('a', 64), Size = 1, Name = payload }],
    };

    [Fact]
    public void Html_EscapesUntrustedMetadata()
    {
        const string xss = "<script>alert(1)</script><img src=x onerror=alert(2)>";
        var html = HtmlReporter.Write(Sample(xss));
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_RoundTrips_WithStringEnums()
    {
        var json = JsonReporter.Write(Sample("pack"));
        Assert.Contains("\"status\": \"Fail\"", json, StringComparison.Ordinal);
        Assert.Contains("\"schema\": \"1.0\"", json, StringComparison.Ordinal);
        var back = JsonReporter.Read(json)!;
        Assert.Equal(OverallStatus.Fail, back.Overall);
        Assert.Equal("PC040", back.Checks[0].Id);
    }

    [Fact]
    public void Json_MatchesPublishedSchemaRequiredFields()
    {
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "report.schema.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        using var doc = JsonDocument.Parse(JsonReporter.Write(Sample("pack")));
        foreach (var req in schema.RootElement.GetProperty("required").EnumerateArray())
        {
            Assert.True(doc.RootElement.TryGetProperty(req.GetString()!, out _), $"missing {req}");
        }

        var check = schema.RootElement.GetProperty("$defs").GetProperty("check");
        var allowed = check.GetProperty("properties").GetProperty("status").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToHashSet();
        foreach (var c in doc.RootElement.GetProperty("checks").EnumerateArray())
        {
            Assert.Contains(c.GetProperty("status").GetString(), allowed);
            foreach (var req in check.GetProperty("required").EnumerateArray())
            {
                Assert.True(c.TryGetProperty(req.GetString()!, out _));
            }
        }
    }
}

public class UpdatePolicyTests
{
    private static readonly Version Current = new(1, 0, 0);
    private static readonly DateOnly Day1 = new(2026, 10, 28);

    private static ReleaseInfo Release(string tag, bool asset = true) => new(
        tag, ReleaseInfo.TryParseTag(tag, out var v) ? v : new Version(), tag, "notes", DateTimeOffset.Now, new Uri("https://github.com/X1NPAR1/PackCheck/releases"),
        asset ? new ReleaseAsset(GitHubReleaseClient.AssetName, new Uri("https://github.com/a.zip"), 10) : null,
        asset ? new ReleaseAsset(GitHubReleaseClient.AssetName + ".sha256", new Uri("https://github.com/a.sha256"), 1) : null);

    [Fact]
    public void NoRelease_OrSameVersion_DoesNothing()
    {
        Assert.Equal(UpdateAction.None, UpdatePolicy.Decide(Current, null, new(), false, Day1).Action);
        Assert.Equal(UpdateAction.None, UpdatePolicy.Decide(Current, Release("v1.0.0"), new(), false, Day1).Action);
        Assert.Equal(UpdateAction.None, UpdatePolicy.Decide(Current, Release("v0.9.0"), new(), false, Day1).Action);
    }

    [Fact]
    public void ReleaseWithoutPackage_DoesNothing() =>
        Assert.Equal(UpdateAction.None, UpdatePolicy.Decide(Current, Release("v1.1.0", asset: false), new(), false, Day1).Action);

    [Fact]
    public void AutoUpdate_InstallsImmediately() =>
        Assert.Equal(UpdateAction.AutoInstall, UpdatePolicy.Decide(Current, Release("v1.1.0"), new(), true, Day1).Action);

    [Fact]
    public void GracePeriod_PromptsOnDay1And2_ForcesOnDay3()
    {
        var r = Release("v1.1.0");
        var d1 = UpdatePolicy.Decide(Current, r, new(), false, Day1);
        Assert.Equal(UpdateAction.Prompt, d1.Action);
        Assert.Equal(2, d1.DaysLeft);

        var state = UpdatePolicy.AfterPrompt(new(), r, Day1);
        var d2 = UpdatePolicy.Decide(Current, r, state, false, Day1.AddDays(1));
        Assert.Equal(UpdateAction.Prompt, d2.Action);
        Assert.Equal(1, d2.DaysLeft);

        Assert.Equal(UpdateAction.ForceInstall, UpdatePolicy.Decide(Current, r, state, false, Day1.AddDays(2)).Action);
        Assert.Equal(UpdateAction.ForceInstall, UpdatePolicy.Decide(Current, r, state, false, Day1.AddDays(30)).Action);
    }

    [Fact]
    public void NewerReleaseDuringGrace_KeepsOriginalDeadline()
    {
        var state = UpdatePolicy.AfterPrompt(new(), Release("v1.1.0"), Day1);
        state = UpdatePolicy.AfterPrompt(state, Release("v1.2.0"), Day1.AddDays(1));
        Assert.Equal(UpdateAction.ForceInstall, UpdatePolicy.Decide(Current, Release("v1.2.0"), state, false, Day1.AddDays(2)).Action);
    }

    [Fact]
    public void Reconcile_ClearsStateAfterUpdating()
    {
        var state = UpdatePolicy.AfterPrompt(new(), Release("v1.1.0"), Day1);
        Assert.Null(UpdatePolicy.Reconcile(state, new Version(1, 1, 0)).FirstNotified);
        Assert.NotNull(UpdatePolicy.Reconcile(state, Current).FirstNotified);
    }

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("v2.0.0-beta.1", 2, 0, 0)]
    [InlineData("1.0.0+3d3c42e5", 1, 0, 0)]
    public void TagParsing(string tag, int a, int b, int c)
    {
        Assert.True(ReleaseInfo.TryParseTag(tag, out var v));
        Assert.Equal(new Version(a, b, c), v);
    }

    [Fact]
    public void GitHubPayload_IsParsed_AndNonGitHubAssetsIgnored()
    {
        const string json = """
            {"tag_name":"v1.4.0","name":"PackCheck 1.4.0","body":"Fixes","html_url":"https://github.com/X1NPAR1/PackCheck/releases/tag/v1.4.0","published_at":"2026-10-28T10:00:00Z",
             "assets":[{"name":"PackCheck-win-x64.zip","browser_download_url":"https://github.com/X1NPAR1/PackCheck/releases/download/v1.4.0/PackCheck-win-x64.zip","size":100},
                       {"name":"PackCheck-win-x64.zip.sha256","browser_download_url":"https://evil.example.com/x.sha256","size":64}]}
            """;
        using var doc = JsonDocument.Parse(json);
        var r = GitHubReleaseClient.Parse(doc.RootElement)!;
        Assert.Equal(new Version(1, 4, 0), r.Version);
        Assert.NotNull(r.Asset);
        Assert.Null(r.Checksum);
    }

    [Fact]
    public async Task Offline_CheckReportsNone()
    {
        using var http = new HttpClient(new FailingHandler());
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var svc = new UpdateService(new GitHubReleaseClient(http), http, state);
        var d = await svc.CheckAsync(false, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Equal(UpdateAction.None, d.Action);
        File.Delete(state);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline", null, HttpStatusCode.ServiceUnavailable);
    }
}
