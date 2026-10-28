using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PackCheck.Providers;

public enum CatalogKind
{
    Plugin,
    Mod,
    Modpack,
}

public enum CatalogSort
{
    Downloads,
    Updated,
    Newest,
    Relevance,
}

/// <summary>A project in a browse list, with the versions/loaders it declares so compatibility can be shown.</summary>
public sealed record CatalogItem(
    string Provider,
    string Id,
    string Slug,
    string Title,
    string Description,
    string? Author,
    long Downloads,
    Uri? Icon,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    Uri? Page);

public sealed record CatalogPage(IReadOnlyList<CatalogItem> Items, int Total, string? Error)
{
    public static CatalogPage Failed(string error) => new([], 0, error);
}

public interface ICatalog
{
    string Name { get; }

    bool Supports(CatalogKind kind);

    bool IsAvailable { get; }

    /// <summary>Category keys this source can filter by (empty when it has no category filter).</summary>
    IReadOnlyList<string> Categories(CatalogKind kind);

    Task<CatalogPage> BrowseAsync(CatalogKind kind, string query, CatalogSort sort, int page, int pageSize, CancellationToken ct, string? category = null);

    /// <summary>The project's long description as plain text, or null when it cannot be loaded.</summary>
    Task<string?> DescribeAsync(CatalogItem item, CancellationToken ct);
}

/// <summary>Turns Markdown or HTML descriptions into readable plain text for the detail panel.</summary>
public static partial class PlainText
{
    public static string From(string text)
    {
        var t = BreakTag().Replace(text, "\n");
        t = HtmlTag().Replace(t, string.Empty);
        t = MdImage().Replace(t, string.Empty);
        t = MdLink().Replace(t, "$1");
        t = MdMarks().Replace(t, string.Empty);
        t = System.Net.WebUtility.HtmlDecode(t);
        t = BlankLines().Replace(t, "\n\n");
        return t.Trim();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"<br\s*/?>|</(p|li|h\d|div)>", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex BreakTag();

    [System.Text.RegularExpressions.GeneratedRegex(@"<[^>]+>")]
    private static partial System.Text.RegularExpressions.Regex HtmlTag();

    [System.Text.RegularExpressions.GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial System.Text.RegularExpressions.Regex MdImage();

    [System.Text.RegularExpressions.GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial System.Text.RegularExpressions.Regex MdLink();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?m)^#{1,6}\s*|\*\*|__|`{1,3}|^>\s?")]
    private static partial System.Text.RegularExpressions.Regex MdMarks();

    [System.Text.RegularExpressions.GeneratedRegex(@"\n[ \t]*\n(?:[ \t]*\n)+")]
    private static partial System.Text.RegularExpressions.Regex BlankLines();
}

/// <summary>Loaders that run Bukkit-style plugins.</summary>
public static class PluginLoaders
{
    public static readonly string[] All = ["paper", "spigot", "bukkit", "purpur", "folia"];
}

public sealed class ModrinthCatalog(HttpClient http, TimeSpan? timeout = null) : ProviderBase(http, timeout), ICatalog
{
    public string Name => "Modrinth";

    public bool IsAvailable => true;

    public bool Supports(CatalogKind kind) => true;

    private static readonly string[] ContentCategories =
        ["adventure", "decoration", "economy", "equipment", "food", "game-mechanics", "library", "magic", "management", "minigame", "mobs",
         "optimization", "social", "storage", "technology", "transportation", "utility", "worldgen"];

    private static readonly string[] PackCategories = ["adventure", "challenging", "combat", "kitchen-sink", "lightweight", "magic", "multiplayer", "optimization", "quests", "technology"];

    public IReadOnlyList<string> Categories(CatalogKind kind) => kind == CatalogKind.Modpack ? PackCategories : ContentCategories;

    public async Task<CatalogPage> BrowseAsync(CatalogKind kind, string query, CatalogSort sort, int page, int pageSize, CancellationToken ct, string? category = null)
    {
        var type = kind switch { CatalogKind.Plugin => "plugin", CatalogKind.Mod => "mod", _ => "modpack" };
        var facets = kind == CatalogKind.Plugin
            ? $"[[\"project_type:{type}\"],[{string.Join(',', PluginLoaders.All.Select(l => $"\"categories:{l}\""))}]"
            : $"[[\"project_type:{type}\"],[\"server_side:required\",\"server_side:optional\"]";
        facets += category is null ? "]" : $",[\"categories:{category}\"]]";
        var index = sort switch
        {
            CatalogSort.Updated => "updated",
            CatalogSort.Newest => "newest",
            CatalogSort.Relevance when query.Length > 0 => "relevance",
            _ => "downloads",
        };
        var uri = new Uri($"https://api.modrinth.com/v2/search?limit={pageSize}&offset={page * pageSize}&index={index}&query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(facets)}");
        var r = await GetAsync(uri, ct).ConfigureAwait(false);
        if (!r.Ok)
        {
            return CatalogPage.Failed(r.Error!);
        }

        using var doc = r.Value!;
        try
        {
            var root = doc.RootElement;
            var items = root.GetProperty("hits").EnumerateArray().Select(h => new CatalogItem(
                Name,
                h.GetProperty("project_id").GetString()!,
                h.GetProperty("slug").GetString()!,
                h.GetProperty("title").GetString()!,
                h.GetProperty("description").GetString() ?? string.Empty,
                h.TryGetProperty("author", out var a) ? a.GetString() : null,
                h.GetProperty("downloads").GetInt64(),
                h.TryGetProperty("icon_url", out var i) && i.ValueKind == JsonValueKind.String && Uri.TryCreate(i.GetString(), UriKind.Absolute, out var icon) ? icon : null,
                h.GetProperty("versions").EnumerateArray().Select(v => v.GetString()!).ToList(),
                h.GetProperty("categories").EnumerateArray().Select(v => v.GetString()!).ToList(),
                new Uri($"https://modrinth.com/{type}/{h.GetProperty("slug").GetString()}"))).ToList();
            return new CatalogPage(items, root.GetProperty("total_hits").GetInt32(), null);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return CatalogPage.Failed("unexpected response shape");
        }
    }

    public async Task<string?> DescribeAsync(CatalogItem item, CancellationToken ct)
    {
        var r = await GetAsync(new Uri($"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(item.Id)}"), ct).ConfigureAwait(false);
        using var doc = r.Value;
        return doc is not null && doc.RootElement.TryGetProperty("body", out var body) && body.GetString() is { } text ? PlainText.From(text) : null;
    }
}

