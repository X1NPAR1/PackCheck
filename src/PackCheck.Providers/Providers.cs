using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace PackCheck.Providers;

public sealed record ProjectHit(string Provider, string Id, string Slug, string Title, string Description, long Downloads, string? Author);

public sealed record ProjectFile(string FileName, Uri Url, string Hash, HashAlgorithmName Algorithm, long Size, string VersionNumber);

public sealed record ProviderResult<T>(T? Value, string? Error)
{
    public bool Ok => Error is null;
}

public interface IPluginProvider
{
    string Name { get; }

    Task<ProviderResult<IReadOnlyList<ProjectHit>>> SearchAsync(string query, string? minecraftVersion, CancellationToken ct);

    Task<ProviderResult<ProjectFile>> GetLatestFileAsync(string projectId, string minecraftVersion, CancellationToken ct);
}

/// <summary>Shared HTTP behavior: timeout, in-memory cache, and failures returned as values (never thrown).</summary>
public abstract class ProviderBase(HttpClient http, TimeSpan? timeout = null)
{
    private readonly ConcurrentDictionary<Uri, (DateTimeOffset At, string Body)> _cache = new();
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(10);

    public static TimeSpan CacheLifetime { get; } = TimeSpan.FromMinutes(10);

    protected async Task<ProviderResult<JsonDocument>> GetAsync(Uri uri, CancellationToken ct)
    {
        if (_cache.TryGetValue(uri, out var hit) && DateTimeOffset.Now - hit.At < CacheLifetime)
        {
            return new(JsonDocument.Parse(hit.Body), null);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("PackCheck", "1.0"));
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/X1NPAR1/PackCheck)"));
            using var res = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                return new(null, $"HTTP {(int)res.StatusCode}");
            }

            var body = await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var doc = JsonDocument.Parse(body);
            _cache[uri] = (DateTimeOffset.Now, body);
            return new(doc, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(null, "timeout");
        }
        catch (HttpRequestException ex)
        {
            return new(null, ex.Message);
        }
        catch (JsonException)
        {
            return new(null, "invalid response");
        }
    }
}

public sealed class ModrinthProvider(HttpClient http, TimeSpan? timeout = null) : ProviderBase(http, timeout), IPluginProvider
{
    public string Name => "Modrinth";

    public async Task<ProviderResult<IReadOnlyList<ProjectHit>>> SearchAsync(string query, string? minecraftVersion, CancellationToken ct)
    {
        var facets = "[[\"project_type:plugin\"],[\"categories:paper\"]" + (minecraftVersion is null ? string.Empty : $",[\"versions:{minecraftVersion}\"]") + "]";
        var uri = new Uri($"https://api.modrinth.com/v2/search?limit=20&query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(facets)}");
        var r = await GetAsync(uri, ct).ConfigureAwait(false);
        if (!r.Ok)
        {
            return new(null, r.Error);
        }

        using var doc = r.Value!;
        try
        {
            var hits = doc.RootElement.GetProperty("hits").EnumerateArray().Select(h => new ProjectHit(
                Name,
                h.GetProperty("project_id").GetString()!,
                h.GetProperty("slug").GetString()!,
                h.GetProperty("title").GetString()!,
                h.GetProperty("description").GetString() ?? string.Empty,
                h.GetProperty("downloads").GetInt64(),
                h.TryGetProperty("author", out var a) ? a.GetString() : null)).ToList();
            return new(hits, null);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return new(null, "unexpected response shape");
        }
    }

