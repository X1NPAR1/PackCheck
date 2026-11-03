using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using PackCheck.Updater;

namespace PackCheck.Tests;

/// <summary>Resumable, retried and verified update downloads, plus the one-time "what's new" dialog.</summary>
public sealed class UpdateDownloadTests : IDisposable
{
    private static readonly byte[] Package = [.. Enumerable.Range(0, 1000).Select(i => (byte)(i * 7))];
    private static readonly Uri PackageUrl = new("https://github.com/x/PackCheck-Setup-99.0.0.exe");
    private static readonly Uri SumUrl = new("https://github.com/x/PackCheck-Setup-99.0.0.exe.sha256");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pc-upd-" + Guid.NewGuid().ToString("N"));

    private string StateFile => Path.Combine(_root, "update-state.json");

    private string PartFile => Path.Combine(_root, "updates", "v99.0.0", "PackCheck-Setup-99.0.0.exe.part");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static ReleaseInfo Release() => new(
        "v99.0.0",
        new Version(99, 0, 0),
        "PackCheck 99",
        "notes",
        DateTimeOffset.UnixEpoch,
        new Uri("https://github.com/X1NPAR1/PackCheck/releases/tag/v99.0.0"),
        new ReleaseAsset("PackCheck-Setup-99.0.0.exe", PackageUrl, Package.Length),
        new ReleaseAsset("PackCheck-Setup-99.0.0.exe.sha256", SumUrl, 64));

    private static HttpResponseMessage Sum(byte[] content) =>
        new(HttpStatusCode.OK) { Content = new StringContent(Convert.ToHexStringLower(SHA256.HashData(content)) + "  PackCheck-Setup-99.0.0.exe") };

    private static HttpResponseMessage Full(byte[] content) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

