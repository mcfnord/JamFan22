#!/usr/bin/env python3
"""
profile-essay.py — A personal essay about a specific Jamulus musician or band.

Scans the full census history to build a rich portrait of who they are, who
they play with, where they go, and what they play.

Usage:
  python3 ops/profile-essay.py --name "Anne"       # most active match wins
  python3 ops/profile-essay.py --name "cutlove"    # band: all matching GUIDs
  python3 ops/profile-essay.py --guid <32-hex>     # exact GUID

Output: ops/essay-profile-<slug>.html
"""

import argparse, csv, json, re, os, sys, time, urllib.parse, urllib.request
from collections import defaultdict
from datetime import datetime, timedelta

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
BASE  = os.path.dirname(SCRIPT_DIR)
APP   = os.path.join(BASE, 'JamFan22')
DATA  = os.path.join(APP, 'data')

CENSUS_CSV    = os.path.join(DATA, 'census.csv')
CENSUSGEO_CSV = os.path.join(DATA, 'censusgeo.csv')
SERVER_CSV    = os.path.join(DATA, 'server.csv')
TT_JSON       = os.path.join(APP,  'timeTogether.json')
URLS_CSV      = os.path.join(DATA, 'urls.csv')
LORE_JSON     = os.path.join(DATA, 'server-lore.json')
KEY_FILE      = os.path.join(DATA, 'gemini-key.txt')
OUT_DIR       = os.path.join(APP, 'wwwroot', 'essays')

EPOCH      = datetime(2023, 1, 1)
GEMINI_URL = 'https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:generateContent'

# ── System prompt ─────────────────────────────────────────────────────────────

SYSTEM_PROMPT = """
You write a personal essay for Jamulus.live about a specific musician or band on the Jamulus network — a real-time online music jamming platform.

This essay is based on weeks or months of network data. It is a portrait: specific, direct, observational. Think of it as a musician profile in a music magazine, written by someone who was paying close attention — not a stats dump, not a bio, but a story told through what the data reveals.

This essay may be shared with the person or band it describes.

Your essay:
- Opens immediately with a specific detail about this person or band: an instrument, a habit, a regular partner, a server they call home
- Names the subject and their collaborators by name throughout
- Describes patterns that only weeks of data reveal: who they almost always play with, what time of day they tend to show up, how many servers they've visited, whether they wander or stay put
- Includes song titles and artists when available — these are the most personal detail of all
- Notes how long they have been present on the network during this data window
- Mentions their country or city if known, woven naturally into the narrative
- Stops when the portrait is complete — no summary, no compliments

For BAND profiles (multiple members under the same band name):
- Treat the band as a collective subject
- Note the different members' roles and instruments
- Describe when and where they play together vs. apart
- Include outside collaborators who appear regularly in their sessions

STYLE:
- HTML <p> tags only. No markdown, no headers, no bullets.
- <b>musician name</b> every time. <i>server name</i> every time.
- Simple past tense, not progressive.
- Never state statistics directly ("183 hours together") — translate them into voice ("barely a week without each other").
- Song titles from the data are fact — never mention links or chat.
- Lyric echo: <blockquote><em>line one<br>line two</em></blockquote>
- Original poem if something earns it: <blockquote>line one<br>line two</blockquote>
- No hedging ("it seems", "likely", "at some point").
- Same name twice = one person on two connections. Write as one person doing two things.

BANNED: "passion for music", "love of music", "global community", "transcends borders",
"musical journey", "testament to", "wonderful", "amazing", "partners in crime",
"held court", "the usual suspects"

WRONG: Anne has accumulated significant time on the Frozen Land server over the past weeks.
RIGHT: Anne barely missed a session at Frozen Land. The mandolin was always there.

WRONG: The band shows a passion for music that connects musicians worldwide.
RIGHT: Cutlove ran a set last Tuesday — John on guitar, Skins on drums, Andy filling in on second guitar.
"""

