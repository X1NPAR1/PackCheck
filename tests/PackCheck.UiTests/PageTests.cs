using System.IO.Compression;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;

namespace PackCheck.UiTests;

[Collection("app")]
[Trait("Category", "UI")]
public sealed class PageTests(AppFixture app)
{
    [Theory]
    [InlineData("NavServers", "ServersPage")]
    [InlineData("NavValidate", "ValidatePage")]
    [InlineData("NavPlugins", "BrowsePage")]
    [InlineData("NavMods", "BrowsePage")]
    [InlineData("NavProxy", "ProxyPage")]
    [InlineData("NavOnline", "OnlinePage")]
    [InlineData("NavAssistant", "AssistantPage")]
    [InlineData("NavSettings", "SettingsPage")]
    [InlineData("NavAbout", "AboutPage")]
    public void Navigation_ShowsThePage(string nav, string page)
    {
        // Plugins and Mods are greyed out (with a reason) when no server of a fitting platform exists.
        var item = app.Find(nav);
        if (!item.IsEnabled)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.HelpText), $"{nav} is disabled without telling why");
            return;
        }

        app.Navigate(nav);
        Assert.True(app.Find(page).IsOffscreen == false, $"{page} is not visible after selecting {nav}");
    }

    [Fact]
    public void Validation_ReportsFindingsGroupedWithInventory()
    {
        var pack = Path.Combine(Path.GetTempPath(), "packcheck-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(pack, "plugins"));
        try
        {
            File.WriteAllText(Path.Combine(pack, "server.properties"), "server-port=25565\n");
            Jar(Path.Combine(pack, "plugins", "Shop.jar"), "name: Shop\nversion: 1.0\nmain: a.B\napi-version: '1.21'\ndepend: [Vault]\n");
            File.WriteAllBytes(Path.Combine(pack, "plugins", "Broken.jar"), "not a zip"u8.ToArray());

            app.Navigate("NavValidate");
            app.Find("ValidateTarget").AsTextBox().Text = pack;
            app.Find("RunValidation").AsButton().Invoke();

            var overall = Retry.WhileNull(() =>
            {
                var t = app.Main.FindFirstDescendant(cf => cf.ByAutomationId("OverallResult"));
                return t is not null && t.Name.Length > 0 ? t : null;
            }, TimeSpan.FromSeconds(30)).Result;
            Assert.NotNull(overall);

            var list = app.Find("CheckList");
            Assert.True(Retry.WhileFalse(() => list.FindAllChildren().Length > 0, TimeSpan.FromSeconds(10)).Result, "no finding groups");
            Assert.Contains("PC020", AllText(list), StringComparison.Ordinal); // corrupt jar
            Assert.Contains("PC040", AllText(list), StringComparison.Ordinal); // missing Vault

            app.Find("ValidateInventoryTab").AsRadioButton().IsChecked = true;
            Assert.Contains("Shop.jar", AllText(app.Find("ValidateInventory")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(pack, true);
        }
    }

    [Fact]
    public void SettingsSearch_HidesSectionsThatDoNotMatch()
    {
        app.Navigate("NavSettings");
        var sections = app.Find("SettingsSections");
        var before = Visible(sections);

        var search = app.Find("SettingsSearch").AsTextBox();
        search.Focus();
        search.Text = "Java";
        Wait.UntilInputIsProcessed();
        Assert.True(Retry.WhileFalse(() => Visible(sections) < before, TimeSpan.FromSeconds(5)).Result, "search did not filter the sections");

        search.Text = string.Empty;
        Assert.True(Retry.WhileFalse(() => Visible(sections) == before, TimeSpan.FromSeconds(5)).Result, "clearing the search did not bring the sections back");
    }

    [Fact]
    public void SettingsAssistant_OffersProvidersAndModelsToPick()
    {
        app.Navigate("NavSettings");
        var providers = app.Find("AiProviderBox").AsComboBox();
        providers.Expand();
        var names = providers.Items.Select(i => i.Name).ToList();
        providers.Collapse();
        Assert.Contains(names, n => n.Contains("Grok", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("Gemini", StringComparison.Ordinal));

        var models = app.Find("AiModelBox").AsComboBox();
        models.Expand();
        Assert.NotEmpty(models.Items);
        models.Collapse();
    }

    [Fact]
    public void About_ShowsSystemInformation()
    {
        app.Navigate("NavAbout");
        Assert.Contains(".NET", AllText(app.Find("AboutSystemInfo")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TabBackups", "CreateBackup")]
    [InlineData("TabPerformance", "PerformancePage")]
    [InlineData("TabPlayers", "PlayerSearch")]
    [InlineData("TabIdentity", "IdentityPage")]
    [InlineData("TabAutomation", "DiscordUrl")]
    [InlineData("OpenUpgrade", "UpgradePage")]
    public void ServerDetail_ShowsTheTab(string tab, string page)
    {
        app.Navigate("NavServers");
        var first = app.Find("ServerList").FindFirstChild();
        if (first is null)
        {
            // No server on this machine; the tabs only exist for a selected server.
            return;
        }

        first.AsListBoxItem().Select();
        var control = app.Find(tab);
        if (control.ControlType == ControlType.Button)
        {
            control.AsButton().Invoke();
        }
        else
        {
            control.AsRadioButton().IsChecked = true;
        }

        Assert.True(Retry.WhileFalse(() => !app.Find(page).IsOffscreen, TimeSpan.FromSeconds(5)).Result, $"{page} is not visible after {tab}");
    }

    private static int Visible(AutomationElement list) => list.FindAllChildren().Count(c => !c.IsOffscreen);

    private static string AllText(AutomationElement root)
    {
        var sb = new StringBuilder(root.Name);
        foreach (var e in root.FindAllDescendants())
        {
            sb.Append(' ').Append(e.Name);
        }

        return sb.ToString();
    }

    private static void Jar(string path, string pluginYml)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("plugin.yml").Open());
        w.Write(pluginYml);
    }
}
