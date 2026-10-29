using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PackCheck.App.Infrastructure;
using PackCheck.App.ViewModels;
using PackCheck.App.Views;
using PackCheck.Core;
using PackCheck.Providers;
using PackCheck.Servers;
using PackCheck.Updater;
using PackCheck.Validation;
using Serilog;

namespace PackCheck.App;

public partial class App : Application
{
    private IHost? _host;

    public static IServiceProvider Services { get; private set; } = null!;

    public static TrayIcon? Tray { get; private set; }

    public static AppSettings? Settings { get; private set; }

    /// <summary>Quits for real (tray "Exit", updates): closes the window instead of hiding it.</summary>
    public static void Quit()
    {
        if (Current.MainWindow is MainWindow w)
        {
            w.AllowClose = true;
            w.Close();
        }
        else
        {
            Current.Shutdown();
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Elevated helper mode: "PackCheck.exe --firewall add|remove <port>" (started via UAC by the Online page).
        if (e.Args is ["--firewall", var op and ("add" or "remove"), var portText] && int.TryParse(portText, out var fwPort) && fwPort is > 0 and < 65536)
        {
            Shutdown(Firewall.RunElevatedCommands(fwPort, op == "add"));
            return;
        }
        DispatcherUnhandledException += OnUnhandled;

        Directory.CreateDirectory(AppPaths.Logs);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(AppPaths.Logs, "packcheck-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 10)
            .CreateLogger();

        // Step 1: settings (needed before anything is drawn, so the splash uses the right theme/language)
        var settings = AppSettings.Load();
        Settings = settings;
        Motion.Level = settings.Animations;
        Loc.Instance.Culture = CultureInfo.GetCultureInfo(settings.Language);
        ThemeManager.Apply(settings.Theme);

        var splash = new SplashWindow();
        splash.Show();
        splash.Report(0.08, Loc.T("Splash_Settings"));

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(s =>
            {
                s.AddSingleton(settings);
                s.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
                s.AddSingleton(sp => new GitHubReleaseClient(sp.GetRequiredService<HttpClient>()));
                s.AddSingleton(sp => new UpdateService(sp.GetRequiredService<GitHubReleaseClient>(), sp.GetRequiredService<HttpClient>()));
                s.AddSingleton<ServerSources>();
                s.AddSingleton<JavaRuntimes>();
                s.AddSingleton<ServerCreator>();
                s.AddSingleton(_ => new ServerRegistry());
                s.AddSingleton(_ => new PackValidator());
                s.AddSingleton<RuntimeTestRunner>();
                s.AddSingleton<IPluginProvider>(sp => new ModrinthProvider(sp.GetRequiredService<HttpClient>()));
                s.AddSingleton<IPluginProvider>(sp => new HangarProvider(sp.GetRequiredService<HttpClient>()));
                s.AddSingleton<IModProvider>(sp => new ModrinthMods(sp.GetRequiredService<HttpClient>()));
                s.AddSingleton<IModProvider>(sp => new CurseForgeMods(sp.GetRequiredService<HttpClient>(), () => sp.GetRequiredService<AppSettings>().CurseForgeApiKey));
                s.AddSingleton<ICatalog>(sp => new ModrinthCatalog(sp.GetRequiredService<HttpClient>()));
                s.AddSingleton<ICatalog>(sp => new HangarCatalog(sp.GetRequiredService<HttpClient>()));
                s.AddSingleton<ICatalog>(sp => new CurseForgeCatalog(sp.GetRequiredService<HttpClient>(), () => sp.GetRequiredService<AppSettings>().CurseForgeApiKey));
                s.AddSingleton<ModsViewModel>();
                s.AddSingleton<AppState>();
                s.AddSingleton<MainViewModel>();
                s.AddSingleton<ServersViewModel>();
                s.AddSingleton<ValidateViewModel>();
                s.AddSingleton<PluginsViewModel>();
                s.AddSingleton<SettingsViewModel>();
                s.AddSingleton<OnlineViewModel>();
                s.AddSingleton<ProxyViewModel>();
                s.AddSingleton<AssistantViewModel>();
                s.AddSingleton<AboutViewModel>();
            })
            .Build();
        Services = _host.Services;
        await _host.StartAsync().ConfigureAwait(true);

        // Step 2: theme + language resources are applied; give the step its moment on screen
        splash.Report(0.22, Loc.T("Splash_Theme"));
        await Task.Delay(Motion.Enabled ? 250 : 0).ConfigureAwait(true);

        // Step 3: Java runtimes
        splash.Report(0.38, Loc.T("Splash_Java"));
        var state = Services.GetRequiredService<AppState>();
        try
        {
            state.JavaRuntimes = await Services.GetRequiredService<JavaRuntimes>().DetectAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Java detection failed");
        }

        // First run: install a current LTS Java so the first server can start right away.
        if (!settings.FirstRunDone)
        {
            if (!state.JavaRuntimes.Any(r => r.FeatureVersion >= 21))
            {
                try
                {
                    var java = Services.GetRequiredService<JavaRuntimes>();
                    var rt = await java.InstallTemurinAsync(21, new Progress<double>(p => splash.Report(0.38 + (p * 0.18), Loc.F("Splash_InstallJava", $"{p:P0}"))), CancellationToken.None).ConfigureAwait(true);
                    state.JavaRuntimes = [rt, .. state.JavaRuntimes];
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    Log.Warning(ex, "First-run Java install skipped");
                }
            }

            settings.FirstRunDone = true;
            settings.Save();
        }

        // Step 4: servers
        splash.Report(0.58, Loc.T("Splash_Servers"));
        var servers = Services.GetRequiredService<ServersViewModel>();
        servers.Load();

        // Step 5: updates (never blocks startup for long; offline = no update)
        splash.Report(0.76, Loc.T("Splash_Updates"));
        var updates = Services.GetRequiredService<UpdateService>();
        var decision = await updates.CheckAsync(settings.AutoUpdate, TimeSpan.FromSeconds(6), CancellationToken.None).ConfigureAwait(true);
        Log.Information("Update check: {Action} {Tag}", decision.Action, decision.Release?.Tag);

        splash.Report(0.92, Loc.T("Splash_Almost"));
        var main = new MainWindow { DataContext = Services.GetRequiredService<MainViewModel>() };
        await Task.Delay(Motion.Enabled ? 200 : 0).ConfigureAwait(true);
        splash.Report(1, Loc.T("Splash_Ready"));
        await Task.Delay(Motion.Enabled ? 450 : 0).ConfigureAwait(true);

        // Main window is created hidden with the theme background, so there is no white flash.
        MainWindow = main;
        var minimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        main.Opacity = 0;
        if (!minimized)
        {
            main.Show();
        }

        await splash.FadeOutAsync().ConfigureAwait(true);
        splash.Close();
        var shell = (MainViewModel)main.DataContext;
        Tray = new TrayIcon(new TrayActions(
            main.ShowFromTray,
            Quit,
            () => shell.Servers.Items.ToList(),
            s =>
            {
                main.ShowFromTray();
                shell.Servers.Selected = s;
                shell.Navigate(shell.Servers);
            },
            index =>
            {
                main.ShowFromTray();
                shell.NavIndex = index;
            }));
        foreach (var s in shell.Servers.Items)
        {
            s.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ServerItemViewModel.State))
                {
                    Tray?.UpdateTooltip();
                }
            };
        }

        // Opens the tray menu once without a click (screenshots, UI checks).
        if (Array.IndexOf(e.Args, "--tray-preview") is var at and >= 0 && at + 1 < e.Args.Length)
        {
            var target = e.Args[at + 1];
            _ = Dispatcher.InvokeAsync(() => Tray?.RenderMenu(target), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        if (minimized)
        {
            main.Opacity = 1;
        }
        else
        {
            main.FadeIn();
        }

        await Services.GetRequiredService<MainViewModel>().HandleUpdateDecisionAsync(decision).ConfigureAwait(true);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        if (Services?.GetService<MainViewModel>() is { } vm)
        {
            vm.Toast(Loc.T("Common_Error") + ": " + e.Exception.Message, isError: true);
            e.Handled = true;
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Tray?.Dispose();
        if (Services?.GetService<ServersViewModel>() is { } servers)
        {
            await servers.StopAllAsync().ConfigureAwait(true);
        }

        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            _host.Dispose();
        }

        await Log.CloseAndFlushAsync().ConfigureAwait(true);
        base.OnExit(e);
    }
}

/// <summary>Shared, process-wide state that several pages read.</summary>
public sealed class AppState
{
    public IReadOnlyList<JavaRuntime> JavaRuntimes { get; set; } = [];
}
