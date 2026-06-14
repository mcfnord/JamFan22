#!/usr/bin/env python3
"""
recurring-slots.py — detect recurring session slots in census.csv and write server-lore.json.

Trigger: a (server, weekday, hour_utc) slot is confirmed when 4+ GUIDs each appear
in 3+ consecutive calendar weeks at that slot.

Adjacent confirmed hours on the same server are merged into one event (e.g.
Wednesday 03:00–06:00 UTC). Blocks longer than MAX_BLOCK_HOURS are skipped —
they indicate an always-on server, not a scheduled session.

Drop: a confirmed auto event is removed when the most recently completed occurrence
of that block's start weekday/hour had fewer than MIN_REGULARS of the block's
regulars present.

Only touches events tagged with "auto": true in server-lore.json.
Hand-authored events (no "auto" field) are never modified.
"""

import json, os, time
from collections import defaultdict
from datetime import datetime, timezone, timedelta
from urllib.request import urlopen
from urllib.error import URLError

DATA_DIR         = '/root/JamFan22/JamFan22/data'
CENSUS_PATH      = os.path.join(DATA_DIR, 'census.csv')
LORE_PATH        = os.path.join(DATA_DIR, 'server-lore.json')
EPOCH            = datetime(2023, 1, 1, tzinfo=timezone.utc)
WEEKS_LOOKBACK   = 8
MIN_REGULARS     = 4   # GUIDs required to confirm/keep a slot
MIN_WEEKS        = 3   # consecutive weeks required to confirm
MAX_BLOCK_HOURS  = 8   # blocks longer than this are always-on noise, skip them

WEEKDAY_NAMES = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']


def minutes_to_dt(minutes):
    return EPOCH + timedelta(minutes=int(minutes))


def iso_week(dt):
    return dt.isocalendar()[:2]   # (year, week)


def weeks_consecutive(sorted_weeks):
    for i in range(1, len(sorted_weeks)):
        y0, w0 = sorted_weeks[i - 1]
        y1, w1 = sorted_weeks[i]
        if y0 == y1 and w1 == w0 + 1:
            continue
        if y1 == y0 + 1 and w1 == 1:
            last = datetime(y0, 12, 28, tzinfo=timezone.utc).isocalendar()[1]
            if w0 == last:
                continue
        return False
    return True


def last_completed_week(weekday, now):
    """ISO (year, week) of the most recently completed week containing this weekday."""
    days_back = (now.weekday() - weekday) % 7
    if days_back == 0:
        days_back = 7
    return iso_week(now - timedelta(days=days_back))


def load_census(now):
    cutoff = int((now - EPOCH).total_seconds() / 60) - WEEKS_LOOKBACK * 7 * 24 * 60
    data = defaultdict(set)   # (server, weekday, hour, iso_week) -> set of guids
    rows = 0
    with open(CENSUS_PATH, newline='') as f:
        for line in f:
            parts = line.split(',')
            if len(parts) < 3:
                continue
            try:
                ts = int(parts[0])
            except ValueError:
                continue
            if ts < cutoff:
                continue
            guid   = parts[1].strip()
            server = parts[2].strip()
            if not guid or not server:
                continue
            dt = minutes_to_dt(ts)
            data[(server, dt.weekday(), dt.hour, iso_week(dt))].add(guid)
            rows += 1
    print(f'  {rows:,} census rows in lookback window')
    return data


def detect_confirmed(raw):
    """Returns {(server, weekday, hour): {'regulars': set, 'week_data': {iso_week: set}}}"""
    by_slot = defaultdict(dict)
    for (server, wd, hr, wk), guids in raw.items():
        by_slot[(server, wd, hr)][wk] = guids

    confirmed = {}
    for (server, wd, hr), week_data in by_slot.items():
        weeks = sorted(week_data.keys())
        if len(weeks) < MIN_WEEKS:
            continue
        # GUIDs present in at least MIN_WEEKS distinct weeks
        guid_count = defaultdict(int)
        for guids in week_data.values():
            for g in guids:
                guid_count[g] += 1
        regulars = {g for g, n in guid_count.items() if n >= MIN_WEEKS}
        if len(regulars) < MIN_REGULARS:
            continue
        # Must have a run of MIN_WEEKS consecutive weeks each with MIN_REGULARS present
        found = False
        for i in range(len(weeks) - MIN_WEEKS + 1):
            run = weeks[i:i + MIN_WEEKS]
            if weeks_consecutive(run) and all(
                    len(week_data[w] & regulars) >= MIN_REGULARS for w in run):
                found = True
                break
        if found:
            confirmed[(server, wd, hr)] = {'regulars': regulars, 'week_data': week_data}
    return confirmed