    public async Task<ProviderResult<ProjectFile>> GetLatestFileAsync(string projectId, string minecraftVersion, CancellationToken ct)
    {
        var uri = new Uri($"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(projectId)}/version?loaders={Uri.EscapeDataString("[\"paper\"]")}&game_versions={Uri.EscapeDataString($"[\"{minecraftVersion}\"]")}");
        var r = await GetAsync(uri, ct).ConfigureAwait(false);
        if (!r.Ok)
        {
            return new(null, r.Error);
        }

        using var doc = r.Value!;
        try
        {
            var version = doc.RootElement.EnumerateArray().FirstOrDefault();
            if (version.ValueKind == JsonValueKind.Undefined)
            {
                return new(null, $"No release for Minecraft {minecraftVersion}.");
            }

            var files = version.GetProperty("files").EnumerateArray().ToList();
            var file = files.FirstOrDefault(f => f.GetProperty("primary").GetBoolean());
            if (file.ValueKind == JsonValueKind.Undefined)
            {
                file = files[0];
            }

            return new(new ProjectFile(
                file.GetProperty("filename").GetString()!,
                new Uri(file.GetProperty("url").GetString()!),
                file.GetProperty("hashes").GetProperty("sha512").GetString()!,
                HashAlgorithmName.SHA512,
                file.GetProperty("size").GetInt64(),
                version.GetProperty("version_number").GetString()!), null);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            return new(null, "unexpected response shape");
        }
    }
}

public sealed class HangarProvider(HttpClient http, TimeSpan? timeout = null) : ProviderBase(http, timeout), IPluginProvider
{
    public string Name => "Hangar";

    public async Task<ProviderResult<IReadOnlyList<ProjectHit>>> SearchAsync(string query, string? minecraftVersion, CancellationToken ct)
    {
        var uri = new Uri($"https://hangar.papermc.io/api/v1/projects?limit=20&platform=PAPER&q={Uri.EscapeDataString(query)}"
            + (minecraftVersion is null ? string.Empty : $"&version={Uri.EscapeDataString(minecraftVersion)}"));
        var r = await GetAsync(uri, ct).ConfigureAwait(false);
        if (!r.Ok)
        {
            return new(null, r.Error);
        }

        using var doc = r.Value!;
        try
        {
            var hits = doc.RootElement.GetProperty("result").EnumerateArray().Select(p =>
            {
                var ns = p.GetProperty("namespace");
                var owner = ns.GetProperty("owner").GetString()!;
                var slug = ns.GetProperty("slug").GetString()!;
                return new ProjectHit(Name, $"{owner}/{slug}", slug, p.GetProperty("name").GetString()!,
                    p.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty,
                    p.GetProperty("stats").GetProperty("downloads").GetInt64(), owner);
            }).ToList();
            return new(hits, null);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return new(null, "unexpected response shape");
        }
    }

    public async Task<ProviderResult<ProjectFile>> GetLatestFileAsync(string projectId, string minecraftVersion, CancellationToken ct)
    {
        var slug = projectId.Contains('/', StringComparison.Ordinal) ? projectId.Split('/')[1] : projectId;
        var uri = new Uri($"https://hangar.papermc.io/api/v1/projects/{Uri.EscapeDataString(slug)}/versions?limit=1&platform=PAPER&platformVersion={Uri.EscapeDataString(minecraftVersion)}");
        var r = await GetAsync(uri, ct).ConfigureAwait(false);
        if (!r.Ok)
        {
            return new(null, r.Error);
        }

        using var doc = r.Value!;
        try
        {
            var v = doc.RootElement.GetProperty("result").EnumerateArray().FirstOrDefault();
            if (v.ValueKind == JsonValueKind.Undefined)
            {
                return new(null, $"No release for Minecraft {minecraftVersion}.");
            }

            var dl = v.GetProperty("downloads").GetProperty("PAPER");
            if (dl.GetProperty("fileInfo").ValueKind == JsonValueKind.Null || dl.GetProperty("downloadUrl").ValueKind == JsonValueKind.Null)
            {
                return new(null, "This release is hosted externally and cannot be verified.");
            }

            var info = dl.GetProperty("fileInfo");
            return new(new ProjectFile(
                info.GetProperty("name").GetString()!,
                new Uri(dl.GetProperty("downloadUrl").GetString()!),
                info.GetProperty("sha256Hash").GetString()!,
                HashAlgorithmName.SHA256,
                info.GetProperty("sizeBytes").GetInt64(),
                v.GetProperty("name").GetString()!), null);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return new(null, "unexpected response shape");
        }
    }
}
