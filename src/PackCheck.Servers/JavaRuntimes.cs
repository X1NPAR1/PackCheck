using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PackCheck.Core;

namespace PackCheck.Servers;

public sealed record JavaRuntime(string JavaExe, int FeatureVersion, string Display);

/// <summary>Finds installed Java runtimes and installs Eclipse Temurin JREs from the Adoptium API.</summary>
public sealed partial class JavaRuntimes(HttpClient http)
{
    [GeneratedRegex("version \"(?<v>[0-9._]+)")]
    private static partial Regex VersionLine();

    public async Task<IReadOnlyList<JavaRuntime>> DetectAsync(CancellationToken ct)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddHome(string? home)
        {
            if (!string.IsNullOrWhiteSpace(home))
            {
                var exe = Path.Combine(home, "bin", "java.exe");
                if (File.Exists(exe))
                {
                    candidates.Add(Path.GetFullPath(exe));
                }
            }
        }

        AddHome(Environment.GetEnvironmentVariable("JAVA_HOME"));
        var roots = new[]
        {
            AppPaths.Runtimes,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zulu"),
        };
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                AddHome(dir);
            }
        }

        var result = new List<JavaRuntime>();
        foreach (var exe in candidates)
        {
            var v = await ProbeAsync(exe, ct).ConfigureAwait(false);
            if (v is not null)
            {
                result.Add(v);
            }
        }

        return result.OrderByDescending(r => r.FeatureVersion).ToList();
    }

    public static async Task<JavaRuntime?> ProbeAsync(string javaExe, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(javaExe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-version");
        try
        {
            using var p = Process.Start(psi)!;
            var err = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            var feature = ParseFeature(err);
            return feature is null ? null : new JavaRuntime(javaExe, feature.Value, err.Split('\n')[0].Trim());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>"1.8.0_402" → 8, "21.0.5" → 21.</summary>
    public static int? ParseFeature(string versionOutput)
    {
        var m = VersionLine().Match(versionOutput);
        if (!m.Success)
        {
            return null;
        }

        var parts = m.Groups["v"].Value.Split('.', '_');
        if (!int.TryParse(parts[0], out var first))
        {
            return null;
        }

        return first == 1 && parts.Length > 1 && int.TryParse(parts[1], out var second) ? second : first;
    }

    /// <summary>Temurin publishes 8, 11, 17, 21 and 25; map a requirement to the release to install.</summary>
    public static int InstallableFeature(int required) => required switch
    {
        <= 8 => 8,
        <= 11 => 11,
        <= 17 => 17,
        <= 21 => 21,
        _ => 25,
    };

    /// <summary>
    /// Returns a runtime that satisfies the requirement, installing Temurin when none is present.
    /// Old Minecraft versions (Java 8 era) prefer exactly Java 8, because newer Java breaks them.
    /// </summary>
    public async Task<JavaRuntime> EnsureAsync(int required, IReadOnlyList<JavaRuntime> known, IProgress<double>? progress, CancellationToken ct)
    {
        var match = required <= 8
            ? known.FirstOrDefault(r => r.FeatureVersion == 8)
            : known.Where(r => r.FeatureVersion >= required).OrderBy(r => r.FeatureVersion).FirstOrDefault();
        return match ?? await InstallTemurinAsync(InstallableFeature(required), progress, ct).ConfigureAwait(false);
    }

    /// <summary>Downloads a Temurin JRE zip, verifies its SHA-256 and extracts it into the PackCheck runtimes folder.</summary>
    public static bool IsJdk(string javaExe) => File.Exists(Path.Combine(Path.GetDirectoryName(javaExe)!, "javac.exe"));

    /// <summary>A JDK (with javac) is needed to compile Spigot/CraftBukkit with BuildTools.</summary>
    public async Task<JavaRuntime> EnsureJdkAsync(int required, IReadOnlyList<JavaRuntime> known, IProgress<double>? progress, CancellationToken ct)
    {
        var match = known.Where(r => r.FeatureVersion >= required && IsJdk(r.JavaExe)).OrderBy(r => r.FeatureVersion).FirstOrDefault();
        return match ?? await InstallTemurinAsync(InstallableFeature(required), progress, ct, jdk: true).ConfigureAwait(false);
    }

    /// <summary>One install at a time: two servers starting together must not download into the same files.</summary>
    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    public async Task<JavaRuntime> InstallTemurinAsync(int feature, IProgress<double>? progress, CancellationToken ct, bool jdk = false)
    {
        await InstallGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Already installed (by an earlier run or by the call we just waited for)? A running server may be using it.
            var existing = Path.Combine(AppPaths.Runtimes, jdk ? $"temurin-{feature}-jdk" : $"temurin-{feature}");
            if (Directory.Exists(existing)
                && Directory.EnumerateFiles(existing, "java.exe", SearchOption.AllDirectories).FirstOrDefault(f => f.EndsWith(Path.Combine("bin", "java.exe"), StringComparison.OrdinalIgnoreCase)) is { } found
                && await ProbeAsync(found, ct).ConfigureAwait(false) is { } ready && ready.FeatureVersion >= feature)
            {
                return ready;
            }

            return await DownloadTemurinAsync(feature, progress, jdk, ct).ConfigureAwait(false);
        }
        finally
        {
            InstallGate.Release();
        }
    }

    private async Task<JavaRuntime> DownloadTemurinAsync(int feature, IProgress<double>? progress, bool jdk, CancellationToken ct)
    {
        var image = jdk ? "jdk" : "jre";
        var api = new Uri($"https://api.adoptium.net/v3/assets/latest/{feature}/hotspot?os=windows&architecture=x64&image_type={image}&vendor=eclipse");
        await using var s = await http.GetStreamAsync(api, ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
        var pkg = doc.RootElement[0].GetProperty("binary").GetProperty("package");
        var url = new Uri(pkg.GetProperty("link").GetString()!);
        var sha = pkg.GetProperty("checksum").GetString()!;

        Directory.CreateDirectory(AppPaths.Runtimes);
        var zip = Path.Combine(AppPaths.Runtimes, $"temurin-{feature}-{image}.zip");
        await VerifiedDownloader.DownloadAsync(http, url, zip, sha, HashAlgorithmName.SHA256, 300L * 1024 * 1024, progress, ct).ConfigureAwait(false);

        var target = Path.Combine(AppPaths.Runtimes, jdk ? $"temurin-{feature}-jdk" : $"temurin-{feature}");
        if (Directory.Exists(target))
        {
            Directory.Delete(target, true);
        }

        ZipFile.ExtractToDirectory(zip, target);
        File.Delete(zip);
        var exe = Directory.EnumerateFiles(target, "java.exe", SearchOption.AllDirectories).First(f => f.EndsWith(Path.Combine("bin", "java.exe"), StringComparison.OrdinalIgnoreCase));
        return await ProbeAsync(exe, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("Installed runtime could not be started.");
    }
}
