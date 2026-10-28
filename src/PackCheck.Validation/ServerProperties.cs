namespace PackCheck.Validation;

/// <summary>Minimal Java .properties reader/writer that keeps comments and order on write.</summary>
public static class ServerProperties
{
    public static Dictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] is '#' or '!')
            {
                continue;
            }

            var idx = line.IndexOfAny(['=', ':']);
            if (idx <= 0)
            {
                continue;
            }

            result[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }

        return result;
    }

    public static string Update(string original, IReadOnlyDictionary<string, string> values)
    {
        var pending = new Dictionary<string, string>(values);
        var lines = original.Replace("\r\n", "\n").Split('\n').ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] is '#' or '!')
            {
                continue;
            }

            var idx = line.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

            var key = line[..idx].Trim();
            if (pending.Remove(key, out var v))
            {
                lines[i] = $"{key}={v}";
            }
        }

        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        lines.AddRange(pending.Select(kv => $"{kv.Key}={kv.Value}"));
        return string.Join('\n', lines) + "\n";
    }
}