# ── Data loaders ──────────────────────────────────────────────────────────────

def load_key():
    try:
        return open(KEY_FILE).read().strip()
    except FileNotFoundError:
        sys.exit(f'[ERROR] No API key at {KEY_FILE}')


def load_player_meta():
    meta = {}
    with open(CENSUSGEO_CSV, newline='', encoding='utf-8', errors='replace') as f:
        for row in csv.reader(f):
            if len(row) < 2 or len(row[0]) != 32:
                continue
            name  = urllib.parse.unquote_plus(row[1]).strip()
            instr = urllib.parse.unquote_plus(row[2]).strip() if len(row) > 2 else ''
            city  = urllib.parse.unquote_plus(row[3]).strip() if len(row) > 3 else ''
            ctry  = urllib.parse.unquote_plus(row[4]).strip() if len(row) > 4 else ''
            if name and name not in ('-', 'No Name'):
                meta[row[0]] = {
                    'name': name,
                    'instrument': instr if instr not in ('-', '') else '',
                    'city': city, 'country': ctry,
                }
    return meta


def load_server_meta():
    meta = {}
    with open(SERVER_CSV, newline='', encoding='utf-8', errors='replace') as f:
        for row in csv.reader(f):
            if len(row) < 2 or ':' not in row[0]:
                continue
            name = urllib.parse.unquote_plus(row[1]).strip()
            city = urllib.parse.unquote_plus(row[2]).strip() if len(row) > 2 else ''
            ctry = urllib.parse.unquote_plus(row[3]).strip() if len(row) > 3 else ''
            if name:
                meta[row[0]] = {'name': name, 'city': city, 'country': ctry}
    try:
        lore = json.load(open(LORE_JSON))
        for ip_port, entry in lore.items():
            if isinstance(entry, dict) and entry.get('name'):
                if ip_port in meta:
                    meta[ip_port]['name'] = entry['name']
                else:
                    meta[ip_port] = {'name': entry['name'], 'city': '', 'country': ''}
    except (FileNotFoundError, json.JSONDecodeError):
        pass
    return meta


def load_time_together():
    def parse_ts(s):
        m = re.match(r'(?:(\d+)\.)?(\d+):(\d+):(\d+)', s)
        if not m:
            return 0.0
        d, h, mi = int(m.group(1) or 0), int(m.group(2)), int(m.group(3))
        return d * 24 + h + mi / 60
    with open(TT_JSON, encoding='utf-8') as f:
        data = json.load(f)
    return {item['Key']: parse_ts(item['Value']) for item in data}


def load_songs():
    songs = defaultdict(list)
    seen  = set()
    with open(URLS_CSV, newline='', encoding='utf-8', errors='replace') as f:
        for row in csv.reader(f):
            server = title = artist = None
            for i, col in enumerate(row):
                if re.match(r'\d+\.\d+\.\d+\.\d+:\d+', col.strip()):
                    server = col.strip()
                    t_i    = i + 2
                    if t_i < len(row):
                        title = urllib.parse.unquote_plus(row[t_i]).strip()
                    if t_i + 1 < len(row):
                        artist = urllib.parse.unquote_plus(row[t_i + 1]).strip()
                    break
            if server and title and title not in ('-', '') and (server, title) not in seen:
                seen.add((server, title))
                songs[server].append((title, artist or ''))
    return dict(songs)


def load_lore():
    try:
        with open(LORE_JSON, encoding='utf-8') as f:
            return json.load(f)
    except Exception:
        return {}


# ── GUID resolution ───────────────────────────────────────────────────────────

