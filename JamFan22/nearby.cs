// NEARBY MUSICIANS — MusicianFinder eligibility rules
//
// A player appears in the nearby panel if ALL of the following are true:
//   - >= 10 min accrued time (EncounterTracker.m_timeTogether) if currently live,
//     or >= 30 min if recently gone (within 60 min)
//   - EncounterTracker accrues time on ALL public directory servers, not just fleet servers.
//     A player on any public server (e.g. bluemuse.org) can qualify.
//   - Has a record in join-events.csv with a non-empty IP (col 11). No IP = no location
//     = cannot appear, regardless of accrued time.
//   - Quality filter: MaxGoldenValue <= 1 AND >= 94.3% of entries are golden=0
//     => excluded as likely bot/ghost.
//   - Sensor-blocked filter: >= 10 join events AND MaxGoldenValue < 3
//     => excluded as unlocatable (never passed country+city metadata check).

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
        
        // --- Caching Fields ---
        private static CacheItem<Dictionary<string, double>> _accruedTimeCache;
        private static CacheItem<Dictionary<string, DateTime>> _lastSeenMapCache;
        private static CacheItem<List<PredictedRecord>> _predictedUsersCache;
        private static CacheItem<MusicianDataCache> _musicianDataCache;
        private static CacheItem<Dictionary<string, (string name, string instrument)>> _guidCensusCache;
        private static CacheItem<Dictionary<string, string>> _tooltipCache;
        private static CacheItem<Dictionary<string, Dictionary<string, int>>> _guidServerIpsCache;
        private static readonly SemaphoreSlim _guidServerIpsSem = new SemaphoreSlim(1, 1);
        // Cache for join-events anchor IP lookup (GUID → best (ip, strength)). Avoids scanning
        // join-events.csv once per musician in GetGuidInferredRegionAsync.
        private static CacheItem<Dictionary<string, (string ip, int strength)>> _jeAnchorCache;
        private static readonly SemaphoreSlim _jeAnchorSem = new SemaphoreSlim(1, 1);
        private static readonly object _cacheLock = new object();
        private static Dictionary<string, List<string>> _usStateAdjacency;
        private static readonly object _adjacencyLock = new object();
        
        // --- 23-Hour Blocklist Fields ---
        private static readonly ConcurrentDictionary<string, DateTime> _liveUserFirstSeen = new ConcurrentDictionary<string, DateTime>();
        private static readonly ConcurrentDictionary<string, byte> _sessionBlocklist = new ConcurrentDictionary<string, byte>();
        // Permanently excluded: default/blank usernames that should never appear in nearby list
        private static readonly HashSet<string> _permanentExclusions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "efb8576645356ec2b7757a742b6b7e79",
            "4f43e639e0779f19257be072b7846fd5",
            "ec737a7f6bd96c004b2891bd5203043b",
        };
        
        // --- Nearby HTML result cache (per client IP, 90-second TTL) ---
        // Prevents thread pool starvation when multiple browser sessions call /api/nearby simultaneously.
        private static readonly ConcurrentDictionary<string, (string Html, DateTime Expiry)> _nearbyHtmlCache
            = new ConcurrentDictionary<string, (string, DateTime)>();
        private static readonly SemaphoreSlim _nearbyComputeSem = new SemaphoreSlim(1, 1);

        // --- Promotion Hack Field ---
        private static readonly ConcurrentDictionary<string, string> _promotedGuidMap = new ConcurrentDictionary<string, string>();
        
        // --- Prediction Diagnostics ---
        private static readonly ConcurrentDictionary<string, byte> _allTimePredictedGuids = new ConcurrentDictionary<string, byte>();
        private static readonly ConcurrentDictionary<string, byte> _allTimeArrivedGuids = new ConcurrentDictionary<string, byte>();


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
            public string InferredRegion { get; set; }
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
            public HashSet<string> ClientIps { get; set; } = new HashSet<string>();
            public long MostRecentIpTimestamp { get; set; } = 0;
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

            string cacheKey = userIp.Replace("::ffff:", "");
            if (_nearbyHtmlCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.Expiry)
            {
                Console.WriteLine($"[NearbyCache] HIT for {cacheKey}");
                return cached.Html;
            }

            // Serialize computation so concurrent requests don't all run the full pipeline.
            await _nearbyComputeSem.WaitAsync();
            try
            {
                // Re-check after acquiring the lock — another request may have populated the cache.
                if (_nearbyHtmlCache.TryGetValue(cacheKey, out cached) && DateTime.UtcNow < cached.Expiry)
                {
                    Console.WriteLine($"[NearbyCache] HIT (post-lock) for {cacheKey}");
                    return cached.Html;
                }

                var userLocation = await GetIpDetailsAsync(userIp);
                if (userLocation?.status != "success")
                    return $"<table><tr><td>Error: Could not determine your location from IP address {userIp}.</td></tr></table>";

                Console.WriteLine($"Client City: {userLocation.city}");
                string html = await FindMusiciansHtmlAsync(userLocation.lat, userLocation.lon);
                _nearbyHtmlCache[cacheKey] = (html, DateTime.UtcNow.AddSeconds(90));
                return html;
            }
            finally
            {
                _nearbyComputeSem.Release();
            }
        }

        public async Task<string> GeoDiagAsync()
        {
            var rawLiveGuids = await GetLiveGuidsFromApiAsync();
            rawLiveGuids.RemoveWhere(g => HiddenPersonaManager.IsHidden(g));

            var accruedTimeMap = await GetAccruedTimeMap();
            var eligibleGuids = rawLiveGuids
                .Where(g => accruedTimeMap.GetValueOrDefault(g, 0) >= 10.0)
                .ToHashSet();

            var cachedData = GetMusicianDataFromCsv();
            var censusNameMap = await GetGuidCensusMapAsync();

            var rows = new List<(int sortKey, string html)>();
            int pendingGeos = 0;
            int t1FleetSameIp = 0, t1FleetSlash8Agree = 0, t1FleetGeoAgree = 0, t1FleetGeoDiffer = 0;

            foreach (var guid in eligibleGuids)
            {
                // Display name: prefer join-events record, fall back to census
                string displayName = null;
                if (cachedData.FullStatsMap.TryGetValue(guid, out var diagStats) && diagStats.MostRecentRecord != null)
                    displayName = diagStats.MostRecentRecord.Name;
                if (string.IsNullOrWhiteSpace(displayName) && censusNameMap.TryGetValue(guid, out var diagCensus))
                    displayName = diagCensus.name;
                if (string.IsNullOrWhiteSpace(displayName)) continue;

                var (winner, _, _, tier, ipSrc, ipRegion, rawServers, maskedIp, isPending, jeStrength) =
                    await ResolveGuidLocationAsync(guid, cachedData, waitIfThrottled: true);

                if (isPending) pendingGeos++;

                if (tier == "T1" && ipSrc.Contains("+fleet("))
                {
                    if (ipSrc.Contains(",same-ip)"))                  t1FleetSameIp++;
                    else if (ipSrc.Contains(",/8-agree)"))            t1FleetSlash8Agree++;
                    else if (ipSrc.Contains(",/8-differ,geo-agree)")) t1FleetGeoAgree++;
                    else if (ipSrc.Contains(",/8-differ,geo-differ)")) t1FleetGeoDiffer++;
                }

                string tierBg = tier switch {
                    "T1"  => "#d4edda",
                    var t when t.StartsWith("T2") => "#fff3cd",
                    _     => "#e9ecef"
                };
                string name    = System.Net.WebUtility.HtmlEncode(displayName);
                string ipReg   = System.Net.WebUtility.HtmlEncode(ipRegion ?? "-");
                string svrReg  = System.Net.WebUtility.HtmlEncode(rawServers.Length > 0 ? rawServers : "-");
                string winCell = System.Net.WebUtility.HtmlEncode(winner ?? "-");
                string guidShort = guid.Length >= 8 ? guid[..8] : guid;
                string rowHtml = $"<tr style=\"background:{tierBg}\"><td>{name}</td><td>{guidShort}</td><td>{maskedIp}</td><td>{ipSrc}</td><td>{ipReg}</td><td>{svrReg}</td><td><b>{winCell}</b></td><td>{tier}</td></tr>";
                rows.Add((jeStrength, rowHtml));
            }

            var sb = new System.Text.StringBuilder();
            if (pendingGeos > 0)
                sb.Append("<meta http-equiv=\"refresh\" content=\"3; url=?\">");
            sb.Append("<style>body{font-family:monospace;font-size:13px} table{border-collapse:collapse} td,th{border:1px solid #ccc;padding:3px 6px}</style>");
            string statusNote = pendingGeos > 0 ? $" — {pendingGeos} geo pending" : " — complete";
            sb.Append($"<p>{eligibleGuids.Count} eligible live GUIDs — {DateTime.UtcNow:HH:mm:ss} UTC{statusNote}</p>");
            int t1FleetTotal = t1FleetSameIp + t1FleetSlash8Agree + t1FleetGeoAgree + t1FleetGeoDiffer;
            if (t1FleetTotal > 0)
                sb.Append($"<p><b>T1+fleet:</b> {t1FleetSameIp} same-ip, {t1FleetSlash8Agree} /8-agree, {t1FleetGeoAgree} /8-differ,geo-agree, {t1FleetGeoDiffer} /8-differ,geo-differ</p>");
            sb.Append("<p>" +
                "<span style=\"background:#d4edda;padding:2px 8px\">T1</span> join-events IP (strength ≥ 2) &nbsp; " +
                "<span style=\"background:#fff3cd;padding:2px 8px\">T2</span> fleet IP (/ip-allowed, used when je &lt; 2) &nbsp; " +
                "<span style=\"background:#e9ecef;padding:2px 8px\">T3</span> server region (no reliable IP)" +
                "</p>");
            sb.Append("<table><tr><th>Name</th><th>GUID</th><th>IP /8</th><th>IP Src</th><th>IP Region</th><th>Servers Region</th><th>Winner</th><th>Tier</th></tr>");
            foreach (var (_, html) in rows.OrderByDescending(r => r.sortKey))
                sb.Append(html);
            sb.Append("</table>");
            return sb.ToString();
        }

