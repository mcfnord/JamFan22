#!/bin/bash
set -x

# this just loops
while true; do

# this only seems to read one line, so just put one line in serversToSample.txt

while IFS= read -r line
do
docker run -i --rm --init -e JAMULUS_CLIENT_NAME=Ear -v /root/JamFan22/JamFan22/wwwroot/mp3s/:/out ghcr.io/dtinth/jamcaster:main src/capture.sh $line
mv /root/JamFan22/JamFan22/wwwroot/mp3s/output.mp3 /root/JamFan22/JamFan22/wwwroot/mp3s/$line.mp3

# detect silence if it's there.
ffmpeg -i /root/JamFan22/JamFan22/wwwroot/mp3s/$line.mp3 -af silencedetect=noise=0.0001 -f null - > silence_scan_results 2>&1

# look for silence in the results and kill if found, for the ten
grep -q 'silence_duration: 10' silence_scan_results
if [ $? -eq 0 ]
then
rm /root/JamFan22/JamFan22/wwwroot/mp3s/$line.mp3
fi

# look for silence in the results and kill if found, for the nine
grep -q 'silence_duration: 9' silence_scan_results
if [ $? -eq 0 ]
then
rm /root/JamFan22/JamFan22/wwwroot/mp3s/$line.mp3
fi

done < "serversToSample.txt"

sleep 120
done

