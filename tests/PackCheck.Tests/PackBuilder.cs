using System.IO.Compression;
using System.Text;

namespace PackCheck.Tests;

/// <summary>Builds throw-away server packs for tests.</summary>
public sealed class PackBuilder : IDisposable
{
    public PackBuilder()
    {
        Root = Path.Combine(Path.GetTempPath(), "pc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "plugins"));
    }

    public string Root { get; }

    public PackBuilder Plugin(string file, string yaml, string descriptor = "plugin.yml")
    {
        using var zip = ZipFile.Open(Path.Combine(Root, "plugins", file), ZipArchiveMode.Create);
        var e = zip.CreateEntry(descriptor);
        using var w = new StreamWriter(e.Open(), Encoding.UTF8);
        w.Write(yaml);
        return this;
    }

    public PackBuilder Simple(string file, string name, string version = "1.0", string? apiVersion = "1.20", string? depend = null) =>
        Plugin(file, $"name: {name}\nversion: '{version}'\nmain: x.{name}\n" + (apiVersion is null ? string.Empty : $"api-version: '{apiVersion}'\n") + (depend is null ? string.Empty : $"depend: [{depend}]\n"));

    public PackBuilder Raw(string relative, byte[] content)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return this;
    }

    public PackBuilder Text(string relative, string content) => Raw(relative, Encoding.UTF8.GetBytes(content));

    public string Zip()
    {
        var zip = Root + ".zip";
        ZipFile.CreateFromDirectory(Root, zip);
        return zip;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
            File.Delete(Root + ".zip");
        }
        catch (IOException)
        {
        }
    }
}
