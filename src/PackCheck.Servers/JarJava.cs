using System.IO.Compression;

namespace PackCheck.Servers;

/// <summary>Reads which Java a server jar was compiled for (class file major version - 44 = Java feature version).</summary>
public static class JarJava
{
    public static int? RequiredFeature(string jarPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            var manifest = zip.GetEntry("META-INF/MANIFEST.MF");
            if (manifest is null)
            {
                return null;
            }

            string? main;
            using (var reader = new StreamReader(manifest.Open()))
            {
                // Bundler jars (Paper, Velocity) launch through a small main class compiled for the same Java as the server.
                main = reader.ReadToEnd().Split('\n').Select(l => l.Trim())
                    .FirstOrDefault(l => l.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase))?[11..].Trim();
            }

            if (string.IsNullOrEmpty(main) || zip.GetEntry(main.Replace('.', '/') + ".class") is not { } cls)
            {
                return null;
            }

            using var s = cls.Open();
            var header = new byte[8];
            if (s.ReadAtLeast(header, 8, throwOnEndOfStream: false) < 8 || header[0] != 0xCA || header[1] != 0xFE)
            {
                return null;
            }

            var major = (header[6] << 8) | header[7];
            return major >= 52 ? major - 44 : 8;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
