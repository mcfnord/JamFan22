#!/usr/bin/env python3
"""
band-finder.py — mine census.csv for recurring band assemblies (2+ members).

Key insight: when 1-2 members appear together, the whole band tends to show up.
We find tight cliques where overlap ratio is high, then analyze timing patterns.

Usage:
  python3 band-finder.py [--top N] [--min-sessions N] [--window-minutes N]
"""

import csv, sys, argparse, re, json, unicodedata
from collections import defaultdict, Counter
from datetime import datetime, timezone, timedelta
from itertools import combinations
from urllib.parse import unquote_plus

# Decorative symbols common in many unrelated names — skip as name identifiers.
_DECO_SYMBOLS = {'★', '彡', '♛', '✺', '♤', '✿', '◆', '◇', '▲', '▶', '▷'}

def _extract_symbols(name):
    result = set()
    for c in name:
        cp = ord(c)
        cat = unicodedata.category(c)
        if (cat.startswith('S') or
                0x1F300 <= cp <= 0x1FAFF or
                0x2600  <= cp <= 0x27BF):
            result.add(c)
    return result

def _emoji_crew_name(emoji):
    try:
        word = unicodedata.name(emoji, '').title()
        if word:
            return f"{word} Gang"
    except Exception:
        pass
    return f"{emoji} Gang"

def detect_band_name(display_names, cities):
    """Return a band name based on shared emoji, shared city, or None."""
    # 1. Shared emoji (2+ members, skip decorative)
    sym_counts = Counter()
    for n in display_names:
        for s in _extract_symbols(n):
            sym_counts[s] += 1
    for sym, cnt in sym_counts.most_common():
        if cnt < 2:
            break
        if sym in _DECO_SYMBOLS:
            continue
        return _emoji_crew_name(sym)

    # 2. Shared city (2+ members, decoded)
    decoded_cities = [unquote_plus(c) for c in cities if c]
    city_counts = Counter(decoded_cities)
    top = city_counts.most_common(1)
    if top and top[0][1] >= 2:
        return f"{top[0][0]} Crew"

    return None

CENSUS    = "/root/JamFan22/JamFan22/data/census.csv"
CENSUSGEO = "/root/JamFan22/JamFan22/data/censusgeo.csv"
EPOCH     = datetime(2023, 1, 1, tzinfo=timezone.utc)
DAYS      = ["Sun","Mon","Tue","Wed","Thu","Fri","Sat"]

OWNER_HASHES = {
    "9dd8bae07c44800edd80024c02a0bbf6",
    "8bfcb9816ab178394d56f6155cab4e73",
    "52f7652674c02b2b9f1070c072881116",
}

def minutes_to_dt(m):
    return EPOCH + timedelta(minutes=int(m))

def is_noise_name(name):
    """Filter lobby placeholders only. Frequency thresholds weed out the rest."""
    if not name:
        return True
    if "lobby" in name.lower():
        return True
    return False

def load_names():
    """Last-seen name and city per GUID from censusgeo.csv. Last row wins (most recent)."""
    names = {}
    cities = {}
    try:
        with open(CENSUSGEO) as f:
            for row in csv.reader(f):
                if len(row) >= 2 and row[0]:
                    if row[1]:
                        names[row[0]] = row[1]
                    if len(row) >= 4:
                        cities[row[0]] = row[3]  # col 3 = city
    except FileNotFoundError:
        pass
    return names, cities

def load_census(window_minutes):
    """
    Build assembly events: groups of GUIDs co-present on same server within
    a time window. Each event is (server, bucket_start_minutes, frozenset_of_guids).

    Also returns guid_buckets: guid -> set of bucket_ids (any server) for prediction.
    """
    print("Loading census.csv...", flush=True)
    buckets = defaultdict(set)        # (server, bucket_id) -> set of guids
    guid_servers = defaultdict(set)
    guid_buckets = defaultdict(set)   # guid -> set of bucket_ids (server-agnostic)

    with open(CENSUS) as f:
        for i, row in enumerate(csv.reader(f)):
            if len(row) < 3:
                continue
            minute, guid, server = row[0].strip(), row[1].strip(), row[2].strip()
            if not minute.isdigit() or not guid or guid in OWNER_HASHES:
                continue
            bucket_id = int(minute) // window_minutes
            buckets[(server, bucket_id)].add(guid)
            guid_servers[guid].add(server)
            guid_buckets[guid].add(bucket_id)
            if i % 1_000_000 == 0:
                print(f"  ...{i:,} rows", flush=True)

    print(f"  {len(buckets):,} buckets across {len(guid_servers):,} unique GUIDs", flush=True)

    # guid_server_buckets: guid -> server -> set of bucket_ids (for server-specific canary rates)
    guid_server_buckets = defaultdict(lambda: defaultdict(set))
    for (server, bucket_id), guids in buckets.items():
        for guid in guids:
            guid_server_buckets[guid][server].add(bucket_id)

    events = [
        (server, bucket_id * window_minutes, frozenset(guids))
        for (server, bucket_id), guids in buckets.items()
        if len(guids) >= 2
    ]
    print(f"  {len(events):,} multi-player events (2+ GUIDs in same window)", flush=True)
    return events, guid_servers, guid_buckets, guid_server_buckets

