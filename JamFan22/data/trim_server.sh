#!/bin/bash

# Set the working directory
cd /root/JamFan22/JamFan22/data/ || { echo "Failed to change directory"; exit 1; }

# Rename the existing server.csv
mv server.csv serverfrozen.csv

# Create a new server.csv file
touch server.csv

# Sort and remove duplicates from the frozen server file, then store it in newserver.csv
cat serverfrozen.csv | sort | uniq > newserver.csv
rm serverfrozen.csv

# Append the new server.csv content to newserver.csv
cat server.csv >> newserver.csv
rm server.csv
mv newserver.csv server.csv
