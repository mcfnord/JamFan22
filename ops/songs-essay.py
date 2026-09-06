#!/usr/bin/env python3
"""
songs-essay.py — A full long-form essay about the Jamulus network told through
song titles and cultural references. Builds a music-first context from url-guids.csv,
then asks Gemini to tell the community's story by following the songs.

Usage:  python3 ops/songs-essay.py
Output: JamFan22/wwwroot/essays/essay-songs-en.html
"""

import json, os, re, sys, time, urllib.parse, urllib.request
from collections import defaultdict
from datetime import datetime

# ── Paths ────────────────────────────────────────────────────────────────────

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
BASE  = os.path.dirname(SCRIPT_DIR)
APP   = os.path.join(BASE, 'JamFan22')
DATA  = os.path.join(APP, 'data')

URL_GUIDS_CSV = os.path.join(DATA, 'url-guids.csv')
URLS_CSV      = os.path.join(DATA, 'urls.csv')
CENSUSGEO_CSV = os.path.join(DATA, 'censusgeo.csv')
SERVER_CSV    = os.path.join(DATA, 'server.csv')
LORE_JSON     = os.path.join(DATA, 'server-lore.json')
KEY_FILE      = os.path.join(DATA, 'gemini-key.txt')
OUT_DIR       = os.path.join(APP, 'wwwroot', 'essays')

GEMINI_URL = 'https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:generateContent'

# ── Style ────────────────────────────────────────────────────────────────────

SYSTEM_PROMPT = """You write a long-form essay for Jamulus.live — a live radar for the global real-time music jamming network. Jamulus lets musicians play together live over the internet.

THIS ESSAY: Tell the story of the Jamulus community through the songs they play. Use the song titles and artists as anchors — follow the music to understand who these people are. What does "Killing the Blues" tell you about a late-night duo in South America? What do thirty Thai pop songs reveal about the people who chose them? What does it mean when a server full of people reaches for "Oh Ma Jolie Sarah" by Johnny Hallyday?

STRUCTURE — build around musical worlds:
- Each section hangs on a song, an artist, a genre, or a cultural moment — not a server.
- People appear because the music pulls them in. Geography appears only when it earns its place.
- The Thai world is enormous and distinct. Give it real weight — this is not a footnote.
- Lyric echoes earn their place when a specific line illuminates what was happening.

GEOGRAPHY RULE: mention a location at most once per paragraph, only when it is the actual point — not decoration. Never connect a song's origin to where it was played ("written in Melbourne, played in Germany" — don't). Never use server datacenter location as atmosphere. City/country of a musician: only if it's the point of the sentence, not a detail to fill space.

INSTRUMENTS RULE: player instruments are self-reported and may not match what they actually played that day. Use broader categories when it serves the prose — "a horn" instead of "trumpet", "strings" instead of "violin", "keyboards" instead of "organ". Or just name the person without naming their instrument. Never write "listed as", "credited with", "allegedly", or any other qualifier that implies distrust. Simply be naturally vague.

STYLE:
- HTML <p> tags only. No markdown, no headers, no bullets.
- <b>musician name</b> every time a musician is named. <i>server name</i> every time.
- <i>Song Title</i> every time a song is named.
- Simple past tense ("held" not "was holding").
- Stop when the story is told — no summary paragraph.
- Lyric echo: <blockquote><em>line one<br>line two</em></blockquote>
- Song titles come from the data — never mention links, chat, or how you know.
- No hedging words ("it seems", "seemingly", "at some point"). Instrument hedging is fine.
- Never say "over at" — just "at."
- Lobby entries (names like "++lobby++" or "++lobby+[N]++") are streaming listeners — mention at most as an anonymous crowd.
- Never use the word "unnamed" — if you don't know who someone is, omit them or write "another player", "someone else".
- LowBot is an automated backing track, not a human.

BANNED PHRASES: "global community", "passion for music", "love of music", "transcends borders",
"musical journey", "testament to", "thriving", "wonderful", "amazing", "the beauty of",
"connect musicians", "came together", "around the world", "shared a passion", "from across",
"knew each other's instincts", "not just playing songs", "running a set", "held court",
"deep songbook", "the chops to", "deep cuts", "effortlessly", "seamlessly"

Do not editorialize about the musicians' skill, chemistry, or intentions. Let the song choices speak. If you find yourself writing a sentence about *how well* they played together, cut it and describe what they played instead.

WRONG: The transition from seventies disco to a contemporary pop hit was seamless, the work of players who knew each other's instincts. They were not just playing songs; they were running a set.
RIGHT: They powered through <i>Hot Stuff</i> and <i>Don't Leave Me This Way</i>, then turned to Olivia Rodrigo's <i>Vampire</i>.

WRONG: On a server hosted in Germany, a line written in Melbourne decades earlier took on a new life.
RIGHT: [just say what they played. The geography of the song's origin and the server's datacenter are not the story.]

WRONG: Musicians from France, Japan, Germany, and Spain came together on a server.
RIGHT: The same group ran through <i>Oh Ma Jolie Sarah</i>, <i>Sur La Route De Memphis</i>, and <i>Dakota</i> in a single sitting.

WRONG: Eric on bass and David on drums locked in.
RIGHT: <b>Eric</b> and <b>David</b> were there for all of it.

Length: 6–9 paragraphs. Dense with specifics. No filler.
"""

