#!/bin/bash
# release.sh — publish JamFan22 into a pinned release directory and flip `current`.
#
#   /root/JamFan22/releases/<utcstamp>-<sha>[-dirty-<diffhash>]/   the published bytes
#   /root/JamFan22/releases/<same>.build.log                       publish output + peak RSS of the build
#   /root/JamFan22/releases/current -> <dir>                       what jamfan22.service runs
#
# Usage: release.sh [Release|Debug]        (default Release)
# Does NOT restart anything. Restart is the operator's call:  systemctl restart jamfan22
# The unit's ExecStartPre cats <dir>/RELEASE into output.log on every start, so the log
# says which bytes came up.
set -euo pipefail
REPO=/root/JamFan22
PROJ=$REPO/JamFan22
REL=$REPO/releases
CONF=${1:-Release}
DOTNET=/root/.dotnet/dotnet

cd "$REPO"
SHA=$(git rev-parse --short HEAD)
if git diff --quiet HEAD; then
    TAG=$SHA
    DIRTY=clean
else
    DIRTY=$(git diff HEAD | sha256sum | cut -c1-8)
    TAG=$SHA-dirty-$DIRTY
fi
STAMP=$(date -u +%Y%m%dT%H%M%SZ)
DIR=$REL/$STAMP-$TAG
mkdir -p "$REL"

echo "publishing $CONF -> $DIR"
/usr/bin/time -v "$DOTNET" publish "$PROJ/JamFan22.csproj" -c "$CONF" -o "$DIR" > "$DIR.build.log" 2>&1 \
    || { echo "PUBLISH FAILED — see $DIR.build.log"; tail -20 "$DIR.build.log"; exit 1; }
[ -f "$DIR/JamFan22.dll" ] || { echo "no JamFan22.dll in $DIR"; exit 1; }

{
    echo "sha=$SHA"
    echo "dirty=$DIRTY"
    echo "config=$CONF"
    echo "built=$STAMP"
    echo "dotnet=$($DOTNET --version)"
    echo "dll_sha256=$(sha256sum "$DIR/JamFan22.dll" | cut -c1-16)"
    [ "$DIRTY" = clean ] || git diff --stat HEAD | tail -1 | sed 's/^/modified: /'
} > "$DIR/RELEASE"

# atomic flip
ln -sfn "$DIR" "$REL/current.tmp"
mv -T "$REL/current.tmp" "$REL/current"

echo "current -> $(readlink "$REL/current")"
cat "$DIR/RELEASE"
grep -E "Maximum resident set size|Elapsed \(wall clock\)" "$DIR.build.log" | sed 's/^\s*/build: /'
