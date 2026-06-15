#!/usr/bin/env python3
"""
fleet-report.py — recent fleet server activity summary.
Shows welcome messages, silence state changes, and session durations.

Usage:
  python3 fleet-report.py          # last 4 hours
  python3 fleet-report.py --hours 8
  python3 fleet-report.py --today
"""
import sys, re, html, datetime, collections

# ── Config ────────────────────────────────────────────────────────────────────
BASE_DIR  = "/root/JamFan22/JamFan22"
CENSUS    = f"{BASE_DIR}/data/census.csv"
CENSUSGEO = f"{BASE_DIR}/data/censusgeo.csv"
FLEET_IPS = f"{BASE_DIR}/data/fleet-server-ips.txt"
EVENTS    = f"{BASE_DIR}/data/welcome-events.log"
OUTLOG    = f"{BASE_DIR}/output.log"
EPOCH     = datetime.datetime(2023, 1, 1, tzinfo=datetime.timezone.utc)

LOBBY_RE  = re.compile(r'lobby', re.I)
OWNER_HASHES = {
    "9dd8bae07c44800edd80024c02a0bbf6",
    "8bfcb9816ab178394d56f6155cab4e73",
    "52f7652674c02b2b9f1070c072881116",
}
OWNER_PREFIXES = ("9d13bfe92e03", "cfab1ba8c67c")

# ── Args ──────────────────────────────────────────────────────────────────────
hours = 4
for i, arg in enumerate(sys.argv[1:]):
    if arg == "--hours" and i + 2 <= len(sys.argv[1:]):
        hours = int(sys.argv[i + 2])
    elif arg == "--today":
        now_utc = datetime.datetime.now(datetime.timezone.utc)
        hours = now_utc.hour + now_utc.minute / 60 + 0.01

now_utc    = datetime.datetime.now(datetime.timezone.utc)
cutoff_dt  = now_utc - datetime.timedelta(hours=hours)
cutoff_str = cutoff_dt.strftime("%Y-%m-%d %H:%M")
minute_now = int((now_utc - EPOCH).total_seconds() / 60)
minute_cut = minute_now - int(hours * 60)

print(f"Fleet report — {cutoff_dt.strftime('%Y-%m-%d %H:%M UTC')} → now  ({hours:.0f}h window)")
print("=" * 70)

# ── Load fleet servers ────────────────────────────────────────────────────────
fleet_servers = set()
with open(FLEET_IPS) as f:
    for line in f:
        s = line.strip()
        if not s or s.startswith('#'): continue
        parts = s.split(':')
        if len(parts) >= 2:
            fleet_servers.add(f"{parts[0]}:{parts[1]}")

# ── Load name lookup ──────────────────────────────────────────────────────────
guid_name = {}
with open(CENSUSGEO) as f:
    for line in f:
        parts = line.strip().split(',')
        if len(parts) >= 2:
            guid_name[parts[0]] = parts[1].replace('+', ' ')

# ── Welcome messages ──────────────────────────────────────────────────────────
print("\n── Welcome messages ──")
TAG_RE = re.compile(r'<[^>]+>')

def strip_html(s):
    return html.unescape(TAG_RE.sub(' ', s)).strip()

welcome_count = llm_count = fallback_count = 0
with open(EVENTS) as f:
    for line in f:
        ts = line[:16]  # "YYYY-MM-DD HH:MM"
        if ts < cutoff_str:
            continue
        # Format: "TIMESTAMP | meta key=val signals=a|b|c | <html>"
        # Split only on " | " (with spaces) to preserve pipes inside signals
        parts = line.strip().split(' | ', 2)
        if len(parts) < 3:
            continue
        meta, msg = parts[1], parts[2]
        clean = strip_html(msg)

        m_server  = re.search(r'server="([^"]+)"', meta)
        m_nation  = re.search(r'nation=(\w*)', meta)
        m_rich    = re.search(r'rich=(\d)', meta)
        m_llm     = re.search(r'llm=(\d)', meta)
        m_ms      = re.search(r'ms=(\d+)', meta)
        m_signals = re.search(r'signals=(\S+)', meta)

        server  = m_server.group(1)  if m_server  else '?'
        nation  = m_nation.group(1)  if m_nation  else ''
        rich    = m_rich.group(1)    if m_rich    else '0'
        llm     = m_llm.group(1)     if m_llm     else '0'
        ms      = int(m_ms.group(1)) if m_ms      else 0
        signals = m_signals.group(1) if m_signals else ''

        welcome_count += 1
        if llm == '1':
            llm_count += 1
        else:
            fallback_count += 1

        label = f"LLM {ms}ms" if llm == '1' else f"fallback {ms}ms"
        nat   = f"[{nation}]" if nation else "[no-nation]"
        sig   = f"  {signals}" if signals else ""
        print(f"  {ts}  {server:<18} {nat:<6}  {label}{sig}")
        print(f"             {clean[:120]}")

