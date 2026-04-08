#!/usr/bin/env python3
"""
user-awareness.py
Total Situation Awareness — correlates website telemetry with Jamulus census
to build a behavioural profile of each visitor.

Run from /root/JamFan22/JamFan22/:
    python3 ../user-awareness.py [--hash <musicianHash>] [--ip <clientIP>]

Without flags, prints a summary of all visitors ranked by engagement.
"""

import json, csv, re, sys, os, time
from collections import defaultdict
from datetime import datetime, timezone
from urllib.parse import unquote
from urllib.request import urlopen
import ipaddress

# ---------------------------------------------------------------------------
# Paths (relative to JamFan22/ app root)
# ---------------------------------------------------------------------------
BASE = os.path.dirname(os.path.abspath(__file__)) + "/JamFan22"
TELEMETRY_PATH   = BASE + "/data/telemetry.log"
CENSUS_PATH      = BASE + "/data/census.csv"
JOIN_EVENTS_PATH = BASE + "/join-events.csv"
GUID_NAMES_PATH  = BASE + "/guidNamePairs.json"

EPOCH_SECONDS = 1672531200  # 2023-01-01 00:00:00 UTC

def minute_to_utc(m):
    ts = EPOCH_SECONDS + m * 60
    return datetime.fromtimestamp(ts, tz=timezone.utc).strftime("%Y-%m-%d %H:%M UTC")

_geo_cache = {}
def geoip(ip):
    """Look up city/ISP for an IP via ip-api.com (free, no key). Cached."""
    ip = ip.replace("::ffff:", "")
    if ip in _geo_cache:
        return _geo_cache[ip]
    try:
        url = f"http://ip-api.com/json/{ip}?fields=city,regionName,country,isp,org,as,lat,lon,status"
        with urlopen(url, timeout=5) as r:
            data = json.loads(r.read())
        time.sleep(0.1)  # stay well under 45 req/min
        result = data if data.get("status") == "success" else {}
    except Exception:
        result = {}
    _geo_cache[ip] = result
    return result

def load_blocklists():
    """Returns (blocked_asns, blocked_cidrs, allowed_cidrs) from the wwwroot block files."""
    blocked_asns, blocked_cidrs, allowed_cidrs = set(), [], []
    for fname in ("wwwroot/asn-blocks.txt", "wwwroot/asn-ip-client-blocks.txt"):
        path = BASE + "/" + fname
        try:
            for line in open(path):
                line = line.strip()
                if not line: continue
                allow = line.startswith("!")
                entry = line.lstrip("!")
                if re.match(r"AS\d+", entry):
                    asn = entry.split()[0]
                    if not allow: blocked_asns.add(asn)
                elif "/" in entry or re.match(r"\d+\.\d+", entry):
                    try:
                        net = ipaddress.ip_network(entry, strict=False)
                        (allowed_cidrs if allow else blocked_cidrs).append(net)
                    except ValueError: pass
        except FileNotFoundError: pass
    return blocked_asns, blocked_cidrs, allowed_cidrs

def check_blocklist(ip, blocked_asns, blocked_cidrs, allowed_cidrs):
    """Returns (is_blocked, is_allowed_override, reason) for an IP."""
    geo = geoip(ip)
    asn_str = geo.get("as", "")          # e.g. "AS212238 Datacamp Limited"
    asn_num = asn_str.split()[0] if asn_str else ""

    try: addr = ipaddress.ip_address(ip)
    except ValueError: return False, False, ""

    # Check allow overrides first
    for net in allowed_cidrs:
        if addr in net: return False, True, f"!{net} (allow override)"

    # Check blocked CIDRs
    for net in blocked_cidrs:
        if addr in net: return True, False, f"CIDR {net}"

    # Check blocked ASNs
    if asn_num and asn_num in blocked_asns:
        return True, False, f"{asn_str}"

    return False, False, ""

def parse_args():
    filter_hash, filter_ip = None, None
    args = sys.argv[1:]
    for i, a in enumerate(args):
        if a == "--hash" and i + 1 < len(args): filter_hash = args[i + 1]
        if a == "--ip"   and i + 1 < len(args): filter_ip   = args[i + 1]
    return filter_hash, filter_ip

