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
- **`tail-welcomes.py`** — tails the welcome events log.

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
