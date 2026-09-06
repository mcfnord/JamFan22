#!/bin/bash

cd /root/JamFan22/JamFan22/data/ || { echo "Failed to change directory"; exit 1; }

mv url-guids.csv url-guids-frozen.csv
touch url-guids.csv

# Filter for last 90 days (129600 minutes)
MAX_MINS=$(awk -F',' 'BEGIN{max=0} {if($1>max+0) max=$1} END{print max}' url-guids-frozen.csv)
if [ -z "$MAX_MINS" ] || [ "$MAX_MINS" -eq 0 ]; then
    cp url-guids-frozen.csv url-guids-trimmed.csv
else
    THRESHOLD=$((MAX_MINS - 129600))
    awk -F',' -v thresh="$THRESHOLD" '$1 >= thresh' url-guids-frozen.csv > url-guids-trimmed.csv
fi
rm url-guids-frozen.csv

cat url-guids.csv >> url-guids-trimmed.csv
rm url-guids.csv
mv -f url-guids-trimmed.csv url-guids.csv
