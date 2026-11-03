using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PackCheck.Servers;

/// <summary>What goes into a settings export.</summary>
/// <param name="IncludeSecrets">
/// Keeps the AI key, CurseForge key, DuckDNS token and Discord webhooks. Keys that are DPAPI-protected on this PC are
/// decrypted and written as plain text, because DPAPI data only opens for the same Windows user; on import they are
/// protected again for the user who imports. The file must then be kept private.
/// </param>
/// <param name="IncludeChats">Adds the assistant conversations (attachments are not included).</param>
public sealed record SettingsExportOptions(bool IncludeSecrets = false, bool IncludeChats = false);

/// <summary>Counts written by an export.</summary>
public sealed record SettingsExportSummary(int Servers, int ConfigFiles, int Chats, bool IncludesSecrets);

/// <summary>Per-server files carried in the package (raw JSON text, null when the server had none).</summary>
public sealed record ServerConfigFiles(string? Automation, string? Discord);

/// <summary>The content of a .packcheck-settings.zip after reading.</summary>
public sealed record SettingsPackage(
    int Format,
    string? AppVersion,
    bool IncludesSecrets,
    JsonObject? Settings,
    IReadOnlyList<ServerDefinition> Servers,
    IReadOnlyDictionary<string, ServerConfigFiles> ServerFiles,
    IReadOnlyDictionary<string, string> Chats);

/// <summary>One setting whose imported value differs from the current one.</summary>
public sealed class SettingChange(string key, string? current, string? imported, bool isSecret)
{
    public string Key { get; } = key;

    public string? Current { get; } = current;

    public string? Imported { get; } = imported;

    public bool IsSecret { get; } = isSecret;

    /// <summary>Whether this change is applied; the user can untick it in the preview.</summary>
    public bool Apply { get; set; } = true;
}

public enum ServerImportStatus
{
    /// <summary>No server with this Id or folder exists here.</summary>
    New,

    /// <summary>A server with the same Id is already registered.</summary>
    SameId,

    /// <summary>Another server already uses the same folder.</summary>
    SameDirectory,
}

public enum ServerImportAction
{
    Skip,
    Add,

    /// <summary>Overwrites the registered server that has the same Id.</summary>
    Replace,

    /// <summary>Adds it next to the existing one with a new Id.</summary>
    AddAsCopy,
}

/// <summary>One server from the package and what the user decided for it.</summary>
public sealed class ServerImportItem(ServerDefinition imported, ServerImportStatus status, string? existingName, bool directoryExists, bool hasAutomation, bool hasDiscord)
{
    public ServerDefinition Imported { get; } = imported;

    public ServerImportStatus Status { get; } = status;

    /// <summary>Name of the server here that collides with this one.</summary>
    public string? ExistingName { get; } = existingName;

    /// <summary>False when the original folder does not exist on this PC; the user must pick one or skip.</summary>
    public bool DirectoryExists { get; } = directoryExists;

    public bool HasAutomation { get; } = hasAutomation;

    public bool HasDiscord { get; } = hasDiscord;

    /// <summary>Where the server lives on this PC; starts as the original path.</summary>
    public string TargetDirectory { get; set; } = imported.Directory;

    /// <summary>
    /// New servers whose folder exists are added; everything else is skipped until the user decides,
    /// so nothing is overwritten silently.
    /// </summary>
    public ServerImportAction Action { get; set; } = status == ServerImportStatus.New && directoryExists ? ServerImportAction.Add : ServerImportAction.Skip;

    /// <summary>Replace automation.json / discord.json that already exist in the target folder.</summary>
    public bool OverwriteConfig { get; set; }
}

/// <summary>The preview: what an import would add or change. Mutable choices live on the items.</summary>
public sealed record SettingsImportPlan(
    IReadOnlyList<SettingChange> SettingChanges,
    IReadOnlyList<ServerImportItem> Servers,
    int NewChats,
    int ExistingChats)
{
    public IEnumerable<ServerImportItem> MissingDirectories => Servers.Where(s => !s.DirectoryExists);
}

