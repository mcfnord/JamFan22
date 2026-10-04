#!/bin/bash

# Culls telemetry.log and urls-rejected.csv to 15 days, fleet-guid-ip.csv and urls.csv to 90 days.
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

# fleet-guid-ip.csv: 90 days = 129600 minutes (operator 2026-10-04; was 15)
FLEET_CUTOFF=$((MINUTES_SINCE_EPOCH - 129600))
awk -v cutoff="$FLEET_CUTOFF" -F, '$1 >= cutoff' "data/fleet-guid-ip.csv" > "data/tmpfile.csv"
cat data/tmpfile.csv > data/fleet-guid-ip.csv
rm data/tmpfile.csv

awk -v cutoff="$CUTOFF_MINUTES" -F, '$1 >= cutoff' "data/urls-rejected.csv" > "data/tmpfile.csv"
cat data/tmpfile.csv > data/urls-rejected.csv
rm data/tmpfile.csv

# urls.csv: 90 days = 129600 minutes
URLS_CUTOFF=$((MINUTES_SINCE_EPOCH - 129600))
awk -v cutoff="$URLS_CUTOFF" -F, '$1 >= cutoff' "data/urls.csv" > "data/tmpfile.csv"
cat data/tmpfile.csv > data/urls.csv
rm data/tmpfile.csv
