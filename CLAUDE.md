# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

JamFan22 is a live radar for the global [Jamulus](https://jamulus.io) network — a real-time online music jamming platform. The app shows who is playing on which servers worldwide, with social graphs, geolocation radar, and predictive arrival patterns. Tech stack: ASP.NET Core 9, SignalR, Vanilla JS/D3.js.

- **C# application:** see [JamFan22/CLAUDE.md](JamFan22/CLAUDE.md)
- **Ops scripts & diagnostics:** see [ops/CLAUDE.md](ops/CLAUDE.md)

## Build & Run

```bash
# Build
cd /root/JamFan22/JamFan22
dotnet build

# Test deployment (runs on port 5000, sandboxed in /tmp/jamfan-test-build)
kill $(lsof -t -i :5000) 2>/dev/null; cd /root/JamFan22 && ./ops/deploy-test-build.sh

# Production: managed as systemd service — do NOT use `dotnet run`
systemctl status jamfan22
systemctl restart jamfan22
```

**CRITICAL — Two instances run as dotnet processes.** Never kill dotnet processes by PID or `pkill`. Use only:
- `systemctl restart jamfan22` — for production (port 443). Use this to restart production after code changes — do NOT run `dotnet build` first; the service handles it.
- To stop the debug instance specifically: `kill $(lsof -t -i :5000)`

**When the user asks to restart production:** run `systemctl restart jamfan22` directly. Do not build first, do not use `deploy-test-build.sh`.

**Important:** Never read large `.json`, `.csv`, or `.log` data files without bounds — use `grep`/`jq` for efficient extraction.

## Infrastructure

nginx is not installed on this host. For routing/proxy needs use ASP.NET middleware or iptables.

## Memory

**Do NOT write to the auto-memory system.** The user does not want memories saved. Never create or update files in `~/.claude/projects/`. Put persistent guidance in CLAUDE.md or in the relevant system prompt file instead.

## Working with Claude Code

- **Edit size:** break file edits into ≤15–20 lines of new code per Edit call so diffs fit on screen.
- **Prune and chain:** after completing a task, prune its CLAUDE.md/TODO.md entry immediately. Then scan both files for closely related items and surface 1–2 as natural follow-ups — without waiting to be asked. The goal is a chain of small focused actions that steadily shortens both files. "Closely related" means same subsystem, same bug class, or same design concern — not a full project review.
- **Telemetry analysis — owner exclusions:** when running `user-awareness.py` or any visitor analysis, exclude these owner identifiers:
  - Hashes: `9dd8bae07c44800edd80024c02a0bbf6`, `8bfcb9816ab178394d56f6155cab4e73`, `52f7652674c02b2b9f1070c072881116`, `9d13bfe92e03` (Amber/Tacoma), `cfab1ba8c67c` (no-name/Tacoma)
  - IP prefixes: `134.19.`, `172.56.`
  - Exact IPs: `24.17.80.236`, `75.253.12.89`
- **Owner's home IP (Tacoma):** `24.17.80.236` — use this when investigating what the owner sees in the UI (e.g. `/api/nearby`, geo behavior)

## TODO

See [TODO.md](TODO.md).
