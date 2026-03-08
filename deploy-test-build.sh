#!/bin/bash

# Configuration
TEST_PORT=5000
TARGET_DIR="/tmp/jamfan-test-build"
SOURCE_DIR="/root/JamFan22/JamFan22"

echo "🚀 Starting experimental test build deployment..."

# 1. Clean and Build
echo "📦 Publishing application to $TARGET_DIR..."
rm -rf "$TARGET_DIR"

# dotnet publish copies the binaries and static files (like css/js from wwwroot)
dotnet publish "$SOURCE_DIR/JamFan22.csproj" -c Release -o "$TARGET_DIR"

if [ $? -ne 0 ]; then
    echo "❌ Build failed. Aborting."
    exit 1
fi

# 2. Replicate Production Data
echo "📂 Replicating production data and state files..."

# Ensure necessary directories exist in the target
mkdir -p "$TARGET_DIR/data"
mkdir -p "$TARGET_DIR/wwwroot"

# A single, explicit list of all essential data, state, and key files needed
# across the root, data, and wwwroot directories.
FILES_TO_COPY=(
    # --- Root Application Directory ---
    "timeTogether.json"
    "timeTogetherLastUpdates.json"
    "guidNamePairs.json"
    "tooltips.json"
    "allSvrIpPorts.txt"
    "activeSvrIpPorts.txt"
    "serversToSample.txt"
    "non-signals.txt"
    "join-events.csv"
    "erased.txt"
    "no-ping.txt"
    "arn-servers-blocked.txt"
    "lobby.txt"
    "last-snippet.txt"
    "secretGeoApifykey.txt"
    "keyJan26.pfx"

    # --- Data Directory ---
    "data/urls.csv"
    "data/server.csv"
    "data/census.csv"
    "data/censusgeo.csv"

    # --- Wwwroot State/Text Files ---
    "wwwroot/paircount.csv"
    "wwwroot/livestatus.json"
    "wwwroot/jammer-map.json"
    "wwwroot/asn-blocks.txt"
    "wwwroot/asn-ip-client-blocks.txt"
    "wwwroot/halo-snippeting.txt"
    "wwwroot/halo-streaming.txt"
    "wwwroot/can-dock.txt"
    "wwwroot/cannot-dock.txt"
)

# Loop through the array and copy each file if it exists
for file in "${FILES_TO_COPY[@]}"; do
    if [ -f "$SOURCE_DIR/$file" ]; then
        cp "$SOURCE_DIR/$file" "$TARGET_DIR/$file"
        echo "  ✅ Copied: $file"
    else
        # Not all files might exist immediately (like empty logs), so we warn but don't fail
        echo "  ⚠️  Missing: $file (Skipped)"
    fi
done

# 3. Launch the test instance
echo ""
echo "🌐 Launching test build on port $TEST_PORT..."
echo "💡 The app is sandboxed. It will read and write ONLY to $TARGET_DIR"
echo "🛑 Press Ctrl+C to stop the test server."
echo ""

cd "$TARGET_DIR"

# Use the PORT environment variable we added to Program.cs
ASPNETCORE_ENVIRONMENT=Development PORT=$TEST_PORT dotnet JamFan22.dll
