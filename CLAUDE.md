# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Security / Committing

**This is a PUBLIC repository.** Before every commit, scan staged files for:
- Server IPs, hostnames, or domain names
- GUIDs, API keys, tokens, or secrets
- Deploy configs, service credentials, or `.pfx`/`.pem`/`.env` files
- Any file not clearly application source (e.g. `*.pfx`, `*.bak`, generated reports)

Never commit infra config files without explicit confirmation from the user.

**Pushing:** this host holds no working GitHub credential and never needs one. Commit here, then push
from central command, which fetches this working tree over SSH and pushes with its own logged-in `gh`
(`git fetch root@jamulus.live:/root/JamFan22 main`, fast-forward check, `git push origin FETCH_HEAD:refs/heads/main`,
then `git update-ref refs/remotes/origin/main` here). Never write a token into the remote URL — the one
that was there sat world-readable in `.git/config` until it expired (2026-09-29). Player-IP files
(`data/*.csv`, `data/essay-you.log`, `data/band-invite-log.json`) are never tracked and never in a backup glob.

## Project Overview

JamFan22 is a live radar for the global [Jamulus](https://jamulus.io) network — a real-time online music jamming platform. The app shows who is playing on which servers worldwide, with social graphs, geolocation radar, and predictive arrival patterns. Tech stack: ASP.NET Core 9, SignalR, Vanilla JS/D3.js.

- **C# application:** see [JamFan22/CLAUDE.md](JamFan22/CLAUDE.md)
- **Ops scripts & diagnostics:** see [ops/CLAUDE.md](ops/CLAUDE.md)
- **Gate verdicts (`/ip-allowed`) are in `data/gate.csv`** (live since 2026-09-26; one row per poll: `epochmin,caller,query,guid,verdict`, epoch minutes since 2023-01-01 UTC) and `data/gate-history.csv` (everything before, extracted once). **Never grep `output.log` for `[IP-ALLOWED]` lines** (operator 2026-09-26). Schema: `/root/jamfan-command/SCHEMAS.md`.
- **"How many humans on the web app right now?"** — don't parse telemetry.log by hand; run `python3 /root/JamFan22/ops/traffic.py` (default 60-min window, pass minutes as arg for a different window). See "Live User Count — Tier Classification" in [ops/CLAUDE.md](ops/CLAUDE.md) for what the tiers mean.

## Build & Run

```bash
# Build
cd /root/JamFan22/JamFan22
dotnet build
```

**`dotnet` path**: `/root/.dotnet/dotnet` — not in `$PATH` in non-interactive SSH sessions. The commands above work in an interactive shell; for `ssh host 'command'` invocations, use the full path explicitly.

```bash
# Test deployment (Release publish, port 5000, sandboxed in /tmp/jamfan-test-build with copies of the data files)
kill $(lsof -t -i :5000) 2>/dev/null; cd /root/JamFan22 && ./ops/deploy-test-build.sh

# Production: systemd runs the pinned publish in releases/current — do NOT run `dotnet run` yourself
systemctl status jamfan22
ops/release.sh                # publish HEAD + flip current; restarts nothing
systemctl restart jamfan22    # restarts whatever current points at (see below)
```

**What production actually runs (since 2026-10-02, FOLLOW-UP 670):** a pinned **Release publish**, not the
working tree. The drop-in `/etc/systemd/system/jamfan22.service.d/10-release.conf` replaces `dotnet run` with
`ExecStart=/root/.dotnet/dotnet /root/JamFan22/releases/current/JamFan22.dll`; `WorkingDirectory` is still
`JamFan22/`, so `data/`, `wwwroot/` and `key-current.pfx` resolve from the tree as before. Its `ExecStartPre`
cats `releases/current/RELEASE` (sha, dirty hash, build time, dll hash) into `output.log`, so every start
records which bytes came up. `ASPNETCORE_ENVIRONMENT=Development` is still set there (developer exception
page, no HSTS) — the flip to Production is FU670 step 4. Rollback of the whole scheme: `rm` the drop-in,
`systemctl daemon-reload`, restart.

**Consequences:** `systemctl restart jamfan22` restarts the SAME bytes — a plain restart never picks up a code
change, and the service compiles nothing. Conversely, once `current` is flipped, any start (a crash under
`Restart=always`, a reboot) brings the new bytes up whether or not anyone restarted on purpose. **To tell
whether an edit is live:** `readlink releases/current; cat releases/current/RELEASE` — the `sha=` must be at
or after the commit, `dirty=clean` (a `-dirty-<hash>` release was published from uncommitted edits). The
2026-10-15 group-welcome fix (`55666e6`) sat built-but-not-live because the old text said a restart would ship it.

**Shipping a code change to production** (the operator's call; after 0:00 UTC when possible):
1. Commit (`release.sh` tags the release with the HEAD sha; uncommitted edits make it `-dirty-<hash>`).
2. `cd /root/JamFan22 && ops/release.sh` — `dotnet publish -c Release` into
   `releases/<utc>-<sha>[-dirty-<hash>]/`, writes `RELEASE`, flips `current` atomically. **Restarts nothing.**
   On failure it exits non-zero and `current` is untouched.
3. `systemctl restart jamfan22`, then `tail -c 50000000 JamFan22/output.log | grep -a '^sha=' | tail -1` shows the new sha.
4. Rollback: `ln -sfn /root/JamFan22/releases/<previous-dir> releases/current && systemctl restart jamfan22`.

**CRITICAL — Two instances run as dotnet processes.** Never kill dotnet processes by PID or `pkill`. Use only:
- `systemctl restart jamfan22` — production (port 443); runs whatever `releases/current` points at.
- `kill $(lsof -t -i :5000)` — stops the debug instance (`ops/deploy-test-build.sh`, its own publish in `/tmp/jamfan-test-build`).

**When the operator asks to ship a change:** steps 1–3 above. **When asked only to restart:** `systemctl restart
jamfan22` — say whether `current` is behind HEAD, because a restart alone ships nothing new. `dotnet build` in
the tree is a compile check only; production never sees it. Production restarts are the operator's call
(build → test on :5000 → operator verifies → release + restart on request).

**Important:** Never read large `.json`, `.csv`, or `.log` data files without bounds — use `grep`/`jq`/`tail -c` for efficient extraction. `output.log` is 11 GB; `tail -c 300000000 output.log | grep -a …` is the shape.

## Infrastructure

nginx is installed but **stopped and disabled** (2026-08-10 — operator wanted it off, it had no purpose besides serving certbot's authenticator). Cert renewal now uses certbot's **standalone** authenticator instead (binds :80 itself only during the renewal handshake, then releases it) — `/etc/letsencrypt/renewal/jamulus.live.conf` has `authenticator = standalone`, dry-run verified. Do not re-enable nginx for renewal; if it's ever started for another reason, remember it will hold port 80 and a concurrent standalone renewal will fail to bind. A certbot deploy hook (`/etc/letsencrypt/renewal-hooks/deploy/make-pfx.sh`) regenerates `JamFan22/key-current.pfx` (the fixed-name pfx Program.cs loads) after every renewal; the service picks it up at its next restart. For app routing/proxy needs use ASP.NET middleware or iptables.

### Memory and OOM protection

The droplet is `s-2vcpu-8gb-amd`. Measured 2026-09-29: production JamFan22 601–963 MB RSS (p50 813 MB over
400 heap-dump samples), its `dotnet run` parent 224 MB, the :5000 test instance ~1.2 GB when up, everything
else under 0.4 GB; the box peaked at 2.48 GB of 7.9 GB over nine days, CPU 4–11%. A downsize to 4 GB is
planned (`/root/jamfan-command/TODO.md` FOLLOW-UP 669). **These caps assume 8 GB and must be re-set with
any resize:** `user-0.slice` `MemoryMax=4G` (persisted in `/etc/systemd/system.control/user-0.slice.d/50-MemoryMax.conf`;
limits all interactive/tmux processes, including ops scripts run via Claude Code, so a runaway script is
OOM-killed before it starves production) and `jamfan22.service` `MemoryHigh=5G` / `MemoryMax=7G`.
`ops/gcdump-monitor.sh` (cron, every 2 h) snapshots both instances' heaps into `heapdumps/` and logs each RSS.

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
- **Telemetry analysis — owner exclusions:** `ops/traffic.py`'s `EXCLUDE_IPS` / `EXCLUDE_PREFIXES` / `EXCLUDE_HASHES` is the ONE list of owner identifiers (home and phone IPs, browser hashes). Any visitor analysis — `user-awareness.py`, an ad-hoc script — imports or copies that list; never retype a subset here or elsewhere (it drifted: this file listed fewer entries than the script until 2026-09-29).
- **"What does the owner see?"** — the owner's home IP (Tacoma) is `24.17.80.236` (also in `EXCLUDE_IPS`); use it as `X-Forwarded-For` when investigating `/api/nearby`, geo behavior or the essay from the owner's vantage.

## TODO

See [TODO.md](TODO.md).
