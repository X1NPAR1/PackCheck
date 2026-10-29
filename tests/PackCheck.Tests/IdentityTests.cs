using PackCheck.Servers;
using PackCheck.Validation;
using Xunit;

namespace PackCheck.Tests;

public class IdentityTests
{
    [Fact]
    public void Motd_colors_and_styles_are_parsed_per_line()
    {
        var lines = Motd.Parse("§aHello §l§cWorld\n&ebye");
        Assert.Equal(2, lines.Count);
        Assert.Equal("Hello ", lines[0][0].Text);
        Assert.Equal("#55FF55", lines[0][0].Color);
        Assert.False(lines[0][0].Bold);
        // a color code after a style resets it
        Assert.Equal("World", lines[0][1].Text);
        Assert.Equal("#FF5555", lines[0][1].Color);
        Assert.False(lines[0][1].Bold);
        Assert.Equal("#FFFF55", lines[1][0].Color);
    }

    [Fact]
    public void Motd_style_after_color_is_kept()
    {
        var span = Assert.Single(Motd.Parse("§c§lBig")[0]);
        Assert.True(span.Bold);
        Assert.Equal("#FF5555", span.Color);
    }

    [Fact]
    public void Motd_normalize_and_plain()
    {
        Assert.Equal("§aHi & you", Motd.Normalize("&aHi & you"));
        Assert.Equal("Hi & you", Motd.Plain("&aHi & you"));
    }

    [Fact]
    public void Properties_escape_round_trips_section_sign_and_newline()
    {
        const string motd = "§aTürk sunucu\n§7ikinci satır";
        var escaped = PropertiesText.Escape(motd);
        Assert.DoesNotContain('§', escaped);
        Assert.Contains("\\u00a7", escaped);
        Assert.Contains(@"\n", escaped);
        Assert.Equal(motd, PropertiesText.Unescape(escaped));

        var file = ServerProperties.Update("motd=old\nmax-players=20\n", new Dictionary<string, string> { ["motd"] = escaped });
        Assert.Equal(motd, PropertiesText.Unescape(ServerProperties.Parse(file)["motd"]));
        Assert.Equal("20", ServerProperties.Parse(file)["max-players"]);
    }

    [Fact]
    public void Properties_file_is_read_as_utf8_or_latin1()
    {
        var dir = Directory.CreateTempSubdirectory("pc-props").FullName;
        var utf8 = Path.Combine(dir, "a.properties");
        var latin = Path.Combine(dir, "b.properties");
        File.WriteAllText(utf8, "motd=§aTürk\n", new System.Text.UTF8Encoding(false));
        File.WriteAllText(latin, "motd=§aTürk\n", System.Text.Encoding.Latin1);
        Assert.Equal("motd=§aTürk\n", PropertiesText.ReadFile(utf8));
        Assert.Equal("motd=§aTürk\n", PropertiesText.ReadFile(latin));
    }

    [Fact]
    public void Icon_validation_checks_png_size()
    {
        static byte[] Png(int w, int h)
        {
            var b = new byte[24];
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16), w);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), h);
            return b;
        }

        Assert.True(ServerIcon.IsValid(Png(64, 64)));
        Assert.False(ServerIcon.IsValid(Png(128, 128)));
        Assert.False(ServerIcon.IsValid([1, 2, 3]));
    }
}
