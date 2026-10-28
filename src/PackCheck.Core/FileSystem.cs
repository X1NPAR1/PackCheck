namespace PackCheck.Core;

public static class FileSystem
{
    /// <summary>
    /// Deletes a folder even when it contains read-only files (git objects left by BuildTools are read-only).
    /// Retries briefly because antivirus scanners and indexers can hold files for a moment.
    /// </summary>
    public static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 10)
            {
                Thread.Sleep(500);
            }
        }
    }
}
