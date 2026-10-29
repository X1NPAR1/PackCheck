<p align="center">
  <img src="docs/media/banner.png" alt="PackCheck" width="560">
</p>

<p align="center">
  <a href="https://x1npar1.github.io/PackCheck/"><img alt="Website: x1npar1.github.io/PackCheck" src="https://img.shields.io/badge/Website-x1npar1.github.io%2FPackCheck-3F7A2A?style=for-the-badge"></a>
  <a href="https://github.com/X1NPAR1/PackCheck/releases/latest"><img alt="Download for Windows" src="https://img.shields.io/badge/Download-Windows-1B1E1C?style=for-the-badge"></a>
</p>

<p align="center">
  <b>Free Minecraft server manager for Windows: create, run and validate Minecraft Java servers, and catch broken plugin and mod packs before they go live.</b>
</p>

<p align="center">
  <a href="https://github.com/X1NPAR1/PackCheck/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/X1NPAR1/PackCheck?label=download&color=5E9A3E"></a>
  <a href="https://github.com/X1NPAR1/PackCheck/releases"><img alt="Downloads" src="https://img.shields.io/github/downloads/X1NPAR1/PackCheck/total?color=5E9A3E"></a>
  <a href="https://github.com/X1NPAR1/PackCheck/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/X1NPAR1/PackCheck/actions/workflows/ci.yml/badge.svg"></a>
  <a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/github/license/X1NPAR1/PackCheck?color=5E9A3E"></a>
  <img alt="Windows 10/11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-3A3F3B">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-3A3F3B">
</p>

<p align="center">
  <a href="https://x1npar1.github.io/PackCheck/"><b>Website</b></a> ·
  <a href="https://github.com/X1NPAR1/PackCheck/releases/latest">Download</a> ·
  <a href="#cli">CLI</a> ·
  <a href="docs/security.md">Security model</a> ·
  <a href="CHANGELOG.md">Changelog</a>
</p>

---

PackCheck is a free, open-source Windows desktop app (WPF, .NET 10) for anyone who hosts a Minecraft Java server at home: a survival world for friends, a modded server, or a small network behind a proxy. It does two jobs:

- **Server studio.** Create a Paper, Purpur, Folia, Spigot, Vanilla, Fabric, Quilt, Forge, NeoForge, Mohist, Velocity or BungeeCord server from official sources in a few clicks, pick RAM and CPU cores, start and stop it, watch the live console and TPS, edit `server.properties`, install plugins and mods, open it to your friends over the internet, and take backups.
- **Pack validation.** Before you add or update plugins, PackCheck reads every JAR and config file and tells you what will break: missing dependencies, duplicate plugins, corrupt downloads, plugins built for a newer Minecraft version, invalid settings. Each finding comes with evidence and a suggested fix.

Static checks never execute plugin code. A real startup test is available, but you have to turn it on yourself, and it runs on a throw-away copy of the pack.

## Screenshots

| Server dashboard | Create a server |
|---|---|
| ![Server dashboard with TPS, CPU, RAM and ping gauges, resources and technical details](docs/media/server-overview.png) | ![Create server wizard with profiles, platform cards, RAM and CPU sliders](docs/media/create-wizard.png) |

| Performance history and advice | Players |
|---|---|
| ![Performance tab with TPS, tick time, CPU, RAM and player charts, diagnosis and spark profiling](docs/media/performance.png) | ![Players tab with whitelist, operators, bans, play time and IP history](docs/media/players.png) |

| Backups | Version upgrade |
|---|---|
| ![Backups by type with notes, pinning, retention and per-world restore](docs/media/backups.png) | ![Version upgrade wizard with a plugin and mod compatibility report](docs/media/upgrade.png) |

| Crash diagnosis | Server icon and MOTD |
|---|---|
| ![Crash banner naming the broken config file and line with a one-click fix](docs/media/crash-diagnosis.png) | ![Server icon upload and MOTD editor with color codes and a live server-list preview](docs/media/identity.png) |

| Automation and Discord | Console |
|---|---|
| ![Automation rules with schedules, crash restarts and triggers](docs/media/automation.png) | ![Live console with filters, search and player list](docs/media/console.png) |

| Pack validation | Theme and language |
|---|---|
| ![Validation findings grouped by severity, category and file](docs/media/validation.gif) | ![Dark and light theme, five languages](docs/media/theme-language.gif) |

