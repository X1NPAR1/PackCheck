using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using PackCheck.Core;

namespace PackCheck.Updater;

/// <summary>Release notes shown once after the app first runs on <paramref name="Version"/>.</summary>
public sealed record WhatsNew(Version Version, string Notes, Uri PageUrl);

/// <summary>Checks GitHub, stores the grace-period state and installs verified releases.</summary>
public sealed class UpdateService(GitHubReleaseClient client, HttpClient http, string? stateFile = null, TimeProvider? clock = null)
{
    public const long MaxPackageBytes = 512L * 1024 * 1024;
    public const string HelperName = "PackCheck.UpdateHelper.exe";

    private readonly string _stateFile = stateFile ?? AppPaths.UpdateStateFile;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly string _updatesRoot = Path.Combine(Path.GetDirectoryName(stateFile ?? AppPaths.UpdateStateFile)!, "updates");
    private readonly string _notesCache = Path.Combine(Path.GetDirectoryName(stateFile ?? AppPaths.UpdateStateFile)!, "cache", "release-notes");

    /// <summary>Waits between download attempts; one retry per entry.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    /// <summary>A download that receives no data for this long is aborted and retried.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    public static Version CurrentVersion { get; } =
        ReleaseInfo.TryParseTag(Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0", out var v)
            ? v
            : new Version(0, 0, 0);

    public UpdateDecision? LastDecision { get; private set; }

    /// <summary>Never throws for network problems: an offline launch simply reports "no update".</summary>
    public async Task<UpdateDecision> CheckAsync(bool autoUpdate, TimeSpan timeout, CancellationToken ct)
    {
        var state = UpdatePolicy.Reconcile(LoadState(), CurrentVersion);
        SaveState(state);

        ReleaseInfo? latest = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            latest = await client.GetLatestAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or UriFormatException)
        {
            latest = null;
        }

        LastDecision = UpdatePolicy.Decide(CurrentVersion, latest, state, autoUpdate, Today);
        return LastDecision;
    }

    public void MarkPrompted(ReleaseInfo release) =>
        SaveState(UpdatePolicy.AfterPrompt(LoadState(), release, Today));

