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
| `urls.csv` | `minutes, source, ip:port, encoded_url` | URLs matching chat-patterns.txt, from all ingest paths (source = lounge/server/client) |
| `urls-rejected.csv` | `minutes, source, addr, encoded_url` | URLs arriving via any path that did NOT match chat-patterns.txt — review to discover missing patterns |
| `telemetry.log` | `minutes, ip, hash, flags, reserved, json` | Web client events (session start/end, actions) |
| `stream-gate.json` | `{ActiveIp, ExpiryUtc}` | Single-slot streaming lease (see StreamGate) |
| `stream-requests.json` | — | Queued stream slot requests |
| `fleet-guid-ip.csv` | `timestamp_minutes, guid, client_ip, server_ip` | Cross-session GUID↔IP evidence from fleet /ip-allowed calls; appended by `FleetGuidCache.UpsertGuid`, hydrated into RAM at startup. Cull to 15 days (pending). |

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

### Memory Leak / OOM Risk

**Confirmed May 2026 (first incident):** The debug instance (port 5000) was OOM-killed by the kernel after growing to ~3.4 GB anon RSS. Normal startup RSS is ~225 MB, so the process had leaked ~3.2 GB over its lifetime. The OOM kill leaves no trace in `output.log` — the only evidence is in `dmesg` (`grep "oom-kill\|Killed process" /var/log/kern.log` or `dmesg | grep -i oom`).

**Confirmed 2026-05-20 (second incident):** Debug instance (pid 3227422) OOM-killed again at ~3.43 GB anon RSS (`rss_anon:3430384kB`). The instance was running alongside the production process; `tmux` triggered the OOM killer by requesting memory, and dotnet was selected as the largest victim. Production survived this incident because it was not yet the target — but both instances are leaking at the same rate, so production will eventually be killed too if left long enough.

**OOM priority mitigation (implemented 2026-05-20):** To ensure the debug instance is always killed before production when both are running:
- `deploy-test-build.sh` sets `oom_score_adj=500` on the debug process immediately after launch (preferred kill victim).
- `/etc/systemd/system/jamfan22.service` sets `OOMScoreAdjust=-500` (strongly protected). Reload with `systemctl daemon-reload` after editing the unit file.

**Telemetry coverage — important caveat:** Each instance writes its own `[RSS-bg]` log — production to `/root/JamFan22/JamFan22/output.log`, debug to `/tmp/jamfan-test-build/output.log`. The `.gcdump` files in `heapdumps/` were **production-only** until 2026-05-20; `gcdump-monitor.sh` now also dumps the debug instance when port 5000 is active (labelled `jamfan22-debug-*.gcdump`). The historical gcdumps (`jamfan22-*.gcdump`) therefore represent production only. The debug instance that died had its working directory wiped — no post-mortem heap data exists for it. Do not draw conclusions about the leak source from production telemetry alone; production may behave differently (HTTPS, `dotnet run`, lower manual traffic). As of 2026-05-20, production GC heap is stable at ~157 MB across 22-hour gcdump spans, and `[RSS-bg]` shows production RSS fluctuating 500–900 MB without a clear upward trend — so either the leak is debug-specific, or it is very slow in production.

**gcdump monitor now captures both instances** (`gcdump-monitor.sh`): production dumps to `jamfan22-YYYYMMDD-HHMM.gcdump`; if port 5000 is active, debug dumps to `jamfan22-debug-YYYYMMDD-HHMM.gcdump`. Each label keeps its own 12-file rolling window.

**`[RSS-bg]` log format (updated 2026-05-20):** Now includes GC heap and thread count:
```
[RSS-bg] 2026-05-20 16:45:42Z 807108 kB  gc=157432kB  threads=25
```
If RSS grows while `gc=` stays flat, the leak is in native/unmanaged memory. If `threads=` climbs alongside RSS (~4 MB per thread stack), thread proliferation is the cause.