# ── Loaders ──────────────────────────────────────────────────────────────────

def load_server_meta():
    meta = {}
    try:
        with open(SERVER_CSV) as f:
            for line in f:
                cols = line.strip().split(',')
                if len(cols) >= 2 and cols[0]:
                    meta[cols[0]] = urllib.parse.unquote_plus(cols[1]) if cols[1] else cols[0]
    except FileNotFoundError:
        pass
    # server-lore.json 'name' field overrides server.csv (server.csv lost chronological
    # ordering when it was sorted+deduped, so its "last row" is not reliably most recent)
    try:
        lore = json.load(open(LORE_JSON))
        for ip_port, entry in lore.items():
            if isinstance(entry, dict) and entry.get('name'):
                meta[ip_port] = entry['name']
    except (FileNotFoundError, json.JSONDecodeError):
        pass
    return meta

def load_guid_meta():
    meta = {}
    try:
        with open(CENSUSGEO_CSV) as f:
            for line in f:
                cols = line.strip().split(',')
                if len(cols) >= 2 and cols[0]:
                    name    = urllib.parse.unquote_plus(cols[1]) if cols[1] else ''
                    instr   = urllib.parse.unquote_plus(cols[2]) if len(cols) > 2 and cols[2] else ''
                    country = urllib.parse.unquote_plus(cols[4]) if len(cols) > 4 and cols[4] else ''
                    meta[cols[0]] = (name, instr, country)
    except FileNotFoundError:
        pass
    return meta

def load_lore():
    try:
        with open(LORE_JSON) as f:
            return json.load(f)
    except (FileNotFoundError, json.JSONDecodeError):
        return {}

GEO_CACHE_FILE = os.path.join(DATA, 'server-ip-geo-cache.json')

def geolocate_servers(ip_ports):
    """Return {ip: 'City, Country'} from the shared geo cache. No network calls."""
    try:
        cache = json.load(open(GEO_CACHE_FILE))
    except (FileNotFoundError, json.JSONDecodeError):
        cache = {}

    result = {}
    for ip_port in ip_ports:
        ip = ip_port.split(':')[0]
        hit = cache.get(ip, {})
        if hit.get('status') == 'success':
            parts = [p for p in [hit.get('city', ''), hit.get('country', '')] if p]
            result[ip] = ', '.join(parts) if parts else ''
        else:
            result[ip] = ''  # caller falls back to server.csv claimed location
    return result

def is_bot_name(name):
    return bool(re.search(r'lobby', name, re.I)) or name.startswith('++')

def parse_title(raw):
    t = urllib.parse.unquote_plus(raw).replace('+', ' ').strip()
    return t

def extract_ug_slug(url):
    """Return (artist, title) from a UG tab URL, or None."""
    m = re.search(r'tabs\.ultimate-guitar\.com/tab/([^/?]+)/([^?]+)', url)
    if not m:
        return None
    artist = m.group(1).replace('-', ' ').title()
    slug   = m.group(2)
    title  = re.sub(r'[-_](?:chords|tabs|tab|official).*', '', slug, flags=re.I)
    title  = title.replace('-', ' ').replace('_', ' ').strip().title()
    return artist, title

def extract_thai_room(url):
    m = re.search(r'[?&]room=([^&]+)', url)
    if m:
        return urllib.parse.unquote_plus(m.group(1))
    return None

# ── Build song context ───────────────────────────────────────────────────────

