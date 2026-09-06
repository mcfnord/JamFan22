#!/usr/bin/env python3
"""
Computes loyal session regulars for each scheduled Jamulus session.
Run weekly from JamFan22/ directory (cron: Sunday midnight).
Output: data/session-regulars.json

Rules:
  - Look at the last 8 occurrences of each session slot in census.csv
  - A "regular" must: (a) have attended >= 5 of those 8 sessions,
    AND (b) been present at the MOST RECENT session
  - If they missed last week, they're not mentioned this week.
"""
import json, csv, sys, urllib.parse
from datetime import datetime, timedelta
from collections import defaultdict

EPOCH = datetime(2023, 1, 1)
LOOKBACK = 8
MIN_SESSIONS = 5

def minutes_to_dt(m):
    return EPOCH + timedelta(minutes=int(m))

def cs_dow_to_py(cs_dow):
    # C# DayOfWeek: 0=Sun,1=Mon,...,6=Sat → Python weekday: 0=Mon,...,6=Sun
    return (cs_dow - 1) % 7

def in_session_window(dt, py_dow, start_hour, duration_hours):
    if dt.weekday() == py_dow and dt.hour >= start_hour:
        return True
    # Handle midnight overflow (e.g. Hot Texas! 23:00–03:00 UTC)
    if start_hour + duration_hours > 24:
        overflow = start_hour + duration_hours - 24
        next_dow = (py_dow + 1) % 7
        if dt.weekday() == next_dow and dt.hour < overflow:
            return True
    return False

def session_date_key(dt, py_dow, start_hour):
    """Normalize to session's start-day date string (handles midnight overflow)."""
    if dt.weekday() == py_dow and dt.hour >= start_hour:
        return dt.strftime("%Y-%m-%d")
    # Overflow into next calendar day — attribute back to start day
    return (dt - timedelta(days=1)).strftime("%Y-%m-%d")

with open("data/stream-reservations.json") as f:
    reservations = json.load(f)

with open("data/server-lore.json") as f:
    lore = json.load(f)

# Tracked servers and their session parameters
tracked = {}
for res in reservations:
    server = res["JamulusServer"]
    tracked[server] = {
        "py_dow":     cs_dow_to_py(res["DayOfWeek"]),
        "cs_dow":     res["DayOfWeek"],
        "start_hour": res["StartHour"],
        "duration":   res.get("DurationHours", 3),
    }

# Accumulate census ticks per (server, session-date)
session_guids = {s: defaultdict(set) for s in tracked}

print("Scanning census.csv …", flush=True)
with open("data/census.csv") as f:
    for line in f:
        parts = line.strip().split(',')
        # census.csv rows carry an optional 4th column (audible) on servers with
        # level data — i.e. every fleet server this script tracks. Anchor to >= 3,
        # never == 3, or ~73% of tracked rows are silently dropped.
        if len(parts) < 3:
            continue
        server = parts[2]
        if server not in tracked:
            continue
        try:
            dt = minutes_to_dt(parts[0])
        except ValueError:
            continue
        t = tracked[server]
        if not in_session_window(dt, t["py_dow"], t["start_hour"], t["duration"]):
            continue
        dk = session_date_key(dt, t["py_dow"], t["start_hour"])
        session_guids[server][dk].add(parts[1])

# Build latest display names from censusgeo.csv (last row per GUID wins)
names = {}
with open("data/censusgeo.csv") as f:
    for row in csv.reader(f):
        if len(row) >= 2:
            decoded = urllib.parse.unquote_plus(row[1]).strip()
            if decoded and "no name" not in decoded.lower():
                names[row[0]] = decoded

result = {}

for server, t in tracked.items():
    all_dates = sorted(session_guids[server].keys())
    if not all_dates:
        print(f"  {server}: no session data found")
        continue

    recent = all_dates[-LOOKBACK:]
    last_date = recent[-1]
    last_guids = session_guids[server][last_date]

    guid_count = defaultdict(int)
    for d in recent:
        for guid in session_guids[server][d]:
            guid_count[guid] += 1

    # Must have attended last session AND hit loyalty threshold
    qualified = sorted(
        [(g, c) for g, c in guid_count.items() if c >= MIN_SESSIONS and g in last_guids],
        key=lambda x: -x[1]
    )

    # Lobby bots attend every session, so they always pass the loyalty
    # threshold — never list them as regulars
    regular_names = [names[g] for g, _ in qualified
                     if g in names and "lobby" not in names[g].lower()]

    server_lore = lore.get(server, {})
    events = server_lore.get("events", [])
    session_name = events[0].get("name", "session") if events else "session"

    result[server] = {
        "session_name":    session_name,
        "day_of_week":     t["cs_dow"],
        "start_hour":      t["start_hour"],
        "duration_hours":  t["duration"],
        "regulars":        regular_names,
        "last_session_date": last_date,
        "sessions_scanned": len(recent),
    }
    print(f"  {server}: {session_name} | last={last_date} | {len(regular_names)} regulars: {regular_names[:6]}")

with open("data/session-regulars.json", "w") as f:
    json.dump(result, f, indent=2)

print("Done → data/session-regulars.json")
