# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

JamFan22 is a live radar for the global [Jamulus](https://jamulus.io) network — a real-time online music jamming platform. The app shows who is playing on which servers worldwide, with social graphs, geolocation radar, and predictive arrival patterns. Tech stack: ASP.NET Core 9, SignalR, Vanilla JS/D3.js.

## Build & Run

```bash
# Build
cd /root/JamFan22/JamFan22
dotnet build

# Test deployment (runs on port 5000, sandboxed in /tmp/jamfan-test-build)
cd /root/JamFan22
./deploy-test-build.sh

# Production: managed as systemd service — do NOT use `dotnet run`
systemctl status jamfan22
systemctl restart jamfan22
```

**CRITICAL — Two instances run as dotnet processes.** Never kill dotnet processes by PID or `pkill`. Use only:
- `systemctl restart jamfan22` — for production (port 443)
- `deploy-test-build.sh` kills and relaunches the debug instance (port 5000) itself — let it handle that

To stop the debug instance specifically: `kill $(lsof -t -i :5000)`

**Important:** Never read large `.json`, `.csv`, or `.log` data files without bounds — use `grep`/`jq` for efficient extraction.

## Architecture

### Service Layer (`Services/`)

All business logic lives in `Services/`. Dependency order (register dependencies first):

```
EncounterTracker  (no deps)
JamulusCacheManager  → EncounterTracker
IpAnalyticsService  (no deps)
GeolocationService  (no deps)
JamulusAnalyzer  → JamulusCacheManager, EncounterTracker, IpAnalyticsService, GeolocationService
```

All services are singletons. Two hosted background services (`JamulusListRefreshService`, `JammerHarvestService`) drive the polling loop.

**Service responsibilities:**
- **EncounterTracker** — user hashing (`GetHash`), session tracking, time-together persistence. Static fields for shared app state.
- **JamulusCacheManager** — background Jamulus server list polling, `LastReportedList`, `JamulusListURLs`. Static helpers: `MinutesSince2023AsInt()`, `IsDebuggingOnWindows`.
- **IpAnalyticsService** — IP→ASN lookups, rate-limit backoff, SmartNations, country code cache. Reads `wwwroot/asn-ip-client-blocks.txt` for the active blocklist. (`wwwroot/asn-blocks.txt` is **deprecated** and no longer maintained — ignore it.)
- **GeolocationService** — IP→lat/lon via OpenCage, distance calculations. `m_ipAddrToLatLong` is a static cache.
- **JamulusAnalyzer** — orchestrates all above. Server list processing pipeline, preloaded data cache, IP→GUID resolution, user stats. Owns static `m_connectedLounges` (used by `harvest.cs`).

### Non-DI Code Accessing Statics

Some files access services via static fields rather than DI:
- `harvest.cs` → `JamulusAnalyzer.m_connectedLounges`, `JamulusCacheManager.MinutesSince2023AsInt()`, `JamulusCacheManager.IsDebuggingOnWindows`
- `nearby.cs` → `JamulusCacheManager.JamulusListURLs`, `JamulusCacheManager.LastReportedList`
- `login.cshtml.cs` → `JamulusCacheManager.LastReportedList`, `EncounterTracker.GetHash()`
- `Program.cs` (hotties route) → same pattern

### Page Models (`Pages/`)

Thin page models — no business logic:
- `IndexModel` — injects `JamulusAnalyzer` only
- `ApiModel` — injects all 5 services; has per-request `m_TwoLetterNationCode`
- `ClientModel` — injects `JamulusCacheManager`

### API Routes (defined in `Program.cs`)

- `GET /countries` — diagnostics: unique IPs by country
- `GET /api/nearby` — HTML for nearby Jamulus servers based on client IP
- `GET /hotties/{encodedGuid}` — co-jammers ranked by time spent together
- `GET /halos/` — streaming/snippeting halo server list

### Data Directory (`data/`)

Persistent append-only flat files written by the harvester and read back at startup:

| File | Schema | Purpose |
|------|--------|---------|
| `census.csv` | `minutes, server_hash, ip:port` | Every observed jammer+server presence tick |
| `censusgeo.csv` | `hash, name, instrument, city, nation` | Jammer profile snapshots |
| `server.csv` | `ip:port, name, city, nation` | Known server metadata |
| `urls.csv` | `minutes, ip:port, encoded_url` | URLs shared in server chat |
| `telemetry.log` | `minutes, ip, hash, flags, reserved, json` | Web client events (session start/end, actions) |
| `stream-gate.json` | `{ActiveIp, ExpiryUtc}` | Single-slot streaming lease (see StreamGate) |
| `stream-requests.json` | — | Queued stream slot requests |

**`ping-log.csv`** lives in `JamFan22/` (alongside `join-events.csv`, not in `data/`). Schema: `minute, ip, origin_port, dest_port, status`. Synced every 10 min from harvest-pings via cron (full overwrite). ~120MB — never read without bounds, use `grep`/`awk`. Used by `prospect-radar.py`.

Trim scripts deduplicate and prune each CSV to a rolling 90-day window, run via cron:
- `trim_census.sh` / `trim_server.sh` — weekly
- `trim_censusgeo.sh` — daily

### Other Scheduled Scripts (cron)

- **`predict-future.py`** — runs multiple times daily; reads `census.csv` + `censusgeo.csv`, writes `tooltips.json` and related prediction outputs to `wwwroot/`.
- **`jammer-map.py`** — runs every 2 hours; ETL over `census.csv` + `censusgeo.csv`, writes `wwwroot/jammer-map.json` (traveler map with per-country stats).
- **`cull-data.sh`** — runs daily; trims `data/telemetry.log` to the last 15 days.
- **`user-awareness.py`** — manual analysis tool (not cron); correlates `data/telemetry.log` with `data/census.csv` to build behavioural profiles of website visitors. Run from repo root: `python3 user-awareness.py [--hash <musicianHash>] [--ip <clientIP>]`. Without flags, prints all visitors ranked by engagement. **Note:** the summary table truncates hashes to 12 chars; `--hash` requires the full 32-char hash. Extract it via `grep -m1 ',<prefix>' data/telemetry.log | cut -d',' -f3`.
- **`gcdump-monitor.sh`** — runs every 2 hours; collects a `.gcdump` heap snapshot of the running process into `heapdumps/`, keeping the last 12.
- **`sense-drops.py`** — manual analysis tool (not cron); reads `data/census.csv` and reports dropout gaps in the minute sequence with UTC timestamps. Run from repo root: `python3 sense-drops.py`. Use this first when investigating census coverage gaps.
- **`prospect-radar.py`** — manual analysis tool (not cron); identifies Jamulus musicians who are connected to a public directory right now AND have previously shown a "7-slot sweep" pattern (hitting 6+ distinct server ports in one minute — a sign of manual server searching). Cross-references against `data/telemetry.log` to flag whether they've visited JamFan22 yet. Run from repo root: `python3 prospect-radar.py`. Reads `ping-log.csv` (synced every 10 min from harvest-pings) and live directory endpoints.

### Diagnosing Census Gaps

If `sense-drops.py` reports gaps in `census.csv`, check `output.log` for these markers:

- **`[RSS-bg] <timestamp> <rss>`** — printed at the end of each successful RefreshThreadTask census-write cycle. Absence of this marker for a stretch of lines = RefreshThreadTask was not completing. Grep: `grep '\[RSS-bg\]' output.log | tail -20`
- **`[WARN] Slow fetch`** — printed when all Jamulus URL fetches together exceed 5 seconds.
- **`[WARN] RefreshThreadTask: HTTP timeout/cancel`** — printed when an HttpClient timeout caused the task loop to restart (distinguished from service shutdown).
- **`RefreshThreadTask restarted (count: N)`** — logged by `JamulusListRefreshService` whenever `RefreshThreadTask` returns unexpectedly.

Root cause of the April 2026 gap: `TaskCanceledException` (HttpClient timeout) was silently caught by `catch (OperationCanceledException) { break; }`, causing the task to exit without logging. Now split into two catch clauses.

### Key Design Notes

- `EncounterTracker.DetailsFromHash()` takes `jamulusListURLs` + `lastReportedList` as params (avoids circular dep with `JamulusCacheManager`)
- `JamulusAnalyzer.LocalizedText(nationCode, ...)` is static, takes nation code as first param
- `DurationHere(server, who, nationCode)` takes nation code as param — no per-request singleton state
- Port is read from `PORT` env var; defaults to 443 with HTTPS (`keyJan26.pfx`); non-443 runs plain HTTP (used for test builds on port 5000)
