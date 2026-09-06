#!/usr/bin/env python3
"""
recurring-slots.py — detect recurring session slots in census.csv and write server-lore.json.

Trigger: a (server, weekday, hour_utc) slot is confirmed when 4+ GUIDs each appear
in 3+ consecutive calendar weeks at that slot.

Adjacent confirmed hours on the same server are merged into one event. Slots are
detected in UTC (the census timestamps are UTC), but the human-facing day and time
in each event's name/schedule are converted to the SERVER's local timezone before
being written — so a slot at Wednesday 03:00 UTC on a California server is labeled
"Tuesday" (its actual local evening), not "Wednesday". Blocks longer than
MAX_BLOCK_HOURS are skipped — they indicate an always-on server, not a session.

Drop: a confirmed auto event is removed when the most recently completed occurrence
of that block's start weekday/hour had fewer than MIN_REGULARS of the block's
regulars present.

Only touches events tagged with "auto": true in server-lore.json.
Hand-authored events (no "auto" field) are never modified.
"""

import json, os, time
from collections import defaultdict
from urllib.parse import unquote_plus
from datetime import datetime, timezone, timedelta
from zoneinfo import ZoneInfo
from urllib.request import urlopen
from urllib.error import URLError

DATA_DIR         = '/root/JamFan22/JamFan22/data'
CENSUS_PATH      = os.path.join(DATA_DIR, 'census.csv')
LORE_PATH        = os.path.join(DATA_DIR, 'server-lore.json')
CENSUSGEO_PATH   = os.path.join(DATA_DIR, 'censusgeo.csv')
EPOCH            = datetime(2023, 1, 1, tzinfo=timezone.utc)
WEEKS_LOOKBACK   = 8
MIN_REGULARS     = 6   # GUIDs required to confirm/keep a slot
MIN_WEEKS        = 4   # consecutive weeks required to confirm
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
    """Stable identifier for an auto event — encodes only the start slot so block
    growth or shrinkage doesn't invalidate existing events."""
    return f"{block['start_wd']}:{block['start_hr']}"


def lookup_geo(ip):
    """Return (lat, lon, tz) for an IP via ip-api.com, or (None, None, None).
    tz is the IANA timezone name (e.g. 'America/Los_Angeles') used to localize
    each server's session day/time."""
    try:
        url = f'http://ip-api.com/json/{ip}?fields=lat,lon,timezone,status'
        with urlopen(url, timeout=5) as r:
            data = json.loads(r.read())
        if data.get('status') == 'success':
            return data['lat'], data['lon'], data.get('timezone')
    except (URLError, KeyError, ValueError):
        pass
    return None, None, None


def enrich_geo(lore):
    """Fill lat/lon/tz on any lore entry missing coordinates OR a timezone.
    Existing entries predate the tz field, so they get a one-time backfill."""
    enriched = 0
    for server_key, entry in lore.items():
        has_latlon = entry.get('lat') is not None and entry.get('lon') is not None
        has_tz     = bool(entry.get('tz'))
        if has_latlon and has_tz:
            continue
        ip = server_key.split(':')[0]
        lat, lon, tz = lookup_geo(ip)
        if lat is not None:
            entry['lat'] = lat
            entry['lon'] = lon
            if tz:
                entry['tz'] = tz
            enriched += 1
            time.sleep(0.1)   # stay well under 45 req/min free-tier limit
    if enriched:
        print(f'  Geo-enriched {enriched} server entries')


def load_names():
    """Return {guid: display_name} from censusgeo.csv.
    Schema: hash,name,instrument,city,nation — last entry per GUID wins (append order = most recent).
    Lobby bots (raw name starts with '++') and junk names are excluded.
    """
    junk_decoded = {'', '-', 'no name', 'no mic', 'no name no m'}
    names = {}
    try:
        with open(CENSUSGEO_PATH, newline='') as f:
            for line in f:
                parts = line.split(',')
                if len(parts) < 2:
                    continue
                guid = parts[0].strip()
                raw  = parts[1].strip()
                if not guid or not raw:
                    continue
                name = unquote_plus(raw).strip()
                if not name or name.lower() in junk_decoded or len(name) < 2:
                    continue
                if name.lower().startswith('lobby'):  # lobby bot (e.g. ++lobby+[0]++)
                    continue
                names[guid] = name
    except FileNotFoundError:
        pass
    return names


def block_leaders(block, names):
    """Return up to 3 display names for the block's regulars, ranked by attendance weeks."""
    guid_weeks = {
        guid: sum(1 for guids in block['week_data'].values() if guid in guids)
        for guid in block['regulars']
    }
    seen = []
    for guid in sorted(block['regulars'], key=lambda g: -guid_weeks.get(g, 0)):
        name = names.get(guid)
        if not name or name in seen:
            continue
        seen.append(name)
        if len(seen) >= 3:
            break
    return seen