**Suspected leak sources (investigate in order):**
1. **Thread proliferation** — each .NET thread stack is ~4 MB outside GC heap; 800 leaked threads = 3.2 GB, which exactly matches observed kill size. Watch `threads=` in `[RSS-bg]` over a debug session. Likely cause: async loops in `harvest.cs` SSE connections not awaited cleanly, or thread-pool starvation spinning up excess threads.
2. **Static caches with unbounded growth** — `GeolocationService.m_ipAddrToLatLong`, `IpAnalyticsService` country/ASN caches, `EncounterTracker` session maps, `JamulusAnalyzer` preloaded data. Any `Dictionary` or `ConcurrentDictionary` that is only ever added to and never evicted is a candidate. (Note: production gcdumps show the large `Entry<String,...>[]` arrays are *stable* in size — not currently growing in production.)
3. **HttpClient response bodies** — if SSE or streaming connections buffer large payloads without disposal.
4. **LOH fragmentation** — large objects (>85 KB) land on the Large Object Heap and are never compacted. RSS stays high even after GC collects them.

**Next steps:**
- Run a debug session and watch `threads=` in its `[RSS-bg]` output as RSS grows.
- Compare `jamfan22-debug-*.gcdump` files (when available) using `DOTNET_ROOT=/root/.dotnet PATH="/root/.dotnet:/root/.dotnet/tools:$PATH" dotnet-gcdump report <file>`.
- Add a cap or LRU eviction to any unbounded static dictionary.
- Consider adding an RSS threshold alert in `RefreshThreadTask` (e.g., log `[WARN] RSS exceeded 1GB` and optionally call `GC.Collect()` as a diagnostic signal).

### Key Design Notes

- `EncounterTracker.DetailsFromHash()` takes `jamulusListURLs` + `lastReportedList` as params (avoids circular dep with `JamulusCacheManager`)
- `JamulusAnalyzer.LocalizedText(nationCode, ...)` is static, takes nation code as first param
- `DurationHere(server, who, nationCode)` takes nation code as param — no per-request singleton state
- Port is read from `PORT` env var; defaults to 443 with HTTPS (`keyJan26.pfx`); non-443 runs plain HTTP (used for test builds on port 5000)


## InferredRegion — GUID geolocation

`GetGuidInferredRegionAsync` in `nearby.cs` (~line 1346). Renders as `record.Location?.regionName ?? record.InferredRegion ?? ""`.

**Algorithm (v2):**
1. Get all server IPs the GUID has been seen on via `GetGuidServerIpsAsync()` (census.csv, 4h cache).
2. Geolocate each → collect unique `regionName` strings (`serverRegions`).
3. Find anchor: scan join-events.csv for this GUID; take highest-strength row with non-empty col 11 IP; geolocate that IP → `anchorState`.
4. If anchor found: filter `serverRegions` to states equal to or adjacent to anchor (US adjacency only).
   - Exactly 1 survives → **tier1-tiebreaker**
   - 2–3 survive and are mutually adjacent → **tier2-adjacent** (emits "X or Y")
   - 0 or 4+ survive → null
5. If no anchor: if `serverRegions.Count == 1` → **v1-fallback**; else null.
Log line: `[InferredRegion] <name>: <region> (<tier>)` or `null`.

**Adjacency data** (`data/`): `us-state-adjacency.json` (keys match ip-api `regionName`); `country-adjacency.json` (keys = Natural Earth full names — **mismatch**: ip-api returns `"United States"` not `"United States of America"`). Pre-computed via `/root/compute-adjacency.py`. Country-level adjacency not yet wired into v2.

**Production stats** (May 2026, 1508 events): 160 v1-fallback, 14 tier1-tiebreaker (all European — giulio/Lazio is the only named player), 0 tier2-adjacent. Tier1 is working; tier2 and US players have not fired yet.

**Key limitation**: server location ≠ player location. v2 reduces this only when the anchor filters wrong-state servers; players with no anchor (empty col 11) fall back to v1.

