using System.Diagnostics;

// Usage:
//   PackCheck.UpdateHelper <pid> <stagingDir> <installDir> <exeToRestart>                 (portable zip; also the v1.x call)
//   PackCheck.UpdateHelper <pid> exe|msi <installerPath> <installDir> <exeToRestart>      (setup packages)
// Waits for the app to exit, installs the update and restarts the app.
var legacy = args.Length == 4;
if ((!legacy && args.Length != 5) || !int.TryParse(args[0], out var pid))
{
    return 64;
}

var mode = legacy ? "zip" : args[1];
var payload = legacy ? args[1] : args[2];
var installDir = legacy ? args[2] : args[3];
var restart = legacy ? args[3] : args[4];

var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackCheck", "logs", "update.log");
Directory.CreateDirectory(Path.GetDirectoryName(log)!);
void Log(string m) => File.AppendAllText(log, $"{DateTime.Now:u} {m}{Environment.NewLine}");

void Restart()
{
    if (File.Exists(restart))
    {
        Process.Start(new ProcessStartInfo(restart) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(restart)! });
    }
}

try
{
    try
    {
        // Servers are already stopped by the app; give it time to close, then end it so the update never hangs.
        using var p = Process.GetProcessById(pid);
        if (!p.WaitForExit(TimeSpan.FromMinutes(3)))
        {
            Log("App did not exit within 3 minutes; ending it and continuing the update.");
            p.Kill(entireProcessTree: false);
            p.WaitForExit(TimeSpan.FromSeconds(20));
        }
    }
    catch (ArgumentException)
    {
        // already exited
    }

    switch (mode)
    {
        case "zip":
            CopyOver(payload, installDir);
            break;
        case "exe":
            RunInstaller(payload, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/SP-"]);
            break;
        case "msi":
            RunInstaller(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), ["/i", payload, "/qn", "/norestart"]);
            break;
        default:
            Log("Unknown mode " + mode);
            return 64;
    }

    Log($"Installed update ({mode}) from {payload}");
    Restart();
    return 0;
}
catch (Exception ex)
{
    Log("Update failed: " + ex);
    Restart();
    return 1;
}

void CopyOver(string staging, string target)
{
    // Copy new files over the old ones with retries (antivirus / indexers may hold handles briefly).
    foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
    {
        var dest = Path.Combine(target, Path.GetRelativePath(staging, file));
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
}

void RunInstaller(string file, string[] arguments)
{
    var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var a in arguments)
    {
        psi.ArgumentList.Add(a);
    }

    using var p = Process.Start(psi)!;
    if (!p.WaitForExit(TimeSpan.FromMinutes(10)))
    {
        throw new TimeoutException("The installer did not finish within 10 minutes.");
    }

    // 3010 = success, restart required (never for a per-user install, but not a failure).
    if (p.ExitCode is not 0 and not 3010)
    {
        throw new InvalidOperationException($"Installer exited with code {p.ExitCode}.");
    }
}