def find_guids(args_name, args_guid, player_meta):
    """Return (guids: set, label: str, is_band: bool)."""
    if args_guid:
        g = args_guid.strip().lower()
        if g not in player_meta:
            sys.exit(f'[ERROR] GUID {g} not found in censusgeo')
        return {g}, player_meta[g]['name'], False

    pattern = args_name.lower().strip()
    matches = {
        g: m for g, m in player_meta.items()
        if pattern in m['name'].lower()
    }
    if not matches:
        sys.exit(f'[ERROR] No player found matching "{args_name}"')

    print(f'[find] {len(matches)} GUIDs matching "{args_name}":', flush=True)
    for g, m in sorted(matches.items(), key=lambda x: x[1]['name'])[:10]:
        print(f'  {g[:8]}...  {m["name"]!r}  {m.get("instrument","")}  {m.get("city","")}  {m.get("country","")}')

    is_band = len(matches) > 3  # multiple GUIDs → treat as band/alias group
    if is_band:
        # Use the canonical name (most common non-empty name among matches)
        from collections import Counter
        label = Counter(m['name'] for m in matches.values()).most_common(1)[0][0]
    else:
        label = next(iter(matches.values()))['name']

    return set(matches.keys()), label, is_band


# ── Targeted census scan ──────────────────────────────────────────────────────

def scan_for_subjects(target_guids, server_meta):
    """
    Scan census.csv tracking only target GUIDs and their co-players.
    Returns:
      subject_stats[guid]: {ticks, server_ticks{srv:int}, first_min, last_min, days, hours[24]}
      coplayer_ticks[guid]: total ticks shared on same server at same minute
      server_coappearances[srv][guid] = ticks on that server (all players)
    """
    subject_stats = {g: {
        'ticks': 0, 'server_ticks': {}, 'first_min': None, 'last_min': None,
        'days': set(), 'hours': [0] * 24,
    } for g in target_guids}

    # Track which minutes+servers the subjects were active, to find co-players
    # minute → set of servers subjects were on
    subject_presence = defaultdict(set)   # minute → {server_key}

    print('[census] pass 1 — subject activity...', end='', flush=True)
    n = 0
    with open(CENSUS_CSV, newline='', encoding='utf-8', errors='replace') as f:
        for row in csv.reader(f):
            if len(row) < 3:
                continue
            try:
                minute = int(row[0])
            except ValueError:
                continue
            guid, server = row[1], row[2]
            if len(guid) != 32 or guid not in target_guids:
                continue
            n += 1
            dt  = EPOCH + timedelta(minutes=minute)
            ps  = subject_stats[guid]
            ps['ticks'] += 1
            ps['server_ticks'][server] = ps['server_ticks'].get(server, 0) + 1
            ps['first_min'] = minute if ps['first_min'] is None else min(ps['first_min'], minute)
            ps['last_min']  = minute if ps['last_min']  is None else max(ps['last_min'],  minute)
            ps['days'].add(dt.date())
            ps['hours'][dt.hour] += 1
            subject_presence[minute].add(server)

    print(f' {n:,} subject ticks', flush=True)

    # Co-player pass: find all players sharing minute+server with subjects
    coplayer_ticks = defaultdict(int)   # other_guid → ticks co-present
    server_players = defaultdict(lambda: defaultdict(int))  # srv → guid → ticks

    print('[census] pass 2 — co-players...', end='', flush=True)
    n2 = 0
    with open(CENSUS_CSV, newline='', encoding='utf-8', errors='replace') as f:
        for row in csv.reader(f):
            if len(row) < 3:
                continue
            try:
                minute = int(row[0])
            except ValueError:
                continue
            if minute not in subject_presence:
                continue
            guid, server = row[1], row[2]
            if len(guid) != 32 or guid in target_guids:
                continue
            if server not in subject_presence[minute]:
                continue
            n2 += 1
            coplayer_ticks[guid] += 1
            server_players[server][guid] += 1

    print(f' {n2:,} co-present ticks from {len(coplayer_ticks):,} co-players', flush=True)
    return subject_stats, dict(coplayer_ticks), {k: dict(v) for k, v in server_players.items()}


# ── Context builder ───────────────────────────────────────────────────────────