def build_context(server_meta, guid_meta, lore, server_geo):
    # song_events: list of {server, server_name, title, artist, guids, players}
    song_events = []

    with open(URL_GUIDS_CSV) as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            cols = line.split(',')
            if len(cols) < 3:
                continue
            server = cols[1]
            url_raw = cols[2]
            title_raw = cols[3] if len(cols) > 3 else ''
            guids_raw = cols[4] if len(cols) > 4 else ''

            url = urllib.parse.unquote_plus(url_raw)
            title = parse_title(title_raw) if title_raw else ''
            guids = [g for g in guids_raw.split('|') if g] if guids_raw else []

            # Resolve title / artist
            artist = ''
            if '—' in title:
                parts = title.split('—', 1)
                title = parts[0].strip()
                artist = parts[1].strip()
            elif not title:
                ug = extract_ug_slug(url)
                if ug:
                    artist, title = ug
                else:
                    room = extract_thai_room(url)
                    if room:
                        title = room
                        artist = '(Thai set room)'

            if not title:
                continue

            # Players (non-bot); instruments are self-reported, country omitted
            players = []
            for g in guids:
                info = guid_meta.get(g, ('', '', ''))
                name, instr, _country = info
                if name and not is_bot_name(name):
                    players.append({'name': name, 'instr': instr})

            server_name = server_meta.get(server, server)
            song_events.append({
                'server': server,
                'server_name': server_name,
                'title': title,
                'artist': artist,
                'players': players,
                'n_guids': len(guids),
            })

    # Deduplicate: (server, title) → merge players
    merged = {}
    for ev in song_events:
        key = (ev['server'], ev['title'])
        if key not in merged:
            merged[key] = ev.copy()
            merged[key]['player_set'] = {p['name'] for p in ev['players']}
        else:
            for p in ev['players']:
                if p['name'] not in merged[key]['player_set']:
                    merged[key]['players'].append(p)
                    merged[key]['player_set'].add(p['name'])

    events = list(merged.values())

    # Group by server for context output
    by_server = defaultdict(list)
    for ev in events:
        by_server[ev['server']].append(ev)

    # Identify Thai vs Western by geolocated server IP
    thai_servers = set()
    west_servers = set()
    for server in by_server:
        ip = server.split(':')[0]
        geo = server_geo.get(ip, '')
        if any(c in geo for c in ('Thailand', 'Hong Kong', 'Philippines', 'Vietnam', 'Singapore')):
            thai_servers.add(server)
        else:
            west_servers.add(server)

    # Build context string
    lines = []
    lines.append("=== SONGS PLAYED ON JAMULUS — FULL URL DATA ===\n")
    lines.append("These are real song titles found in chord/tab URLs shared during sessions.\n")
    lines.append("Each entry: [Server] | Song Title — Artist | Players present (name, instrument, country)\n\n")

    # Produce server sections sorted: western servers first, then Thai
    order = sorted(by_server.keys(), key=lambda s: (
        0 if s in west_servers and s not in thai_servers else
        1 if s in thai_servers else 2
    ))

    for server in order:
        evs = by_server[server]
        sname = server_meta.get(server, server)
        ip = server.split(':')[0]
        geo = server_geo.get(ip) or '(location unknown)'
        srv_lore = lore.get(server, {})
        lines.append(f"SERVER: {sname} — hosted in {geo}")
        if srv_lore.get('tagline'):
            lines.append(f"  Tagline: {srv_lore['tagline']}")
        if srv_lore.get('themes'):
            lines.append(f"  Themes: {', '.join(srv_lore['themes'])}")
        lines.append("")

        for ev in evs:
            song_line = ev['title']
            if ev['artist'] and ev['artist'] != '(Thai set room)':
                song_line += f" — {ev['artist']}"
            lines.append(f"  Song: {song_line}")
            for p in ev['players'][:8]:
                desc = p['name']
                if p['instr']: desc += f' (self-reported: {p["instr"]})'
                lines.append(f"    - {desc}")
            lines.append("")

    # Also pull raw url songs (no guid data) to fill Thai world
    lines.append("\n=== ADDITIONAL URL EVIDENCE (no player list) ===\n")
    lines.append("From urls.csv — server + URL without guid resolution:\n")
    thai_urls = defaultdict(set)
    with open(URLS_CSV) as f:
        for line in f:
            cols = line.strip().split(',')
            url_col = None
            for i, c in enumerate(cols):
                if re.match(r'https?%3a', c, re.I):
                    url_col = i
                    break
            if url_col is None:
                continue
            server = None
            # server is often at col 0 (old) or col 2 (new with source col)
            for c in cols:
                if re.match(r'\d+\.\d+\.\d+\.\d+:\d+', c):
                    server = c
                    break
            if server is None:
                continue
            url = urllib.parse.unquote_plus(cols[url_col])
            if 'chordtabs.in.th' in url or 'dochord.com' in url or 'busk.town' in url or 'chords69cl' in url:
                m = re.search(r'busk\.town/songs/(.+)', url)
                if m:
                    title = urllib.parse.unquote_plus(m.group(1)).replace('-', ' ')
                    thai_urls[server].add(f"busk.town: {title}")
                m2 = re.search(r'[?&]room=([^&]+)', url)
                if m2:
                    room = urllib.parse.unquote_plus(m2.group(1))
                    thai_urls[server].add(f"room: {room}")

    for server, items in sorted(thai_urls.items()):
        sname = server_meta.get(server, server)
        lines.append(f"Thai server {sname} ({server}):")
        for item in sorted(items):
            lines.append(f"  {item}")
        lines.append("")

    return '\n'.join(lines)