**ip-api city/state contradictions** are a known failure mode (e.g., city="California SF Bay", state="Michigan"). Treat as low-confidence regardless of source. A more extreme variant has been observed: ip-api returns a correct-looking `regionName` paired with coordinates from a completely different region. Confirmed cases: Joezep (genuinely from Jerilderie NSW) whose dominant server got `regionName="New South Wales"` but California coordinates — causing him to appear on US users' nearby lists. zxfbull (censusgeo says Canada, InferredRegion says Sichuan) likely same issue with their dominant Sichuan server. Herb M (lives in Colorado) is labelled "New Jersey" by ip-api for his dominant server, and his coordinates are also wrong — they place him closer than California to a Washington-state viewer, so he sorts ahead of California musicians despite living in Colorado. Both the label and the placement are wrong. In all cases the lat/lon and the regionName from ip-api are unreliable for these server IPs. Likely cause: Jamulus servers run on cloud/datacenter IPs (Linode, DigitalOcean, etc.) whose address blocks churn frequently across regions — ip-api's database lags behind reassignments. Residential IPs (the basis of join-events player geolocation) are sticky and accurate; datacenter IPs are not. This means server-region inference is fundamentally lower-quality than player-IP inference. Assumed to be transient ip-api data errors; no mitigation applied.

**Study findings** (`investigate_guid_ip.py`, 40K rows / 4,031 GUIDs): strength is bimodal — 77% of GUIDs at 0 (never correlated), 11% at ≥16 (high-confidence). Among 446 high-confidence GUIDs, 66% have 2+ IPs (heavy DHCP/mobile users, e.g. Peter: 11 IPs). Server-region inference is 76% accurate; failures are systematic for players on cross-region servers.

**`IdentityManager.GetGuidStrengths`**: draws from join-events.csv (strength ≥ 16 = FLAG_HISTORY) and fleet GUID-IP cache. Fleet synth strength: ≥3 distinct calendar days for a (GUID, IP) pair → 16 (FLAG_HISTORY equivalent); else `min(hitCount * 4, 12)`. Fleet cache is hydrated from `fleet-guid-ip.csv` at startup.

**`GetGuidInferredRegionAsync`**: uses fleet high-confidence IPs (≥3 days) as a second anchor alongside join-events col 11. Anchors are geolocated; IPs with cross-country disagreement are discarded. When both anchors exist and agree → high confidence; when they disagree → join-events wins and disagreement is logged.

### Corroboration Model — Design & Status

**Vision**: Replace the simple fallback chain (IP geo → server-region) with a confidence-aware decision that uses server-region inference to validate or override a low-confidence IP. Agreement between two independent sources upgrades confidence; disagreement is a signal to suppress or flag the region.

**Important**: A GUID only gets a join-events.csv entry when the harvester catches them joining a **server** (not a lounge). Players on servers the harvester hasn't observed will have no join-events IP at all.

#### Current Decision Logic (geo-diag `winner` column / nearby list)

| IP source | Condition | Region winner | Lat/Lon |
|---|---|---|---|
| `join-events(N)` | N ≥ 2 | IP Region | from join-events IP |
| `join-events(0)` or `join-events(1)` | strength ≤ 1 | Top Server Region | centroid of top server region |
| `join-events(N)`, ip-api throttled | any N, `ipRegion == null` | Top Server Region | unchanged (join-events lat/lon) |
| `fleet(Nd)` | any days | IP Region (pending observation) | from fleet IP |
| No IP | — | Anchor-filtered inferred region or null | unchanged |

**Method labels** emitted in geo-diag:
| Label | Meaning |
|---|---|
| `join-events-geo` | join-events IP, strength ≥ 2, ip-api succeeded |
| `server-region(ip-conf=N)` | join-events IP overridden by top server region (strength ≤ 1) |
| `fleet-geo(Nd)` | Fleet IP, N calendar days observed |
| `geo-unavailable/{tier}` | Had IP but ip-api returned null; fell back to server-region |
| `server-region` / `anchor-filtered` / etc. | No IP at all; pure server-region inference |

**Strength threshold**: join-events strength ≤ 1 is treated as unreliable (noise-level correlation). The bimodal distribution (77% at 0, 11% at ≥16) supports a sharp cutoff; values 2–15 are uncommon but treated as IP-wins for now.

**Top Server Region**: the tick-dominant region from `rawServers` — the region whose servers the GUID has spent the most cumulative minutes on. Extracted as the first segment of the duration-weighted string (e.g. `"California (47); Michigan (3)"` → `"California"`). When used as winner, **lat/lon is replaced with the centroid of the top server region** (from the highest-tick server in that region via ip-api). This is wired into both geo-diag (winner column) and the nearby list (distance calculation + display).

