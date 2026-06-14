#!/bin/bash
# Auto-snapshot prompt files into git. Run via cron or manually.
set -e
cd /root/JamFan22

CHANGED=$(git diff --name-only JamFan22/data/essay-system-prompt.txt JamFan22/data/welcome-system-prompt.txt JamFan22/data/welcome-config.txt 2>/dev/null)
NEW=$(git ls-files --others --exclude-standard JamFan22/data/essay-system-prompt.txt JamFan22/data/welcome-system-prompt.txt JamFan22/data/welcome-config.txt 2>/dev/null)

if [ -z "$CHANGED" ] && [ -z "$NEW" ]; then
    echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) snapshot-prompts: no changes"
    exit 0
fi

git add JamFan22/data/essay-system-prompt.txt \
        JamFan22/data/welcome-system-prompt.txt \
        JamFan22/data/welcome-config.txt

git commit -m "Auto-snapshot prompts $(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) snapshot-prompts: committed"
