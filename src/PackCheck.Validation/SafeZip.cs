using System.IO.Compression;

namespace PackCheck.Validation;

public sealed class UnsafeArchiveException(string message) : Exception(message);

/// <summary>Extracts archives with path-traversal, entry-count and zip-bomb protection.</summary>
public static class SafeZip
{
    public const long MaxTotalBytes = 4L * 1024 * 1024 * 1024;
    public const int MaxEntries = 50_000;
    public const double MaxCompressionRatio = 200;

    public static void Extract(string zipPath, string destination, long maxTotalBytes = MaxTotalBytes)
    {
        var root = Path.GetFullPath(destination);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }

        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > MaxEntries)
        {
            throw new UnsafeArchiveException($"Archive has {zip.Entries.Count} entries (limit {MaxEntries}).");
        }

        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnsafeArchiveException($"Entry '{entry.FullName}' escapes the extraction directory.");
            }

            total += entry.Length;
            if (total > maxTotalBytes)
            {
                throw new UnsafeArchiveException("Archive expands beyond the allowed size.");
            }

            if (entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > MaxCompressionRatio && entry.Length > 10 * 1024 * 1024)
            {
                throw new UnsafeArchiveException($"Entry '{entry.FullName}' has a suspicious compression ratio.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
    }
}
