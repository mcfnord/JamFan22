#!/bin/bash

echo "Restarting jamfan22 service to launch the new branch..."
systemctl restart jamfan22
echo "Waiting 15 seconds for services to start up and generate data..."
sleep 15

echo ""
echo "--- 1. Checking Systemd Status ---"
if systemctl is-active --quiet jamfan22; then
    echo "✅ jamfan22 service is active and running."
else
    echo "❌ jamfan22 service failed to start."
    systemctl status jamfan22 --no-pager
    exit 1
fi

echo ""
echo "--- 2. Checking Logs for New Services ---"
# We check the tail of the log to ensure we see the recent startup, not old logs if the service restarted before.
if tail -n 100 /root/JamFan22/JamFan22/output.log | grep -q "JamulusListRefreshService is starting"; then
    echo "✅ JamulusListRefreshService started successfully."
else
    echo "❌ JamulusListRefreshService startup log not found."
fi

if tail -n 100 /root/JamFan22/JamFan22/output.log | grep -q "JammerHarvestService is starting"; then
    echo "✅ JammerHarvestService started successfully."
else
    echo "❌ JammerHarvestService startup log not found."
fi

echo ""
echo "--- 3. Checking livestatus.json updates ---"
FILE="/root/JamFan22/JamFan22/wwwroot/livestatus.json"
if [ -f "$FILE" ]; then
    # Get file modification time in seconds since epoch
    MOD_TIME=$(stat -c %Y "$FILE")
    CUR_TIME=$(date +%s)
    DIFF=$((CUR_TIME - MOD_TIME))
    
    if [ "$DIFF" -le 25 ]; then
        echo "✅ livestatus.json was updated $DIFF seconds ago (Background loop is healthy)."
    else
        echo "❌ livestatus.json is stale. Last updated $DIFF seconds ago."
    fi
else
    echo "❌ livestatus.json does not exist yet."
fi
echo ""
