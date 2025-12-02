import pandas as pd
from datetime import timedelta
import urllib.parse
import json
import os
from collections import Counter

# --- Helper for "Fake" Italics in Plain Text ---
def to_italic(text):
    result = ""
    for char in text:
        code = ord(char)
        if 65 <= code <= 90: # A-Z
            result += chr(0x1D608 + (code - 65))
        elif 97 <= code <= 122: # a-z
            result += chr(0x1D622 + (code - 97))
        else:
            result += char
    return result

def generate_tooltips(df, server_map, context_info, target_guids, max_minutes, output_path='tooltips.json'):
    print("\n" + "="*60)
    print("Generating Tooltips (Background Analysis)...")
    
    # Configuration for Tooltip Logic
    DOMINANCE_THRESHOLD = 0.80  # If #1 is 80%, hide #2
    NOISE_THRESHOLD = 0.10      # If #2 is < 10%, hide #2

    # 1. Filter Data (Last 60 Days)
    recent_mins = max_minutes - (60 * 24 * 60)
    recent_df = df[df['mins'] >= recent_mins].copy()
    
    if recent_df.empty: return

    # 2. Create 'Sessions'
    recent_df['time_bucket'] = (recent_df['mins'] / 10).round().astype(int)
    recent_df['session_key'] = recent_df['time_bucket'].astype(str) + "_" + recent_df['ip']

    # 3. Pre-calculate Lookups
    print(f"Indexing social graph for {len(target_guids)} active/predicted users...")
    
    def get_name(g):
        val = context_info.get(g, '')
        return val.split(',')[0] if val else 'Unknown'

    history_by_guid = recent_df.groupby('guid')
    room_populations = recent_df.groupby('session_key')['guid'].apply(list).to_dict()

    tooltip_data = {}
    count = 0
    total = len(target_guids)

    for guid in target_guids:
        count += 1
        if count % 100 == 0: print(f"Processed {count}/{total}...")

        if guid not in history_by_guid.groups: continue

        my_history = history_by_guid.get_group(guid)

        # --- PART A: SMART SERVER LIST ---
        server_counts = my_history['ip'].value_counts()
        total_sessions = server_counts.sum()
        
        fav_servers = []
        if not server_counts.empty:
            # Always get #1
            ip1 = server_counts.index[0]
            count1 = server_counts.iloc[0]
            
            s_name1 = server_map.get(ip1, ip1)
            if len(s_name1) > 25: s_name1 = s_name1[:22] + ".."
            fav_servers.append(s_name1)

            # Check logic for #2
            if len(server_counts) > 1:
                ip2 = server_counts.index[1]
                count2 = server_counts.iloc[1]
                
                ratio1 = count1 / total_sessions
                ratio2 = count2 / total_sessions
                
                # Only add #2 if #1 isn't dominating AND #2 isn't noise
                if ratio1 < DOMINANCE_THRESHOLD and ratio2 >= NOISE_THRESHOLD:
                    s_name2 = server_map.get(ip2, ip2)
                    if len(s_name2) > 25: s_name2 = s_name2[:22] + ".."
                    fav_servers.append(s_name2)

        # --- PART B: SMART COMPANION LIST ---
        my_keys = my_history['session_key'].unique()
        all_peers = []
        for k in my_keys:
            if k in room_populations:
                all_peers.extend(room_populations[k])
        
        peer_counts = Counter(all_peers)
        if guid in peer_counts: del peer_counts[guid]
        
        total_peers = sum(peer_counts.values())
        
        # Look at top 10 candidates to allow for filtering out bots/[brackets]
        raw_candidates = peer_counts.most_common(10)
        
        clean_candidates = []
        for f_guid, freq in raw_candidates:
            name = get_name(f_guid)
            
            # --- FILTER ---
            # Exclude anyone with an open bracket '[' in their name
            if '[' in name:
                continue
            # --------------
            
            clean_candidates.append((f_guid, freq, name))
            if len(clean_candidates) >= 2:
                break # We found our top 2 valid humans
        
        top_friends = []
        if clean_candidates and total_peers > 0:
            # Process #1
            f_guid1, count1, name1 = clean_candidates[0]
            
            if count1 > 2: # Min 3 meetings
                top_friends.append(to_italic(name1))
                
                # Process #2
                if len(clean_candidates) > 1:
                    f_guid2, count2, name2 = clean_candidates[1]
                    
                    ratio1 = count1 / total_peers
                    ratio2 = count2 / total_peers
                    
                    # Same logic: dominance and noise
                    if count2 > 2 and ratio1 < DOMINANCE_THRESHOLD and ratio2 >= NOISE_THRESHOLD:
                        top_friends.append(to_italic(name2))

        # --- PART C: BUILD STRING ---
        lines = []
        lines.extend(fav_servers)
        lines.extend(top_friends)

        if lines:
            tooltip_data[guid] = "\n".join(lines)

    with open(output_path, 'w', encoding='utf-8') as f:
        json.dump(tooltip_data, f)
    
    print(f"Saved tooltips for {len(tooltip_data)} users to {output_path}")