private async Task<string> FindMusiciansHtmlAsync(double userLat, double userLon)
        {
            // Get all data (from cache or file)
            var rawLiveGuids = await GetLiveGuidsFromApiAsync();
            rawLiveGuids.RemoveWhere(g => HiddenPersonaManager.IsHidden(g));

            var lastSeenMap = await GetLastSeenMap();
            var accruedTimeMap = await GetAccruedTimeMap();
            var predictedUsers = GetPredictedUsers();
            predictedUsers.RemoveAll(p => HiddenPersonaManager.IsHidden(p.Guid));

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
                        // bool hasHighZeroRatio = stats.TotalEntries > 0 && ((double)stats.ZeroGoldenCount / stats.TotalEntries >= 0.9375);
                        
                        // Rule 2: Golden/Inferred Entry Requirement
                        bool hasGoldenMatchEver = csvDataDebug.AllGoldenGuids.Contains(pred.Guid);
                        bool passesInferredIpTest = stats.TotalEntries > 1;

                        diagInfo += $"\n    -> STATS: Entries={stats.TotalEntries}, MaxGold={stats.MaxGoldenValue}, ZeroCount={stats.ZeroGoldenCount}";
                        
                        if (hasLowCeiling)
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
                        string fleetFallbackIp = FleetGuidCache.GetBestNonBlockedIpByGuid(pred.Guid);
                        if (fleetFallbackIp != null)
                            diagInfo += $"\n    -> RESULT: [MISSING/FLEET] No join-events entry; fleet fallback IP={fleetFallbackIp} will be used for location.";
                        else
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
                if (_sessionBlocklist.ContainsKey(guid) || _permanentExclusions.Contains(guid))
                {
                    string name = "Unknown"; 
                    if (fullStatsMap.TryGetValue(guid, out var stats) && stats?.MostRecentRecord != null && !string.IsNullOrWhiteSpace(stats.MostRecentRecord.Name))
                    {
                        name = stats.MostRecentRecord.Name; 
                    }
                    else if (censusNameMap.TryGetValue(guid, out var censusEntry))
                    {
                        name = censusEntry.name; 
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

            var liveGuids = rawLiveGuids.Where(g => !_sessionBlocklist.ContainsKey(g) && !_permanentExclusions.Contains(g)).ToHashSet();

            // Keep "Gone" users for 60 minutes, but we will fade them visually
            var recentGuids = lastSeenMap.Where(kvp => kvp.Value > DateTime.UtcNow.AddMinutes(-60))
                .Select(kvp => kvp.Key)
                .Where(g => !_sessionBlocklist.ContainsKey(g) && !_permanentExclusions.Contains(g)) 
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
                .Where(p => !_sessionBlocklist.ContainsKey(p.Guid) && !_permanentExclusions.Contains(p.Guid))
                .Where(p => p.Name == null || p.Name.IndexOf("lobby", StringComparison.OrdinalIgnoreCase) < 0)
                .ToList();

            var predictedGuids = finalPredictedUsers.Select(p => p.Guid).ToHashSet();
            var combinedGuidsForLookup = finalGuids.Union(predictedGuids).ToHashSet();

            if (!combinedGuidsForLookup.Any())
                return "<table><tr><td>No musicians found matching all criteria.</td></tr></table>";

            // Get Data
            var musicianRecords = GetMusicianRecords(combinedGuidsForLookup, userLat, userLon, new Dictionary<string, DateTime>(_liveUserFirstSeen));
            
            var predictedUserDataMap = finalPredictedUsers.ToDictionary(p => p.Guid);

            var completedMusicianRecords = new List<MusicianRecord>();
            
            foreach (var record in musicianRecords)
            {
                var (winner, winLat, winLon, tier, _, _, _, _, _, jeStrength) =
                    await ResolveGuidLocationAsync(record.Guid, csvDataDebug, waitIfThrottled: false);
                record.InferredRegion = winner;
                record.Location = null; // display uses InferredRegion
                record.UserCity = CleanAndFormatUserCity(record.UserCity, winner);
                if (winLat.HasValue) record.Lat = winLat.Value;
                if (winLon.HasValue) record.Lon = winLon.Value;
                Console.WriteLine($"[GeoResolve] {record.Name}: {winner ?? "null"} ({tier}) jeStrength={jeStrength}");

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

            // --- FLEET FALLBACK FOR PREDICTED-ONLY GUIDs ---
            // Predicted users skipped by GetMusicianRecords (no join-events entry) may still have
            // a client IP from fleet-guid-ip.csv. Synthesize a minimal MusicianRecord for them.
            var completedGuids = completedMusicianRecords.Select(r => r.Guid).ToHashSet();
            foreach (var pred in finalPredictedUsers)
            {
                if (completedGuids.Contains(pred.Guid)) continue;
                string fleetIp = FleetGuidCache.GetBestNonBlockedIpByGuid(pred.Guid);
                if (fleetIp == null) continue;
                var fleetLoc = await GetIpDetailsAsync(fleetIp);
                if (fleetLoc == null) continue;
                double dist = CalculateDistance(userLat, userLon, fleetLoc.lat, fleetLoc.lon);
                if (dist >= 5000) continue;
                var synthRecord = new MusicianRecord
                {
                    Guid = pred.Guid,
                    Name = pred.Name,
                    IsPredicted = true,
                    PredictedArrivalTime = pred.PredictedArrivalTime,
                    PredictedServer = pred.Server,
                    Lat = fleetLoc.lat,
                    Lon = fleetLoc.lon,
                    InferredRegion = fleetLoc.regionName,
                    DistanceKm = dist,
                };
                Console.WriteLine($"[FLEET-FALLBACK] Synthesized record for predicted {pred.Name} ({pred.Guid}) via fleet IP={fleetIp} region={fleetLoc.regionName} dist={dist:F0}km");
                completedMusicianRecords.Add(synthRecord);
            }

            // --- FALLBACK FOR LIVE GUIDs DROPPED BY GetMusicianRecords ---
            // Uses the same T1/T2/T3 logic as the main enrichment loop.
            completedGuids = completedMusicianRecords.Select(r => r.Guid).ToHashSet();
            foreach (var liveGuid in eligibleLiveGuids)
            {
                if (completedGuids.Contains(liveGuid)) continue;

                var (liveWinner, liveLat, liveLon, liveTier, _, _, _, _, _, _) =
                    await ResolveGuidLocationAsync(liveGuid, csvDataDebug, waitIfThrottled: false);
                if (!liveLat.HasValue || !liveLon.HasValue) continue;

                double liveDist = CalculateDistance(userLat, userLon, liveLat.Value, liveLon.Value);
                if (liveDist >= 5000) continue;

                censusNameMap.TryGetValue(liveGuid, out var liveCensus);
                string liveName = liveCensus.name;
                if (string.IsNullOrWhiteSpace(liveName)) continue;
                if (liveName.IndexOf("lobby", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                string liveUserCity = string.Empty;
                string liveInstrument = liveCensus.instrument ?? string.Empty;
                if (fullStatsMap.TryGetValue(liveGuid, out var liveStats) && liveStats.MostRecentRecord != null)
                {
                    liveUserCity = liveStats.MostRecentRecord.UserCity ?? string.Empty;
                    if (!string.IsNullOrEmpty(liveStats.MostRecentRecord.Instrument))
                        liveInstrument = liveStats.MostRecentRecord.Instrument;
                }

                var liveSynthRecord = new MusicianRecord
                {
                    Guid = liveGuid,
                    Name = liveName,
                    UserCity = CleanAndFormatUserCity(liveUserCity, liveWinner),
                    Instrument = liveInstrument,
                    IsPredicted = false,
                    LastSeen = DateTime.UtcNow,
                    Lat = liveLat.Value,
                    Lon = liveLon.Value,
                    InferredRegion = liveWinner,
                    DistanceKm = liveDist,
                };
                Console.WriteLine($"[LIVE-FALLBACK] {liveName} ({liveGuid[..8]}) tier={liveTier} region={liveWinner ?? "null"} dist={liveDist:F0}km");
                completedMusicianRecords.Add(liveSynthRecord);
            }
            // -----------------------------------------------

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

// Find the index of the first Live or Predicted user
int firstActiveIndex = orderedRecords.FindIndex(r => liveGuids.Contains(r.Guid) || r.IsPredicted);

// If no active users exist, we check the entire list. Otherwise, we only check up to the first active user.
int trimEnd = firstActiveIndex == -1 ? orderedRecords.Count : firstActiveIndex;

// Iterate backwards to safely remove items without throwing off the index
for (int i = trimEnd - 1; i >= 0; i--)
{
    if ((DateTime.UtcNow - orderedRecords[i].LastSeen).TotalMinutes > 30)
    {
        orderedRecords.RemoveAt(i);
    }
}

            int lastActiveIndex = -1;
            for (int i = orderedRecords.Count - 1; i >= 0; i--)
            {
                if (liveGuids.Contains(orderedRecords[i].Guid) || orderedRecords[i].IsPredicted)
                {
                    lastActiveIndex = i;
                    break;
                }
            }
            int keepCount = Math.Max(3, lastActiveIndex + 1);
            if (keepCount < orderedRecords.Count)
            {
                orderedRecords = orderedRecords.Take(keepCount).ToList();
            }

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

                string ipDerivedRegion = record.Location?.regionName ?? record.InferredRegion ?? "";
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
            sb.AppendLine($"<div class='{rowClass}' data-name=\"{WebUtility.HtmlEncode(record.Name)}\" {tooltipAttr}>");
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
                        var clientIpOnly = fields[11].Trim().Split(':')[0];
                        if (!string.IsNullOrWhiteSpace(clientIpOnly) && clientIpOnly != "-")
                            stats.ClientIps.Add(clientIpOnly);
                        if (long.TryParse(fields[0].Trim(), out long ipMinutes) && ipMinutes > stats.MostRecentIpTimestamp)
                            stats.MostRecentIpTimestamp = ipMinutes;
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

private List<MusicianRecord> GetMusicianRecords(HashSet<string> guidsToFind, double userLat, double userLon, Dictionary<string, DateTime> liveFirstSeen)
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
                else if (string.IsNullOrWhiteSpace(stats.MostRecentRecord.Name))
                {
                    continue;
                }

                // --- NEW FILTER RULE ---
                // Rule: Max Value <= 1 AND >= 93.75% are zeroes
                bool hasLowCeiling = stats.MaxGoldenValue <= 1;
                bool hasHighZeroRatio = stats.TotalEntries > 0 && ((double)stats.ZeroGoldenCount / stats.TotalEntries >= 0.9375);

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

                // Sensor-blocked: >= 10 join events, IP never verified at country+city level (score < 3).
                // Location is a random false correlation; omitting is better than misleading.
                if (stats.TotalEntries >= 10 && stats.MaxGoldenValue < 3 && stats.ClientIps.Count > 3)
                {
                    double zeroRatio = (double)stats.ZeroGoldenCount / stats.TotalEntries;
                    Console.WriteLine($"[SENSOR-BLOCKED] Excluded {guid} ({stats.MostRecentRecord?.Name ?? "?"}). " +
                                      $"Entries={stats.TotalEntries}, EntriesWithIP={stats.EntriesWithInferredIp}, MaxScore={stats.MaxGoldenValue}, " +
                                      $"UniqueIPs={stats.ClientIps.Count}, ZeroRatio={zeroRatio:P0}");
                    continue;
                }

                // For default-named players, require a confirmed IP ping since their arrival in this session.
                // No session-window IP = no location = exclude entirely rather than show stale Wisconsin.
                if (string.Equals(stats.MostRecentRecord.Name, "No Name", StringComparison.OrdinalIgnoreCase)
                    && liveFirstSeen.TryGetValue(guid, out var arrivalUtc))
                {
                    long arrivalMinute = (long)(arrivalUtc - new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMinutes;
                    if (stats.MostRecentIpTimestamp < arrivalMinute)
                    {
                        Console.WriteLine($"[NO-NAME-FILTER] Excluded {guid}: no IP ping since session arrival at minute {arrivalMinute}");
                        continue;
                    }
                }

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
            foreach (var key in JamFan22.Services.JamulusCacheManager.JamulusListURLs.Keys)
            {
                try
                {
                    if (JamFan22.Services.JamulusCacheManager.LastReportedList.TryGetValue(key, out var jsonString) && !string.IsNullOrEmpty(jsonString))
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
            if (Services.EncounterTracker.m_timeTogetherUpdated != null)
            {
                var dict = new Dictionary<string, DateTime>(Services.EncounterTracker.m_timeTogetherUpdated);
                var allUpdates = new List<(string Guid, DateTime LastSeen)>();
                foreach (var kvp in dict)
                {
                    string key = kvp.Key;
                    if (key != null && key.Length == 64)
                    {
                        string guidA = key.Substring(0, 32);
                        string guidB = key.Substring(32, 32);
                        allUpdates.Add((guidA, kvp.Value));
                        allUpdates.Add((guidB, kvp.Value));
                    }
                }
                return allUpdates.GroupBy(u => u.Guid).ToDictionary(g => g.Key, g => g.Max(item => item.LastSeen));
            }
            return new Dictionary<string, DateTime>();
        }

        private async Task<Dictionary<string, double>> GetAccruedTimeMap()
        {
            if (Services.EncounterTracker.m_timeTogether != null)
            {
                var dict = new Dictionary<string, TimeSpan>(Services.EncounterTracker.m_timeTogether);
                var accruedTime = new Dictionary<string, double>();
                foreach (var kvp in dict)
                {
                    string key = kvp.Key;
                    if (key != null && key.Length == 64)
                    {
                        string guidA = key.Substring(0, 32);
                        string guidB = key.Substring(32, 32);
                        double minutes = kvp.Value.TotalMinutes;
                        accruedTime[guidA] = accruedTime.GetValueOrDefault(guidA, 0) + minutes;
                        accruedTime[guidB] = accruedTime.GetValueOrDefault(guidB, 0) + minutes;
                    }
                }
                return accruedTime;
            }
            return new Dictionary<string, double>();
        }

        private async Task<Dictionary<string, (string name, string instrument)>> GetGuidCensusMapAsync()
        {
            if (_guidCensusCache != null && !_guidCensusCache.IsExpired)
            {
                return _guidCensusCache.Data;
            }

            var censusMap = new Dictionary<string, (string name, string instrument)>();
            const string fileName = "data/censusgeo.csv";
            const int cacheDurationMinutes = 1440; // Changed from 5 to 1440 (24 hours) since it's just a fallback for historical names.

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
                            string instr = fields.Length >= 3 ? WebUtility.UrlDecode(fields[2].Trim()) : string.Empty;
                            if (!string.IsNullOrWhiteSpace(guid) && !string.IsNullOrWhiteSpace(name))
                            {
                                censusMap[guid] = (name, instr);
                            }
                        }
                    }
                    
                    Console.WriteLine($"[GetGuidCensusMapAsync] Successfully loaded {censusMap.Count} unique GUIDs from {fileName}.");
                    lock (_cacheLock) { _guidCensusCache = new CacheItem<Dictionary<string, (string name, string instrument)>>(censusMap, TimeSpan.FromMinutes(cacheDurationMinutes)); }
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
            lock (_cacheLock) { _guidCensusCache = new CacheItem<Dictionary<string, (string name, string instrument)>>(censusMap, TimeSpan.FromMinutes(cacheDurationMinutes)); } 
            return censusMap;
        }

        private async Task<IpApiDetails> GetIpDetailsAsync(string ip, bool waitIfThrottled = false)
        {
            string ipAddress = ip.Contains("::ffff:") ? ip.Substring(ip.LastIndexOf(':') + 1) : ip.Split(':')[0];

            var json = await Services.IpAnalyticsService.FetchIpApiAsync(ipAddress, waitIfThrottled: waitIfThrottled);
            if (json == null)
                return new IpApiDetails { status = "throttled" };

            return new IpApiDetails
            {
                status      = json["status"]?.ToString(),
                city        = json["city"]?.ToString(),
                regionName  = json["regionName"]?.ToString(),
                countryCode = json["countryCode"]?.ToString(),
                lat         = (double?)json["lat"] ?? 0,
                lon         = (double?)json["lon"] ?? 0,
            };
        }

        private async Task<Dictionary<string, Dictionary<string, int>>> GetGuidServerIpsAsync()
        {
            lock (_cacheLock)
            {
                if (_guidServerIpsCache != null && !_guidServerIpsCache.IsExpired)
                    return _guidServerIpsCache.Data;
            }

            await _guidServerIpsSem.WaitAsync();
            try
            {
                lock (_cacheLock)
                {
                    if (_guidServerIpsCache != null && !_guidServerIpsCache.IsExpired)
                        return _guidServerIpsCache.Data;
                }

                const string fileName = "data/census.csv";
                var result = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

                try
                {
                    foreach (var line in File.ReadLines(fileName))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var parts = line.Split(',');
                        if (parts.Length < 3) continue;
                        string guid = parts[1].Trim();
                        string serverIpPort = parts[2].Trim();
                        if (string.IsNullOrEmpty(guid) || string.IsNullOrEmpty(serverIpPort)) continue;

                        string serverIp = serverIpPort.Contains(':') ? serverIpPort.Split(':')[0] : serverIpPort;

                        if (!result.TryGetValue(guid, out var ticks))
                        {
                            ticks = new Dictionary<string, int>(StringComparer.Ordinal);
                            result[guid] = ticks;
                        }
                        ticks[serverIp] = ticks.GetValueOrDefault(serverIp) + 1;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GetGuidServerIpsAsync] Error reading {fileName}: {ex.Message}");
                }

                lock (_cacheLock)
                {
                    _guidServerIpsCache = new CacheItem<Dictionary<string, Dictionary<string, int>>>(result, TimeSpan.FromMinutes(10));
                }
                return result;
            }
            finally
            {
                _guidServerIpsSem.Release();
            }
        }

        private async Task<Dictionary<string, (string ip, int strength)>> GetJeAnchorMapAsync()
        {
            lock (_cacheLock)
            {
                if (_jeAnchorCache != null && !_jeAnchorCache.IsExpired)
                    return _jeAnchorCache.Data;
            }
            await _jeAnchorSem.WaitAsync();
            try
            {
                lock (_cacheLock)
                {
                    if (_jeAnchorCache != null && !_jeAnchorCache.IsExpired)
                        return _jeAnchorCache.Data;
                }
                var map = new Dictionary<string, (string ip, int strength)>(StringComparer.Ordinal);
                if (File.Exists("join-events.csv"))
                {
                    foreach (var line in File.ReadLines("join-events.csv"))
                    {
                        var fields = line.Split(',');
                        if (fields.Length < 13) continue;
                        string guid = fields[2].Trim();
                        string clientIp = fields[11].Trim();
                        if (string.IsNullOrWhiteSpace(clientIp) || clientIp == "-") continue;
                        if (!int.TryParse(fields[12].Trim(), out int strength)) continue;
                        if (!map.TryGetValue(guid, out var existing) || strength > existing.strength)
                            map[guid] = (clientIp.Split(':')[0], strength);
                    }
                }
                lock (_cacheLock) { _jeAnchorCache = new CacheItem<Dictionary<string, (string ip, int strength)>>(map, TimeSpan.FromMinutes(10)); }
                Console.WriteLine($"[JeAnchorMap] Loaded {map.Count} GUID anchor entries from join-events.csv");
                return map;
            }
            finally
            {
                _jeAnchorSem.Release();
            }
        }

        private Dictionary<string, List<string>> GetUsStateAdjacency()
        {
            if (_usStateAdjacency != null) return _usStateAdjacency;
            lock (_adjacencyLock)
            {
                if (_usStateAdjacency != null) return _usStateAdjacency;
                try
                {
                    string json = File.ReadAllText("data/us-state-adjacency.json");
                    _usStateAdjacency = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json)
                        ?? new Dictionary<string, List<string>>();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[UsStateAdjacency] Failed to load: {ex.Message}");
                    _usStateAdjacency = new Dictionary<string, List<string>>();
                }
            }
            return _usStateAdjacency;
        }

        private async Task<(string winner, double? lat, double? lon, string tier, string ipSrc, string ipRegion, string rawServers, string maskedIp, bool isPending, int jeStrength)>
            ResolveGuidLocationAsync(string guid, MusicianDataCache cachedData, bool waitIfThrottled)
        {
            string jeIp = null;
            int jeStrength = 0;
            if (cachedData.FullStatsMap.TryGetValue(guid, out var stats) && stats.MostRecentRecord?.IpAddress != null)
            {
                jeIp = stats.MostRecentRecord.IpAddress.Split(':')[0];
                jeStrength = IdentityManager.GetStrengthForIpGuid(jeIp, guid);
            }

            string resolvedIp;
            string ipSrc;
            int fleetDays = 0;
            int tier;

            if (jeStrength >= 2)
            {
                resolvedIp = jeIp;
                ipSrc = $"je({jeStrength})";
                tier = 1;
            }
            else
            {
                var fleetIps = FleetGuidCache.GetAllFleetIpsByGuid(guid);
                if (fleetIps.Count > 0)
                {
                    var best = fleetIps.OrderByDescending(x => x.days).First();
                    resolvedIp = best.ip;
                    fleetDays = best.days;
                    ipSrc = jeIp != null ? $"fleet({fleetDays}d)/je({jeStrength})" : $"fleet({fleetDays}d)";
                    tier = 2;
                }
                else
                {
                    resolvedIp = jeIp; // geolocate for display; winner uses server region
                    ipSrc = jeIp != null ? $"je({jeStrength})" : "-";
                    tier = 3;
                }
            }

            string maskedIp = resolvedIp != null ? $"{resolvedIp.Split('.')[0]}." : "-";
            string ipRegion = null;
            double? ipLat = null, ipLon = null;
            if (resolvedIp != null)
            {
                var loc = await GetIpDetailsAsync(resolvedIp, waitIfThrottled: waitIfThrottled);
                if (loc?.status == "success")
                {
                    ipRegion = loc.regionName;
                    ipLat = loc.lat;
                    ipLon = loc.lon;
                }
            }
            bool isPending = resolvedIp != null && ipRegion == null;

            // T1: annotate fleet IP agreement category (runs after geo so ipRegion is known)
            if (tier == 1)
            {
                var fleetIpsT1 = FleetGuidCache.GetAllFleetIpsByGuid(guid);
                if (fleetIpsT1.Count > 0)
                {
                    var bestT1 = fleetIpsT1.OrderByDescending(x => x.days).First();
                    string fleetCat;
                    if (string.Equals(bestT1.ip, jeIp, StringComparison.Ordinal))
                    {
                        fleetCat = "same-ip";
                    }
                    else if (bestT1.ip.Split('.')[0] == jeIp.Split('.')[0])
                    {
                        fleetCat = "/8-agree";
                    }
                    else
                    {
                        var fleetLoc = await GetIpDetailsAsync(bestT1.ip, waitIfThrottled: waitIfThrottled);
                        string fleetRegion = fleetLoc?.status == "success" ? fleetLoc.regionName : null;
                        bool geoAgree = fleetRegion != null && ipRegion != null &&
                                        string.Equals(fleetRegion, ipRegion, StringComparison.OrdinalIgnoreCase);
                        fleetCat = geoAgree ? "/8-differ,geo-agree" : "/8-differ,geo-differ";
                        if (jeStrength >= 3)
                            Console.WriteLine($"[JE-FLEET-DIVERGE] guid={guid[..8]} je-strength={jeStrength} je-region={ipRegion ?? "null"} fleet-region={fleetRegion ?? "null"} fleet-days={bestT1.days}");
                    }
                    ipSrc += $" +fleet({bestT1.days}d,{fleetCat})";
                }
            }

            var (inferredRegion, _, rawServers, topLat, topLon) = await GetGuidInferredRegionAsync(guid);
            string topServerRegion = null;
            if (!string.IsNullOrEmpty(rawServers))
            {
                var first = rawServers.Split(';')[0].Trim();
                var pi = first.IndexOf(" (");
                topServerRegion = pi > 0 ? first[..pi] : first;
            }

            string winner = tier switch {
                1 or 2 => ipRegion ?? topServerRegion,
                _      => topServerRegion ?? inferredRegion ?? ipRegion
            };

            // Prefer IP-derived lat/lon for T1/T2; fall back to server centroid
            double? lat = (tier <= 2 && ipLat.HasValue) ? ipLat : topLat;
            double? lon = (tier <= 2 && ipLon.HasValue) ? ipLon : topLon;

            string tierLabel = tier switch {
                1 => "T1",
                2 => $"T2 fleet({fleetDays}d)",
                _ => "T3"
            };

            return (winner, lat, lon, tierLabel, ipSrc, ipRegion, rawServers ?? "", maskedIp, isPending, jeStrength);
        }

        private async Task<(string region, string tier, string rawServers, double? topLat, double? topLon)> GetGuidInferredRegionAsync(string guidHash)
        {
            if (string.IsNullOrEmpty(guidHash)) return (null, null, null, null, null);

            // Step 1: Collect server states weighted by tick count (ticks ≈ minutes of presence)
            var guidServerIps = await GetGuidServerIpsAsync();
            if (!guidServerIps.TryGetValue(guidHash, out var serverIpTicks) || serverIpTicks.Count == 0)
                return (null, null, null, null, null);

            var regionTicks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // Track a representative lat/lon per region from the highest-tick server in that region
            var regionBestLatLon = new Dictionary<string, (double lat, double lon, int ticks)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (serverIp, ticks) in serverIpTicks)
            {
                var json = await Services.IpAnalyticsService.FetchIpApiAsync(serverIp);
                if (json == null) continue;
                string region = json["regionName"]?.ToString();
                if (!string.IsNullOrWhiteSpace(region))
                {
                    regionTicks[region] = regionTicks.GetValueOrDefault(region) + ticks;
                    double lat = json["lat"]?.ToObject<double>() ?? 0;
                    double lon = json["lon"]?.ToObject<double>() ?? 0;
                    if (!regionBestLatLon.TryGetValue(region, out var cur) || ticks > cur.ticks)
                        regionBestLatLon[region] = (lat, lon, ticks);
                }
            }
            var serverRegions = new HashSet<string>(regionTicks.Keys, StringComparer.OrdinalIgnoreCase);
            // rawServers: dominant region first, with tick counts
            string rawServers = regionTicks.Count > 0
                ? string.Join("; ", regionTicks.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ({kv.Value})"))
                : null;
            // Centroid of the tick-dominant region — used when IP confidence is too low
            string topRegionFallback = null;
            (double? topLat, double? topLon) = (null, null);
            if (regionTicks.Count > 0)
            {
                var topRegion = regionTicks.OrderByDescending(kv => kv.Value).First().Key;
                topRegionFallback = topRegion;
                if (regionBestLatLon.TryGetValue(topRegion, out var ll))
                    (topLat, topLon) = (ll.lat, ll.lon);
            }

            // Step 2: Find anchor state — highest-strength join-events row with non-empty client IP
            string anchorState = null;
            try
            {
                var jeAnchorMap = await GetJeAnchorMapAsync();
                if (jeAnchorMap.TryGetValue(guidHash, out var anchor))
                {
                    var anchorJson = await Services.IpAnalyticsService.FetchIpApiAsync(anchor.ip);
                    anchorState = anchorJson?["regionName"]?.ToString();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetGuidInferredRegionAsync] anchor lookup error: {ex.Message}");
            }

            // Fleet IP fallback: when join-events has no anchor IP, use most recent non-blocked fleet IP
            if (anchorState == null)
            {
                string fleetFallbackIp = FleetGuidCache.GetBestNonBlockedIpByGuid(guidHash);
                if (fleetFallbackIp != null)
                {
                    try
                    {
                        var ffj = await Services.IpAnalyticsService.FetchIpApiAsync(fleetFallbackIp);
                        string ffRegion = ffj?["regionName"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(ffRegion))
                        {
                            double? ffLat = ffj["lat"]?.ToObject<double>();
                            double? ffLon = ffj["lon"]?.ToObject<double>();
                            Console.WriteLine($"[GetGuidInferredRegionAsync] fleet IP fallback: {guidHash[..8]} ip={fleetFallbackIp} region={ffRegion}");
                            return (ffRegion, "fleet-ip", rawServers, ffLat, ffLon);
                        }
                    }
                    catch (Exception exFleet)
                    {
                        Console.WriteLine($"[GetGuidInferredRegionAsync] fleet IP fallback error: {exFleet.Message}");
                    }
                }
            }

            // Step 3: Fleet anchor — every observation is signal; multiple distinct days = stronger confidence
            string fleetAnchorState = null;
            try
            {
                var fleetIps = FleetGuidCache.GetAllFleetIpsByGuid(guidHash);
                if (fleetIps.Count > 0)
                {
                    var fleetGeo = new List<(string country, string region, int days)>();
                    foreach (var (fip, days) in fleetIps)
                    {
                        var fj = await Services.IpAnalyticsService.FetchIpApiAsync(fip);
                        if (fj == null) continue;
                        string fc = fj["country"]?.ToString();
                        string fr = fj["regionName"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(fr)) fleetGeo.Add((fc, fr, days));
                    }
                    var countries = fleetGeo.Select(g => g.country).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
                    if (countries.Count == 1)
                    {
                        var top = fleetGeo.GroupBy(g => g.region).OrderByDescending(g => g.Sum(x => x.days)).First();
                        fleetAnchorState = top.Key;
                        int totalDays = top.Sum(x => x.days);
                        Console.WriteLine($"[GetGuidInferredRegionAsync] fleet anchor: {fleetAnchorState} ({totalDays} day-obs)");
                    }
                    else if (countries.Count > 1)
                        Console.WriteLine($"[GetGuidInferredRegionAsync] fleet country disagreement for {guidHash}: {string.Join(", ", countries)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetGuidInferredRegionAsync] fleet anchor error: {ex.Message}");
            }

            // Resolve final anchor: prefer agreement; join-events wins on disagreement
            string finalAnchorState;
            string anchorSource;
            if (anchorState != null && fleetAnchorState != null)
            {
                if (string.Equals(anchorState, fleetAnchorState, StringComparison.OrdinalIgnoreCase))
                {
                    finalAnchorState = anchorState;
                    anchorSource = "both";
                    Console.WriteLine($"[GetGuidInferredRegionAsync] anchors agree: {finalAnchorState}");
                }
                else
                {
                    Console.WriteLine($"[GetGuidInferredRegionAsync] anchor disagreement: join-events={anchorState} fleet={fleetAnchorState} — join-events wins");
                    finalAnchorState = anchorState;
                    anchorSource = "join-events";
                }
            }
            else if (anchorState != null)
            {
                finalAnchorState = anchorState;
                anchorSource = "join-events";
            }
            else if (fleetAnchorState != null)
            {
                finalAnchorState = fleetAnchorState;
                anchorSource = "fleet";
            }
            else
            {
                finalAnchorState = null;
                anchorSource = null;
            }

            if (finalAnchorState != null)
            {
                var adjacency = GetUsStateAdjacency();

                var filtered = serverRegions
                    .Where(r => string.Equals(r, finalAnchorState, StringComparison.OrdinalIgnoreCase)
                        || (adjacency.TryGetValue(r, out var neighbors)
                            && neighbors.Any(n => string.Equals(n, finalAnchorState, StringComparison.OrdinalIgnoreCase))))
                    .ToList();

                if (filtered.Count == 1)
                    return (filtered[0], $"anchor-filtered/{anchorSource}", rawServers, topLat, topLon);

                if (filtered.Count >= 2 && filtered.Count <= 3)
                {
                    bool mutuallyAdjacent = true;
                    for (int i = 0; i < filtered.Count && mutuallyAdjacent; i++)
                    {
                        for (int j = i + 1; j < filtered.Count && mutuallyAdjacent; j++)
                        {
                            bool aNeighborsB = adjacency.TryGetValue(filtered[i], out var n1)
                                && n1.Any(n => string.Equals(n, filtered[j], StringComparison.OrdinalIgnoreCase));
                            bool bNeighborsA = adjacency.TryGetValue(filtered[j], out var n2)
                                && n2.Any(n => string.Equals(n, filtered[i], StringComparison.OrdinalIgnoreCase));
                            if (!aNeighborsB || !bNeighborsA)
                                mutuallyAdjacent = false;
                        }
                    }
                    if (mutuallyAdjacent)
                    {
                        var sorted = filtered.OrderBy(r => r).ToList();
                        string combined = sorted.Count == 2
                            ? $"{sorted[0]} or {sorted[1]}"
                            : $"{string.Join(", ", sorted.Take(sorted.Count - 1))}, or {sorted.Last()}";
                        return (combined, $"anchor-adjacent/{anchorSource}", rawServers, topLat, topLon);
                    }
                }

                return (null, null, rawServers, topLat, topLon);
            }

            // No anchor — region is unanimous across all observed servers
            if (serverRegions.Count == 1)
                return (serverRegions.First(), "server-region", rawServers, topLat, topLon);

            // Multiple regions, no anchor — fall back to tick-dominant server region
            return (topRegionFallback, "top-server-region", rawServers, topLat, topLon);
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
