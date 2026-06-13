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

## Testing Welcome Messages

**"What would be said to player X on server Y?"** — use the debug preview endpoint (localhost only):

```bash
curl "http://localhost:5000/debug/welcome-preview?guid=GUID&nation=IT&serverIp=IP&serverport=22124&rpcport=9999"
```

**Workflow to go from a name to a preview:**

1. Find the player on the live Any Genre 1 feed (the most active directory):
   ```bash
   curl -s "http://24.199.107.192:5001/servers_data/anygenre1.jamulus.io:22124/cached_data" | python3 -c "
   import json,sys; data=json.load(sys.stdin)
   for s in data.get('servers_data',data):
     for c in s.get('clients',[]):
       if 'NAMEHERE' in c.get('name','').lower():
         print(s['ip'],s['port'],s['name'],'|',c['name'],c['instrument'],c['country'])"
   ```
2. Get the player's GUID from censusgeo.csv (last match = most recent profile):
   ```bash
   grep -i "NAMEHERE" /root/JamFan22/JamFan22/data/censusgeo.csv | tail -5
   # pick the hash with the most census.csv ticks:
   grep -c ",HASH," /root/JamFan22/JamFan22/data/census.csv
   ```
3. Call the preview endpoint with serverIp/port from step 1, guid from step 2.

The response JSON has: `context` (what was sent to the LLM), `signals` (compact signal summary), `rich` (bool — whether LLM was attempted), `llmMessage` (rendered HTML or null), `english` (second LLM call in English for non-US nations), `fallback` (static fallback string), `usingLlm` (bool).

**To get an English translation of a non-English production message:** pass the same params to the debug preview endpoint but with `nation=US` — the `llmMessage` field will be in English. Alternatively, the `english` field in a non-US preview call already contains the English version (generated as a second LLM call). The production `welcome-llm.log` does NOT currently log the English translation alongside non-English messages.

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

All services are singletons. Two hosted background services drive the polling loop: `JamulusListRefreshService` and `JammerHarvestService`.