public sealed class HangarCatalog(HttpClient http, TimeSpan? timeout = null) : ProviderBase(http, timeout), ICatalog
{
    public string Name => "Hangar";

    public bool IsAvailable => true;

    public bool Supports(CatalogKind kind) => kind == CatalogKind.Plugin;

    private static readonly string[] HangarCategories = ["admin_tools", "chat", "dev_tools", "economy", "gameplay", "games", "protection", "role_playing", "world_management", "misc"];

    public IReadOnlyList<string> Categories(CatalogKind kind) => HangarCategories;

    public async Task<CatalogPage> BrowseAsync(CatalogKind kind, string query, CatalogSort sort, int page, int pageSize, CancellationToken ct, string? category = null)
    {
        var order = sort switch { CatalogSort.Updated => "-updated", CatalogSort.Newest => "-newest", _ => "-downloads" };
        var uri = new Uri($"https://hangar.papermc.io/api/v1/projects?limit={pageSize}&offset={page * pageSize}&sort={order}&platform=PAPER"
            + (query.Length > 0 ? $"&q={Uri.EscapeDataString(query)}" : string.Empty)
            + (category is null ? string.Empty : $"&category={category}"));
        var r = await GetAsync(uri, ct).ConfigureAwait(false);
        if (!r.Ok)
        {
            return CatalogPage.Failed(r.Error!);
        }

        using var doc = r.Value!;
        try
        {
            var root = doc.RootElement;
            var items = root.GetProperty("result").EnumerateArray().Select(p =>
            {
                var ns = p.GetProperty("namespace");
                var owner = ns.GetProperty("owner").GetString()!;
                var slug = ns.GetProperty("slug").GetString()!;
                var versions = p.TryGetProperty("supportedPlatforms", out var sp) && sp.TryGetProperty("PAPER", out var paper)
                    ? paper.EnumerateArray().Select(v => v.GetString()!).ToList()
                    : [];
                return new CatalogItem(
                    Name, $"{owner}/{slug}", slug, p.GetProperty("name").GetString()!,
                    p.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty,
                    owner, p.GetProperty("stats").GetProperty("downloads").GetInt64(),
                    p.TryGetProperty("avatarUrl", out var av) && Uri.TryCreate(av.GetString(), UriKind.Absolute, out var icon) ? icon : null,
                    versions, ["paper"], new Uri($"https://hangar.papermc.io/{owner}/{slug}"));
            }).ToList();
            return new CatalogPage(items, root.GetProperty("pagination").GetProperty("count").GetInt32(), null);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return CatalogPage.Failed("unexpected response shape");
        }
    }

    public async Task<string?> DescribeAsync(CatalogItem item, CancellationToken ct)
    {
        // The main page is served as raw Markdown, not JSON.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var text = await Http.GetStringAsync(new Uri($"https://hangar.papermc.io/api/v1/pages/main/{Uri.EscapeDataString(item.Slug)}"), cts.Token).ConfigureAwait(false);
            return PlainText.From(text);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }
}