# ── LLM call ────────────────────────────────────────────────────────────────

def call_llm(api_key, context):
    payload = {
        'system_instruction': {'parts': [{'text': SYSTEM_PROMPT}]},
        'contents': [{'role': 'user', 'parts': [{'text': context}]}],
        'generationConfig': {
            'maxOutputTokens': 8192,
            'temperature': 1.3,
        },
    }
    body = json.dumps(payload).encode()
    url = f'{GEMINI_URL}?key={api_key}'
    req = urllib.request.Request(url, data=body,
                                 headers={'Content-Type': 'application/json'})
    t0 = time.time()
    print('[llm] calling Gemini 2.5 Pro...')
    with urllib.request.urlopen(req, timeout=120) as resp:
        data = json.load(resp)
    elapsed = time.time() - t0
    print(f'[llm] done in {elapsed:.1f}s')

    parts = data['candidates'][0]['content']['parts']
    text = ''
    for part in parts:
        if part.get('thought'):
            continue
        text += part.get('text', '')
    return text.strip()

def wrap_html(body, ts):
    return f"""<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>What They Played — Jamulus Songs Essay</title>
<style>
body {{ font-family: Georgia, serif; max-width: 740px; margin: 3em auto; line-height: 1.7; color: #222; padding: 0 1.5em; }}
blockquote {{ border-left: 3px solid #ccc; margin: 1.5em 0; padding: 0.5em 1.5em; color: #555; font-style: italic; }}
p {{ margin: 1.2em 0; }}
.meta {{ font-size: 0.8em; color: #999; margin-top: 3em; }}
</style>
</head>
<body>
{body}
<p class="meta">Generated {ts} · Jamulus network — song URL data · jamulus.live</p>
</body>
</html>
"""

# ── Main ─────────────────────────────────────────────────────────────────────

def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    api_key = open(KEY_FILE).read().strip()

    print('[load] server meta...')
    server_meta = load_server_meta()
    print('[load] guid meta...')
    guid_meta = load_guid_meta()
    print('[load] lore...')
    lore = load_lore()

    print('[geo] resolving server IPs from cache...')
    # Collect all server IPs that appear in url-guids.csv
    import csv as _csv
    server_ips = set()
    with open(URL_GUIDS_CSV) as f:
        for line in f:
            cols = line.strip().split(',')
            if len(cols) >= 2:
                server_ips.add(cols[1])
    server_geo = geolocate_servers(server_ips)
    for ip_port, loc in sorted(server_geo.items()):
        print(f'  {ip_port} → {loc or "(not in cache)"}')

    print('[build] song context...')
    context = build_context(server_meta, guid_meta, lore, server_geo)
    print(f'[context] {len(context):,} chars')

    essay_html = call_llm(api_key, context)

    ts = datetime.utcnow().strftime('%Y-%m-%d %H:%M UTC')
    out_path = os.path.join(OUT_DIR, 'essay-songs-en.html')
    with open(out_path, 'w') as f:
        f.write(wrap_html(essay_html, ts))
    print(f'[done] wrote {out_path}')

if __name__ == '__main__':
    main()