def _where(sm):
    city, ctry = sm.get('city', ''), sm.get('country', '')
    if city and ctry:
        return f'{city}, {ctry}'
    return ctry or city or ''


def _approx_span(first_min, last_min):
    d = (last_min - first_min) // 1440
    if d < 2:  return 'a day or so'
    if d < 7:  return f'{d} days'
    if d < 14: return 'about a week'
    if d < 21: return 'about two weeks'
    if d < 45: return f'about {d // 7} weeks'
    return f'about {d // 30} months'


def _peak_time_label(hours_24):
    """Return a loose time-of-day label for the peak hour bucket."""
    if not any(hours_24):
        return None
    peak = hours_24.index(max(hours_24))
    if 5 <= peak < 9:   return 'early morning (UTC)'
    if 9 <= peak < 12:  return 'mid-morning (UTC)'
    if 12 <= peak < 17: return 'afternoon (UTC)'
    if 17 <= peak < 21: return 'evening (UTC)'
    if 21 <= peak < 24: return 'late night (UTC)'
    return 'overnight / early hours (UTC)'


def build_context(label, is_band, target_guids,
                  subject_stats, coplayer_ticks, server_players,
                  player_meta, server_meta, tt, songs, lore):

    lines = []
    a = lines.append

    # ── Subject identity ──────────────────────────────────────────────────────
    if is_band:
        member_names = sorted({player_meta[g]['name'] for g in target_guids if g in player_meta})
        instruments  = sorted({player_meta[g].get('instrument', '')
                                for g in target_guids if g in player_meta
                                and player_meta[g].get('instrument', '')})
        countries    = sorted({player_meta[g].get('country', '')
                                for g in target_guids if g in player_meta
                                and player_meta[g].get('country', '')})
        a(f'BAND: {label}')
        a(f'  Known member names / aliases: {", ".join(member_names)}')
        a(f'  Instruments: {", ".join(instruments) or "unknown"}')
        a(f'  Country: {", ".join(countries) or "unknown"}')
    else:
        g    = next(iter(target_guids))
        m    = player_meta.get(g, {})
        a(f'MUSICIAN: {label}')
        a(f'  Instrument: {m.get("instrument","unknown")}')
        loc_parts = [p for p in [m.get("city",""), m.get("country","")] if p]
        if loc_parts:
            a(f'  Location: {", ".join(loc_parts)}')

    # ── Combined activity stats ───────────────────────────────────────────────
    all_days      = set()
    all_servers   = {}   # srv → total ticks from subject(s)
    combined_hours = [0] * 24
    first_min = last_min = None

    for g, ps in subject_stats.items():
        if ps['first_min'] is None:
            continue
        all_days.update(ps['days'])
        for srv, t in ps['server_ticks'].items():
            all_servers[srv] = all_servers.get(srv, 0) + t
        for h, t in enumerate(ps['hours']):
            combined_hours[h] += t
        first_min = ps['first_min'] if first_min is None else min(first_min, ps['first_min'])
        last_min  = ps['last_min']  if last_min  is None else max(last_min,  ps['last_min'])

    if first_min is None:
        a('[no census data found for this player]')
        return '\n'.join(lines)

    d1 = (EPOCH + timedelta(minutes=first_min)).strftime('%B %-d, %Y')
    d2 = (EPOCH + timedelta(minutes=last_min)).strftime('%B %-d, %Y')
    span = _approx_span(first_min, last_min)
    peak_label = _peak_time_label(combined_hours)

    a(f'')
    a(f'ACTIVITY WINDOW: {d1} through {d2} ({span})')
    a(f'  Days active: {len(all_days)}')
    a(f'  Servers visited: {len(all_servers)}')
    a(f'  Peak activity time: {peak_label or "varies"}')

    # ── Server history — top servers ──────────────────────────────────────────
    top_servers = sorted(all_servers.items(), key=lambda x: -x[1])
    a(f'')
    a(f'SERVERS VISITED (by time spent):')
    for srv, ticks in top_servers[:12]:
        sm   = server_meta.get(srv, {})
        name = sm.get('name', srv)
        where = _where(sm)
        loc  = f' ({where})' if where else ''
        srv_songs = songs.get(srv, [])
        song_str  = ''
        if srv_songs:
            song_str = '  Songs: ' + ', '.join(
                f'"{t}" (by {a_})' if a_ else f'"{t}"' for t, a_ in srv_songs[:4]
            )
        entry = lore.get(srv)
        lore_str = ''
        if isinstance(entry, dict) and entry.get('tagline'):
            lore_str = f'  Identity: {entry["tagline"]}'
        a(f'  {name}{loc} — {ticks} ticks' +
          (f'\n    {song_str}' if song_str else '') +
          (f'\n    {lore_str}' if lore_str else ''))

    # ── Long-term partners from timeTogether ──────────────────────────────────
    def parse_ts(s):
        m = re.match(r'(?:(\d+)\.)?(\d+):(\d+):(\d+)', s)
        if not m: return 0.0
        d, h, mi = int(m.group(1) or 0), int(m.group(2)), int(m.group(3))
        return d * 24 + h + mi / 60

    partners = []
    seen_pairs = set()
    for g in target_guids:
        for key64, hours in tt.items():
            if hours < 5:
                continue
            g1, g2 = key64[:32], key64[32:]
            other = None
            if g1 == g and g2 not in target_guids:
                other = g2
            elif g2 == g and g1 not in target_guids:
                other = g1
            if other is None or other in seen_pairs:
                continue
            seen_pairs.add(other)
            om = player_meta.get(other)
            if not om or om['name'].lower().startswith('lobby'):
                continue
            tier = (
                'legendary'    if hours >= 200 else
                'long-running' if hours >= 100 else
                'close'        if hours >= 50  else
                'recurring'    if hours >= 10  else
                'occasional'
            )
            partners.append((hours, other, om['name'], om.get('instrument',''),
                             om.get('country',''), tier))

    partners.sort(reverse=True)
    if partners:
        a(f'')
        a(f'MUSICAL PARTNERS (timeTogether, all time):')
        a(f'(Never state hours directly — translate into voice)')
        for hours, _, name, instr, ctry, tier in partners[:15]:
            i_str = f' ({instr})' if instr else ''
            c_str = f' [{ctry}]' if ctry else ''
            a(f'  {name}{i_str}{c_str} — {tier} partner')

    # ── Co-players seen in the window ─────────────────────────────────────────
    top_coplayers = sorted(coplayer_ticks.items(), key=lambda x: -x[1])[:15]
    if top_coplayers:
        a(f'')
        a(f'MOST FREQUENT CO-PLAYERS this window (not in timeTogether necessarily):')
        for g2, t in top_coplayers:
            om = player_meta.get(g2)
            if not om:
                continue
            i_str = f' ({om["instrument"]})' if om.get('instrument') else ''
            c_str = f' [{om["country"]}]' if om.get('country') else ''
            a(f'  {om["name"]}{i_str}{c_str}')

    # ── Song data on their servers ────────────────────────────────────────────
    subject_songs = []
    seen_songs    = set()
    for srv in all_servers:
        for title, artist in songs.get(srv, []):
            if (title, artist) not in seen_songs:
                seen_songs.add((title, artist))
                subject_songs.append((title, artist, server_meta.get(srv, {}).get('name', srv)))
    if subject_songs:
        a(f'')
        a(f'SONGS PLAYED ON THEIR SERVERS:')
        for title, artist, srv_name in subject_songs[:10]:
            a_str = f' (by {artist})' if artist else ''
            a(f'  "{title}"{a_str} — on {srv_name}')

    return '\n'.join(lines)


