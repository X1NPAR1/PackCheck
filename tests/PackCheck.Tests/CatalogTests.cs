using PackCheck.Providers;

namespace PackCheck.Tests;

public class CatalogTests
{
    private static CatalogItem Item(string[] versions, string[] loaders) =>
        new("Modrinth", "id", "slug", "Title", "d", "a", 1, null, versions, loaders, null);

    [Fact]
    public void CompatibleMod()
    {
        var (ok, reason) = Compatibility.Check(Item(["1.21.1", "1.21.4"], ["fabric"]), CatalogKind.Mod, "1.21.4", "fabric");
        Assert.True(ok);
        Assert.Null(reason);
    }

    [Fact]
    public void WrongVersion_ExplainsSupportedRange()
    {
        var (ok, reason) = Compatibility.Check(Item(["1.20", "1.20.6", "1.20.1", "24w10a"], ["fabric"]), CatalogKind.Mod, "1.21.4", "fabric");
        Assert.False(ok);
        Assert.Equal("version:1.20 – 1.20.6", reason);
    }

    [Fact]
    public void WrongLoader_IsRejectedBeforeVersion()
    {
        var (ok, reason) = Compatibility.Check(Item(["1.21.4"], ["forge", "neoforge"]), CatalogKind.Mod, "1.21.4", "fabric");
        Assert.False(ok);
        Assert.StartsWith("loader:", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Plugins_AcceptAnyBukkitFamilyLoader()
    {
        Assert.True(Compatibility.Check(Item(["1.21.4"], ["spigot", "bukkit"]), CatalogKind.Plugin, "1.21.4", "paper").Ok);
        Assert.False(Compatibility.Check(Item(["1.21.4"], ["fabric"]), CatalogKind.Plugin, "1.21.4", "paper").Ok);
    }

    [Fact]
    public void UnknownMetadata_IsNotBlocked() =>
        Assert.True(Compatibility.Check(Item([], []), CatalogKind.Plugin, "1.21.4", "paper").Ok);

    [Theory]
    [Trait("Category", "Network")]
    [InlineData(CatalogKind.Plugin)]
    [InlineData(CatalogKind.Mod)]
    [InlineData(CatalogKind.Modpack)]
    public async Task Modrinth_BrowsesWithoutQuery_AndPages(CatalogKind kind)
    {
        using var http = new HttpClient();
        var c = new ModrinthCatalog(http);
        var p1 = await c.BrowseAsync(kind, string.Empty, CatalogSort.Downloads, 0, 20, CancellationToken.None);
        var p2 = await c.BrowseAsync(kind, string.Empty, CatalogSort.Downloads, 1, 20, CancellationToken.None);
        Assert.Null(p1.Error);
        Assert.Equal(20, p1.Items.Count);
        Assert.True(p1.Total > 40);
        Assert.Empty(p1.Items.Select(i => i.Id).Intersect(p2.Items.Select(i => i.Id)));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Hangar_BrowsesWithVersions()
    {
        using var http = new HttpClient();
        var p = await new HangarCatalog(http).BrowseAsync(CatalogKind.Plugin, string.Empty, CatalogSort.Downloads, 0, 10, CancellationToken.None);
        Assert.Null(p.Error);
        Assert.NotEmpty(p.Items);
        Assert.Contains(p.Items, i => i.GameVersions.Count > 0);
    }

    [Fact]
    public async Task CurseForge_WithoutKey_FailsAsValue()
    {
        using var http = new HttpClient();
        var p = await new CurseForgeCatalog(http, () => null).BrowseAsync(CatalogKind.Mod, string.Empty, CatalogSort.Downloads, 0, 10, CancellationToken.None);
        Assert.NotNull(p.Error);
        Assert.Empty(p.Items);
    }
}
