import json
import csv
import os

# --- Configuration ---
DATA_DIR = "./data"
OUTPUT_FILE = "wwwroot/jammer-map.json"

# Column Indexes
SERVER_COL_INDEX = 2    # In census.csv
COUNTRY_COL_INDEX = 4   # In censusgeo.csv

print("--- Starting Jammer Map ETL (Case-Insensitive Name Deduplication) ---")

# 1. Identify Travelers & Track Last Seen Time
print("Scanning census.csv for travelers...")
guid_server_history = {}
guid_last_seen_minutes = {}  # Store raw minutes here
valid_guids = set()

try:
    with open(os.path.join(DATA_DIR, "census.csv"), 'r', encoding='utf-8', errors='replace') as f:
        reader = csv.reader(f)
        for row in reader:
            if len(row) > SERVER_COL_INDEX:
                try:
                    # Column 0 is minutes since 1/1/2023 (Integer)
                    minutes = int(row[0]) 
                    guid = row[1] 
                    server = row[SERVER_COL_INDEX]
                    
                    if guid not in guid_server_history: guid_server_history[guid] = set()
                    guid_server_history[guid].add(server)
                    
                    # Track the highest minute value seen for this GUID
                    if guid not in guid_last_seen_minutes or minutes > guid_last_seen_minutes[guid]:
                        guid_last_seen_minutes[guid] = minutes
                        
                except ValueError:
                    continue # Skip header or bad rows

    # for guid, servers in guid_server_history.items():
    #     if len(servers) > 1:
    #         valid_guids.add(guid)

    # NEW LOGIC: Accept everyone. 
    # If they are on a server, they deserve to be on the map.
    for guid, servers in guid_server_history.items():
        valid_guids.add(guid)    
    print(f"Found {len(valid_guids)} travelers.")

except FileNotFoundError:
    print("Error: census.csv not found.")

# 2. Load Identity
print("Loading identities...")
nodes = {}
try:
    with open(os.path.join(DATA_DIR, "censusgeo.csv"), 'r', encoding='utf-8', errors='replace') as f:
        reader = csv.reader(f)
        for row in reader:
            if len(row) >= 3:
                guid = row[0]
                if guid not in valid_guids: continue
                
                name = row[1].strip()
                raw_instrument = row[2].strip()
                
                # CLEAN INSTRUMENT: Treat "-" as empty
                if raw_instrument == "-":
                    instrument = "Unknown" 
                else:
                    instrument = raw_instrument
                
                # --- CAPTURE COUNTRY ---
                clean_country = "Unknown"
                if len(row) > COUNTRY_COL_INDEX:
                    raw_country = row[COUNTRY_COL_INDEX].strip()
                    clean_country = raw_country.replace("+", " ")
                # -----------------------

                # Filters
                if "obby" in name.lower(): continue 
                if " track" in name.lower(): continue 
                clean_name = name.replace("+", " ")
                if clean_name == "No Name": continue

                nodes[guid] = {
                    "id": guid,
                    "name": clean_name,
                    "country": clean_country,
                    "instrument": instrument,
                    "last_seen_minutes": guid_last_seen_minutes.get(guid, 0), # RAW INT
                    "total_seconds": 0,
                    "connections": {} 
                }
except FileNotFoundError:
    print("Error: censusgeo.csv not found.")

# 3. Process Edges & Calculate Time
print("Processing timeTogether.json...")
try:
    with open("timeTogether.json", 'r', encoding='utf-8') as f:
        data = json.load(f)
        for entry in data:
            key = entry.get("Key")
            val_str = entry.get("Value")
            
            if len(key) == 64:
                guid_a = key[:32]
                guid_b = key[32:]
                
                if guid_a in nodes and guid_b in nodes:
                    try:
                        parts = val_str.split(':')
                        secs = (float(parts[0]) * 3600) + (float(parts[1]) * 60) + float(parts[2])
                    except:
                        secs = 0
                    
                    if secs > 600: 
                        nodes[guid_a]["connections"][guid_b] = secs
                        nodes[guid_a]["total_seconds"] += secs
                        
                        nodes[guid_b]["connections"][guid_a] = secs
                        nodes[guid_b]["total_seconds"] += secs

except FileNotFoundError:
    print("timeTogether.json not found.")

# --- STEP 3.5: DEDUPLICATE (Name Case-Insensitive + Exact Country) ---
print("Consolidating duplicate identities...")

# 1. Determine the "Winner" for each unique person
best_identities = {}
for guid, node in nodes.items():
    unique_key = (node["name"].lower(), node["country"])
    seconds = node["total_seconds"]
    
    if unique_key not in best_identities:
        best_identities[unique_key] = node
    else:
        current_winner = best_identities[unique_key]
        node_mins = node.get("last_seen_minutes", 0)
        winner_mins = current_winner.get("last_seen_minutes", 0)
        
        if node_mins > winner_mins:
            best_identities[unique_key] = node
        elif node_mins == winner_mins and seconds > current_winner["total_seconds"]:
            best_identities[unique_key] = node

# 2. Map every original GUID to its winner's GUID
guid_to_winner_guid = {}
for guid, node in nodes.items():
    unique_key = (node["name"].lower(), node["country"])
    guid_to_winner_guid[guid] = best_identities[unique_key]["id"]

# 3. Reconstruct nodes with consolidated connections
consolidated_nodes = {}
for winner_node in best_identities.values():
    n = dict(winner_node)
    n["connections"] = {}
    n["total_seconds"] = 0
    consolidated_nodes[winner_node["id"]] = n

# 4. Aggregate all original connections into winners
for guid, node in nodes.items():
    winner_guid = guid_to_winner_guid[guid]
    target_node = consolidated_nodes[winner_guid]
    
    for peer_guid, secs in node["connections"].items():
        if peer_guid not in guid_to_winner_guid: continue
        peer_winner_guid = guid_to_winner_guid[peer_guid]
        
        if peer_winner_guid == winner_guid: continue # Avoid self-connection
        
        target_node["connections"][peer_winner_guid] = target_node["connections"].get(peer_winner_guid, 0) + secs

# 5. Finalize totals and filter out zero-time nodes
for n in consolidated_nodes.values():
    n["total_seconds"] = sum(n["connections"].values())

final_nodes_list = [n for n in consolidated_nodes.values() if n["total_seconds"] > 0]

print(f"Consolidation complete. Reduced {len(nodes)} raw nodes to {len(final_nodes_list)} unique identities.")

# 4. Export
print(f"Exporting to {OUTPUT_FILE}...")
with open(OUTPUT_FILE, 'w', encoding='utf-8') as f:
    json.dump(final_nodes_list, f)

print("Done.")
