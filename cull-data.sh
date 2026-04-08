#!/bin/bash

# Culls telemetry.log, keeping only lines newer than 15 days.
# First column is minutes since 2023-01-01 00:00:00 UTC (CSV).

cd /root/JamFan22/JamFan22

EPOCH_SECONDS=$(date -u -d "2023-01-01" +%s)
NOW_SECONDS=$(date -u +%s)
MINUTES_SINCE_EPOCH=$(((NOW_SECONDS - EPOCH_SECONDS) / 60))

# Cutoff: 15 days = 21600 minutes
CUTOFF_MINUTES=$((MINUTES_SINCE_EPOCH - 21600))

awk -v cutoff="$CUTOFF_MINUTES" -F, '$1 >= cutoff' "data/telemetry.log" > "data/tmpfile.log"
cat data/tmpfile.log > data/telemetry.log
rm data/tmpfile.log
