# JamFan22 — Ops & Scripts

Operational scripts, diagnostic workflows, and support tooling. Scripts that read `data/` with relative paths (`jammer-map.py`, `predict-future.py`, `session-regulars.py`, `sense-drops.py`) must be run with working directory `/root/JamFan22/JamFan22/` — cron entries already do this.

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

## Dormant Fleet Monitor (`dormant-monitor.py`)

**`/root/dormant-monitor.py`** runs 24/7 as `dormant-monitor.service` (systemd). Every 20 minutes it scores geographic demand near each dormant AWS instance (using `census.csv`) and starts/stops instances via boto3.

Key files:
- **`/root/dormant-instances.json`** — instance config (id, region, lat/lon, threshold, stop_streak). An optional `"schedule": "HH:MM-HH:MM"` (UTC, may wrap midnight) puts an instance on a fixed daily uptime instead of demand scoring — currently Paris (`08:00-01:00`) and Milan (`15:00-01:00`), windows padded ±1h so they survive CET/CEST shifts without edits. `threshold-calibrate.py` (daily 09:00 UTC cron) skips scheduled instances.
- **`/root/dormant-ip-cache.json`** — last-known public IP per instance_id; persists across restarts
- **`/root/dormant-thresholds.json`** — live threshold overrides (no restart needed)
- **`data/fleet-dormant-ips.txt`** — written by the monitor; ip:port lines for currently running dormant instances (used by `Program.cs` to tag them as dormant)

**IP tracking:** When an instance starts and gets a new public IP, the monitor patches `fleet-server-ips.txt` and `fleet-rpc-ports.txt` in place (replacing old IP with new). It can only patch entries that already exist — adding a new dormant instance to `dormant-instances.json` also requires manually adding its initial entry to `fleet-server-ips.txt`.

**Historical analysis caveat:** `fleet-server-ips.txt` and `dormant-ip-cache.json` only hold each sleeper's *current* IP — dormant instances change IP on every start (sometimes several times a day), so cross-referencing `census.csv` against the current file will silently miss most historical fleet sessions. To check who used the fleet in the past, first rebuild a per-instance IP timeline from `/root/dormant-monitor.log` (`grep "IP changed"` — lines are `"<instance>: IP changed <old> → <new>"`) and match census rows against the IP that was live at that timestamp, not just today's IP. Always-on relays (e.g. `50.116.25.151`, `24.199.107.192`) don't have this problem — their IP is static.

**Logs:** `journalctl -u dormant-monitor -f` or `/root/dormant-monitor.log`.

## Scheduled Scripts (cron)

- **`predict-future.py`** — runs 6× daily; writes `tooltips.json` and prediction outputs to `wwwroot/`. Run with `cd /root/JamFan22/JamFan22/`.
- **`jammer-map.py`** — runs every 2 hours; writes `wwwroot/jammer-map.json`. Run with `cd /root/JamFan22/JamFan22/`.
- **`gcdump-monitor.sh`** — runs every 2 hours; snapshots both production and debug (port 5000) instances into `heapdumps/`, 12-file rolling window per label.
- **`cull-data.sh`** — runs daily; trims telemetry.log, fleet-guid-ip.csv, urls-rejected.csv (15 days), urls.csv (90 days).
- **`band-finder.py`** — runs weekly; writes `JamFan22/data/bands.json`. Run with `cd /root/JamFan22/`.
- **`session-regulars.py`** — runs weekly; writes `data/session-regulars.json`. Run with `cd /root/JamFan22/JamFan22/`.
- **`recurring-slots.py`** — runs weekly; logs to `/tmp/recurring-slots.log`.

## Manual Diagnostic Scripts

