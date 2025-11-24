import pandas as pd
from datetime import timedelta
import urllib.parse

def find_imminent_hotspots(file_path='data/census.csv', 
                           context_file_path='data/censusgeo.csv', 
                           server_file_path='data/server.csv'):
    """
    Analyzes GUIDs to find predictable patterns (Time AND Location).
    Outputs: predicted.csv (minutes_since_epoch, guid, user_name, server_name)
    """
    # --- Configuration ---
    CONFIDENCE_THRESHOLD = 30
    MINIMUM_APPEARANCES = 4
    MINIMUM_HOTSPOT_SESSIONS = 5
    MINIMUM_HOTSPOT_SAMPLES = 30
    ANALYSIS_WINDOW_DAYS = 89
    EPOCH_START_UTC = pd.to_datetime('2023-01-01', utc=True)

    print(f"--- Next Arrival Prediction (UTC-Only) ---")

    # --- 1. Load Auxiliary Data (Context & Servers) ---
    context_info = {}
    server_map = {}

    # Load User Context (Geo/Name)
    try:
        with open(context_file_path, 'r', encoding='utf-8', errors='ignore') as f:
            for line in f:
                try:
                    guid, rest = line.strip().split(',', 1)
                    context_info[guid] = rest
                except ValueError: continue
        print("Context data loaded.")
    except FileNotFoundError:
        print("Warning: Context data not found.")

    # Load Server Map (Last entry wins for IP:PORT mapping)
    try:
        with open(server_file_path, 'r', encoding='utf-8') as f:
            for line in f:
                parts = line.strip().split(',')
                if len(parts) >= 2:
                    # parts[0] is IP:PORT, parts[1] is Encoded Name
                    # unquote_plus handles both %xx encoding and '+' as space
                    server_map[parts[0]] = urllib.parse.unquote_plus(parts[1])
        print("Server data loaded.")
    except FileNotFoundError:
        print("Warning: Server data not found.")

    # --- 2. Find Latest Timestamp (Reference 'Now') ---
    try:
        print("\nScanning for latest timestamp...")
        max_minutes = 0
        for chunk in pd.read_csv(file_path, names=['mins', 'guid', 'ip'], chunksize=100000, usecols=['mins']):
            chunk['mins'] = pd.to_numeric(chunk['mins'], errors='coerce')
            max_minutes = max(max_minutes, chunk['mins'].max())

        if pd.isna(max_minutes) or max_minutes == 0: return

        now_utc = EPOCH_START_UTC + timedelta(minutes=max_minutes)
        print(f"Now (UTC): {now_utc.strftime('%Y-%m-%d %H:%M:%S')}")

        # --- 3. Load Recent Data ---
        min_minutes = max_minutes - (ANALYSIS_WINDOW_DAYS * 24 * 60)
        print(f"Loading data from last {ANALYSIS_WINDOW_DAYS} days...")
        
        chunks = []
        for chunk in pd.read_csv(file_path, names=['mins', 'guid', 'ip'], chunksize=100000):
            chunk['mins'] = pd.to_numeric(chunk['mins'], errors='coerce')
            chunk.dropna(subset=['mins'], inplace=True)
            relevant = chunk[chunk['mins'] >= min_minutes]
            if not relevant.empty:
                chunks.append(relevant)
        
        if not chunks: return
        df = pd.concat(chunks, ignore_index=True)
        df.drop_duplicates(inplace=True)
        print(f"Analyzing {len(df)} unique entries.")

    except FileNotFoundError:
        print(f"Error: '{file_path}' not found.")
        return

    # --- 4. Precompute Features ---
    df['timestamp_utc'] = EPOCH_START_UTC + pd.to_timedelta(df['mins'], unit='m')
    df['day_of_week_utc'] = df['timestamp_utc'].dt.dayofweek
    df['hour_utc'] = df['timestamp_utc'].dt.hour
    df['date_utc'] = df['timestamp_utc'].dt.date

    # --- 5. Analyze Patterns ---
    print("\nDetecting hotspots...")
    # Count unique sessions per GUID
    df_sessions = df.drop_duplicates(subset=['guid', 'date_utc', 'hour_utc'])
    
    # Filter GUIDs by minimum total appearances
    guid_counts = df_sessions['guid'].value_counts()
    eligible_guids = guid_counts[guid_counts >= MINIMUM_APPEARANCES].index

    # Find best hotspot (Day/Hour) for eligible GUIDs
    hotspots = df_sessions[df_sessions['guid'].isin(eligible_guids)] \
        .groupby(['guid', 'day_of_week_utc', 'hour_utc']).size().reset_index(name='hotspot_sessions')
    
    # Keep only the best slot per GUID
    best_hotspots = hotspots.loc[hotspots.groupby('guid')['hotspot_sessions'].idxmax()]

    # Calculate confidence
    stats = best_hotspots.merge(guid_counts.rename('total_sessions'), left_on='guid', right_index=True)
    
    # Get raw sample count (pings) for that slot to ensure density
    raw_counts = df.groupby(['guid', 'day_of_week_utc', 'hour_utc']).size().reset_index(name='raw_samples')
    stats = stats.merge(raw_counts, on=['guid', 'day_of_week_utc', 'hour_utc'])

    stats['confidence'] = (stats['hotspot_sessions'] / stats['total_sessions']) * 100

    # Apply Thresholds
    predictable = stats[
        (stats['confidence'] >= CONFIDENCE_THRESHOLD) &
        (stats['hotspot_sessions'] >= MINIMUM_HOTSPOT_SESSIONS) &
        (stats['raw_samples'] >= MINIMUM_HOTSPOT_SAMPLES)
    ].copy()

    # --- 6. Strict Recency Filter (Last 2 Weeks) ---
    print("Verifying attendance in last 2 weeks...")
    
    def get_past_date(target_weekday, weeks_ago):
        days_diff = target_weekday - now_utc.weekday()
        target_date = now_utc.date() + timedelta(days=days_diff) - timedelta(weeks=weeks_ago)
        return target_date

    # Check Last Week (suffixes added to fix KeyError)
    predictable['last_week_date'] = predictable['day_of_week_utc'].apply(lambda x: get_past_date(x, 1))
    check_1 = predictable.merge(df_sessions, 
                                left_on=['guid', 'last_week_date', 'hour_utc'], 
                                right_on=['guid', 'date_utc', 'hour_utc'],
                                suffixes=('', '_dup'))

    # Check 2 Weeks Ago
    check_1['two_weeks_ago_date'] = check_1['day_of_week_utc'].apply(lambda x: get_past_date(x, 2))
    final_candidates = check_1.merge(df_sessions, 
                                     left_on=['guid', 'two_weeks_ago_date', 'hour_utc'], 
                                     right_on=['guid', 'date_utc', 'hour_utc'],
                                     suffixes=('', '_dup2'))
    
    print(f"Found {len(final_candidates)} GUIDs with consistent patterns.")

    # --- 7. Generate Predictions & Server Lookups ---
    results = []
    day_map = {0: 'Mon', 1: 'Tue', 2: 'Wed', 3: 'Thu', 4: 'Fri', 5: 'Sat', 6: 'Sun'}

    for _, row in final_candidates.iterrows():
        guid = row['guid']
        h_day = row['day_of_week_utc']
        h_hour = row['hour_utc']

        # --- PREDICT SERVER ---
        mask = (df['guid'] == guid) & (df['day_of_week_utc'] == h_day) & (df['hour_utc'] == h_hour)
        likely_ip_port = df.loc[mask, 'ip'].mode()
        
        server_name = "" # Default to empty for CSV
        
        if not likely_ip_port.empty:
            server_ip = likely_ip_port.iloc[0]
            # Look up name (returns decoded string), default to IP if missing
            server_name = server_map.get(server_ip, server_ip)

        # --- PREDICT TIME ---
        last_arrival = df[
            (df['guid'] == guid) & 
            (df['date_utc'] == row['last_week_date']) & 
            (df['hour_utc'] == h_hour)
        ]['timestamp_utc'].min()
        
        minute, second = (last_arrival.minute, last_arrival.second) if pd.notna(last_arrival) else (0,0)

        days_ahead = (h_day - now_utc.dayofweek + 7) % 7
        next_time = now_utc.replace(hour=h_hour, minute=minute, second=second, microsecond=0) + timedelta(days=days_ahead)
        
        if next_time < now_utc: next_time += timedelta(days=7)
        
        pred_mins = int((next_time - EPOCH_START_UTC).total_seconds() / 60)

        # Heatmap
        user_data = df[df['guid'] == guid]
        heatmap = pd.crosstab(user_data['day_of_week_utc'], user_data['hour_utc'])
        heatmap.index = heatmap.index.map(day_map)

        results.append({
            'guid': guid,
            'pred_mins': pred_mins,
            'timestamp': next_time,
            'context': context_info.get(guid, 'Context unavailable'),
            'server_name': server_name,
            'heatmap': heatmap,
            'confidence': row['confidence'],
            'hotspot_count': row['hotspot_sessions'],
            'total_count': row['total_sessions']
        })

    # --- 8. Output ---
    results_df = pd.DataFrame(results).sort_values('pred_mins')
    
    # Save CSV
    # 1. Encode User Name
    results_df['safe_username'] = results_df['context'].apply(
        lambda x: urllib.parse.quote(x.split(',')[0]) if isinstance(x, str) else ''
    )
    
    # 2. Encode Server Name (New column)
    results_df['safe_servername'] = results_df['server_name'].apply(
        lambda x: urllib.parse.quote(x) if x else ''
    )

    # 3. Write 4 columns
    output_cols = ['pred_mins', 'guid', 'safe_username', 'safe_servername']
    results_df[output_cols].to_csv('predicted.csv', header=False, index=False)
    print(f"Saved predictions to 'predicted.csv' (4 columns).")

    # Console Output
    for _, row in results_df.iterrows():
        print("\n" + "="*60)
        mins_from_now = row['pred_mins'] - max_minutes
        print(f"GUID:       {row['guid']}")
        print(f"Prediction: {row['pred_mins']} (+{mins_from_now}m)")
        print(f"Time:       {row['timestamp'].strftime('%A, %Y-%m-%d at %H:%M:%S UTC')}")
        
        # Display decoded server name in console for readability
        display_server = row['server_name'] if row['server_name'] else "Unknown"
        print(f"Location:   {display_server}") 
        
        print(f"Confidence: {int(row['confidence'])}% ({row['hotspot_count']}/{row['total_count']} sessions)")
        print(f"Context:    {row['context']}")
        print("-" * 20 + " Hourly Activity " + "-" * 20)
        print(row['heatmap'].to_string() if not row['heatmap'].empty else "No Data")

if __name__ == '__main__':
    find_imminent_hotspots()