# ---------------------------------------------------------------------------
# Load guidNamePairs: hash → display name
# ---------------------------------------------------------------------------
def load_names():
    names = {}
    try:
        with open(GUID_NAMES_PATH) as f:
            pairs = json.load(f)
        for p in pairs:
            k = p.get("Key", "").strip()
            v = re.sub(r'&[^;]+;', '', p.get("Value", "")).strip()
            v = unquote(v).strip()
            if k:
                names[k] = v or "(no name)"
    except Exception as e:
        print(f"[warn] Could not load guidNamePairs: {e}")
    return names

# ---------------------------------------------------------------------------
# Load join-events: hash → {name, instrument, country, city}
# ---------------------------------------------------------------------------
def load_join_profiles():
    profiles = {}
    try:
        with open(JOIN_EVENTS_PATH, newline='') as f:
            for row in csv.reader(f):
                if len(row) < 7: continue
                h = row[2].strip()
                if not h: continue
                profiles[h] = {
                    "name":       unquote(row[3].replace('+', ' ')).strip(),
                    "instrument": unquote(row[4].replace('+', ' ')).strip(),
                    "city":       unquote(row[5].replace('+', ' ')).strip(),
                    "country":    unquote(row[6].replace('+', ' ')).strip(),
                }
    except Exception as e:
        print(f"[warn] Could not load join-events: {e}")
    return profiles

# ---------------------------------------------------------------------------
# Load telemetry: list of dicts per event row
# ---------------------------------------------------------------------------
def load_telemetry(filter_hash=None, filter_ip=None):
    visitors = defaultdict(lambda: {
        "ip": None,
        "hash": None,
        "sessions": [],       # list of {start_min, end_min, duration_sec, ua, width}
        "actions": [],        # list of {minute, action, data}
        "dark_mode": None,
        "nearby_only": None,
        "_pending_sec": None, # orphan session_end that arrived before any session_start
    })

    try:
        with open(TELEMETRY_PATH) as f:
            for line in f:
                line = line.strip()
                if not line: continue
                parts = line.split(',', 5)
                if len(parts) < 6: continue
                try:
                    minute = int(parts[0])
                    ip     = parts[1].strip()
                    h      = parts[2].strip()
                    d      = int(parts[3].strip())
                    n      = int(parts[4].strip())
                    evt    = json.loads(parts[5].strip())
                except Exception:
                    continue

                if filter_hash and h != filter_hash: continue
                if filter_ip   and filter_ip not in ip: continue

                key = h if h != "anon" else ip
                v = visitors[key]
                v["ip"]   = ip
                v["hash"] = h

                action = evt.get("a", "")

                if action == "session_start":
                    pending = v.pop("_pending_sec", None)
                    v["_pending_sec"] = None
                    v["sessions"].append({
                        "start_min": minute,
                        "end_min":   None,
                        "duration":  pending,  # carry over orphan end if present
                        "ua":        evt.get("ua", ""),
                        "width":     evt.get("w"),
                    })
                elif action == "session_end":
                    sec = evt.get("sec")
                    # attach to the last open session
                    attached = False
                    for s in reversed(v["sessions"]):
                        if s["end_min"] is None:
                            s["end_min"]  = minute
                            s["duration"] = sec
                            attached = True
                            break
                    if not attached:
                        v["_pending_sec"] = sec  # store for next session_start
                elif action in ("ui_dark",):
                    v["dark_mode"] = evt.get("s")
                elif action in ("ui_nearby",):
                    v["nearby_only"] = evt.get("s")
                else:
                    v["actions"].append({"minute": minute, "action": action, "data": evt})

    except FileNotFoundError:
        print(f"[error] telemetry.log not found at {TELEMETRY_PATH}")
    return visitors

# ---------------------------------------------------------------------------
# Load census into a per-hash server timeline: hash → sorted list of (minute, server)
# ---------------------------------------------------------------------------
def load_census(hashes_of_interest):
    timeline = defaultdict(list)
    try:
        with open(CENSUS_PATH, newline='') as f:
            for row in csv.reader(f):
                if len(row) < 3: continue
                try: minute = int(row[0].strip())
                except: continue
                h      = row[1].strip()
                server = row[2].strip()
                if h in hashes_of_interest:
                    timeline[h].append((minute, server))
    except FileNotFoundError:
        print(f"[error] census.csv not found at {CENSUS_PATH}")
    # sort each timeline
    for h in timeline:
        timeline[h].sort()
    return timeline

