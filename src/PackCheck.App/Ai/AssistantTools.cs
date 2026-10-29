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
public sealed class AssistantTools(ServersViewModel servers, Func<ServerItemViewModel?> current)
{
    private const int MaxRead = 60_000;

    public static IReadOnlyList<AiToolSpec> Specs { get; } =
    [
        Spec("list_servers", "List all servers managed by PackCheck with platform, version, port and state.", []),
        Spec("read_console", "Read the last lines of the selected server's console.", [("lines", "integer", "How many lines (max 400)")]),
        Spec("list_files", "List files in a folder of the selected server (relative path, '' for the root).", [("path", "string", "Folder relative to the server root")]),
        Spec("read_file", "Read a text file of the selected server (relative path).", [("path", "string", "File relative to the server root")], ["path"]),
        Spec("write_file", "Create or replace a text file in the selected server. Requires user approval; a backup is kept. Use it to fix configs or to write translated message files (keep keys, placeholders like %player% or {0}, and & or § color codes unchanged).",
            [("path", "string", "File relative to the server root"), ("content", "string", "Full new content")], ["path", "content"]),
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

                    var text = await File.ReadAllTextAsync(file).ConfigureAwait(true);
                    return (text.Length > MaxRead ? text[..MaxRead] + "\n[truncated]" : text, null);
                }

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
                        if (File.Exists(full))
                        {
                            var backup = Path.Combine(Server.Definition.Directory, ".packcheck", "edits", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                            Directory.CreateDirectory(backup);
                            File.Copy(full, Path.Combine(backup, $"{DateTime.Now:HHmmss}-{Path.GetFileName(full)}"), overwrite: true);
                        }

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

    private PendingAction CreateServers(JsonArray list)
    {
        var plan = list.OfType<JsonObject>().Select(o => (
            Name: o["name"]?.ToString() ?? "server",
            Platform: Enum.TryParse<ServerPlatform>(o["platform"]?.ToString(), true, out var p) ? p : ServerPlatform.Paper,
            Version: o["version"]?.ToString() ?? "latest",
            Memory: int.TryParse(o["memory_mb"]?.ToString(), out var m) ? Math.Clamp(m, 512, 65536) : 4096,
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
