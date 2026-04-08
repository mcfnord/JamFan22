#!/usr/bin/env python3
"""
prospect-radar.py
Who's online right now (connected to a public Jamulus directory) that has
previously shown the 7-directory sweep pattern — and have they found JamFan22?

Flow:
  ping-log IPs  →  join-events (IP→names)  →  live directory (name match)
  telemetry (IP→visited site?)

Run from repo root:
    python3 prospect-radar.py
"""

import os, json
from collections import defaultdict
from urllib.request import urlopen
from urllib.parse import unquote

BASE        = os.path.dirname(os.path.abspath(__file__)) + "/JamFan22"
PING_LOG    = BASE + "/ping-log.csv"
JOIN_EVENTS = BASE + "/join-events.csv"
TELEMETRY   = BASE + "/data/telemetry.log"

MIN_PORTS = 6   # "7-directory" sweep threshold

DIRECTORY_URLS = {
    "Any Genre 1":       "http://24.199.107.192:5001/servers_data/anygenre1.jamulus.io:22124/cached_data",
    "Any Genre 2":       "http://24.199.107.192:5001/servers_data/anygenre2.jamulus.io:22224/cached_data",
    "Any Genre 3":       "http://24.199.107.192:5001/servers_data/anygenre3.jamulus.io:22624/cached_data",
    "Genre Rock":        "http://24.199.107.192:5001/servers_data/rock.jamulus.io:22424/cached_data",
    "Genre Jazz":        "http://24.199.107.192:5001/servers_data/jazz.jamulus.io:22324/cached_data",
    "Genre Classical/Folk": "http://24.199.107.192:5001/servers_data/classical.jamulus.io:22524/cached_data",
    "Genre Choral/BBShop":  "http://24.199.107.192:5001/servers_data/choral.jamulus.io:22724/cached_data",
}

# ---------------------------------------------------------------------------
# 1. Find sweeper IPs from ping-log
# ---------------------------------------------------------------------------
def load_sweeper_ips():
    per_min = defaultdict(set)
    with open(PING_LOG) as f:
        for line in f:
            parts = line.split(',')
            if len(parts) < 5: continue
            try:
                minute = int(parts[0].strip())
                ip     = parts[1].strip()
                dport  = int(parts[3].strip())
            except:
                continue
            per_min[(ip, minute)].add(dport)
    sweepers = set()
    for (ip, _), ports in per_min.items():
        if len(ports) >= MIN_PORTS:
            sweepers.add(ip)
    print(f"  {len(sweepers)} IPs have ever shown the sweep pattern")
    return sweepers

# ---------------------------------------------------------------------------
# 2. Build name→IP and IP→profile maps from join-events
# ---------------------------------------------------------------------------
def load_join_profiles(sweeper_ips):
    # name_to_ips: normalised name -> set of IPs that have used that name
    # ip_profile:  ip -> {names, instruments, countries}
    name_to_ips = defaultdict(set)
    ip_profile  = defaultdict(lambda: {"names": [], "instruments": set(), "countries": set()})

    with open(JOIN_EVENTS) as f:
        for line in f:
            parts = line.split(',')
            if len(parts) < 12: continue
            client_ip  = parts[11].strip().split(':')[0]
            name       = unquote(parts[3].strip().replace('+', ' ')).strip()
            instrument = unquote(parts[4].strip().replace('+', ' ')).strip()
            country    = unquote(parts[6].strip().replace('+', ' ')).strip()
            if not name: continue
            name_to_ips[name.lower()].add(client_ip)
            p = ip_profile[client_ip]
            if name not in p["names"]:
                p["names"].append(name)
            if instrument: p["instruments"].add(instrument)
            if country:    p["countries"].add(country)

    # Drop names that are too generic to identify an individual:
    # - appear on more than 2 distinct IPs (shared across many people)
    # - too short (single char, or just punctuation/emoji)
    # - well-known placeholder names
    GENERIC = {"no name", "lobby [0]", "lobby [1]", "lobby", "-", "test",
               "listener", "streamer", "no name here", ""}
    def is_generic(name):
        n = name.lower().strip()
        if n in GENERIC: return True
        if len(n) <= 1:  return True
        if len(name_to_ips[n]) > 2: return True
        return False

    # Restrict to sweeper IPs only, excluding generic names
    sweeper_name_to_ips = {n: ips & sweeper_ips for n, ips in name_to_ips.items()
                           if (ips & sweeper_ips) and not is_generic(n)}
    return sweeper_name_to_ips, ip_profile

