using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using PackCheck.Core;

namespace PackCheck.Servers;

public enum ServerPlatform
{
    Paper,
    Vanilla,
}

public sealed record ServerDownload(string FileName, Uri Url, string Hash, HashAlgorithmName Algorithm, long Size);

/// <summary>Resolves server JARs from official sources only (PaperMC Fill API, Mojang piston-meta).</summary>
public sealed class ServerSources(HttpClient http)
{
    private static readonly Uri PaperVersions = new("https://fill.papermc.io/v3/projects/paper");
    private static readonly Uri MojangManifest = new("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");

    public async Task<IReadOnlyList<string>> GetVersionsAsync(ServerPlatform platform, CancellationToken ct)
    {
        if (platform == ServerPlatform.Paper)
        {
            using var doc = await GetJsonAsync(PaperVersions, ct).ConfigureAwait(false);
            var list = new List<string>();
            foreach (var group in doc.RootElement.GetProperty("versions").EnumerateObject())
            {
                list.AddRange(group.Value.EnumerateArray().Select(v => v.GetString()!).Where(v => MinecraftVersion.TryParse(v, out _)));
            }

            return list.OrderByDescending(v => MinecraftVersion.TryParse(v, out var mv) ? mv : default).ToList();
        }

        using var manifest = await GetJsonAsync(MojangManifest, ct).ConfigureAwait(false);
        return manifest.RootElement.GetProperty("versions").EnumerateArray()
            .Where(v => v.GetProperty("type").GetString() == "release")
            .Select(v => v.GetProperty("id").GetString()!)
            .ToList();
    }

    public async Task<ServerDownload> ResolveAsync(ServerPlatform platform, string version, CancellationToken ct)
    {
        if (platform == ServerPlatform.Paper)
        {
            using var doc = await GetJsonAsync(new Uri($"https://fill.papermc.io/v3/projects/paper/versions/{Uri.EscapeDataString(version)}/builds/latest"), ct).ConfigureAwait(false);
            var d = doc.RootElement.GetProperty("downloads").GetProperty("server:default");
            return new ServerDownload(
                d.GetProperty("name").GetString()!,
                new Uri(d.GetProperty("url").GetString()!),
                d.GetProperty("checksums").GetProperty("sha256").GetString()!,
                HashAlgorithmName.SHA256,
                d.GetProperty("size").GetInt64());
        }

        using var manifest = await GetJsonAsync(MojangManifest, ct).ConfigureAwait(false);
        var entry = manifest.RootElement.GetProperty("versions").EnumerateArray()
            .FirstOrDefault(v => v.GetProperty("id").GetString() == version);
        if (entry.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException($"Unknown Minecraft version {version}.");
        }

        using var meta = await GetJsonAsync(new Uri(entry.GetProperty("url").GetString()!), ct).ConfigureAwait(false);
        var server = meta.RootElement.GetProperty("downloads").GetProperty("server");
        return new ServerDownload("server.jar", new Uri(server.GetProperty("url").GetString()!), server.GetProperty("sha1").GetString()!, HashAlgorithmName.SHA1, server.GetProperty("size").GetInt64());
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("PackCheck", "1.0"));
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        await using var s = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
    }
}

/// <summary>HTTPS-only download with size limit and hash verification. The file only appears once verified.</summary>
public static class VerifiedDownloader
{
    public static async Task DownloadAsync(HttpClient http, Uri url, string destination, string expectedHash, HashAlgorithmName algorithm, long maxBytes, IProgress<double>? progress, CancellationToken ct)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Only HTTPS downloads are allowed.");
        }

        var temp = destination + ".part";
        using (var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? 0;
            if (total > maxBytes)
            {
                throw new InvalidOperationException("Download exceeds the size limit.");
            }

            using var hasher = IncrementalHash.CreateHash(algorithm);
            await using var input = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (var output = File.Create(temp))
            {
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    read += n;
                    if (read > maxBytes)
                    {
                        throw new InvalidOperationException("Download exceeds the size limit.");
                    }

                    hasher.AppendData(buffer, 0, n);
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    if (total > 0)
                    {
                        progress?.Report((double)read / total);
                    }
                }
            }

            var actual = Convert.ToHexStringLower(hasher.GetHashAndReset());
            if (!string.Equals(actual, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temp);
                throw new InvalidOperationException($"Checksum mismatch for {Path.GetFileName(destination)}.");
            }
        }

        File.Move(temp, destination, overwrite: true);
    }
}
