#!/bin/bash

# Set the working directory
cd /root/JamFan22/JamFan22/data/ || { echo "Failed to change directory"; exit 1; }

# Rename the existing census.csv
mv censusgeo.csv censusgeofrozen.csv

# Create a new census.csv file
touch censusgeo.csv

# Sort and remove duplicates from the frozen census file, then store it in newcensus.csv
cat censusgeofrozen.csv | sort | uniq > newcensusgeo.csv
rm censusgeofrozen.csv

# Append the new census.csv content to newcensus.csv
cat censusgeo.csv >> newcensusgeo.csv
rm censusgeo.csv
mv newcensusgeo.csv censusgeo.csv
