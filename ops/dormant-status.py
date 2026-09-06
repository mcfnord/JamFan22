#!/usr/bin/env python3
"""Print current dormant server scores as % of their start threshold."""

import re
from datetime import datetime, timezone, timedelta

LOG = '/root/dormant-monitor.log'
# Drop names not seen within this many minutes of the newest log entry. The
# monitor cycles every ~20 min, so a renamed-away instance (e.g. an old alias)
# stops updating and falls off automatically — no hand-maintained alias list.
STALE_MINUTES = 90

_TS_RE = re.compile(r'^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})')

latest = {}
last_seen = {}
with open(LOG) as f:
    for line in f:
        tsm = _TS_RE.match(line)
        ts = (datetime.strptime(tsm.group(1), '%Y-%m-%d %H:%M:%S')
              .replace(tzinfo=timezone.utc)) if tsm else None
        m = re.search(
            r'(\w[\w. ]*?):\s+score=([\d.]+)\s+instance=(running|stopped).*threshold=([\d.]+)',
            line)
        if m:
            name, score, state, thr = (
                m.group(1).strip(), float(m.group(2)), m.group(3), float(m.group(4)))
            latest[name] = (score, state, thr)
            last_seen[name] = ts
        ms = re.search(
            r'(\w[\w. ]*?):\s+schedule=(\S+?)UTC\s+in_window=\w+\s+instance=(running|stopped)',
            line)
        if ms:
            name, sched, state = ms.group(1).strip(), ms.group(2), ms.group(3)
            latest[name] = (sched, state, None)
            last_seen[name] = ts
        # Stop/start decisions happen after the score line — override state immediately
        m2 = re.search(r'(\w[\w. ]*?):\s+stopping (?:after|\(scheduled)', line)
        if m2:
            name = m2.group(1).strip()
            if name in latest:
                latest[name] = (latest[name][0], 'stopped', latest[name][2])
                last_seen[name] = ts
        m3 = re.search(r'(\w[\w. ]*?):\s+starting \((?:score|scheduled)', line)
        if m3:
            name = m3.group(1).strip()
            if name in latest:
                latest[name] = (latest[name][0], 'running', latest[name][2])
                last_seen[name] = ts

# Prune stale names (renamed-away aliases stop updating and fall off here).
if last_seen:
    ref = max(t for t in last_seen.values() if t)
    stale = timedelta(minutes=STALE_MINUTES)
    latest = {n: v for n, v in latest.items()
              if last_seen.get(n) and ref - last_seen[n] <= stale}

def startup_sort_key(item):
    _, (score, state, thr) = item
    if state == 'running':
        return (2, 0.0)
    if thr is None:
        # Scheduled: minutes until next window opens
        try:
            start_str = score.split('-')[0]
            sh, sm = map(int, start_str.split(':'))
            now = datetime.now(timezone.utc)
            nxt = now.replace(hour=sh, minute=sm, second=0, microsecond=0)
            if nxt <= now:
                nxt += timedelta(days=1)
            return (0, (nxt - now).total_seconds() / 60)
        except Exception:
            return (0, float('inf'))
    # Demand-scored: ascending pct so highest is just before RUNNING
    return (1, 100 * score / thr)

print(f"{'Server':<14} {'Score':>8} {'Threshold':>10} {'% of thr':>9} {'State':>8}")
print('-' * 55)
for name, (score, state, thr) in sorted(latest.items(), key=startup_sort_key):
    state_str = 'RUNNING' if state == 'running' else 'stopped'
    if thr is None:
        print(f"{name:<14} {'sched':>8} {score:>10} {'-':>8}  {state_str:>8}")
        continue
    pct = 100 * score / thr
    print(f"{name:<14} {score:>8.4f} {thr:>10.5f} {pct:>8.0f}% {state_str:>8}")
