#!/usr/bin/env python3
"""
Co-player frequency analysis on census.csv.

Usage:
  python3 coplayers.py [GUID] [--days N] [--top N]

Without a GUID: prints the top co-player pairs globally.
With a GUID:    prints that GUID's top co-players by shared minutes.

census.csv columns (no header):
  0: timestamp_minutes (minutes since 2023-01-01 00:00 UTC)
  1: guid
  2: server_ip:port
"""

import sys
import csv
import time
from collections import defaultdict

CENSUS = "/root/JamFan22/JamFan22/data/census.csv"
EPOCH_MINUTES = 0  # all timestamps are already minutes since epoch

def parse_args():
    import argparse
    p = argparse.ArgumentParser(description="Co-player analysis from census.csv")
    p.add_argument("guid", nargs="?", default=None, help="GUID to query (omit for global top pairs)")
    p.add_argument("--days", type=int, default=30, help="Look-back window in days (default 30)")
    p.add_argument("--top", type=int, default=20, help="Number of results to show (default 20)")
    return p.parse_args()

def minutes_now():
    import datetime
    epoch = datetime.datetime(2023, 1, 1, tzinfo=datetime.timezone.utc)
    now = datetime.datetime.now(datetime.timezone.utc)
    return int((now - epoch).total_seconds() / 60)

def main():
    args = parse_args()
    cutoff = minutes_now() - args.days * 1440

    target_guid = args.guid

    # Group GUIDs by (timestamp, server)
    # For targeted query: only load rows where we need to find co-players
    # Strategy: one pass — build bucket → [guid] map, then compute pairs

    print(f"Loading census.csv (last {args.days} days)...", file=sys.stderr)
    t0 = time.time()

    if target_guid:
        # Find all (ts, server) buckets the target GUID was in
        target_buckets = set()
        with open(CENSUS, newline="") as f:
            for row in csv.reader(f):
                if len(row) < 3:
                    continue
                try:
                    ts = int(row[0])
                except ValueError:
                    continue
                if ts < cutoff:
                    continue
                if row[1] == target_guid:
                    target_buckets.add((ts, row[2]))

        print(f"  {len(target_buckets)} active minutes for target GUID", file=sys.stderr)

        # Second pass: count co-presence with other GUIDs in those buckets
        coplayer_minutes = defaultdict(int)
        with open(CENSUS, newline="") as f:
            for row in csv.reader(f):
                if len(row) < 3:
                    continue
                try:
                    ts = int(row[0])
                except ValueError:
                    continue
                if ts < cutoff:
                    continue
                guid = row[1]
                if guid == target_guid:
                    continue
                if (ts, row[2]) in target_buckets:
                    coplayer_minutes[guid] += 1

        print(f"\nTop co-players for {target_guid} (last {args.days}d):\n")
        print(f"{'Minutes':>8}  GUID")
        print("-" * 50)
        for guid, mins in sorted(coplayer_minutes.items(), key=lambda x: -x[1])[:args.top]:
            print(f"{mins:>8}  {guid}")

    else:
        # Global mode: find top co-player pairs
        # Group by (ts, server) → set of guids, then count pairs
        bucket_guids = defaultdict(list)
        with open(CENSUS, newline="") as f:
            for row in csv.reader(f):
                if len(row) < 3:
                    continue
                try:
                    ts = int(row[0])
                except ValueError:
                    continue
                if ts < cutoff:
                    continue
                bucket_guids[(ts, row[2])].append(row[1])

        print(f"  {len(bucket_guids)} (timestamp, server) buckets loaded", file=sys.stderr)

        pair_minutes = defaultdict(int)
        for guids in bucket_guids.values():
            guids = list(set(guids))  # dedupe within bucket
            if len(guids) < 2:
                continue
            guids.sort()
            for i in range(len(guids)):
                for j in range(i + 1, len(guids)):
                    pair_minutes[(guids[i], guids[j])] += 1

        elapsed = time.time() - t0
        print(f"  Done in {elapsed:.1f}s", file=sys.stderr)

        print(f"\nTop co-player pairs globally (last {args.days}d):\n")
        print(f"{'Minutes':>8}  GUID-A                            GUID-B")
        print("-" * 80)
        for (g1, g2), mins in sorted(pair_minutes.items(), key=lambda x: -x[1])[:args.top]:
            print(f"{mins:>8}  {g1}  {g2}")

if __name__ == "__main__":
    main()