**Fleet IP path**: fleet IPs (from `/ip-allowed` calls logged to `fleet-guid-ip.csv`) appear in geo-diag as `fleet(Nd)`. `fleet(1d)` and `fleet(2d)` are low-confidence (transient connection, possible VPN/hotel) and should override a weak join-events IP (strength ≤ 1) the same way the top server region does. `fleet(3d+)` is high-confidence (equivalent to join-events strength 16). Current code uses IP Region for all fleet days regardless of join-events strength — the ≤1 override rule should be applied to `fleet(1d)` as well.

**geo-diag refresh countdown**: on first page load with pending geos, sets `?r={pendingGeos-1}` in the meta-refresh URL and decrements each reload. Stops when `r=0` or `pendingGeos=0` — so the page auto-refreshes exactly as many times as there are unresolved IPs, then stops.

#### Blues/Rock Bot Filter *(implemented for 77.163.83.31:22124)*

The Blues/Rock server (77.163.83.31:22124) hosts five permanent Netherlands bots (Audio Player, Bass Track, Drums Track, Others Track, Vocals Track) that play backing tracks continuously — they are never the sign of a live jam.

**Desired behavior:**
- When **only the bots are present** (all five have been connected longer than `currentTimeoutPeriod` = 8 hours), the server card is **suppressed entirely** — it disappears from the list.
- When **a real user joins**, the card reappears. The bots are hidden; a single italic *"Tracks Playing"* line appears first (rendered by the `isTracksMarker: true` guard in `renderMusician()`), followed by the real user's entry as normal.
- The card disappears again once the real user leaves and enough time passes for the bots to re-exceed the threshold.

**Implementation** (`Api.cshtml.cs`):
- `isTracksServer` is set at the top of the server loop.
- In the suppression block, `isTracksServer` servers use `iTimeoutPeriod = int.MaxValue` so the standard all-users-exceeded suppression never fires (the tracks logic handles visibility instead).
- In the client-building loop, any user with `minsHere >= currentTimeoutPeriod` on an `isTracksServer` is skipped silently; `tracksBotsPresent` is set to true.
- After the loop: if `tracksBotsPresent && clients.Count > 0`, the "Tracks Playing" marker is prepended.
- Before `apiResponse.Add`: if `isTracksServer && clients.All(c => c.isTracksMarker)` (i.e., no real users), the server is suppressed via `continue`.
- **Background sighting fix** (`JamulusCacheManager.cs`): `_tracker.NotateWhoHere(...)` is called in the background polling loop for every visible client, so connection durations accumulate continuously — not only when someone hits the API. Without this fix the 8-hour threshold could never be reached (sightings reset every ~2 hours of API inactivity).

**Note on generalization**: the current implementation is hard-coded to 77.163.83.31:22124. The CLAUDE.md TODO about a general Blues/Rock GUID filter for other servers is a separate, unimplemented idea.

**Lobby filter**: Any musician name containing "lobby" (case-insensitive) is excluded from the nearby list. This catches "Lobby [0]", "lobby [🐰]", etc.

#### Concerns & Open Questions

- **Server region ≠ player location**: tick-dominant region is the best available signal when IP is weak, but a player who always joins cross-region servers will be mis-labelled. No fix yet.
- **ip-api throttling**: free tier silently returns null, causing `geo-unavailable/...`. Geo-diag auto-refresh now stops after draining the queue.
- **Strength 2–15 gap**: threshold ≤1 may be too conservative. Strengths 2–5 are barely above noise.
- **ip-api city/state contradictions** (e.g. city="California SF Bay", state="Michigan") can corrupt both IP Region and the anchor inside `GetGuidInferredRegionAsync`. No mitigation yet.
- **Country-level adjacency**: `country-adjacency.json` is precomputed but not wired into v2 anchor filtering.
- **Datacenter IP coordinate bug causes wrong distance filtering**: ip-api sometimes returns a plausible `regionName` (e.g. "Sichuan") paired with coordinates from a completely different region (e.g. North America) for churned datacenter address blocks. This causes musicians to pass the 5000km distance filter and appear on nearby lists with a clearly wrong region label (e.g. zxfbull showing "Sichuan" to a North American viewer). **TODO**: build a `data/region-centroids.json` lookup keyed by `{countryCode}:{regionName}` with canonical lat/lon centroids sourced from GeoNames or Natural Earth admin-1 data. Use this instead of ip-api's server-IP coordinates whenever a server region name is resolved — so "Sichuan" always maps to actual Sichuan coordinates (~30°N, 103°E) regardless of what ip-api returns for that server IP. Keys must use `countryCode` prefix to disambiguate collisions (e.g. "Georgia" US state vs "Georgia" country, "Victoria" Australia vs Canada).

