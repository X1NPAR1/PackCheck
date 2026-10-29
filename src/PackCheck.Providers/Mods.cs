using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace PackCheck.Providers;

public enum ModKind
{
    Mod,
    Modpack,
}

/// <summary>A file of a mod or modpack, already filtered for the server's Minecraft version and loader.</summary>
public sealed record ModFile(string FileName, Uri Url, string? Hash, HashAlgorithmName Algorithm, long Size, string Version, bool IsServerPack = false);

public interface IModProvider
{
    string Name { get; }

    bool IsAvailable { get; }

    Task<ProviderResult<IReadOnlyList<ProjectHit>>> SearchAsync(string query, ModKind kind, string minecraftVersion, string loader, CancellationToken ct);

    Task<ProviderResult<ModFile>> GetFileAsync(string projectId, ModKind kind, string minecraftVersion, string loader, CancellationToken ct);
}

public sealed class ModrinthMods(HttpClient http, TimeSpan? timeout = null) : ProviderBase(http, timeout), IModProvider
{
    public string Name => "Modrinth";

    public bool IsAvailable => true;

    public async Task<ProviderResult<IReadOnlyList<ProjectHit>>> SearchAsync(string query, ModKind kind, string minecraftVersion, string loader, CancellationToken ct)
    {
        var type = kind == ModKind.Mod ? "mod" : "modpack";
        var facets = $"[[\"project_type:{type}\"],[\"categories:{loader}\"],[\"versions:{minecraftVersion}\"],[\"server_side:required\",\"server_side:optional\"]]";
        var r = await GetAsync(new Uri($"https://api.modrinth.com/v2/search?limit=30&index=relevance&query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(facets)}"), ct).ConfigureAwait(false);
        if (!r.Ok)
        {
            return new(null, r.Error);
        }

        using var doc = r.Value!;
        try
        {
            return new(doc.RootElement.GetProperty("hits").EnumerateArray().Select(h => new ProjectHit(
                Name, h.GetProperty("project_id").GetString()!, h.GetProperty("slug").GetString()!, h.GetProperty("title").GetString()!,
                h.GetProperty("description").GetString() ?? string.Empty, h.GetProperty("downloads").GetInt64(),
                h.TryGetProperty("author", out var a) ? a.GetString() : null)).ToList(), null);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return new(null, "unexpected response shape");
        }
    }

    public async Task<ProviderResult<ModFile>> GetFileAsync(string projectId, ModKind kind, string minecraftVersion, string loader, CancellationToken ct)
    {
        var uri = new Uri($"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(projectId)}/version?loaders={Uri.EscapeDataString($"[\"{loader}\"]")}&game_versions={Uri.EscapeDataString($"[\"{minecraftVersion}\"]")}");
        var r = await GetAsync(uri, ct).ConfigureAwait(false);
        if (!r.Ok)
        {
            return new(null, r.Error);
        }

        using var doc = r.Value!;
        var version = doc.RootElement.EnumerateArray().FirstOrDefault();
        if (version.ValueKind == JsonValueKind.Undefined)
        {
            return new(null, $"No release for Minecraft {minecraftVersion} / {loader}.");
        }

        var files = version.GetProperty("files").EnumerateArray().ToList();
        var file = files.FirstOrDefault(f => f.GetProperty("primary").GetBoolean());
        if (file.ValueKind == JsonValueKind.Undefined)
        {
            file = files[0];
        }

        return new(new ModFile(file.GetProperty("filename").GetString()!, new Uri(file.GetProperty("url").GetString()!),
            file.GetProperty("hashes").GetProperty("sha512").GetString(), HashAlgorithmName.SHA512, file.GetProperty("size").GetInt64(),
            version.GetProperty("version_number").GetString()!), null);
    }
}

/// <summary>CurseForge needs a personal API key (console.curseforge.com). Without one the provider is unavailable.</summary>
public sealed class CurseForgeMods(HttpClient http, Func<string?> apiKey, TimeSpan? timeout = null) : IModProvider
{
    private const int MinecraftGameId = 432;
    private const int ModsClass = 6;
    private const int ModpacksClass = 4471;
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(15);

    public string Name => "CurseForge";

    public bool IsAvailable => !string.IsNullOrWhiteSpace(apiKey());

    private static int LoaderType(string loader) => loader switch
    {
        "forge" => 1,
        "fabric" => 4,
        "quilt" => 5,
        "neoforge" => 6,
        _ => 0,
    };

