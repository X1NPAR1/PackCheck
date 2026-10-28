# ServerCreator audit

PackCheck replaces the earlier Python project ServerCreator (private repository X1NPAR1/ServerCreator). This note records what was carried over, what was changed and why.

| ServerCreator module | PackCheck counterpart | Change |
|---|---|---|
| `core/downloader` | `PackCheck.Servers/Downloads.cs`, `MorePlatforms.cs` | Every download is checked against the hash the platform API publishes (SHA-256, else SHA-1/MD5). Version lists come only from each project's API, so versions without a real build are never offered. 18 platforms instead of 3. |
| `java_manager` | `JavaRuntimes.cs`, `JarJava.cs` | Java is chosen from the class file version of the server jar instead of a hard-coded table, so new Minecraft releases (Java 25) work without an update. Temurin JRE/JDK is downloaded per user. |
| `server_runtime` | `ServerProcess.cs`, `JobObject.cs`, `Telemetry.cs` | Servers run in a Windows Job Object, so closing PackCheck never leaves an orphaned Java process. Console lines are parsed (time, level, source), TPS is read with color codes and comma decimals stripped. |
| `version_manager` | `ServerRegistry.cs` (`ServerCreator`) | Installers (Forge, NeoForge, Fabric, Quilt, BuildTools) run in a temporary folder and only a finished server is registered. |
| `updater` | `PackCheck.Updater`, `PackCheck.UpdateHelper` | Signed-hash check of the release asset, installer-based update (EXE or MSI), three-day postpone rule, optional auto update. |
| (none) | `PackCheck.Validation` | New: static pack validation that never runs plugin code. |

## Problems found in ServerCreator and how PackCheck avoids them

- **Unverified downloads.** Files were saved as downloaded. PackCheck refuses a file whose hash does not match and deletes the partial file.
- **Hard-coded version lists** went stale. PackCheck asks the platform API every time and caches for 10 minutes.
- **EULA accepted silently.** PackCheck writes `eula=true` only after the user ticks the EULA box.
- **Java processes survived crashes** of the launcher. Fixed by the Job Object.
- **Blocking UI during downloads.** Everything network-bound is async with progress and cancellation.
- **No tests.** PackCheck has unit, network and end-to-end tests (see `docs/testing.md`).
