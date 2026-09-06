#!/usr/bin/env python3
"""
long-essay.py — Three long-form essays about the Jamulus network using full census history.

Generates three English variants, each with a different editorial angle:
  essay-long-en-people.html  — The People  (relationships and pairs)
  essay-long-en-places.html  — The Places  (servers as communities)
  essay-long-en-music.html   — The Music   (songs, genres, repertoire)

Run from repo root:  python3 ops/long-essay.py [--days N]
"""

import argparse, csv, json, re, os, sys, time, urllib.parse, urllib.request
from collections import defaultdict
from datetime import datetime, timedelta

# ── Paths ─────────────────────────────────────────────────────────────────────

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

BLUES_BOT_SERVER = '77.163.83.31:22124'
BOT_H     = 2000  # hours together → likely bot pair (real bots: 3774h+; highest human pair: 702h)
MIN_PAIR_H = 10   # min hours for a notable human pair
EPOCH = datetime(2023, 1, 1)
GEMINI_URL = 'https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:generateContent'

SEA_COUNTRIES = {
    'Thailand', 'Philippines', 'China', 'Singapore', 'Japan', 'South Korea',
    'Malaysia', 'Indonesia', 'Vietnam', 'Bangladesh', 'Australia', 'New Zealand',
    'Taiwan', 'Hong Kong', 'Myanmar', 'Cambodia',
}

# ── Shared style rules appended to every system prompt ───────────────────────

_STYLE = """
STYLE (same as daily essay):
- HTML <p> tags only. No markdown, no headers, no bullets.
- <b>musician name</b> every time a musician is named. <i>server name</i> every time.
- Simple past tense, not progressive ("held" not "was holding").
- Stop when the story is told — no summary paragraph at the end.
- Song titles come from the data — never mention links, chat, or how you know.
- Lyric echo: <blockquote><em>line one<br>line two</em></blockquote>
- Original poem (roughly 1 in 3 essays, when something earns it): <blockquote>...</blockquote>
- BOT RULE: all bot content in exactly one paragraph. Never name bots elsewhere.
- Listener characters earn at most a parenthetical — never their own sentence.
- No hedging qualifiers ("it seems", "likely", "at some point", "seemingly").
- Player counts are total unique visitors over the full window, not concurrent — never say "at its peak."
- Same name appearing twice = one person on two connections. Write as one person doing two things.
- Never use "unnamed" for a server. Just describe by what you know.
- Never say "over at" — just "at." Never "out of [city]" — use "[city]'s [server]."
- "a score" means a musical script in this context — never use it to mean twenty.

BANNED PHRASES (never use):
"global community", "passion for music", "love of music", "transcends borders",
"musical journey", "testament to", "thriving", "wonderful", "amazing",
"the beauty of", "partners in crime", "held court", "into the mix",
"holding down the low end", "the usual suspects", "came together",
"connect musicians", "floor warm", "wasn't sleeping", "just taking it all in"

WRONG: Two musicians have accumulated 183 hours of playing together.
RIGHT: Those two have barely missed a week in three months.

WRONG: The network connects musicians from across the globe with a passion for music.
RIGHT: [name a person, a place, or a song. Start there.]

WRONG: Bertus was on synthesizer, while Bertus handled vocals.
RIGHT: Bertus played synth and sang.  (same name twice = one person, two connections)

WRONG: It seems RustyShackleford was also spotted at some point on Cascadia Jazz.
RIGHT: RustyShackleford also played on Cascadia Jazz.
"""

# ── Three editorial prompts ───────────────────────────────────────────────────
# Each variant uses identical data context; the prompt alone shapes the angle.