# ── LLM call ─────────────────────────────────────────────────────────────────

def call_llm(api_key, context, label):
    payload = {
        'system_instruction': {'parts': [{'text': SYSTEM_PROMPT}]},
        'contents':           [{'parts': [{'text': context}]}],
        'generationConfig':   {'maxOutputTokens': 4096, 'temperature': 1.3},
    }
    req = urllib.request.Request(
        f'{GEMINI_URL}?key={api_key}',
        data=json.dumps(payload).encode(),
        method='POST',
        headers={'Content-Type': 'application/json'},
    )
    print(f'[llm] calling for profile of "{label}"...', flush=True)
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            raw = json.loads(resp.read())
    except Exception as e:
        print(f'[llm] ERROR: {e}', file=sys.stderr)
        return None
    print(f'[llm] done in {time.time()-t0:.1f}s', flush=True)
    try:
        for part in raw['candidates'][0]['content']['parts']:
            if part.get('thought'):
                continue
            text = part.get('text', '').strip()
            if text.startswith('```'):
                text = re.sub(r'^```[^\n]*\n', '', text)
                text = re.sub(r'```\s*$', '', text).strip()
            return text
    except (KeyError, IndexError) as e:
        print(f'[llm] parse error: {e}', file=sys.stderr)
    return None


