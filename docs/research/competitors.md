# Other server tools

Compiled from the projects' public documentation, October 2026. Feature lists change; this is a positioning note, not a benchmark.

| Tool | Form | Strengths | Gaps PackCheck targets |
|---|---|---|---|
| MCSS (Minecraft Server Software) | Windows desktop | Simple local server creation | No pack validation; limited mod platform coverage |
| Crafty Controller | Self-hosted web panel (Python) | Multi-user, schedules, backups | Needs a web server and browser; more setup than a desktop app |
| MCSManager | Self-hosted web panel (Node.js) | Multi-node, many games | Aimed at hosting providers; no plugin/mod compatibility checks |
| Pterodactyl / Pelican | Web panel + Docker daemon | Industry standard for hosts, isolation | Linux and Docker required; overkill for a home server |
| PufferPanel | Self-hosted web panel (Go) | Lightweight, templates | No content browser with compatibility reasons |
| AMP (CubeCoders) | Commercial panel | Polished, many games | Paid license |

## What PackCheck does differently

1. **Validation before start**: missing dependencies, wrong Minecraft versions, duplicates, corrupt jars and client-only mods are found statically with evidence and a suggested fix.
2. **Compatibility in the browser**: incompatible plugins and mods are shown with the exact reason instead of failing after download.
3. **Desktop first**: no web server, per-user install without admin rights, tray icon, Windows firewall/UPnP handled with one prompt.
4. **Assistant with approvals**: reads console and files, and every change waits for the user.