| Plugins and mods | Play over the internet |
|---|---|
| ![Plugin browser filtered to the selected server](docs/media/plugins.png) | ![Port forwarding guide and reachability test](docs/media/online-router.png) |

| Files and code editor | Tray menu |
|---|---|
| ![Files tab with code editor](docs/media/files.png) | ![Tray menu with live server status](docs/media/tray-menu.png) |

| Startup | Navigation |
|---|---|
| ![Startup](docs/media/splash.gif) | ![Moving between pages](docs/media/navigation.gif) |

| Settings | About |
|---|---|
| ![Settings with section sidebar and search](docs/media/settings.png) | ![About with system information](docs/media/about.png) |

## Features

**Servers**
- 18 platforms: Paper, Purpur, Folia, Leaf, Leaves, Spigot and CraftBukkit (compiled with BuildTools), Vanilla, Fabric, Quilt, Forge, NeoForge, SpongeVanilla, Mohist, Youer, Arclight, and the proxies Velocity and BungeeCord. Version lists come from each project's own API, so only versions with a real download appear.
- Downloads are hash-verified wherever the source publishes a hash; Forge, NeoForge, Quilt and BuildTools run automatically.
- The right Java (8, 17, 21 or 25) is installed automatically from Eclipse Temurin before a server starts.
- Create wizard with ready-made profiles (survival with friends, modded, creative, minigames, hardcore, low-end PC), platform cards, RAM slider (1–256 GB, warns when it leaves too little for Windows), CPU core limit (up to 64), game rules (game mode, difficulty, players, view distance, PvP, whitelist, online mode, hardcore, MOTD), a free port chosen for you and a live capacity estimate.
- Change memory, CPU cores and garbage-collector flags later; see PID, threads, handles and the exact Java start command.
- Delete a server for good from its page, with two confirmations (backups are kept).
- Explicit EULA consent: nothing is accepted on your behalf.
- Live dashboard: ring gauges for TPS, CPU, RAM and ping with five health levels, players, uptime, disk and world size.
- Console with level filters and counters, search that highlights matches with next/previous, clickable player names (op, kick, ban, teleport), timestamps, copy/save, command history and Tab completion, quick actions, player list, and a crash banner that can hand the error to the assistant.
- Files tab: upload by drag and drop, multi-select, copy/cut/paste, zip/unzip, delete to the Recycle Bin, and a code editor with line numbers, syntax highlighting and a syntax check before saving.
- Settings editor for `server.properties` plus the main performance settings of `bukkit.yml`, `spigot.yml` and Paper's config files, every one explained in five languages, typed, range-checked and resettable, with a "changed from default" filter. YAML files keep their comments and layout.
- **Server icon and MOTD**: pick any image and it becomes the 64×64 `server-icon.png`; edit the two-line MOTD with color and style buttons and a live preview of the multiplayer list.
- **Crash diagnosis**: when a server crashes, PackCheck reads the console, `latest.log` and the crash report and names the cause (EULA, port in use, out of memory, wrong Java, missing dependency, incompatible mod, mixin failure, plugin error, broken config file with line number, damaged world) with a one-click fix: accept the EULA, pick a free port, add RAM, install the right Java, disable the jar that caused it, open the file or restore a backup. Crashes that exit with code 0 (Paper does this) are detected too.
- **Version upgrade wizard**: pick a newer Minecraft version and PackCheck checks every plugin and mod against it, finds newer builds on Modrinth (verified by SHA-512), takes a pinned full backup, installs the right Java and the new server, test-starts it, and rolls everything back with one click if the test fails.
- Every server process tree runs in a Windows Job Object; nothing keeps running after a crash or a forced stop.
- **Backups**: full, worlds-only or settings-only backups with a note, taken safely while the server runs (`save-off`, `save-all flush`, `save-on`); files in use are skipped instead of failing the backup. Keep the newest N plus one per day and one per week, pin backups that must never be deleted, copy every backup to a second folder (OneDrive, Google Drive, NAS), and restore everything or just one world. A safety backup is taken before every restore.
- **Players**: everyone who ever joined, with last seen, play time, IP addresses and accounts sharing an IP; whitelist, op, ban and kick whether the server is running or not (UUIDs come from the server cache, Mojang or the offline formula).
- **Performance**: one sample per minute kept for 7 days, charts for TPS, tick time, CPU, RAM and players over 1 h, 24 h or 7 days, a summary, ranked advice (view and simulation distance, entity activation range, memory, GC flags, "Can't keep up" warnings) and a 30-second spark profile with the report link.