def find_bands(events, names, min_sessions, top_n):
    # Work with just the guid-sets for clique finding; keep index for timing later
    guid_sets = [e[2] for e in events]

    print("Counting pair co-occurrences...", flush=True)
    pair_events = defaultdict(list)  # (a,b) -> list of event indices

    for idx, guids in enumerate(guid_sets):
        for a, b in combinations(guids, 2):
            key = (min(a, b), max(a, b))
            pair_events[key].append(idx)
        if idx % 50_000 == 0:
            print(f"  ...{idx:,}/{len(events):,} events", flush=True)

    strong_pairs = {pair: idxs for pair, idxs in pair_events.items()
                    if len(idxs) >= min_sessions}
    print(f"  {len(strong_pairs):,} pairs with >= {min_sessions} sessions together", flush=True)

    adj = defaultdict(set)
    for (a, b) in strong_pairs:
        adj[a].add(b)
        adj[b].add(a)

    # Filter out noise-named GUIDs from the clique search
    noise_guids = {g for g in adj if is_noise_name(names.get(g, ""))}
    if noise_guids:
        print(f"  Filtering {len(noise_guids)} noise-named GUIDs (lobby/numbers/etc.)", flush=True)
        for g in noise_guids:
            for nb in list(adj[g]):
                adj[nb].discard(g)
            del adj[g]
        # Also strip from pair_events
        strong_pairs = {(a,b): idxs for (a,b), idxs in strong_pairs.items()
                        if a not in noise_guids and b not in noise_guids}

    print("Finding cliques (3+ via Bron-Kerbosch, then qualifying pairs)...", flush=True)
    cliques = []

    def bron_kerbosch(R, P, X):
        if not P and not X:
            if len(R) >= 3:
                cliques.append(frozenset(R))
            return
        pivot = max(P | X, key=lambda v: len(adj[v] & P))
        for v in list(P - adj[pivot]):
            bron_kerbosch(R | {v}, P & adj[v], X & adj[v])
            P.remove(v)
            X.add(v)

    bron_kerbosch(set(), set(adj.keys()), set())

    # Add qualifying 2-member pairs not already covered by a larger clique
    already_paired = set()
    for c in cliques:
        for a, b in combinations(c, 2):
            already_paired.add((min(a, b), max(a, b)))
    pairs_added = 0
    for (a, b), idxs in strong_pairs.items():
        if (a, b) not in already_paired:
            cliques.append(frozenset([a, b]))
            pairs_added += 1
    print(f"  {len(cliques)} cliques found ({len(cliques)-pairs_added} groups of 3+, {pairs_added} pairs)", flush=True)

    results = []
    for clique in cliques:
        members = list(clique)
        pairs = list(combinations(members, 2))

        # Events where ALL members present
        seed_key = (min(pairs[0][0], pairs[0][1]), max(pairs[0][0], pairs[0][1]))
        candidate_idxs = set(pair_events.get(seed_key, []))
        for a, b in pairs[1:]:
            key = (min(a, b), max(a, b))
            candidate_idxs &= set(pair_events.get(key, []))

        full_idxs = [idx for idx in candidate_idxs if clique <= guid_sets[idx]]
        full_sessions = len(full_idxs)

        if full_sessions < min_sessions:
            continue

        # Overlap ratio
        pair_counts = []
        for a, b in pairs:
            key = (min(a, b), max(a, b))
            pc = len(pair_events.get(key, []))
            if pc > 0:
                pair_counts.append(full_sessions / pc)
        overlap_ratio = sum(pair_counts) / len(pair_counts) if pair_counts else 0

        # Timing: day-of-week + hour-of-day for each full session
        session_times = []
        server_counts = Counter()
        for idx in full_idxs:
            srv, t_mins, _ = events[idx]
            dt = minutes_to_dt(t_mins)
            session_times.append(dt)
            server_counts[srv] += 1

        top_srv, top_cnt = server_counts.most_common(1)[0]
        pserver = top_srv if top_cnt / full_sessions >= 0.65 else None

        results.append({
            "members": members,
            "full_sessions": full_sessions,
            "overlap_ratio": overlap_ratio,
            "score": full_sessions * overlap_ratio,
            "session_times": session_times,
            "server_counts": server_counts,
            "primary_server": pserver,
        })

    results.sort(key=lambda r: -r["score"])
    return results[:top_n]