public sealed record SettingsImportResult(int SettingsApplied, int Added, int Replaced, int Skipped, int ConfigFiles, int Chats, JsonObject? MergedSettings, IReadOnlyList<ServerDefinition> ChangedServers);

/// <summary>Export and import of PackCheck settings, the server list and per-server automation/Discord files.</summary>
public static class SettingsTransfer
{
    public const int FormatVersion = 1;
    public const string Extension = ".packcheck-settings.zip";

    /// <summary>Settings stored with DPAPI for the current Windows user.</summary>
    public static readonly IReadOnlyList<string> ProtectedKeys = ["AiKey", "DuckDnsToken"];

    /// <summary>Settings that are secret but stored as plain text.</summary>
    public static readonly IReadOnlyList<string> PlainSecretKeys = ["CurseForgeApiKey"];

    /// <summary>Machine-bound state that is never carried over.</summary>
    public static readonly IReadOnlyList<string> LocalOnlyKeys = ["FirstRunDone"];

    private const string WebhookKey = "WebhookUrl";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static bool IsSecretKey(string key) => ProtectedKeys.Contains(key) || PlainSecretKeys.Contains(key);

    /// <summary>Writes the package. <paramref name="unprotect"/> turns a DPAPI value into plain text (only used with secrets).</summary>
    public static SettingsExportSummary Export(string zipPath, string dataRoot, IReadOnlyList<ServerDefinition> servers, SettingsExportOptions options, Func<string?, string?> unprotect, string? appVersion)
    {
        var settings = ReadObject(Path.Combine(dataRoot, "settings.json")) ?? [];
        foreach (var key in LocalOnlyKeys)
        {
            settings.Remove(key);
        }

        foreach (var key in ProtectedKeys)
        {
            var plain = options.IncludeSecrets ? unprotect(settings[key]?.GetValue<string>()) : null;
            settings.Remove(key);
            if (!string.IsNullOrEmpty(plain))
            {
                settings[key] = plain;
            }
        }

        if (!options.IncludeSecrets)
        {
            foreach (var key in PlainSecretKeys)
            {
                settings.Remove(key);
            }
        }

        var tmp = zipPath + ".tmp";
        var configs = 0;
        var chats = 0;
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            var manifest = new JsonObject
            {
                ["format"] = FormatVersion,
                ["appVersion"] = appVersion,
                ["created"] = DateTimeOffset.Now.ToString("O"),
                ["includesSecrets"] = options.IncludeSecrets,
                ["includesChats"] = options.IncludeChats,
            };
            Write(zip, "manifest.json", manifest.ToJsonString(Json));
            Write(zip, "settings.json", settings.ToJsonString(Json));
            Write(zip, "servers.json", JsonSerializer.Serialize(servers, Json));
            foreach (var s in servers)
            {
                var automation = TryRead(AutomationStore.FileOf(s));
                if (automation is not null)
                {
                    Write(zip, $"servers/{s.Id}/automation.json", automation);
                    configs++;
                }

                var discord = TryRead(DiscordSettings.FileFor(s.Directory));
                if (discord is not null)
                {
                    Write(zip, $"servers/{s.Id}/discord.json", options.IncludeSecrets ? discord : StripWebhook(discord));
                    configs++;
                }
            }

            var chatDir = Path.Combine(dataRoot, "assistant", "chats");
            if (options.IncludeChats && Directory.Exists(chatDir))
            {
                foreach (var f in Directory.EnumerateFiles(chatDir, "*.json"))
                {
                    Write(zip, "chats/" + Path.GetFileName(f), File.ReadAllText(f));
                    chats++;
                }
            }
        }

