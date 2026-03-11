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
- **IpAnalyticsService** — IP→ASN lookups, rate-limit backoff, SmartNations, country code cache.
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

### Key Design Notes

- `EncounterTracker.DetailsFromHash()` takes `jamulusListURLs` + `lastReportedList` as params (avoids circular dep with `JamulusCacheManager`)
- `JamulusAnalyzer.LocalizedText(nationCode, ...)` is static, takes nation code as first param
- `DurationHere(server, who, nationCode)` takes nation code as param — no per-request singleton state
- Port is read from `PORT` env var; defaults to 443 with HTTPS (`keyJan26.pfx`); non-443 runs plain HTTP (used for test builds on port 5000)