def server_at(timeline, h, minute, window_before=10, window_after=20):
    """Return (server_before, server_after) relative to `minute`."""
    entries = timeline.get(h, [])
    before = [s for (m, s) in entries if minute - window_before <= m < minute]
    after  = [s for (m, s) in entries if minute < m <= minute + window_after]
    srv_before = before[-1] if before else None
    srv_after  = after[0]  if after  else None
    return srv_before, srv_after

# ---------------------------------------------------------------------------
# Infer device from user-agent string
# ---------------------------------------------------------------------------
def device_label(ua):
    ua = ua or ""
    if "iPhone" in ua or "Android" in ua and "Mobile" in ua: return "Phone"
    if "iPad" in ua or "Android" in ua: return "Tablet"
    if "Windows" in ua: return "Windows"
    if "Macintosh" in ua: return "Mac"
    if "Linux" in ua: return "Linux"
    return "Unknown"

def browser_label(ua):
    ua = ua or ""
    if "Edg/" in ua: return "Edge"
    if "Chrome" in ua: return "Chrome"
    if "Firefox" in ua: return "Firefox"
    if "Safari" in ua and "Chrome" not in ua: return "Safari"
    return "Other"

# ---------------------------------------------------------------------------
# Build engagement score: rough proxy for how much they love the app
# ---------------------------------------------------------------------------
def engagement_score(v):
    sessions = v["sessions"]
    total_sec = sum(s["duration"] or 0 for s in sessions)
    n_clicks  = len([a for a in v["actions"] if a["action"].startswith("click") or a["action"].startswith("tab")])
    return len(sessions) * 2 + total_sec // 60 + n_clicks * 3