        File.Move(tmp, zipPath, overwrite: true);
        return new(servers.Count, configs, chats, options.IncludeSecrets);
    }

    /// <summary>Reads a package. Packages without a manifest (format 0) and settings from older versions are accepted.</summary>
    public static SettingsPackage Read(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var manifest = ParseObject(Text(zip, "manifest.json"));
        var format = manifest?["format"] is JsonValue f && f.TryGetValue<int>(out var n) ? n : 0;
        if (format > FormatVersion)
        {
            throw new InvalidDataException($"This file was made by a newer PackCheck (format {format}).");
        }

        var settings = ParseObject(Text(zip, "settings.json"));
        var serversText = Text(zip, "servers.json");
        if (settings is null && serversText is null)
        {
            throw new InvalidDataException("This is not a PackCheck settings file.");
        }

        List<ServerDefinition> servers;
        try
        {
            servers = serversText is null ? [] : JsonSerializer.Deserialize<List<ServerDefinition>>(serversText, Json) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The server list in this file is damaged.", ex);
        }

        var files = new Dictionary<string, ServerConfigFiles>(StringComparer.Ordinal);
        foreach (var s in servers)
        {
            var a = Text(zip, $"servers/{s.Id}/automation.json");
            var d = Text(zip, $"servers/{s.Id}/discord.json");
            if (a is not null || d is not null)
            {
                files[s.Id] = new(a, d);
            }
        }

        var chats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("chats/", StringComparison.Ordinal) && e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            // Only plain file names: an entry can never write outside the chats folder.
            if (e.Name == Path.GetFileName(e.Name) && e.FullName == "chats/" + e.Name)
            {
                using var r = new StreamReader(e.Open());
                chats[e.Name] = r.ReadToEnd();
            }
        }

        var secrets = manifest?["includesSecrets"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        return new(format, manifest?["appVersion"]?.GetValue<string>(), secrets, settings, servers, files, chats);
    }

    /// <summary>Compares the package with what is here. Nothing is written.</summary>
    public static SettingsImportPlan Plan(SettingsPackage package, JsonObject? currentSettings, IReadOnlyList<ServerDefinition> currentServers, string dataRoot, Func<string, bool>? directoryExists = null)
    {
        directoryExists ??= Directory.Exists;
        var changes = new List<SettingChange>();
        if (package.Settings is { } imported)
        {
            foreach (var (key, value) in imported)
            {
                if (LocalOnlyKeys.Contains(key) || value is null)
                {
                    continue;
                }

                var current = currentSettings?[key];

                // Protected values on this PC are ciphertext and cannot be compared, so a present secret always counts as a change.
                if (!ProtectedKeys.Contains(key) && current is not null && JsonNode.DeepEquals(current, value))
                {
                    continue;
                }

                var secret = IsSecretKey(key);
                changes.Add(new(key, secret ? Mask(current) : Display(current), secret ? Mask(value) : Display(value), secret));
            }
        }

        var items = new List<ServerImportItem>();
        foreach (var s in package.Servers)
        {
            var sameId = currentServers.FirstOrDefault(c => c.Id == s.Id);
            var sameDir = sameId is null ? currentServers.FirstOrDefault(c => SamePath(c.Directory, s.Directory)) : null;
            var status = sameId is not null ? ServerImportStatus.SameId : sameDir is not null ? ServerImportStatus.SameDirectory : ServerImportStatus.New;
            package.ServerFiles.TryGetValue(s.Id, out var cfg);
            items.Add(new(s, status, (sameId ?? sameDir)?.Name, directoryExists(s.Directory), cfg?.Automation is not null, cfg?.Discord is not null));
        }

        var chatDir = Path.Combine(dataRoot, "assistant", "chats");
        var existing = package.Chats.Keys.Count(k => File.Exists(Path.Combine(chatDir, k)));
        return new(changes, items, package.Chats.Count - existing, existing);
    }

    /// <summary>
    /// Applies the plan: merges the ticked settings, registers servers as decided and writes their automation/Discord files.
    /// Settings missing from the package keep their current value; existing chats are never replaced.
    /// </summary>
    public static SettingsImportResult Apply(SettingsPackage package, SettingsImportPlan plan, string dataRoot, ServerRegistry registry, Func<string?, string?> protect)
    {
        var settingsFile = Path.Combine(dataRoot, "settings.json");
        JsonObject? merged = null;
        var applied = 0;
        var picked = plan.SettingChanges.Where(c => c.Apply).ToList();
        if (picked.Count > 0 && package.Settings is { } imported)
        {
            merged = ReadObject(settingsFile) ?? [];
            foreach (var c in picked)
            {
                var value = imported[c.Key]?.DeepClone();
                if (ProtectedKeys.Contains(c.Key))
                {
                    value = protect(value?.GetValue<string>()) is { } p ? JsonValue.Create(p) : null;
                    if (value is null)
                    {
                        continue;
                    }
                }

                merged[c.Key] = value;
                applied++;
            }

            Directory.CreateDirectory(dataRoot);
            var tmp = settingsFile + ".tmp";
            File.WriteAllText(tmp, merged.ToJsonString(Json));
            File.Move(tmp, settingsFile, overwrite: true);
        }

        var list = registry.Load().ToList();
        int added = 0, replaced = 0, skipped = 0, configs = 0;
        var changed = new List<ServerDefinition>();
        foreach (var item in plan.Servers)
        {
            var dir = item.TargetDirectory;
            if (item.Action == ServerImportAction.Skip || string.IsNullOrWhiteSpace(dir))
            {
                skipped++;
                continue;
            }

            var def = item.Imported with { Directory = Path.GetFullPath(dir) };
            switch (item.Action)
            {
                case ServerImportAction.Replace:
                    list.RemoveAll(s => s.Id == def.Id);
                    replaced++;
                    break;
                case ServerImportAction.AddAsCopy:
                    def = def with { Id = Guid.NewGuid().ToString("N"), Name = UniqueName(def.Name, list) };
                    added++;
                    break;
                default:
                    if (list.Any(s => s.Id == def.Id))
                    {
                        // An Add over an existing Id would overwrite it; the user has to choose Replace for that.
                        skipped++;
                        continue;
                    }

                    added++;
                    break;
            }

            list.Add(def);
            changed.Add(def);
            if (package.ServerFiles.TryGetValue(item.Imported.Id, out var files))
            {
                configs += WriteConfigs(def, files, item.OverwriteConfig);
            }
        }

        if (changed.Count > 0)
        {
            registry.Save(list);
        }

        var chatDir = Path.Combine(dataRoot, "assistant", "chats");
        var chats = 0;
        foreach (var (name, text) in package.Chats)
        {
            var target = Path.Combine(chatDir, name);
            if (!File.Exists(target))
            {
                Directory.CreateDirectory(chatDir);
                File.WriteAllText(target, text);
                chats++;
            }
        }

        return new(applied, added, replaced, skipped, configs, chats, merged, changed);
    }

    private static int WriteConfigs(ServerDefinition def, ServerConfigFiles files, bool overwrite)
    {
        var written = 0;
        if (files.Automation is { } a)
        {
            var f = AutomationStore.FileOf(def);
            if (overwrite || !File.Exists(f))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                File.WriteAllText(f, a);
                written++;
            }
        }

        if (files.Discord is { } d)
        {
            var f = DiscordSettings.FileFor(def.Directory);
            if (overwrite || !File.Exists(f))
            {
                // A package without secrets has no webhook; keep the one this folder already has.
                var node = ParseObject(d) ?? [];
                if (node[WebhookKey] is null && ParseObject(TryRead(f))?[WebhookKey] is { } keep)
                {
                    node[WebhookKey] = keep.DeepClone();
                }

                Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                File.WriteAllText(f, node.ToJsonString(Json));
                written++;
            }
        }

        return written;
    }

    private static string UniqueName(string name, List<ServerDefinition> list)
    {
        for (var i = 2; ; i++)
        {
            var candidate = $"{name} ({i})";
            if (!list.Any(s => string.Equals(s.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
    }

    private static string StripWebhook(string json)
    {
        var node = ParseObject(json);
        if (node is null)
        {
            return json;
        }

        node.Remove(WebhookKey);
        return node.ToJsonString(Json);
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? Display(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => n.ToJsonString(),
    };

    private static string? Mask(JsonNode? n) => n is null ? null : "••••••";

    private static void Write(ZipArchive zip, string name, string text)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open());
        w.Write(text);
    }

    private static string? Text(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name);
        if (e is null)
        {
            return null;
        }

        using var r = new StreamReader(e.Open());
        return r.ReadToEnd();
    }

    private static string? TryRead(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static JsonObject? ReadObject(string file) => ParseObject(TryRead(file));

    private static JsonObject? ParseObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