PROMPT_LONGVIEW = f"""You write a long-form essay for Jamulus.live — a radar for the global real-time music jamming network. Jamulus lets musicians play together live over the internet.

This essay covers weeks of activity, not a single day. It is a cumulative portrait of the network — the kind of picture only time reveals.

ANGLE: THE LONG VIEW.
Tell the whole story: patterns, characters, relationships, music. No single day can show this. A bassist who showed up every Tuesday for three months. A duo that never seems to play with anyone else. A server that drew an unexpected crowd. Follow whatever thread is most interesting — people, rooms, music, or all three.

- Open immediately with something specific that could only be known from weeks of data.
- People are not fixed to one server — follow them wherever they went.
- If a particular server turns out to be the natural center of the story, that is fine. But do not force it.
- Use ALL sections of the context: relationships, servers, songs, individuals. Weave them together as the story demands.
- "Truly international" players (active on both Western and SEA servers) are rare and remarkable — call them out when present.
- The bot aside: one wry paragraph somewhere in the middle or end.
- Include a short poem if something specific earns it.
{_STYLE}"""

PROMPT_WEB = f"""You write a long-form essay for Jamulus.live — a radar for the global real-time music jamming network. Jamulus lets musicians play together live over the internet.

This essay covers weeks of activity, not a single day.

ANGLE: THE WEB.
This essay is about connection — the invisible threads between musicians across servers, cities, and continents. The most interesting material here is the pairs who kept finding each other, the groups that reconvene wherever they land, the player who traveled everywhere and left a trail of brief partnerships.

- Open with a relationship and follow the threads outward.
- People are not fixed to their rooms. A musician may have a home base but also visit ten others — that mobility IS the story.
- Cross-region connections (UK player + Canadian player; Thai regular + German passerby) are the most remarkable material. Surface them when present.
- "Truly international" players (active on both Western and SEA servers) are extremely rare — if they appear in the data, they deserve a moment.
- Servers appear as meeting grounds, not endpoints. Name them, but keep the focus on who meets there.
- The bots: one paragraph — the permanent fixtures that never leave, never travel, never connect.
- Include a short poem if something earns it.
{_STYLE}"""

PROMPT_PORTRAITS = f"""You write a long-form essay for Jamulus.live — a radar for the global real-time music jamming network. Jamulus lets musicians play together live over the internet.

This essay covers weeks of activity, not a single day.

ANGLE: THE CHARACTERS.
Profile the most remarkable individuals: the person who barely missed a day; the nomad who played on more servers than anyone; the duo who have clearly figured each other out; the player whose time-of-day pattern reveals a lifestyle. Specific people, not archetypes.

- Open with one person, named, with a specific detail about how they play or show up.
- Move from character to character, letting the network emerge through their individual stories.
- Songs give characters texture — name what someone played when you know it.
- Use NOTABLE INDIVIDUALS as primary material; pull from LONG-TERM RELATIONSHIPS for the partnerships.
- The bot section: one paragraph. Cast the bots as characters too — permanent, tireless, incurious.
- Include a short poem if something specific earns it.
{_STYLE}"""

# ── Data loaders ──────────────────────────────────────────────────────────────

def load_key():
    try:
        return open(KEY_FILE).read().strip()
    except FileNotFoundError:
        sys.exit(f'[ERROR] No API key at {KEY_FILE}')


def load_player_meta():
    """censusgeo.csv → {guid: {name, instrument, city, country}}  last row wins."""
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
                    'city': city,
                    'country': ctry,
                }
    return meta


def load_server_meta():
    """server.csv → {ip:port: {name, city, country}}  last row wins.
    server-lore.json 'name' overrides server.csv where present (server.csv lost
    chronological ordering when sorted+deduped; lore is the authoritative name source)."""
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
    """timeTogether.json → {guid64: hours_float}"""
    def parse_ts(s):
        m = re.match(r'(?:(\d+)\.)?(\d+):(\d+):(\d+)', s)
        if not m:
            return 0.0
        d, h, mi = int(m.group(1) or 0), int(m.group(2)), int(m.group(3))
        return d * 24 + h + mi / 60
    with open(TT_JSON, encoding='utf-8') as f:
        data = json.load(f)
    return {item['Key']: parse_ts(item['Value']) for item in data}


