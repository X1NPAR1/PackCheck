using System.IO.Compression;
using System.Text.Json.Nodes;
using PackCheck.App.Infrastructure;
using PackCheck.Servers;

namespace PackCheck.Tests;

public sealed class SettingsTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pc-transfer-" + Guid.NewGuid().ToString("N"));

    public SettingsTransferTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // Stand-ins for DPAPI so the tests do not depend on the Windows user.
    private static string? FakeProtect(string? s) => s is null ? null : "enc:" + s;

    private static string? FakeUnprotect(string? s) => s is not null && s.StartsWith("enc:", StringComparison.Ordinal) ? s[4..] : null;

    private string Data(string name)
    {
        var d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    private ServerDefinition Server(string id, string name, string dir) => new()
    {
        Id = id,
        Name = name,
        Directory = dir,
        Platform = ServerPlatform.Paper,
        MinecraftVersion = "1.21.1",
        JarFile = "server.jar",
        JavaPath = "java",
    };

    /// <summary>A source data root with secrets, one server with automation and Discord files, and one chat.</summary>
    private (string Root, ServerDefinition Server) Source()
    {
        var root = Data("a");
        File.WriteAllText(Path.Combine(root, "settings.json"), """
            { "Language": "tr", "Theme": 1, "BackupsToKeep": 9, "FirstRunDone": true,
              "AiKey": "enc:sk-test", "DuckDnsToken": "enc:duck", "CurseForgeApiKey": "cf-key" }
            """);
        var dir = Data("srv-a");
        var s = Server("abc", "Survival", dir);
        new ServerRegistry(Path.Combine(root, "servers.json")).Save([s]);
        AutomationStore.Save(s, new([], []));
        new DiscordSettings { WebhookUrl = "https://discord.com/api/webhooks/1/x", Mention = "@here" }.Save(dir);
        Directory.CreateDirectory(Path.Combine(root, "assistant", "chats"));
        File.WriteAllText(Path.Combine(root, "assistant", "chats", "c1.json"), "{}");
        return (root, s);
    }

    private string Export(string root, ServerDefinition s, SettingsExportOptions o)
    {
        var zip = Path.Combine(_root, Guid.NewGuid().ToString("N") + SettingsTransfer.Extension);
        SettingsTransfer.Export(zip, root, [s], o, FakeUnprotect, "3.4.0");
        return zip;
    }

    [Fact]
    public void Secrets_are_stripped_by_default()
    {
        var (root, s) = Source();
        var zip = Export(root, s, new());
        using var z = ZipFile.OpenRead(zip);
        var all = string.Join('\n', z.Entries.Select(e => new StreamReader(e.Open()).ReadToEnd()));
        Assert.DoesNotContain("sk-test", all, StringComparison.Ordinal);
        Assert.DoesNotContain("duck", all, StringComparison.Ordinal);
        Assert.DoesNotContain("cf-key", all, StringComparison.Ordinal);
        Assert.DoesNotContain("webhooks", all, StringComparison.Ordinal);
        Assert.DoesNotContain("FirstRunDone", all, StringComparison.Ordinal);
        Assert.Null(z.GetEntry("chats/c1.json"));
        Assert.False(SettingsTransfer.Read(zip).IncludesSecrets);
    }

    [Fact]
    public void Included_secrets_are_decrypted_for_export_and_protected_again_on_import()
    {
        var (root, s) = Source();
        var zip = Export(root, s, new(IncludeSecrets: true));
        var p = SettingsTransfer.Read(zip);
        Assert.True(p.IncludesSecrets);
        Assert.Equal("sk-test", p.Settings!["AiKey"]!.GetValue<string>());

        var b = Data("b");
        var plan = SettingsTransfer.Plan(p, null, [], b);
        SettingsTransfer.Apply(p, plan, b, new ServerRegistry(Path.Combine(b, "servers.json")), FakeProtect);
        var merged = JsonNode.Parse(File.ReadAllText(Path.Combine(b, "settings.json")))!;
        Assert.Equal("enc:sk-test", merged["AiKey"]!.GetValue<string>());
        Assert.Equal("cf-key", merged["CurseForgeApiKey"]!.GetValue<string>());
    }

    [Fact]
    public void Round_trip_keeps_settings_servers_and_config_files()
    {
        var (root, s) = Source();
        var zip = Export(root, s, new(IncludeChats: true));
        var b = Data("b");
        var registry = new ServerRegistry(Path.Combine(b, "servers.json"));
        var p = SettingsTransfer.Read(zip);
        var plan = SettingsTransfer.Plan(p, null, [], b);
        Assert.Equal(1, plan.NewChats);
        var r = SettingsTransfer.Apply(p, plan, b, registry, FakeProtect);

        Assert.Equal(1, r.Added);
        Assert.Equal(1, r.Chats);
        var back = Assert.Single(registry.Load());
        Assert.Equal(s with { Directory = Path.GetFullPath(s.Directory) }, back);
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(b, "settings.json")))!;
        Assert.Equal("tr", settings["Language"]!.GetValue<string>());
        Assert.Equal(9, settings["BackupsToKeep"]!.GetValue<int>());
        Assert.Null(settings["AiKey"]);
        Assert.True(File.Exists(Path.Combine(b, "assistant", "chats", "c1.json")));

        // Same folder: the existing webhook stays because the package had none.
        Assert.Equal("https://discord.com/api/webhooks/1/x", DiscordSettings.Load(s.Directory).WebhookUrl);
        Assert.Equal("@here", DiscordSettings.Load(s.Directory).Mention);
    }

    [Fact]
    public void Missing_folders_are_detected_and_can_be_pointed_elsewhere()
    {
        var (root, s) = Source();
        var zip = Export(root, s, new());
        var b = Data("b");
        var p = SettingsTransfer.Read(zip);
        var plan = SettingsTransfer.Plan(p, null, [], b, _ => false);
        var item = Assert.Single(plan.MissingDirectories);
        Assert.Equal(ServerImportAction.Skip, item.Action);

        var moved = Data("moved");
        item.TargetDirectory = moved;
        item.Action = ServerImportAction.Add;
        var registry = new ServerRegistry(Path.Combine(b, "servers.json"));
        var r = SettingsTransfer.Apply(p, plan, b, registry, FakeProtect);
        Assert.Equal(2, r.ConfigFiles);
        Assert.Equal(Path.GetFullPath(moved), Assert.Single(registry.Load()).Directory);
        Assert.True(File.Exists(Path.Combine(moved, ".packcheck", "automation.json")));
        Assert.Null(DiscordSettings.Load(moved).WebhookUrl);
    }

    [Fact]
    public void Duplicates_are_never_overwritten_without_a_choice()
    {
        var (root, s) = Source();
        var zip = Export(root, s, new());
        var b = Data("b");
        var registry = new ServerRegistry(Path.Combine(b, "servers.json"));
        var mine = s with { Name = "Mine", MaxMemoryMb = 8192 };
        registry.Save([mine]);
        var p = SettingsTransfer.Read(zip);

        var plan = SettingsTransfer.Plan(p, null, registry.Load(), b);
        var item = Assert.Single(plan.Servers);
        Assert.Equal(ServerImportStatus.SameId, item.Status);
        Assert.Equal("Mine", item.ExistingName);
        Assert.Equal(ServerImportAction.Skip, item.Action);
        SettingsTransfer.Apply(p, plan, b, registry, FakeProtect);
        Assert.Equal("Mine", Assert.Single(registry.Load()).Name);

        // Add on an existing Id is refused too.
        item.Action = ServerImportAction.Add;
        SettingsTransfer.Apply(p, plan, b, registry, FakeProtect);
        Assert.Equal("Mine", Assert.Single(registry.Load()).Name);

        item.Action = ServerImportAction.AddAsCopy;
        SettingsTransfer.Apply(p, plan, b, registry, FakeProtect);
        var all = registry.Load();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, x => x.Name == "Survival (2)" && x.Id != s.Id);

        item.Action = ServerImportAction.Replace;
        SettingsTransfer.Apply(p, plan, b, registry, FakeProtect);
        Assert.Equal("Survival", registry.Load().Single(x => x.Id == s.Id).Name);
    }

    [Fact]
    public void Same_folder_under_another_id_is_flagged()
    {
        var (root, s) = Source();
        var p = SettingsTransfer.Read(Export(root, s, new()));
        var plan = SettingsTransfer.Plan(p, null, [s with { Id = "other", Name = "Other" }], Data("b"));
        Assert.Equal(ServerImportStatus.SameDirectory, plan.Servers[0].Status);
        Assert.Equal(ServerImportAction.Skip, plan.Servers[0].Action);
    }

    [Fact]
    public void Preview_lists_only_changed_settings_and_unticked_ones_are_kept()
    {
        var (root, s) = Source();
        var p = SettingsTransfer.Read(Export(root, s, new()));
        var b = Data("b");
        File.WriteAllText(Path.Combine(b, "settings.json"), """{ "Language": "de", "Theme": 1, "AiKey": "enc:mine" }""");
        var current = JsonNode.Parse(File.ReadAllText(Path.Combine(b, "settings.json")))!.AsObject();
        var plan = SettingsTransfer.Plan(p, current, [], b);

        Assert.DoesNotContain(plan.SettingChanges, c => c.Key == "Theme");
        Assert.DoesNotContain(plan.SettingChanges, c => c.Key is "AiKey" or "FirstRunDone");
        var lang = plan.SettingChanges.Single(c => c.Key == "Language");
        Assert.Equal("de", lang.Current);
        Assert.Equal("tr", lang.Imported);
        lang.Apply = false;

        SettingsTransfer.Apply(p, plan, b, new ServerRegistry(Path.Combine(b, "servers.json")), FakeProtect);
        var merged = JsonNode.Parse(File.ReadAllText(Path.Combine(b, "settings.json")))!;
        Assert.Equal("de", merged["Language"]!.GetValue<string>());
        Assert.Equal("enc:mine", merged["AiKey"]!.GetValue<string>());
        Assert.Equal(9, merged["BackupsToKeep"]!.GetValue<int>());
    }

    [Fact]
    public void Older_settings_without_manifest_import_cleanly()
    {
        // An early-3.x layout: no manifest, fewer keys, one key that no longer exists.
        var zip = Path.Combine(_root, "old" + SettingsTransfer.Extension);
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("settings.json").Open()))
            {
                w.Write("""{ "Language": "fr", "AutoUpdate": true, "BackupsToKeep": 3, "LegacyOption": 42 }""");
            }

            using (var w = new StreamWriter(z.CreateEntry("servers.json").Open()))
            {
                w.Write("""[{ "Id": "old1", "Name": "Old", "Directory": "C:\\nowhere\\old", "Platform": 0, "MinecraftVersion": "1.20.1", "JarFile": "server.jar", "JavaPath": "java" }]""");
            }
        }

        var p = SettingsTransfer.Read(zip);
        Assert.Equal(0, p.Format);
        var b = Data("b");
        var plan = SettingsTransfer.Plan(p, null, [], b, _ => false);
        Assert.Single(plan.MissingDirectories);
        var r = SettingsTransfer.Apply(p, plan, b, new ServerRegistry(Path.Combine(b, "servers.json")), FakeProtect);
        Assert.Equal(0, r.Added);

        var json = File.ReadAllText(Path.Combine(b, "settings.json"));
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal("fr", settings.Language);
        Assert.True(settings.AutoUpdate);
        Assert.Equal(3, settings.BackupsToKeep);
        Assert.Equal(7, settings.BackupKeepDaily);
    }

    [Fact]
    public void Newer_format_and_foreign_zips_are_rejected()
    {
        var newer = Path.Combine(_root, "new.zip");
        using (var z = ZipFile.Open(newer, ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("manifest.json").Open()))
        {
            w.Write("""{ "format": 99 }""");
        }

        Assert.Throws<InvalidDataException>(() => SettingsTransfer.Read(newer));

        var foreign = Path.Combine(_root, "foreign.zip");
        using (var z = ZipFile.Open(foreign, ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("readme.txt").Open()))
        {
            w.Write("hi");
        }

        Assert.Throws<InvalidDataException>(() => SettingsTransfer.Read(foreign));
    }

    [Fact]
    public void Chat_entries_cannot_escape_the_chats_folder_and_existing_chats_stay()
    {
        var zip = Path.Combine(_root, "chats.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("settings.json").Open()))
            {
                w.Write("{}");
            }

            using (var w = new StreamWriter(z.CreateEntry("chats/../../evil.json").Open()))
            {
                w.Write("{}");
            }

            using (var w = new StreamWriter(z.CreateEntry("chats/keep.json").Open()))
            {
                w.Write("""{"new":1}""");
            }
        }

        var b = Data("b");
        Directory.CreateDirectory(Path.Combine(b, "assistant", "chats"));
        File.WriteAllText(Path.Combine(b, "assistant", "chats", "keep.json"), "mine");
        var p = SettingsTransfer.Read(zip);
        Assert.Equal(["keep.json"], p.Chats.Keys);
        var plan = SettingsTransfer.Plan(p, null, [], b);
        Assert.Equal(1, plan.ExistingChats);
        SettingsTransfer.Apply(p, plan, b, new ServerRegistry(Path.Combine(b, "servers.json")), FakeProtect);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(b, "assistant", "chats", "keep.json")));
        Assert.False(File.Exists(Path.Combine(_root, "evil.json")));
    }

    [Fact]
    public void Imported_java_path_must_be_java_and_bad_settings_are_not_written()
    {
        Assert.Equal("java", SettingsTransfer.SafeJava(@"C:\Windows\System32\calc.exe"));
        Assert.Equal(@"C:\jdk\bin\java.exe", SettingsTransfer.SafeJava(@"C:\jdk\bin\java.exe"));

        var zip = Path.Combine(_root, "bad" + SettingsTransfer.Extension);
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("manifest.json").Open()))
            {
                w.Write("""{ "format": 1, "appVersion": 5 }""");
            }

            using (var w = new StreamWriter(z.CreateEntry("settings.json").Open()))
            {
                w.Write("""{ "Theme": "abc", "AiKey": 7 }""");
            }
        }

        var p = SettingsTransfer.Read(zip);
        Assert.Null(p.AppVersion);
        var b = Data("b");
        File.WriteAllText(Path.Combine(b, "settings.json"), """{ "Language": "de" }""");
        var plan = SettingsTransfer.Plan(p, null, [], b);
        Assert.Throws<InvalidDataException>(() => SettingsTransfer.Apply(p, plan, b, new ServerRegistry(Path.Combine(b, "servers.json")), FakeProtect, m =>
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(m.ToJsonString()) is not null;
            }
            catch (System.Text.Json.JsonException)
            {
                return false;
            }
        }));
        Assert.Equal("""{ "Language": "de" }""", File.ReadAllText(Path.Combine(b, "settings.json")));
    }
}
