using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PackCheck.Validation;

namespace PackCheck.Servers;

public sealed record ProxyBackend(string Name, string Directory, int Port, ServerPlatform Platform);

public sealed record ProxyIssue(string Severity, string Message);

/// <summary>
/// Writes a working proxy setup: the proxy's own config plus the matching settings on every backend
/// (offline mode behind the proxy, loopback binding, forwarding secret).
/// </summary>
public static class ProxyConfigurator
{
    public static bool IsProxy(ServerPlatform p) => p is ServerPlatform.Velocity or ServerPlatform.BungeeCord;

    public static string NewSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    /// <summary>Backends listen on 127.0.0.1 so players can only reach them through the proxy.</summary>
    public static void ConfigureBackend(ProxyBackend b, ServerPlatform proxy, string secret)
    {
        var props = Path.Combine(b.Directory, "server.properties");
        var original = File.Exists(props) ? File.ReadAllText(props) : string.Empty;
        File.WriteAllText(props, ServerProperties.Update(original, new Dictionary<string, string>
        {
            ["online-mode"] = "false",
            ["server-ip"] = "127.0.0.1",
            ["server-port"] = b.Port.ToString(CultureInfo.InvariantCulture),
            ["enforce-secure-profile"] = "false",
        }));

        if (proxy == ServerPlatform.Velocity && PlatformInfo.UsesPlugins(b.Platform))
        {
            // Paper reads proxies.velocity from config/paper-global.yml (missing keys are filled with defaults on start).
            var cfg = Path.Combine(b.Directory, "config", "paper-global.yml");
            Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
            var yaml = File.Exists(cfg) ? File.ReadAllText(cfg) : string.Empty;
            File.WriteAllText(cfg, SetVelocityBlock(yaml, secret));
        }
        else if (proxy == ServerPlatform.BungeeCord && PlatformInfo.UsesPlugins(b.Platform))
        {
            var spigot = Path.Combine(b.Directory, "spigot.yml");
            var yaml = File.Exists(spigot) ? File.ReadAllText(spigot) : string.Empty;
            File.WriteAllText(spigot, SetBungeeFlag(yaml));
        }
    }

    /// <summary>Replaces (or adds) the top-level "proxies:" section with an enabled Velocity block.</summary>
    public static string SetVelocityBlock(string yaml, string secret)
    {
        var block = $"proxies:\n  bungee-cord:\n    online-mode: true\n  proxy-protocol: false\n  velocity:\n    enabled: true\n    online-mode: true\n    secret: '{secret}'\n";
        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
        var start = lines.FindIndex(l => l.StartsWith("proxies:", StringComparison.Ordinal));
        if (start < 0)
        {
            return yaml.TrimEnd() + (yaml.Length > 0 ? "\n" : string.Empty) + block;
        }

        var end = start + 1;
        while (end < lines.Count && (lines[end].StartsWith(' ') || lines[end].Length == 0))
        {
            end++;
        }

        lines.RemoveRange(start, end - start);
        lines.Insert(start, block.TrimEnd('\n'));
        return string.Join('\n', lines);
    }

    public static string SetBungeeFlag(string yaml)
    {
        if (yaml.Contains("bungeecord:", StringComparison.Ordinal))
        {
            return System.Text.RegularExpressions.Regex.Replace(yaml, @"(?m)^(\s*)bungeecord:\s*\S+", "$1bungeecord: true");
        }

        return yaml.TrimEnd() + (yaml.Length > 0 ? "\n" : string.Empty) + "settings:\n  bungeecord: true\n";
    }

    public static string VelocityToml(int port, string motd, IReadOnlyList<ProxyBackend> backends, IReadOnlyList<string> tryOrder) =>
        $"""
        # Written by PackCheck. Velocity adds any missing keys on start.
        config-version = "2.7"
        bind = "0.0.0.0:{port}"
        motd = "<#7CC24F>{motd.Replace("\"", "'", StringComparison.Ordinal)}"
        show-max-players = 500
        online-mode = true
        force-key-authentication = true
        prevent-client-proxy-connections = false
        player-info-forwarding-mode = "modern"
        forwarding-secret-file = "forwarding.secret"
        announce-forge = false
        kick-existing-players = false
        ping-passthrough = "DISABLED"

        [servers]
        {string.Join("\n", backends.Select(b => $"{Key(b.Name)} = \"127.0.0.1:{b.Port}\""))}
        try = [{string.Join(", ", tryOrder.Select(n => $"\"{Key(n)}\""))}]

        [forced-hosts]

        [advanced]
        compression-threshold = 256
        compression-level = -1
        login-ratelimit = 3000
        connection-timeout = 5000
        read-timeout = 30000

        [query]
        enabled = false
        port = {port}
        """;

