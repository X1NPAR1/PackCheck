using System.Text;

namespace PackCheck.Servers;

/// <summary>One styled piece of a MOTD line.</summary>
public sealed record MotdSpan(string Text, string Color, bool Bold, bool Italic, bool Underline, bool Strike, bool Obfuscated);

/// <summary>Minecraft legacy formatting codes (§0-§f, §k-§o, §r) used by the server list MOTD.</summary>
public static class Motd
{
    public const char Section = '§';
    public const int MaxLength = 59 * 2;

    /// <summary>Code → hex color, in the order the game lists them.</summary>
    public static readonly IReadOnlyList<(char Code, string Hex)> Colors =
    [
        ('0', "#000000"), ('1', "#0000AA"), ('2', "#00AA00"), ('3', "#00AAAA"),
        ('4', "#AA0000"), ('5', "#AA00AA"), ('6', "#FFAA00"), ('7', "#AAAAAA"),
        ('8', "#555555"), ('9', "#5555FF"), ('a', "#55FF55"), ('b', "#55FFFF"),
        ('c', "#FF5555"), ('d', "#FF55FF"), ('e', "#FFFF55"), ('f', "#FFFFFF"),
    ];

    public const string DefaultColor = "#AAAAAA";

    /// <summary>Splits a MOTD (with "\n" between the two lines) into lines of styled spans.</summary>
    public static IReadOnlyList<IReadOnlyList<MotdSpan>> Parse(string motd)
    {
        var lines = new List<IReadOnlyList<MotdSpan>>();
        foreach (var line in motd.Replace("\r", string.Empty).Split('\n').Take(2))
        {
            lines.Add(ParseLine(line));
        }

        return lines;
    }

    private static List<MotdSpan> ParseLine(string line)
    {
        var spans = new List<MotdSpan>();
        var sb = new StringBuilder();
        var color = DefaultColor;
        bool bold = false, italic = false, under = false, strike = false, obf = false;

        void Flush()
        {
            if (sb.Length > 0)
            {
                spans.Add(new MotdSpan(sb.ToString(), color, bold, italic, under, strike, obf));
                sb.Clear();
            }
        }

        for (var i = 0; i < line.Length; i++)
        {
            if ((line[i] == Section || line[i] == '&') && i + 1 < line.Length && IsCode(line[i + 1]))
            {
                Flush();
                var c = char.ToLowerInvariant(line[++i]);
                var hex = Colors.FirstOrDefault(x => x.Code == c).Hex;
                if (hex is not null)
                {
                    // A color code resets the styles, as in the game.
                    color = hex;
                    bold = italic = under = strike = obf = false;
                }
                else
                {
                    switch (c)
                    {
                        case 'k': obf = true; break;
                        case 'l': bold = true; break;
                        case 'm': strike = true; break;
                        case 'n': under = true; break;
                        case 'o': italic = true; break;
                        case 'r': color = DefaultColor; bold = italic = under = strike = obf = false; break;
                    }
                }

                continue;
            }

            sb.Append(line[i]);
        }

        Flush();
        return spans;
    }

    private static bool IsCode(char c) => "0123456789abcdefklmnorABCDEFKLMNOR".Contains(c);

    /// <summary>Converts '&amp;' codes typed by the user into real section signs.</summary>
    public static string Normalize(string motd)
    {
        var sb = new StringBuilder(motd.Length);
        for (var i = 0; i < motd.Length; i++)
        {
            sb.Append(motd[i] == '&' && i + 1 < motd.Length && IsCode(motd[i + 1]) ? Section : motd[i]);
        }

        return sb.ToString();
    }

    /// <summary>Text without formatting codes, for length checks and plain display.</summary>
    public static string Plain(string motd) => string.Concat(Parse(motd).Select(l => string.Concat(l.Select(s => s.Text))));
}

/// <summary>Java .properties escaping for values (server.properties is read as ISO-8859-1).</summary>
public static class PropertiesText
{
    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': break;
                case '\t': sb.Append(@"\t"); break;
                default:
                    if (c < 0x20 || c > 0x7E)
                    {
                        sb.Append(@"\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Newer servers write server.properties as UTF-8, older ones as ISO-8859-1. Strict UTF-8 first, Latin-1 if the bytes are not valid UTF-8.
    /// </summary>
    public static string ReadFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    public static string Unescape(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                sb.Append(value[i]);
                continue;
            }

            var n = value[++i];
            switch (n)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': break;
                case 'u' when i + 4 < value.Length && int.TryParse(value.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code):
                    sb.Append((char)code);
                    i += 4;
                    break;
                default: sb.Append(n); break;
            }
        }

        return sb.ToString();
    }
}

/// <summary>Location of the 64×64 server-icon.png the game shows in the server list.</summary>
public static class ServerIcon
{
    public const string FileName = "server-icon.png";
    public const int Size = 64;

    public static string PathFor(string serverDirectory) => System.IO.Path.Combine(serverDirectory, FileName);

    /// <summary>True when the bytes are a PNG whose IHDR says 64×64 (what the game requires).</summary>
    public static bool IsValid(byte[] png)
    {
        ReadOnlySpan<byte> sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(sig))
        {
            return false;
        }

        var w = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        var h = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        return w == Size && h == Size;
    }
}