## Disruptive User: `++li++` (Thailand)

**Identity**: Jammer name `++li++` (variants: `++li.+`, `+li+`, `+++li+++`, etc.) active primarily on Thai East Asian servers. Hash `e1cc1d8d4f7985932447f3623e6f9a9b` (canonical). Top servers as of May 2026:

| Minutes | Server |
|---------|--------|
| 1,239 | TEST [Wangmai] |
| 618 | Thomas playground |
| 399 | Bunnies Jam ! |
| 336 | Chaeng Watthana |
| 328 | MJTH [Wattana] |

Secondary variant `++li.+` (hash `b962558b4787fee6e3d5890e05c27179`): 145 min on MJTH [Wattana].

**Plan**: Compile evidence of disruptive behavior from census/server logs, then contact the operators of the Thai servers above (Thomas playground, Bunnies Jam !, Chaeng Watthana, MJTH [Wattana], TEST [Wangmai]) with a report. Invite them to run a patched Jamulus server build that can repel this user by name pattern or hash.

**Evidence to gather**: session duration patterns, frequency of name-variant changes, any chat/URL data in `urls.csv` or `urls-rejected.csv` tied to these server IPs during `++li++` sessions.

#### Incremental Plan (remaining steps)

1. ~~Observe: show IP Region and Servers Region side by side~~ ✓
2. ~~Act on low-confidence IP: defer to top server region when join-events strength ≤ 1~~ ✓
3. ~~Wire new region + lat/lon logic into nearby list~~ ✓
4. ~~**Observe fleet rows**~~ ✓ — apply ≤1 override to `fleet(1d)` (not yet coded)
5. **Corroboration column**: add `agree`/`conflict`/`ip-only`/`server-only` classification to gather stats.
6. **Country-level adjacency**: wire `country-adjacency.json` into anchor filtering.

## TODO

- **`/chat-url-server` fleet IP gate** (`Program.cs`): The endpoint currently accepts POSTs from any IP. Only fleet servers should be calling it. Add a check against the known fleet IP list (or `IsIpAllowedAsync` with a fleet-specific allowlist) before processing the URL, returning 403 for unrecognized callers.

- **Alt-source silent-state slow polling** (`JamulusCacheManager.cs`, `PollOneAltSourceServerAsync`): After each poll of a blocked server, check if every client in the response has `minsHere` exceeding a long-duration threshold (e.g., 8 hours = permanent bots). If so, mark that server as "silent" and skip it in the round-robin for N cycles before polling again. When any short-duration client appears, clear the silent flag immediately. Implementation: small `Dictionary<string, int>` alongside `_altSourceCache` (server key → cycles-to-skip). Reduces unnecessary HTTP load on explorer.jamulus.io without losing census accuracy for real musicians.

- **InferredRegion: nightly cull cron + /login enhancement**: Add cron job to prune stale data from census-based server-region cache. Also: enhance `/login` to pre-filter by visitor IP and persist identity server-side. (Country-level adjacency and fleet anchor are already documented under Corroboration Model.)

- **fleet-guid-ip.csv fallback for prediction geolocation** (`Program.cs`, `FleetGuidCache`): Use most recent non-blocked `client_ip` from fleet-guid-ip.csv when join-events.csv col 11 is empty. Insertion point: `[MISSING]` log path in prediction diagnostics. See SCHEMAS.md for format details.

- **VPN-user geofencing** (`Program.cs`, `FleetGuidCache`): Build GUID-level VPN allowlist from blocked-IP frequency in fleet-guid-ip.csv. Geofence VPN-dependent GUIDs rather than silently failing them. See SCHEMAS.md for query and approach.

