#!/usr/bin/env python3
"""Hold an append-only log to its last N days by cutting its head IN PLACE.

Bisects on byte offset, to one block, for the first line dated >= now-N days
(the first 'YYYY-MM-DD HH:MM:SS' stamp in a 1 MB read), floors that offset to the 4096-byte
block, and removes everything before it with `fallocate --collapse-range`.
The cut is in place, so a writer holding the file O_APPEND (systemd's
`StandardOutput=append:`) keeps landing at the new end and loses nothing.
Never copy-and-rewrite a live log: lines appended during the copy are lost.
The first kept line may start mid-line (block alignment).

Dry by default; --apply cuts. Prints one `[LOGCUT]` line either way.
Refuses (exit 2) unless the stamps around the boundary bracket the cutoff.

usage: logcut.py <log> <days> [--apply]
"""
import datetime, os, re, subprocess, sys

STAMP = re.compile(rb"(20\d\d-\d\d-\d\d)[ T](\d\d:\d\d:\d\d)")
CHUNK = 1 << 20
BLOCK = 4096


def stamp_at(f, off):
    f.seek(off)
    m = STAMP.search(f.read(CHUNK))
    if not m:
        return None
    return datetime.datetime.strptime((m.group(1) + b" " + m.group(2)).decode(), "%Y-%m-%d %H:%M:%S")


def main():
    args = [a for a in sys.argv[1:] if a != "--apply"]
    apply = "--apply" in sys.argv[1:]
    if len(args) != 2:
        sys.exit(__doc__)
    path, days = args[0], int(args[1])
    now = datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None)
    cutoff = now - datetime.timedelta(days=days)
    size = os.path.getsize(path)
    head = f"[LOGCUT] {now:%F %T} {path} days={days} cutoff={cutoff:%F %T} size={size}"
    with open(path, "rb") as f:
        first = stamp_at(f, 0)
        if first is None or first >= cutoff:
            print(f"{head} first={first} nothing-to-cut")
            return
        lo, hi = 0, size
        while hi - lo > BLOCK:
            mid = (lo + hi) // 2
            s = stamp_at(f, mid)
            if s is None or s < cutoff:
                lo = mid
            else:
                hi = mid
        cut = lo - lo % BLOCK
        at_cut, at_hi = stamp_at(f, cut), stamp_at(f, hi)
    # Guard: the kept head must be older than the cutoff (we never cut newer
    # data) and the boundary must be real (newer data exists just past it).
    if cut <= 0:
        print(f"{head} first={first} cut=0 nothing-to-cut")
        return
    if at_cut is None or at_cut >= cutoff or (hi < size and (at_hi is None or at_hi < cutoff)):
        print(f"{head} REFUSE stamp_at_cut={at_cut} stamp_at_hi={at_hi} cut={cut}")
        sys.exit(2)
    line = (f"{head} first={first} cut={cut} ({cut / 1e9:.2f} GB) keep={size - cut} "
            f"stamp_at_cut={at_cut}")
    if not apply:
        print(f"{line} DRY")
        return
    subprocess.run(["fallocate", "--collapse-range", "--offset", "0", "--length", str(cut), path], check=True)
    print(f"{line} CUT newsize={os.path.getsize(path)}")


if __name__ == "__main__":
    main()