**Automation**
- Per-server rules on an Automation tab: start when PackCheck opens, restart after a crash (with a limit per hour), scheduled restart with a chat countdown, scheduled backup, scheduled or repeating console commands, chat announcements, warn or restart on low TPS or high memory, and stop when nobody is online.
- Triggers: when this or another server starts, stops or crashes (start, stop, restart, back up, run a command, send a message or notify, after a delay), and when the player count reaches a number.
- Days of the week, "Run now", next and last run for every rule, an activity log, and a one-click recommended setup.
- Rules keep working while PackCheck sits in the tray; with "Start with Windows" a server comes back after every reboot.
- **Discord notifications** per server through a webhook: started, stopped, crashed (with the diagnosis), player joined or left, lag, backup done or failed and automation actions, with an optional @here or role mention for important events, a "Send test" button and a "post to Discord" automation action. Only real Discord webhook URLs are accepted.

**Plugins, mods and modpacks**
- Browse Modrinth, Hangar and CurseForge page by page, sorted by downloads or updates and filtered by category, without having to search first.
- Project detail panel with the full description, supported versions and loaders.
- Installed plugins and mods show **Update** when a newer compatible file exists; updating replaces the old jar safely.
- Items that do not match the selected server's Minecraft version or loader cannot be installed, and the reason is shown.
- Modrinth `.mrpack` modpacks are installed file by file with SHA-512 checks; CurseForge server packs are extracted safely.
- The sidebar only enables Plugins or Mods when the selected server can use them.

**Proxy networks**
- Connect Velocity or BungeeCord to backend servers; PackCheck writes `velocity.toml` / `config.yml`, a forwarding secret, and secures every backend (offline mode behind the proxy, bound to 127.0.0.1).
- Setup check for secret mismatches, unsafe backends and port clashes; start and stop the whole network in order.

**AI assistant**
- Choose a provider (Anthropic Claude, OpenAI, Google Gemini, xAI Grok, DeepSeek, Mistral, Groq, OpenRouter, Ollama, LM Studio) and pick one of its models from a list; the key is stored encrypted with Windows DPAPI.
- Answers stream in as they are written, with Markdown formatting; each server keeps its own chat history.
- Four modes: ask before every change, plan first (nothing changes until you run the plan), auto-edit files, or bypass approvals.
- Every step it takes is listed with a readable title; click a step to see what was sent and what came back.
- It reads the console, files and validation results, and can edit configs, translate plugin message files, send commands or create several servers at once.
- Every change waits for your approval with a preview; it cannot leave the server folder and has no shell.

**Online**
- Open a server to the internet: a firewall rule (after you approve the Windows prompt) and UPnP forwarding on the router.
- Copy the public address, get A and SRV records for your own domain, optionally keep a DuckDNS name up to date.
- When the router refuses automatic forwarding, a step-by-step guide shows the exact values to enter, links to the router page and explains CGNAT, double routers and NAT loopback.
- **Test from outside** checks whether players on the internet can really reach the server.
- Carrier-grade NAT is detected and explained. Closing removes the forwarding and the firewall rule again, and PackCheck offers to close open ports when you quit.

**Validation**
- Pack structure, `server.properties` values, corrupt or unreadable JARs, oversized files.
- Reads `plugin.yml` and `paper-plugin.yml` metadata; compatibility is never guessed from file names.
- Required and optional dependencies, duplicates, `api-version` vs. server version, Java vs. Minecraft version.
- Mods: Fabric, Quilt, Forge and NeoForge metadata, missing dependencies, Minecraft version ranges, client-only mods, duplicates and mixed loaders.
- Results are `PASS`, `WARN`, `FAIL`, `SKIP` or `ERROR`. "Failed" and "could not run" are never the same thing.
- Findings grouped by severity, category or file, a file tree of affected files, search, a plugin inventory (version, API, size, SHA-256, dependencies) and "Ask the assistant" on any problem.
- Pre-flight checks before plugin changes, run history with diff, JSON (with a [schema](docs/schema/report.schema.json)) and self-contained HTML reports.

**App**
- EXE and MSI installers, per-user, no admin rights needed.
- English, Turkish, German, French and Russian, switchable instantly.
- Dark and light themes (or follow Windows); Full, Reduced or Off animations.
- Runs in the system tray with a menu that shows every server's state and starts or stops them; optionally starts with Windows.
- No telemetry.