if welcome_count == 0:
    print("  (none)")
print(f"\n  Total: {welcome_count}  (LLM: {llm_count}, fallback: {fallback_count})")

# ── Silence state changes ─────────────────────────────────────────────────────
print("\n── Silence state changes (LEVEL-POLL) ──")
silence_events = []
date_prefix = cutoff_dt.strftime("%Y-%m-%d")
today_prefix = now_utc.strftime("%Y-%m-%d")
prefixes = set()
d = cutoff_dt
while d <= now_utc + datetime.timedelta(days=1):
    prefixes.add(d.strftime("%Y-%m-%d"))
    d += datetime.timedelta(days=1)

with open(OUTLOG) as f:
    for line in f:
        if '[LEVEL-POLL]' not in line:
            continue
        # Line format: [LEVEL-POLL] 2026-06-15 HH:MM:SS ip:port: quiet=X clients=N
        m = re.search(r'\[LEVEL-POLL\] (\d{4}-\d{2}-\d{2} \d{2}:\d{2})', line)
        if not m:
            continue
        if m.group(1) < cutoff_str:
            continue
        silence_events.append(line.strip())

if silence_events:
    for e in silence_events:
        print(f"  {e}")
else:
    print("  (no state changes in window — fleet silence was stable)")

# ── Session durations ─────────────────────────────────────────────────────────
print("\n── Sessions on fleet servers ──")

# guid -> server -> sorted list of minutes
ticks   = collections.defaultdict(lambda: collections.defaultdict(list))
audible = collections.defaultdict(lambda: collections.defaultdict(int))

with open(CENSUS) as f:
    for line in f:
        parts = line.strip().split(',')
        if len(parts) < 3: continue
        try:
            minute = int(parts[0])
        except ValueError:
            continue
        if minute < minute_cut:
            continue
        guid, server = parts[1], parts[2]
        if server not in fleet_servers:
            continue
        name = guid_name.get(guid, '')
        if guid in OWNER_HASHES or any(guid.startswith(p) for p in OWNER_PREFIXES):
            continue
        is_lobby = LOBBY_RE.search(name) or LOBBY_RE.search(guid)
        aud = len(parts) >= 4 and parts[3] == '1'
        ticks[guid][server].append(minute)
        if aud:
            audible[guid][server] += 1

rows = []
for guid, svrs in ticks.items():
    name = guid_name.get(guid, guid[:8])
    is_lobby = bool(LOBBY_RE.search(name) or LOBBY_RE.search(guid))
    for server, minutes in svrs.items():
        uniq = sorted(set(minutes))
        dur  = len(uniq)
        aud  = audible[guid][server]
        start_dt = EPOCH + datetime.timedelta(minutes=uniq[0])
        rows.append((is_lobby, dur, aud, name, server, start_dt))

rows.sort(key=lambda r: (r[0], -r[1]))  # humans first, then by duration desc

humans = [r for r in rows if not r[0]]
bots   = [r for r in rows if r[0]]

if humans:
    for _, dur, aud, name, server, start in humans:
        aud_str = f"aud={aud}" if aud else "silent"
        print(f"  {start.strftime('%H:%M UTC')}  {dur:3d} min  {aud_str:<8}  {name:<25}  {server}")
else:
    print("  (no human sessions)")

if bots:
    print(f"\n  Lobby bots ({len(bots)}):")
    for _, dur, aud, name, server, start in bots:
        print(f"    {start.strftime('%H:%M UTC')}  {dur:3d} min  {name:<30}  {server}")