- **`user-awareness.py`** — correlates telemetry.log + census.csv. Run from any directory: `python3 /root/JamFan22/ops/user-awareness.py [--hash <32-char-hash>] [--ip <ip>]`.
- **`sense-drops.py`** — reports census.csv dropout gaps. Run first when investigating coverage gaps. Run with `cd /root/JamFan22/JamFan22/`.
- **`prospect-radar.py`** — finds musicians doing 7-slot sweeps who haven't visited JamFan22 yet.
- **`traffic.py`** — counts live human users by tier. Default window 60 min; pass arg for different window (e.g. `python3 traffic.py 30`). Do not write ad-hoc telemetry parsing — this script already implements correct tier classification and owner/fleet IP exclusions.
- **`fleet-probe.py`**, **`probe-1028.py`**, **`fleet-server-stats.py`** — fleet server diagnostics.
- **`fleet-report.py`** — historical fleet activity summary for a time window: welcome messages (with signals), silence state changes, and session durations from census. Default 4h; `--hours N` or `--today`. Run from any directory.
- **`tail-welcomes.py`** — live tail of welcome-llm.log. Auto-translates non-English messages via Gemini, shows signal context alongside each message. For reviewing history use `fleet-report.py` instead.

## Live User Count — Tier Classification

Run `traffic.py` to count live human users. Classification logic for reference:

Parse `data/telemetry.log` excluding owner/fleet IPs (`134.19.*`, `172.56.*`, `24.17.80.236`, `75.253.12.89`, `50.116.25.151`). Use a 15–60 minute window. Classify each unique IP:

| Tier | Label | Criteria |
|------|-------|----------|
| 1 | Clear human interaction | Any of: `friend_visibility`, `click_musician`, `click_listen`, `click_more`, `nearby_toggle`, `tracked_arrival`, `ui_active`, `ui_nearby`, `ui_dark`, `ui_hide`, `tab_switch`, `nearby_layout`, `grid_layout`, `hover_server`, `scroll_depth` |
| 2+ | Genuine browser, behavioral evidence | Legit UA (contains Mozilla/Chrome/Firefox/Safari/Edge) + `jamulus.live` referrer + at least one of: `session_start`, `return_visit`, `tab_hidden`, `tab_visible`, `scroll_depth`, `hover_server` |
| 2− | Suspect — spoofable | Legit UA + `jamulus.live` referrer, but `http_req` only — indistinguishable from a curl with spoofed headers |
| 3 | No evidence | No jamulus.live referrer, bot/crawler UA, known bots (`GoogleAssociationService`, `facebookexternalhit`, `AhrefsBot`, `YandexBot`, `zgrab`, `curl`, `Jamulus-CentralDefense`, etc.) |

Note: bookmarked direct visits (no referrer) with behavioral events may appear in Tier 3 despite being real users.


## Log-Marker Glossary — human activity vs. timer noise (output.log)

To find human-generated events in the last N minutes (`tail -200 /root/JamFan22/JamFan22/output.log`):

**Human signals:**
- `[IP-ALLOWED]` with `caller=50.116.25.151 query=147.182.199.22` — lounge joined a fleet server (StreamGate or manual)
- `[BAND-SOON]` — real players detected about to arrive (human presence signal)
- `[VISIT]` with a `Client City:` line — a human loaded the web UI
- Player `NOW`/`GONE` lines in welcome context output — musicians joining/leaving
- `[gojam.Client DEBUG] Connected!` — gojam established a new server connection
- `[fleet-rpc-channel] <UTC ts> connected/disconnected` — fleet server WS channel (restart or dormant
  wake, not a player, but operationally interesting). **Timestamped 2026-08-19**; it sits between the
  marker and the word, house style, so grep `\[fleet-rpc-channel\].*connected`, never the two as one
  literal. Until then these lines carried no time at all, which is why the channel could not be used
  as an outage signal (see `fleetwatch.py` on central command).

**Automated/timer noise to ignore:**
- `[ON-DEMAND-REFRESH]` — directory cache misses in call-servers-php
- `[RSS]`, `[ProcessServerListsAsync]`, `[DEBUG] Processing Genre` — routine directory cycles
- `[GeoResolve]`, `[FILTER]`, `[SENSOR-BLOCKED]` — per-join welcome context computation
- `[LEVEL-POLL]`, `[EAR]` — silence polling loops
