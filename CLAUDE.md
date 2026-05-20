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
- `systemctl restart jamfan22` — for production (port 443). Use this to restart production after code changes — do NOT run `dotnet build` first; the service handles it.
- `deploy-test-build.sh` does **not** kill the old debug instance — always kill port 5000 first, then run the script:
  ```bash
  kill $(lsof -t -i :5000) 2>/dev/null; cd /root/JamFan22 && ./deploy-test-build.sh
  ```

To stop the debug instance specifically: `kill $(lsof -t -i :5000)`

**When the user asks to restart production:** run `systemctl restart jamfan22` directly. Do not build first, do not use `deploy-test-build.sh`.

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
- **JamulusCacheManager** — background Jamulus server list polling, `LastReportedList`, `JamulusListURLs`. Static helpers: `MinutesSince2023AsInt()`, `IsDebuggingOnWindows`. Also runs alt-source round-robin poll for blocked servers.
- **IpAnalyticsService** — IP→ASN lookups, rate-limit backoff, SmartNations, country code cache. Reads `wwwroot/asn-ip-client-blocks.txt` for the active blocklist. (`wwwroot/asn-blocks.txt` is **deprecated** — ignore it.)
- **GeolocationService** — IP→lat/lon via OpenCage, distance calculations. `m_ipAddrToLatLong` is a static cache.
- **JamulusAnalyzer** — orchestrates all above. Server list processing pipeline, preloaded data cache, IP→GUID resolution, user stats. Owns static `m_connectedLounges` (used by `harvest.cs`).

### Non-DI Code Accessing Statics

- `harvest.cs` → `JamulusAnalyzer.m_connectedLounges`, `JamulusCacheManager.MinutesSince2023AsInt()`, `JamulusCacheManager.IsDebuggingOnWindows`
- `nearby.cs` → `JamulusCacheManager.JamulusListURLs`, `JamulusCacheManager.LastReportedList`
- `login.cshtml.cs` → `JamulusCacheManager.LastReportedList`, `EncounterTracker.GetHash()`
- `Program.cs` (hotties route) → same pattern

### Page Models (`Pages/`)

- `IndexModel` — injects `JamulusAnalyzer` only
- `ApiModel` — injects all 5 services; has per-request `m_TwoLetterNationCode`
- `ClientModel` — injects `JamulusCacheManager`

### API Routes (defined in `Program.cs`)

- `GET /countries` — diagnostics: unique IPs by country
- `GET /api/nearby` — HTML for nearby Jamulus servers based on client IP
- `GET /api/geo-diag` — corroboration model diagnostic table for live T1 musicians
- `GET /hotties/{encodedGuid}` — co-jammers ranked by time spent together
- `GET /halos/` — streaming/snippeting halo server list
- `POST /chat-url-server` — fleet servers submit chat URLs; gated to `data/fleet-server-ips.txt`
- `POST /chat-url` — web clients submit chat URLs; gated by `IsIpAllowedAsync`
- `POST /chat-command-server` — fleet servers request stream slot; intentionally ungated (TryRequestStream has its own quality gate)

### Data Directory (`data/`)

| File | Schema | Purpose |
|------|--------|---------|
| `census.csv` | `minutes, server_hash, ip:port` | Every observed jammer+server presence tick |
| `censusgeo.csv` | `hash, name, instrument, city, nation` | Jammer profile snapshots |
| `server.csv` | `ip:port, name, city, nation` | Known server metadata |
| `urls.csv` | `minutes, source, ip:port, encoded_url, title` | URLs matching chat-patterns.txt (source = lounge/server/client) |
| `urls-rejected.csv` | `minutes, source, addr, encoded_url` | URLs that failed chat-patterns.txt — review to find missing patterns |
| `telemetry.log` | `minutes, ip, hash, flags, reserved, json` | Web client events |
| `stream-gate.json` | `{ActiveIp, ExpiryUtc, JamulusServer}` | Single-slot streaming lease |
| `stream-reservations.json` | `[{Ip, JamulusServer, DayOfWeek, StartHour, DurationHours}]` | Weekly recurring stream reservations |
| `fleet-guid-ip.csv` | `timestamp_minutes, guid, client_ip, server_ip, blocked` | GUID↔IP evidence from fleet /ip-allowed calls |
| `fleet-server-ips.txt` | one IP per line, `#` = comment | Allowlist for /chat-url-server |

**`ping-log.csv`** lives in `JamFan22/` (not in `data/`). ~120MB — never read without bounds. Used by `prospect-radar.py`.

