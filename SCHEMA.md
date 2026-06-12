# Data Directory Schema

| File | Schema | Purpose |
|------|--------|---------|
| `census.csv` | `minutes, player_guid, server_ip:port` | Every observed jammer+server presence tick. **Proposed 4th field:** `audio` — `null` if no silence sample available for that tick, `0` if player was silent, `N>0` = number of active audio connections detected during that sample. Sources: fleet silence (`m_fleetSilenceStatus` via UDP ChannelLevelList) and non-fleet silence (`NonFleetSilencePoller` via gjprobe). Would allow census consumers (essays, welcome context, etc.) to distinguish idle connections from live playing. Not yet implemented. |
| `censusgeo.csv` | `hash, name, instrument, city, nation` | Jammer profile snapshots |
| `server.csv` | `ip:port, name, city, nation` | Known server metadata |
| `urls.csv` | `minutes, source, ip:port, encoded_url, title` | URLs matching chat-patterns.txt (source = lounge/server/client) |
| `urls-rejected.csv` | `minutes, source, addr, encoded_url` | URLs that failed chat-patterns.txt — review to find missing patterns |
| `telemetry.log` | `minutes, ip, hash, flags, reserved, json` | Web client events. `json.a` = event name. **Human-signal events** (indicate genuine UI engagement): `hover_server`, `tab_hidden`, `tab_visible`, `grid_layout`, `nearby_layout`, `nearby_toggle`, `friend_visibility`. IPs with only `http_req` events (`distinct_types=1`) are passive pollers or bots. IPs with 5+ human-signal events in a 10-min window are deeply engaged humans. |
| `stream-gate.json` | `{ActiveIp, ExpiryUtc, JamulusServer}` | Single-slot streaming lease |
| `stream-reservations.json` | `[{Ip, JamulusServer, DayOfWeek, StartHour, DurationHours}]` | Weekly recurring stream reservations. DayOfWeek is C# `DayOfWeek` int (0=Sun). Current slots: Studio D Wed 02, Hot Texas! Thu 23, Capitol Wed 22 + Sun 18, Freiheit Mon 20 + Thu 16 (all UTC). |
| `server-lore.json` | `{serverKey: {name, emoji, tagline, themes[], events[{name, schedule, description, listen_url?}], milestones[], ambient_inducements[]}}` | Per-server persona for the LLM welcome system. Re-read per call. Keyed by `ip:port`. Current entries: Freiheit, Hot Texas!, Capitol, Studio D. |
| `fleet-guid-ip.csv` | `timestamp_minutes, guid, client_ip, server_ip, blocked` | GUID↔IP evidence from fleet /ip-allowed calls |
| `fleet-server-ips.txt` | `ip:jamulusPort:rpcPort` per line, `#` = comment | Allowlist for /chat-url-server + RPC port map for StreamAnnouncementService. `FleetIpAllowlist` strips to IP; `StreamAnnouncementService.ReadFleetRpcPorts` reads `ip:jamulusPort` → `rpcPort`. |
| `fleet-rpc-ports.txt` | `ip=rpcPort` per line, `#` = comment | Override RPC port per server IP. Read by `FleetRpcPorts.GetPort(ip)` (5-min TTL). Default 9999. Only needed for non-standard ports (e.g. Syncopé 9998). |
| `bands.json` | `{bands: [{id, band_name?, primary_server?, members: [{guid, name, canary_level}]}]}` | Band definitions written weekly by `band-finder.py`. Used by `BandIndex`. Canary levels: `strong` (one = trigger), `pair` (two = trigger). 12h reload TTL. |
| `welcome-config.txt` | `key=value` lines | Hot-reloadable thresholds + model/temp for welcome system. Defaults: `crew_elsewhere_min_mins=30`, `forecast_min_mins=60`, `forecast_sighting_hours=2`, `forecast_max_entries=4`, `model=gemini-2.5-flash`, `temperature=0.9`, `pro_temperature=1.3`, `llm_timeout_ms=2000` |
| `welcome-system-prompt.txt` | plain text | LLM system prompt for welcome messages. Re-read on every call. Falls back to a hardcoded one-liner if missing. |
| `welcome-group-announce-prompt.txt` | plain text | System prompt for the group-announce LLM call (player-identified events). |
| `essay-system-prompt.txt` | plain text | System prompt for daily essay LLM calls. Used by `DailyEssayService`. |
| `url-guids.csv` | `minutes, serverAddr, encoded_url, encoded_title, guid1\|guid2\|...` | Written by `harvest.cs:AppendGuidLog` after `ScrapeTitleAsync` resolves. Used by `WelcomeContext` to find shared songs. |
| `gemini-key.txt` | plain text | Gemini API key. Loaded at startup by both `WelcomeMessageGenerator` and `DailyEssayService`. |

**Files outside `data/`:**
- **`predicted.csv`** (project root, `JamFan22/`): `predictedMinute, player_guid, name, serverName` — written by `predict-future.py`, read by `WelcomeContext` for co-jammer arrival forecasts.
- **`wwwroot/livestatus.json`**: server-key → `{clients: [guid, ...]}` map of currently active players; read by `WelcomeContext` to find crew elsewhere.
- **`ping-log.csv`** lives in `JamFan22/` (not in `data/`). ~120MB — never read without bounds. Used by `prospect-radar.py`.

Trim scripts run via cron: `trim_census.sh` / `trim_server.sh` (weekly), `trim_censusgeo.sh` (daily), `cull-data.sh` (daily — telemetry.log, fleet-guid-ip.csv 15d; urls.csv 90d; urls-rejected.csv 15d).
