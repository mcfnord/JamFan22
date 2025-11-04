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

public class MusicianFinder
{
    private static readonly HttpClient HttpClient = new HttpClient();
    private static readonly ConcurrentDictionary<string, CacheItem<IpApiDetails>> IpCache = new ConcurrentDictionary<string, CacheItem<IpApiDetails>>();
    
    // --- Caching Fields ---
    private static CacheItem<Dictionary<string, double>> _accruedTimeCache;
    private static CacheItem<Dictionary<string, DateTime>> _lastSeenMapCache;
    private static CacheItem<List<PredictedRecord>> _predictedUsersCache;
    private static CacheItem<MusicianDataCache> _musicianDataCache;
    private static readonly object _cacheLock = new object();
    
    // --- 24-Hour Blocklist Fields ---
    // Tracks when a user *first* appeared in a continuous live session
    private static readonly ConcurrentDictionary<string, DateTime> _liveUserFirstSeen = new ConcurrentDictionary<string, DateTime>();
    // Users who have been live > 24h are added here for the app's lifetime
    private static readonly ConcurrentDictionary<string, byte> _sessionBlocklist = new ConcurrentDictionary<string, byte>();
    
    // --- Promotion Hack Field ---
    // Maps a new, untrusted GUID (Key) to an old, trusted GUID (Value)
    private static readonly ConcurrentDictionary<string, string> _promotedGuidMap = new ConcurrentDictionary<string, string>();
    
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
        
        // Constructor for variable durations (e.g., seconds)
        public CacheItem(T data, TimeSpan duration) 
        { 
            Data = data; 
            Expiration = DateTime.UtcNow.Add(duration); 
        }
        