    private async Task<ProviderResult<JsonDocument>> GetAsync(string path, CancellationToken ct)
    {
        if (!IsAvailable)
        {
            return new(null, "API key missing (Settings)");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.curseforge.com" + path));
            CurseForgeKey.Apply(req, apiKey());
            using var res = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                return new(null, CurseForgeKey.Failure(res.StatusCode));
            }

            return new(JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false)), null);
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

    public async Task<ProviderResult<IReadOnlyList<ProjectHit>>> SearchAsync(string query, ModKind kind, string minecraftVersion, string loader, CancellationToken ct)
    {
        var cls = kind == ModKind.Mod ? ModsClass : ModpacksClass;
        var r = await GetAsync($"/v1/mods/search?gameId={MinecraftGameId}&classId={cls}&gameVersion={Uri.EscapeDataString(minecraftVersion)}&modLoaderType={LoaderType(loader)}&searchFilter={Uri.EscapeDataString(query)}&sortField=2&sortOrder=desc&pageSize=30", ct).ConfigureAwait(false);
        if (!r.Ok)
        {
            return new(null, r.Error);
        }

        using var doc = r.Value!;
        return new(doc.RootElement.GetProperty("data").EnumerateArray().Select(m => new ProjectHit(
            Name, m.GetProperty("id").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture), m.GetProperty("slug").GetString()!,
            m.GetProperty("name").GetString()!, m.GetProperty("summary").GetString() ?? string.Empty, (long)m.GetProperty("downloadCount").GetDouble(),
            m.GetProperty("authors").EnumerateArray().Select(a => a.GetProperty("name").GetString()).FirstOrDefault())).ToList(), null);
    }

    public async Task<ProviderResult<ModFile>> GetFileAsync(string projectId, ModKind kind, string minecraftVersion, string loader, CancellationToken ct)
    {
        var r = await GetAsync($"/v1/mods/{Uri.EscapeDataString(projectId)}/files?gameVersion={Uri.EscapeDataString(minecraftVersion)}&modLoaderType={LoaderType(loader)}&pageSize=20", ct).ConfigureAwait(false);
        if (!r.Ok)
        {
            return new(null, r.Error);
        }

        using var doc = r.Value!;
        foreach (var f in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            // Modpacks: use the author's server pack when one exists.
            if (kind == ModKind.Modpack && f.TryGetProperty("serverPackFileId", out var sp) && sp.ValueKind == JsonValueKind.Number)
            {
                var spr = await GetAsync($"/v1/mods/{Uri.EscapeDataString(projectId)}/files/{sp.GetInt32()}", ct).ConfigureAwait(false);
                if (spr.Ok)
                {
                    using var spd = spr.Value!;
                    return Map(spd.RootElement.GetProperty("data"), isServerPack: true);
                }
            }

            if (kind == ModKind.Mod)
            {
                return Map(f, false);
            }
        }

        return new(null, kind == ModKind.Modpack ? "This modpack has no server pack for this version." : $"No file for Minecraft {minecraftVersion} / {loader}.");
    }

    private static ProviderResult<ModFile> Map(JsonElement f, bool isServerPack)
    {
        if (f.GetProperty("downloadUrl").ValueKind != JsonValueKind.String)
        {
            return new(null, "The author disabled third-party downloads for this file.");
        }

        var sha1 = f.GetProperty("hashes").EnumerateArray().Where(h => h.GetProperty("algo").GetInt32() == 1).Select(h => h.GetProperty("value").GetString()).FirstOrDefault();
        return new(new ModFile(f.GetProperty("fileName").GetString()!, new Uri(f.GetProperty("downloadUrl").GetString()!), sha1, HashAlgorithmName.SHA1,
            f.GetProperty("fileLength").GetInt64(), f.GetProperty("displayName").GetString()!, isServerPack), null);
    }
}

/// <summary>Installs a Modrinth .mrpack into a server folder: server-side files (SHA-512 verified) plus overrides.</summary>
public static class MrpackInstaller
{
    public static async Task<int> InstallAsync(HttpClient http, string mrpack, string serverDir, Func<HttpClient, Uri, string, string?, HashAlgorithmName, Task> download, IProgress<double>? progress, CancellationToken ct)
    {
        var root = Path.GetFullPath(serverDir) + Path.DirectorySeparatorChar;
        string Safe(string rel)
        {
            var full = Path.GetFullPath(Path.Combine(root, rel));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : throw new InvalidDataException($"Unsafe path in modpack: {rel}");
        }

        using var zip = ZipFile.OpenRead(mrpack);
        var index = zip.GetEntry("modrinth.index.json") ?? throw new InvalidDataException("Not a Modrinth modpack (modrinth.index.json missing).");
        using var doc = await JsonDocument.ParseAsync(index.Open(), cancellationToken: ct).ConfigureAwait(false);
        var files = doc.RootElement.GetProperty("files").EnumerateArray()
            .Where(f => !f.TryGetProperty("env", out var env) || env.GetProperty("server").GetString() != "unsupported").ToList();

        var done = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var target = Safe(f.GetProperty("path").GetString()!);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var url = f.GetProperty("downloads").EnumerateArray().Select(d => new Uri(d.GetString()!)).First(u => u.Scheme == Uri.UriSchemeHttps);
            await download(http, url, target, f.GetProperty("hashes").GetProperty("sha512").GetString(), HashAlgorithmName.SHA512).ConfigureAwait(false);
            progress?.Report(++done / (double)Math.Max(1, files.Count));
        }

        foreach (var prefix in new[] { "overrides/", "server-overrides/" })
        {
            foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.Name.Length > 0))
            {
                var target = Safe(e.FullName[prefix.Length..]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                e.ExtractToFile(target, overwrite: true);
            }
        }

        return files.Count;
    }
}
