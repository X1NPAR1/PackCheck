# Common problems of people running a server at home

Collected from the questions that come up again and again in server help channels and forums. Each row shows where PackCheck answers it.

| Problem | Typical symptom | PackCheck |
|---|---|---|
| Wrong Java version | `UnsupportedClassVersionError`, server closes at once | Java picked from the jar's class version; correct Temurin downloaded automatically |
| Plugin for another Minecraft version | `api-version` errors, plugin disabled on load | Validation PC050 and the browser's incompatible badge with the reason |
| Missing dependency | "Unknown dependency Vault" | PC040 / PC064 with the missing plugin name |
| Two copies of a plugin | "Ambiguous plugin name" | PC030 / PC063 |
| Corrupt or half-downloaded jar | `ZipException` on start | PC020 / PC060, verified downloads |
| Client-only mod on a server | Crash with missing client classes | PC066 |
| Friends cannot join | Firewall or router blocks the port | Online page: firewall rule + UPnP + address to share, and a way to close it again |
| Crash with a long log | Not knowing which line matters | Crash banner with the last error, latest.log button, "Ask the assistant" |
| Lag | Low TPS, high RAM | Dashboard with TPS, CPU, RAM and ping gauges and history |
| Editing configs safely | Broken YAML after a manual edit | Editor checks YAML/JSON/TOML/properties syntax before saving and keeps a backup |
