# Performance

Measured on 29 Oct 2026 on the development machine (Windows 10 Pro 19045, self-contained Release build, win-x64). Each number is from three runs; the first run after a build is reported separately because Windows has not cached the files yet.

## App start

| | First run | Following runs |
|---|---|---|
| Splash visible | 4.1 s | 0.7 s |
| Main window ready (splash finished all steps, including the update check) | 12.1 s | 3.0–3.3 s |
| Memory after start (working set / private) | 240 MB / 185 MB | 240 MB / 186 MB |

Most of the time behind the splash is the GitHub update check and the Java runtime scan. The splash shows the real step.

## Validation (CLI)

| Pack | Time (process start to exit) | Analysis time in report |
|---|---|---|
| 4 jars + server.properties | 0.20 s (1.0 s first run) | < 0.1 s |
| 200 jars, 30 MB, dependency chain | 0.32 s | 0.20 s |

## Console

The console keeps the last 10 000 lines and uses a virtualizing list, so long sessions do not grow memory without bound. Search highlighting and player links are built per visible line only.

## How to reproduce

```powershell
dotnet publish src/PackCheck.App -c Release -r win-x64 --self-contained -o artifacts/perf
dotnet publish src/PackCheck.Cli -c Release -r win-x64 --self-contained -o artifacts/perfcli
# start time: launch artifacts/perf/PackCheck.exe and wait for the window with AutomationId "MainWindow" (UI Automation)
Measure-Command { artifacts/perfcli/packcheck.exe validate <pack> --format json --output out.json }
```
