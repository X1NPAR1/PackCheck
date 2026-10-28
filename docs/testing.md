# Testing

```powershell
dotnet test tests/PackCheck.Tests --filter "Category!=Network&Category!=EndToEnd"   # unit + integration, what CI runs
dotnet test tests/PackCheck.Tests --filter "Category=Network"                         # live APIs (PaperMC, Modrinth, Hangar, ...)
$env:PACKCHECK_E2E_DIR = "D:\MCSERVER"; dotnet test tests/PackCheck.Tests --filter "Category=EndToEnd"   # real servers
dotnet build src/PackCheck.App; dotnet test tests/PackCheck.UiTests                                  # UI (needs a desktop session)
```

## What is covered

| Area | How |
|---|---|
| Validation (plugins and mods), reports, schema, XSS | Unit tests on generated packs |
| Zip safety (path traversal, zip bombs) | Unit tests |
| Updates: 3-day grace period, auto update, installer asset choice, offline | Full `UpdateService` with a movable clock and a stubbed GitHub API |
| CurseForge | Recorded-style responses (no API key was available for live tests) |
| Platform version lists and downloads (16 platforms, newest and oldest version) | Live APIs |
| Server creation, start, `list`, status ping, TPS, stop | Real servers, see below |
| Proxy | Generated configs parsed as TOML/YAML; real Velocity in front of Paper |
| Assistant | Tool loop against a stubbed OpenAI-compatible provider, approval gate, path confinement |
| UI | FlaUI tests in `tests/PackCheck.UiTests` (navigation, validation with findings and inventory, settings search, assistant providers, About), plus `tools/shoot.ps1` / `tools/record.ps1` for screenshots |

## End-to-end results (28 Oct 2026, Windows 10, Minecraft 26.3)

Each server was created by PackCheck's own code in `D:\MCSERVER\pc-e2e-*`, started, queried and stopped; the folder was deleted afterwards.

| Platform | Java | Ready after | Status ping | TPS | Stop |
|---|---|---|---|---|---|
| Paper | 25 | 15 s | ✅ | 20.0 | ✅ |
| Purpur | 25 | 17 s | ✅ | 20.0 | ✅ |
| Folia | 25 | 15 s | ✅ | n/a (region report) | ✅ |
| Spigot (BuildTools, 7.5 min build) | 25 JDK | 11 s | ✅ | 20.0 | ✅ |
| Vanilla | 25 | 9 s | ✅ | n/a | ✅ |
| Fabric | 25 | 17 s | ✅ | n/a without spark | ✅ |
| Quilt | 25 | 13 s | ✅ | n/a without spark | ✅ |
| Forge | 25 | 10 s | ✅ | 20.0 | ✅ |
| NeoForge | 25 | 10 s | ✅ | 20.0 | ✅ |
| Mohist (1.20.2) | 17 | 77 s | ✅ | 20.0 | ✅ |
| Velocity + Paper backend | 25 | – | ✅ through the proxy | – | ✅ |

Problems these runs found and that were fixed: TPS lines with ANSI colors and comma decimals, an outdated Quilt installer hash in Quilt's meta API, BuildTools needing a JDK, read-only git files blocking cleanup, the status query being too early right after "Done", and Velocity 4 needing Java 25.

Not tested end to end: CraftBukkit, Leaf, Leaves, SpongeVanilla, Youer, Arclight and BungeeCord (their downloads are verified by the network tests), and opening a server to the internet (needs the user to approve the Windows administrator prompt).