def find_imminent_hotspots(file_path='data/census.csv', 
                           context_file_path='data/censusgeo.csv', 
                           server_file_path='data/server.csv'):
    # --- Configuration ---
    CONFIDENCE_THRESHOLD = 30
    MINIMUM_APPEARANCES = 4
    MINIMUM_HOTSPOT_SESSIONS = 5
    MINIMUM_HOTSPOT_SAMPLES = 30
    ANALYSIS_WINDOW_DAYS = 89
    EPOCH_START_UTC = pd.to_datetime('2023-01-01', utc=True)

    print(f"--- Next Arrival Prediction (UTC-Only) ---")

    # --- 1. Load Auxiliary Data ---
    context_info = {}
    server_map = {}

    try:
        with open(context_file_path, 'r', encoding='utf-8', errors='ignore') as f:
            for line in f:
                try:
                    guid, rest = line.strip().split(',', 1)
                    decoded_rest = urllib.parse.unquote_plus(rest)
                    context_info[guid] = decoded_rest
                except ValueError: continue
    except FileNotFoundError: pass

    try:
        with open(server_file_path, 'r', encoding='utf-8') as f:
            for line in f:
                parts = line.strip().split(',')
                if len(parts) >= 2:
                    server_map[parts[0]] = urllib.parse.unquote_plus(parts[1])
    except FileNotFoundError: pass

    # --- 2. Find Latest Timestamp ---
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
        df_raw = df.copy() 
        df.drop_duplicates(inplace=True)
        print(f"Analyzing {len(df)} unique entries.")

    except FileNotFoundError: return

    # --- 4. Precompute Features ---
    df['timestamp_utc'] = EPOCH_START_UTC + pd.to_timedelta(df['mins'], unit='m')
    df['day_of_week_utc'] = df['timestamp_utc'].dt.dayofweek
    df['hour_utc'] = df['timestamp_utc'].dt.hour
    df['date_utc'] = df['timestamp_utc'].dt.date

    # --- 5. Analyze Patterns ---
    print("\nDetecting hotspots...")
    df_sessions = df.drop_duplicates(subset=['guid', 'date_utc', 'hour_utc'])
    
    guid_counts = df_sessions['guid'].value_counts()
    eligible_guids = guid_counts[guid_counts >= MINIMUM_APPEARANCES].index

    hotspots = df_sessions[df_sessions['guid'].isin(eligible_guids)] \
        .groupby(['guid', 'day_of_week_utc', 'hour_utc']).size().reset_index(name='hotspot_sessions')
    
    best_hotspots = hotspots.loc[hotspots.groupby('guid')['hotspot_sessions'].idxmax()]

    stats = best_hotspots.merge(guid_counts.rename('total_sessions'), left_on='guid', right_index=True)
    raw_counts = df.groupby(['guid', 'day_of_week_utc', 'hour_utc']).size().reset_index(name='raw_samples')
    stats = stats.merge(raw_counts, on=['guid', 'day_of_week_utc', 'hour_utc'])

    stats['confidence'] = (stats['hotspot_sessions'] / stats['total_sessions']) * 100

    predictable = stats[
        (stats['confidence'] >= CONFIDENCE_THRESHOLD) &
        (stats['hotspot_sessions'] >= MINIMUM_HOTSPOT_SESSIONS) &
        (stats['raw_samples'] >= MINIMUM_HOTSPOT_SAMPLES)
    ].copy()

    # --- 6. Strict Recency Filter ---
    print("Verifying attendance in last 2 weeks...")
    def get_past_date(target_weekday, weeks_ago):
        days_diff = target_weekday - now_utc.weekday()
        target_date = now_utc.date() + timedelta(days=days_diff) - timedelta(weeks=weeks_ago)
        return target_date

    predictable['last_week_date'] = predictable['day_of_week_utc'].apply(lambda x: get_past_date(x, 1))
    check_1 = predictable.merge(df_sessions, left_on=['guid', 'last_week_date', 'hour_utc'], right_on=['guid', 'date_utc', 'hour_utc'], suffixes=('', '_dup'))
    check_1['two_weeks_ago_date'] = check_1['day_of_week_utc'].apply(lambda x: get_past_date(x, 2))
    final_candidates = check_1.merge(df_sessions, left_on=['guid', 'two_weeks_ago_date', 'hour_utc'], right_on=['guid', 'date_utc', 'hour_utc'], suffixes=('', '_dup2'))
    
    print(f"Found {len(final_candidates)} GUIDs with consistent patterns.")

    # --- 7. Generate Predictions ---
    results = []
    day_map = {0: 'Mon', 1: 'Tue', 2: 'Wed', 3: 'Thu', 4: 'Fri', 5: 'Sat', 6: 'Sun'}

    for _, row in final_candidates.iterrows():
        guid = row['guid']
        h_day = row['day_of_week_utc']
        h_hour = row['hour_utc']

        mask = (df['guid'] == guid) & (df['day_of_week_utc'] == h_day) & (df['hour_utc'] == h_hour)
        likely_ip_port = df.loc[mask, 'ip'].mode()
        server_name = "" 
        if not likely_ip_port.empty:
            server_ip = likely_ip_port.iloc[0]
            server_name = server_map.get(server_ip, server_ip)

        last_arrival = df[(df['guid'] == guid) & (df['date_utc'] == row['last_week_date']) & (df['hour_utc'] == h_hour)]['timestamp_utc'].min()
        minute, second = (last_arrival.minute, last_arrival.second) if pd.notna(last_arrival) else (0,0)

        days_ahead = (h_day - now_utc.dayofweek + 7) % 7
        next_time = now_utc.replace(hour=h_hour, minute=minute, second=second, microsecond=0) + timedelta(days=days_ahead)
        if next_time < now_utc: next_time += timedelta(days=7)
        pred_mins = int((next_time - EPOCH_START_UTC).total_seconds() / 60)

        results.append({
            'guid': guid,
            'pred_mins': pred_mins,
            'timestamp': next_time,
            'context': context_info.get(guid, 'Context unavailable'),
            'server_name': server_name,
            'confidence': row['confidence'],
            'hotspot_count': row['hotspot_sessions'],
            'total_count': row['total_sessions']
        })

    # --- 8. Output Predictions ---
    results_df = pd.DataFrame(results).sort_values('pred_mins')
    results_df['safe_username'] = results_df['context'].apply(lambda x: urllib.parse.quote(x.split(',')[0]) if isinstance(x, str) else '')
    results_df['safe_servername'] = results_df['server_name'].apply(lambda x: urllib.parse.quote(x) if x else '')

    output_cols = ['pred_mins', 'guid', 'safe_username', 'safe_servername']
    results_df[output_cols].to_csv('predicted.csv', header=False, index=False)
    print(f"Saved predictions to 'predicted.csv'.")

    for _, row in results_df.iterrows():
        print("\n" + "="*60)
        mins_from_now = row['pred_mins'] - max_minutes
        print(f"GUID:       {row['guid']}")
        print(f"Prediction: {row['pred_mins']} (+{mins_from_now}m)")
        print(f"Location:   {row['server_name'] or 'Unknown'}") 

    # --- 9. TRIGGER TOOLTIPS ---
    predicted_guids = set(results_df['guid'].unique())
    live_threshold = max_minutes - 1440
    live_guids = set(df_raw[df_raw['mins'] > live_threshold]['guid'].unique())
    all_targets = predicted_guids.union(live_guids)
    
    if all_targets:
        generate_tooltips(df_raw, server_map, context_info, all_targets, max_minutes)

if __name__ == '__main__':
    find_imminent_hotspots()