def scan_census(cutoff_min=None):
    """
    Single pass over census.csv.
      player_stats[guid] = {ticks, servers(set), first_min, last_min, days(set), hours[24]}
      server_stats[ip:port] = {ticks, guid_ticks{guid:int}, first_min, last_min}
    """
    player_stats = {}
    server_stats = {}
    n = 0
    print('[census] scanning...', end='', flush=True)
    with open(CENSUS_CSV, newline='', encoding='utf-8', errors='replace') as f:
        for row in csv.reader(f):
            if len(row) < 3:
                continue
            try:
                minute = int(row[0])
            except ValueError:
                continue
            if cutoff_min is not None and minute < cutoff_min:
                continue
            guid, server = row[1], row[2]
            if len(guid) != 32:
                continue
            n += 1
            dt   = EPOCH + timedelta(minutes=minute)

            ps = player_stats.get(guid)
            if ps is None:
                player_stats[guid] = ps = {
                    'ticks': 0, 'servers': set(), 'first_min': minute, 'last_min': minute,
                    'days': set(), 'hours': [0] * 24,
                }
            ps['ticks'] += 1
            ps['servers'].add(server)
            if minute < ps['first_min']: ps['first_min'] = minute
            if minute > ps['last_min']:  ps['last_min']  = minute
            ps['days'].add(dt.date())
            ps['hours'][dt.hour] += 1

            ss = server_stats.get(server)
            if ss is None:
                server_stats[server] = ss = {
                    'ticks': 0, 'guid_ticks': {}, 'first_min': minute, 'last_min': minute,
                }
            ss['ticks'] += 1
            ss['guid_ticks'][guid] = ss['guid_ticks'].get(guid, 0) + 1
            if minute < ss['first_min']: ss['first_min'] = minute
            if minute > ss['last_min']:  ss['last_min']  = minute

    print(f' {n:,} rows → {len(player_stats):,} players, {len(server_stats):,} servers',
          flush=True)
    return player_stats, server_stats


def load_songs():
    """urls.csv → {server_key: [(title, artist), ...]}  de-duped."""
    songs = defaultdict(list)
    seen  = set()
    with open(URLS_CSV, newline='', encoding='utf-8', errors='replace') as f:
        for row in csv.reader(f):
            server = title = artist = None
            for i, col in enumerate(row):
                if re.match(r'\d+\.\d+\.\d+\.\d+:\d+', col.strip()):
                    server = col.strip()
                    t_i    = i + 2   # skip the url col
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


# ── Analysis ──────────────────────────────────────────────────────────────────

def classify_bots(tt, player_stats):
    """Any GUID in a pair exceeding BOT_H hours is a bot candidate."""
    bots = set()
    for key64, hours in tt.items():
        if hours > BOT_H:
            bots.add(key64[:32])
            bots.add(key64[32:])
    return bots


def build_human_pairs(tt, player_meta, bot_guids):
    pairs = []
    seen  = set()
    for key64, hours in sorted(tt.items(), key=lambda x: -x[1]):
        if hours < MIN_PAIR_H:
            break
        g1, g2 = key64[:32], key64[32:]
        if g1 in bot_guids or g2 in bot_guids:
            continue
        pkey = min(g1, g2) + max(g1, g2)
        if pkey in seen:
            continue
        seen.add(pkey)
        m1, m2 = player_meta.get(g1), player_meta.get(g2)
        if not m1 or not m2:
            continue
        if m1['name'].lower().startswith('lobby') or m2['name'].lower().startswith('lobby'):
            continue
        tier = (
            'legendary duo'       if hours >= 200 else
            'long-running duo'    if hours >= 100 else
            'close collaborators' if hours >= 50  else
            'recurring collaborators'
        )
        pairs.append({'g1': g1, 'g2': g2, 'm1': m1, 'm2': m2,
                       'hours': hours, 'tier': tier})
    return pairs


def build_bot_profiles(player_meta, player_stats, bot_guids, server_meta):
    profiles = {}
    for g in bot_guids:
        m, ps = player_meta.get(g), player_stats.get(g)
        if not m or not ps or m['name'].lower().startswith('lobby'):
            continue
        profiles[g] = {
            'name': m['name'], 'instrument': m.get('instrument', ''),
            'servers': list(ps['servers']),
            'ticks': ps['ticks'],
            'days_active': len(ps['days']),
        }
    server_bots = defaultdict(list)
    for g, p in profiles.items():
        for srv in p['servers']:
            server_bots[srv].append(g)
    return profiles, dict(server_bots)