# ---------------------------------------------------------------------------
# Print a profile for one visitor
# ---------------------------------------------------------------------------
def print_profile(key, v, census_tl, names, join_profiles, verbose=True,
                  blocked_asns=None, blocked_cidrs=None, allowed_cidrs=None):
    h = v["hash"]
    ip = (v["ip"] or "").replace("::ffff:", "")
    name = names.get(h, "")
    jp   = join_profiles.get(h, {})

    display_name = jp.get("name") or name or "(anonymous)"
    instrument   = jp.get("instrument", "")
    city         = jp.get("city", "")
    country      = jp.get("country", "")

    sessions = v["sessions"]
    total_sec = sum(s["duration"] or 0 for s in sessions)
    devices   = list(dict.fromkeys(
        f"{device_label(s['ua'])}/{browser_label(s['ua'])}" for s in sessions if s["ua"]
    ))
    widths = sorted(set(s["width"] for s in sessions if s["width"]))

    geo = geoip(ip)
    geo_str = ", ".join(filter(None, [geo.get("city"), geo.get("regionName"), geo.get("country")]))
    isp_str = geo.get("isp") or geo.get("org") or ""

    print(f"\n{'='*70}")
    print(f"  {display_name}  [{h[:12]}...]  IP: {ip}")
    blocked, allowed_override, bl_reason = False, False, ""
    if blocked_asns is not None:
        blocked, allowed_override, bl_reason = check_blocklist(ip, blocked_asns, blocked_cidrs, allowed_cidrs)
    bl_tag = "  [!ALLOW OVERRIDE]" if allowed_override else ("  [** BLOCKLISTED: " + bl_reason + " **]" if blocked else "")
    if geo_str: print(f"  Location (IP): {geo_str}   ISP: {isp_str}{bl_tag}")
    if instrument: print(f"  Instrument: {instrument}   Location (Jamulus): {city}, {country}")
    print(f"  Devices: {', '.join(devices) or 'n/a'}   Widths: {widths}")
    print(f"  Sessions: {len(sessions)}   Total time: {total_sec//60}m {total_sec%60}s")
    if v["dark_mode"] is not None:
        print(f"  Preferences: dark={'on' if v['dark_mode'] else 'off'}, nearby-only={'on' if v['nearby_only'] else 'off'}")
    print()

    # Action summary
    action_counts = defaultdict(int)
    for a in v["actions"]: action_counts[a["action"]] += 1
    if action_counts:
        summary = "  Interactions: " + ", ".join(f"{k}×{n}" for k, n in sorted(action_counts.items()))
        print(summary)

    if not verbose: return

    # Session-by-session breakdown with relocation detection
    print()
    print("  Sessions & Relocations:")
    for i, s in enumerate(sessions):
        start_str = minute_to_utc(s["start_min"])
        dur_str   = f"{s['duration']//60}m {s['duration']%60}s" if s["duration"] else "?"
        width_str = f"w={s['width']}" if s["width"] else ""
        print(f"    [{i+1}] {start_str}  dur={dur_str}  {width_str}")

        if h and h != "anon":
            srv_before, srv_after = server_at(census_tl, h, s["start_min"])
            if srv_before or srv_after:
                moved = srv_before and srv_after and srv_before != srv_after
                arrow = f"  {'** RELOCATED **' if moved else 'stayed'}"
                print(f"         Jamulus: {srv_before or '(offline)'} → {srv_after or '(offline)'}{arrow}")

    # Clicked musicians
    clicked = [a for a in v["actions"] if a["action"] == "click_musician"]
    if clicked:
        print()
        print(f"  Clicked musicians: {len(clicked)} times")
        for c in clicked[:5]:
            mid = c["data"].get("id", "?")
            ctrl = " (Ctrl-click)" if c["data"].get("c") else ""
            print(f"    - {mid}{ctrl} at {minute_to_utc(c['minute'])}")
        if len(clicked) > 5: print(f"    ... and {len(clicked)-5} more")

    # Hovered servers
    hovered = [a for a in v["actions"] if a["action"] == "hover_server"]
    if hovered:
        srv_ids = list(dict.fromkeys(a["data"].get("id","?") for a in hovered))
        print(f"  Hovered servers: {', '.join(srv_ids[:6])}")

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
def main():
    filter_hash, filter_ip = parse_args()

    print("Loading data...")
    names         = load_names()
    join_profiles = load_join_profiles()
    blocked_asns, blocked_cidrs, allowed_cidrs = load_blocklists()
    visitors      = load_telemetry(filter_hash, filter_ip)

    if not visitors:
        print("No telemetry data matched.")
        return

    # Load census only for hashes we care about (keeps memory sane)
    hashes = {v["hash"] for v in visitors.values() if v["hash"] and v["hash"] != "anon"}
    print(f"Loading census for {len(hashes)} musician hash(es)...")
    census_tl = load_census(hashes)

    # Sort by engagement
    ranked = sorted(visitors.items(), key=lambda kv: engagement_score(kv[1]), reverse=True)

    verbose = (filter_hash is not None or filter_ip is not None or len(ranked) == 1)

    print(f"\n{'='*70}")
    print(f"  TOTAL SITUATION AWARENESS  —  {len(ranked)} unique visitor(s)")
    print(f"{'='*70}")

    if not verbose:
        # Summary table
        print(f"\n  {'Name':<22} {'Hash':<14} {'Sessions':>8} {'Time':>8} {'Int':>5} {'Location':<28} {'Devices'}")
        print(f"  {'-'*22} {'-'*14} {'-'*8} {'-'*8} {'-'*5} {'-'*28} {'-'*20}")
        for key, v in ranked:
            h = v["hash"]
            ip = (v["ip"] or "").replace("::ffff:", "")
            jp = join_profiles.get(h, {})
            name = jp.get("name") or names.get(h, "(anon)")
            sessions = v["sessions"]
            total_sec = sum(s["duration"] or 0 for s in sessions)
            n_int = len(v["actions"])
            devices = list(dict.fromkeys(
                f"{device_label(s['ua'])}/{browser_label(s['ua'])}" for s in sessions if s["ua"]
            ))
            geo = geoip(ip)
            loc = ", ".join(filter(None, [geo.get("city"), geo.get("country")])) or ""
            is_blocked, is_allowed, _ = check_blocklist(ip, blocked_asns, blocked_cidrs, allowed_cidrs)
            bl = " [BL]" if is_blocked else (" [!]" if is_allowed else "")
            print(f"  {name:<22} {h[:12]:<14} {len(sessions):>8} {total_sec//60:>6}m   {n_int:>4}   {loc:<28}{bl}")
        print()
        print("  Run with --hash <hash> or --ip <ip> for a full profile.")
    else:
        for key, v in ranked:
            print_profile(key, v, census_tl, names, join_profiles, verbose=True,
                          blocked_asns=blocked_asns, blocked_cidrs=blocked_cidrs, allowed_cidrs=allowed_cidrs)

    print()

if __name__ == "__main__":
    main()
