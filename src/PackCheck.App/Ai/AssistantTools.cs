using PackCheck.App.Infrastructure;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using PackCheck.App.ViewModels;
using PackCheck.Reporting;
using PackCheck.Servers;
using PackCheck.Validation;

namespace PackCheck.App.Ai;

/// <summary>A change the assistant wants to make; nothing happens until the user approves it.</summary>
public sealed record PendingAction(string Title, string Preview, Func<Task<string>> Execute);

/// <summary>
/// The tools the assistant may call. Reads are answered directly; every write, command or install is turned into a
/// <see cref="PendingAction"/> that needs the user's approval. All paths are confined to the selected server folder;
/// there is no shell access.
/// </summary>
/// <summary>One line of the assistant's task list.</summary>
public sealed record TodoItem(string Text, string Status)
{
    public bool IsDone => Status == "done";

    public bool IsActive => Status == "in_progress";
}

public sealed class AssistantTools(ServersViewModel servers, Func<ServerItemViewModel?> current)
{
    private const int MaxRead = 60_000;

    public static IReadOnlyList<AiToolSpec> Specs { get; } =
    [
        Spec("list_servers", "List all servers managed by PackCheck with platform, version, port and state.", []),
        Spec("read_console", "Read the last lines of the selected server's console.", [("lines", "integer", "How many lines (max 400)")]),
        Spec("list_files", "List files in a folder of the selected server (relative path, '' for the root).", [("path", "string", "Folder relative to the server root")]),
        Spec("find_files", "Find files of the selected server recursively by glob pattern, e.g. 'plugins/**/*.yml', '**/messages*.yml', 'plugins/*/lang/*'. Use it to find every file of a kind before working through them.",
            [("pattern", "string", "Glob pattern relative to the server root; ** matches any folders, * any characters in a name")], ["pattern"]),
        Spec("search_files", "Search text files of the selected server for a text (case-insensitive). Returns file:line: text.",
            [("text", "string", "Text to look for"), ("path", "string", "Folder to search in ('' for the whole server)")], ["text"]),
        Spec("read_file", "Read a text file of the selected server (relative path). Large files are returned in parts: pass start_line to continue where the previous part ended.",
            [("path", "string", "File relative to the server root"), ("start_line", "integer", "First line to return (1-based, default 1)")], ["path"]),
        Spec("write_file", "Create or replace a text file in the selected server. Requires user approval; a backup is kept. Use it to fix configs or to write translated message files (keep keys, placeholders like %player% or {0}, and & or § color codes unchanged).",
            [("path", "string", "File relative to the server root"), ("content", "string", "Full new content")], ["path", "content"]),
        Spec("edit_file", "Replace exact text in a file of the selected server (old_text must appear exactly once). Prefer it over write_file for changes in large files, and for translating big files part by part. A backup is kept.",
            [("path", "string", "File relative to the server root"), ("old_text", "string", "Exact text to replace (unique in the file)"), ("new_text", "string", "Replacement text")], ["path", "old_text", "new_text"]),
        Spec("update_todo", "Show and update your task list for multi-step work (for example one item per plugin to translate). Send the full list each time with every item's status. Keep it current as you work.",
            [("items", "array", "List of {text, status} where status is pending, in_progress or done")], ["items"]),
        Spec("task_complete", "Call when the whole task is finished and checked. Give a short summary of what was done (and anything left that needs the user). Do not call it while todo items are still open.",
            [("summary", "string", "What was done")], ["summary"]),
        Spec("list_addons", "List installed plugins and mods of the selected server.", []),
        Spec("run_validation", "Run PackCheck's static validation on the selected server and return the findings.", []),
        Spec("send_command", "Send a console command to the selected (running) server. Requires approval.", [("command", "string", "Command without the leading slash")], ["command"]),
        Spec("start_server", "Start the selected server. Requires approval.", []),
        Spec("stop_server", "Stop the selected server. Requires approval.", []),
        Spec("create_servers", "Plan and create one or more new servers. Requires approval. Each item: name, platform (Paper, Purpur, Folia, Leaf, Leaves, Spigot, CraftBukkit, Vanilla, Fabric, Quilt, Forge, NeoForge, SpongeVanilla, Mohist, Youer, Arclight, Velocity, BungeeCord), version ('latest' allowed), memory_mb, port.",
            [("servers", "array", "List of {name, platform, version, memory_mb, port}")], ["servers"]),
    ];

