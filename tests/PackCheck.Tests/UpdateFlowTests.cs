using System.Net;
using System.Text;
using PackCheck.Providers;
using PackCheck.Updater;

namespace PackCheck.Tests;

/// <summary>Movable clock for time-dependent flows.</summary>
public sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

/// <summary>Answers HTTP requests from a dictionary of URL prefixes.</summary>
public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class UpdateFlowTests
{
    private const string Release = """
        {"tag_name":"v99.0.0","name":"PackCheck 99","body":"notes","html_url":"https://github.com/X1NPAR1/PackCheck/releases/tag/v99.0.0","published_at":"2026-10-28T10:00:00Z",
         "assets":[{"name":"PackCheck-win-x64.zip","browser_download_url":"https://github.com/x/PackCheck-win-x64.zip","size":10},
                   {"name":"PackCheck-win-x64.zip.sha256","browser_download_url":"https://github.com/x/PackCheck-win-x64.zip.sha256","size":64}]}
        """;

    /// <summary>The full service (HTTP + state file + clock): prompt on day 1 and 2, install on the first start of day 3.</summary>
    [Fact]
    public async Task ThreeDayGracePeriod_EndToEnd()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Json(Release)));
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            UpdateService NewSession() => new(new GitHubReleaseClient(http), http, state, clock);

            // Day 1, first start: ask; user postpones.
            var d1 = await NewSession().CheckAsync(false, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal(UpdateAction.Prompt, d1.Action);
            NewSession().MarkPrompted(d1.Release!);

            // Day 1, later start: still asks.
            clock.Now = clock.Now.AddHours(10);
            Assert.Equal(UpdateAction.Prompt, (await NewSession().CheckAsync(false, TimeSpan.FromSeconds(5), CancellationToken.None)).Action);

            // Day 2: asks, one day left.
            clock.Now = clock.Now.AddDays(1);
            var d2 = await NewSession().CheckAsync(false, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal(UpdateAction.Prompt, d2.Action);
            Assert.Equal(1, d2.DaysLeft);
            NewSession().MarkPrompted(d2.Release!);

            // Day 3, first start: installs without asking.
            clock.Now = clock.Now.AddDays(1);
            Assert.Equal(UpdateAction.ForceInstall, (await NewSession().CheckAsync(false, TimeSpan.FromSeconds(5), CancellationToken.None)).Action);
        }
        finally
        {
            File.Delete(state);
        }
    }

    [Fact]
    public async Task AutoUpdate_InstallsOnFirstStart()
    {
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Json(Release)));
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var d = await new UpdateService(new GitHubReleaseClient(http), http, state).CheckAsync(true, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(UpdateAction.AutoInstall, d.Action);
        File.Delete(state);
    }

    [Fact]
    public async Task RateLimitedGitHub_IsNoUpdate()
    {
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Json("{}", HttpStatusCode.Forbidden)));
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var d = await new UpdateService(new GitHubReleaseClient(http), http, state).CheckAsync(false, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(UpdateAction.None, d.Action);
        File.Delete(state);
    }
}

/// <summary>CurseForge against recorded-style responses (no API key is available for live tests).</summary>
public class CurseForgeTests
{
    private const string Search = """
        {"data":[{"id":238222,"slug":"jei","name":"Just Enough Items","summary":"View items","downloadCount":350000000.0,
          "authors":[{"name":"mezz"}],"logo":{"thumbnailUrl":"https://media.forgecdn.net/avatars/thumbnails/29/69/64/64/635838945588716414.jpeg"},
          "links":{"websiteUrl":"https://www.curseforge.com/minecraft/mc-mods/jei"},
          "latestFilesIndexes":[{"gameVersion":"1.21.1","modLoader":6},{"gameVersion":"1.20.1","modLoader":1}]}],
         "pagination":{"index":0,"pageSize":20,"resultCount":1,"totalCount":1}}
        """;

    [Fact]
    public async Task Search_SendsKey_AndMapsVersionsAndLoaders()
    {
        var handler = new StubHandler(_ => StubHandler.Json(Search));
        using var http = new HttpClient(handler);
        var page = await new CurseForgeCatalog(http, () => "test-key").BrowseAsync(CatalogKind.Mod, "jei", CatalogSort.Downloads, 0, 20, CancellationToken.None);
        Assert.Null(page.Error);
        var item = Assert.Single(page.Items);
        Assert.Equal(["1.21.1", "1.20.1"], item.GameVersions);
        Assert.Equal(["neoforge", "forge"], item.Loaders);
        Assert.Equal("test-key", handler.Requests[0].Headers.GetValues("x-api-key").Single());
        Assert.Contains("classId=6", handler.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledThirdPartyDownload_IsExplained()
    {
        const string files = """{"data":[{"id":1,"fileName":"x.jar","displayName":"x","fileLength":10,"downloadUrl":null,"hashes":[{"value":"aa","algo":1}]}]}""";
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Json(files)));
        var r = await new CurseForgeMods(http, () => "k").GetFileAsync("1", ModKind.Mod, "1.21.1", "neoforge", CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("third-party", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Modpack_UsesServerPack()
    {
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/files/77", StringComparison.Ordinal)
            ? StubHandler.Json("""{"data":{"id":77,"fileName":"server.zip","displayName":"Server Pack","fileLength":5,"downloadUrl":"https://edge.forgecdn.net/files/server.zip","hashes":[{"value":"bb","algo":1}]}}""")
            : StubHandler.Json("""{"data":[{"id":1,"fileName":"client.zip","displayName":"Client","fileLength":5,"downloadUrl":"https://x/client.zip","serverPackFileId":77,"hashes":[]}]}"""));
        using var http = new HttpClient(handler);
        var r = await new CurseForgeMods(http, () => "k").GetFileAsync("5", ModKind.Modpack, "1.21.1", "neoforge", CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal("server.zip", r.Value!.FileName);
        Assert.Equal("bb", r.Value.Hash);
    }

    [Fact]
    public async Task HttpError_IsReturnedAsValue()
    {
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Json("{}", HttpStatusCode.Forbidden)));
        var page = await new CurseForgeCatalog(http, () => "bad").BrowseAsync(CatalogKind.Mod, string.Empty, CatalogSort.Downloads, 0, 20, CancellationToken.None);
        Assert.Equal("API key rejected (HTTP 403)", page.Error);
    }
}
