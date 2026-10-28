using System.Diagnostics;

// Usage: PackCheck.UpdateHelper <pid> <stagingDir> <installDir> <exeToRestart>
// Waits for the app to exit, replaces the install folder contents and restarts the app.
if (args.Length != 4 || !int.TryParse(args[0], out var pid))
{
    return 64;
}

var (staging, installDir, restart) = (args[1], args[2], args[3]);
var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackCheck", "logs", "update.log");
Directory.CreateDirectory(Path.GetDirectoryName(log)!);
void Log(string m) => File.AppendAllText(log, $"{DateTime.Now:u} {m}{Environment.NewLine}");

try
{
    try
    {
        using var p = Process.GetProcessById(pid);
        if (!p.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            Log("App did not exit in time; update aborted.");
            return 1;
        }
    }
    catch (ArgumentException)
    {
        // already exited
    }

    var backup = installDir + ".old";
    if (Directory.Exists(backup))
    {
        Directory.Delete(backup, true);
    }

    // Copy new files over the old ones with retries (antivirus / indexers may hold handles briefly).
    foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(staging, file);
        var dest = Path.Combine(installDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(file, dest, overwrite: true);
                break;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(500);
            }
        }
    }

    Log($"Installed update from {staging}");
    Process.Start(new ProcessStartInfo(restart) { UseShellExecute = false, WorkingDirectory = installDir });
    return 0;
}
catch (Exception ex)
{
    Log("Update failed: " + ex);
    if (File.Exists(restart))
    {
        Process.Start(new ProcessStartInfo(restart) { UseShellExecute = false, WorkingDirectory = installDir });
    }

    return 1;
}
