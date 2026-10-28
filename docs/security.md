# Security model

Plugins are programs. PackCheck's default mode (static analysis) only reads files. It never loads classes or runs Java.

## Threats and mitigations

| Threat | Mitigation | Residual risk |
|---|---|---|
| Malicious JAR executed during validation | Static analysis opens JARs as zip archives only; nothing is loaded or executed | None for static mode |
| Malicious plugin during the opt-in runtime test | Runs on a temporary copy; bound to `127.0.0.1` on a random port; RCON and query off; sanitized environment (no user tokens or variables); Windows Job Object with a memory cap, a process limit and kill-on-close; time limit; temp folder deleted afterwards | **Plugin code still runs with your user's file-system and network rights.** Windows offers no unprivileged way to fully sandbox it. Only use the runtime test on plugins you trust, or inside a VM |
| Path traversal in zip packs (`../`) | Every entry path is resolved and must stay inside the extraction folder | — |
| Zip bombs / resource exhaustion | Entry count limit (50,000), total size limit (4 GB), compression-ratio check, per-file size limit for inspection (256 MB) | Large but legitimate packs may be refused |
| Malicious YAML (type tags) | YamlDotNet representation model only: no object construction from tags; descriptors larger than 512 KB are skipped | — |
| Tampered downloads | HTTPS only; SHA-256 (Paper, Temurin, Hangar), SHA-512 (Modrinth) or SHA-1 (Mojang, the only hash Mojang publishes) checked before the file is moved into place; size limits | SHA-1 is weaker than SHA-256 but is what the official source provides |
| Tampered update package | Only assets on `github.com` from this repository's latest release; SHA-256 must match the published `.sha256`; the package must contain `PackCheck.exe` | A compromised GitHub account could publish a malicious release. Code signing is planned |
| XSS in the HTML report | All values are HTML-encoded; `Content-Security-Policy: default-src 'none'`; no scripts and no remote resources | — |
| Shell injection | All processes start with `ProcessStartInfo.ArgumentList`; no shell strings | — |
| Hanging server processes | Job Object kills the whole tree; graceful `stop` with a timeout, then a forced kill | — |
| Provider outages or bad responses | Timeouts, results returned as values, unexpected JSON shapes reported as errors | Search results may be incomplete |
| CI secret leakage | Workflows use least-privilege `permissions`, actions pinned by commit SHA, no secrets in PR builds | — |