**Service responsibilities:**
- **EncounterTracker** — user hashing (`GetHash`), session tracking, time-together persistence. Static fields for shared app state.
- **JamulusCacheManager** — background Jamulus server list polling, `LastReportedList`, `JamulusListURLs`. Static helpers: `MinutesSince2023AsInt()`, `IsDebuggingOnWindows`. Also runs alt-source round-robin poll for blocked servers. Calls `RecentDepartureTracker.UpdateSnapshot` on every `liveStatus.json` write. Blocked list refresh interval changed from 1 hour → **1 minute**; newly-blocked servers are immediately polled (don't wait for round-robin).
- **IpAnalyticsService** — IP→ASN lookups, rate-limit backoff, SmartNations, country code cache. Reads `wwwroot/asn-ip-client-blocks.txt` for the active blocklist. (`wwwroot/asn-blocks.txt` is **deprecated** — ignore it.) `WarmupComplete` flag (volatile bool) — set to `true` by the startup warmup task after all census server IPs are pre-cached. `DailyEssayService` checks this before serving essays.
- **GeolocationService** — IP→lat/lon via OpenCage, distance calculations. `m_ipAddrToLatLong` is a static cache.
- **JamulusAnalyzer** — orchestrates all above. Server list processing pipeline, preloaded data cache, IP→GUID resolution, user stats. Owns static `m_connectedLounges` (used by `harvest.cs`).

### Non-DI Code Accessing Statics

- `harvest.cs` → `JamulusAnalyzer.m_connectedLounges`, `JamulusCacheManager.MinutesSince2023AsInt()`, `JamulusCacheManager.IsDebuggingOnWindows`, `NonFleetSilencePoller.PollLoopAsync`
- `nearby.cs` → `JamulusCacheManager.JamulusListURLs`, `JamulusCacheManager.LastReportedList`
- `login.cshtml.cs` → `JamulusCacheManager.LastReportedList`, `EncounterTracker.GetHash()`
- `Program.cs` (hotties route) → same pattern
- `Api.cshtml.cs` → `harvest.m_fleetSilenceStatus`, `NonFleetSilencePoller.Status`, `BandIndex.GetBandSoon`, `RecentDepartureTracker` (indirectly via liveStatus)

### Page Models (`Pages/`)

- `IndexModel` — injects `JamulusAnalyzer` only
- `ApiModel` — injects all 5 services; has per-request `m_TwoLetterNationCode`
- `ClientModel` — injects `JamulusCacheManager`

### API Routes (defined in `Program.cs`)

- `GET /countries` — diagnostics: unique IPs by country
- `GET /api/nearby` — HTML for nearby Jamulus servers based on client IP
- `GET /api/nearby-essay` — returns cached daily essay HTML (or 204 if not ready); waits up to 70s for first generation
- `GET /api/geo-diag` — corroboration model diagnostic table for live T1 musicians
- `GET /hotties/{encodedGuid}` — co-jammers ranked by time spent together (min 10 min together to qualify)
- `GET /halos/` — streaming/snippeting halo server list
- `GET /debug/welcome-preview` — **localhost only** — preview welcome for guid/nation/server; see Testing section above
- `GET /ip-allowed/{ip}` — fleet-server gate; **now gated to `data/fleet-server-ips.txt` IPs only** (403 for non-fleet callers)
- `GET /stream` — async; delegates to `StreamGate.TryRequestStream` (async since lease-break needs RPC)
- `POST /chat-url-server` — fleet servers submit chat URLs; gated to `data/fleet-server-ips.txt`
- `POST /chat-url-client` — web clients submit chat URLs; gated by `IsIpAllowedAsync` + 3 req/min rate limit per IP; also accepts optional `serverAddr` field

**chat-patterns.txt coordination**: `wwwroot/chat-patterns.txt` serves two roles: (1) fleet server builds fetch it as their own URL-matching pattern list; (2) `harvest.cs` reads it to validate all incoming `/chat-url-client` and `/chat-url-server` reports (defense-in-depth). The client binary's pattern list is hardcoded in `kPatterns[]` (`jamulus/src/chatreporter.cpp:40`). **The two lists must be identical.** A domain missing from `chat-patterns.txt` is silently rejected here; a domain missing from `kPatterns[]` is never reported by client builds. When adding a domain to either place, add it to both. Abuse monitoring on `/chat-url-client` (rate-limiting, IP logging) must be in place before the client binary is widely distributed.
- `POST /chat-command-server` — fleet servers request stream slot; intentionally ungated (TryRequestStream has its own quality gate)

### Data Directory (`data/`)

See [SCHEMA.md](SCHEMA.md) for full file schemas and field descriptions.

### Other Scheduled Scripts (cron)

- **`predict-future.py`** — runs multiple times daily; writes `tooltips.json` and prediction outputs to `wwwroot/`.
- **`jammer-map.py`** — runs every 2 hours; writes `wwwroot/jammer-map.json`.
- **`gcdump-monitor.sh`** — runs every 2 hours; snapshots both production and debug (port 5000) instances into `heapdumps/`, 12-file rolling window per label.
- **`user-awareness.py`** — manual; correlates telemetry.log + census.csv. `python3 user-awareness.py [--hash <32-char-hash>] [--ip <ip>]`.
- **`sense-drops.py`** — manual; reports census.csv dropout gaps. Run this first when investigating coverage gaps.
- **`prospect-radar.py`** — manual; finds musicians doing 7-slot sweeps who haven't visited JamFan22 yet.

### Alt-source cache (`gather-server-data.py` on `24.199.107.192:5001`)

All 7 `JamulusListURLs` in `JamulusCacheManager` point to `http://24.199.107.192:5001/servers_data/<central>/cached_data`.
This Flask service (`call-servers-php.service`) caches upstream directory data with a 300s staleness threshold.

**Normal behavior:** JamFan22 polls every ~5s; the cache is refreshed on-demand when stale (upstream responds in ~8ms).
Max staleness under normal operation: ~305s. `[ON-DEMAND-REFRESH]` lines in the Layer 1 journal confirm refreshes.


**Diagnosing staleness from JamFan22's side:** if `[WARN] Slow fetch` lines appear in `output.log`, the 7 parallel
`GetStringAsync` calls each took >5s wall-clock — check if `call-servers-php.service` is running on `24.199.107.192`.
If the service is up but data is stale, check `journalctl -u call-servers-php.service | grep ON-DEMAND-REFRESH-FAIL`.

### Key Design Notes

- `EncounterTracker.DetailsFromHash()` takes `jamulusListURLs` + `lastReportedList` as params (avoids circular dep with `JamulusCacheManager`)
- `JamulusAnalyzer.LocalizedText(nationCode, ...)` is static, takes nation code as first param
- `DurationHere(server, who, nationCode)` takes nation code as param — no per-request singleton state
- Port is read from `PORT` env var; defaults to 443 with HTTPS (`keyJan26.pfx`); non-443 runs plain HTTP

### Startup Sequence

Documented inline in `Program.cs` — see comments before `app.Run()`. Key log markers: `[CENSUS-INDEX] Built`, `[STARTUP-WARM] Done`.

### CensusIndex — In-Memory Census Query Engine

`CensusIndex.cs` — static class, built once at startup from `census.csv`. All subsequent queries are O(1) dictionary lookups. New ticks fed in via `AddTick` (called by `JamulusCacheManager` each time a tick is written). See XML doc comments on each public method for full API.

### BandIndex — Band Canary Detection

`BandIndex.cs` — static class. Reads `data/bands.json` (written weekly by `band-finder.py`). 12h reload TTL. Detects when "canary" band members are present on a server and returns missing members for the Soon field.

**Canary trigger logic:** one `strong`-level member, OR two `pair`-level members. If `primary_server` is set, only triggers on that server. Returns `BandSoonResult` with `BandId`, `BandName`, `CanaryNames` (present triggering members), `Missing` (members not yet on server). Caller de-dups against existing `soonNames` before inserting. Logs `[BAND-SOON]`.

**`Api.cshtml.cs` integration:** called per server card during API response build. Missing member names inserted at front of `soonNames` list with band name or member list as label.

### NonFleetSilencePoller — "Ear" Design

`NonFleetSilencePoller.cs` — static class started as a background `Task.Run` from `JammerHarvestService`.

**Kill switch (currently ACTIVE):** Disables Ear probing only — lounge-connected and fleet silence data continue unaffected.
```bash
touch /root/JamFan22/JamFan22/silence-poller-disabled   # disable (current state)
rm /root/JamFan22/JamFan22/silence-poller-disabled       # re-enable
```

**Free silence sources — Ear never visits these:**
- **Lounge-connected servers:** `JamulusAnalyzer.m_connectedLounges.ContainsKey(ipPort)` → silence read from `harvest.m_loungeIsQuiet`.
- **Fleet servers:** UDP 1028 handled by `harvest.cs:ChannelLevelPollLoopAsync`; excluded by fleet IP set.

**Probe order — cheapest check first:**
Before sending a named Ear connection, try the connectionless 1028 UDP frame (300ms timeout). If the server responds, parse levels and update `Status` — no named connection needed. Track responding servers in `_connectionlessCapable` (HashSet) and skip Ear for them permanently. As of 2026-06 zero non-fleet servers responded (0/219 via `probe-1028.py`); this changes as Jamulus server builds evolve.

**"Ear" — named connection for all remaining servers:**

Brief named Jamulus client connection. Name players see: `Ear`. Stays ≥500ms then disconnects. **Single thread — one server at a time.** Loop picks the most-overdue eligible server, probes it, schedules next visit, repeats.

**Ear motives — priority order:**
1. **GUIDs never sampled for silence are the top priority.** The longer a GUID has been visible in `LastReportedList` without ever receiving a silence sample on their server, the more urgent the probe becomes. Finding GUIDs that are *always* silent is the primary goal — these are the players who look active but never contribute audio.
2. Confirm that a server that just went quiet stays quiet (fast re-check at 4 min).
3. Keep confirmed-quiet servers re-checked at a reasonable interval (10 min) so departures surface promptly.
4. Back off from active servers to minimize disruption.

**Interval table:**
| Situation | Next probe |
|---|---|
| New server (never probed) | 8 min — assume active |
| Sound detected | 20 min — stay away |
| **First quiet** (QuietStreak 0→1) | **4 min** — hard UX floor |
| Confirmed quiet (QuietStreak ≥ 2) | 10 min |
| Error / timeout | 12 min |

The 4-minute floor: first quiet shows 🔇 emoji; only the *second* consecutive quiet reading triggers card removal in Active Only. Gap lets emoji settle before Ear confirms.

**Candidate filter:** has clients in `LastReportedList`, not fleet IP, not lounge-connected.

**State fields:**
- `_nextProbeAfter` — `ConcurrentDictionary<string, DateTime>`
- `_quietStreak` — `ConcurrentDictionary<string, int>` — 0=unknown/active, 1=first quiet, 2+=confirmed quiet
- `_connectionlessCapable` — `HashSet<string>` — servers that responded to 1028 (Ear skipped)

**Loop:** pick most-overdue due candidate; if none due, sleep until soonest (max 60s). Probe. Update state. Repeat.

**Departure cleanup:** server vanishes from `LastReportedList` → remove from `_nextProbeAfter` and `_quietStreak`. Gets fresh 8-min first-probe on return.

**Logging — one line per probe:**
```
[EAR] ip:port "Server Name" clients=N reason=new|active|silence-1st|silence-chk method=1028|ear quiet=T/F audible=N/M streak=X→Y next=Nmin
```

**Output:** `NonFleetSilencePoller.Status` — `ConcurrentDictionary<string, SilenceStatus>`. `SilenceStatus` has `Quiet`, `Levels`, `UpdatedAt`, `Error?`.

**In Api.cshtml.cs:** `isSignalKnown = NonFleetSilencePoller.Status.ContainsKey(serverAddress)`. `isQuiet` merges fleet + non-fleet + lounge-quiet.

**gjprobe** (`/usr/local/bin/gjprobe`): Go binary for Ear named connections. `gjprobe -server ip:port -timeout 6s [-directory anygenre1.jamulus.io:22124]`. Returns `{quiet:bool, levels:[int,...], error?:string}`.

### Fleet Silence Detection — UDP ChannelLevelList Protocol

`harvest.cs:ChannelLevelPollLoopAsync` — polls fleet servers every **29s** via native Jamulus UDP protocol (not gjprobe). Reads `data/fleet-server-ips.txt` for server list (ip:port format). Each poll sends **1014 then 1028** on the same UDP socket (see Companion protocol below) — both responses arrive within one round-trip of each other, ensuring the client-slot map and level snapshot are consistent.

**Protocol:** sends `PROTMESSID_CLM_REQ_CHANNEL_LEVEL_LIST` (message ID 1028 = `0x0404` LE) frame: 9 bytes total = TAG(2) + ID(2 LE) + counter(1) + bodyLen(2 LE=0) + CCITT-CRC(2 LE). CRC: poly=0x1021, init=0xFFFF, inverted. Parses response body: packed nibbles, low nibble = even-index client level, high nibble = odd-index; sentinel `0xF` = end. Any level > 0 → `quiet=false`.

**Output:** `harvest.m_fleetSilenceStatus` — `ConcurrentDictionary<string, bool>` keyed by `ip:jamulusPort`. Logs on state change: `[LEVEL-POLL] ip:port: quiet=X clients=N`. Removed from dict on timeout or error (logged).

### Companion protocol — UDP 1014/1013: client metadata

`CLM_REQ_CONN_CLIENTS_LIST` (message ID **1014**) → server responds with `CLM_CONN_CLIENTS_LIST` (**1013**), returning name/city/country/instrument/channelId for all connected clients. Unlike 1028 (fleet-only, requires the build that merged the PR), **1014 works on all standard Jamulus servers** — connectionless, no handshake, no IP restriction in the handler. The channel slot in 1013 is the same index used in the 1015 nibble levels, so the two responses can be correlated by slot to get per-GUID audio levels on any server.

**1014 request frame** — same 9-byte structure as 1028: `TAG(2=0x0000) + ID(2 LE = 0xF6 0x03) + counter(1=0x00) + bodyLen(2 LE=0x0000) + CCITT-CRC(2 LE)`. ID 1014 = 0x03F6, so bytes 2–3 are `0xF6, 0x03`.

**1013 response body** — one record per connected client, back-to-back:
`ChannelId(1) + CountryId(2 LE) + InstrumentId(4 LE) + SkillLevel(1) + padding(4) + nameLen(2 LE) + name(nameLen) + cityLen(2 LE) + city(cityLen)`
Verify response ID: `buf[2]==0xF5 && buf[3]==0x03` (1013 = 0x03F5 LE).

**Implemented:** `harvest.cs` sends 1014 then 1028 per poll; cross-joins by channel slot into `m_fleetClientLevels` (`ip:port` → `playerName → level`). `JamulusCacheManager` writes the result as the 4th `audible` column in `census.csv`.

### RecentDepartureTracker

`RecentDepartureTracker.cs` — static class. Tracks when GUIDs leave servers and retains the departure for 120 minutes.

**`UpdateSnapshot(serverKey, currentGuids, nowMinutes)`** — called by `JamulusCacheManager` each `liveStatus.json` write. Compares current GUID set against previous snapshot; departures recorded as `DepartureRecord(guid, serverKey, departureMinute, sessionMinutes)`.

**`GetRecentDepartures(serverKeys, nowMinutes, maxAgoMinutes, minSessionMinutes)`** → list of recent departures from those servers, newest first.

### WelcomeMessages, WelcomeCache, FleetRpcPorts (Program.cs static classes)

**`WelcomeMessages`** — static class in `Program.cs`. Provides localized fallback strings for welcome messages (25+ languages). `Get(nation)` → HTML link with "shows more" text. `YouveJoined(nation)` → translated "You've joined:" phrase. Maps nation code → language code → message.

**`WelcomeCache`** — static class in `Program.cs`. Thread-safe expiry cache keyed by arbitrary string, 5-min default TTL. Used for: rapid re-join cache (`guid:serverKey`), flag-change suppression (`channel:{serverIP}:{channelId}:{guid}` with 1-min TTL).

**`FleetRpcPorts`** — static class in `Program.cs`. Reads `data/fleet-rpc-ports.txt` (format: `ip=port`, `#`=comment) with 5-min TTL. `GetPort(serverIp)` → rpc port (default 9999). Used for non-standard fleet RPC ports (Syncopé=9998).

**`FleetIpAllowlist`** — already existed; updated to strip port from `ip:port:rpc` lines in `fleet-server-ips.txt` so the IP-only check still works.

## Memory Leak / OOM Risk

See [RUNBOOK.md](RUNBOOK.md) for full diagnosis steps. Production is OOMScoreAdjust=-500 (protected); debug is +500 (preferred victim). Watch `[RSS-bg] threads=` in `output.log` — thread growth alongside RSS means thread proliferation; flat GC heap means native leak.

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

**Lobby filter**: Any musician name containing "lobby" (case-insensitive) is excluded from the nearby list.

**Blues/Rock bot filter**: 77.163.83.31:22124 permanent bots suppressed in `Api.cshtml.cs`; card hidden when only bots present, "Tracks Playing" marker shown when a real user joins.


## LLM Welcome System

`WelcomeContext.cs` + `WelcomeMessageGenerator.cs` + `WelcomeEventLog.cs` wired into `/ip-allowed` `Task.Run`. Model: Gemini 2.5 Flash. API key: `data/gemini-key.txt`. System prompt: `data/welcome-system-prompt.txt` (re-read per call). Hot-reloadable config: `data/welcome-config.txt`. Logs: `data/welcome-llm.log` (full), `data/welcome-events.log` (compact — tail this). Debug: `GET /debug/welcome-preview?guid=X&nation=DE&serverIp=Y&serverport=22124&rpcport=9999` (localhost only — returns `signals` and `english` fields).

**Fleet coverage**: all fleet servers receive welcomes — all carry the binary that passes `channelId`+`rpcport` to `/ip-allowed`. Check `output.log` for `[IP-ALLOWED-WELCOME]`.

**Reliability:**
- **LLM timeout**: default 2000ms (`llm_timeout_ms` in welcome-config.txt). Static fallback on timeout. Events log shows `llm=timeout/error/1/0/cached`. Normal Gemini latency: 650–1050ms; fallback <300ms.
- **Rapid re-join cache** (`WelcomeCache`): keyed `guid:serverKey`, 5-min TTL. Logged as `llm=cached signals=cached`.
- **Flag-change suppression** (`WelcomeCache`): keyed `channel:{serverIP}:{channelId}:{guid}`, 1-min TTL. Suppresses re-welcome when player changes flag mid-session; GUID in key so new player on recycled slot is NOT suppressed. Logged as `[IP-ALLOWED-WELCOME] suppressed flag-change re-welcome`.
- **TCP connect timeout**: 5-second `CancellationTokenSource`. Was unbounded — confirmed cause of 138s Task.Run hang (Sindone, 16:45 UTC).
- **Self-exclusion from room list** (`WelcomeContext.GatherAsync`): arriving player excluded by GUID hash (`EncounterTracker.GetHash(p.Name, p.Country, p.Instrument) != arrivingGuid`). `[WARN-WELCOME-SELF-IN-ROOM]` fires (with diagnostic details) if the arriving player's name still appears in `others` after hash-based exclusion.
- **Empty-name fix** (`WelcomeContext.cs` lines ~761/770): blank `p.Name` (not just literal "No Name") now falls through to `m_guidNamePairs`/censusgeo lookups instead of leaving `arrivingName=""`, which caused the LLM to use city field as player name.
- **Crew-elsewhere humor** (`WelcomeContext.cs` ~line 1070): when server is empty and `usualCrewElsewhere.Count > 0`, LLM gets a hint to acknowledge with light humor that the arriving player is alone while their usual crew is active elsewhere.
- **Studio D Jazz Jam tip** (`WelcomeContext.cs`, `IsStudioDFirstHour`): when a player joins any non-Studio-D fleet server during Studio D's first reservation hour (Wednesday 02:xx UTC), the context includes a tip that Marsha K's Jazz Jam just started — LLM weaves it into the welcome. Fires for the first hour only by design.

**Prompt engineering notes:**
- Banned phrases must be language-agnostic; English strings don't prevent Italian/French equivalents.
- WRONG/RIGHT examples outperform added rule bullets — add a pair for each new failure mode.
- English preview: regex-replace "Language to use for message: X" → "English" in context string; nation="US" alone has no effect.

**Server lore** (`data/server-lore.json`): keyed by `ip:port`. Re-read on every welcome call. Fields rendered into context: `tagline` → `Server identity:`, `themes` → `Server themes:`, `events` → `Event — {name}: {schedule}; {description}; listen: {url}`. To add/update entries, edit `data/server-lore.json` — no restart needed. Periodically refresh by re-running census analysis when player rosters or schedules shift.

**Event hype hygiene:** Only hype scheduled events that are actually happening. The Freiheit **Monday Night Jam** had a strong showing on 2026-05-18 (20 players) but went completely dark for the next three Mondays (May 25, Jun 1, Jun 8 — 0 players). If an event has missed 2+ consecutive weeks with no attendance, remove or suspend it from `server-lore.json` and update the WRONG/RIGHT examples in `welcome-system-prompt.txt`. Hyping a dead event damages trust.

**Title-artist context** (implemented): UG URL titles extracted from URL slug (no HTTP fetch). Other URLs: `ScrapeTitleAsync` → `m_songTitleAtAddr` (in-memory).

**URL-to-GUID presence tracking** (implemented): `harvest.cs:AppendAcceptedLog` writes `data/url-guids.csv` (`minutes, serverAddr, encoded_url, encoded_title, guid1|guid2|...`). `WelcomeContext.cs` scans this file into three buckets: (a) `Shared songs with people here:` (arriving + room member both present), (b) `Songs people here often play:` (room only), (c) `Songs {name} has played in other sessions:` (arriving history). Also extracts dominant artist per bucket (≥2 occurrences) and most recently played song for the arriving player. Logged as `|songs:N`, `|room-songs:N`, `|history-songs:N`, `|room-artist:X`, `|artist:X`, `|recent-song`.

**Encounter hinting:** `timeTogether.json` format: list of `{Key: guid1+guid2 (64 chars), Value: ".NET TimeSpan string"}`. Bands: 10–500h = meaningful musician relationships; <10h = incidental; >500h = likely bots. Daily Essay already uses this.

**Web user IP cache** (`WelcomeContext.cs`): `_webUserIps` is refreshed from `data/telemetry.log` every hour. Tracks IPs that have visited the web UI. Used in welcome context to signal whether an arriving player has a web presence.


## Daily Essay — Easter Egg

A prose essay about the last 24 hours on the Jamulus network appears 60 seconds after page load. It's a genuine narrative — specific names, instruments, durations, servers — not a data dump. Written in the user's native language. Nobody expects it. After the essay is shown, a 4-hour in-memory suppression prevents auto-refetch (saving LLM cost on unattended tabs); the suppression resets on manual page refresh.

### Geographic Centers (hard-coded)

Derived from telemetry.log IP cluster analysis (top-100 human IPs, last 30 days). Center assignment: nearest Euclidean distance to center lat/lon. **All centers now use `gemini-2.5-pro`.**

| ID | Center coords | Countries served | Language | LLM tier |
|----|--------------|-----------------|----------|----------|
| `EU-W` | 51.5°N 6.5°E | DE, AT, NL, FR, BE, SE, NO, TR, BG, PL, CZ, DK, FI, HU, RO, CH, SK, RS, HR, GR, UA, LT, LV, EE | German | **Pro** |
| `IT` | 44.0°N 11.0°E | IT | Italian | **Pro** |
| `UK` | 51.5°N 1.5°W | GB, IE | English | **Pro** |
| `NA-E` | 42.0°N 80.0°W | US, CA, MX | English | **Pro** |
| `NA-W` | 47.5°N 122.0°W | US, CA | English | **Pro** |
| `SA` | 15.0°S 60.0°W | BR, AR, CL, CO, PE, VE, EC, BO, PY, UY, GT, CU, DO, HN, SV, NI, CR, PA, PR, GY, SR | Spanish/Portuguese | **Pro** |
| `SEA` | 14.0°N 108.0°E | TH, PH, CN, SG, JP, KR, MY, ID, VN, HK, TW, AU, NZ | Thai / Chinese / English | **Pro** |
| `WORLD` | 0°N 0°E | fallback for all others | English | **Pro** |

Language follows the individual user's IP country code (same `_countryLanguage` mapping as welcome system), not the center's primary language. Cache key: `(centerId, languageCode)`. Thai and Chinese are native — LLM writes directly, no translation step.

**WarmupComplete gate:** `DailyEssayService.GetEssayHtmlAsync` returns null if `IpAnalyticsService.WarmupComplete` is false. This prevents essay generation (which calls ip-api for the client IP) before the ip-api cache is warm, avoiding noisy throttle logs on early requests.

**System prompt:** `data/essay-system-prompt.txt` — re-read per call (not compiled in). Falls back to inline default if missing.

### Cache Strategy

In-memory `ConcurrentDictionary<string, (string html, DateTime generatedAt)>` keyed by `"EU-W:German"`, `"IT:Italian"`, etc. **8-hour TTL.** Cache survives restarts only incidentally — first visitor after restart triggers regeneration (acceptable; they wait up to 70s semaphore timeout).

**Scheduled pre-generation:** `DailyEssayService.StartScheduledPregeneration()` runs a background loop that pre-generates the top 8 center:language pairs at 05:00, 12:00, and 19:00 UTC — just before the EU morning, EU afternoon/NA-E morning, and EU evening peak activity waves. Log prefix `[ESSAY-SCHED]`. On-demand generation still fires for rare pairs not in the list.

**Anti-duplication lock (critical):** One `SemaphoreSlim(1,1)` per cache key. When two users simultaneously request an uncached essay, the second waits on the same semaphore — zero duplicate LLM calls.

### Data Inputs Per Essay

Assembled in C# at generation time:

- **Local sessions** (servers within ~400km of center): from `census.csv` last 24h, joined to `censusgeo.csv` (names, instruments) and `server.csv` (server names, cities). Grouped by server, sorted by tick count. Include: server name, city, player names+instruments, session start/end approximated from first/last tick, total player-minutes.
- **Global highlights**: top 5 sessions by player-count or total player-minutes, worldwide, same 24h window. Include cross-geography notes (e.g., 4 countries in one session).
- **URL context** (optional enrichment): from `urls.csv`, last 24h — song titles being played on active servers. Surface if interesting (title-artist pairs, not raw URLs).

### Prompt Structure

Same discipline as welcome messages: WRONG/RIGHT pairs over rule bullets, banned-phrases list, no word target. Paragraphs have jobs:

1. **Local arc** — what happened on servers nearest this user's region. Named players, named servers, real durations, time of day.
2–3. **Regional texture** — vignettes from the wider region. What ran longest, who was unexpected, what genre dominated.
4–5. **Global reach** — something from the other side of the world in the same 24h window. Cross-geography sessions, surprising geography.
6+. **Earned extras** — only if data supports: notable songs from URLs, unusual instrument combinations, cross-server encounters.

BANNED phrases (seed list, expand as failures emerge): "global community", "passion for music", "connect musicians worldwide", "around the world", "the beauty of", "musicians gathered", "love of music", "transcends borders".

WRONG/RIGHT example (seed — add pairs for each new failure mode):
```
WRONG: Many musicians played on servers near you yesterday, sharing their love of music.
RIGHT: At just past midnight, a trio on Cascadia Jazz held a 47-minute session — RustyShackleford on drums, a bassist from Salem, and an unidentified keyboard player who never quite resolved the bridge but kept trying.
```

### LLM Cost Model (as of 2026-06-05, updated)

All centers use **Gemini 2.5 Pro** (SA center added). Thinking tokens are included in the output price — no separate charge.

| | Rate |
|---|---|
| Input | $1.25 / 1M tokens (≤200K context) |
| Output (incl. thinking) | $10.00 / 1M tokens |

**Per-call estimate:** ~1,500 input tokens + ~1,500 output tokens ≈ **$0.017/call**.

**Cache TTL: 8 hours.** Scheduled pre-gen fires at 05:00, 12:00, 19:00 UTC for top 8 pairs; on-demand covers rare pairs. Each pair generates at most 3 calls/day from the schedule. Rare on-demand pairs also cap at 3/day with 8h TTL.

**Top 8 scheduled pairs** (cover ~84% of all generations, derived from telemetry analysis):
`EU-W:German`, `IT:Italian`, `NA-W:English`, `EU-W:Dutch`, `UK:French`, `NA-E:English`, `UK:English`, `WORLD:English`

**Expected daily call volume:**

| Traffic | Calls/day | Cost/day | Cost/month |
|---|---|---|---|
| Typical | 24–30 | $0.41–$0.51 | ~$13–$16 |
| Busy | 40 | $0.68 | ~$20 |

Flash for all would be ~$3–5/month (4× cheaper). Switch back if Pro costs escalate.

**Reader hit counter:** `output.log` logs `prev_readers=N` on every scheduled or on-demand generation — how many readers were served from the expiring cache entry before it was replaced. Use this to compute real per-reader cost: `$0.017 / prev_readers`. Scheduled pre-gen shows `prev_readers=0` when no user triggered it organically.

**Diagnostics commands:**
```bash
# On-demand generations (user-triggered)
grep "\[ESSAY\] generating" /root/JamFan22/JamFan22/output.log | tail -30

# Scheduled pre-gen events (05:00, 12:00, 19:00 UTC)
grep "\[ESSAY-SCHED\]" /root/JamFan22/JamFan22/output.log | tail -30

# LLM call details (model, language, ms, errors)
grep "\[ESSAY-LLM\]" /root/JamFan22/JamFan22/output.log | tail -30

# Full context + essay output for each call
tail -200 /root/JamFan22/JamFan22/data/essay-llm.log

# Which (center:language) pairs have been generated today
grep "generating" /root/JamFan22/JamFan22/data/essay-llm.log | grep "$(date -u +%Y-%m-%d)"
```

**AI Studio monitoring:** Google AI Studio API dashboard shows per-model token usage and cumulative cost. Ground truth as days accumulate.

**Trigger for spoofed EU-W essay (German IP, Pro model, from this host):**
```bash
curl -k -s --max-time 35 -H "X-Forwarded-For: 85.214.0.1" "https://localhost/api/nearby-essay"
```

## TODO

See [TODO.md](TODO.md).

## Server Suppression Rules

See comment block at the `// ── Server suppression` marker in `Api.cshtml.cs`.

## Active Only Filter

Hides quiet servers (two consecutive silent readings) from the grid. Implemented entirely client-side in `Client.cshtml`.

### Design aspiration: calm, concise product

The grid should feel settled. Avoid states that look broken or unfinished: stale ghosts lingering after a server leaves, rapid-fire animations, or cards snapping to new positions without transition. Every visual change should look intentional.

**Ghost discipline:** ghosts exist to smooth a single transition, not to linger. A quiet-server ghost (`dataset.quietSid`) is preserved so the server can reclaim its spot on return, but it must not accumulate — if a different server fills the ghost, the map entry is cleaned up immediately. If ghosts pile up visibly, that is a bug.

### Quiet-out / quiet-in design (Active Only mode)

**Do not invent new animation code for quiet-server transitions.** Use the existing `remove_server` / `add_server` machinery:

- **Quiet-out**: same `all 0.75s ease-out` fade as `remove_server` multi-column — opacity/transform/blur, then ghost placed at 750ms via `replaceWith`. Ghost stored in `_quietGhosts` (keyed by `sId`). `optimizeGhostPlacement` runs as normal.
- **Quiet-in**: detected at the top of the `update_server_meta_*` handler (before `getElementById`, because the element is no longer in the DOM). A fresh card is built from the new data (`createServerElement`), dropped into the waiting ghost if still present (original spot), otherwise fills the first available ghost or appends. Same `all 0.75s ease-in` entrance as `add_server`.
- `_quietFading` set prevents re-triggering the fade during the 750ms window.
- `purgeTrailingGhosts` skips ghosts with `dataset.quietSid` — quiet ghosts are not trailing clutter.
- If a different `add_server` fills the quiet ghost first, the `_quietGhosts` entry is deleted so the returning server falls through to normal placement.

**CSS rule** (`site.css`): `#client-grid.active-only-active .quiet-server { display:none !important }` — still present for non-Active-Only quiet display (emoji only). In Active Only mode the element is removed from the DOM via ghost, so this rule is irrelevant for the active hidden state.

## Live User Count — Tier Classification

**To count live human users, run `traffic.py`** (at `/root/JamFan22/traffic.py`). Default window is 60 min; pass an argument for a different window (e.g. `python3 traffic.py 30`). Do not write ad-hoc telemetry parsing — this script already implements the correct tier classification and owner/fleet IP exclusions.

The classification logic for reference — to count live human users, parse `data/telemetry.log` excluding owner/fleet IPs (`134.19.*`, `172.56.*`, `24.17.80.236`, `75.253.12.89`, `50.116.25.151`). Use a 15–60 minute window. Classify each unique IP:

| Tier | Label | Criteria |
|------|-------|----------|
| 1 | Clear human interaction | Any of: `friend_visibility`, `click_musician`, `click_listen`, `click_more`, `nearby_toggle`, `tracked_arrival`, `ui_active`, `ui_nearby`, `ui_dark`, `ui_hide`, `tab_switch`, `nearby_layout`, `grid_layout`, `hover_server`, `scroll_depth` |
| 2+ | Genuine browser, behavioral evidence | Legit UA (contains Mozilla/Chrome/Firefox/Safari/Edge) + `jamulus.live` referrer + at least one of: `session_start`, `return_visit`, `tab_hidden`, `tab_visible`, `scroll_depth`, `hover_server` |
| 2− | Suspect — spoofable | Legit UA + `jamulus.live` referrer, but `http_req` only — indistinguishable from a curl with spoofed headers |
| 3 | No evidence | No jamulus.live referrer, bot/crawler UA, known bots (`GoogleAssociationService`, `facebookexternalhit`, `AhrefsBot`, `YandexBot`, `zgrab`, `curl`, `Jamulus-CentralDefense`, etc.) |

Note: bookmarked direct visits (no referrer) with behavioral events may appear in Tier 3 despite being real users.

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

## Infrastructure notes

nginx is not installed on this host. For routing/proxy needs use ASP.NET middleware or iptables.

## Fleet JSON-RPC

Raw TCP, newline-delimited JSON, port 9999 (Syncopé: 9998). Firewalled to `134.199.209.51` + `147.182.199.22` only. Secret in `/secret.txt`. Two messages per session: `jamulus/apiAuth` first, then the method call; read response after each. See the `// Fleet JSON-RPC` comment block in `Program.cs` for the call pattern.

**Key methods:** `jamulusserver/getClients` (fields: `instrumentCode` int, `countryName` string — NOT `instrument`/`country`), `jamulusserver/sendClientChatMessage` (params: `{channelId, message}`).

**Trigger:** `/ip-allowed/{ip}` receives `channelId`+`rpcport` from the fleet binary; welcome fires async after the 200 response.

