namespace PackCheck.Servers;

/// <summary>
/// Reads and rewrites scalar values in YAML config files (spigot.yml, bukkit.yml, paper-*.yml) by dotted path,
/// line by line, so comments, order and formatting stay exactly as they were.
/// </summary>
public static class YamlPatcher
{
    /// <summary>The scalar at <paramref name="path"/> (quotes removed), or null when missing or not a scalar.</summary>
    public static string? Get(string text, string path)
    {
        var hit = Find(SplitLines(text), path);
        return hit is null ? null : Unquote(hit.Value.Value);
    }

    /// <summary>Replaces the scalar at <paramref name="path"/>; returns null when the key is not in the file.</summary>
    public static string? Set(string text, string path, string value)
    {
        var lines = SplitLines(text);
        var hit = Find(lines, path);
        if (hit is null)
        {
            return null;
        }

        var (index, valueStart, oldValue, comment) = hit.Value;
        var line = lines[index];
        var quote = oldValue.Length >= 2 && oldValue[0] is '\'' or '"' && oldValue[^1] == oldValue[0] ? oldValue[0] : (char?)null;
        var written = quote is { } q ? q + value.Replace(q.ToString(), q == '\'' ? "''" : "\\\"") + q : NeedsQuotes(value) ? "'" + value.Replace("'", "''") + "'" : value;
        lines[index] = line[..valueStart] + written + comment;
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        return string.Join(newline, lines);
    }

    private static List<string> SplitLines(string text) => [.. text.Replace("\r\n", "\n").Split('\n')];

    private static (int Index, int ValueStart, string Value, string Comment)? Find(List<string> lines, string path)
    {
        var want = path.Split('.');
        var stack = new List<(int Indent, string Key)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-")
            {
                continue;
            }

            var indent = line.Length - trimmed.Length;
            var colon = KeyColon(trimmed);
            if (colon <= 0)
            {
                continue;
            }

            var key = Unquote(trimmed[..colon].Trim());
            while (stack.Count > 0 && stack[^1].Indent >= indent)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            stack.Add((indent, key));
            if (stack.Count != want.Length || !stack.Select(s => s.Key).SequenceEqual(want, StringComparer.Ordinal))
            {
                continue;
            }

            var after = trimmed[(colon + 1)..];
            var (value, comment) = SplitComment(after);
            if (value.Trim().Length == 0 || value.TrimStart()[0] is '|' or '>' or '[' or '{' or '&' or '*')
            {
                return null;
            }

            var lead = after.Length - after.TrimStart().Length;
            var valueStart = indent + colon + 1 + lead;
            return (i, valueStart, value.Trim(), comment);
        }

        return null;
    }

    /// <summary>Index of the ':' that ends the key (outside quotes and followed by space or end).</summary>
    private static int KeyColon(string s)
    {
        char? quote = null;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote is not null)
            {
                if (c == quote)
                {
                    quote = null;
                }

                continue;
            }

            if (c is '\'' or '"' && i == 0)
            {
                quote = c;
            }
            else if (c == ':' && (i + 1 == s.Length || s[i + 1] == ' '))
            {
                return i;
            }
            else if (c == '#')
            {
                return -1;
            }
        }

        return -1;
    }

    /// <summary>Splits " value # comment" keeping the comment (with its leading spaces) intact.</summary>
    private static (string Value, string Comment) SplitComment(string s)
    {
        char? quote = null;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote is not null)
            {
                if (c == quote)
                {
                    quote = null;
                }
            }
            else if (c is '\'' or '"' && s[..i].Trim().Length == 0)
            {
                quote = c;
            }
            else if (c == '#' && i > 0 && s[i - 1] == ' ')
            {
                var start = i;
                while (start > 0 && s[start - 1] == ' ')
                {
                    start--;
                }

                return (s[..start], s[start..]);
            }
        }

        return (s.TrimEnd(), string.Empty);
    }

    private static string Unquote(string v) =>
        v.Length >= 2 && v[0] == '\'' && v[^1] == '\'' ? v[1..^1].Replace("''", "'")
        : v.Length >= 2 && v[0] == '"' && v[^1] == '"' ? v[1..^1].Replace("\\\"", "\"")
        : v;

    private static bool NeedsQuotes(string v) =>
        v.Length == 0 || v.Trim() != v || v.IndexOfAny([':', '#', '\'', '"', '{', '}', '[', ']', ',', '&', '*', '!', '|', '>', '%', '@', '`']) >= 0;
}