    public static string BungeeYaml(int port, string motd, IReadOnlyList<ProxyBackend> backends, IReadOnlyList<string> priorities)
    {
        var sb = new StringBuilder();
        sb.Append("# Written by PackCheck. BungeeCord adds any missing keys on start.\n");
        sb.Append("ip_forward: true\nonline_mode: true\nprevent_proxy_connections: false\n");
        sb.Append("servers:\n");
        foreach (var b in backends)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {Key(b.Name)}:\n    motd: '{b.Name.Replace("'", "''", StringComparison.Ordinal)}'\n    address: 127.0.0.1:{b.Port}\n    restricted: false\n");
        }

        sb.Append("listeners:\n- query_port: ").Append(port).Append("\n  motd: '").Append(motd.Replace("'", "''", StringComparison.Ordinal)).Append("'\n");
        sb.Append("  tab_list: GLOBAL_PING\n  query_enabled: false\n  proxy_protocol: false\n  forced_hosts: {}\n  ping_passthrough: false\n");
        sb.Append("  priorities:\n");
        foreach (var p in priorities)
        {
            sb.Append("  - ").Append(Key(p)).Append('\n');
        }

        sb.Append("  bind_local_address: true\n  host: 0.0.0.0:").Append(port).Append("\n  max_players: 500\n  tab_size: 60\n  force_default_server: false\n");
        return sb.ToString();
    }

    /// <summary>Config keys must be simple identifiers.</summary>
    public static string Key(string name)
    {
        var k = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return k.Length == 0 ? "server" : k;
    }

    /// <summary>Writes the proxy config, the forwarding secret and every backend's settings. Returns the secret.</summary>
    public static string Apply(string proxyDir, ServerPlatform proxy, int port, string motd, IReadOnlyList<ProxyBackend> backends, string defaultBackend)
    {
        var order = backends.Select(b => b.Name).OrderBy(n => n == defaultBackend ? 0 : 1).ToList();
        var secretFile = Path.Combine(proxyDir, "forwarding.secret");
        var secret = File.Exists(secretFile) ? File.ReadAllText(secretFile).Trim() : NewSecret();
        if (proxy == ServerPlatform.Velocity)
        {
            File.WriteAllText(secretFile, secret);
            File.WriteAllText(Path.Combine(proxyDir, "velocity.toml"), VelocityToml(port, motd, backends, order));
        }
        else
        {
            File.WriteAllText(Path.Combine(proxyDir, "config.yml"), BungeeYaml(port, motd, backends, order));
        }

        foreach (var b in backends)
        {
            ConfigureBackend(b, proxy, secret);
        }

        File.WriteAllText(Path.Combine(proxyDir, "packcheck-proxy.json"), JsonSerializer.Serialize(new { backends = backends.Select(b => b.Name), defaultBackend }));
        return secret;
    }

    /// <summary>Checks that a proxy setup is consistent and safe.</summary>
    public static IReadOnlyList<ProxyIssue> Check(ServerPlatform proxy, int proxyPort, IReadOnlyList<ProxyBackend> backends, string? secret)
    {
        var issues = new List<ProxyIssue>();
        foreach (var dup in backends.GroupBy(b => b.Port).Where(g => g.Count() > 1))
        {
            issues.Add(new("error", $"Port {dup.Key} is used by {string.Join(", ", dup.Select(b => b.Name))}."));
        }

        if (backends.Any(b => b.Port == proxyPort))
        {
            issues.Add(new("error", $"A backend uses the proxy port {proxyPort}."));
        }

        foreach (var b in backends)
        {
            var file = Path.Combine(b.Directory, "server.properties");
            var props = File.Exists(file) ? ServerProperties.Parse(File.ReadAllText(file)) : [];
            if (!props.TryGetValue("online-mode", out var om) || om != "false")
            {
                issues.Add(new("error", $"{b.Name}: online-mode must be false behind a proxy."));
            }

            if (!props.TryGetValue("server-ip", out var ip) || ip != "127.0.0.1")
            {
                issues.Add(new("warn", $"{b.Name}: not bound to 127.0.0.1, so players could bypass the proxy."));
            }

            if (proxy == ServerPlatform.Velocity && PlatformInfo.UsesPlugins(b.Platform))
            {
                var cfg = Path.Combine(b.Directory, "config", "paper-global.yml");
                var yaml = File.Exists(cfg) ? File.ReadAllText(cfg) : string.Empty;
                if (secret is not null && !yaml.Contains(secret, StringComparison.Ordinal))
                {
                    issues.Add(new("error", $"{b.Name}: the Velocity secret in paper-global.yml does not match the proxy."));
                }
            }

            if (PlatformInfo.UsesMods(b.Platform) && !PlatformInfo.UsesPlugins(b.Platform))
            {
                var mods = Path.Combine(b.Directory, "mods");
                var hasForwarding = Directory.Exists(mods) && Directory.EnumerateFiles(mods, "*.jar")
                    .Any(f => Path.GetFileName(f).Contains("proxy", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Contains("crossstitch", StringComparison.OrdinalIgnoreCase));
                if (!hasForwarding)
                {
                    issues.Add(new("warn", $"{b.Name}: a {b.Platform} backend needs a forwarding mod (FabricProxy-Lite or Proxy-Compatible-Forge) so player data reaches it."));
                }
            }
        }

        return issues;
    }
}