# ---------------------------------------------------------------------------
# 3. Fetch live directory — return list of {name, instrument, country,
#    server_name, directory}
# ---------------------------------------------------------------------------
def fetch_live_clients():
    live = []
    for dirname, url in DIRECTORY_URLS.items():
        try:
            with urlopen(url, timeout=8) as r:
                data = json.loads(r.read())
            for srv in data.get("servers_data", []):
                for c in srv.get("clients", []):
                    name = c.get("name", "").strip()
                    if name:
                        live.append({
                            "name":       name,
                            "instrument": c.get("instrument", ""),
                            "country":    c.get("country", ""),
                            "server":     srv.get("name", ""),
                            "directory":  dirname,
                        })
        except Exception as e:
            print(f"  [warn] {dirname}: {e}")
    print()
    return live

# ---------------------------------------------------------------------------
# 4. Check telemetry for site visits
# ---------------------------------------------------------------------------
def load_web_visitors():
    visitors = set()
    try:
        with open(TELEMETRY) as f:
            for line in f:
                parts = line.split(',', 5)
                if len(parts) < 2: continue
                visitors.add(parts[1].strip().replace('::ffff:', ''))
    except FileNotFoundError:
        pass
    return visitors

# ---------------------------------------------------------------------------
# 5. Match live clients against sweeper names
# ---------------------------------------------------------------------------
def main():
    sweeper_ips          = load_sweeper_ips()
    sweeper_name_to_ips, ip_profile = load_join_profiles(sweeper_ips)
    live_clients         = fetch_live_clients()
    web_visitors         = load_web_visitors()

    # Match: for each live client, check if their name maps to a sweeper IP
    seen_ips = set()
    matches  = []

    for client in live_clients:
        key = client["name"].lower()
        ips = sweeper_name_to_ips.get(key, set())
        if not ips:
            continue
        for ip in ips:
            if ip in seen_ips:
                continue
            seen_ips.add(ip)
            p = ip_profile[ip]
            matches.append({
                "ip":         ip,
                "name":       client["name"],
                "instrument": client["instrument"],
                "country":    client["country"],
                "server":     client["server"],
                "directory":  client["directory"],
                "all_names":  p["names"],
                "visited":    ip in web_visitors,
                "shared":     len(p["names"]) > 4,
            })

    unconverted = [m for m in matches if not m["visited"]]
    converted   = [m for m in matches if     m["visited"]]
    # Fewer aliases = more likely an individual, sort those first
    unconverted.sort(key=lambda m: len(m["all_names"]))
    converted.sort(  key=lambda m: len(m["all_names"]))

    print()
    print("=" * 80)
    print(f"  SLOT-SWEEPERS ONLINE NOW")
    print(f"  {len(matches)} matched  |  {len(unconverted)} never visited JamFan22  |  {len(converted)} already have")
    print("=" * 80)

    def show(m):
        tag   = "  [shared NAT]" if m["shared"] else ""
        names = ", ".join(m["all_names"][:4]) + ("…" if len(m["all_names"]) > 4 else "")
        print(f"  {m['name']:<22s}  {m['instrument']:<20s}  {m['country']:<18s}  "
              f"{m['directory']:<18s}  {m['server'][:30]}{tag}")
        if len(m["all_names"]) > 1:
            print(f"    (also seen as: {names})")

    if unconverted:
        print(f"\n  — Never visited JamFan22 ({len(unconverted)}) —")
        for m in unconverted:
            show(m)

    if converted:
        print(f"\n  — Already use JamFan22 ({len(converted)}) —")
        for m in converted:
            show(m)

    if not matches:
        print("\n  No slot-sweepers currently connected to any public directory.")

    print()

if __name__ == "__main__":
    main()