    private static AiToolSpec Spec(string name, string description, (string Name, string Type, string Doc)[] props, string[]? required = null)
    {
        var properties = new JsonObject();
        foreach (var (n, t, d) in props)
        {
            properties[n] = t == "array"
                ? new JsonObject { ["type"] = "array", ["description"] = d, ["items"] = new JsonObject { ["type"] = "object" } }
                : new JsonObject { ["type"] = t, ["description"] = d };
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required is { Length: > 0 })
        {
            schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        }

        return new AiToolSpec(name, description, schema);
    }

    private ServerItemViewModel Server => current() ?? throw new InvalidOperationException("No server is selected. Ask the user to select one on the Servers page.");

    private string Confine(string relative)
    {
        var root = Path.GetFullPath(Server.Definition.Directory);
        var full = Path.GetFullPath(Path.Combine(root, relative.TrimStart('/', '\\')));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Paths must stay inside the server folder.");
        }

        return full;
    }

    private static string S(JsonObject a, string key) => a[key]?.ToString() ?? string.Empty;

    /// <summary>Returns either an immediate result (reads) or a pending action that needs approval (writes).</summary>
    public async Task<(string? Result, PendingAction? Pending)> RunAsync(AiToolCall call)
    {
        var a = call.Arguments;
        switch (call.Name)
        {
            case "list_servers":
                return (string.Join("\n", servers.Items.Select(s => $"- {s.Name}: {s.Definition.Platform} {s.Definition.MinecraftVersion}, port {s.Definition.Port}, {s.State}{(s == current() ? " (selected)" : string.Empty)}")), null);

            case "read_console":
                {
                    var n = Math.Clamp(int.TryParse(S(a, "lines"), out var l) ? l : 80, 1, 400);
                    var lines = Server.Console.Skip(Math.Max(0, Server.Console.Count - n)).Select(e => e.Raw);
                    return ($"State: {Server.State}\n" + string.Join("\n", lines), null);
                }

            case "list_files":
                {
                    var dir = Confine(S(a, "path"));
                    var entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Take(300)
                        .Select(e => e is DirectoryInfo ? e.Name + "/" : $"{e.Name} ({((FileInfo)e).Length} bytes)");
                    return (string.Join("\n", entries), null);
                }

            case "read_file":
                {
                    var file = Confine(S(a, "path"));
                    if (!FilesViewModel.IsText(file))
                    {
                        return ("This is not a text file.", null);
                    }

                    var start = Math.Max(1, int.TryParse(S(a, "start_line"), out var sl) ? sl : 1);
                    var lines = PropertiesText.Decode(await File.ReadAllBytesAsync(file).ConfigureAwait(true)).Replace("\r\n", "\n").Split('\n');
                    return (ReadPart(lines, start), null);
                }

            case "find_files":
                return (FindFiles(S(a, "pattern")), null);

            case "search_files":
                return (SearchFiles(S(a, "text"), S(a, "path")), null);

            case "edit_file":
                {
                    var path = S(a, "path");
                    var full = Confine(path);
                    var oldText = S(a, "old_text");
                    var newText = S(a, "new_text");
                    if (!File.Exists(full))
                    {
                        return ($"{path} does not exist. Use write_file to create it.", null);
                    }

                    var current = PropertiesText.Decode(await File.ReadAllBytesAsync(full).ConfigureAwait(true));
                    var (found, normalized) = Locate(current, oldText);
                    if (found != 1)
                    {
                        return (found == 0 ? "old_text was not found in the file. Read the file again and copy the text exactly." : $"old_text appears {found} times; include more surrounding lines so it is unique.", null);
                    }

                    var updated = ReplaceOnce(current, normalized, newText.Replace("\r\n", "\n").Replace("\n", current.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n"));
                    return (null, new PendingAction($"edit {path}", Diff(current, updated), async () =>
                    {
                        Backup(full);
                        await File.WriteAllTextAsync(full, updated, new UTF8Encoding(false)).ConfigureAwait(true);
                        return $"Edited {path}.";
                    }));
                }

            case "update_todo":
                {
                    var items = (a["items"] as JsonArray ?? []).OfType<JsonObject>()
                        .Select(o => new TodoItem(o["text"]?.ToString() ?? string.Empty, o["status"]?.ToString() ?? "pending")).Where(t => t.Text.Length > 0).ToList();
                    TodoChanged?.Invoke(items);
                    var open = items.Count(t => t.Status != "done");
                    return ($"Task list updated: {items.Count - open} done, {open} open.", null);
                }

            case "task_complete":
                return ("Task marked complete.", null);

            case "list_addons":
                Server.RefreshPlugins();
                return (Server.PluginList.Count == 0 ? "No plugins or mods installed." : string.Join("\n", Server.PluginList.Select(p => $"- {p.Name ?? "?"} {p.Version} ({p.File}){(p.Enabled ? string.Empty : " [disabled]")}")), null);

            case "run_validation":
                {
                    var mc = Core.MinecraftVersion.TryParse(Server.Definition.MinecraftVersion, out var v) ? v : (Core.MinecraftVersion?)null;
                    var report = await new PackValidator().ValidateAsync(Server.Definition.Directory, new ValidationOptions { MinecraftVersion = mc }).ConfigureAwait(true);
                    var sb = new StringBuilder($"Overall: {HtmlReporter.Label(report.Overall)}\n");
                    foreach (var c in report.Checks.Where(c => c.Status != Core.CheckStatus.Pass))
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"- [{c.Status}] {c.Id} {c.Name}: {c.Description}");
                        if (c.Fix is not null)
                        {
                            sb.Append(" Fix: ").Append(c.Fix.Text);
                        }

                        sb.Append('\n');
                    }

                    return (sb.ToString(), null);
                }

            case "write_file":
                {
                    var path = S(a, "path");
                    var full = Confine(path);
                    var content = S(a, "content");
                    var old = File.Exists(full) ? await File.ReadAllTextAsync(full).ConfigureAwait(true) : string.Empty;
                    return (null, new PendingAction($"write {path}", Diff(old, content), async () =>
                    {
                        Backup(full);

                        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                        await File.WriteAllTextAsync(full, content, new UTF8Encoding(false)).ConfigureAwait(true);
                        return $"Wrote {path} ({content.Length} characters). A backup of the previous version is in .packcheck/edits.";
                    }));
                }

            case "send_command":
                {
                    var cmd = S(a, "command").Trim().TrimStart('/');
                    return (null, new PendingAction($"console: {cmd}", "> " + cmd, async () =>
                    {
                        Server.Command = cmd;
                        await Server.SendCommand.ExecuteAsync(null).ConfigureAwait(true);
                        await Task.Delay(1500).ConfigureAwait(true);
                        return "Sent. Recent output:\n" + string.Join("\n", Server.Console.Skip(Math.Max(0, Server.Console.Count - 15)).Select(e => e.Raw));
                    }));
                }

            case "start_server":
                return (null, new PendingAction($"start {Server.Name}", Server.Name, async () =>
                {
                    await Server.StartCommand.ExecuteAsync(null).ConfigureAwait(true);
                    return "Start requested; read the console in a moment to see the result.";
                }));

            case "stop_server":
                return (null, new PendingAction($"stop {Server.Name}", Server.Name, async () =>
                {
                    await Server.StopCommand.ExecuteAsync(null).ConfigureAwait(true);
                    return "Stopped.";
                }));

            case "create_servers":
                return (null, CreateServers(a["servers"] as JsonArray ?? []));
        }

        return ($"Unknown tool {call.Name}.", null);
    }

    /// <summary>Raised when the model sends its task list.</summary>
    public event Action<IReadOnlyList<TodoItem>>? TodoChanged;

    private const int ReadLines = 800;

    /// <summary>Up to <see cref="ReadLines"/> lines (and <see cref="MaxRead"/> characters) from <paramref name="start"/>, with a note how to continue.</summary>
    public static string ReadPart(string[] lines, int start)
    {
        if (start > lines.Length)
        {
            return $"The file has only {lines.Length} lines.";
        }

        var sb = new StringBuilder();
        var i = start - 1;
        for (; i < lines.Length && i - (start - 1) < ReadLines && sb.Length + lines[i].Length < MaxRead; i++)
        {
            sb.Append(lines[i]).Append('\n');
        }

        if (i < lines.Length)
        {
            sb.Append(CultureInfo.InvariantCulture, $"[lines {start}-{i} of {lines.Length}; call read_file with start_line {i + 1} for the rest]");
        }

        return sb.ToString();
    }

    private static readonly string[] SkipDirs = ["libraries", "versions", "cache", ".packcheck", ".packcheck-backups", "logs", "crash-reports", "world", "world_nether", "world_the_end", ".fabric", "bundler"];

    private IEnumerable<string> WalkFiles(string dir)
    {
        var root = Path.GetFullPath(Server.Definition.Directory);
        var stack = new Stack<string>([dir]);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            IEnumerable<string> files;
            IEnumerable<string> subs;
            try
            {
                files = Directory.EnumerateFiles(d).ToList();
                subs = Directory.EnumerateDirectories(d).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var f in files)
            {
                yield return f;
            }

            foreach (var s in subs)
            {
                var rel = Path.GetRelativePath(root, s);
                if (!SkipDirs.Contains(rel, StringComparer.OrdinalIgnoreCase) && !Directory.Exists(Path.Combine(s, "region")))
                {
                    stack.Push(s);
                }
            }
        }
    }

    /// <summary>Glob to regex: ** any folders, * any characters within a name, ? one character.</summary>
    public static System.Text.RegularExpressions.Regex GlobRegex(string pattern)
    {
        var p = pattern.Replace('\\', '/').TrimStart('/');
        var sb = new StringBuilder("^");
        for (var i = 0; i < p.Length; i++)
        {
            if (p[i] == '*' && i + 1 < p.Length && p[i + 1] == '*')
            {
                var slash = i + 2 < p.Length && p[i + 2] == '/';
                sb.Append(slash ? "(?:.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else
            {
                sb.Append(p[i] switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    _ => System.Text.RegularExpressions.Regex.Escape(p[i].ToString()),
                });
            }
        }

        return new System.Text.RegularExpressions.Regex(sb.Append('$').ToString(), System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private string FindFiles(string pattern)
    {
        var root = Path.GetFullPath(Server.Definition.Directory);
        var rx = GlobRegex(pattern.Length == 0 ? "**/*" : pattern);
        var hits = WalkFiles(root).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Where(r => rx.IsMatch(r)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (hits.Count == 0)
        {
            return "No files match.";
        }

        var shown = hits.Take(500).Select(h => $"{h} ({new FileInfo(Path.Combine(root, h)).Length} bytes)");
        return string.Join("\n", shown) + (hits.Count > 500 ? $"\n… {hits.Count - 500} more; use a narrower pattern." : $"\n({hits.Count} files)");
    }

    private string SearchFiles(string text, string path)
    {
        if (text.Length == 0)
        {
            return "Give a text to search for.";
        }

        var root = Path.GetFullPath(Server.Definition.Directory);
        var sb = new StringBuilder();
        var count = 0;
        foreach (var f in WalkFiles(Confine(path)).Where(FilesViewModel.IsText))
        {
            try
            {
                if (new FileInfo(f).Length > 4_000_000)
                {
                    continue;
                }

                var lines = File.ReadAllLines(f);
                for (var i = 0; i < lines.Length && count < 300; i++)
                {
                    if (lines[i].Contains(text, StringComparison.OrdinalIgnoreCase))
                    {
                        var line = lines[i].Trim();
                        sb.Append(CultureInfo.InvariantCulture, $"{Path.GetRelativePath(root, f).Replace('\\', '/')}:{i + 1}: {(line.Length > 200 ? line[..200] + "…" : line)}\n");
                        count++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return count == 0 ? "No matches." : sb.ToString() + (count >= 300 ? "[first 300 matches]" : string.Empty);
    }

    /// <summary>Counts matches of <paramref name="oldText"/>, trying the file's own line endings too.</summary>
    public static (int Count, string Text) Locate(string content, string oldText)
    {
        if (oldText.Length == 0)
        {
            return (0, oldText);
        }

        var variants = new[] { oldText, oldText.Replace("\r\n", "\n").Replace("\n", "\r\n"), oldText.Replace("\r\n", "\n") }.Distinct();
        foreach (var v in variants)
        {
            var n = Count(content, v);
            if (n > 0)
            {
                return (n, v);
            }
        }

        return (0, oldText);
    }

    private static int Count(string s, string part)
    {
        var n = 0;
        for (var i = s.IndexOf(part, StringComparison.Ordinal); i >= 0; i = s.IndexOf(part, i + part.Length, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }

    private static string ReplaceOnce(string s, string oldText, string newText)
    {
        var i = s.IndexOf(oldText, StringComparison.Ordinal);
        return s[..i] + newText + s[(i + oldText.Length)..];
    }

    private void Backup(string full)
    {
        if (File.Exists(full))
        {
            var backup = Path.Combine(Server.Definition.Directory, ".packcheck", "edits", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(backup);
            File.Copy(full, Path.Combine(backup, $"{DateTime.Now:HHmmss}-{Path.GetFileName(full)}"), overwrite: true);
        }
    }

    private PendingAction CreateServers(JsonArray list)
    {
        var plan = list.OfType<JsonObject>().Select(o => (
            Name: o["name"]?.ToString() ?? "server",
            Platform: Enum.TryParse<ServerPlatform>(o["platform"]?.ToString(), true, out var p) ? p : ServerPlatform.Paper,
            Version: o["version"]?.ToString() ?? "latest",
            Memory: int.TryParse(o["memory_mb"]?.ToString(), out var m) ? Math.Clamp(m, 512, SystemLimits.MaxRamGb * 1024) : Math.Min(4096, SystemLimits.MaxRamGb * 1024),
            Port: int.TryParse(o["port"]?.ToString(), out var port) ? port : 0)).ToList();
        var table = new StringBuilder("Name | Platform | Version | RAM | Port\n");
        foreach (var s in plan)
        {
            table.Append(CultureInfo.InvariantCulture, $"{s.Name} | {s.Platform} | {s.Version} | {s.Memory} MB | {(s.Port == 0 ? "auto" : s.Port)}\n");
        }

        table.Append('\n').Append(Loc.T("Assistant_EulaNotice"));
        return new PendingAction($"create {plan.Count} server(s)", table.ToString(), async () =>
        {
            var sources = (ServerSources)App.Services.GetService(typeof(ServerSources))!;
            var creator = (ServerCreator)App.Services.GetService(typeof(ServerCreator))!;
            var java = (JavaRuntimes)App.Services.GetService(typeof(JavaRuntimes))!;
            var state = (AppState)App.Services.GetService(typeof(AppState))!;
            var settings = (Infrastructure.AppSettings)App.Services.GetService(typeof(Infrastructure.AppSettings))!;
            var results = new StringBuilder();
            var usedPorts = servers.Items.Select(s => s.Definition.Port).ToHashSet();
            foreach (var s in plan)
            {
                try
                {
                    var version = s.Version.Equals("latest", StringComparison.OrdinalIgnoreCase)
                        ? (await sources.GetVersionsAsync(s.Platform, CancellationToken.None).ConfigureAwait(true))[0]
                        : s.Version;
                    var port = s.Port > 0 ? s.Port : Enumerable.Range(25565, 200).First(p => !usedPorts.Contains(p));
                    usedPorts.Add(port);
                    var runtime = await java.EnsureAsync(PlatformInfo.RequiredJava(s.Platform, version), state.JavaRuntimes, null, CancellationToken.None).ConfigureAwait(true);
                    var def = await creator.CreateAsync(new ServerCreator.Request(s.Name, settings.DefaultServerFolder, s.Platform, version, runtime.JavaExe, s.Memory, port, null, AcceptEula: true), null, CancellationToken.None).ConfigureAwait(true);
                    servers.Add(def);
                    results.Append(CultureInfo.InvariantCulture, $"Created {def.Name}: {def.Platform} {version} on port {port}.\n");
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Net.Http.HttpRequestException or ArgumentException)
                {
                    results.Append(CultureInfo.InvariantCulture, $"Failed {s.Name}: {ex.Message}\n");
                }
            }

            return results.ToString();
        });
    }

    /// <summary>A compact line diff for the approval card.</summary>
    public static string Diff(string before, string after)
    {
        var a = before.Replace("\r\n", "\n").Split('\n');
        var b = after.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        var max = Math.Max(a.Length, b.Length);
        var shown = 0;
        for (var i = 0; i < max && shown < 200; i++)
        {
            var x = i < a.Length ? a[i] : null;
            var y = i < b.Length ? b[i] : null;
            if (x == y)
            {
                continue;
            }

            if (x is not null)
            {
                sb.Append("- ").Append(x).Append('\n');
            }

            if (y is not null)
            {
                sb.Append("+ ").Append(y).Append('\n');
            }

            shown++;
        }

        return sb.Length == 0 ? "(no changes)" : sb.ToString();
    }
}
