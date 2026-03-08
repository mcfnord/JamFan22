#!/bin/bash

# JamFan22 CLI Tool
# Usage: 
#   ./jamfan-cli.sh              - List top 10 closest active servers
#   ./jamfan-cli.sh search <name> - Search for a musician by name
#   ./jamfan-cli.sh all          - List all active servers sorted by distance

API_URL="http://localhost:5000/api"

if ! command -v jq &> /dev/null; then
    echo "Error: 'jq' is not installed. Please install it to use this CLI."
    exit 1
fi

format_server() {
    jq -r '
    def format_client:
        "    - \(.name) (\(.instrument))" +
        (if .city != "" and .city != null then " [\(.city)]" else "" end) +
        (if .durationStatus != "" and .durationStatus != null then " *" + .durationStatus + "*" else "" end) +
        (if .duration != "" and .duration != null then " (" + .duration + ")" else "" end);

    def format_leavers:
        if (.leavers | length) > 0 then
            "    Bye: " + (.leavers | join(" • "))
        else
            empty
        end;

    def format_soon:
        if (.soonNames | length) > 0 then
            "    Soon: " + (.soonNames | join(" • "))
        else
            empty
        end;

    def format_title:
        if .songTitle != null and .songTitle != "" then
            "    🎶 " + .songTitle
        else
            empty
        end;

    .[] | 
    "[\(.distanceAway)km] \(.name) (\(.usercount)/\(.maxusercount)) - \(.city), \(.country)",
    format_title,
    (if .clients then (.clients[] | format_client) else empty end),
    format_leavers,
    format_soon,
    ""
    '
}

case "$1" in
    search)
        if [ -z "$2" ]; then
            echo "Usage: $0 search <name>"
            exit 1
        fi
        SEARCH_TERM="$2"
        echo "Searching for '$SEARCH_TERM'..."
        curl -s "$API_URL" | jq "map(select(.clients != null and any(.clients[]; .name | ascii_downcase | contains(\"$SEARCH_TERM\" | ascii_downcase))))" | format_server
        ;;
    all)
        curl -s "$API_URL" | format_server
        ;;
    *)
        echo "Top 10 Closest Active Servers:"
        echo "-----------------------------"
        curl -s "$API_URL" | jq ".[0:10]" | format_server
        echo "-----------------------------"
        echo "Use '$0 search <name>' to find a musician."
        ;;
esac