def _is_sea(server_key, server_meta):
    return server_meta.get(server_key, {}).get('country', '') in SEA_COUNTRIES


def _where(sm):
    city, ctry = sm.get('city', ''), sm.get('country', '')
    if city and ctry:
        return f'{city}, {ctry}'
    return ctry or city or '?'


def _approx_days(minutes_span):
    d = minutes_span // 1440
    if d < 2:  return 'a day or so'
    if d < 7:  return f'{d} days'
    if d < 14: return 'about a week'
    if d < 21: return 'about two weeks'
    if d < 45: return f'about {d // 7} weeks'
    return f'about {d // 30} months'


# ── Context builder ───────────────────────────────────────────────────────────

def build_context(player_meta, server_meta, player_stats, server_stats,
                  human_pairs, bot_profiles, bot_server_map,
                  songs, lore, first_min, last_min):

    d1 = (EPOCH + timedelta(minutes=first_min)).strftime('%B %-d, %Y')
    d2 = (EPOCH + timedelta(minutes=last_min)).strftime('%B %-d, %Y')
    span_days = (last_min - first_min) // 1440
    now_str   = datetime.utcnow().strftime('%A, %Y-%m-%d %H:%M UTC')

    lines = [
        f'Data window: {d1} through {d2} ({span_days} days)',
        f'Essay generated: {now_str}',
        f'Network totals: {len(player_meta):,} unique players, {len(server_stats):,} unique servers',
        '',
    ]

    def server_block(srv_key, ss, limit_songs=6):
        sm    = server_meta.get(srv_key, {})
        name  = sm.get('name', srv_key)
        where = _where(sm)
        span  = _approx_days(ss['last_min'] - ss['first_min'])
        lines.append('')
        lines.append(f'{name} ({where})')
        lines.append(f'  {len(ss["guid_ticks"])} unique players · active across ~{span}')
        top = sorted(
            [(g, player_meta[g]['name'], player_meta[g].get('instrument', ''))
             for g in ss['guid_ticks']
             if g in player_meta and g not in bot_profiles
             and not player_meta[g]['name'].lower().startswith('lobby')],
            key=lambda x: -ss['guid_ticks'].get(x[0], 0)
        )[:6]
        if top:
            pstrs = [f'{n} ({i})' if i else n for _, n, i in top]
            lines.append(f'  Regulars: {", ".join(pstrs)}')
        srv_songs = songs.get(srv_key, [])
        if srv_songs:
            sstrs = [f'"{t}" (by {a})' if a else f'"{t}"' for t, a in srv_songs[:limit_songs]]
            lines.append(f'  Songs: {", ".join(sstrs)}')
        entry = lore.get(srv_key)
        if isinstance(entry, dict):
            if entry.get('tagline'):
                lines.append(f'  Identity: {entry["tagline"]}')
            themes = entry.get('themes', [])
            if themes:
                lines.append(f'  Themes: {", ".join(themes[:3])}')

    # Western servers
    western = sorted(
        [(k, v) for k, v in server_stats.items()
         if not _is_sea(k, server_meta) and k != BLUES_BOT_SERVER],
        key=lambda x: -x[1]['ticks']
    )
    lines.append('SERVERS — Western community (EU + NA + SA), by total activity:')
    for k, v in western[:15]:
        server_block(k, v)

    # SEA servers
    sea = sorted(
        [(k, v) for k, v in server_stats.items() if _is_sea(k, server_meta)],
        key=lambda x: -x[1]['ticks']
    )
    lines.append('')
    lines.append('SERVERS — Southeast Asia & Pacific, by total activity:')
    for k, v in sea[:10]:
        server_block(k, v)

    # Human pairs
    lines.append('')
    lines.append('LONG-TERM MUSICIAN RELATIONSHIPS:')
    lines.append('(Never state hours or statistics. Use these to color how you describe people.)')
    prev_tier = None
    seen_pairs = set()
    for p in human_pairs:
        pkey = min(p['g1'], p['g2']) + max(p['g1'], p['g2'])
        if pkey in seen_pairs:
            continue
        seen_pairs.add(pkey)
        t = p['tier']
        if t != prev_tier:
            lines.append(f'  [{t.upper()}]')
            prev_tier = t
        m1, m2 = p['m1'], p['m2']
        i1, i2 = m1.get('instrument', ''), m2.get('instrument', '')
        combo = (f' ({i1} + {i2})' if i1 and i2 and i1 != i2
                 else f' ({i1 or i2})' if (i1 or i2) else '')
        c1, c2 = m1.get('country', ''), m2.get('country', '')
        geo = f' [{c1} / {c2}]' if c1 and c2 and c1 != c2 else (f' [{c1 or c2}]' if c1 or c2 else '')
        lines.append(f'    {m1["name"]} + {m2["name"]}{combo}{geo}')
        if len(seen_pairs) >= 25:
            break

    # Notable individuals
    lines.append('')
    lines.append('NOTABLE INDIVIDUALS (excluding bots):')
    humans = [
        (g, player_meta[g], player_stats[g])
        for g in player_stats
        if g in player_meta and g not in bot_profiles
        and not player_meta[g]['name'].lower().startswith('lobby')
    ]
    most_days = sorted(humans, key=lambda x: -len(x[2]['days']))[:8]
    lines.append('  Most consistent (days active):')
    for g, m, ps in most_days:
        lines.append(f'    {m["name"]} ({m.get("instrument","")}) — {len(ps["days"])} days, '
                     f'{len(ps["servers"])} servers')

    wanderers = sorted(humans, key=lambda x: -len(x[2]['servers']))
    lines.append('  Most nomadic (servers visited):')
    for g, m, ps in wanderers[:6]:
        if len(ps['servers']) < 4:
            break
        lines.append(f'    {m["name"]} — {len(ps["servers"])} servers')

    # Truly international players: active on servers in both SEA and Western regions
    international = []
    for g, m, ps in humans:
        sea_ticks = sum(
            server_stats[s]['guid_ticks'].get(g, 0)
            for s in ps['servers'] if s in server_stats and _is_sea(s, server_meta)
        )
        west_ticks = sum(
            server_stats[s]['guid_ticks'].get(g, 0)
            for s in ps['servers'] if s in server_stats and not _is_sea(s, server_meta)
        )
        if sea_ticks >= 20 and west_ticks >= 20:
            international.append((g, m, sea_ticks, west_ticks))
    if international:
        international.sort(key=lambda x: -(x[2] + x[3]))
        lines.append('  Truly international (active on both Western and SEA servers):')
        for g, m, sea_t, west_t in international[:6]:
            lines.append(f'    {m["name"]} ({m.get("instrument","")}) — '
                         f'SEA ticks: {sea_t}, Western ticks: {west_t}')

    # Bot section
    lines.append('')
    lines.append('THE BOTS — exactly one wry paragraph, never mention bots anywhere else:')
    for srv_key, guid_list in sorted(bot_server_map.items(), key=lambda x: -len(x[1]))[:4]:
        sm   = server_meta.get(srv_key, {})
        name = sm.get('name', srv_key)
        bots = sorted(
            [(bot_profiles[g]['name'], bot_profiles[g].get('instrument', ''),
              bot_profiles[g]['ticks'])
             for g in guid_list if g in bot_profiles],
            key=lambda x: -x[2]
        )
        nstr = ', '.join(f'{n} ({i})' if i else n for n, i, _ in bots[:6])
        lines.append(f'  {name} ({_where(sm)}): {nstr}')
        lines.append(f'    Present essentially 24/7 across the full {span_days}-day window.')

    return '\n'.join(lines)


