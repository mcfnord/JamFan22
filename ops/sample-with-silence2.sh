#!/bin/bash
set -x

while true; do
  current_hour=$(date +"%H")

  if [ "$current_hour" -ge 2 ] && [ "$current_hour" -lt 12 ]; then
    echo "Script paused."
    sleep 3600
    continue
  fi

  while IFS= read -r line
  do
    docker run -i --rm --init -e JAMULUS_CLIENT_NAME=Ear -v /root/JamFan22/JamFan22/wwwroot/mp3s/:/out ghcr.io/dtinth/jamcaster:main src/capture.sh $line
    mv /root/JamFan22/JamFan22/wwwroot/mp3s/output.mp3 /root/JamFan22/JamFan22/wwwroot/mp3s/$line.mp3

    file_path="/root/JamFan22/JamFan22/wwwroot/mp3s/$line.mp3"
    silence_log="/root/JamFan22/JamFan22/silence_scan_results"

    # Get MP3 metadata
    ffmpeg -i "$file_path" 2>&1 | tee -a /root/JamFan22/JamFan22/mp3_metadata.log
    mediainfo "$file_path" >> /root/JamFan22/JamFan22/mp3_metadata.log 2>/dev/null
    stat "$file_path" >> /root/JamFan22/JamFan22/mp3_metadata.log
    ls -lh "$file_path" >> /root/JamFan22/JamFan22/mp3_metadata.log

    # Detect silence
    ffmpeg -i "$file_path" -af silencedetect=noise=0.0001 -f null - > "$silence_log" 2>&1
    echo "==========================================" >> "$silence_log"

    # If silence is detected, log details before deletion
    if grep -q 'silence_duration: 10' "$silence_log" || grep -q 'silence_duration: 9' "$silence_log"; then
        echo "Silence detected in $file_path. Logging details before deletion..." >> /root/JamFan22/JamFan22/mp3_metadata.log
        cat "$silence_log" >> /root/JamFan22/JamFan22/mp3_metadata.log
        rm "$file_path"
#        touch "/root/JamFan22/JamFan22/wwwroot/mp3s/$line.sil"
    fi

    # Append last few lines of metadata
    tail -n 3 /root/JamFan22/JamFan22/mp3_metadata.log > /root/JamFan22/JamFan22/last-snippet.txt

  done < "serversToSample.txt"

  sleep 520
done
