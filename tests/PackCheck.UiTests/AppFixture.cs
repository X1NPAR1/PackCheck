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
        foreach (var p in Process.GetProcessesByName("PackCheck"))
        {
            p.Kill();
            p.WaitForExit(5000);
        }

        var exe = Path.Combine(FindRoot(), "src", "PackCheck.App", "bin",
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            "net10.0-windows", "PackCheck.exe");
        App = Application.Launch(exe);
        Automation = new UIA3Automation();

        // The splash is a separate window; wait for the main window.
        Main = Retry.WhileNull(
            () => App.GetAllTopLevelWindows(Automation).FirstOrDefault(w => w.AutomationId == "MainWindow"),
            TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(250)).Result
            ?? throw new TimeoutException("Main window did not appear.");
    }

    public Application App { get; }

    public UIA3Automation Automation { get; }

    public Window Main { get; }

    public AutomationElement Find(string automationId, int seconds = 10) =>
        Retry.WhileNull(() => Main.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), TimeSpan.FromSeconds(seconds), TimeSpan.FromMilliseconds(100)).Result
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
