# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Security / Committing

**This is a PUBLIC repository.** Before every commit, scan staged files for:
- Server IPs, hostnames, or domain names
- GUIDs, API keys, tokens, or secrets
- Deploy configs, service credentials, or `.pfx`/`.pem`/`.env` files
- Any file not clearly application source (e.g. `*.pfx`, `*.bak`, generated reports)

Never commit infra config files without explicit confirmation from the user.

## Project Overview

JamFan22 is a live radar for the global [Jamulus](https://jamulus.io) network — a real-time online music jamming platform. The app shows who is playing on which servers worldwide, with social graphs, geolocation radar, and predictive arrival patterns. Tech stack: ASP.NET Core 9, SignalR, Vanilla JS/D3.js.

- **C# application:** see [JamFan22/CLAUDE.md](JamFan22/CLAUDE.md)
- **Ops scripts & diagnostics:** see [ops/CLAUDE.md](ops/CLAUDE.md)
- **"How many humans on the web app right now?"** — don't parse telemetry.log by hand; run `python3 /root/JamFan22/ops/traffic.py` (default 60-min window, pass minutes as arg for a different window). See "Live User Count — Tier Classification" in [ops/CLAUDE.md](ops/CLAUDE.md) for what the tiers mean.

## Build & Run

```bash
# Build
cd /root/JamFan22/JamFan22
dotnet build
```

**`dotnet` path**: `/root/.dotnet/dotnet` — not in `$PATH` in non-interactive SSH sessions. The commands above work in an interactive shell; for `ssh host 'command'` invocations, use the full path explicitly.

```bash
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

nginx is installed but **stopped and disabled** (2026-08-10 — operator wanted it off, it had no purpose besides serving certbot's authenticator). Cert renewal now uses certbot's **standalone** authenticator instead (binds :80 itself only during the renewal handshake, then releases it) — `/etc/letsencrypt/renewal/jamulus.live.conf` has `authenticator = standalone`, dry-run verified. Do not re-enable nginx for renewal; if it's ever started for another reason, remember it will hold port 80 and a concurrent standalone renewal will fail to bind. A certbot deploy hook (`/etc/letsencrypt/renewal-hooks/deploy/make-pfx.sh`) regenerates `JamFan22/key-current.pfx` (the fixed-name pfx Program.cs loads) after every renewal; the service picks it up at its next restart. For app routing/proxy needs use ASP.NET middleware or iptables.

### OOM Protection

`user-0.slice` is capped at 4 GB (`systemctl set-property user-0.slice MemoryMax=4G`, persisted in `/etc/systemd/system.control/user-0.slice.d/50-MemoryMax.conf`). This limits all interactive/tmux processes (including ops scripts run via Claude Code) to 4 GB, leaving the other 4 GB safe for `jamfan22`. A runaway script gets OOM-killed before it can starve the production service.

**Forensics after an OOM kill:** `auditd` and `acct` are installed and running.
- Full command line (with script path): `ausearch -k exec_log --start HH:MM:SS --end HH:MM:SS`
- Quick recent command list: `lastcomm --user root | head -40`
- Kernel OOM details (victim PID, cgroup, RSS): `journalctl -k | grep -i "oom\|killed process"`

The killed process runs in a cgroup like `tmux-spawn-*.scope` (Claude Code tmux session) — visible in the kernel log but the script path requires `auditd`.

**Directory server population metadata chain:** JamFan22 (this host, `134.199.209.51`, hostname `jamfan25`) gets directory server population metadata from the **ping harvester** (`24.199.107.192`, hostname `harvest-pings`, aka "jamfan26" — runs `gather-server-data.py`, Flask cache on :5001), which in turn gets it from the **sampler** (`137.184.43.255`, `jamulus-sampler` on :5001 — does the actual directory sweeps). SSH trust is one-way down the chain: this host → harvester → sampler; the sampler cannot reach back upstream.

## Memory

**Do NOT write to the auto-memory system.** The user does not want memories saved. Never create or update files in `~/.claude/projects/`. Put persistent guidance in CLAUDE.md or in the relevant system prompt file instead.

## Working with Claude Code

- **Edit size:** break file edits into ≤15–20 lines of new code per Edit call so diffs fit on screen.
- **Dormant server table:** in the on/off state column, always render `RUNNING` in ALL CAPS and `stopped` in lowercase.
- **Prune and chain:** after completing a task, prune its CLAUDE.md/TODO.md entry immediately. Then scan both files for closely related items and surface 1–2 as natural follow-ups — without waiting to be asked. The goal is a chain of small focused actions that steadily shortens both files. "Closely related" means same subsystem, same bug class, or same design concern — not a full project review.
- **Describing session data:** never speculate on session start/stop times or durations. Only state what the data directly shows (e.g. visitor counts). Keep it minimal and vague — "a few visitors" not "a rolling session that ran through the night." Imagery is only accurate when it's minimal.
- **Telemetry analysis — owner exclusions:** when running `user-awareness.py` or any visitor analysis, exclude these owner identifiers:
  - Hashes: `9dd8bae07c44800edd80024c02a0bbf6`, `8bfcb9816ab178394d56f6155cab4e73`, `52f7652674c02b2b9f1070c072881116`, `9d13bfe92e03` (Amber/Tacoma)
  - IP prefixes: `134.19.`, `172.56.`
  - Exact IPs: `24.17.80.236`, `75.253.12.89`
- **Owner's home IP (Tacoma):** `24.17.80.236` — use this when investigating what the owner sees in the UI (e.g. `/api/nearby`, geo behavior)

## TODO

See [TODO.md](TODO.md).