/// <summary>CurseForge browse. Needs the user's API key.</summary>
public sealed class CurseForgeCatalog(HttpClient http, Func<string?> apiKey, TimeSpan? timeout = null) : ICatalog
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(15);

    public string Name => "CurseForge";

    public bool IsAvailable => !string.IsNullOrWhiteSpace(apiKey());

    public bool Supports(CatalogKind kind) => true;

    public IReadOnlyList<string> Categories(CatalogKind kind) => [];

    public async Task<string?> DescribeAsync(CatalogItem item, CancellationToken ct)
    {
        if (!IsAvailable)
        {
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://api.curseforge.com/v1/mods/{Uri.EscapeDataString(item.Id)}/description"));
            req.Headers.Add("x-api-key", apiKey());
            using var res = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("data", out var data) && data.GetString() is { } html ? PlainText.From(html) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    public async Task<CatalogPage> BrowseAsync(CatalogKind kind, string query, CatalogSort sort, int page, int pageSize, CancellationToken ct, string? category = null)
    {
        if (!IsAvailable)
        {
            return CatalogPage.Failed("API key missing (Settings)");
        }

        // classId: 5 = Bukkit plugins, 6 = mods, 4471 = modpacks. sortField: 2 popularity, 3 last updated, 11 release date.
        var cls = kind switch { CatalogKind.Plugin => 5, CatalogKind.Mod => 6, _ => 4471 };
        var field = sort switch { CatalogSort.Updated => 3, CatalogSort.Newest => 11, _ => 2 };
        var path = $"/v1/mods/search?gameId=432&classId={cls}&sortField={field}&sortOrder=desc&pageSize={pageSize}&index={page * pageSize}&searchFilter={Uri.EscapeDataString(query)}";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.curseforge.com" + path));
            req.Headers.Add("x-api-key", apiKey());
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var res = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                return CatalogPage.Failed($"HTTP {(int)res.StatusCode}");
            }

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
            return Parse(doc.RootElement, Name);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return CatalogPage.Failed("timeout");
        }
        catch (HttpRequestException ex)
        {
            return CatalogPage.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return CatalogPage.Failed("unexpected response shape");
        }
    }

    public static CatalogPage Parse(JsonElement root, string provider)
    {
        static string Loader(int t) => t switch { 1 => "forge", 4 => "fabric", 5 => "quilt", 6 => "neoforge", _ => string.Empty };
        var items = root.GetProperty("data").EnumerateArray().Select(m =>
        {
            var indexes = m.TryGetProperty("latestFilesIndexes", out var idx) ? idx.EnumerateArray().ToList() : [];
            var versions = indexes.Select(f => f.GetProperty("gameVersion").GetString()!).Distinct().ToList();
            var loaders = indexes.Where(f => f.TryGetProperty("modLoader", out _)).Select(f => Loader(f.GetProperty("modLoader").GetInt32())).Where(l => l.Length > 0).Distinct().ToList();
            return new CatalogItem(
                provider, m.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture), m.GetProperty("slug").GetString()!,
                m.GetProperty("name").GetString()!, m.GetProperty("summary").GetString() ?? string.Empty,
                m.GetProperty("authors").EnumerateArray().Select(a => a.GetProperty("name").GetString()).FirstOrDefault(),
                (long)m.GetProperty("downloadCount").GetDouble(),
                m.TryGetProperty("logo", out var logo) && logo.ValueKind == JsonValueKind.Object && Uri.TryCreate(logo.GetProperty("thumbnailUrl").GetString(), UriKind.Absolute, out var icon) ? icon : null,
                versions, loaders,
                m.TryGetProperty("links", out var links) && Uri.TryCreate(links.GetProperty("websiteUrl").GetString(), UriKind.Absolute, out var page) ? page : null);
        }).ToList();
        return new CatalogPage(items, Math.Min(10_000, root.GetProperty("pagination").GetProperty("totalCount").GetInt32()), null);
    }
}

/// <summary>Decides whether a catalog item can be installed on a server, and why not.</summary>
public static class Compatibility
{
    public static (bool Ok, string? Reason) Check(CatalogItem item, CatalogKind kind, string minecraftVersion, string loader)
    {
        var loaderOk = kind == CatalogKind.Plugin
            ? item.Loaders.Count == 0 || item.Loaders.Any(l => PluginLoaders.All.Contains(l, StringComparer.OrdinalIgnoreCase))
            : item.Loaders.Count == 0 || item.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase);
        if (!loaderOk)
        {
            return (false, $"loader:{string.Join(", ", item.Loaders)}");
        }

        if (item.GameVersions.Count > 0 && !item.GameVersions.Contains(minecraftVersion, StringComparer.OrdinalIgnoreCase))
        {
            return (false, $"version:{Range(item.GameVersions)}");
        }

        return (true, null);
    }

    /// <summary>"1.20 – 1.21.4" from a list of release versions (snapshots ignored).</summary>
    public static string Range(IReadOnlyList<string> versions)
    {
        var releases = versions.Where(v => Core.MinecraftVersion.TryParse(v, out _))
            .Select(v => (Text: v, V: Core.MinecraftVersion.TryParse(v, out var mv) ? mv : default)).OrderBy(x => x.V).ToList();
        return releases.Count switch
        {
            0 => "?",
            1 => releases[0].Text,
            _ => $"{releases[0].Text} – {releases[^1].Text}",
        };
    }
}
