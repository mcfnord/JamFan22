#!/bin/bash
set -x

while true; do

while IFS= read -r line
do
docker run -i --rm --init -e JAMULUS_CLIENT_NAME=Ear -v /root/JamFan22/JamFan22/wwwroot/mp3s/:/out ghcr.io/dtinth/jamcaster:main src/capture.sh $line
mv /root/JamFan22/JamFan22/wwwroot/mp3s/output.mp3 /root/JamFan22/JamFan22/wwwroot/mp3s/$line.mp3
done < "serversToSample.txt"

sleep 300
done

