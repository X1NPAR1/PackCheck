namespace PackCheck.App.ViewModels;

public enum PropertyKind
{
    Bool,
    Number,
    Choice,
    Text,
}

/// <summary>A known server.properties key: its type, default (vanilla) and group. Descriptions live in the RESX files as Prop_&lt;key&gt;.</summary>
public sealed record PropertyInfo(string Key, PropertyKind Kind, string Default, string Group, string[]? Options = null, int Min = 0, int Max = int.MaxValue, bool Secret = false, string? File = null, string? Path = null)
{
    public bool IsYaml => File is not null;
}

public static class PropertyCatalog
{
    public static readonly string[] Groups = ["General", "World", "Players", "Network", "Performance", "Security"];

    public static readonly IReadOnlyList<PropertyInfo> All =
    [
        // General
        new("motd", PropertyKind.Text, "A Minecraft Server", "General"),
        new("gamemode", PropertyKind.Choice, "survival", "General", ["survival", "creative", "adventure", "spectator"]),
        new("force-gamemode", PropertyKind.Bool, "false", "General"),
        new("difficulty", PropertyKind.Choice, "easy", "General", ["peaceful", "easy", "normal", "hard"]),
        new("hardcore", PropertyKind.Bool, "false", "General"),
        new("pvp", PropertyKind.Bool, "true", "General"),
        new("enable-command-block", PropertyKind.Bool, "false", "General"),
        new("allow-flight", PropertyKind.Bool, "false", "General"),
        new("spawn-protection", PropertyKind.Number, "16", "General", Max: 10000),

        // World
        new("level-name", PropertyKind.Text, "world", "World"),
        new("level-seed", PropertyKind.Text, string.Empty, "World"),
        new("level-type", PropertyKind.Choice, "minecraft:normal", "World", ["minecraft:normal", "minecraft:flat", "minecraft:large_biomes", "minecraft:amplified", "minecraft:single_biome_surface"]),
        new("generate-structures", PropertyKind.Bool, "true", "World"),
        new("allow-nether", PropertyKind.Bool, "true", "World"),
        new("max-world-size", PropertyKind.Number, "29999984", "World", Min: 1, Max: 29999984),
        new("generator-settings", PropertyKind.Text, "{}", "World"),

        // Players
        new("max-players", PropertyKind.Number, "20", "Players", Max: 100000),
        new("white-list", PropertyKind.Bool, "false", "Players"),
        new("enforce-whitelist", PropertyKind.Bool, "false", "Players"),
        new("op-permission-level", PropertyKind.Choice, "4", "Players", ["1", "2", "3", "4"]),
        new("function-permission-level", PropertyKind.Choice, "2", "Players", ["1", "2", "3", "4"]),
        new("player-idle-timeout", PropertyKind.Number, "0", "Players", Max: 100000),
        new("hide-online-players", PropertyKind.Bool, "false", "Players"),
        new("broadcast-console-to-ops", PropertyKind.Bool, "true", "Players"),

        // Network
        new("server-port", PropertyKind.Number, "25565", "Network", Min: 1, Max: 65535),
        new("server-ip", PropertyKind.Text, string.Empty, "Network"),
        new("online-mode", PropertyKind.Bool, "true", "Network"),
        new("enable-status", PropertyKind.Bool, "true", "Network"),
        new("enable-query", PropertyKind.Bool, "false", "Network"),
        new("query.port", PropertyKind.Number, "25565", "Network", Min: 1, Max: 65535),
        new("network-compression-threshold", PropertyKind.Number, "256", "Network", Min: -1, Max: 65535),
        new("rate-limit", PropertyKind.Number, "0", "Network", Max: 100000),
        new("accepts-transfers", PropertyKind.Bool, "false", "Network"),
        new("resource-pack", PropertyKind.Text, string.Empty, "Network"),
        new("require-resource-pack", PropertyKind.Bool, "false", "Network"),

        // Performance
        new("view-distance", PropertyKind.Number, "10", "Performance", Min: 2, Max: 32),
        new("simulation-distance", PropertyKind.Number, "10", "Performance", Min: 2, Max: 32),
        new("entity-broadcast-range-percentage", PropertyKind.Number, "100", "Performance", Min: 10, Max: 1000),
        new("max-tick-time", PropertyKind.Number, "60000", "Performance", Min: -1),
        new("sync-chunk-writes", PropertyKind.Bool, "true", "Performance"),
        new("use-native-transport", PropertyKind.Bool, "true", "Performance"),
        new("max-chained-neighbor-updates", PropertyKind.Number, "1000000", "Performance"),

        // Security
        new("enforce-secure-profile", PropertyKind.Bool, "true", "Security"),
        new("prevent-proxy-connections", PropertyKind.Bool, "false", "Security"),
        new("log-ips", PropertyKind.Bool, "true", "Security"),
        new("enable-rcon", PropertyKind.Bool, "false", "Security"),
        new("rcon.port", PropertyKind.Number, "25575", "Security", Min: 1, Max: 65535),
        new("rcon.password", PropertyKind.Text, string.Empty, "Security", Secret: true),
        new("broadcast-rcon-to-ops", PropertyKind.Bool, "true", "Security"),
    ];