def merge_blocks(confirmed):
    """
    Merge confirmed (server, weekday, hour) slots into contiguous time blocks.

    Treats the week as a linear sequence: Monday 00:00 = index 0 … Sunday 23:00 = index 167.
    Adjacent indices (diff == 1) are merged, including across the midnight boundary.
    Cross-week wrapping (Sunday 23:00 → Monday 00:00) is NOT merged.

    Returns list of dicts:
      { server, start_wd, start_hr, end_wd, end_hr, regulars, week_data_list }
    where end_hr is exclusive (the hour after the last slot).
    """
    # Group by server
    by_server = defaultdict(list)
    for (server, wd, hr), info in confirmed.items():
        by_server[server].append((wd * 24 + hr, wd, hr, info))

    blocks = []
    for server, slots in by_server.items():
        slots.sort(key=lambda x: x[0])  # sort by weekly index

        i = 0
        while i < len(slots):
            # Start a new block
            idx0, wd0, hr0, info0 = slots[i]
            block_slots = [(wd0, hr0, info0)]
            j = i + 1
            while j < len(slots):
                idx_next, wd_next, hr_next, info_next = slots[j]
                if idx_next == idx0 + len(block_slots):
                    block_slots.append((wd_next, hr_next, info_next))
                    j += 1
                else:
                    break
            i = j

            n_hours = len(block_slots)
            if n_hours > MAX_BLOCK_HOURS:
                continue  # always-on, skip

            last_wd, last_hr, _ = block_slots[-1]
            # end is exclusive: hour after last slot (may cross weekday boundary)
            end_total = last_wd * 24 + last_hr + 1
            end_wd = (end_total // 24) % 7
            end_hr = end_total % 24

            # Merge regulars and week_data across constituent hours
            all_regulars = set()
            combined_week_data = defaultdict(set)
            for _, _, info in block_slots:
                all_regulars |= info['regulars']
                for wk, guids in info['week_data'].items():
                    combined_week_data[wk] |= guids

            blocks.append({
                'server':     server,
                'start_wd':   wd0,
                'start_hr':   hr0,
                'end_wd':     end_wd,
                'end_hr':     end_hr,
                'n_hours':    n_hours,
                'regulars':   all_regulars,
                'week_data':  dict(combined_week_data),
            })

    return blocks


def is_stale(block, now):
    """Block is stale if the most recently completed occurrence of its start weekday
    had fewer than MIN_REGULARS of its regulars present."""
    target = last_completed_week(block['start_wd'], now)
    present = block['week_data'].get(target, set()) & block['regulars']
    return len(present) < MIN_REGULARS


def block_auto_key(block):
    """Unique identifier for an auto event — stored in the JSON to enable idempotent re-runs."""
    return f"{block['start_wd']}:{block['start_hr']}:{block['end_wd']}:{block['end_hr']}"


def lookup_latlon(ip):
    """Return (lat, lon) for an IP via ip-api.com, or (None, None) on failure."""
    try:
        url = f'http://ip-api.com/json/{ip}?fields=lat,lon,status'
        with urlopen(url, timeout=5) as r:
            data = json.loads(r.read())
        if data.get('status') == 'success':
            return data['lat'], data['lon']
    except (URLError, KeyError, ValueError):
        pass
    return None, None


def enrich_latlon(lore):
    """Add lat/lon to any lore entry that doesn't already have both."""
    enriched = 0
    for server_key, entry in lore.items():
        if entry.get('lat') is not None and entry.get('lon') is not None:
            continue
        ip = server_key.split(':')[0]
        lat, lon = lookup_latlon(ip)
        if lat is not None:
            entry['lat'] = lat
            entry['lon'] = lon
            enriched += 1
            time.sleep(0.1)   # stay well under 45 req/min free-tier limit
    if enriched:
        print(f'  Geo-enriched {enriched} server entries')


def make_event(block):
    start_day = WEEKDAY_NAMES[block['start_wd']]
    end_day   = WEEKDAY_NAMES[block['end_wd']]
    s_hr, e_hr = block['start_hr'], block['end_hr']

    if block['end_wd'] == block['start_wd']:
        name     = f"{start_day} {s_hr:02d}:00 UTC session"
        schedule = f"{start_day}s {s_hr:02d}:00–{e_hr:02d}:00 UTC"
    else:
        name     = f"{start_day} {s_hr:02d}:00 UTC session"
        schedule = f"{start_day}s {s_hr:02d}:00 – {end_day} {e_hr:02d}:00 UTC"

    return {
        'name':        name,
        'schedule':    schedule,
        'description': None,
        'listen_url':  None,
        'auto':        True,
        'auto_key':    block_auto_key(block),
        'weekday':     block['start_wd'],   # Python weekday: Mon=0…Sun=6
        'hour':        block['start_hr'],
    }


def load_lore():
    try:
        with open(LORE_PATH) as f:
            return json.load(f)
    except (FileNotFoundError, json.JSONDecodeError):
        return {}


def save_lore(lore):
    with open(LORE_PATH, 'w') as f:
        json.dump(lore, f, indent=2)


def main():
    now = datetime.now(timezone.utc)
    print('Loading census data...')
    raw = load_census(now)
    print('Detecting recurring slots...')
    confirmed = detect_confirmed(raw)
    print(f'  {len(confirmed)} confirmed slot-hours')
    blocks = merge_blocks(confirmed)
    print(f'  {len(blocks)} merged blocks (≤{MAX_BLOCK_HOURS}h)')

    lore = load_lore()
    added = dropped = unchanged = 0

    # Servers that already have hand-authored events — don't add auto events there
    hand_authored_servers = {
        k for k, v in lore.items()
        if any(not ev.get('auto') for ev in v.get('events', []))
    }

    # Build a set of all current block keys
    active_keys = {(b['server'], block_auto_key(b)) for b in blocks}

    # Add or keep each block
    for block in blocks:
        server  = block['server']
        key     = block_auto_key(block)

        if server in hand_authored_servers:
            continue  # hand-authored event exists; don't mix in auto events

        entry   = lore.setdefault(server, {})
        events  = entry.setdefault('events', [])
        existing = next((ev for ev in events
                         if ev.get('auto') and ev.get('auto_key') == key), None)

        if is_stale(block, now):
            if existing:
                events.remove(existing)
                dropped += 1
                wd_name = WEEKDAY_NAMES[block['start_wd']]
                print(f'  DROP  {server}  {wd_name} {block["start_hr"]:02d}:00 UTC  (stale)')
            continue

        if existing:
            # Backfill weekday/hour if missing (migration from earlier format)
            if existing.get('weekday') is None:
                existing['weekday'] = block['start_wd']
                existing['hour']    = block['start_hr']
            unchanged += 1
        else:
            events.append(make_event(block))
            added += 1
            wd_name = WEEKDAY_NAMES[block['start_wd']]
            print(f'  ADD   {server}  {wd_name} {block["start_hr"]:02d}:00–'
                  f'{block["end_hr"]:02d}:00 UTC  '
                  f'({len(block["regulars"])} regulars, {block["n_hours"]}h)')

    # Remove auto events whose block no longer exists in confirmed
    for server_key, entry in list(lore.items()):
        for ev in list(entry.get('events', [])):
            if not ev.get('auto'):
                continue
            ak = ev.get('auto_key', '')
            if (server_key, ak) not in active_keys:
                entry['events'].remove(ev)
                dropped += 1
                print(f'  DROP  {server_key}  auto_key={ak}  (no longer confirmed)')

    print('Enriching server lat/lon...')
    enrich_latlon(lore)

    save_lore(lore)
    print(f'\nResult: +{added} added, -{dropped} dropped, {unchanged} unchanged')
    print(f'Wrote {LORE_PATH}')


if __name__ == '__main__':
    main()