# ── LLM call ─────────────────────────────────────────────────────────────────

def call_llm(api_key, system_prompt, context, label):
    payload = {
        'system_instruction': {'parts': [{'text': system_prompt}]},
        'contents':           [{'parts': [{'text': context}]}],
        'generationConfig':   {'maxOutputTokens': 8192, 'temperature': 1.4},
    }
    req = urllib.request.Request(
        f'{GEMINI_URL}?key={api_key}',
        data=json.dumps(payload).encode(),
        method='POST',
        headers={'Content-Type': 'application/json'},
    )
    print(f'[llm] calling for "{label}"...', flush=True)
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
                lines = text.split('\n')
                text  = '\n'.join(lines[1:])
                text  = re.sub(r'```\s*$', '', text).strip()
            return text
    except (KeyError, IndexError) as e:
        print(f'[llm] parse error: {e}  snippet={str(raw)[:200]}', file=sys.stderr)
    return None


# ── HTML wrapper ──────────────────────────────────────────────────────────────

def wrap_html(body, subtitle):
    ts = datetime.utcnow().strftime('%Y-%m-%d %H:%M UTC')
    return f"""<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Jamulus — {subtitle}</title>
  <style>
    body {{ max-width:720px; margin:3em auto; padding:0 1.5em;
           font-family:Georgia,serif; font-size:1.1em; line-height:1.7;
           color:#222; background:#fdfdf8; }}
    p {{ margin:0 0 1.2em; }}
    b {{ font-weight:bold; }}
    i {{ font-style:italic; }}
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
    ap = argparse.ArgumentParser(description='Generate three long-form network essays')
    ap.add_argument('--days', type=int, default=None,
                    help='Limit to last N days (default: full census)')
    args = ap.parse_args()

    api_key = load_key()

    print('[load] player meta...', flush=True)
    player_meta = load_player_meta()
    print(f'       {len(player_meta):,} players', flush=True)

    print('[load] server meta...', flush=True)
    server_meta = load_server_meta()
    print(f'       {len(server_meta):,} servers', flush=True)

    print('[load] timeTogether...', flush=True)
    tt = load_time_together()
    print(f'       {len(tt):,} pairs', flush=True)

    print('[load] songs...', flush=True)
    songs = load_songs()
    print(f'       {sum(len(v) for v in songs.values())} titles', flush=True)

    lore = load_lore()

    cutoff_min = None
    if args.days:
        with open(CENSUS_CSV, 'rb') as f:
            f.seek(-512, 2)
            tail = f.read().split(b'\n')
            last_line = next((l.decode('utf-8', errors='replace')
                              for l in reversed(tail) if l.strip()), '')
        try:
            cutoff_min = int(last_line.split(',')[0]) - args.days * 1440
        except ValueError:
            print('[warn] could not parse last census line for cutoff', file=sys.stderr)

    player_stats, server_stats = scan_census(cutoff_min)

    first_min = min(v['first_min'] for v in server_stats.values())
    last_min  = max(v['last_min']  for v in server_stats.values())

    print('[analyze] bots...', flush=True)
    bot_guids = classify_bots(tt, player_stats)
    print(f'          {len(bot_guids)} bot GUIDs', flush=True)

    print('[analyze] pairs...', flush=True)
    human_pairs = build_human_pairs(tt, player_meta, bot_guids)
    print(f'          {len(human_pairs)} notable human pairs', flush=True)

    bot_profiles, bot_server_map = build_bot_profiles(
        player_meta, player_stats, bot_guids, server_meta)
    print(f'          {len(bot_profiles)} bot profiles', flush=True)

    print('[context] building...', flush=True)
    ctx = build_context(
        player_meta, server_meta, player_stats, server_stats,
        human_pairs, bot_profiles, bot_server_map,
        songs, lore, first_min, last_min,
    )
    print(f'          {len(ctx):,} chars', flush=True)

    for slug, prompt, subtitle in [
        ('longview',  PROMPT_LONGVIEW,  'The Long View — a cumulative portrait'),
        ('web',       PROMPT_WEB,       'The Web — connections across the network'),
        ('portraits', PROMPT_PORTRAITS, 'The Characters — individual player stories'),
    ]:
        body = call_llm(api_key, prompt, ctx, subtitle)
        if not body:
            print(f'[{slug}] LLM failed, skipping', file=sys.stderr)
            continue
        out = os.path.join(OUT_DIR, f'essay-long-en-{slug}.html')
        with open(out, 'w', encoding='utf-8') as f:
            f.write(wrap_html(body, subtitle))
        print(f'[{slug}] wrote {out}  ({len(body):,} chars)', flush=True)

    print('[done]', flush=True)


if __name__ == '__main__':
    main()
