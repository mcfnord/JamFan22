import pandas as pd
from datetime import timedelta
import urllib.parse

def find_imminent_hotspots(file_path='data/census.csv', context_file_path='data/censusgeo.csv'):
    """
    Analyzes all GUIDs in a file to find those with predictable patterns
    and outputs their next likely arrival time.
    """
    # --- Configuration ---
    # Setting the standard based on Marsha K's statistics (5 hotspot sessions out of 14 total).
    # This equates to a ~35.7% confidence score.
    CONFIDENCE_THRESHOLD = 35
    MINIMUM_APPEARANCES = 4
    MINIMUM_HOTSPOT_SESSIONS = 5 # Only show GUIDs that have appeared in their hotspot at least this many times.
    MINIMUM_HOTSPOT_SAMPLES = 30 # A GUID's hotspot must have at least this many raw samples.

    # How many days of recent data to consider for the analysis.
    ANALYSIS_WINDOW_DAYS = 89

    # The start date for the timestamps in the data.
    EPOCH_START_UTC = pd.to_datetime('2023-01-01', utc=True)
    LOCAL_TIMEZONE = 'America/Los_Angeles'

    # --- 1. Load Context Data (Memory Efficiently) ---
    context_data_loaded = False
    last_context_info = {}
    try:
        print(f"--- Next Arrival Prediction ---")
        print(f"Loading context data from '{context_file_path}'...")
        with open(context_file_path, 'r', encoding='utf-8', errors='ignore') as f:
            for line in f:
                try:
                    guid, rest_of_line = line.strip().split(',', 1)
                    if guid:
                        last_context_info[guid] = rest_of_line
                except ValueError:
                    continue
        context_data_loaded = True
        print("Context data loaded successfully.")
    except FileNotFoundError:
        print(f"Warning: Context data file not found at '{context_file_path}'. Predictions will not include context.")

    # --- 2. Find Latest Timestamp and Load Recent Data (Two-Pass Approach) ---
    try:
        print("\nFirst pass: Finding the latest timestamp in the data to define 'now'...")
        max_minutes_in_data = 0

        chunk_iterator_for_max = pd.read_csv(
            file_path, header=None, names=['minutes_since_epoch', 'guid', 'ip_address'],
            chunksize=100000, usecols=['minutes_since_epoch']
        )
        for chunk in chunk_iterator_for_max:
            chunk['minutes_since_epoch'] = pd.to_numeric(chunk['minutes_since_epoch'], errors='coerce')
            chunk.dropna(inplace=True)
            if not chunk.empty:
                max_minutes_in_data = max(max_minutes_in_data, chunk['minutes_since_epoch'].max())

        if max_minutes_in_data == 0:
            print("No valid timestamps found. Exiting.")
            return

        now_utc = EPOCH_START_UTC + timedelta(minutes=max_minutes_in_data)
        now_local = now_utc.tz_convert(LOCAL_TIMEZONE)

        print(f"Latest timestamp in data corresponds to: {now_local.strftime('%Y-%m-%d %H:%M:%S %Z')}")

        min_minutes_since_epoch = max_minutes_in_data - (ANALYSIS_WINDOW_DAYS * 24 * 60)

        print(f"\nSecond pass: Loading relevant data from the last {ANALYSIS_WINDOW_DAYS} days...")
        chunk_iterator = pd.read_csv(
            file_path, header=None, names=['minutes_since_epoch', 'guid', 'ip_address'],
            chunksize=100000
        )
        recent_chunks = []
        for chunk in chunk_iterator:
            chunk['minutes_since_epoch'] = pd.to_numeric(chunk['minutes_since_epoch'], errors='coerce')
            chunk.dropna(subset=['minutes_since_epoch'], inplace=True)
            recent_chunk = chunk[chunk['minutes_since_epoch'] >= min_minutes_since_epoch]
            if not recent_chunk.empty:
                recent_chunks.append(recent_chunk)

        if not recent_chunks:
            print("No recent data found. Exiting.")
            return

        df = pd.concat(recent_chunks, ignore_index=True)

        print("Dropping duplicate log entries...")
        initial_rows = len(df)
        df.drop_duplicates(inplace=True)
        final_rows = len(df)
        print(f"Dropped {initial_rows - final_rows} duplicate entries.")
        print(f"Kept {final_rows} unique, recent entries for analysis.")

    except FileNotFoundError:
        print(f"Error: The file '{file_path}' was not found.")
        return

    # --- 3. Create Timezone-Aware Features ---
    df['timestamp_utc'] = EPOCH_START_UTC + pd.to_timedelta(df['minutes_since_epoch'], unit='m')
    df['timestamp_pdt'] = df['timestamp_utc'].dt.tz_convert(LOCAL_TIMEZONE)
    df['day_of_week_pdt'] = df['timestamp_pdt'].dt.dayofweek
    df['hour_pdt'] = df['timestamp_pdt'].dt.hour
    df['date_pdt'] = df['timestamp_pdt'].dt.date

    # --- 4. Analyze Patterns Based on Unique Sessions ---
    print("\nAnalyzing all GUID patterns based on unique sessions...")
    df_sessions = df.drop_duplicates(subset=['guid', 'date_pdt', 'hour_pdt']).copy()
    total_counts = df_sessions['guid'].value_counts().reset_index()
    total_counts.columns = ['guid', 'total_sessions']
    eligible_guids = total_counts[total_counts['total_sessions'] >= MINIMUM_APPEARANCES]
    hotspot_counts = df_sessions.groupby(['guid', 'day_of_week_pdt', 'hour_pdt']).size().reset_index(name='hotspot_sessions')
    top_hotspots = hotspot_counts.loc[hotspot_counts.groupby('guid')['hotspot_sessions'].idxmax()]
    raw_sample_counts = df.groupby(['guid', 'day_of_week_pdt', 'hour_pdt']).size().reset_index(name='raw_hotspot_samples')
    analysis_df = pd.merge(top_hotspots, eligible_guids, on='guid')
    analysis_df = pd.merge(analysis_df, raw_sample_counts, on=['guid', 'day_of_week_pdt', 'hour_pdt'])
    analysis_df['confidence'] = (analysis_df['hotspot_sessions'] / analysis_df['total_sessions']) * 100
    predictable_guids = analysis_df[
        (analysis_df['confidence'] >= CONFIDENCE_THRESHOLD) &
        (analysis_df['hotspot_sessions'] >= MINIMUM_HOTSPOT_SESSIONS) &
        (analysis_df['raw_hotspot_samples'] >= MINIMUM_HOTSPOT_SAMPLES)
    ].copy()
    if context_data_loaded:
        predictable_guids['context_info'] = predictable_guids['guid'].map(last_context_info)
        predictable_guids['context_info'].fillna('Context info not found', inplace=True)
    else:
        predictable_guids['context_info'] = 'Context info unavailable'


    # --- 5. Filter for Attendance During the Last TWO Weeks' Hotspot Windows ---
    print("\nFiltering for GUIDs that attended their specific hotspot hour in BOTH of the last two weeks...")

    # --- First Filter: Check for attendance LAST week ---
    predictable_guids['last_week_hotspot_date'] = predictable_guids['day_of_week_pdt'].apply(
        lambda hotspot_day: (now_local.date() - timedelta(days=now_local.weekday()) +
                             timedelta(days=hotspot_day) - timedelta(days=7))
    )
    
    original_predictable_count = len(predictable_guids)

    attended_last_week = pd.merge(
        predictable_guids,
        df_sessions[['guid', 'date_pdt', 'hour_pdt']],
        left_on=['guid', 'last_week_hotspot_date', 'hour_pdt'],
        right_on=['guid', 'date_pdt', 'hour_pdt'],
        how='inner'
    )
    
    count_after_first_filter = len(attended_last_week)
    print(f"Filtered out {original_predictable_count - count_after_first_filter} GUIDs that missed last week's hotspot.")

    # --- Second Filter: From the remaining, check for attendance the week BEFORE last ---
    attended_last_week['two_weeks_ago_hotspot_date'] = attended_last_week['day_of_week_pdt'].apply(
        lambda hotspot_day: (now_local.date() - timedelta(days=now_local.weekday()) +
                             timedelta(days=hotspot_day) - timedelta(days=14)) # Use 14 days for the week before
    )
    
    attended_both_weeks = pd.merge(
        attended_last_week,
        df_sessions[['guid', 'date_pdt', 'hour_pdt']],
        left_on=['guid', 'two_weeks_ago_hotspot_date', 'hour_pdt'],
        right_on=['guid', 'date_pdt', 'hour_pdt'],
        how='inner'
    )

    # Clean up the final DataFrame
    predictable_guids = attended_both_weeks.copy()
    # CORRECTED LINE BELOW
    predictable_guids.drop(columns=['date_pdt_x', 'date_pdt_y', 'last_week_hotspot_date', 'two_weeks_ago_hotspot_date'], inplace=True)
    
    final_predictable_count = len(predictable_guids)
    print(f"Filtered out another {count_after_first_filter - final_predictable_count} GUIDs that missed the hotspot two weeks ago.")
    print(f"Keeping {final_predictable_count} GUIDs that attended in both recent weeks for prediction.")

    # --- 6. Generate Heatmaps ---
    heatmaps = {}
    day_map_pdt = {0: 'Mon', 1: 'Tue', 2: 'Wed', 3: 'Thu', 4: 'Fri', 5: 'Sat', 6: 'Sun'}
    if not predictable_guids.empty:
        df_for_heatmaps = df[df['guid'].isin(predictable_guids['guid'])]
        for guid, group in df_for_heatmaps.groupby('guid'):
            heatmap = pd.crosstab(group['day_of_week_pdt'], group['hour_pdt'])
            heatmap = heatmap.reindex(index=range(7), columns=range(24), fill_value=0)
            heatmap.index = heatmap.index.map(day_map_pdt)
            heatmap = heatmap.loc[:, (heatmap != 0).any(axis=0)]
            heatmaps[guid] = heatmap

    # --- 7. Predict and Report Results ---
    predictions = []
    for _, guid_info in predictable_guids.iterrows():
        hotspot_day = int(guid_info['day_of_week_pdt'])
        hotspot_hour = int(guid_info['hour_pdt'])
        guid_for_debug = guid_info['guid'] # Get GUID for logging

        # --- START DEBUG BLOCK ---
        # Let's check the variables for Myanna
        if guid_for_debug == 'e10fd3c145f5f90098af52973d6a85cc': 
            print("\n--- DEBUGGING Myanna (e10fd3...) ---")
            print(f"now_local: {now_local} (Day: {now_local.dayofweek})")
            print(f"hotspot_day: {hotspot_day}")
            print(f"hotspot_hour: {hotspot_hour}")
        # --- END DEBUG BLOCK ---

        days_ahead = (hotspot_day - now_local.dayofweek + 7) % 7
        
        next_date_base = now_local.replace(
            hour=hotspot_hour, minute=0, second=0, microsecond=0
        )
        
        next_date_local = next_date_base + timedelta(days=days_ahead)

        if next_date_local < now_local:
            next_date_local += timedelta(days=7)

        predicted_minutes = int((next_date_local.tz_convert('UTC') - EPOCH_START_UTC).total_seconds() / 60)

        # --- START DEBUG BLOCK 2 ---
        if guid_for_debug == 'e10fd3c145f5f90098af52973d6a85cc':
            print(f"days_ahead: {days_ahead}")
            print(f"next_date_base (now_local.replace): {next_date_base}")
            print(f"next_date_local (after logic): {next_date_local}")
            print(f"FINAL predicted_minutes variable: {predicted_minutes}")
            print("--- END DEBUGGING ---\n")
        # --- END DEBUG BLOCK 2 ---

        # --- MODIFIED: This block is now complete ---
        predictions.append({
            'guid': guid_for_debug,
            'predicted_minutes_since_epoch': predicted_minutes,
            'predicted_timestamp_local': next_date_local,
            'context_info': guid_info['context_info'],
            'hotspot_count': guid_info['hotspot_sessions'],
            'total_count': guid_info['total_sessions'],
            'heatmap': heatmaps.get(guid_info['guid'])
        })
        # --- End of modification ---

    print("\n--- Predicted Next Appearances (Attended Hotspot Last Week) ---")
    if not predictions:
        print("No GUIDs met the criteria for prediction.")
        return

    results_df = pd.DataFrame(predictions).sort_values('predicted_minutes_since_epoch')

    # --- 8. Generate and Save CSV Output ---
    def get_urlencoded_name(context_info):
        if not isinstance(context_info, str) or 'not found' in context_info or 'unavailable' in context_info:
            return ''
        try:
            # Assumes name is the first part of a comma-separated string
            name = context_info.split(',')[0].strip()
            return urllib.parse.quote(name)
        except:
            return ''

    results_df['urlencoded_name'] = results_df['context_info'].apply(get_urlencoded_name)
    output_df = results_df[['predicted_minutes_since_epoch', 'guid', 'urlencoded_name']]
    output_filename = 'predicted.csv'
    output_df.to_csv(output_filename, header=False, index=False)
    print(f"\n--- Predictions also saved to '{output_filename}' ---")


    # --- 9. Print Detailed Console Output ---
    for _, row in results_df.iterrows():
        print("\n" + "="*70)
        print(f"GUID: {row['guid']}")
        time_comment = f"for {row['predicted_timestamp_local'].strftime('%A around %m/%d/%Y at %I:00 %p %Z')}"
        confidence_comment = f"{int(row['hotspot_count'])} hotspot sessions out of {int(row['total_count'])} total."
        print(f"Prediction: {row['predicted_minutes_since_epoch']} # {time_comment}")
        print(f"Confidence: {confidence_comment}")
        print(f"Context: {row['context_info']}")
        print("--- Appearance Heatmap (Raw counts per hour in PDT) ---")
        if row['heatmap'] is not None and not row['heatmap'].empty:
            print(row['heatmap'].to_string())
        else:
            print("No heatmap data available.")
        print("="*70)

if __name__ == '__main__':
    DATA_FILE = 'data/census.csv'
    CONTEXT_DATA_FILE = 'data/censusgeo.csv'
    find_imminent_hotspots(file_path=DATA_FILE, context_file_path=CONTEXT_DATA_FILE)