def wrap_html(body, title):
    ts = datetime.utcnow().strftime('%Y-%m-%d %H:%M UTC')
    return f"""<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Jamulus — {title}</title>
  <style>
    body {{ max-width:720px; margin:3em auto; padding:0 1.5em;
           font-family:Georgia,serif; font-size:1.1em; line-height:1.7;
           color:#222; background:#fdfdf8; }}
    p {{ margin:0 0 1.2em; }}
    b {{ font-weight:bold; }} i {{ font-style:italic; }}
    blockquote {{ margin:1em 2em; border-left:3px solid #ccc;
                  padding-left:1em; color:#555; }}
    .footer {{ font-size:.8em; color:#aaa; text-align:right; margin-top:2em; }}
  </style>
</head>
<body>
{body}
<p class="footer">Generated {ts}</p>
</body>
</html>"""


# ── Main ──────────────────────────────────────────────────────────────────────

def main():
    ap = argparse.ArgumentParser(description='Personal essay about a Jamulus musician or band')
    ap.add_argument('--name', help='Name pattern to search (case-insensitive)')
    ap.add_argument('--guid', help='Exact 32-char GUID')
    args = ap.parse_args()
    if not args.name and not args.guid:
        ap.error('Provide --name or --guid')

    api_key = load_key()

    print('[load] player meta...', flush=True)
    player_meta = load_player_meta()
    print(f'       {len(player_meta):,} players', flush=True)

    print('[load] server meta...', flush=True)
    server_meta = load_server_meta()

    print('[load] timeTogether...', flush=True)
    tt = load_time_together()

    print('[load] songs...', flush=True)
    songs = load_songs()

    lore = load_lore()

    target_guids, label, is_band = find_guids(args.name, args.guid, player_meta)
    print(f'[subject] "{label}"  is_band={is_band}  guids={len(target_guids)}', flush=True)

    subject_stats, coplayer_ticks, server_players = scan_for_subjects(
        target_guids, server_meta)

    ctx = build_context(
        label, is_band, target_guids,
        subject_stats, coplayer_ticks, server_players,
        player_meta, server_meta, tt, songs, lore,
    )
    print(f'[context] {len(ctx):,} chars', flush=True)

    body = call_llm(api_key, ctx, label)
    if not body:
        sys.exit('[ERROR] LLM call failed')

    slug = re.sub(r'[^a-z0-9]+', '-', label.lower()).strip('-')
    out  = os.path.join(OUT_DIR, f'essay-profile-{slug}.html')
    with open(out, 'w', encoding='utf-8') as f:
        f.write(wrap_html(body, label))
    print(f'[done] wrote {out}', flush=True)


if __name__ == '__main__':
    main()