    private static HttpResponseMessage Partial(byte[] content, long from)
    {
        var body = new ByteArrayContent(content[(int)from..]);
        body.Headers.ContentRange = new ContentRangeHeaderValue(from, content.Length - 1, content.Length);
        return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = body };
    }

    private UpdateService Service(HttpClient http) =>
        new(new GitHubReleaseClient(http), http, StateFile) { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero] };

    private void WritePart(int length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PartFile)!);
        File.WriteAllBytes(PartFile, Package[..length]);
    }

    /// <summary>Collects reports synchronously (Progress&lt;T&gt; would post them to the thread pool).</summary>
    private sealed class Reports : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }

    [Fact]
    public async Task PartialFile_ResumesWith206_AndAppends()
    {
        WritePart(400);
        var handler = new StubHandler(req => req.RequestUri == SumUrl ? Sum(Package) : Partial(Package, req.Headers.Range!.Ranges.Single().From!.Value));
        using var http = new HttpClient(handler);
        var progress = new Reports();

        var path = await Service(http).DownloadAndStageAsync(Release(), progress, CancellationToken.None);

        Assert.Equal(Package, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(PartFile));
        var download = handler.Requests.Single(r => r.RequestUri == PackageUrl);
        Assert.Equal(400, download.Headers.Range!.Ranges.Single().From);

        // Progress starts at the resumed offset, never goes back and ends at 100 %.
        Assert.Equal(0.4, progress.Values[0], 3);
        Assert.Equal(progress.Values.Order(), progress.Values);
        Assert.Equal(1, progress.Values[^1]);
    }

    [Fact]
    public async Task ServerIgnoresRange_200_RestartsFile()
    {
        WritePart(400);
        var handler = new StubHandler(req => req.RequestUri == SumUrl ? Sum(Package) : Full(Package));
        using var http = new HttpClient(handler);
        var progress = new Reports();

        var path = await Service(http).DownloadAndStageAsync(Release(), progress, CancellationToken.None);

        Assert.Equal(Package, await File.ReadAllBytesAsync(path));
        Assert.Equal(0, progress.Values[0]);
        Assert.All(progress.Values, v => Assert.InRange(v, 0, 1));
    }

    [Fact]
    public async Task ServiceUnavailable_IsRetried()
    {
        var calls = 0;
        var handler = new StubHandler(req => req.RequestUri == SumUrl ? Sum(Package)
            : ++calls <= 2 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Full(Package));
        using var http = new HttpClient(handler);

        var path = await Service(http).DownloadAndStageAsync(Release(), null, CancellationToken.None);

        Assert.Equal(Package, await File.ReadAllBytesAsync(path));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ServiceUnavailable_GivesUpAfterThreeRetries()
    {
        var calls = 0;
        var handler = new StubHandler(req => req.RequestUri == SumUrl ? Sum(Package) : Count(ref calls, new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var http = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Service(http).DownloadAndStageAsync(Release(), null, CancellationToken.None));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task NotFound_IsNotRetried()
    {
        var calls = 0;
        var handler = new StubHandler(req => req.RequestUri == SumUrl ? Sum(Package) : Count(ref calls, new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => Service(http).DownloadAndStageAsync(Release(), null, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Cancellation_StopsRetrying()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri == SumUrl)
            {
                return Sum(Package);
            }

            cts.Cancel();
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        using var http = new HttpClient(handler);
        var service = new UpdateService(new GitHubReleaseClient(http), http, StateFile) { RetryDelays = [TimeSpan.FromMinutes(5)] };

        await Assert.ThrowsAnyAsync<Exception>(() => service.DownloadAndStageAsync(Release(), null, cts.Token));
        Assert.Single(handler.Requests, r => r.RequestUri == PackageUrl);
    }

    [Fact]
    public async Task HashMismatch_IsRejected_AndPartRemoved()
    {
        var tampered = Package.ToArray();
        tampered[10] ^= 0xFF;
        var handler = new StubHandler(req => req.RequestUri == SumUrl ? Sum(Package) : Full(tampered));
        using var http = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(http).DownloadAndStageAsync(Release(), null, CancellationToken.None));

        Assert.Contains("Checksum mismatch", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(PartFile));
        Assert.False(File.Exists(PartFile[..^5]));
    }

    /// <summary>Sends a few bytes, then keeps the connection open without data until the read is cancelled.</summary>
    private sealed class StallingStream(byte[] head) : Stream
    {
        private bool _sent;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                head.CopyTo(buffer);
                return head.Length;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task StalledDownload_IsRetried_AndResumes()
    {
        var calls = 0;
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri == SumUrl)
            {
                return Sum(Package);
            }

            if (++calls == 1)
            {
                var content = new StreamContent(new StallingStream(Package[..300]));
                content.Headers.ContentLength = Package.Length;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            return Partial(Package, req.Headers.Range!.Ranges.Single().From!.Value);
        });
        using var http = new HttpClient(handler);
        var service = new UpdateService(new GitHubReleaseClient(http), http, StateFile) { RetryDelays = [TimeSpan.Zero], StallTimeout = TimeSpan.FromMilliseconds(200) };

        var path = await service.DownloadAndStageAsync(Release(), null, CancellationToken.None);

        Assert.Equal(Package, await File.ReadAllBytesAsync(path));
        Assert.Equal(2, calls);
        Assert.Equal(300, handler.Requests.Last().Headers.Range!.Ranges.Single().From);
    }

    [Fact]
    public async Task TruncatedDownload_IsRetried_AndResumes()
    {
        var calls = 0;
        var handler = new StubHandler(req => req.RequestUri == SumUrl ? Sum(Package)
            : ++calls == 1 ? Full(Package[..300])
            : Partial(Package, req.Headers.Range!.Ranges.Single().From!.Value));
        using var http = new HttpClient(handler);

        var path = await Service(http).DownloadAndStageAsync(Release(), null, CancellationToken.None);

        Assert.Equal(Package, await File.ReadAllBytesAsync(path));
        Assert.Equal(300, handler.Requests.Last().Headers.Range!.Ranges.Single().From);
    }

    [Fact]
    public async Task WhatsNew_UnwritableCache_StillReturnsNotes()
    {
        Directory.CreateDirectory(Path.Combine(_root, "cache"));
        await File.WriteAllTextAsync(StateFile, """{"LastRunVersion":"0.0.1"}""");

        // A file where the cache folder should be makes the cache write fail.
        await File.WriteAllTextAsync(Path.Combine(_root, "cache", "release-notes"), "x");
        var current = UpdateService.CurrentVersion.ToString(3);
        var body = $$"""{"tag_name":"v{{current}}","name":"x","body":"notes","html_url":"https://github.com/X1NPAR1/PackCheck/releases/tag/v{{current}}","assets":[]}""";
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Json(body)));

        var w = await Service(http).TakeWhatsNewAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("notes", w!.Notes);
    }

    [Fact]
    public async Task WhatsNew_UnreadableCache_FallsBackToDownload()
    {
        var current = UpdateService.CurrentVersion.ToString(3);
        var cache = Path.Combine(_root, "cache", "release-notes");
        Directory.CreateDirectory(cache);
        await File.WriteAllTextAsync(StateFile, """{"LastRunVersion":"0.0.1"}""");
        var cacheFile = Path.Combine(cache, "v" + current + ".md");
        await File.WriteAllTextAsync(cacheFile, "old");
        var body = $$"""{"tag_name":"v{{current}}","name":"x","body":"fresh","html_url":"https://github.com/X1NPAR1/PackCheck/releases/tag/v{{current}}","assets":[]}""";
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Json(body)));

        WhatsNew? w;
        using (new FileStream(cacheFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            w = await Service(http).TakeWhatsNewAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }

        Assert.Equal("fresh", w!.Notes);
    }

    private static HttpResponseMessage Count(ref int calls, HttpResponseMessage response)
    {
        calls++;
        return response;
    }

    [Theory]
    [InlineData("3.3.0", true, "3.4.0", true)]
    [InlineData("3.4.0", true, "3.4.0", false)]
    [InlineData("3.5.0", true, "3.4.0", false)]
    [InlineData(null, true, "3.4.0", true)]
    [InlineData(null, false, "3.4.0", false)]
    [InlineData("3.3.0", true, "0.0.0", false)]
    public void WhatsNew_Policy(string? last, bool existed, string current, bool show) =>
        Assert.Equal(show, UpdatePolicy.ShouldShowWhatsNew(new UpdateState { LastRunVersion = last }, existed, Version.Parse(current)));

    [Fact]
    public void Reconcile_KeepsLastRunVersion()
    {
        var state = new UpdateState { NotifiedVersion = "v1.0.0", FirstNotified = new DateOnly(2026, 1, 1), LastRunVersion = "1.0.0" };
        var next = UpdatePolicy.Reconcile(state, new Version(1, 0, 0));
        Assert.Null(next.NotifiedVersion);
        Assert.Equal("1.0.0", next.LastRunVersion);
    }

    /// <summary>
    /// The test host has its own version; an older LastRunVersion makes the service report it once,
    /// cache the notes and stay quiet on the next start.
    /// </summary>
    [Fact]
    public async Task WhatsNew_ShownOncePerVersion()
    {
        var current = UpdateService.CurrentVersion;
        Assert.True(current > new Version(0, 0, 0), "test assembly needs a version");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(StateFile, """{"LastRunVersion":"0.0.1"}""");
        var body = $$"""{"tag_name":"v{{current.ToString(3)}}","name":"x","body":"## Fixed\n- things","html_url":"https://github.com/X1NPAR1/PackCheck/releases/tag/v{{current.ToString(3)}}","assets":[]}""";
        var handler = new StubHandler(_ => StubHandler.Json(body));
        using var http = new HttpClient(handler);

        var first = await Service(http).TakeWhatsNewAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        var second = await Service(http).TakeWhatsNewAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal("## Fixed\n- things", first.Notes);
        Assert.Equal(current, first.Version);
        Assert.Null(second);
        Assert.EndsWith("/releases/tags/v" + current.ToString(3), handler.Requests.Single().RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, "cache", "release-notes", "v" + current.ToString(3) + ".md")));
    }

    [Fact]
    public async Task WhatsNew_FreshInstall_ShowsNothing()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("no network expected")));
        Assert.Null(await Service(http).TakeWhatsNewAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Null(await Service(http).TakeWhatsNewAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task WhatsNew_Offline_StillLinksToRelease()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(StateFile, """{"LastRunVersion":"0.0.1"}""");
        using var http = new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline")));
        var w = await Service(http).TakeWhatsNewAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.NotNull(w);
        Assert.Equal(string.Empty, w.Notes);
        Assert.Contains("/releases/tag/v", w.PageUrl.ToString(), StringComparison.Ordinal);
    }
}