def predict_assembly(band, events, guid_buckets, names, lookahead=1,
                     primary_server=None, guid_server_buckets=None):
    """
    For each quorum size N (1..size-1), compute:
      P(full band assembles within lookahead hours | N members currently together)

    Also per-member: given this player is online (any server), P(full assembly soon).
    'Full assembly' = all members on the same server in the same bucket (as detected).
    """
    members = frozenset(band["members"])
    n_total = len(members)

    # Full-assembly bucket_ids (from detected events, same-server definition)
    # We want: for each full-session event, what bucket_id was it?
    guid_sets = [e[2] for e in events]
    full_bucket_ids = set()
    for e in events:
        srv, t_mins, guids = e
        if members <= guids:  # all members present on same server
            bucket_id = t_mins // 60  # window_minutes=60 baked in via t_mins
            full_bucket_ids.add(bucket_id)

    if not full_bucket_ids:
        return None

    # Build lookahead set: any bucket_id within lookahead steps of a full assembly
    full_window = set()
    for b in full_bucket_ids:
        for d in range(lookahead + 1):
            full_window.add(b - d)  # "will assemble within lookahead buckets after now"

    # Per-member solo trigger rate — server-specific if primary_server set
    member_rates = []
    for m in members:
        if primary_server and guid_server_buckets:
            appearances = guid_server_buckets.get(m, {}).get(primary_server, set())
        else:
            appearances = guid_buckets.get(m, set())
        if not appearances:
            continue
        triggers = sum(1 for b in appearances if b in full_window)
        rate = triggers / len(appearances)
        member_rates.append((rate, triggers, len(appearances), m, names.get(m, m[:8])))
    member_rates.sort(reverse=True)

    # Quorum trigger rates
    bucket_members = defaultdict(set)
    for m in members:
        if primary_server and guid_server_buckets:
            src = guid_server_buckets.get(m, {}).get(primary_server, set())
        else:
            src = guid_buckets.get(m, set())
        for b in src:
            bucket_members[b].add(m)

    quorum_rates = {}
    for n in range(1, n_total):
        buckets_with_n = [b for b, ms in bucket_members.items() if len(ms) >= n]
        if not buckets_with_n:
            continue
        triggers = sum(1 for b in buckets_with_n if b in full_window)
        quorum_rates[n] = (triggers, len(buckets_with_n))

    return {"quorum": quorum_rates, "members": member_rates, "n_total": n_total}


def timing_summary(session_times):
    """Summarize when a band plays: top day+hour slots."""
    if not session_times:
        return "  No timing data"

    # Count (weekday, hour) pairs — weekday 0=Mon in Python
    slot_counts = Counter()
    for dt in session_times:
        # Python weekday: 0=Mon; convert to Sun=0 like DAYS list
        wd = (dt.weekday() + 1) % 7  # Mon→1 … Sun→0
        slot_counts[(wd, dt.hour)] += 1

    # Hour-of-day distribution (collapse across days)
    hour_counts = Counter()
    for (wd, hr), cnt in slot_counts.items():
        hour_counts[hr] += cnt
    peak_hour = hour_counts.most_common(1)[0][0]

    # Day distribution
    day_counts = Counter()
    for (wd, hr), cnt in slot_counts.items():
        day_counts[wd] += cnt
    top_days = [DAYS[wd] for wd, _ in day_counts.most_common(3)]

    # Top 3 specific slots
    top_slots = slot_counts.most_common(3)
    slot_str = "  |  ".join(
        f"{DAYS[wd]} ~{hr:02d}:00 UTC (×{cnt})"
        for (wd, hr), cnt in top_slots
    )

    # Date range
    earliest = min(session_times).strftime("%Y-%m-%d")
    latest   = max(session_times).strftime("%Y-%m-%d")

    lines = []
    lines.append(f"  Top slots:   {slot_str}")
    lines.append(f"  Peak hour:   ~{peak_hour:02d}:00 UTC  |  Active days: {', '.join(top_days)}")
    lines.append(f"  Date range:  {earliest} → {latest}")
    return "\n".join(lines)

STRONG_CANARY_THRESH = 0.74
PAIR_CANARY_THRESH   = 0.75

