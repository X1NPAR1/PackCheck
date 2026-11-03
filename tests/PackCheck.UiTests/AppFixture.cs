using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace PackCheck.UiTests;

/// <summary>Starts the freshly built app once for all UI tests and closes it at the end.</summary>
public sealed class AppFixture : IDisposable
{
    public AppFixture()
    {
        // Own data folder with one seeded server: the tests never read or change the user's real server list,
        // and they do not depend on what happens to be installed on the machine (CI starts empty).
        DataRoot = Path.Combine(Path.GetTempPath(), "packcheck-ui-data-" + Guid.NewGuid().ToString("N"));
        SeedData(DataRoot);

        var exe = Path.Combine(FindRoot(), "src", "PackCheck.App", "bin",
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            "net10.0-windows", "PackCheck.exe");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        start.Environment["PACKCHECK_DATA"] = DataRoot;
        App = Application.Launch(start);
        Automation = new UIA3Automation();

        // The splash is a separate window; wait for the main window.
        // UI Automation can time out while the app is still starting (slow CI runners), so those errors are retried;
        // if the app exits, the test fails at once with the last error.
        Exception? last = null;
        var deadline = DateTime.UtcNow.AddSeconds(120);
        Window? main = null;
        while (main is null && DateTime.UtcNow < deadline)
        {
            if (App.HasExited)
            {
                throw new InvalidOperationException($"PackCheck exited during startup (exit code {App.ExitCode}).", last);
            }

            try
            {
                main = App.GetAllTopLevelWindows(Automation).FirstOrDefault(w => w.AutomationId == "MainWindow");
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TimeoutException)
            {
                last = ex;
            }

            if (main is null)
            {
                Thread.Sleep(500);
            }
        }

        Main = main ?? throw new TimeoutException("Main window did not appear within 120 seconds.", last);

        // CI screens are small; a maximized window shows every page section the tests look at.
        Main.Patterns.Window.PatternOrDefault?.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
    }

    /// <summary>Temporary data folder used by this test run (removed on dispose).</summary>
    public string DataRoot { get; }

    public const string SeededServer = "UI Test Server";

    /// <summary>A stopped Paper server folder (no real jar needed: the tests never start it) plus English UI settings.</summary>
    private static void SeedData(string root)
    {
        var server = Path.Combine(root, "servers", "ui-test");
        Directory.CreateDirectory(Path.Combine(server, "plugins"));
        File.WriteAllText(Path.Combine(server, "server.properties"), "server-port=25599\nmotd=UI test\n");
        File.WriteAllText(Path.Combine(server, "eula.txt"), "eula=true\n");
        File.WriteAllBytes(Path.Combine(server, "paper.jar"), [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        var json = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new
            {
                Id = "uitest0000000000000000000000000",
                Name = SeededServer,
                Directory = server,
                Platform = 0,
                MinecraftVersion = "1.21.4",
                JarFile = "paper.jar",
                JavaPath = "java.exe",
                MinMemoryMb = 1024,
                MaxMemoryMb = 2048,
                UseAikarFlags = true,
                Port = 25599,
                CpuCores = 0,
                CreatedAt = DateTimeOffset.Now,
            },
        });
        File.WriteAllText(Path.Combine(root, "servers.json"), json);
    }

    public Application App { get; }

    public UIA3Automation Automation { get; }

    public Window Main { get; }

    public AutomationElement Find(string automationId, int seconds = 10) =>
        Retry.WhileNull(() => Main.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), TimeSpan.FromSeconds(seconds), TimeSpan.FromMilliseconds(100), throwOnTimeout: false, ignoreException: true).Result
        ?? throw new InvalidOperationException($"Element '{automationId}' not found.");

    public void Navigate(string navId) => Find(navId).AsListBoxItem().Select();

    public void Dispose()
    {
        App.Close();
        if (!App.HasExited)
        {
            App.Kill();
        }

        Automation.Dispose();
        App.Dispose();
        try
        {
            Directory.Delete(DataRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "PackCheck.sln")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new DirectoryNotFoundException("repository root");
        }

        return dir;
    }
}

[CollectionDefinition("app")]
public sealed class AppCollection : ICollectionFixture<AppFixture>;