- **Browser compatibility audit — Firefox on Windows**: Investigate whether Firefox on Windows can stream or record audio (Web Audio API / MediaRecorder). If not, add a translated user-facing notice (short, idiom-free) shown when the browser is Firefox. Broader goal: enumerate User-Agents from JamFan22 logs and confirm or explicitly apologize for each browser that doesn't work. Note: nginx is not running on jamulus.live — check JamFan22 logs for UA data.

- **geo-diag: Categorize fleet/JE IP disagreement** (`nearby.cs`, `ResolveGuidLocationAsync`): Replace (or augment) `+fleet(Nd,diff:X.)` with a named category:
  - `same-ip` — exact match
  - `/8-agree` — same first octet (DHCP rotation, multi-homed)
  - `/8-differ,geo-agree` — different /8 but same region (corporate VPN in same city)
  - `/8-differ,geo-differ` — different /8 and different geo-region (meaningful disagreement)
  Requires geo lookup on fleet IP when it diverges from JE IP. Safe to display publicly (no raw IPs).

- **geo-diag: Server-side structured log for JE/fleet geo conflicts**: When `jeStrength >= 3` and fleet /8 differs from JE /8, emit: `[JE-FLEET-DIVERGE] guid=<first8> je-strength=N je-region=<R1> fleet-region=<R2> fleet-days=D`. No raw IPs. Query: `grep '\[JE-FLEET-DIVERGE\]' output.log | sort | uniq -c | sort -rn`.

- **geo-diag: Fleet IP /8 diversity per GUID**: For each row, add `fleet-ranges` indicator — count of distinct first octets for that GUID across fleet-guid-ip.csv (`GetAllFleetIpsByGuid` already returns all IPs — just `Distinct` on `ip.Split('.')[0]`). `fleet-ranges:1` = stable; `fleet-ranges:2+` = VPN candidate or traveler. Cross-references VPN-geofencing TODO above.

- **geo-diag: Page-level agreement stats for T1 GUIDs**: At page top, add one summary line for T1 rows with fleet data: `T1+fleet: N same-ip, M /8-agree, K geo-agree, L geo-differ`. Shows system-level distribution at a glance without inspecting individual rows.

- **Nearby musicians section — add telemetry**: No telemetry currently covers the nearby bar's visibility or sizing behavior. Need to add `Tracker.log` calls for: (1) `nearby-btn` collapse/expand clicks (event `nearby_toggle`, payload `{ open: 1|0 }`); (2) compressed vs. full-height mode transitions inside `updateNearbyMaxHeight()` (event `nearby_mode`, payload `{ compressed: 1|0 }`, fire only on state change to avoid spam); (3) whether the bar is open or closed at `session_start`. Also add column-count tracking: the `.musician-grid` uses `repeat(auto-fit, minmax(min(100%, 450px), 1fr))` — pure CSS, so column count (1, 2, or 3) is never observed. Add a `ResizeObserver` on `.musician-grid` that computes `cols = Math.round(containerWidth / 450)` and fires `Tracker.log('nearby_cols', { cols })` on change. This will reveal whether any users other than the developer interact with or dismiss the section, how often it renders in compact vs. expanded form, and which column layouts actually occur. See `Client.cshtml` (click handler around nearby-btn), `updateNearbyMaxHeight()`, and nearby.cs (grid CSS).

## Expansion plan: servers blocking 137.184.43.255

Some public Jamulus servers block UDP traffic from the harvest-pings servers.php instance at `137.184.43.255` (DigitalOcean San Francisco). This prevents detection of join events on those servers. The plan to address this has three sequential phases:

### Phase 1 (now): measure hidden traffic via explorer.jamulus.io
`explorer.jamulus.io` runs two servers.php instances from IPs not blocked by these servers:
- `https://explorer.jamulus.io/servers.php` — Linode/Akamai London (139.144.151.196, AS63949)
- `https://explorer.jamulus.io/servers-lon2.php` — AWS London

Query these endpoints with `?directory=<dir>` to get client lists for servers invisible to 137.184.43.255. Client data contains name, country, instrument, city — **no IPs**, so not usable for the correlation engine. Useful only for census counting (unique musician names per server over time).

The chronic-blocker server list and diagnostic script live at `/root/probe_blocked.py` on `24.199.107.192`. The long-running census script is `/root/blocked_census.py` (logs to `/root/blocked_census.csv`) on that same host. The explorer HTTP approach is simpler: no new infrastructure, no UDP code needed.