Trim scripts run via cron: `trim_census.sh` / `trim_server.sh` (weekly), `trim_censusgeo.sh` (daily), `cull-data.sh` (daily — telemetry.log, fleet-guid-ip.csv 15d; urls.csv 90d; urls-rejected.csv 15d).

### Other Scheduled Scripts (cron)

- **`predict-future.py`** — runs multiple times daily; writes `tooltips.json` and prediction outputs to `wwwroot/`.
- **`jammer-map.py`** — runs every 2 hours; writes `wwwroot/jammer-map.json`.
- **`gcdump-monitor.sh`** — runs every 2 hours; snapshots both production and debug (port 5000) instances into `heapdumps/`, 12-file rolling window per label.
- **`user-awareness.py`** — manual; correlates telemetry.log + census.csv. `python3 user-awareness.py [--hash <32-char-hash>] [--ip <ip>]`.
- **`sense-drops.py`** — manual; reports census.csv dropout gaps. Run this first when investigating coverage gaps.
- **`prospect-radar.py`** — manual; finds musicians doing 7-slot sweeps who haven't visited JamFan22 yet.

### Diagnosing Census Gaps

Check `output.log` for:
- **`[RSS-bg]`** — printed at end of each successful RefreshThreadTask cycle. Format: `[RSS-bg] <utc> <VmRSS>  gc=<kB>kB  threads=<N>`. Absence = task not completing.
- **`[WARN] Slow fetch`** — all URL fetches exceeded 5s.
- **`[WARN] RefreshThreadTask: HTTP timeout/cancel`** — HttpClient timeout caused restart.
- **`RefreshThreadTask restarted (count: N)`** — logged by JamulusListRefreshService.

### Key Design Notes

- `EncounterTracker.DetailsFromHash()` takes `jamulusListURLs` + `lastReportedList` as params (avoids circular dep with `JamulusCacheManager`)
- `JamulusAnalyzer.LocalizedText(nationCode, ...)` is static, takes nation code as first param
- `DurationHere(server, who, nationCode)` takes nation code as param — no per-request singleton state
- Port is read from `PORT` env var; defaults to 443 with HTTPS (`keyJan26.pfx`); non-443 runs plain HTTP

## Memory Leak / OOM Risk

Two OOM kills confirmed on the debug instance (May 2026): ~3.4 GB anon RSS at death vs. ~225 MB normal startup. GC heap was stable (~157 MB) — leak is in native/unmanaged memory.

**Mitigations in place:** `deploy-test-build.sh` sets `oom_score_adj=500` on the debug process (preferred kill victim); `/etc/systemd/system/jamfan22.service` sets `OOMScoreAdjust=-500` (production strongly protected).

**`[RSS-bg]` telemetry:** production logs to `output.log`; debug to `/tmp/jamfan-test-build/output.log`. If `threads=` climbs alongside RSS (~4 MB per stack), thread proliferation is the cause. If RSS grows while `gc=` stays flat, the leak is native.

**Suspected causes (in order):**
1. **Thread proliferation** — async loops in `harvest.cs` SSE connections not awaited cleanly, or thread-pool starvation. Watch `threads=` over a debug session.
2. **Unbounded static caches** — `GeolocationService.m_ipAddrToLatLong`, IpAnalyticsService caches, EncounterTracker session maps. Any `Dictionary` never evicted is a candidate.
3. **HttpClient response body buffering** — SSE/streaming connections with large unread payloads.
4. **LOH fragmentation** — large objects never compacted.

**Investigate:** run a debug session and watch `threads=` in `/tmp/jamfan-test-build/output.log` as RSS grows. Compare gcdumps with `DOTNET_ROOT=/root/.dotnet PATH="/root/.dotnet:/root/.dotnet/tools:$PATH" dotnet-gcdump report <file>`.

## InferredRegion — GUID geolocation

`GetGuidInferredRegionAsync` in `nearby.cs`. Renders as `record.Location?.regionName ?? record.InferredRegion ?? ""`.

Algorithm: collect all servers this GUID has visited (census.csv, 4h cache), geolocate each weighted by this GUID's own tick count. Join-events anchor = highest-strength row with non-empty col 11 IP. Fleet anchor = all fleet IPs for this GUID, geolocated; day count is a confidence weight, not a gate; cross-country disagreement discards the fleet anchor. When both anchors agree → elevated confidence; when they conflict → join-events wins.

**Key limitation:** server location ≠ player location. Datacenter IPs (Linode, DigitalOcean) churn across regions faster than ip-api updates — ip-api can return a correct-looking `regionName` paired with coordinates from a completely different region. Residential IPs (join-events) are reliable; server IPs are not. Confirmed bad cases: Joezep (NSW, Australia), zxfbull (Canada), Herb M (Colorado).

