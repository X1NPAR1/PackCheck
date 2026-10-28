# Architecture

PackCheck is a .NET 10 solution. The WPF app and the CLI share every library; nothing in the libraries depends on WPF.

```
PackCheck.Core          models (CheckResult, ValidationReport), MinecraftVersion, hashing, AppPaths, safe file helpers
PackCheck.Validation    analyzers (PC0xx), plugin/mod metadata readers, SafeZip, PackValidator
PackCheck.Reporting     JSON report (schema in docs/schema) and HTML report (escaped, strict CSP)
PackCheck.Providers     Modrinth, Hangar, CurseForge: browse, latest file, descriptions; cached, timeouts, failures as values
PackCheck.Servers       platform downloads, Java runtimes, server process (Job Object), telemetry, networking, proxy, runtime test
PackCheck.Updater       GitHub release check, three-day policy, installer asset choice
PackCheck.UpdateHelper  small process that waits for the app to exit, runs the installer silently and restarts the app
PackCheck.App           WPF UI (MVVM), assistant, tray, settings
PackCheck.Cli           packcheck validate | doctor | version
```

## App

- **Hosting**: `App.xaml.cs` builds a Generic Host. Services, view models and the shared `HttpClient` are singletons. Serilog writes to `%LOCALAPPDATA%\PackCheck\logs`.
- **MVVM**: CommunityToolkit.Mvvm source generators (`[ObservableProperty]`, `[RelayCommand]`). Views have code-behind only for view concerns (scrolling, focus, context menus).
- **Navigation**: `MainViewModel.NavIndex` selects the page view model; `DataTemplate`s in `MainWindow.xaml` map each view model to its view. Pages that need fresh data get an `Activate()` call when shown.
- **Servers**: `ServersViewModel` owns `ServerItemViewModel`s; each wraps a `ServerDefinition` from `servers.json`, a `ServerProcess` and the console, gauges, files, properties and backups for that server.
- **Assistant**: `Ai/Providers.cs` has one conversation type for Anthropic (official SDK, streaming with the SDK's aggregator) and one for every OpenAI-compatible API (OpenAI, Gemini, xAI Grok, DeepSeek, Mistral, Groq, OpenRouter, Ollama, LM Studio; server-sent events rebuilt into a full response). `Ai/AssistantTools.cs` defines the tools; anything that changes a file, sends a command or starts/stops/creates a server returns a `PendingAction` that only runs after the user approves it. Paths are confined to the selected server folder.
- **Theme and language**: `ThemeManager` swaps the color dictionary; `Loc` serves RESX strings and raises a change notification so every `{l:Loc}` binding updates without a restart.

## Server lifecycle

1. `ServerCreator` resolves the platform version list from the project's own API, downloads the jar (SHA-256 or SHA-1/MD5 when that is all the API gives), runs installers (Forge, NeoForge, Fabric, Quilt, BuildTools) and writes `eula.txt` only after the user accepted it.
2. `JarJava` reads the class file version of the jar's main class to pick the Java feature version; `JavaRuntimes` finds an installed one or downloads Temurin (a JDK for BuildTools).
3. `ServerProcess` starts Java inside a Job Object (kill-on-close, so no orphan servers), reads stdout/stderr line by line and accepts commands on stdin.
4. `Telemetry` samples CPU and memory of the process tree, pings with Server List Ping and asks for TPS where the platform supports it.

## Validation

`PackValidator` builds an inventory (`Analysis`) once and runs each `IAnalyzer` on it. Analyzers never execute plugin code; they read archives through `SafeZip` (path traversal, size and ratio limits). An empty analyzer list or an analyzer crash is reported as `Error`, which makes the overall result `Incomplete`, never `Pass`. The optional runtime test copies the pack to a temporary folder and starts it in a Job Object with limits.

## Updates

On start the app asks GitHub for the latest release. "Later" stores the first postpone date; on the first start on day three it updates without asking. With auto update on it installs right away. The asset is chosen by `install-type.txt` (written by the EXE or MSI installer); the hash is checked against the `.sha256` asset before `PackCheck.UpdateHelper` runs the installer silently.
