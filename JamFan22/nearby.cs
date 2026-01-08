using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace JamFan22
{
    public class MusicianFinder
    {
        private static readonly HttpClient HttpClient = new HttpClient();
        private static readonly ConcurrentDictionary<string, CacheItem<IpApiDetails>> IpCache = new ConcurrentDictionary<string, CacheItem<IpApiDetails>>();
        
        // --- Caching Fields ---
        private static CacheItem<Dictionary<string, double>> _accruedTimeCache;
        private static CacheItem<Dictionary<string, DateTime>> _lastSeenMapCache;
        private static CacheItem<List<PredictedRecord>> _predictedUsersCache;
        private static CacheItem<MusicianDataCache> _musicianDataCache;
        private static CacheItem<Dictionary<string, string>> _guidCensusCache;
        private static CacheItem<Dictionary<string, string>> _tooltipCache; 
        private static readonly object _cacheLock = new object();
        
        // --- 23-Hour Blocklist Fields ---
        private static readonly ConcurrentDictionary<string, DateTime> _liveUserFirstSeen = new ConcurrentDictionary<string, DateTime>();
        private static readonly ConcurrentDictionary<string, byte> _sessionBlocklist = new ConcurrentDictionary<string, byte>();
        
        // --- Promotion Hack Field ---
        private static readonly ConcurrentDictionary<string, string> _promotedGuidMap = new ConcurrentDictionary<string, string>();
        
        // --- Prediction Diagnostics ---
        private static readonly ConcurrentDictionary<string, byte> _allTimePredictedGuids = new ConcurrentDictionary<string, byte>();
        private static readonly ConcurrentDictionary<string, byte> _allTimeArrivedGuids = new ConcurrentDictionary<string, byte>();

        // --- IP API Throttle Fields ---
        private static DateTime _lastIpApiCall = DateTime.MinValue;
        private static TimeSpan _ipApiDelay = TimeSpan.FromSeconds(1);
        private static readonly object _ipApiLock = new object();
        // ------------------------------

        private static readonly TextInfo TextCaseInfo = new CultureInfo("en-US", false).TextInfo;

        private static readonly HashSet<string> Acronyms = new HashSet<string>(StringComparer.Ordinal)
        {
            "CDMX", "NRW"
        };

        private static readonly HashSet<string> UsStateAbbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "FL", "GA",
            "HI", "ID", "IL", "IN", "IA", "KS", "KY", "LA", "ME", "MD",
            "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ",
            "NM", "NY", "NC", "ND", "OH", "OK", "OR", "PA", "RI", "SC",
            "SD", "TN", "TX", "UT", "VT", "VA", "WA", "WV", "WI", "WY",
            "DC"
        };
        
        private static readonly HashSet<string> CanadianProvinceAbbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AB", "BC", "MB", "NB", "NL", "NS", "ON", "PE", "QC", "SK",
            "NT", "NU", "YT"
        };

        public MusicianFinder() {}

        // --- DATA MODELS ---
        private class JamulusServer { public List<ApiClient> clients { get; set; } = new List<ApiClient>(); }
        private class ApiClient { public string name { get; set; } public string country { get; set; } public string instrument { get; set; } public string city { get; set; } }
        private class TimeRecord { public string Key { get; set; } public string Value { get; set; } }
        private class LastUpdateRecordRaw { public string Key { get; set; } public string Value { get; set; } }
        private class IpApiDetails { public string status { get; set; } public string city { get; set; } public string regionName { get; set; } public string countryCode { get; set; } public double lat { get; set; } public double lon { get; set; } }
        
        private class CacheItem<T> 
        { 
            public T Data { get; } 
            public DateTime Expiration { get; }
            
            public CacheItem(T data, TimeSpan duration) 
            { 
                Data = data; 
                Expiration = DateTime.UtcNow.Add(duration); 
            }
            
            public CacheItem(T data, int hours = 1) 
            { 
                Data = data; 
                Expiration = DateTime.UtcNow.AddHours(hours); 
            } 
            public bool IsExpired => DateTime.UtcNow > Expiration; 
        }

        private class PredictedRecord 
        { 
            public DateTime PredictedArrivalTime { get; set; } 
            public string Guid { get; set; } 
            public string Name { get; set; }
            public string Server { get; set; } 
        }
        
        private class MusicianRecord
        {
            public long MinutesSinceEpoch { get; set; }
            public int GoldenValue { get; set; } 
            public string Guid { get; set; }
            public string Name { get; set; }
            public string Instrument { get; set; }
            public string UserCity { get; set; }
            public double Lat { get; set; }
            public double Lon { get; set; }
            public string IpAddress { get; set; }
            public double DistanceKm { get; set; }
            public DateTime LastSeen { get; set; }
            public IpApiDetails Location { get; set; }
            public bool IsPredicted { get; set; } = false;
            public DateTime PredictedArrivalTime { get; set; }
            public string PredictedServer { get; set; } 
        }
        
        private class UserStats
        {
            public int TotalEntries { get; set; } = 0;
            public int EntriesWithInferredIp { get; set; } = 0;
            public MusicianRecord MostRecentRecord { get; set; } = null;
            
            // --- NEW: History Quality Tracking ---
            public int MaxGoldenValue { get; set; } = 0;
            public int ZeroGoldenCount { get; set; } = 0;
        }
        
        private class MusicianDataCache
        {
            public HashSet<string> AllGoldenGuids { get; set; }
            public Dictionary<string, UserStats> FullStatsMap { get; set; }
        }

        public async Task<string> FindMusiciansHtmlAsync(string userIp)
        {
            if (string.IsNullOrWhiteSpace(userIp))
                return "<table><tr><td>Error: IP address cannot be empty.</td></tr></table>";

            var userLocation = await GetIpDetailsAsync(userIp);
            if (userLocation?.status != "success")
                return $"<table><tr><td>Error: Could not determine your location from IP address {userIp}.</td></tr></table>";

            Console.WriteLine($"Client City: {userLocation.city}");
            return await FindMusiciansHtmlAsync(userLocation.lat, userLocation.lon);
        }