**`IdentityManager.GetGuidStrengths`**: merges join-events (strength ≥ 16 = FLAG_HISTORY) with fleet cache. Fleet synth strength: ≥3 calendar days → 16; else `min(hitCount * 4, 12)`. Join-events wins on conflict.

### Corroboration Model — Current Decision Logic

| IP source | Condition | Region winner | Lat/Lon |
|---|---|---|---|
| `join-events(N)` | N ≥ 2 | IP Region | from join-events IP |
| `join-events(0/1)` | strength ≤ 1 | Top Server Region | centroid of GUID's top-tick server in that region |
| ip-api throttled | ipRegion == null | Top Server Region | unchanged |
| `fleet(Nd)` | any days | IP Region | from fleet IP |
| No IP | — | Anchor-filtered inferred region or null | unchanged |

**Geo-diag labels:** `join-events-geo`, `server-region(ip-conf=N)`, `fleet-geo(Nd)`, `geo-unavailable/{tier}`.

**Open concerns:**
- **Strength 2–15 gap**: threshold ≤1 may be too conservative. Values 2–5 are barely above noise.
- **Country-level adjacency**: `country-adjacency.json` precomputed but not wired into anchor filtering.
- **Datacenter IP coordinate bug**: fix is a `data/region-centroids.json` lookup keyed by `{countryCode}:{regionName}` — canonical centroids from GeoNames/Natural Earth so "Sichuan" always maps to actual Sichuan coordinates regardless of what ip-api returns for the server IP.
- **`fleet(1d)` override**: current code uses IP Region for all fleet days regardless of join-events strength; the ≤1 override rule should apply to fleet(1d) too.

**Lobby filter**: Any musician name containing "lobby" (case-insensitive) is excluded from the nearby list.

**Blues/Rock bot filter**: 77.163.83.31:22124 permanent bots suppressed in `Api.cshtml.cs`; card hidden when only bots present, "Tracks Playing" marker shown when a real user joins.

### Privacy opt-out
Server operators who want to be excluded must block both 137.184.43.255 and the London IPs (139.144.151.196, 13.42.109.202).

## TODO

- **`/chat-url` (client) rate limiting** (`Program.cs`): Gated by `IsIpAllowedAsync` but no per-IP rate limit. Any non-blocked IP can spam valid UG/chords69cl URLs into `urls.csv`. Add simple per-IP rate limit (e.g. 5 req/min via `ConcurrentDictionary<string, (int count, DateTime window)>`).

- **Alt-source silent-state slow polling** (`JamulusCacheManager.cs`, `PollOneAltSourceServerAsync`): When every client on a blocked server has `minsHere` exceeding 8h (permanent bots), mark it silent and skip N round-robin cycles. Clear on any short-duration client. Reduces load on explorer.jamulus.io.

- **InferredRegion: nightly cull cron + /login enhancement**: Prune stale census-based server-region cache. Enhance `/login` to pre-filter by visitor IP and persist identity server-side.

- **fleet-guid-ip.csv fallback for prediction geolocation** (`Program.cs`, `FleetGuidCache`): Use most recent non-blocked `client_ip` when join-events col 11 is empty. Insertion point: `[MISSING]` log path in prediction diagnostics.

- **VPN-user geofencing** (`Program.cs`, `FleetGuidCache`): Build GUID-level VPN allowlist from blocked-IP frequency in fleet-guid-ip.csv.

- **Browser compatibility audit — Firefox on Windows**: Investigate Web Audio API / MediaRecorder support. Add translated notice if unsupported. Enumerate User-Agents from JamFan22 logs.

- **geo-diag: Fleet/JE IP disagreement categories** (`nearby.cs`, `ResolveGuidLocationAsync`): `same-ip`, `/8-agree`, `/8-differ,geo-agree`, `/8-differ,geo-differ`. Also: `[JE-FLEET-DIVERGE]` structured log when jeStrength ≥ 3 and fleet /8 differs. Also: `fleet-ranges` indicator (count of distinct first octets per GUID).

- **geo-diag: Page-level agreement stats**: Summary line at top: `T1+fleet: N same-ip, M /8-agree, K geo-agree, L geo-differ`.

- **Nearby musicians section — add telemetry**: `nearby_toggle` (open/close), `nearby_mode` (compressed/full), column count via `ResizeObserver` on `.musician-grid`.

- **region-centroids.json**: Canonical lat/lon per `{countryCode}:{regionName}` from GeoNames/Natural Earth. Use instead of ip-api server-IP coordinates when resolving server region names, to fix datacenter IP coordinate corruption.

## Infrastructure notes

nginx is not installed on this host. For routing/proxy needs use ASP.NET middleware or iptables.
