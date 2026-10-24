#!/bin/bash

# Culls telemetry.log and urls-rejected.csv to 15 days, fleet-guid-ip.csv and urls.csv to 90 days,
# gate.csv and gate-history.csv to 180 days, /root/dormant-telemetry to 90 days.
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

# gate.csv + gate-history.csv: 180 days = 259200 minutes (operator 2026-10-24). gate-history.csv is a
# one-time extraction, never appended; gate.csv is appended per row by jamfan22 (same copy-back as above).
GATE_CUTOFF=$((MINUTES_SINCE_EPOCH - 259200))
for f in gate.csv gate-history.csv; do
    awk -v cutoff="$GATE_CUTOFF" -F, '$1 >= cutoff' "data/$f" > "data/tmpfile.csv"
    cat data/tmpfile.csv > "data/$f"
    rm data/tmpfile.csv
done

# Dormant telemetry harvests (held-chat lines carry sender IPs): 90 days by file mtime (operator 2026-10-24)
find /root/dormant-telemetry -type f -mtime +90 -delete

# output.log: 90 days, cut IN PLACE (FU743, operator 2026-10-08). jamfan22 holds it O_APPEND via
# systemd; never awk/cat-copy it back like the files above -- appends during the copy are lost.
python3 /root/JamFan22/ops/logcut.py /root/JamFan22/JamFan22/output.log 90 --apply >> /root/JamFan22/JamFan22/output.log 2>&1
