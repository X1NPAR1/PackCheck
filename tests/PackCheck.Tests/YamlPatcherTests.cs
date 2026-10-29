using PackCheck.Servers;
using Xunit;

namespace PackCheck.Tests;

public class YamlPatcherTests
{
    private const string Spigot = """
        # This is the main configuration file for Spigot.
        settings:
          timeout-time: 60 # watchdog
          restart-on-crash: true
          restart-script: ./start.sh
        world-settings:
          default:
            entity-activation-range:
              animals: 32
              monsters: 32
            entity-tracking-range:
              players: 48
          'my world':
            entity-activation-range:
              animals: 16
        messages:
          whitelist: 'You are not whitelisted: sorry'
        """;

    [Theory]
    [InlineData("settings.timeout-time", "60")]
    [InlineData("settings.restart-on-crash", "true")]
    [InlineData("world-settings.default.entity-activation-range.monsters", "32")]
    [InlineData("world-settings.my world.entity-activation-range.animals", "16")]
    [InlineData("messages.whitelist", "You are not whitelisted: sorry")]
    public void Reads_nested_scalars(string path, string expected) => Assert.Equal(expected, YamlPatcher.Get(Spigot, path));

    [Theory]
    [InlineData("settings")]
    [InlineData("world-settings.default.entity-activation-range")]
    [InlineData("settings.missing")]
    [InlineData("entity-activation-range.animals")]
    public void Missing_or_map_paths_return_null(string path) => Assert.Null(YamlPatcher.Get(Spigot, path));

    [Fact]
    public void Set_keeps_comments_indentation_and_other_lines()
    {
        var updated = YamlPatcher.Set(Spigot, "settings.timeout-time", "120")!;
        Assert.Contains("  timeout-time: 120 # watchdog", updated);
        Assert.Equal("120", YamlPatcher.Get(updated, "settings.timeout-time"));
        Assert.Equal(Spigot.Replace("timeout-time: 60", "timeout-time: 120"), updated);
    }

    [Fact]
    public void Set_only_touches_the_matching_world()
    {
        var updated = YamlPatcher.Set(Spigot, "world-settings.default.entity-activation-range.animals", "8")!;
        Assert.Equal("8", YamlPatcher.Get(updated, "world-settings.default.entity-activation-range.animals"));
        Assert.Equal("16", YamlPatcher.Get(updated, "world-settings.my world.entity-activation-range.animals"));
    }

    [Fact]
    public void Set_keeps_quote_style_and_quotes_when_needed()
    {
        var updated = YamlPatcher.Set(Spigot, "messages.whitelist", "Ask an admin")!;
        Assert.Contains("whitelist: 'Ask an admin'", updated);
        var script = YamlPatcher.Set(Spigot, "settings.restart-script", "C:/start.bat")!;
        Assert.Contains("restart-script: 'C:/start.bat'", script);
        Assert.Equal("C:/start.bat", YamlPatcher.Get(script, "settings.restart-script"));
    }

    [Fact]
    public void Set_returns_null_for_unknown_key_and_keeps_crlf()
    {
        Assert.Null(YamlPatcher.Set(Spigot, "settings.nope", "1"));
        var crlf = "a:\r\n  b: 1\r\n";
        Assert.Equal("a:\r\n  b: 2\r\n", YamlPatcher.Set(crlf, "a.b", "2"));
    }
}
