#!/usr/bin/env python3
"""Count human visitors from telemetry.log. Usage: python3 traffic.py [minutes=60]"""
import sys, json
from datetime import datetime, timedelta, timezone
from collections import defaultdict

WINDOW_MINUTES = int(sys.argv[1]) if len(sys.argv) > 1 else 60

EXCLUDE_IPS = {'24.17.80.236', '75.253.12.89', '50.116.25.151'}
EXCLUDE_PREFIXES = ('134.19.', '172.56.')
EXCLUDE_HASHES = {'9dd8bae07c44800edd80024c02a0bbf6','8bfcb9816ab178394d56f6155cab4e73',
                  '52f7652674c02b2b9f1070c072881116','9d13bfe92e03','cfab1ba8c67c'}

TIER1 = {'friend_visibility','click_musician','click_listen','click_more','nearby_toggle',
         'tracked_arrival','ui_active','ui_nearby','ui_dark','ui_hide','tab_switch',
         'hover_server','scroll_depth'}
TIER2 = {'session_start','return_visit','tab_hidden','tab_visible','scroll_depth','hover_server'}
LEGIT_UA_TOKENS = ('Mozilla','Chrome','Firefox','Safari','Edge')

now = datetime.now(timezone.utc)
cutoff = now - timedelta(minutes=WINDOW_MINUTES)

# keyed by hash (identified) or 'ip:X' (anon)
visitors = defaultdict(lambda: {'events': set(), 'ua': '', 'ref': False})

with open('/root/JamFan22/JamFan22/data/telemetry.log') as f:
    for line in f:
        parts = line.strip().split(',', 5)
        if len(parts) < 6: continue
        try:
            payload = json.loads(parts[5])
        except: continue
        t_ms = payload.get('t', 0)
        if not t_ms or datetime.fromtimestamp(t_ms/1000, tz=timezone.utc) < cutoff:
            continue
        ip = parts[1].replace('::ffff:', '')
        h = parts[2]
        if ip in EXCLUDE_IPS or any(ip.startswith(p) for p in EXCLUDE_PREFIXES): continue
        if any(h.startswith(x) for x in EXCLUDE_HASHES): continue
        key = h if (h and h != 'anon') else f'ip:{ip}'
        visitors[key]['events'].add(payload.get('a', ''))
        if payload.get('ua'): visitors[key]['ua'] = payload['ua']
        if 'jamulus.live' in str(payload.get('ref', '')): visitors[key]['ref'] = True

t1 = t2p = t2m = t3 = 0
for d in visitors.values():
    ua, ref, evts = d['ua'], d['ref'], d['events']
    legit = any(t in ua for t in LEGIT_UA_TOKENS)
    if evts & TIER1:                    t1  += 1
    elif legit and ref and evts & TIER2: t2p += 1
    elif legit and ref:                  t2m += 1
    else:                                t3  += 1

print(f"Last {WINDOW_MINUTES} min — {len(visitors)} visitors (hash-deduped)")
print(f"  Tier 1  (clear human):        {t1}")
print(f"  Tier 2+ (behavioral evidence): {t2p}")
print(f"  Tier 2- (suspect):            {t2m}")
print(f"  Tier 3  (no evidence):        {t3}")
print(f"  Confirmed humans (T1+T2+):    {t1+t2p}")
