#!/bin/bash
# Periodic GC heap dump for JamFan22 memory tracking.
# Keeps 12 snapshots (~24h at 2h interval). Run via cron: 0 */2 * * *

DUMP_DIR="/root/JamFan22/heapdumps"
MAX_DUMPS=12
export DOTNET_ROOT=/root/.dotnet
export PATH="$PATH:/root/.dotnet/tools"

mkdir -p "$DUMP_DIR"

dump_instance() {
    local PID="$1"
    local LABEL="$2"
    local OUTFILE="$DUMP_DIR/${LABEL}-$(date +%Y%m%d-%H%M).gcdump"
    dotnet-gcdump collect -p "$PID" -o "$OUTFILE" >> "$DUMP_DIR/gcdump.log" 2>&1
    RSS_KB=$(awk '/VmRSS/ {print $2}' /proc/$PID/status 2>/dev/null)
    echo "$(date): PID=$PID label=$LABEL RSS=${RSS_KB}kB dump=$OUTFILE" >> "$DUMP_DIR/gcdump.log"
}

# Production instance (runs from JamFan22/bin via dotnet run)
PROD_PID=$(pgrep -f "JamFan22/bin" | head -1)
if [ -z "$PROD_PID" ]; then
    echo "$(date): jamfan22 production process not found, skipping." >> "$DUMP_DIR/gcdump.log"
else
    dump_instance "$PROD_PID" "jamfan22"
fi

# Debug instance (runs from /tmp/jamfan-test-build, if active)
DEBUG_PID=$(lsof -t -i :5000 2>/dev/null | head -1)
if [ -n "$DEBUG_PID" ]; then
    echo "$(date): debug instance found on port 5000 (PID=$DEBUG_PID), dumping." >> "$DUMP_DIR/gcdump.log"
    dump_instance "$DEBUG_PID" "jamfan22-debug"
fi

# Trim old dumps: keep MAX_DUMPS most recent for each label separately
for LABEL in jamfan22 jamfan22-debug; do
    ls -t "$DUMP_DIR"/${LABEL}-*.gcdump 2>/dev/null | tail -n +$((MAX_DUMPS + 1)) | xargs rm -f
done
