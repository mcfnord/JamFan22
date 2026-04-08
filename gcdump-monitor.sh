#!/bin/bash
# Periodic GC heap dump for JamFan22 memory tracking.
# Keeps 12 snapshots (~24h at 2h interval). Run via cron: 0 */2 * * *

DUMP_DIR="/root/JamFan22/heapdumps"
MAX_DUMPS=12
export DOTNET_ROOT=/root/.dotnet
export PATH="$PATH:/root/.dotnet/tools"

mkdir -p "$DUMP_DIR"

PID=$(pgrep -f "JamFan22/bin" | head -1)
if [ -z "$PID" ]; then
    echo "$(date): jamfan22 production process not found, skipping." >> "$DUMP_DIR/gcdump.log"
    exit 0
fi

OUTFILE="$DUMP_DIR/jamfan22-$(date +%Y%m%d-%H%M).gcdump"
dotnet-gcdump collect -p "$PID" -o "$OUTFILE" >> "$DUMP_DIR/gcdump.log" 2>&1

RSS_MB=$(awk '/VmRSS/ {print $2}' /proc/$PID/status 2>/dev/null)
echo "$(date): PID=$PID RSS=${RSS_MB}kB dump=$OUTFILE" >> "$DUMP_DIR/gcdump.log"

# Trim old dumps beyond MAX_DUMPS
ls -t "$DUMP_DIR"/*.gcdump 2>/dev/null | tail -n +$((MAX_DUMPS + 1)) | xargs rm -f
