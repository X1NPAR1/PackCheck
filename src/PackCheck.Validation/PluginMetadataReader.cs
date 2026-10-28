using System.IO.Compression;
using PackCheck.Core;
using YamlDotNet.RepresentationModel;

namespace PackCheck.Validation;

/// <summary>Reads plugin.yml / paper-plugin.yml from a JAR without executing anything.</summary>
public static class PluginMetadataReader
{
    private const long MaxDescriptorBytes = 512 * 1024;

    public static async Task<PluginInfo> ReadAsync(string jarPath, CancellationToken ct)
    {
        var info = new FileInfo(jarPath);
        var sha = await Hashing.Sha256FileAsync(jarPath, ct).ConfigureAwait(false);
        var basic = new PluginInfo { FileName = info.Name, Sha256 = sha, Size = info.Length };

        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            foreach (var name in new[] { "paper-plugin.yml", "plugin.yml" })
            {
                var entry = zip.GetEntry(name);
                if (entry is null)
                {
                    continue;
                }

                if (entry.Length > MaxDescriptorBytes)
                {
                    return basic with { MetadataSource = name + " (too large)" };
                }

                using var reader = new StreamReader(entry.Open());
                var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                return Parse(text, name, basic);
            }

            return basic;
        }
        catch (InvalidDataException)
        {
            return basic with { IsCorrupt = true };
        }
        catch (IOException)
        {
            return basic with { IsCorrupt = true };
        }
    }

    /// <summary>Parses YAML via the representation model only (no object construction from tags).</summary>
    public static PluginInfo Parse(string yaml, string source, PluginInfo basic)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return basic with { MetadataSource = source + " (invalid YAML)" };
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            return basic with { MetadataSource = source + " (empty)" };
        }

        string? Scalar(string key) =>
            root.Children.TryGetValue(new YamlScalarNode(key), out var n) && n is YamlScalarNode s ? s.Value : null;

        var depends = new List<string>();
        var soft = new List<string>();
        AddList(root, "depend", depends);
        AddList(root, "softdepend", soft);

        // paper-plugin.yml: dependencies: { server: { Name: { required: true } } }
        if (root.Children.TryGetValue(new YamlScalarNode("dependencies"), out var deps) && deps is YamlMappingNode depMap)
        {
            var server = depMap.Children.TryGetValue(new YamlScalarNode("server"), out var s) ? s as YamlMappingNode : depMap;
            if (server is not null)
            {
                foreach (var (k, v) in server.Children)
                {
                    if (k is not YamlScalarNode key || key.Value is null)
                    {
                        continue;
                    }

                    var required = true;
                    if (v is YamlMappingNode opts && opts.Children.TryGetValue(new YamlScalarNode("required"), out var r) && r is YamlScalarNode rs)
                    {
                        required = !string.Equals(rs.Value, "false", StringComparison.OrdinalIgnoreCase);
                    }

                    (required ? depends : soft).Add(key.Value);
                }
            }
        }

        return basic with
        {
            Name = Scalar("name"),
            Version = Scalar("version"),
            ApiVersion = Scalar("api-version"),
            Main = Scalar("main"),
            MetadataSource = source,
            Depends = depends,
            SoftDepends = soft,
        };
    }

    private static void AddList(YamlMappingNode root, string key, List<string> into)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode(key), out var node))
        {
            return;
        }

        if (node is YamlSequenceNode seq)
        {
            into.AddRange(seq.Children.OfType<YamlScalarNode>().Select(x => x.Value).OfType<string>());
        }
        else if (node is YamlScalarNode one && !string.IsNullOrWhiteSpace(one.Value))
        {
            into.Add(one.Value);
        }
    }
}