private async Task<string> FindMusiciansHtmlAsync(double userLat, double userLon)
        {
            // Get all data (from cache or file)
            var rawLiveGuids = await GetLiveGuidsFromApiAsync(); 
            var lastSeenMap = await GetLastSeenMap(); 
            var accruedTimeMap = await GetAccruedTimeMap(); 
            var predictedUsers = GetPredictedUsers();

            // --- 1. NEW: DETAILED PREDICTION DIAGNOSTICS ---
            // We fetch the CSV data early here to inspect the predicted users
            var csvDataDebug = GetMusicianDataFromCsv();
            
            if (predictedUsers.Any())
            {
                Console.WriteLine("\n--- DETAILED PREDICTION DIAGNOSTICS ---");
                foreach (var pred in predictedUsers)
                {
                    string diagInfo = $"GUID: {pred.Guid} | Name (Predicted.csv): {pred.Name}";

                    if (csvDataDebug.FullStatsMap.TryGetValue(pred.Guid, out var stats) && stats.MostRecentRecord != null)
                    {
                        var r = stats.MostRecentRecord;
                        diagInfo += $"\n    -> MATCH FOUND IN CSV: {r.Name} ({r.Instrument}) from {r.UserCity}";
                        
                        // --- REPLICATING FILTER LOGIC FROM GetMusicianRecords ---
                        // Rule 1: High Zero Ratio
                        bool hasLowCeiling = stats.MaxGoldenValue <= 1;
                        bool hasHighZeroRatio = stats.TotalEntries > 0 && ((double)stats.ZeroGoldenCount / stats.TotalEntries >= 0.943);
                        
                        // Rule 2: Golden/Inferred Entry Requirement
                        bool hasGoldenMatchEver = csvDataDebug.AllGoldenGuids.Contains(pred.Guid);
                        bool passesInferredIpTest = stats.TotalEntries > 1;

                        diagInfo += $"\n    -> STATS: Entries={stats.TotalEntries}, MaxGold={stats.MaxGoldenValue}, ZeroCount={stats.ZeroGoldenCount}";
                        
                        if (hasLowCeiling && hasHighZeroRatio)
                        {
                            diagInfo += $"\n    -> RESULT: [BLOCKED] User is flagged as 'Low Quality/Bot' (Zero Ratio: {(double)stats.ZeroGoldenCount/stats.TotalEntries:P1} >= 94.3%).";
                        }
                        else if (!hasGoldenMatchEver && !passesInferredIpTest)
                        {
                            diagInfo += $"\n    -> RESULT: [BLOCKED] User has no 'Golden' history and only 1 entry (Ghost filter).";
                        }
                        else
                        {
                            diagInfo += $"\n    -> RESULT: [OK] User passes quality filters. (Should appear unless location is too far).";
                        }
                    }
                    else
                    {
                        diagInfo += $"\n    -> RESULT: [MISSING] User exists in Prediction list but NOT in 'join-events.csv'. Cannot display (no IP/Location data).";
                    }
                    Console.WriteLine(diagInfo);
                }
                Console.WriteLine("---------------------------------------\n");
            }
            // ---------------------------------------------------

            // --- 2. EXISTING: AGGREGATE PREDICTION STATS ---
            if (predictedUsers.Any())
            {
                foreach (var prediction in predictedUsers)
                {
                    _allTimePredictedGuids.TryAdd(prediction.Guid, 1);
                    if (rawLiveGuids.Contains(prediction.Guid))
                    {
                        _allTimeArrivedGuids.TryAdd(prediction.Guid, 1);
                    }
                }

                int totalPredicted = _allTimePredictedGuids.Count;
                int totalArrived = _allTimeArrivedGuids.Count;
                double percentage = totalPredicted > 0 
                    ? (double)totalArrived / totalPredicted * 100.0 
                    : 0.0;

                Console.WriteLine($"[PREDICTION AGGREGATE] Arrived: {totalArrived} / Predicted: {totalPredicted} ({percentage:F1}%)");
            }

            var fullStatsMap = csvDataDebug.FullStatsMap; // Re-use the data fetched above
            var censusNameMap = await GetGuidCensusMapAsync();

            // --- CLEANUP ---
            var usersWhoLeft = _promotedGuidMap.Keys.Except(rawLiveGuids).ToList();
            foreach (var leftGuid in usersWhoLeft)
            {
                _promotedGuidMap.TryRemove(leftGuid, out _);
            }

            // --- 23-HOUR BLOCKLIST LOGIC ---
            var now = DateTime.UtcNow;

            foreach (var guid in rawLiveGuids)
            {
                if (_sessionBlocklist.ContainsKey(guid))
                {
                    string name = "Unknown"; 
                    if (fullStatsMap.TryGetValue(guid, out var stats) && stats?.MostRecentRecord != null && !string.IsNullOrWhiteSpace(stats.MostRecentRecord.Name))
                    {
                        name = stats.MostRecentRecord.Name; 
                    }
                    else if (censusNameMap.TryGetValue(guid, out var censusName))
                    {
                        name = censusName; 
                    }

                    string userInfo = $"{guid} ({name})";
                    Console.WriteLine($"{userInfo,-60} is PERMANENTLY HIDDEN (live > 23h).");
                    continue; 
                }

                _liveUserFirstSeen.TryAdd(guid, now);
                
                if (_liveUserFirstSeen.TryGetValue(guid, out var firstSeenTime))
                {
                    if (now - firstSeenTime > TimeSpan.FromHours(23))
                    {
                        Console.WriteLine($"{guid} has been live for over 23 hours. Adding to session blocklist.");
                        _sessionBlocklist.TryAdd(guid, 1);
                        _liveUserFirstSeen.TryRemove(guid, out _); 
                    }
                }
            }

            var potentialLogoffs = _liveUserFirstSeen.Keys.Except(rawLiveGuids).ToList();
            foreach (var guid in potentialLogoffs)
            {
                // FIX: Only reset their "First Seen" timer if they have been gone 
                // for more than 10 minutes. This prevents server-hopping from 
                // resetting the "Pulse" animation.
                bool recentlySeen = false;
                if (lastSeenMap.TryGetValue(guid, out var lastSeen))
                {
                    if ((DateTime.UtcNow - lastSeen).TotalMinutes < 10) 
                    {
                        recentlySeen = true;
                    }
                }

                if (!recentlySeen)
                {
                    _liveUserFirstSeen.TryRemove(guid, out _); 
                }
            }

            var liveGuids = rawLiveGuids.Where(g => !_sessionBlocklist.ContainsKey(g)).ToHashSet();

            // Keep "Gone" users for 60 minutes, but we will fade them visually
            var recentGuids = lastSeenMap.Where(kvp => kvp.Value > DateTime.UtcNow.AddMinutes(-60))
                .Select(kvp => kvp.Key)
                .Where(g => !_sessionBlocklist.ContainsKey(g)) 
                .ToHashSet();
            
            const double minLiveTime = 10.0;
            const double minRecentTime = 30.0;

            var eligibleLiveGuids = liveGuids
                .Where(g => accruedTimeMap.GetValueOrDefault(g, 0) >= minLiveTime)
                .ToHashSet();

            var eligibleRecentGuids = recentGuids
                .Where(g => accruedTimeMap.GetValueOrDefault(g, 0) >= minRecentTime)
                .ToHashSet();
            
            var finalGuids = eligibleLiveGuids.Union(eligibleRecentGuids).ToHashSet();
            
            foreach (var oldTrustedGuid in _promotedGuidMap.Values)
            {
                finalGuids.Add(oldTrustedGuid);
            }

            var finalPredictedUsers = predictedUsers
                .Where(p => !finalGuids.Contains(p.Guid))
                .Where(p => !_sessionBlocklist.ContainsKey(p.Guid)) 
                .ToList();

            var predictedGuids = finalPredictedUsers.Select(p => p.Guid).ToHashSet();
            var combinedGuidsForLookup = finalGuids.Union(predictedGuids).ToHashSet();

            if (!combinedGuidsForLookup.Any())
                return "<table><tr><td>No musicians found matching all criteria.</td></tr></table>";

            // Get Data
            var musicianRecords = GetMusicianRecords(combinedGuidsForLookup, userLat, userLon);
            
            var predictedUserDataMap = finalPredictedUsers.ToDictionary(p => p.Guid);

            var completedMusicianRecords = new List<MusicianRecord>();
            
            foreach (var record in musicianRecords)
            {
                record.Location = await GetIpDetailsAsync(record.IpAddress);
                record.UserCity = CleanAndFormatUserCity(record.UserCity, record.Location?.regionName);

                if (predictedUserDataMap.TryGetValue(record.Guid, out var predictedData))
                {
                    record.IsPredicted = true;
                    record.PredictedArrivalTime = predictedData.PredictedArrivalTime;
                    record.PredictedServer = predictedData.Server; 
                }
                else
                {
                    record.IsPredicted = false;
                    record.LastSeen = lastSeenMap.TryGetValue(record.Guid, out var seenDate) ? seenDate : DateTime.UtcNow;
                }
                completedMusicianRecords.Add(record);
            }

            // --- "GHOST PROMOTION" HACK ---
            var liveUserRecords = new Dictionary<string, MusicianRecord>();
            foreach (var liveGuid in rawLiveGuids) 
            {
                if (fullStatsMap.TryGetValue(liveGuid, out var stats) && stats.MostRecentRecord != null) 
                {
                    liveUserRecords[liveGuid] = stats.MostRecentRecord;
                }
            }

            var finalProcessedRecords = new List<MusicianRecord>(); 
            
            foreach (var record in completedMusicianRecords)
            {
                bool isLive = rawLiveGuids.Contains(record.Guid);
                
                if (record.IsPredicted || isLive)
                {
                    finalProcessedRecords.Add(record);
                    continue; 
                }

                // Gray record check
                bool wasRevived = false;
                foreach (var liveUser in liveUserRecords.Values)
                {
                    bool isNameMatch = liveUser.Name == record.Name;
                    bool isCityMatch = liveUser.UserCity == record.UserCity;

                    bool hasValidName = isNameMatch && 
                                        !string.IsNullOrWhiteSpace(liveUser.Name) && 
                                        liveUser.Name.Trim() != "No Name" &&
                                        liveUser.Name.Trim() != "-";
                    
                    bool hasValidCity = isCityMatch && 
                                        !string.IsNullOrWhiteSpace(liveUser.UserCity) && 
                                        liveUser.UserCity.Trim() != "-";

                    if (hasValidName && hasValidCity)
                    {
                        Console.WriteLine($"HACK: Reviving gray {record.Name} ({record.Guid}) via City match to live user {liveUser.Guid}.");
                        _promotedGuidMap.TryAdd(liveUser.Guid, record.Guid);
                        
                        record.Instrument = liveUser.Instrument; 
                        record.Guid = liveUser.Guid; 
                        
                        wasRevived = true;
                        finalProcessedRecords.Add(record); 
                        break; 
                    }
                } 

                if (!wasRevived)
                {
                    if (string.Equals(record.Name?.Trim(), "No Name", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    finalProcessedRecords.Add(record);
                }
            }
            // --- END OF HACK ---

            return BuildHtmlTable(finalProcessedRecords, rawLiveGuids, _liveUserFirstSeen);
        }
        
        private static string GetArrivalTimeDisplayString(DateTime arrivalTime)
        {
            double totalMinutes = (arrivalTime - DateTime.UtcNow).TotalMinutes;
            if (totalMinutes <= 0) return "DUE";
            if (totalMinutes <= 15) return "soon";
            if (totalMinutes <= 40) return "in ½ hour";
            if (totalMinutes <= 75) return "in 1 hour";
            if (totalMinutes <= 135) return "in 2 hours"; 
            return "in 2+ hours"; 
        }

        // --- TOOLTIP LOADER ---
        private Dictionary<string, string> GetTooltipMap()
        {
            if (_tooltipCache != null && !_tooltipCache.IsExpired) return _tooltipCache.Data;

            var map = new Dictionary<string, string>();
            string path = "tooltips.json"; 

            if (File.Exists(path))
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        string json = sr.ReadToEnd();
                        map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    }
                }
                catch (Exception ex)
                { 
                    Console.WriteLine($"[GetTooltipMap] Error reading tooltips.json: {ex.Message}");
                }
            }

            lock (_cacheLock) 
            { 
                _tooltipCache = new CacheItem<Dictionary<string, string>>(map, TimeSpan.FromMinutes(1)); 
            }
            return map;
        }

        private string BuildHtmlTable(IEnumerable<MusicianRecord> records, HashSet<string> liveGuids, ConcurrentDictionary<string, DateTime> firstSeenMap)
        {
            var tooltips = GetTooltipMap();
            var sb = new StringBuilder();

            sb.AppendLine("<style>");
            
            // 1. SMART GRID CONTAINER
            // Uses auto-fit to switch between 1, 2, or 3 columns automatically based on width.
            sb.AppendLine("  .musician-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 450px), 1fr)); gap: 2px; width: 100%; }");
            
            // 2. THE ROWS (Cards)
            // 'align-items: stretch' ensures the divider lines connect to the top/bottom borders.
            sb.AppendLine("  .musician-row { border: 1px solid #444; padding: 0; display: flex; align-items: stretch; min-height: 42px; }"); 
            
            // 3. LEFT COLUMN (Musician)
            sb.AppendLine("  .col-musician { width: 55%; position: relative; padding: 4px 8px 4px 4px; display: flex; align-items: center; }"); 
            
            // THE SPLIT MARKERS (The little brackets)
            // CHANGE: Height reduced from 25% to 20% to make them shorter.
            // Top Stub
            sb.AppendLine("  .col-musician::before { content: ''; position: absolute; right: 0; top: 0; height: 20%; width: 1px; background-color: #777; }");
            // Bottom Stub
            sb.AppendLine("  .col-musician::after { content: ''; position: absolute; right: 0; bottom: 0; height: 20%; width: 1px; background-color: #777; }");

            // 4. RIGHT COLUMN (Location)
            // 'align-items: center' centers the text block vertically.
            sb.AppendLine("  .col-location { width: 45%; padding: 4px 4px 4px 8px; word-wrap: break-word; display: flex; align-items: center; }");

            // 5. UTILITIES
            sb.AppendLine("  .bulb { height: 12px; width: 12px; border-radius: 50%; display: inline-block; margin-right: 8px; flex-shrink: 0; }");
            sb.AppendLine("  .green { background-color: #28a745; }");
            sb.AppendLine("  .gray { background-color: #adb5bd; }");
            sb.AppendLine("  .yellow { background-color: #ffc107; }"); 
            sb.AppendLine("  .not-here { color: #6c757d; }");
            
            sb.AppendLine("  .fading-medium { opacity: 0.75; }");
            sb.AppendLine("  .fading-ghost { opacity: 0.45; }");
            sb.AppendLine("  .bulb-shrink { transform: scale(0.75); }");
            
            sb.AppendLine("  @keyframes pulser { 0% { transform: scale(0.95); opacity: 0.7; } 50% { transform: scale(1.15); opacity: 1; } 100% { transform: scale(0.95); opacity: 0.7; } }");
            sb.AppendLine("  .pulse-green { animation: pulser 2s infinite ease-in-out; }");
            sb.AppendLine("  .pulse-yellow { animation: pulser 2s infinite ease-in-out; }");
            sb.AppendLine("  @keyframes urgent-pulse { 0% { transform: scale(1); background-color: #ffc107; box-shadow: 0 0 0 0 rgba(255, 193, 7, 0.7); } 50% { transform: scale(1.3); background-color: #ff5722; box-shadow: 0 0 10px rgba(255, 87, 34, 0.5); } 100% { transform: scale(1); background-color: #ffc107; box-shadow: 0 0 0 0 rgba(255, 193, 7, 0); } }");
            sb.AppendLine("  .pulse-due { animation: urgent-pulse 1s infinite ease-in-out; z-index: 10; position: relative; }");
            
            sb.AppendLine("  .sub-line { font-size: 0.85em; font-family: sans-serif-condensed, Arial Narrow, sans-serif; color: #888; }"); 
            sb.AppendLine("  .sub-line-container { display: flex; justify-content: space-between; align-items: baseline; }");
            sb.AppendLine("  .sub-line-instrument { font-size: 0.85em; font-family: sans-serif-condensed, Arial Narrow, sans-serif; color: #888; }");
            sb.AppendLine("  .sub-line-time { font-size: 0.8em; font-family: sans-serif-condensed, Arial Narrow, sans-serif; color: #777; font-style: italic; margin-left: 4px; }");
            
            sb.AppendLine("  .musician-cell-content { display: flex; align-items: center; width: 100%; }");
            sb.AppendLine("  .musician-info { flex-grow: 1; }");
            
            // 6. SHOW MORE LOGIC
            sb.AppendLine("  .distant-row { display: none !important; }"); 
            sb.AppendLine("  .show-more-card { grid-column: 1 / -1; cursor: pointer; color: #007bff; text-align: center; padding: 10px; border: 1px dashed #555; }");
            sb.AppendLine("  .show-more-card:hover { background-color: rgba(0,0,0,0.1); }");   
            sb.AppendLine("</style>");

            // --- BUILD THE GRID ---
            sb.AppendLine("<div class='musician-grid'>");

var orderedRecords = records
    .OrderBy(r => r.DistanceKm)
    // CHANGE: Group by Name only. This merges multiple connections 
    // for the same person (e.g., "Bass" client and "Stream" client).
    .GroupBy(r => r.Name?.Trim(), StringComparer.OrdinalIgnoreCase) 
    .Select(g => 
    {                    // Priority 1: If one of them is LIVE, show that one.
                    var liveRecord = g.FirstOrDefault(r => liveGuids.Contains(r.Guid));
                    if (liveRecord != null) return liveRecord;

                    // Priority 2: If one is PREDICTED, show that one.
                    var predictedRecord = g.FirstOrDefault(r => r.IsPredicted);
                    if (predictedRecord != null) return predictedRecord;

                    // Priority 3: Otherwise, show the one seen most recently.
                    return g.OrderByDescending(r => r.LastSeen).First();
                })
                .ToList();

            const int minimumVisibleCount = 3;
            const double distanceThresholdKm = 3000;
            var nearbyRecords = new List<MusicianRecord>();
            var distantRecords = new List<MusicianRecord>();

            foreach (var record in orderedRecords)
            {
                if (record.DistanceKm < distanceThresholdKm || nearbyRecords.Count < minimumVisibleCount)
                    nearbyRecords.Add(record);
                else
                    distantRecords.Add(record);
            }

            if (distantRecords.Count <= 2)
            {
                nearbyRecords.AddRange(distantRecords);
                distantRecords.Clear();
            }

            foreach (var record in nearbyRecords)
            {
                sb.Append(BuildMusicianRowHtml(record, liveGuids, firstSeenMap, tooltips));
            }
            
            if (distantRecords.Any())
            {
                sb.AppendLine($"<div id='show-more-card' class='show-more-card' onclick='ShowDistantRows()'>{distantRecords.Count} more</div>");

                foreach (var record in distantRecords)
                {
                    string rowHtml = BuildMusicianRowHtml(record, liveGuids, firstSeenMap, tooltips);
                    string modifiedRowHtml = rowHtml.Replace("class='musician-row", "class='musician-row distant-row");
                    sb.Append(modifiedRowHtml);
                }
            }

            sb.AppendLine("</div>"); 

            if (distantRecords.Any())
            {
                sb.AppendLine("<script>");
                sb.AppendLine("  function ShowDistantRows() {");
                sb.AppendLine("    var distantRows = document.getElementsByClassName('distant-row');");
                sb.AppendLine("    for (var i = distantRows.length - 1; i >= 0; i--) {");
                sb.AppendLine("      distantRows[i].classList.remove('distant-row');"); 
                sb.AppendLine("    }");
                sb.AppendLine("    var btn = document.getElementById('show-more-card');");
                sb.AppendLine("    if (btn) btn.style.display = 'none';");
                sb.AppendLine("  }");
                sb.AppendLine("</script>");
            }        

            // Logging loop
            foreach (var record in orderedRecords)
            {
                if (liveGuids.Contains(record.Guid)) record.IsPredicted = false;
                string musicianPart = !string.IsNullOrWhiteSpace(record.Instrument) && record.Instrument.Trim() != "-"
                    ? $"{record.Name} ({record.Instrument})" : record.Name;
                string status = record.IsPredicted ? "PREDICTED" : (liveGuids.Contains(record.Guid) ? "NOW" : "GONE");
                string detailsPart;
                if(record.IsPredicted)
                    detailsPart = $"Displays \"{GetArrivalTimeDisplayString(record.PredictedArrivalTime)}\"";
                else
                    detailsPart = !string.IsNullOrWhiteSpace(record.UserCity) ? $"{record.UserCity}, {record.Location?.regionName}" : record.Location?.regionName;
                Console.WriteLine($"{record.Guid} {status,-9} {musicianPart,-35} -> {detailsPart}");
            }
            return sb.ToString();
        }

        private string BuildMusicianRowHtml(MusicianRecord record, HashSet<string> liveGuids, ConcurrentDictionary<string, DateTime> firstSeenMap, Dictionary<string, string> tooltips)
        {
            bool isLive = liveGuids.Contains(record.Guid);
            if (isLive) record.IsPredicted = false;

            var sb = new StringBuilder();
            string bulbClass;
            string rowClass = "musician-row";
            string locationDisplay;
            string rowOpacityClass = ""; 
            string bulbSizeClass = "";   
            
            // --- Time Display Logic ---
            string timeDisplay = "";
            bool isFreshArrival = false;

            if (isLive)
            {
                if (firstSeenMap.TryGetValue(record.Guid, out var firstSeenTime))
                {
                    var duration = DateTime.UtcNow - firstSeenTime;
                    if (duration.TotalMinutes < 5) isFreshArrival = true;
                    if (duration.TotalMinutes >= 1)
                    {
                        timeDisplay = "<span class='sub-line-time'>";
                        if (duration.TotalHours >= 1) timeDisplay += $"{(int)duration.TotalHours}h ";
                        timeDisplay += $"{duration.Minutes}m</span>";
                    }
                }
            }
            else if (!record.IsPredicted) 
            {
                var timeGone = DateTime.UtcNow - record.LastSeen;
                if (timeGone.TotalMinutes > 30) { rowOpacityClass = " fading-ghost"; bulbSizeClass = " bulb-shrink"; }
                else if (timeGone.TotalMinutes > 15) { rowOpacityClass = " fading-medium"; }
            }
            
            if (record.IsPredicted)
            {
                bulbClass = "yellow"; 
                double minutesUntil = (record.PredictedArrivalTime - DateTime.UtcNow).TotalMinutes;
                if (minutesUntil <= 0) bulbClass += " pulse-due";
                else if (minutesUntil <= 15) bulbClass += " pulse-yellow";

                string timeStr = GetArrivalTimeDisplayString(record.PredictedArrivalTime);
                locationDisplay = !string.IsNullOrWhiteSpace(record.PredictedServer) 
                    ? $"{timeStr} @<br/><span class='sub-line'>{record.PredictedServer}</span>" 
                    : timeStr;
            }
            else
            {
                rowClass += isLive ? "" : " not-here" + rowOpacityClass;
                bulbClass = isLive ? "green" : "gray" + bulbSizeClass;
                if (isLive && isFreshArrival) bulbClass += " pulse-green"; 

                string ipDerivedRegion = record.Location?.regionName ?? "";
                string regionHtml = $"<span class='sub-line'>{ipDerivedRegion}</span>";
                bool hasValidCity = !string.IsNullOrWhiteSpace(record.UserCity) && record.UserCity.Trim() != "-";
                bool cityIsDifferentFromRegion = !string.Equals(record.UserCity.Trim(), ipDerivedRegion.Trim(), StringComparison.OrdinalIgnoreCase);

                locationDisplay = (hasValidCity && cityIsDifferentFromRegion) 
                    ? $"{record.UserCity},<br/>{regionHtml}" 
                    : regionHtml;
            }

            // --- HTML STRUCTURE ---
            string instrumentHtml = !string.IsNullOrWhiteSpace(record.Instrument) && record.Instrument.Trim() != "-"
                ? $"<span class='sub-line-instrument'>{record.Instrument}</span>"
                : "<span></span>"; 

            string sublineContainer = $"<div class='sub-line-container'>{instrumentHtml}{timeDisplay}</div>";

            string tooltipAttr = "";
            if (tooltips != null && tooltips.TryGetValue(record.Guid, out string tip))
            {
                tooltipAttr = $" title=\"{WebUtility.HtmlEncode(tip)}\"";
            }

            // CHANGE: Wrapped {locationDisplay} in a div so flexbox doesn't flatten the <br/>
            sb.AppendLine($"<div class='{rowClass}' {tooltipAttr}>");
            sb.AppendLine($"  <div class='col-musician'><div class='musician-cell-content'><span class='bulb {bulbClass}'></span><div class='musician-info'>{record.Name}{sublineContainer}</div></div></div>");
            sb.AppendLine($"  <div class='col-location'><div>{locationDisplay}</div></div>");
            sb.AppendLine("</div>");
            
            return sb.ToString();
        }

        private string CleanAndFormatUserCity(string userCity, string? ipRegion)
        {
            if (string.IsNullOrWhiteSpace(userCity)) return userCity;
            
            string cleanedCity = userCity.Trim(); 
            
            if (UsStateAbbreviations.Contains(cleanedCity) || CanadianProvinceAbbreviations.Contains(cleanedCity))
            {
                return "";
            }

            var parts = cleanedCity.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            
            if (parts.Length > 1)
            {
                var lastPart = parts.Last();
                if (UsStateAbbreviations.Contains(lastPart) || CanadianProvinceAbbreviations.Contains(lastPart))
                {
                    cleanedCity = string.Join(" ", parts.Take(parts.Length - 1));
                }
            }
            
            if (!string.IsNullOrWhiteSpace(ipRegion) && cleanedCity.EndsWith(ipRegion, StringComparison.OrdinalIgnoreCase))
            {
                 cleanedCity = cleanedCity.Substring(0, cleanedCity.Length - ipRegion.Length).Trim();
            }

            return SmartCapitalize(cleanedCity);
        }        
        private string SmartCapitalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || Acronyms.Contains(text))
                return text;

            bool isAllUpper = text.Equals(text.ToUpperInvariant());
            bool isAllLower = text.Equals(text.ToLowerInvariant());
            bool isInverted = text.Length > 1 && char.IsLower(text[0]) && text.Substring(1).Equals(text.Substring(1).ToUpperInvariant());

            string baseCapitalizedText;
            if (isAllUpper || isAllLower || isInverted)
            {
                baseCapitalizedText = TextCaseInfo.ToTitleCase(text.ToLowerInvariant());
            }
            else
            {
                baseCapitalizedText = text;
            }

            var sb = new StringBuilder();
            for (int i = 0; i < baseCapitalizedText.Length; i++)
            {
                sb.Append(baseCapitalizedText[i]);
                if (baseCapitalizedText[i] == '.' && (i + 1) < baseCapitalizedText.Length && char.IsLetter(baseCapitalizedText[i + 1]))
                {
                    sb.Append(' ');
                }
            }
            return sb.ToString();
        }

        private MusicianDataCache GetMusicianDataFromCsv()
        {
            if (_musicianDataCache != null && !_musicianDataCache.IsExpired)
            {
                return _musicianDataCache.Data;
            }
            
            var allGoldenGuids = new HashSet<string>();
            var fullStatsMap = new Dictionary<string, UserStats>();
            const int cacheDurationSeconds = 30; 

            try
            {
                var lines = File.ReadAllLines("join-events.csv");
                foreach (var line in lines)
                {
                    var fields = line.Split(',');
                    if (fields.Length < 13) continue; 

                    var guid = fields[2].Trim();
                    
                    // Parse Golden Value safely
                    int currentRowGoldenVal = 0;
                    if (fields.Length > 12)
                    {
                        int.TryParse(fields[12].Trim(), out currentRowGoldenVal);
                    }

                    if (currentRowGoldenVal != 0)
                    {
                        allGoldenGuids.Add(guid);
                    }

                    if (!fullStatsMap.TryGetValue(guid, out UserStats stats))
                    {
                        stats = new UserStats();
                        fullStatsMap[guid] = stats;
                    }
                    
                    stats.TotalEntries++;
                    
                    // --- NEW STATS TRACKING ---
                    if (currentRowGoldenVal > stats.MaxGoldenValue)
                    {
                        stats.MaxGoldenValue = currentRowGoldenVal;
                    }

                    if (currentRowGoldenVal == 0)
                    {
                        stats.ZeroGoldenCount++;
                    }
                    // --------------------------

                    if (!string.IsNullOrWhiteSpace(fields[11]) && fields[11].Trim() != "-")
                    {
                        stats.EntriesWithInferredIp++;
                    }

                    if (long.TryParse(fields[0].Trim(), out long minutes) &&
                        double.TryParse(fields[9].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double lat) &&
                        double.TryParse(fields[10].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double lon) &&
                        !string.IsNullOrWhiteSpace(fields[11].Trim()) && fields[11].Trim() != "-")
                    {
                        bool shouldUpdate = false;

                        if (stats.MostRecentRecord == null)
                        {
                            shouldUpdate = true;
                        }
                        else
                        {
                            // 1. Higher Golden Value wins
                            if (currentRowGoldenVal > stats.MostRecentRecord.GoldenValue)
                            {
                                shouldUpdate = true;
                            }
                            // 2. Tie-break with Time
                            else if (currentRowGoldenVal == stats.MostRecentRecord.GoldenValue && minutes > stats.MostRecentRecord.MinutesSinceEpoch)
                            {
                                shouldUpdate = true;
                            }
                        }

                        if (shouldUpdate)
                        {
                            stats.MostRecentRecord = new MusicianRecord
                            {
                                MinutesSinceEpoch = minutes,
                                GoldenValue = currentRowGoldenVal, 
                                Guid = guid,
                                Name = WebUtility.UrlDecode(fields[3].Trim()),
                                Instrument = WebUtility.UrlDecode(fields[4].Trim()),
                                UserCity = WebUtility.UrlDecode(fields[5].Trim()).Split(',')[0].Trim(),
                                Lat = lat,
                                Lon = lon,
                                IpAddress = fields[11].Trim()
                            };
                        }
                    }
                }
            }
            catch (Exception ex) 
            { 
                Console.WriteLine($"[GetMusicianDataFromCsv] Error reading join-events.csv: {ex.Message}");
            }

            var cacheData = new MusicianDataCache { AllGoldenGuids = allGoldenGuids, FullStatsMap = fullStatsMap };
            
            lock (_cacheLock) { _musicianDataCache = new CacheItem<MusicianDataCache>(cacheData, TimeSpan.FromSeconds(cacheDurationSeconds)); }
            return cacheData;
        }