        // Original constructor, defaults to 1 hour (used by IpCache)
        public CacheItem(T data, int hours = 1) 
        { 
            Data = data; 
            Expiration = DateTime.UtcNow.AddHours(hours); 
        } 
        public bool IsExpired => DateTime.UtcNow > Expiration; 
    }

    private class PredictedRecord { public DateTime PredictedArrivalTime { get; set; } public string Guid { get; set; } public string Name { get; set; } }
    
    private class MusicianRecord
    {
        public long MinutesSinceEpoch { get; set; }
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
    }
    
    private class UserStats
    {
        public int TotalEntries { get; set; } = 0;
        public int EntriesWithInferredIp { get; set; } = 0;
        public MusicianRecord MostRecentRecord { get; set; } = null;
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
        var rawLiveGuids = await GetLiveGuidsFromApiAsync(); // This is the unfiltered "live" list
        var lastSeenMap = await GetLastSeenMap(); 
        var accruedTimeMap = await GetAccruedTimeMap(); 
        var predictedUsers = GetPredictedUsers();

        // Get stats map early for logging names
        var fullStatsMap = GetMusicianDataFromCsv().FullStatsMap;

        // --- NEW: 1. CLEANUP ---
        // Clean up promotions for users who have left
        var usersWhoLeft = _promotedGuidMap.Keys.Except(rawLiveGuids).ToList();
        foreach (var leftGuid in usersWhoLeft)
        {
            _promotedGuidMap.TryRemove(leftGuid, out _);
        }
        // -------------------------

        // --- NEW 24-HOUR BLOCKLIST LOGIC (No Grace Period) ---
        var now = DateTime.UtcNow;

        // 1. Update first-seen timestamps and check for 24-hour violations
        foreach (var guid in rawLiveGuids)
        {
            if (_sessionBlocklist.ContainsKey(guid))
            {
                // Use formatted log message
                string name = "Unknown";
                if (fullStatsMap.TryGetValue(guid, out var stats) && stats?.MostRecentRecord != null)
                {
                    name = stats.MostRecentRecord.Name;
                }
                string userInfo = $"{guid} ({name})";
                Console.WriteLine($"{userInfo,-60} is PERMANENTLY HIDDEN (live > 24h).");
                continue; // Already blocklisted, nothing to do.
            }

            // If this is the first time we've seen them in this session, add them.
            _liveUserFirstSeen.TryAdd(guid, now);
            
            // Check if their first-seen time is older than 24 hours
            if (_liveUserFirstSeen.TryGetValue(guid, out var firstSeenTime))
            {
                if (now - firstSeenTime > TimeSpan.FromHours(24))
                {
                    Console.WriteLine($"{guid} has been live for over 24 hours. Adding to session blocklist.");
                    _sessionBlocklist.TryAdd(guid, 1);
                    _liveUserFirstSeen.TryRemove(guid, out _); // Remove from tracking
                }
            }
        }

        // 2. Clean up _liveUserFirstSeen: remove users who are no longer live
        var usersWhoLoggedOff = _liveUserFirstSeen.Keys.Except(rawLiveGuids).ToList();
        foreach (var guid in usersWhoLoggedOff)
        {
            _liveUserFirstSeen.TryRemove(guid, out _); // Reset their 24h timer
        }

        // 3. Create the *filtered* liveGuids list, excluding blocklisted users
        var liveGuids = rawLiveGuids.Where(g => !_sessionBlocklist.ContainsKey(g)).ToHashSet();
        // --- END NEW LOGIC ---


        // Get users seen in the last 60 minutes.
        var recentGuids = lastSeenMap.Where(kvp => kvp.Value > DateTime.UtcNow.AddMinutes(-60))
            .Select(kvp => kvp.Key)
            .Where(g => !_sessionBlocklist.ContainsKey(g)) // Filter here
            .ToHashSet();
        
        const double minLiveTime = 10.0;
        const double minRecentTime = 30.0;

        // Filter live users: must have >= 10 minutes accrued.
        var eligibleLiveGuids = liveGuids
            .Where(g => accruedTimeMap.GetValueOrDefault(g, 0) >= minLiveTime)
            .ToHashSet();

        // Filter recent users: must have >= 30 minutes accrued.
        var eligibleRecentGuids = recentGuids
            .Where(g => accruedTimeMap.GetValueOrDefault(g, 0) >= minRecentTime)
            .ToHashSet();
        
        // The final list of GUIDs to show (before predictions) is the union of these two filtered lists.
        var finalGuids = eligibleLiveGuids.Union(eligibleRecentGuids).ToHashSet();
        
        // --- NEW: 2. INJECT ---
        // Force the system to load the old, trusted GUIDs for any live, promoted users
        foreach (var oldTrustedGuid in _promotedGuidMap.Values)
        {
            finalGuids.Add(oldTrustedGuid);
        }
        // ------------------------

        // Filter predictions against the blocklist too.
        var finalPredictedUsers = predictedUsers
            .Where(p => !finalGuids.Contains(p.Guid))
            .Where(p => !_sessionBlocklist.ContainsKey(p.Guid)) // Filter here
            .ToList();

        var predictedGuids = finalPredictedUsers.Select(p => p.Guid).ToHashSet();
        var combinedGuidsForLookup = finalGuids.Union(predictedGuids).ToHashSet();

        if (!combinedGuidsForLookup.Any())
            return "<table><tr><td>No musicians found matching all criteria.</td></tr></table>";

        // This now gets all data from cache and filters it fast
        var musicianRecords = GetMusicianRecords(combinedGuidsForLookup, userLat, userLon);
        
        var predictedUserDataMap = finalPredictedUsers.ToDictionary(p => p.Guid);

        var completedMusicianRecords = new List<MusicianRecord>();
        
        // --- This is the ORIGINAL loop that fetches IP data for trusted users ---
        foreach (var record in musicianRecords)
        {
            record.Location = await GetIpDetailsAsync(record.IpAddress);
            record.UserCity = CleanAndFormatUserCity(record.UserCity, record.Location?.regionName);

            if (predictedUserDataMap.TryGetValue(record.Guid, out var predictedData))
            {
                record.IsPredicted = true;
                record.PredictedArrivalTime = predictedData.PredictedArrivalTime;
            }
            else
            {
                record.IsPredicted = false;
                record.LastSeen = lastSeenMap.TryGetValue(record.Guid, out var seenDate) ? seenDate : DateTime.UtcNow;
            }
            completedMusicianRecords.Add(record);
        }

        // --- NEW "GHOST PROMOTION" HACK (Stateful) ---
        // Build a map of ALL live users (even untrusted ones) from their historical data
        var liveUserRecords = new Dictionary<string, MusicianRecord>();
        foreach (var liveGuid in rawLiveGuids) 
        {
            if (fullStatsMap.TryGetValue(liveGuid, out var stats) && stats.MostRecentRecord != null) 
            {
                liveUserRecords[liveGuid] = stats.MostRecentRecord;
            }
        }

        // We create a new list for the final output
        var finalProcessedRecords = new List<MusicianRecord>(); 
        
        foreach (var record in completedMusicianRecords)
        {
            bool isLive = rawLiveGuids.Contains(record.Guid);
            
            // Skip if it's already live or predicted
            if (record.IsPredicted || isLive)
            {
                finalProcessedRecords.Add(record);
                continue; 
            }

            // --- We have a gray record. See if it's a "ghost" ---
            bool wasRevived = false;
            foreach (var liveUser in liveUserRecords.Values)
            {
                // Check for Name + UserCity match
                bool isNameMatch = liveUser.Name == record.Name;
                bool isCityMatch = liveUser.UserCity == record.UserCity;

                // --- VALIDATION (Your Fix) ---
                // A match is only valid if the name and city are *not* empty or default placeholders.
                bool hasValidName = isNameMatch && 
                                    !string.IsNullOrWhiteSpace(liveUser.Name) && 
                                    liveUser.Name.Trim() != "No Name" &&
                                    liveUser.Name.Trim() != "-";
                
                bool hasValidCity = isCityMatch && 
                                    !string.IsNullOrWhiteSpace(liveUser.UserCity) && 
                                    liveUser.UserCity.Trim() != "-";
                // --- END VALIDATION ---

                if (hasValidName && hasValidCity)
                {
                    // Found a live user with the same name AND city.
                    Console.WriteLine($"HACK: Reviving gray {record.Name} ({record.Guid}) via City match to live user {liveUser.Guid}.");
                    
                    // --- NEW: 3. SAVE ---
                    // Save this promotion for future runs
                    _promotedGuidMap.TryAdd(liveUser.Guid, record.Guid);
                    // --------------------
                    
                    // Mutate the 'record' to become the 'liveUser'
                    record.Instrument = liveUser.Instrument; // Overwrite instrument
                    record.Guid = liveUser.Guid; // This is the key: makes BuildMusicianRowHtml see it as "live"
                    
                    wasRevived = true;
                    finalProcessedRecords.Add(record); // Add the *revived* record
                    break; // Stop searching for live matches
                }
            } // end inner loop (live users)

            if (!wasRevived)
            {
                // This was a "real" gray record, not a ghost. Add it normally.
                finalProcessedRecords.Add(record);
            }
        }
        // --- END OF HACK ---

        // We pass the new, "hacked" list, the original 'rawLiveGuids', and the 'first seen' map
        return BuildHtmlTable(finalProcessedRecords, rawLiveGuids, _liveUserFirstSeen);
    }

    private static string GetArrivalTimeDisplayString(DateTime arrivalTime)
    {
        double totalMinutes = (arrivalTime - DateTime.UtcNow).TotalMinutes;
        if (totalMinutes <= 0) return "DUE";
        if (totalMinutes <= 15) return "soon";
        if (totalMinutes <= 40) return "½ hour";
        if (totalMinutes <= 75) return "1 hour";
        return "2 hours";
    }

    private string BuildMusicianRowHtml(MusicianRecord record, HashSet<string> liveGuids, ConcurrentDictionary<string, DateTime> firstSeenMap)
    {
        bool isLive = liveGuids.Contains(record.Guid);
        
        if (isLive)
        {
            record.IsPredicted = false; // Cannot be predicted if already live
        }

        var sb = new StringBuilder();
        string bulbClass;
        string rowClass = "";
        string locationDisplay;
        
        // --- Time Display Logic ---
        string timeDisplay = "";
        if (isLive && firstSeenMap.TryGetValue(record.Guid, out var firstSeenTime))
        {
            var duration = DateTime.UtcNow - firstSeenTime;
            if (duration.TotalMinutes >= 1)
            {
                timeDisplay = "<span class='sub-line-time'>";
                if (duration.TotalHours >= 1)
                {
                    timeDisplay += $"{(int)duration.TotalHours}h ";
                }
                timeDisplay += $"{duration.Minutes}m</span>";
            }
        }
        // -----------------------------
        
        if (record.IsPredicted)
        {
            bulbClass = "orange";
            locationDisplay = $"<span class='sub-line'>{GetArrivalTimeDisplayString(record.PredictedArrivalTime)}</span>";
        }
        else
        {
            rowClass = isLive ? "" : " class='not-here'";
            bulbClass = isLive ? "green" : "gray";
            string ipDerivedRegion = record.Location?.regionName ?? "";
            string regionHtml = $"<span class='sub-line'>{ipDerivedRegion}</span>";
            bool hasValidCity = !string.IsNullOrWhiteSpace(record.UserCity) && record.UserCity.Trim() != "-";
            bool cityIsDifferentFromRegion = !string.Equals(record.UserCity.Trim(), ipDerivedRegion.Trim(), StringComparison.OrdinalIgnoreCase);

            if (hasValidCity && cityIsDifferentFromRegion)
                locationDisplay = $"{record.UserCity},<br/>{regionHtml}";
            else
                locationDisplay = regionHtml;
        }

        // --- NEW HTML STRUCTURE ---
        // 1. Build the instrument part with the new class
        string instrumentHtml = !string.IsNullOrWhiteSpace(record.Instrument) && record.Instrument.Trim() != "-"
            ? $"<span class='sub-line-instrument'>{record.Instrument}</span>"
            : "<span></span>"; // Add empty span to ensure flex alignment works

        // 2. Combine instrument and time in the new container
        string sublineContainer = $"<div class='sub-line-container'>{instrumentHtml}{timeDisplay}</div>";

        // 3. Build the final HTML
        sb.AppendLine($"        <tr{rowClass}>");
        // --- MODIFICATION: Added class='musician-info' ---
        sb.AppendLine($"          <td class='col-musician'><div class='musician-cell-content'><span class='bulb {bulbClass}'></span><div class='musician-info'>{record.Name}{sublineContainer}</div></div></td>");
        sb.AppendLine($"          <td class='col-location'>{locationDisplay}</td>");
        sb.AppendLine("        </tr>");
        
        return sb.ToString();
    }
    
    private string BuildHtmlTable(IEnumerable<MusicianRecord> records, HashSet<string> liveGuids, ConcurrentDictionary<string, DateTime> firstSeenMap)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<style>");
        sb.AppendLine("  table, th, td { border: 1px solid #ccc; border-collapse: collapse; table-layout: fixed; }");
        sb.AppendLine("  th, td { padding: 4px; text-align: left; word-wrap: break-word; vertical-align: middle; }");
        sb.AppendLine("  .bulb { height: 12px; width: 12px; border-radius: 50%; display: inline-block; margin-right: 8px; flex-shrink: 0; }");
        sb.AppendLine("  .green { background-color: #28a745; }");
        sb.AppendLine("  .gray { background-color: #adb5bd; }");
        sb.AppendLine("  .orange { background-color: #fd7e14; }");
        sb.AppendLine("  .not-here { color: #6c757d; }");
        sb.AppendLine("  summary { cursor: pointer; color: #007bff; padding: 8px; }");
        sb.AppendLine("  .col-musician { width: 55%; }");
        sb.AppendLine("  .col-location { width: 45%; }");
        
        // --- UPDATED CSS for sub-lines ---
        sb.AppendLine("  .sub-line { font-size: 0.85em; font-family: sans-serif-condensed, Arial Narrow, sans-serif; color: #555; }"); // For location
        sb.AppendLine("  .sub-line-container { display: flex; justify-content: space-between; align-items: baseline; }");
        sb.AppendLine("  .sub-line-instrument { font-size: 0.85em; font-family: sans-serif-condensed, Arial Narrow, sans-serif; color: #555; }");
        sb.AppendLine("  .sub-line-time { font-size: 0.8em; font-family: sans-serif-condensed, Arial Narrow, sans-serif; color: #777; font-style: italic; margin-left: 4px; }");
        // ---------------------------------

        sb.AppendLine("  .musician-cell-content { display: flex; align-items: center; }");
        // --- ADDED a class to make the info block fill the cell ---
        sb.AppendLine("  .musician-info { flex-grow: 1; }");
        sb.AppendLine("</style>");

        sb.AppendLine("<table style='width: 100%;' class='musician-table'><tbody>");

        var orderedRecords = records
            .OrderBy(r => r.DistanceKm)
            .GroupBy(r => $"{r.Name}|{r.Location?.city}|{r.Location?.regionName}")
            .Select(g => g.FirstOrDefault(r => liveGuids.Contains(r.Guid)) ?? g.FirstOrDefault(r => r.IsPredicted) ?? g.First())
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
            sb.Append(BuildMusicianRowHtml(record, liveGuids, firstSeenMap));
        
        if (distantRecords.Any())
        {
            sb.AppendLine("<tr><td colspan='2' style='padding: 0; border: none;'><details>");
            sb.AppendLine($"<summary>{distantRecords.Count} more</summary>");
            sb.AppendLine("<table style='width: 100%; border-collapse: collapse;' class='musician-table'><tbody>");
            foreach (var record in distantRecords)
                sb.Append(BuildMusicianRowHtml(record, liveGuids, firstSeenMap));
            sb.AppendLine("</tbody></table></details></td></tr>");
        }
        
        sb.AppendLine("</tbody></table>");

        foreach (var record in orderedRecords)
        {
            if (liveGuids.Contains(record.Guid)) record.IsPredicted = false;
            
            string musicianPart = !string.IsNullOrWhiteSpace(record.Instrument) && record.Instrument.Trim() != "-"
                ? $"{record.Name} ({record.Instrument})" : record.Name;
            string status = record.IsPredicted ? "PREDICTED" : (liveGuids.Contains(record.Guid) ? "NOW" : "GONE");
            string detailsPart;
            if(record.IsPredicted)
            {
                detailsPart = $"Displays \"{GetArrivalTimeDisplayString(record.PredictedArrivalTime)}\"";
            }
            else
            {
                string ipDerivedRegion = record.Location?.regionName ?? "Unknown Region";
                detailsPart = !string.IsNullOrWhiteSpace(record.UserCity) && record.UserCity.Trim() != "-" && 
                                !string.Equals(record.UserCity.Trim(), ipDerivedRegion.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? $"{record.UserCity}, {ipDerivedRegion}"
                    : ipDerivedRegion;
            }
            Console.WriteLine($"{record.Guid} {status,-9} {musicianPart,-35} -> {detailsPart}");
        }
        return sb.ToString();
    }

    private string CleanAndFormatUserCity(string userCity, string? ipRegion)
    {
        if (string.IsNullOrWhiteSpace(userCity)) return userCity;
        
        string cleanedCity = userCity;
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
        // --- CACHE CHECK ---
        if (_musicianDataCache != null && !_musicianDataCache.IsExpired)
        {
            return _musicianDataCache.Data;
        }
        // -----------------
        
        var allGoldenGuids = new HashSet<string>();
        var fullStatsMap = new Dictionary<string, UserStats>();
        const int cacheDurationSeconds = 30; // Cache for 30 seconds

        try
        {
            var lines = File.ReadAllLines("join-events.csv");
            foreach (var line in lines)
            {
                var fields = line.Split(',');
                if (fields.Length < 13) continue; 

                var guid = fields[2].Trim();
                
                // Check fields[12] (13th column), not fields[13]
                bool isGolden = fields.Length > 12 && fields[12].Trim() != "0";
                if (isGolden)
                {
                    allGoldenGuids.Add(guid);
                }

                // Get or create stats for *every* GUID in the file
                if (!fullStatsMap.TryGetValue(guid, out UserStats stats))
                {
                    stats = new UserStats();
                    fullStatsMap[guid] = stats;
                }
                
                stats.TotalEntries++;
                
                if (!string.IsNullOrWhiteSpace(fields[11]) && fields[11].Trim() != "-")
                {
                    stats.EntriesWithInferredIp++;
                }

                if (long.TryParse(fields[0].Trim(), out long minutes) &&
                    double.TryParse(fields[9].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double lat) &&
                    double.TryParse(fields[10].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double lon) &&
                    !string.IsNullOrWhiteSpace(fields[11].Trim()) && fields[11].Trim() != "-")
                {
                    if (stats.MostRecentRecord == null || minutes > stats.MostRecentRecord.MinutesSinceEpoch)
                    {
                        stats.MostRecentRecord = new MusicianRecord
                        {
                            MinutesSinceEpoch = minutes,
                            Guid = guid,
                            Name = WebUtility.UrlDecode(fields[3].Trim()),
                            Instrument = fields[4].Trim(),
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
        
        // --- CACHE UPDATE ---
        lock (_cacheLock) { _musicianDataCache = new CacheItem<MusicianDataCache>(cacheData, TimeSpan.FromSeconds(cacheDurationSeconds)); }
        return cacheData;
    }

    private List<MusicianRecord> GetMusicianRecords(HashSet<string> guidsToFind, double userLat, double userLon)
    {
        const double inferredIpRatioThreshold = 0.34;

        // Get all stats from the cache
        var cachedData = GetMusicianDataFromCsv();
        var allGoldenGuids = cachedData.AllGoldenGuids;
        var fullStatsMap = cachedData.FullStatsMap;

        var finalRecords = new List<MusicianRecord>();
        foreach (var guid in guidsToFind)
        {
            // Get the pre-computed stats for this specific GUID
            if (!fullStatsMap.TryGetValue(guid, out var stats) || stats.MostRecentRecord == null)
            {
                continue;
            }
            
            bool hasGoldenMatchEver = allGoldenGuids.Contains(guid);
            bool passesInferredIpTest = false;

            if (!hasGoldenMatchEver)
            {
                // Create a left-aligned, padded string for the user info
                string userInfo = $"{guid} ({stats.MostRecentRecord.Name})";

                // NEW RULE: Must have more than 1 entry to even be considered
                if (stats.TotalEntries > 1) 
                {
                    // Original Rule: Must pass ratio test
                    double ratio = (double)stats.EntriesWithInferredIp / stats.TotalEntries;
                    
                    if (ratio >= inferredIpRatioThreshold) 
                    {
                        passesInferredIpTest = true;
                        // PASSED: Met both backup criteria
                        Console.WriteLine($"{userInfo,-60} PASSED. No 'golden match'. (Entries: {stats.TotalEntries} > 1, IP Log Rate: {ratio:P0} >= {inferredIpRatioThreshold:P0})");
                    }
                    else
                    {
                        // FAILED: Failed on ratio
                        Console.WriteLine($"{userInfo,-60} FAILED. No 'golden match'. (Entries: {stats.TotalEntries} > 1, but IP Log Rate: {ratio:P0} < {inferredIpRatioThreshold:P0})");
                    }
                }
                else
                {
                    // FAILED: Failed on new entry count rule
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
        // Note: This data comes from 'JamFan22.Pages.IndexModel.LastReportedList'
        // which is assumed to be its own in-memory cache managed by another part
        // of the application. No additional file I/O caching is added here.
        
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
        // --- CACHE CHECK ---
        if (_predictedUsersCache != null && !_predictedUsersCache.IsExpired)
        {
            return _predictedUsersCache.Data;
        }
        // -----------------

        var predictedUsers = new List<PredictedRecord>();
        const string fileName = "predicted.csv";
        const int cacheDurationSeconds = 30; // Cache for 30 seconds

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
                var fields = line.Split(new[] { ',' }, 3);
                if (fields.Length != 3) continue;

                if (long.TryParse(fields[0].Trim(), out long minutesSinceEpoch))
                {
                    var arrivalTime = epoch.AddMinutes(minutesSinceEpoch);
                    if (arrivalTime > tenMinutesAgo && arrivalTime <= threeHoursFromNow)
                    {
                        predictedUsers.Add(new PredictedRecord
                        {
                            PredictedArrivalTime = arrivalTime,
                            Guid = fields[1].Trim(),
                            Name = WebUtility.UrlDecode(fields[2].Trim())
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
        // --- CACHE CHECK ---
        if (_lastSeenMapCache != null && !_lastSeenMapCache.IsExpired)
        {
            return _lastSeenMapCache.Data;
        }
        // -----------------

        var lastSeenMap = new Dictionary<string, DateTime>();
        const string fileName = "timeTogetherLastUpdates.json";
        const int cacheDurationSeconds = 30; // Cache for 30 seconds
        
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
                lock (_cacheLock) { _lastSeenMapCache = new CacheItem<Dictionary<string, DateTime>>(lastSeenMap, TimeSpan.FromSeconds(cacheDurationSeconds)); } // Cache failure
                return lastSeenMap;
            }
        }

        Console.WriteLine($"[GetLastSeenMap] CRITICAL ERROR: All {maxRetries} retry attempts failed for {fileName}.");
        lock (_cacheLock) { _lastSeenMapCache = new CacheItem<Dictionary<string, DateTime>>(lastSeenMap, TimeSpan.FromSeconds(cacheDurationSeconds)); } // Cache failure
        return lastSeenMap;
    }

    private async Task<Dictionary<string, double>> GetAccruedTimeMap()
    {
        // --- CACHE CHECK ---
        if (_accruedTimeCache != null && !_accruedTimeCache.IsExpired)
        {
            return _accruedTimeCache.Data;
        }
        // -----------------

        var accruedTime = new Dictionary<string, double>();
        const string fileName = "timeTogether.json";
        const int cacheDurationSeconds = 30; // Cache for 30 seconds

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
                    
                    // The value is a TimeSpan string (e.g., "161.17:25:18.7955569"), not a simple double.
                    if (TimeSpan.TryParse(record.Value, CultureInfo.InvariantCulture, out TimeSpan duration))
                    {
                        string guidA = record.Key.Substring(0, 32);
                        string guidB = record.Key.Substring(32, 32);
                        
                        // Use the TotalMinutes from the parsed TimeSpan
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
                return accruedTime; // Success!
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
                lock (_cacheLock) { _accruedTimeCache = new CacheItem<Dictionary<string, double>>(accruedTime, TimeSpan.FromSeconds(cacheDurationSeconds)); } // Cache failure
                return accruedTime; // Fail fast
            }
        }
        
        Console.WriteLine($"[GetAccruedTimeMap] CRITICAL ERROR: All {maxRetries} retry attempts failed for {fileName}.");
        lock (_cacheLock) { _accruedTimeCache = new CacheItem<Dictionary<string, double>>(accruedTime, TimeSpan.FromSeconds(cacheDurationSeconds)); } // Cache failure
        return accruedTime;
    }

    private async Task<IpApiDetails> GetIpDetailsAsync(string ip)
    {
        string ipAddress = ip.Contains("::ffff:") ? ip.Substring(ip.LastIndexOf(':') + 1) : ip.Split(':')[0];

        if (IpCache.TryGetValue(ipAddress, out var cachedItem) && !cachedItem.IsExpired)
        {
            return cachedItem.Data;
        }

        // --- New Throttle Logic ---
        lock (_ipApiLock)
        {
            var now = DateTime.UtcNow;
            if (now - _lastIpApiCall < _ipApiDelay)
            {
                Console.WriteLine($"[GetIpDetailsAsync] THROTTLED: Call for {ipAddress} blocked. Next call allowed after {_ipApiDelay.TotalSeconds:F0}s.");
                return new IpApiDetails { status = "throttled" }; // Return a custom "failed" status
            }
            _lastIpApiCall = now; // Mark this thread as the one making the call
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
                _ipApiDelay = TimeSpan.FromSeconds(1); // Reset backoff on success
                IpCache[ipAddress] = new CacheItem<IpApiDetails>(details);
                return details;
            }
            else
            {
                // API returned "fail", "invalid query", etc.
                var newDelay = _ipApiDelay.TotalSeconds * 2;
                Console.WriteLine($"[GetIpDetailsAsync] API FAILED for {ipAddress} ({stopwatch.ElapsedMilliseconds}ms): {details?.status}. Doubling delay to {newDelay}s.");
                _ipApiDelay *= 2; // Double the delay
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

        return new IpApiDetails { status = "fail" }; // Return a generic failed status
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
        const double R = 6371; // Earth's mean radius in kilometers
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