    public UpdateState LoadState()
    {
        try
        {
            return File.Exists(_stateFile) ? JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(_stateFile)) ?? new UpdateState() : new UpdateState();
        }
        catch (JsonException)
        {
            return new UpdateState();
        }
    }

    private void SaveState(UpdateState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        File.WriteAllText(_stateFile, JsonSerializer.Serialize(state));
    }

    /// <summary>
    /// Downloads the package into a .part file (resuming a partial one with an HTTP Range request),
    /// retries network errors and 5xx answers, verifies SHA-256 and extracts it to a staging folder.
    /// </summary>
    public async Task<string> DownloadAndStageAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken ct)
    {
        if (release.Asset is null || release.Checksum is null)
        {
            throw new InvalidOperationException("The release does not contain a package and checksum.");
        }

        if (release.Asset.Size > MaxPackageBytes)
        {
            throw new InvalidOperationException("Update package is larger than allowed.");
        }

        var dir = Path.Combine(_updatesRoot, release.Tag);
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, release.Asset.Name);
        var partPath = zipPath + ".part";
        var staging = Path.Combine(dir, "files");

        // A finished package or staging folder from an earlier attempt is redone; a .part file is kept to resume.
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, true);
        }

        File.Delete(zipPath);

        var checksum = await WithRetryAsync(() => http.GetStringAsync(release.Checksum.Url, ct), ct).ConfigureAwait(false);
        var expected = checksum.Trim().Split(' ', '\t')[0];
        await WithRetryAsync(() => DownloadOnceAsync(release.Asset, partPath, progress, ct), ct).ConfigureAwait(false);

        progress?.Report(1);
        var actual = await Hashing.Sha256FileAsync(partPath, ct).ConfigureAwait(false);
        if (!Hashing.EqualsHex(actual, expected))
        {
            File.Delete(partPath);
            throw new InvalidOperationException("Checksum mismatch: the downloaded update was rejected.");
        }

        File.Move(partPath, zipPath, true);

        // Installers are handed to the helper as-is; a portable zip is extracted first.
        if (!zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return zipPath;
        }

        ZipFile.ExtractToDirectory(zipPath, staging);
        if (!File.Exists(Path.Combine(staging, "PackCheck.exe")))
        {
            throw new InvalidOperationException("The update package does not contain PackCheck.exe.");
        }

        return staging;
    }

    private async Task<bool> DownloadOnceAsync(ReleaseAsset asset, string partPath, IProgress<double>? progress, CancellationToken ct)
    {
        var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The partial file no longer fits the server copy: start over on the next attempt.
            File.Delete(partPath);
            throw new HttpRequestException("The server rejected the resume range.", null, response.StatusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Download failed with HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }

        // 206 with a matching range appends; a 200 means the server ignored Range, so the file restarts.
        var range = response.Content.Headers.ContentRange;
        var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent && range?.From == existing;
        if (!append)
        {
            existing = 0;
        }

        var length = response.Content.Headers.ContentLength;
        var total = append
            ? range!.Length ?? (length is { } l ? existing + l : asset.Size)
            : length ?? asset.Size;

        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(partPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        var read = existing;
        progress?.Report(total > 0 ? Math.Min(1, (double)read / total) : 0);
        // A connection that stays open without sending data would block forever; a stall becomes a retry that resumes.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            stall.CancelAfter(StallTimeout);
            var n = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
            if (read > MaxPackageBytes)
            {
                throw new InvalidOperationException("Update package exceeded the size limit while downloading.");
            }

            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            progress?.Report(total > 0 ? Math.Min(1, (double)read / total) : 0);
        }

        return true;
    }

    /// <summary>Retries network errors, timeouts and 5xx answers, waiting <see cref="RetryDelays"/> between attempts.</summary>
    private async Task<T> WithRetryAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < RetryDelays.Count && !ct.IsCancellationRequested && IsTransient(ex))
            {
                await Task.Delay(RetryDelays[attempt], _clock, ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } code } => (int)code >= 500 || code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.RequestedRangeNotSatisfiable,
        IOException or OperationCanceledException => true,
        _ => false,
    };

    /// <summary>
    /// Returns the release notes to show once after the app started on a newer version, or null.
    /// Records the running version either way, so the dialog appears only once per version.
    /// Notes are cached next to the state file; offline, the dialog still links to the release.
    /// </summary>
    public async Task<WhatsNew?> TakeWhatsNewAsync(TimeSpan timeout, CancellationToken ct)
    {
        var existed = File.Exists(_stateFile);
        var state = LoadState();
        var current = CurrentVersion;
        var show = UpdatePolicy.ShouldShowWhatsNew(state, existed, current);
        if (state.LastRunVersion != current.ToString(3))
        {
            SaveState(state with { LastRunVersion = current.ToString(3) });
        }

        if (!show)
        {
            return null;
        }

        var tag = "v" + current.ToString(3);
        var page = new Uri($"https://github.com/{GitHubReleaseClient.DefaultOwner}/{GitHubReleaseClient.DefaultRepo}/releases/tag/{tag}");
        var cacheFile = Path.Combine(_notesCache, tag + ".md");
        try
        {
            if (File.Exists(cacheFile))
            {
                return new WhatsNew(current, await File.ReadAllTextAsync(cacheFile, ct).ConfigureAwait(false), page);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable cache falls through to a fresh download.
        }

        ReleaseInfo? release = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            release = await client.GetByVersionAsync(current, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or UriFormatException or IOException)
        {
            // Offline or rate limited: the dialog still links to the release page.
        }

        if (release is null)
        {
            return new WhatsNew(current, string.Empty, page);
        }

        try
        {
            Directory.CreateDirectory(_notesCache);
            await File.WriteAllTextAsync(cacheFile, release.Notes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache is only a convenience; the notes are shown either way.
        }

        return new WhatsNew(current, release.Notes, release.PageUrl);
    }

    /// <summary>
    /// Starts the helper from a temp copy (so it can overwrite the install folder) and returns.
    /// The caller must shut the app down right after.
    /// </summary>
    public static void LaunchInstaller(string staging)
    {
        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var helperSource = Path.Combine(staging, HelperName);
        if (!Directory.Exists(staging) || !File.Exists(helperSource))
        {
            helperSource = Path.Combine(installDir, HelperName);
        }

        var mode = staging.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "exe"
            : staging.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? "msi"
            : "zip";

        var helper = Path.Combine(Path.GetTempPath(), "PackCheck.UpdateHelper-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(helperSource, helper);

        var psi = new ProcessStartInfo(helper) { UseShellExecute = false };
        psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (mode != "zip")
        {
            psi.ArgumentList.Add(mode);
        }

        psi.ArgumentList.Add(staging);
        psi.ArgumentList.Add(installDir);
        psi.ArgumentList.Add(Path.Combine(installDir, "PackCheck.exe"));
        Process.Start(psi);
    }
}
