using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using PackCheck.Core;

namespace PackCheck.Updater;

/// <summary>Checks GitHub, stores the grace-period state and installs verified releases.</summary>
public sealed class UpdateService(GitHubReleaseClient client, HttpClient http, string? stateFile = null)
{
    public const long MaxPackageBytes = 512L * 1024 * 1024;
    public const string HelperName = "PackCheck.UpdateHelper.exe";

    private readonly string _stateFile = stateFile ?? AppPaths.UpdateStateFile;

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

        LastDecision = UpdatePolicy.Decide(CurrentVersion, latest, state, autoUpdate, DateOnly.FromDateTime(DateTime.Now));
        return LastDecision;
    }

    public void MarkPrompted(ReleaseInfo release) =>
        SaveState(UpdatePolicy.AfterPrompt(LoadState(), release, DateOnly.FromDateTime(DateTime.Now)));

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

    /// <summary>Downloads the package, verifies SHA-256 and extracts it to a staging folder.</summary>
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

        var dir = Path.Combine(AppPaths.DataRoot, "updates", release.Tag);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }

        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, release.Asset.Name);

        var expected = (await http.GetStringAsync(release.Checksum.Url, ct).ConfigureAwait(false)).Trim().Split(' ', '\t')[0];

        using (var response = await http.GetAsync(release.Asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.Asset.Size;
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(zipPath);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                read += n;
                if (read > MaxPackageBytes)
                {
                    throw new InvalidOperationException("Update package exceeded the size limit while downloading.");
                }

                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                progress?.Report(total > 0 ? (double)read / total : 0);
            }
        }

        var actual = await Hashing.Sha256FileAsync(zipPath, ct).ConfigureAwait(false);
        if (!Hashing.EqualsHex(actual, expected))
        {
            File.Delete(zipPath);
            throw new InvalidOperationException("Checksum mismatch: the downloaded update was rejected.");
        }

        // Installers are handed to the helper as-is; a portable zip is extracted first.
        if (!zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return zipPath;
        }

        var staging = Path.Combine(dir, "files");
        ZipFile.ExtractToDirectory(zipPath, staging);
        if (!File.Exists(Path.Combine(staging, "PackCheck.exe")))
        {
            throw new InvalidOperationException("The update package does not contain PackCheck.exe.");
        }

        return staging;
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
