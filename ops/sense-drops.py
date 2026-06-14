import pandas as pd
import os
from datetime import datetime, timedelta, timezone

BASE_UTC = datetime(2023, 1, 1, 0, 0, 0, tzinfo=timezone.utc)

def to_utc(minute_int):
    return (BASE_UTC + timedelta(minutes=int(minute_int))).strftime("%Y-%m-%d %H:%M UTC")

# Your codebase writes to data/census.csv
file_path = 'data/census.csv'
if not os.path.exists(file_path):
    file_path = 'census.csv' # Fallback to root

try:
    # Column 0 is the MinuteSince2023 integer based on JamulusCacheManager.cs
    df = pd.read_csv(file_path, header=None, usecols=[0], names=['Minute'])
    
    # Get unique minutes and sort them chronologically
    unique_minutes = df['Minute'].dropna().astype(int).unique()
    unique_minutes = sorted(unique_minutes)
    
    if len(unique_minutes) < 2:
        print("Not enough data to find dropouts.")
    else:
        gaps = []
        total_expected = unique_minutes[-1] - unique_minutes[0] + 1
        actual_count = len(unique_minutes)
        missing_count = total_expected - actual_count
        
        # Scan for missing minute gaps
        for i in range(1, len(unique_minutes)):
            diff = unique_minutes[i] - unique_minutes[i-1]
            if diff > 1:
                gaps.append((unique_minutes[i-1], unique_minutes[i], diff - 1))
        
        print(f"--- Dropout Analysis of {file_path} ---")
        print(f"First minute recorded: {unique_minutes[0]}  ({to_utc(unique_minutes[0])})")
        print(f"Last minute recorded:  {unique_minutes[-1]}  ({to_utc(unique_minutes[-1])})")
        print(f"Total minutes span:    {total_expected}")
        print(f"Actual minutes w/ data:{actual_count}")
        print(f"Total missing minutes: {missing_count} (Dropouts)")
        print(f"Dropout rate:          {(missing_count/total_expected)*100:.2f}%\n")

        if gaps:
            print(f"Found {len(gaps)} gap sequence(s). Top 15 largest gaps:")
            # Sort gaps by size descending
            gaps.sort(key=lambda x: x[2], reverse=True)
            for start, end, size in gaps[:15]:
                print(f"  - Gap of {size} minute(s): {to_utc(start)} → {to_utc(end)}")
        else:
            print("No dropouts found! The sequence is perfectly continuous.")
            
except Exception as e:
    print(f"Error processing the file: {e}")