def write_bands_json(bands, names, cities, path):
    # Preserve manually-set 'disabled' flags from the existing file (keyed by band_name).
    disabled_names = set()
    try:
        import os
        if os.path.exists(path):
            with open(path) as _f:
                _old = json.load(_f)
            for _b in _old.get("bands", []):
                if _b.get("disabled") and _b.get("band_name"):
                    disabled_names.add(_b["band_name"])
    except Exception:
        pass

    cutoff = datetime.now(timezone.utc) - timedelta(days=90)
    output = []
    for i, band in enumerate(bands, 1):
        pred = band.get("prediction")
        times = band["session_times"]
        if not times:
            continue
        last_seen_dt = max(times)
        if last_seen_dt < cutoff:
            continue

        hits_by_guid = {}
        rate_by_guid = {}
        if pred:
            for rate, hits, total, guid, _name in pred["members"]:
                rate_by_guid[guid] = rate
                hits_by_guid[guid] = hits

        MIN_CANARY_HITS = 3  # require at least 3 confirmed trigger events

        members = []
        for g in band["members"]:
            raw = names.get(g, "")
            display = unquote_plus(raw)
            rate = rate_by_guid.get(g, 0.0)
            hits = hits_by_guid.get(g, 0)
            if hits < MIN_CANARY_HITS:
                level = "none"
            else:
                level = ("strong" if rate >= STRONG_CANARY_THRESH else
                         "pair"   if rate >= PAIR_CANARY_THRESH   else "none")
            members.append({
                "guid": g,
                "name": display,
                "solo_trigger_rate": round(rate, 3),
                "canary_level": level,
            })

        display_names = [m["name"] for m in members]
        member_cities = [cities.get(g, "") for g in band["members"]]
        band_name = band.get("band_name") or detect_band_name(display_names, member_cities)

        all_high = all(m["solo_trigger_rate"] >= 0.90 for m in members)
        entry = {
            "id": i,
            "last_seen": last_seen_dt.strftime("%Y-%m-%d"),
            "full_sessions": band["full_sessions"],
            "overlap_ratio": round(band["overlap_ratio"], 3),
            "always_together": all_high,
            "members": members,
        }
        if band_name:
            entry["band_name"] = band_name
            if band_name in disabled_names:
                entry["disabled"] = True
        ps = band.get("primary_server")
        if ps:
            entry["primary_server"] = ps
        output.append(entry)

    data = {"generated_utc": datetime.now(timezone.utc).isoformat(), "bands": output}
    with open(path, "w") as f:
        json.dump(data, f, indent=2)
    print(f"\nWrote {len(output)} bands → {path}")

def print_report(bands, names):
    print("\n" + "="*70)
    print("BAND DETECTION REPORT")
    print("="*70)
    if not bands:
        print("No bands found. Try lowering --min-sessions.")
        return

    for i, band in enumerate(bands, 1):
        members = band["members"]
        member_names = [names.get(g, g[:8]+"…") for g in members]

        top_servers = band["server_counts"].most_common(2)
        srv_str = "  ".join(f"{ip} (×{cnt})" for ip, cnt in top_servers)
        extra = len(band["server_counts"]) - 2
        if extra > 0:
            srv_str += f"  +{extra} more"

        ps = band.get("primary_server")
        ps_tag = f"  [server-specific: {ps}]" if ps else "  [cross-server]"
        print(f"\n{'─'*70}")
        print(f"#{i}  score={band['score']:.1f}  |  {band['full_sessions']} full sessions  |  overlap={band['overlap_ratio']:.0%}{ps_tag}")
        print(f"  Members ({len(members)}): {', '.join(member_names)}")
        print(f"  Servers: {srv_str}")
        print(timing_summary(band["session_times"]))

        pred = band.get("prediction")
        if pred:
            n = pred["n_total"]
            print(f"  Predictability (P full band assembles within 2h):")
            for q, (hits, total) in sorted(pred["quorum"].items()):
                label = f"{q} of {n} members online"
                bar = "█" * int(hits/total * 20)
                print(f"    {label}: {hits/total:4.0%}  {bar}  ({hits}/{total} buckets)")
            print(f"  Best canary (solo appearance → full band):")
            for rate, hits, total, guid, name in pred["members"][:3]:
                print(f"    {name}: {rate:4.0%}  ({hits}/{total} solo buckets)")

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--top", type=int, default=10, help="Show top N bands")
    ap.add_argument("--min-sessions", type=int, default=4,
                    help="Min full-group sessions to qualify")
    ap.add_argument("--window-minutes", type=int, default=60,
                    help="Time window (minutes) for co-presence (default 60)")
    ap.add_argument("--output", metavar="PATH",
                    help="Write bands index to this JSON path (e.g. data/bands.json)")
    args = ap.parse_args()

    names, cities = load_names()
    events, guid_servers, guid_buckets, guid_server_buckets = load_census(args.window_minutes)
    bands = find_bands(events, names, args.min_sessions, args.top)

    for band in bands:
        band["prediction"] = predict_assembly(
            band, events, guid_buckets, names,
            primary_server=band.get("primary_server"),
            guid_server_buckets=guid_server_buckets)

    print_report(bands, names)
    print(f"\nDone. {len(bands)} bands found.")

    if args.output:
        write_bands_json(bands, names, cities, args.output)

if __name__ == "__main__":
    main()
