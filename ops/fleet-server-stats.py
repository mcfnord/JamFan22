#!/usr/bin/env python3
"""Fleet server performance analysis.
Metrics measured from the youngest fleet server's first tick forward.
Deduplicates (minute, guid, server) before computing time metrics.
"""
import collections, datetime, sys

BASE = datetime.datetime(2023, 1, 1)
DATA = "/root/JamFan22/JamFan22/data"

# Owner hashes to exclude (from CLAUDE.md)
OWNER_HASHES = {
    "9dd8bae07c44800edd80024c02a0bbf6",
    "8bfcb9816ab178394d56f6155cab4e73",
    "52f7652674c02b2b9f1070c072881116",
}
OWNER_PREFIXES = ("9d13bfe92e03", "cfab1ba8c67c")

def is_owner(guid):
    if guid in OWNER_HASHES: return True
    return any(guid.startswith(p) for p in OWNER_PREFIXES)

# Load fleet servers
fleet_servers = []
for line in open(f"{DATA}/fleet-server-ips.txt"):
    s = line.strip()
    if s: fleet_servers.append(s)

# Load censusgeo -> guid -> name (for lobby detection)
guid_name = {}
for line in open(f"{DATA}/censusgeo.csv"):
    parts = line.strip().split(",")
    if len(parts) >= 2:
        guid_name[parts[0]] = parts[1].lower().replace("+", " ")

def is_lobby(guid):
    return "lobby" in guid_name.get(guid, "")

# Find youngest fleet server's first tick
print("Finding youngest fleet server first tick...")
first_ticks = {}
with open(f"{DATA}/census.csv") as f:
    for line in f:
        parts = line.strip().split(",")
        if len(parts) < 3: continue
        try: minute = int(parts[0])
        except: continue
        server = parts[2]
        if server in fleet_servers and server not in first_ticks:
            first_ticks[server] = minute

if not first_ticks:
    print("No fleet data found in census.csv")
    sys.exit(1)

start_tick = max(first_ticks.values())
start_dt = BASE + datetime.timedelta(minutes=start_tick)
print(f"Common start tick: {start_tick} ({start_dt.strftime('%Y-%m-%d %H:%M UTC')})")
print(f"Fleet servers with data: {len(first_ticks)}/{len(fleet_servers)}")
print()

# Pass 2: collect per-server stats from start_tick forward
# Each server tracks:
#   seen[(minute, guid)] = audible (bool)  -- for dedup
#   guid_days[guid] = set of date strings  -- for repeat visitor detection
print("Reading census data from start tick...")

server_data = {s: {"seen": {}, "guid_days": collections.defaultdict(set)} for s in fleet_servers}

with open(f"{DATA}/census.csv") as f:
    for line in f:
        parts = line.strip().split(",")
        if len(parts) < 3: continue
        try: minute = int(parts[0])
        except: continue
        if minute < start_tick: continue
        guid, server = parts[1], parts[2]
        if server not in server_data: continue
        if is_owner(guid) or is_lobby(guid): continue

        audible = len(parts) >= 4 and parts[3] == "1"
        key = (minute, guid)
        # Keep audible=True if ever true for this (minute, guid)
        prev = server_data[server]["seen"].get(key, False)
        server_data[server]["seen"][key] = prev or audible

        date_str = (BASE + datetime.timedelta(minutes=minute)).strftime("%Y-%m-%d")
        server_data[server]["guid_days"][guid].add(date_str)

print("Computing metrics...\n")

# Compute metrics per server
results = []
for server in fleet_servers:
    if server not in first_ticks:
        results.append((server, None))
        continue
    d = server_data[server]
    seen = d["seen"]
    guid_days = d["guid_days"]

    all_guids = {guid for (_, guid) in seen}
    unique_guids = len(all_guids)

    # Group by minute
    minute_guids = collections.defaultdict(set)      # minute -> set of guids
    minute_audible = collections.defaultdict(set)    # minute -> set of audible guids
    for (minute, guid), audible in seen.items():
        minute_guids[minute].add(guid)
        if audible:
            minute_audible[minute].add(guid)

    # Time metrics (in minutes)
    t_any = sum(1 for m, gs in minute_guids.items() if gs)
    t_2plus = sum(1 for m, gs in minute_guids.items() if len(gs) >= 2)
    t_2plus_audible = sum(1 for m, gs in minute_audible.items() if len(gs) >= 2)

    # Repeat visitors: visited on 2+ distinct days
    repeat_guids = {g for g, days in guid_days.items() if len(days) >= 2}
    repeat_count = len(repeat_guids)
    repeat_pct = (repeat_count / unique_guids * 100) if unique_guids else 0

    results.append((server, {
        "unique_guids": unique_guids,
        "t_any_h": t_any / 60,
        "t_2plus_h": t_2plus / 60,
        "t_2plus_aud_h": t_2plus_audible / 60,
        "repeat_visitors": repeat_count,
        "repeat_pct": repeat_pct,
        "total_visitors": unique_guids,
    }))

# Sort by repeat visitors descending
results.sort(key=lambda x: (x[1] or {}).get("repeat_visitors", -1), reverse=True)

# Print table
HDR = f"{'Server':<30} {'Uniq':>5} {'Repeat':>7} {'Rpt%':>6} {'t≥1 h':>7} {'t≥2 h':>7} {'t≥2aud':>7}"
print(HDR)
print("-" * len(HDR))
for server, m in results:
    if m is None:
        print(f"{server:<30}  (no census data)")
        continue
    print(f"{server:<30} {m['unique_guids']:>5} {m['repeat_visitors']:>7} {m['repeat_pct']:>5.0f}%"
          f" {m['t_any_h']:>7.1f} {m['t_2plus_h']:>7.1f} {m['t_2plus_aud_h']:>7.1f}")
