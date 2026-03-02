#!/bin/bash

# Set the working directory
cd /root/JamFan22/JamFan22/data/ || { echo "Failed to change directory"; exit 1; }

# Rename the existing census.csv
mv census.csv censusfrozen.csv

# Create a new census.csv file
touch census.csv

# Sort and remove duplicates from the frozen census file, then store it in newcensus.csv
cat censusfrozen.csv | sort | uniq > newcensus.csv
rm censusfrozen.csv

python3 three-month-trim.py < newcensus.csv > newcensustrimmed.csv
rm newcensus.csv

# Append the new census.csv content to newcensus.csv
cat census.csv >> newcensustrimmed.csv
rm census.csv                         # the mv -f will overwrite this, but i remote it here also
mv -f newcensustrimmed.csv census.csv # overwrite with no confirmation prompt

# echo "Time,Server,Person" > /root/JamFan22/JamFan22/wwwroot/minute-server-person.csv
# tail -n 6000000 /root/JamFan22/JamFan22/data/census.csv | awk -F',' '{print $1","$3","$2}' | sort | uniq >> /root/JamFan22/JamFan22/wwwroot/minute-server-person.csv

# echo "Person,Name,Instrument,City,Nation" > /root/JamFan22/JamFan22/wwwroot/censusgeo.csv
# cat censusgeo.csv | sort | uniq >> /root/JamFan22/JamFan22/wwwroot/censusgeo.csv

# echo "Server,Name,City,Nation" > /root/JamFan22/JamFan22/wwwroot/server.csv
# cat server.csv | sort | uniq >> /root/JamFan22/JamFan22/wwwroot/server.csv