## Updates

On every start, PackCheck asks GitHub for the latest release.

- If a newer version exists, a dialog shows its release notes. You can update now or postpone.
- You can postpone on the day the update is first announced and on the next day. **On the first launch of the third day, PackCheck installs the update by itself.**
- With **Settings → Updates → Install new releases automatically** turned on, new releases are installed at startup without asking.
- Packages are downloaded only from the repository's release assets and are rejected if their SHA-256 does not match the published `.sha256` file.
- Offline? The check is skipped silently and the app starts normally.

## Install

Download **`PackCheck-Setup-<version>.exe`** (or the `.msi`) from the [latest release](https://github.com/X1NPAR1/PackCheck/releases/latest) and run it.
It installs for your Windows user only, so no administrator rights are needed, and later updates install silently.

The app is self-contained, so .NET does not need to be installed. Windows 10 or 11 (x64) is required. The binaries are not code-signed yet, so SmartScreen may warn on the first launch.

## CLI

`packcheck` is the same validation engine without the UI. It is useful in CI.

```text
packcheck validate <folder|pack.zip> [--format text|json|html] [--output file]
                   [--mc-version 1.20.4] [--java 21] [--fail-on-warnings] [--timeout 300] [--verbose]
packcheck doctor
packcheck version
```

| Exit code | Meaning |
|---|---|
| 0 | Pass, or pass with warnings |
| 1 | Validation failed (or warnings with `--fail-on-warnings`) |
| 2 | Incomplete: a check could not run |
| 64 | Usage error |
| 70 | Internal error |

Example output:

```text
PackCheck 3.1.1 - demo-pack
  [FAIL ] PC040 Missing dependency: 'EconomyShop' requires 'ProtocolLib', which is not installed. The plugin will not enable.
           plugin.yml: depend: [Vault, ProtocolLib]
           fix: Install 'ProtocolLib' or remove 'EconomyShop'.
  [FAIL ] PC030 Duplicate plugin: 'Homes' is installed 2 times; the server loads only one of them.
Result: FAIL  (pass 2, warn 1, fail 5, skip 1, error 0)
```

### GitHub Actions

```yaml
- name: Validate server pack
  shell: pwsh
  run: |
    Invoke-WebRequest https://github.com/X1NPAR1/PackCheck/releases/latest/download/packcheck-cli-win-x64.zip -OutFile packcheck.zip
    Expand-Archive packcheck.zip -DestinationPath packcheck
    ./packcheck/packcheck.exe validate ./server --mc-version 1.20.4 --format html --output packcheck-report.html
```

## Checks

| ID | Check |
|---|---|
| PC000 | Target exists; archive is safe to extract (path traversal, zip bombs) |
| PC001 / PC002 | Pack structure, server JAR present |
| PC010 / PC011 | `server.properties` values, offline-mode warning |
| PC020 / PC021 / PC022 | Corrupt JAR, missing metadata, oversized file |
| PC030 / PC031 | Duplicate plugin names, identical files |
| PC040 / PC041 | Required and optional dependencies |
| PC050 / PC051 | `api-version` vs. server, Java vs. Minecraft |
| RT001 / RT002 | Runtime test: server start, plugin enable errors (opt-in) |

Adding a check means adding one class that implements `IAnalyzer`. See [docs/rule-authoring.md](docs/rule-authoring.md).

## Guides

- [How to host a Minecraft server on Windows](https://x1npar1.github.io/PackCheck/guides/host-minecraft-server-windows.html)
- [Minecraft port forwarding: let friends join your server](https://x1npar1.github.io/PackCheck/guides/minecraft-port-forwarding.html)
- [Paper vs Fabric vs Forge vs NeoForge](https://x1npar1.github.io/PackCheck/guides/paper-vs-fabric-vs-forge.html)
- [Check Minecraft plugins and mods before they break your server](https://x1npar1.github.io/PackCheck/guides/check-minecraft-plugins.html)
- [Automate restarts, backups and crash recovery](https://x1npar1.github.io/PackCheck/guides/minecraft-server-automation.html)
- [Türkçe tanıtım](https://x1npar1.github.io/PackCheck/tr/)

## Compared with other Minecraft server tools

| | PackCheck | Web panels (Crafty, MCSManager, Pterodactyl) | Simple desktop launchers |
|---|---|---|---|
| Runs on | Windows desktop, no web server | A server or VM, used through a browser | Windows desktop |
| Setup | Installer, no admin rights | Web server, often Docker or Linux | Installer |
| Plugin and mod compatibility | Checked before install, with the reason | Usually not | Usually not |
| Pack validation (dependencies, versions, duplicates) | Yes, also as a CLI | No | No |
| Port forwarding help and outside test | Yes | Usually not (servers are hosted) | Rarely |
| Automation | Schedules, crash restarts, triggers between servers | Yes (varies) | Rarely |
| Crash diagnosis with a fix | Names the cause and fixes it in one click | Logs only | No |
| Version upgrade | Compatibility report, Modrinth updates, backup, test start, rollback | Manual | Manual |
| Discord notifications | Built in (webhook) | Plugin or add-on | No |
| Best for | Hosting at home for friends | Hosting providers and many users | One quick server |



**How do I make a Minecraft server on Windows?** Install PackCheck, click **New server**, pick a profile (for example *Survival with friends*), accept the EULA and press **Create server**. PackCheck downloads the server, installs the right Java and starts it.

**How do my friends join?** On the same Wi-Fi they use the local address shown on the **Online** page. Over the internet press **Open to the internet**; if your router does not allow automatic forwarding, follow the guide on that page and use **Test from outside** to confirm.

**Which platform should I pick?** Paper for plugins, Fabric or NeoForge for mods, Velocity to connect several servers. Each platform card explains itself.

**Why does my plugin or mod not load?** Run **Validate** on the server: it lists missing dependencies, wrong Minecraft versions, duplicates and corrupt jars with the fix.

**Is it free?** Yes, MIT licensed, no account, no telemetry.

## Documentation

- [Architecture](docs/architecture.md) · [Design system](docs/design-system.md) · [Localization](docs/localization.md)
- [Testing](docs/testing.md) (unit, network, real-server and UI tests) · [Performance](docs/performance.md) (measured)
- [Security model](docs/security.md) · [Writing a check](docs/rule-authoring.md)
- Research: [ServerCreator audit](docs/research/servercreator-audit.md), [other tools](docs/research/competitors.md), [common problems](docs/research/user-problems.md), [Minecraft brand rules](docs/research/minecraft-brand.md)

## Build from source

```powershell
dotnet build PackCheck.sln
dotnet test tests/PackCheck.Tests
dotnet test tests/PackCheck.UiTests   # UI tests, needs a desktop session
dotnet run --project src/PackCheck.App
```

Requires the .NET 10 SDK on Windows.

| Project | Responsibility |
|---|---|
| `PackCheck.Core` | Result model, versions, hashing, exit codes |
| `PackCheck.Validation` | Analyzers, safe zip extraction, metadata parsing |
| `PackCheck.Reporting` | JSON / HTML reports, run diff |
| `PackCheck.Servers` | Downloads, Java runtimes, process + Job Object, backups, runtime test |
| `PackCheck.Providers` | Modrinth, Hangar and CurseForge clients (timeouts, cache, failures as values) |
| `PackCheck.Updater` | GitHub release check, grace-period policy, verified install |
| `PackCheck.App` | WPF UI |
| `PackCheck.Cli` | Headless CLI |
| `PackCheck.UpdateHelper` | Replaces files after the app exits during an update |

## Known limitations

- Windows only.
- Pufferfish is not offered because its build server was not reachable; Magma, Glowstone and Waterfall are no longer maintained.
- CurseForge needs your own API key (Settings), because CurseForge does not allow anonymous access.
- Fabric's server launcher has no published checksum, so it is verified only by HTTPS.
- The runtime test isolates files, ports and memory, but plugin code still runs with your user account's rights. Read [docs/security.md](docs/security.md) before you use it on plugins you don't trust.
- Version checks rely on what plugins declare. A plugin that declares nothing gets a warning, not a guess.
- Backups reach the cloud through a synced folder (OneDrive, Google Drive, Dropbox) chosen as the second copy; there is no direct cloud API.
- Discord support sends notifications; running commands from Discord is not supported yet.
- The upgrade wizard only moves to newer versions, because worlds cannot be opened by an older Minecraft version.

## License

[MIT](LICENSE). Third-party components are listed in [THIRD_PARTY.md](THIRD_PARTY.md).

*Not an official Minecraft product. Not approved by or associated with Mojang or Microsoft.*