private List<MusicianRecord> GetMusicianRecords(HashSet<string> guidsToFind, double userLat, double userLon)
        {
            var cachedData = GetMusicianDataFromCsv();
            var allGoldenGuids = cachedData.AllGoldenGuids;
            var fullStatsMap = cachedData.FullStatsMap;

            var finalRecords = new List<MusicianRecord>();
            foreach (var guid in guidsToFind)
            {
                if (!fullStatsMap.TryGetValue(guid, out var stats) || stats.MostRecentRecord == null)
                {
                    continue;
                }

                // --- FILTER: Exclude names containing "lobby" ---
                if (stats.MostRecentRecord.Name != null && 
                    stats.MostRecentRecord.Name.IndexOf("lobby", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }
                // --- NEW FILTER: Exclude unnamed Streamers ---
                else if (string.IsNullOrWhiteSpace(stats.MostRecentRecord.Name) && 
                         string.Equals(stats.MostRecentRecord.Instrument, "Streamer", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                // ------------------------------------------------

                // --- NEW FILTER RULE ---
                // Rule: Max Value <= 1 AND >= 88% are zeroes
                bool hasLowCeiling = stats.MaxGoldenValue <= 1;
                bool hasHighZeroRatio = stats.TotalEntries > 0 && ((double)stats.ZeroGoldenCount / stats.TotalEntries >= 0.943);

                if (hasLowCeiling && hasHighZeroRatio)
                {
                    string uName = stats.MostRecentRecord.Name ?? "Unknown";
                    Console.WriteLine($"[FILTER] Excluded {guid} ({uName}). " +
                                      $"MaxGold: {stats.MaxGoldenValue}, " +
                                      $"Zeroes: {stats.ZeroGoldenCount}/{stats.TotalEntries} " +
                                      $"({(double)stats.ZeroGoldenCount / stats.TotalEntries:P0})");
                    continue; // Skip this user entirely
                }
                // -----------------------

                bool hasGoldenMatchEver = allGoldenGuids.Contains(guid);
                bool passesInferredIpTest = false;

                if (!hasGoldenMatchEver)
                {
                    string userInfo = $"{guid} ({stats.MostRecentRecord.Name})";

                    if (stats.TotalEntries > 1)
                    {
                        passesInferredIpTest = true;
                        Console.WriteLine($"{userInfo,-60} PASSED. No 'golden match'. (Entries: {stats.TotalEntries} > 1)");
                    }
                    else
                    {
                        Console.WriteLine($"{userInfo,-60} FAILED. No 'golden match'. (Entries: {stats.TotalEntries} <= 1)");
                    }
                }

                if (hasGoldenMatchEver || passesInferredIpTest)
                {
                    finalRecords.Add(stats.MostRecentRecord);
                }
            }

            return finalRecords
                .Select(r =>
                {
                    r.DistanceKm = CalculateDistance(userLat, userLon, r.Lat, r.Lon);
                    return r;
                })
                .Where(r => r.DistanceKm < 5000)
                .ToList();
        }
                    
        private async Task<HashSet<string>> GetLiveGuidsFromApiAsync()
        {
            var liveGuids = new HashSet<string>();
            foreach (var key in JamFan22.Pages.IndexModel.JamulusListURLs.Keys)
            {
                try
                {
                    if (JamFan22.Pages.IndexModel.LastReportedList.TryGetValue(key, out var jsonString) && !string.IsNullOrEmpty(jsonString))
                    {
                        var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServer>>(jsonString);
                        if (serversOnList != null)
                        {
                            foreach (var server in serversOnList)
                            {
                                if (server != null && server.clients != null)
                                {
                                    foreach (var client in server.clients)
                                    {
                                        if (client != null)
                                        {
                                            liveGuids.Add(GetGuid(client.name, client.country, client.instrument));
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GetLiveGuidsFromApiAsync] Error processing data for key '{key}'. Exception: {ex.Message}");
                }
            }
            await Task.CompletedTask;
            return liveGuids;
        }

        private List<PredictedRecord> GetPredictedUsers()
        {
            if (_predictedUsersCache != null && !_predictedUsersCache.IsExpired)
            {
                return _predictedUsersCache.Data;
            }

            var predictedUsers = new List<PredictedRecord>();
            const string fileName = "predicted.csv";
            const int cacheDurationSeconds = 30; 

            if (!File.Exists(fileName))
            {
                lock (_cacheLock) { _predictedUsersCache = new CacheItem<List<PredictedRecord>>(predictedUsers, TimeSpan.FromSeconds(cacheDurationSeconds)); }
                return predictedUsers;
            }

            var now = DateTime.UtcNow;
            var tenMinutesAgo = now.AddMinutes(-10);
            var threeHoursFromNow = now.AddHours(3);
            var epoch = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            try
            {
                var lines = File.ReadAllLines(fileName);
                foreach (var line in lines)
                {
                    var fields = line.Split(','); 
                    if (fields.Length < 3) continue;

                    if (long.TryParse(fields[0].Trim(), out long minutesSinceEpoch))
                    {
                        var arrivalTime = epoch.AddMinutes(minutesSinceEpoch);
                        if (arrivalTime > tenMinutesAgo && arrivalTime <= threeHoursFromNow)
                        {
                            string serverName = "";
                            if (fields.Length > 3)
                            {
                                serverName = WebUtility.UrlDecode(fields[3].Trim());
                            }

                            predictedUsers.Add(new PredictedRecord
                            {
                                PredictedArrivalTime = arrivalTime,
                                Guid = fields[1].Trim(),
                                Name = WebUtility.UrlDecode(fields[2].Trim()),
                                Server = serverName 
                            });
                        }
                    }
                }
                Console.WriteLine($"[GetPredictedUsers] Found {predictedUsers.Count} users predicted to arrive in the next 3 hours (or are up to 10 mins late).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetPredictedUsers] Error reading or parsing {fileName}: {ex.Message}");
            }
            
            lock (_cacheLock) { _predictedUsersCache = new CacheItem<List<PredictedRecord>>(predictedUsers, TimeSpan.FromSeconds(cacheDurationSeconds)); }
            return predictedUsers;
        }

        private async Task<Dictionary<string, DateTime>> GetLastSeenMap()
        {
            if (_lastSeenMapCache != null && !_lastSeenMapCache.IsExpired)
            {
                return _lastSeenMapCache.Data;
            }

            var lastSeenMap = new Dictionary<string, DateTime>();
            const string fileName = "timeTogetherLastUpdates.json";
            const int cacheDurationSeconds = 30; 
            
            int maxRetries = 2; 
            int delayMs = 250; 

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    var json = File.ReadAllText(fileName); 
                    var records = JsonSerializer.Deserialize<List<LastUpdateRecordRaw>>(json);
                    
                    if (records == null)
                    {
                        lock (_cacheLock) { _lastSeenMapCache = new CacheItem<Dictionary<string, DateTime>>(lastSeenMap, TimeSpan.FromSeconds(cacheDurationSeconds)); }
                        return lastSeenMap;
                    }

                    var allUpdates = new List<(string Guid, DateTime LastSeen)>();
                    foreach (var record in records)
                    {
                        if (record.Key.Length != 64) continue;
                        if (DateTime.TryParse(record.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsedDate))
                        {
                            allUpdates.Add((record.Key.Substring(0, 32), parsedDate));
                            allUpdates.Add((record.Key.Substring(32, 32), parsedDate));
                        }
                    }
                    
                    var finalMap = allUpdates
                        .GroupBy(u => u.Guid)
                        .ToDictionary(g => g.Key, g => g.Max(item => item.LastSeen));
                    
                    lock (_cacheLock) { _lastSeenMapCache = new CacheItem<Dictionary<string, DateTime>>(finalMap, TimeSpan.FromSeconds(cacheDurationSeconds)); }
                    return finalMap;
                }
                catch (JsonException ex)
                {
                    Console.WriteLine($"[GetLastSeenMap] Warning: Failed to parse {fileName} on attempt {i + 1}/{maxRetries}. Error: {ex.Message}");
                    if (i < maxRetries - 1) await Task.Delay(delayMs); 
                }
                catch (IOException ex)
                {
                    Console.WriteLine($"[GetLastSeenMap] Warning: Failed to read {fileName} on attempt {i + 1}/{maxRetries}. Error: {ex.Message}");
                    if (i < maxRetries - 1) await Task.Delay(delayMs);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GetLastSeenMap] CRITICAL ERROR reading or parsing {fileName}. Error: {ex.Message}");
                    lock (_cacheLock) { _lastSeenMapCache = new CacheItem<Dictionary<string, DateTime>>(lastSeenMap, TimeSpan.FromSeconds(cacheDurationSeconds)); } 
                    return lastSeenMap;
                }
            }

            Console.WriteLine($"[GetLastSeenMap] CRITICAL ERROR: All {maxRetries} retry attempts failed for {fileName}.");
            lock (_cacheLock) { _lastSeenMapCache = new CacheItem<Dictionary<string, DateTime>>(lastSeenMap, TimeSpan.FromSeconds(cacheDurationSeconds)); } 
            return lastSeenMap;
        }

        private async Task<Dictionary<string, double>> GetAccruedTimeMap()
        {
            if (_accruedTimeCache != null && !_accruedTimeCache.IsExpired)
            {
                return _accruedTimeCache.Data;
            }

            var accruedTime = new Dictionary<string, double>();
            const string fileName = "timeTogether.json";
            const int cacheDurationSeconds = 30; 

            int maxRetries = 2;
            int delayMs = 250;

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    var json = File.ReadAllText(fileName);
                    var records = JsonSerializer.Deserialize<List<TimeRecord>>(json);
                    
                    if (records == null)
                    {
                        lock (_cacheLock) { _accruedTimeCache = new CacheItem<Dictionary<string, double>>(accruedTime, TimeSpan.FromSeconds(cacheDurationSeconds)); }
                        return accruedTime;
                    }

                    foreach (var record in records)
                    {
                        if (record.Key.Length != 64) continue;
                        
                        if (TimeSpan.TryParse(record.Value, CultureInfo.InvariantCulture, out TimeSpan duration))
                        {
                            string guidA = record.Key.Substring(0, 32);
                            string guidB = record.Key.Substring(32, 32);
                            
                            double minutes = duration.TotalMinutes;
                            
                            accruedTime[guidA] = accruedTime.GetValueOrDefault(guidA, 0) + minutes;
                            accruedTime[guidB] = accruedTime.GetValueOrDefault(guidB, 0) + minutes;
                        }
                        else
                        {
                            Console.WriteLine($"[GetAccruedTimeMap] Warning: Could not parse TimeSpan value: {record.Value}");
                        }
                    }
                    
                    lock (_cacheLock) { _accruedTimeCache = new CacheItem<Dictionary<string, double>>(accruedTime, TimeSpan.FromSeconds(cacheDurationSeconds)); }
                    Console.WriteLine($"[GetAccruedTimeMap] Successfully loaded {accruedTime.Count} unique GUIDs from {fileName}.");
                    return accruedTime; 
                }
                catch (JsonException ex)
                {
                    Console.WriteLine($"[GetAccruedTimeMap] Warning: Failed to parse {fileName} on attempt {i + 1}/{maxRetries}. Error: {ex.Message}");
                    if (i < maxRetries - 1) await Task.Delay(delayMs);
                }
                catch (IOException ex)
                {
                    Console.WriteLine($"[GetAccruedTimeMap] Warning: Failed to read {fileName} on attempt {i + 1}/{maxRetries}. Error: {ex.Message}");
                    if (i < maxRetries - 1) await Task.Delay(delayMs);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GetAccruedTimeMap] CRITICAL ERROR reading or parsing {fileName}. Error: {ex.Message}");
                    lock (_cacheLock) { _accruedTimeCache = new CacheItem<Dictionary<string, double>>(accruedTime, TimeSpan.FromSeconds(cacheDurationSeconds)); } 
                    return accruedTime; 
                }
            }
            
            Console.WriteLine($"[GetAccruedTimeMap] CRITICAL ERROR: All {maxRetries} retry attempts failed for {fileName}.");
            lock (_cacheLock) { _accruedTimeCache = new CacheItem<Dictionary<string, double>>(accruedTime, TimeSpan.FromSeconds(cacheDurationSeconds)); } 
            return accruedTime;
        }

        private async Task<Dictionary<string, string>> GetGuidCensusMapAsync()
        {
            if (_guidCensusCache != null && !_guidCensusCache.IsExpired)
            {
                return _guidCensusCache.Data;
            }

            var censusMap = new Dictionary<string, string>();
            const string fileName = "data/censusgeo.csv";
            const int cacheDurationMinutes = 5; 

            int maxRetries = 2; 
            int delayMs = 250; 

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    if (!File.Exists(fileName))
                    {
                        Console.WriteLine($"[GetGuidCensusMapAsync] Warning: File not found: {fileName}");
                        break; 
                    }

                    var lines = File.ReadAllLines(fileName);
                    foreach (var line in lines)
                    {
                        var fields = line.Split(',');
                        if (fields.Length >= 2) 
                        {
                            string guid = fields[0].Trim();
                            string name = WebUtility.UrlDecode(fields[1].Trim());
                            
                            if (!string.IsNullOrWhiteSpace(guid) && !string.IsNullOrWhiteSpace(name))
                            {
                                censusMap[guid] = name;
                            }
                        }
                    }
                    
                    Console.WriteLine($"[GetGuidCensusMapAsync] Successfully loaded {censusMap.Count} unique GUIDs from {fileName}.");
                    lock (_cacheLock) { _guidCensusCache = new CacheItem<Dictionary<string, string>>(censusMap, TimeSpan.FromMinutes(cacheDurationMinutes)); }
                    return censusMap; 
                }
                catch (IOException ex)
                {
                    Console.WriteLine($"[GetGuidCensusMapAsync] Warning: Failed to read {fileName} on attempt {i + 1}/{maxRetries}. Error: {ex.Message}");
                    if (i < maxRetries - 1) await Task.Delay(delayMs);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GetGuidCensusMapAsync] CRITICAL ERROR reading or parsing {fileName}. Error: {ex.Message}");
                    break; 
                }
            }

            Console.WriteLine($"[GetGuidCensusMapAsync] CRITICAL ERROR: All retry attempts failed for {fileName}. Caching empty map.");
            lock (_cacheLock) { _guidCensusCache = new CacheItem<Dictionary<string, string>>(censusMap, TimeSpan.FromMinutes(cacheDurationMinutes)); } 
            return censusMap;
        }

        private async Task<IpApiDetails> GetIpDetailsAsync(string ip)
        {
            string ipAddress = ip.Contains("::ffff:") ? ip.Substring(ip.LastIndexOf(':') + 1) : ip.Split(':')[0];

            if (IpCache.TryGetValue(ipAddress, out var cachedItem) && !cachedItem.IsExpired)
            {
                return cachedItem.Data;
            }

            // --- Throttle Logic ---
            lock (_ipApiLock)
            {
                var now = DateTime.UtcNow;
                if (now - _lastIpApiCall < _ipApiDelay)
                {
                    Console.WriteLine($"[GetIpDetailsAsync] THROTTLED: Call for {ipAddress} blocked. Next call allowed after {_ipApiDelay.TotalSeconds:F0}s.");
                    return new IpApiDetails { status = "throttled" }; 
                }
                _lastIpApiCall = now; 
            }
            // --- End Lock ---

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var response = await HttpClient.GetStringAsync($"http://ip-api.com/json/{ipAddress}");
                stopwatch.Stop();
                var details = JsonSerializer.Deserialize<IpApiDetails>(response);

                if (details?.status == "success")
                {
                    Console.WriteLine($"[GetIpDetailsAsync] API SUCCESS for {ipAddress} ({stopwatch.ElapsedMilliseconds}ms): City={details.city}, Lat={details.lat}, Lon={details.lon}");
                    _ipApiDelay = TimeSpan.FromSeconds(1); 
                    IpCache[ipAddress] = new CacheItem<IpApiDetails>(details);
                    return details;
                }
                else
                {
                    var newDelay = _ipApiDelay.TotalSeconds * 2;
                    Console.WriteLine($"[GetIpDetailsAsync] API FAILED for {ipAddress} ({stopwatch.ElapsedMilliseconds}ms): {details?.status}. Doubling delay to {newDelay}s.");
                    _ipApiDelay *= 2; 
                }
            }
            catch (HttpRequestException ex)
            {
                stopwatch.Stop();
                var newDelay = _ipApiDelay.TotalSeconds * 2;
                Console.WriteLine($"[GetIpDetailsAsync] HTTP Error for {ipAddress} ({stopwatch.ElapsedMilliseconds}ms): {ex.Message}. Doubling delay to {newDelay}s.");
                _ipApiDelay *= 2;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                var newDelay = _ipApiDelay.TotalSeconds * 2;
                Console.WriteLine($"[GetIpDetailsAsync] General error for {ipAddress} ({stopwatch.ElapsedMilliseconds}ms): {ex.Message}. Doubling delay to {newDelay}s.");
                _ipApiDelay *= 2;
            }

            return new IpApiDetails { status = "fail" }; 
        }

        private static string GetGuid(string name, string country, string instrument)
        {
            using var md5 = MD5.Create();
            var input = $"{name}{country}{instrument}";
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private static double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371; 
            var dLat = ToRadians(lat2 - lat1);
            var dLon = ToRadians(lon2 - lon1);
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                    Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }

        private static double ToRadians(double angle) => (Math.PI / 180.0) * angle;
    }
}
