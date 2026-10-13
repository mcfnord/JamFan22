#!/usr/bin/env python3
"""Drop predicted.csv rows whose GUID already has a kept row within WINDOW minutes before.
nearby.cs:483 builds ToDictionary(p => p.Guid) over the rows inside [now-10, now+180];
two rows of one GUID in that span throw and /api/nearby returns 500 for every client.
Backs up first; prints each dropped row. Usage: dedupe-predicted.py <path> [--dry]"""
import sys, shutil, time
path = sys.argv[1]; dry = '--dry' in sys.argv
WINDOW = 190
rows = [l.rstrip('\n') for l in open(path, encoding='utf-8', errors='replace') if l.strip()]
parsed = []
for l in rows:
    f = l.split(',')
    try: t = int(f[0].strip())
    except ValueError: parsed.append((None, None, l)); continue
    parsed.append((t, f[1].strip() if len(f) > 1 else '', l))
order = sorted(range(len(parsed)), key=lambda i: (parsed[i][0] is None, parsed[i][0] or 0))
last = {}; keep = set(); dropped = []
for i in order:
    t, g, l = parsed[i]
    if t is None: keep.add(i); continue
    if g in last and t - last[g] < WINDOW: dropped.append(l); continue
    last[g] = t; keep.add(i)
print(f"rows={len(rows)} kept={len(keep)} dropped={len(dropped)} dry={int(dry)}")
for d in dropped: print("DROP", d)
if dropped and not dry:
    bak = f"{path}.bak-{time.strftime('%Y%m%dT%H%M%SZ', time.gmtime())}"
    shutil.copy2(path, bak); print("backup", bak)
    out = [parsed[i][2] for i in range(len(parsed)) if i in keep]
    tmp = path + '.tmp'
    with open(tmp, 'w', encoding='utf-8') as fh: fh.write('\n'.join(out) + '\n')
    shutil.move(tmp, path); print("wrote", path)