    /// <summary>Groups for Bukkit/Spigot/Paper YAML settings; shown only when the file has the key.</summary>
    public static readonly string[] YamlGroups = ["Bukkit", "Spigot", "Paper"];

    /// <summary>Common performance settings in Bukkit/Spigot/Paper YAML files. Key is the RESX-safe id (Prop_&lt;key&gt;).</summary>
    public static readonly IReadOnlyList<PropertyInfo> Yaml =
    [
        Y("bukkit-monsters", "70", "bukkit.yml", "spawn-limits.monsters", "Bukkit", 0, 500),
        Y("bukkit-animals", "10", "bukkit.yml", "spawn-limits.animals", "Bukkit", 0, 500),
        Y("bukkit-water-animals", "5", "bukkit.yml", "spawn-limits.water-animals", "Bukkit", 0, 500),
        Y("bukkit-ambient", "15", "bukkit.yml", "spawn-limits.ambient", "Bukkit", 0, 500),
        Y("bukkit-monster-spawns", "1", "bukkit.yml", "ticks-per.monster-spawns", "Bukkit", 0, 6000),
        Y("bukkit-animal-spawns", "400", "bukkit.yml", "ticks-per.animal-spawns", "Bukkit", 0, 6000),
        Y("bukkit-connection-throttle", "4000", "bukkit.yml", "settings.connection-throttle", "Bukkit", -1, 60000),
        new("bukkit-allow-end", PropertyKind.Bool, "true", "Bukkit", File: "bukkit.yml", Path: "settings.allow-end"),

        Y("spigot-ear-animals", "32", "spigot.yml", "world-settings.default.entity-activation-range.animals", "Spigot", 0, 256),
        Y("spigot-ear-monsters", "32", "spigot.yml", "world-settings.default.entity-activation-range.monsters", "Spigot", 0, 256),
        Y("spigot-ear-raiders", "64", "spigot.yml", "world-settings.default.entity-activation-range.raiders", "Spigot", 0, 256),
        Y("spigot-ear-misc", "16", "spigot.yml", "world-settings.default.entity-activation-range.misc", "Spigot", 0, 256),
        Y("spigot-etr-players", "128", "spigot.yml", "world-settings.default.entity-tracking-range.players", "Spigot", 0, 512),
        Y("spigot-etr-animals", "96", "spigot.yml", "world-settings.default.entity-tracking-range.animals", "Spigot", 0, 512),
        Y("spigot-etr-monsters", "96", "spigot.yml", "world-settings.default.entity-tracking-range.monsters", "Spigot", 0, 512),
        Y("spigot-timeout", "60", "spigot.yml", "settings.timeout-time", "Spigot", 10, 3600),
        new("spigot-restart-on-crash", PropertyKind.Bool, "true", "Spigot", File: "spigot.yml", Path: "settings.restart-on-crash"),
        new("spigot-bungeecord", PropertyKind.Bool, "false", "Spigot", File: "spigot.yml", Path: "settings.bungeecord"),

        Y("paper-autosave-chunks", "24", "config/paper-world-defaults.yml", "chunks.max-auto-save-chunks-per-tick", "Paper", 1, 1000),
        Y("paper-entity-collisions", "8", "config/paper-world-defaults.yml", "collisions.max-entity-collisions", "Paper", 0, 100),
        Y("paper-spawner-tick", "1", "config/paper-world-defaults.yml", "tick-rates.mob-spawner", "Paper", 1, 100),
        new("paper-optimize-explosions", PropertyKind.Bool, "false", "Paper", File: "config/paper-world-defaults.yml", Path: "environment.optimize-explosions"),
        new("paper-armor-stands-tick", PropertyKind.Bool, "true", "Paper", File: "config/paper-world-defaults.yml", Path: "entities.armor-stands.tick"),
        new("paper-hopper-move-event", PropertyKind.Bool, "false", "Paper", File: "config/paper-world-defaults.yml", Path: "hopper.disable-move-event"),
        new("paper-prevent-unloaded", PropertyKind.Bool, "false", "Paper", File: "config/paper-world-defaults.yml", Path: "chunks.prevent-moving-into-unloaded-chunks"),
        Y("paper-max-joins", "5", "config/paper-global.yml", "misc.max-joins-per-tick", "Paper", 1, 100),
    ];

    private static PropertyInfo Y(string key, string def, string file, string path, string group, int min, int max) =>
        new(key, PropertyKind.Number, def, group, Min: min, Max: max, File: file, Path: path);

    public static PropertyInfo? Find(string key) => All.FirstOrDefault(p => p.Key == key) ?? Yaml.FirstOrDefault(p => p.Key == key);
}
