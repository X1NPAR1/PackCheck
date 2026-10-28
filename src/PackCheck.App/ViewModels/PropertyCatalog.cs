namespace PackCheck.App.ViewModels;

public enum PropertyKind
{
    Bool,
    Number,
    Choice,
    Text,
}

/// <summary>A known server.properties key: its type, default (vanilla) and group. Descriptions live in the RESX files as Prop_&lt;key&gt;.</summary>
public sealed record PropertyInfo(string Key, PropertyKind Kind, string Default, string Group, string[]? Options = null, int Min = 0, int Max = int.MaxValue, bool Secret = false);

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

    public static PropertyInfo? Find(string key) => All.FirstOrDefault(p => p.Key == key);
}