There are exactly 7 public directories (derivable from `DIRECTORIES` in `blocked_census.py`). The census script queries all 7.

**Servers still blocking even the London vantage points** (broader firewall policy):
elizasham (142.204.68.104), KammerJam cluster (129.159.249.5, 7 ports), and a handful of others with `ping=-1` in explorer responses.

### Phase 2: fleet expansion into blocked-server regions
After Phase 1 establishes which regions have significant hidden musician traffic, provision new fleet servers in those regions. Priority order driven by Phase 1 census data. Candidates: Frankfurt (DE), UK, Toronto (CA), North America (Rick's servers span NJ/CHI/YYZ on Linode/Akamai).

### Phase 3: alternative servers.php instances for server cards
After fleet is in place, add servers.php instances at non-blocked IPs to restore visibility into blocked servers for `census.csv` and server-card display. **Important constraint**: data from alternative servers.php instances is NOT usable by the correlation engine, which requires precisely-timed join events from the specific modified instance at 137.184.43.255. Alternative instances feed census only.

### Multi-Source Aggregation: Combining Primary + London Data in JamFan22 *(implemented)*

**Goal**: Servers that block 137.184.43.255 still appear in JamFan22 server cards and accumulate census data, using explorer.jamulus.io as the fallback source. The correlation engine (join-events, GUID strength, IP geolocation) is unaffected — it only runs on primary-source data.

#### Architecture (as implemented)

All changes are in `Services/JamulusCacheManager.cs`. No other files required structural change.

**Per-server round-robin poll** (not directory-level): each `RefreshThreadTask` iteration calls `PollOneAltSourceServerAsync`, which picks the next server from `BlockedServerKeys` via `_altRoundRobinIdx % count`, queries `servers.php?server=IP:PORT` on London-1 then London-2 as fallback, and stores the result in `_altSourceCache[ip:port]`.

**Synthetic directory key**: results are published as `LastReportedList["_alt"]` (JSON array of all cached alt-source servers). `ProcessServerListsAsync` and `GuidFromIpAsync` already iterate `LastReportedList.Keys`, so blocked servers appear in the web UI and GUID lookups without any changes to those code paths.

**Census loop**: after the primary `foreach (var key in JamulusListURLs.Keys)` block, a separate alt-source block iterates `_altSourceCache.Values` and writes to `server.csv`, `census.csv`, `censusgeo.csv`, and calls `NotateWhoHere` — identical to what primary does. No encounter tracking (no join-events for these servers).

| Source | URL pattern | IPs available? |
|--------|-------------|----------------|
| Primary | `JamulusListURLs` (existing) | Yes — join-events correlation works |
| London-1 | `https://explorer.jamulus.io/servers.php?server=IP:PORT` (139.144.151.196, AS63949) | No |
| London-2 | `https://explorer.jamulus.io/servers-lon2.php?server=IP:PORT` (AWS London) | No |
| Blocked list | `http://137.184.43.255/blocked.php` (plain text, one `ip:port` per line, `#` = comment) | — |

**Blocked list refresh**: hourly. `RefreshBlockedListAsync` fetches `blocked.php`, parses lines, and updates `BlockedServerKeys`. On refresh it also evicts `_altSourceCache` entries for servers removed from the list.

**Timing**: with ~15 blocked servers and a 5s poll cycle, two servers are polled per cycle, so each blocked server is re-polled every ~37 seconds — within the 60-second per-minute census window.

#### Safety guards

- **`ping >= 0` check** — entries with `ping == -1` (hard-blocked from all vantage points, e.g. elizasham, KammerJam) are silently skipped; neither cache nor census is written.
- **Stale cache eviction** — when `blocked.php` is refreshed, keys removed from the blocked list are immediately deleted from `_altSourceCache` and drop out of `LastReportedList["_alt"]`.
- **Double-census guard** — before the alt census loop, a `primaryServerKeys` set is built from `m_deserializedCache`. Any alt-source server already present in primary data is skipped, preventing duplicate `census.csv` rows during the race window when a server gets unblocked.
- **Web UI dedup** — `ProcessServerListsAsync` now tracks `seenAddrs` across all `LastReportedList` keys. A server appearing in both `"_alt"` and a primary directory is rendered only once (primary wins because primary keys are iterated first).

#### What does NOT change

- **Correlation engine** — join-events.csv, GUID strength, IP↔GUID mapping. Unaffected; the modified Jamulus binary at 137.184.43.255 never saw these servers anyway.
- **InferredRegion** — already handles the no-IP case via server-region fallback.
- **`DetectJoiners`** — not called for alt-source data. No join-events entries for these musicians; that is expected.
- **CSV schemas** — no changes. Alt-source rows are indistinguishable from primary rows in the CSV files.
- **In-RAM encounter tracking** — the alt-source census loop does NOT populate `m_userServerViewTracker`, `m_userConnectDuration`, `m_userConnectDurationPerServer`, `m_everywhereWeHaveMet`, or call `ReportPairTogether`. This means musicians whose only sessions are on blocked servers don't accumulate hotties/co-jammer rankings and have zero in-RAM duration state. CSV data (census.csv, censusgeo.csv) is complete; only live in-RAM state is partial. **Possible future improvement**: rather than adding these calls individually to the alt-source loop, integrate alt-source data more comprehensively into the primary pipeline so the special-case loop disappears entirely.

#### Diagnostic log lines

| Pattern | Meaning |
|---------|---------|
| `[ALT] Blocked list refreshed: N servers, altCached=M.` | Hourly — `blocked.php` reachable; M = live entries in cache |
| `[ALT] blocked.php fetch failed: ...` | Network error fetching blocked list |
| `[ALT] IP:PORT: N clients via lon1` | Per poll cycle — round-robin working, London-1 succeeded |
| `[ALT] IP:PORT: N clients via lon2` | London-1 failed or returned empty; London-2 succeeded |
| `[ALT] IP:PORT: no response from either explorer endpoint` | Hard-blocked server; expected for elizasham/KammerJam cluster |
| `[ALT-census] cached=N active=A clients=C` | Every census write — N blocked servers tracked, A had live players, C total clients |

#### Testing procedure — debug instance first

**IMPORTANT: always test on port 5000 before touching production.**

```bash
# 1. Start debug instance
kill $(lsof -t -i :5000) 2>/dev/null; cd /root/JamFan22 && ./deploy-test-build.sh

# 2. Watch [ALT] lines — blocked list should load within first two cycles (~10s)
tail -f /tmp/jamfan-test-build/output.log | grep '\[ALT\]'

# 3. Confirm round-robin is walking through servers (one per 5s cycle)
grep '\[ALT\]' /tmp/jamfan-test-build/output.log | head -30

# 4. After ~2 minutes, confirm census entries are accumulating for blocked servers
grep "167.99.132.213" /tmp/jamfan-test-build/data/census.csv | wc -l
grep "45.79.142.148:23000" /tmp/jamfan-test-build/data/census.csv | tail -3

# 5. Confirm no double-counting: [ALT-census] active should be 0 when primary has the server
grep '\[ALT-census\]' /tmp/jamfan-test-build/output.log | head -10

# 6. Confirm hard-blocked servers log "no response" (not counted as active)
grep 'no response' /tmp/jamfan-test-build/output.log

# 7. Check web UI — http://localhost:5000 — look for Frankfurt Jam cluster
#    (167.99.132.213:*) and Rick's servers (45.79.142.148:*) when they have players
```

**Expected outcomes:**
- `[ALT] Blocked list refreshed: 15 servers` appears within 10s of startup
- Each blocked server appears in `[ALT]` lines with a cycle time of ~75s (15 servers × 5s)
- Hard-blocked servers (elizasham, KammerJam) consistently log "no response"
- `[ALT-census] active=N` is > 0 when any blocked server has players
- Those servers appear as cards in the web UI
- `grep "167.99.132.213" data/census.csv` grows over time
- No server appears in `[ALT-census]` counts AND in primary `[DEBUG] Processing ...` for the same cycle

**Only promote to production after all the above pass cleanly on port 5000.**

### Privacy opt-out
If a server operator does not want tracking, instruct them how to block themselves from all major servers.php instances (137.184.43.255 and the explorer London instances). Document the blocking method when this comes up.

## Infrastructure notes

nginx is not installed on this host. For routing/proxy needs use ASP.NET middleware or iptables.
