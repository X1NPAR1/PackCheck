using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PackCheck.Updater;

public sealed record ReleaseAsset(string Name, Uri Url, long Size);

public sealed record ReleaseInfo(string Tag, Version Version, string Title, string Notes, DateTimeOffset PublishedAt, Uri PageUrl, ReleaseAsset? Asset, ReleaseAsset? Checksum)
{
    public static bool TryParseTag(string tag, out Version version)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        var dash = t.IndexOfAny(['-', '+']);
        if (dash >= 0)
        {
            t = t[..dash];
        }

        if (Version.TryParse(t, out var v))
        {
            version = new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }
}

/// <summary>Reads the latest published (non-draft, non-prerelease) release from the GitHub REST API.</summary>
/// <summary>How this copy of PackCheck was installed; decides which release asset updates it.</summary>
public enum InstallKind
{
    Portable,
    SetupExe,
    Msi,
}

public sealed class GitHubReleaseClient(HttpClient http, string owner = GitHubReleaseClient.DefaultOwner, string repo = GitHubReleaseClient.DefaultRepo)
{
    public const string DefaultOwner = "X1NPAR1";
    public const string DefaultRepo = "PackCheck";
    public const string AssetName = "PackCheck-win-x64.zip";

    /// <summary>The installers write install-type.txt ("exe" or "msi") next to the app.</summary>
    public static InstallKind DetectInstallKind(string? appDir = null)
    {
        var marker = Path.Combine(appDir ?? AppContext.BaseDirectory, "install-type.txt");
        var text = File.Exists(marker) ? File.ReadAllText(marker).Trim() : string.Empty;
        return text switch
        {
            "exe" => InstallKind.SetupExe,
            "msi" => InstallKind.Msi,
            _ => InstallKind.Portable,
        };
    }

    public static bool Matches(string assetName, InstallKind kind) => kind switch
    {
        InstallKind.SetupExe => assetName.StartsWith("PackCheck-Setup-", StringComparison.OrdinalIgnoreCase) && assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
        InstallKind.Msi => assetName.StartsWith("PackCheck-", StringComparison.OrdinalIgnoreCase) && assetName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase),
        _ => assetName.Equals(AssetName, StringComparison.OrdinalIgnoreCase),
    };

    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://api.github.com/repos/{owner}/{repo}/releases/latest"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PackCheck", UpdateService.CurrentVersion.ToString(3)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return Parse(doc.RootElement, DetectInstallKind());
    }

    public static ReleaseInfo? Parse(JsonElement root, InstallKind kind = InstallKind.Portable)
    {
        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!ReleaseInfo.TryParseTag(tag, out var version))
        {
            return null;
        }

        var all = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                var url = new Uri(a.GetProperty("browser_download_url").GetString()!);
                if (url.Scheme == Uri.UriSchemeHttps && url.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
                {
                    all.Add(new ReleaseAsset(a.GetProperty("name").GetString() ?? string.Empty, url, a.GetProperty("size").GetInt64()));
                }
            }
        }

        var zip = all.FirstOrDefault(a => Matches(a.Name, kind));
        var sum = zip is null ? null : all.FirstOrDefault(a => a.Name.Equals(zip.Name + ".sha256", StringComparison.OrdinalIgnoreCase));

        var published = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(p.GetString()!, CultureInfo.InvariantCulture)
            : DateTimeOffset.MinValue;

        return new ReleaseInfo(
            tag,
            version,
            root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : tag,
            root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString()! : string.Empty,
            published,
            new Uri(root.GetProperty("html_url").GetString()!),
            zip,
            sum);
    }
}
