<p align="center">
  <img src="docs/media/banner.png" alt="PackCheck" width="560">
</p>

<p align="center">
  <b>Create, run and validate Minecraft Java servers, and catch broken plugin packs before they go live.</b>
</p>

<p align="center">
  <a href="https://github.com/X1NPAR1/PackCheck/releases/latest">Download</a> ·
  <a href="#cli">CLI</a> ·
  <a href="docs/security.md">Security model</a> ·
  <a href="CHANGELOG.md">Changelog</a>
</p>

---

PackCheck is a Windows desktop app (WPF, .NET 10) for people who run Paper servers. It does two jobs:

- **Server studio.** Create a Paper or Vanilla server from official sources, start and stop it, watch the live console, edit `server.properties`, manage plugins and take backups.
- **Pack validation.** Before you add or update plugins, PackCheck reads every JAR and config file and tells you what will break: missing dependencies, duplicate plugins, corrupt downloads, plugins built for a newer Minecraft version, invalid settings. Each finding comes with evidence and a suggested fix.

Static checks never execute plugin code. A real startup test is available, but you have to turn it on yourself, and it runs on a throw-away copy of the pack.

## Screenshots

| Validation | Servers |
|---|---|
| ![Validation results](docs/media/validation.png) | ![Servers](docs/media/servers-empty.png) |

| Settings | About |
|---|---|
| ![Settings](docs/media/settings.png) | ![About](docs/media/about.png) |

## Features

**Servers**
- Paper (PaperMC Fill API) and Vanilla (Mojang piston-meta) downloads, verified with SHA-256 / SHA-1 before use
- Detects installed Java runtimes and installs Eclipse Temurin from the Adoptium API (checksum verified)
- Explicit EULA consent: nothing is accepted on your behalf
- Live console with command history (↑/↓), log filter and colored warnings and errors; RAM and CPU readout with a memory sparkline
- The whole server process tree runs inside a Windows Job Object, so nothing keeps running after a crash or when you force-stop
- `server.properties` editor that keeps your comments and key order
- Plugin manager: enable or disable plugins, add JARs, search and install from Modrinth and Hangar
- Zip backups with retention, and restore with confirmation

**Validation**
- Pack structure, `server.properties` values, corrupt or unreadable JARs, oversized files
- Reads `plugin.yml` and `paper-plugin.yml` metadata; compatibility is never guessed from file names
- Required and optional dependencies, duplicates (same plugin name, or byte-identical files)
- `api-version` vs. server version, Java version vs. Minecraft version
- Results are `PASS`, `WARN`, `FAIL`, `SKIP` or `ERROR`. "Failed" and "could not run" are never the same thing
- **Pre-flight**: adding, enabling or disabling a plugin is validated first. If the change introduces new problems, you see them and can roll back
- Run history per pack, with a diff showing what changed and what broke since the previous run
- JSON (with a [schema](docs/schema/report.schema.json)) and self-contained HTML reports with a strict CSP

**App**
- English, Turkish, German, French and Russian, switchable instantly
- Dark and light themes (or follow Windows), with Full, Reduced or Off animations that also respect Windows' animation setting
- Updates from GitHub Releases (see below)
- No telemetry

## Updates

On every start, PackCheck asks GitHub for the latest release.

- If a newer version exists, a dialog shows its release notes. You can update now or postpone.
- You can postpone on the day the update is first announced and on the next day. **On the first launch of the third day, PackCheck installs the update by itself.**
- With **Settings → Updates → Install new releases automatically** turned on, new releases are installed at startup without asking.
- Packages are downloaded only from the repository's release assets and are rejected if their SHA-256 does not match the published `.sha256` file.
- Offline? The check is skipped silently and the app starts normally.

## Install

1. Download `PackCheck-win-x64.zip` from the [latest release](https://github.com/X1NPAR1/PackCheck/releases/latest).
2. Extract it anywhere you can write to (for example `D:\Apps\PackCheck`), then run `PackCheck.exe`.

The build is self-contained, so .NET does not need to be installed. Windows 10 or 11 (x64) is required. The binaries are not code-signed yet, so SmartScreen may warn on the first launch.

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
PackCheck 1.0.0 - demo-pack
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

## Build from source

```powershell
dotnet build PackCheck.sln
dotnet test tests/PackCheck.Tests
dotnet run --project src/PackCheck.App
```

Requires the .NET 10 SDK on Windows.

| Project | Responsibility |
|---|---|
| `PackCheck.Core` | Result model, versions, hashing, exit codes |
| `PackCheck.Validation` | Analyzers, safe zip extraction, metadata parsing |
| `PackCheck.Reporting` | JSON / HTML reports, run diff |
| `PackCheck.Servers` | Downloads, Java runtimes, process + Job Object, backups, runtime test |
| `PackCheck.Providers` | Modrinth and Hangar clients (timeouts, cache, failures as values) |
| `PackCheck.Updater` | GitHub release check, grace-period policy, verified install |
| `PackCheck.App` | WPF UI |
| `PackCheck.Cli` | Headless CLI |
| `PackCheck.UpdateHelper` | Replaces files after the app exits during an update |

## Known limitations

- Windows only.
- Paper and Vanilla only. Fabric, Forge and proxies are not supported yet.
- The runtime test isolates files, ports and memory, but plugin code still runs with your user account's rights. Read [docs/security.md](docs/security.md) before you use it on plugins you don't trust.
- Version checks rely on what plugins declare. A plugin that declares nothing gets a warning, not a guess.

## License

[MIT](LICENSE). Third-party components are listed in [THIRD_PARTY.md](THIRD_PARTY.md).

*Not an official Minecraft product. Not approved by or associated with Mojang or Microsoft.*