def resolve_tz(entry):
    """Timezone for a lore entry, preferring the IANA name stored by enrich_geo.
    Falls back to a longitude-derived fixed offset (good enough to get the local
    weekday right), then UTC. Server location comes from ip-api, cached in the entry."""
    tzname = (entry or {}).get('tz')
    if tzname:
        try:
            return ZoneInfo(tzname)
        except Exception:
            pass
    lon = (entry or {}).get('lon')
    if lon is not None:
        return timezone(timedelta(hours=max(-12, min(14, round(lon / 15)))))
    return timezone.utc


def local_block_times(block, tz, now):
    """Convert a block's UTC (weekday, hour) start and its exclusive end into the
    server's local time, anchored to the most recent past occurrence so the label
    reflects the current DST season. Returns (start_local, end_local)."""
    days_back = (now.weekday() - block['start_wd']) % 7
    start_utc = (now - timedelta(days=days_back)).replace(
        hour=block['start_hr'], minute=0, second=0, microsecond=0)
    end_utc = start_utc + timedelta(hours=block['n_hours'])
    return start_utc.astimezone(tz), end_utc.astimezone(tz)


def make_event(block, leaders=None, entry=None, now=None):
    """Build an auto event. Detection is UTC (auto_key/weekday/hour stay UTC so the
    match key is stable), but the reader-facing name and schedule are localized to
    the server's own timezone — the day a person there would actually call it."""
    now = now or datetime.now(timezone.utc)
    tz  = resolve_tz(entry)
    start_local, end_local = local_block_times(block, tz, now)
    start_day = start_local.strftime('%A')
    end_day   = end_local.strftime('%A')
    s_hr, e_hr = start_local.hour, end_local.hour

    tzlabel = start_local.tzname() or ''
    if not tzlabel or tzlabel.startswith('UTC') or tzlabel[0] in '+-':
        tzlabel = 'local time'

    if start_day == end_day:
        schedule = f"{start_day}s {s_hr:02d}:00–{e_hr:02d}:00 {tzlabel}"
    else:
        schedule = f"{start_day}s {s_hr:02d}:00 – {end_day} {e_hr:02d}:00 {tzlabel}"

    name = f"{start_day} sessions"
    if leaders:
        name += f" · {', '.join(leaders[:3])}"

    return {
        'name':        name,
        'schedule':    schedule,
        'description': None,
        'listen_url':  None,
        'auto':        True,
        'auto_key':    block_auto_key(block),
        'weekday':     block['start_wd'],   # UTC weekday (Mon=0…Sun=6) — stable match key
        'hour':        block['start_hr'],   # UTC hour — pairs with auto_key
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
    names = load_names()
    added = dropped = unchanged = 0

    # Build a set of all current block keys
    active_keys = {(b['server'], block_auto_key(b)) for b in blocks}

    # Geo-enrich BEFORE building events so make_event can localize each server's
    # session day/time to its own timezone. Pre-create entries for newly-seen
    # servers so they get lat/lon/tz too.
    for block in blocks:
        lore.setdefault(block['server'], {})
    print('Enriching server lat/lon/tz...')
    enrich_geo(lore)

    # Add or keep each block
    for block in blocks:
        server  = block['server']
        key     = block_auto_key(block)

        entry   = lore.setdefault(server, {})
        events  = entry.setdefault('events', [])
        existing = next((ev for ev in events
                         if ev.get('auto_key') == key), None)

        leaders = block_leaders(block, names)

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
            # Refresh leader names AND the localized day/time on auto events each run
            if existing.get('auto') == True:
                fresh = make_event(block, leaders, entry, now)
                existing['name']     = fresh['name']
                existing['schedule'] = fresh['schedule']
            unchanged += 1
        else:
            events.append(make_event(block, leaders, entry, now))
            added += 1
            wd_name = WEEKDAY_NAMES[block['start_wd']]
            print(f'  ADD   {server}  {wd_name} {block["start_hr"]:02d}:00–'
                  f'{block["end_hr"]:02d}:00 UTC  '
                  f'({len(block["regulars"])} regulars, {block["n_hours"]}h)')

    # Remove events (auto or hand-authored) whose auto_key no longer matches a confirmed block
    for server_key, entry in list(lore.items()):
        for ev in list(entry.get('events', [])):
            ak = ev.get('auto_key', '')
            if not ak:
                continue  # no auto_key = permanently hand-curated, never auto-dropped
            if (server_key, ak) not in active_keys:
                entry['events'].remove(ev)
                dropped += 1
                print(f'  DROP  {server_key}  auto_key={ak}  (no longer confirmed)')

    save_lore(lore)
    print(f'\nResult: +{added} added, -{dropped} dropped, {unchanged} unchanged')
    print(f'Wrote {LORE_PATH}')


if __name__ == '__main__':
    main()
