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
    private static readonly object _apiLock = new object();

    private static readonly HashSet<string> UsStateAbbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "FL", "GA",
        "HI", "ID", "IL", "IN", "IA", "KS", "KY", "LA", "ME", "MD",
        "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ",
        "NM", "NY", "NC", "ND", "OH", "OK", "OR", "PA", "RI", "SC",
        "SD", "TN", "TX", "UT", "VT", "VA", "WA", "WV", "WI", "WY",
        "DC"
    };

    /// <summary>
    /// Initializes a new instance of the MusicianFinder.
    /// </summary>
    public MusicianFinder()
    {
    }

    // --- DATA MODELS ---
    private class JamulusServer { public List<ApiClient> clients { get; set; } = new List<ApiClient>(); }
    private class ApiClient { public string name { get; set; } public string country { get; set; } public string instrument { get; set; } public string city { get; set; } }
    private class TimeRecord { public string Key { get; set; } public string Value { get; set; } }
    private class LastUpdateRecordRaw { public string Key { get; set; } public string Value { get; set; } }
    private class IpApiDetails { public string status { get; set; } public string city { get; set; } public string regionName { get; set; } public string countryCode { get; set; } public double lat { get; set; } public double lon { get; set; } }
    private class CacheItem<T> { public T Data { get; } public DateTime Expiration { get; } public CacheItem(T data) { Data = data; Expiration = DateTime.UtcNow.AddHours(1); } public bool IsExpired => DateTime.UtcNow > Expiration; }
    
    private class PredictedRecord
    {
        public DateTime PredictedArrivalTime { get; set; }
        public string Guid { get; set; }
        public string Name { get; set; }
    }

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

    /// <summary>
    /// Finds nearby musicians based on an IP address and returns an HTML table.
    /// </summary>
    public async Task<string> FindMusiciansHtmlAsync(string userIp)
    {
        if (string.IsNullOrWhiteSpace(userIp))
        {
            return "<table><tr><td>Error: IP address cannot be empty.</td></tr></table>";
        }

        var userLocation = await GetIpDetailsAsync(userIp);

        if (userLocation?.status != "success")
        {
            return $"<table><tr><td>Error: Could not determine your location from IP address {userIp}.</td></tr></table>";
        }

        Console.WriteLine($"[Diagnostic] Client City: {userLocation.city}");

        return await FindMusiciansHtmlAsync(userLocation.lat, userLocation.lon);
    }

    /// <summary>
    /// Finds nearby musicians based on coordinates and returns an HTML table.
    /// </summary>
    private async Task<string> FindMusiciansHtmlAsync(double userLat, double userLon)
    {
        const string joinEventsFile = "join-events.csv";
        const string timeTogetherFile = "timeTogether.json";
        const string lastUpdatesFile = "timeTogetherLastUpdates.json";

        if (!File.Exists(joinEventsFile) || !File.Exists(timeTogetherFile) || !File.Exists(lastUpdatesFile))
        {
            var missingFiles = new List<string>();
            if (!File.Exists(joinEventsFile)) missingFiles.Add(joinEventsFile);
            if (!File.Exists(timeTogetherFile)) missingFiles.Add(timeTogetherFile);
            if (!File.Exists(lastUpdatesFile)) missingFiles.Add(lastUpdatesFile);
            return $"<table><tr><td>Error: Required data file(s) not found: {string.Join(", ", missingFiles)}</td></tr></table>";
        }

        var liveGuids = await GetLiveGuidsFromApiAsync();
        var lastSeenMap = GetLastSeenMap();
        var recentGuids = lastSeenMap
            .Where(kvp => kvp.Value > DateTime.UtcNow.AddMinutes(-60))
            .Select(kvp => kvp.Key)
            .ToHashSet();
        var allRelevantGuids = liveGuids.Union(recentGuids).ToHashSet();
        var longSessionGuids = GetLongSessionGuids();
        var finalGuids = allRelevantGuids.Intersect(longSessionGuids).ToHashSet();
        var predictedUsers = GetPredictedUsers();
        var finalPredictedUsers = predictedUsers
            .Where(p => !finalGuids.Contains(p.Guid))
            .ToList();
        var predictedGuids = finalPredictedUsers.Select(p => p.Guid).ToHashSet();
        var combinedGuidsForLookup = finalGuids.Union(predictedGuids).ToHashSet();

        if (!combinedGuidsForLookup.Any())
        {
            return "<table><tr><td>No musicians found matching all criteria in this cycle.</td></tr></table>";
        }

        var musicianRecords = GetMusicianRecords(combinedGuidsForLookup, userLat, userLon);
        var predictedUserDataMap = finalPredictedUsers.ToDictionary(p => p.Guid);

        // --- MODIFICATION: Replaced Task.WhenAll with a serial foreach loop ---
        var completedMusicianRecords = new List<MusicianRecord>();
        foreach (var record in musicianRecords)
        {
            // This 'await' pauses the loop, ensuring requests happen one at a time.
            record.Location = await GetIpDetailsAsync(record.IpAddress);

            if (predictedUserDataMap.TryGetValue(record.Guid, out var predictedData))
            {
                record.IsPredicted = true;
                record.PredictedArrivalTime = predictedData.PredictedArrivalTime;
                Console.WriteLine($"[Diagnostic] {record.Guid} ({record.Name}) is a PREDICTED user, expected at {record.PredictedArrivalTime:HH:mm:ss} UTC.");
            }
            else
            {
                record.IsPredicted = false;
                record.LastSeen = DateTime.UtcNow;
                if (lastSeenMap.TryGetValue(record.Guid, out var seenDate))
                {
                    record.LastSeen = seenDate;
                }
            }
            completedMusicianRecords.Add(record);
        }

        return BuildHtmlTable(completedMusicianRecords, liveGuids);
    }

    private static string GetArrivalTimeDisplayString(DateTime arrivalTime)
    {
        double totalMinutes = (arrivalTime - DateTime.UtcNow).TotalMinutes;

        if (totalMinutes <= 0) return "DUE";
        if (totalMinutes <= 20) return "soon";
        if (totalMinutes <= 45) return "½ hour";
        if (totalMinutes <= 75) return "1 hour";
        return "2 hours";
    }

    private string BuildMusicianRowHtml(MusicianRecord record, HashSet<string> liveGuids)
    {
        var sb = new StringBuilder();
        string bulbClass;
        string rowClass = "";
        string locationDisplay;
        
        if (record.IsPredicted)
        {
            bulbClass = "orange";
            string arrivalString = GetArrivalTimeDisplayString(record.PredictedArrivalTime);
            locationDisplay = $"<span class='sub-line'>{arrivalString}</span>";
        }
        else
        {
            bool isLive = liveGuids.Contains(record.Guid);
            rowClass = isLive ? "" : " class='not-here'";
            bulbClass = isLive ? "green" : "gray";

            string ipDerivedRegion = record.Location?.regionName ?? "";
            string regionHtml = $"<span class='sub-line'>{ipDerivedRegion}</span>";

            bool hasValidCity = !string.IsNullOrWhiteSpace(record.UserCity) && record.UserCity.Trim() != "-";
            bool cityIsDifferentFromRegion = !string.Equals(record.UserCity.Trim(), ipDerivedRegion.Trim(), StringComparison.OrdinalIgnoreCase);

            if (hasValidCity && cityIsDifferentFromRegion)
            {
                locationDisplay = $"{record.UserCity},<br/>{regionHtml}";
            }
            else
            {
                locationDisplay = regionHtml;
            }
        }

        string instrumentHtml = "";
        if (record.Instrument != null && record.Instrument.Trim() != "-")
        {
            instrumentHtml = $"<div class='sub-line'>{record.Instrument}</div>";
        }

        sb.AppendLine($"        <tr{rowClass}>");
        sb.AppendLine($"          <td class='col-musician'><div class='musician-cell-content'><span class='bulb {bulbClass}'></span><div>{record.Name}{instrumentHtml}</div></div></td>");
        sb.AppendLine($"          <td class='col-location'>{locationDisplay}</td>");
        sb.AppendLine("        </tr>");
        
        return sb.ToString();
    }
    
    private string BuildHtmlTable(IEnumerable<MusicianRecord> records, HashSet<string> liveGuids)
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
        sb.AppendLine("  .sub-line { font-size: 0.85em; font-family: sans-serif-condensed, Arial Narrow, sans-serif; color: #555; }");
        sb.AppendLine("  .musician-cell-content { display: flex; align-items: center; }");
        sb.AppendLine("</style>");

        sb.AppendLine("<table style='width: 100%;' class='musician-table'>");
        sb.AppendLine("  <tbody>");

        var orderedRecords = records
            .OrderBy(r => r.DistanceKm)
            .GroupBy(r => $"{r.Name}|{r.Location?.city}|{r.Location?.regionName}")
            .Select(g =>
            {
                return g.FirstOrDefault(r => liveGuids.Contains(r.Guid))
                    ?? g.FirstOrDefault(r => r.IsPredicted)
                    ?? g.First();
            })
            .ToList();

        const int minimumVisibleCount = 3;
        const double distanceThresholdKm = 3000;

        var nearbyRecords = new List<MusicianRecord>();
        var distantRecords = new List<MusicianRecord>();

        foreach (var record in orderedRecords)
        {
            if (record.DistanceKm < distanceThresholdKm || nearbyRecords.Count < minimumVisibleCount)
            {
                nearbyRecords.Add(record);
            }
            else
            {
                distantRecords.Add(record);
            }
        }

        if (distantRecords.Count <= 2)
        {
            nearbyRecords.AddRange(distantRecords);
            distantRecords.Clear();
        }

        foreach (var record in nearbyRecords)
        {
            sb.Append(BuildMusicianRowHtml(record, liveGuids));
        }
        
        if (distantRecords.Any())
        {
            sb.AppendLine("    <tr>");
            sb.AppendLine("      <td colspan='2' style='padding: 0; border: none;'>");
            sb.AppendLine("        <details>");
            sb.AppendLine($"          <summary>{distantRecords.Count} more</summary>");
            sb.AppendLine("          <table style='width: 100%; border-collapse: collapse;' class='musician-table'>");
            sb.AppendLine("            <tbody>");

            foreach (var record in distantRecords)
            {
                sb.Append(BuildMusicianRowHtml(record, liveGuids));
            }

            sb.AppendLine("            </tbody>");
            sb.AppendLine("          </table>");
            sb.AppendLine("        </details>");
            sb.AppendLine("      </td>");
            sb.AppendLine("    </tr>");
        }
        
        sb.AppendLine("  </tbody>");
        sb.AppendLine("</table>");

        foreach (var record in orderedRecords)
        {
            string musicianPart = (record.Instrument != null && record.Instrument.Trim() != "-")
                ? $"{record.Name} ({record.Instrument})"
                : record.Name;

            string status;
            string detailsPart;

            if (record.IsPredicted)
            {
                status = "PREDICTED";
                string arrivalString = GetArrivalTimeDisplayString(record.PredictedArrivalTime);
                detailsPart = $"Displays \"{arrivalString}\"";
            }
            else
            {
                status = liveGuids.Contains(record.Guid) ? "NOW" : "GONE";
                
                string ipDerivedRegion = record.Location?.regionName ?? "Unknown Region";
                string locationPart;

                bool hasValidCity = !string.IsNullOrWhiteSpace(record.UserCity) && record.UserCity.Trim() != "-";
                bool cityIsDifferentFromRegion = !string.Equals(record.UserCity.Trim(), ipDerivedRegion.Trim(), StringComparison.OrdinalIgnoreCase);

                if (hasValidCity && cityIsDifferentFromRegion)
                {
                    locationPart = $"{record.UserCity}, {ipDerivedRegion}";
                }
                else
                {
                    locationPart = ipDerivedRegion;
                }
                detailsPart = locationPart;
            }
            
            Console.WriteLine($"{record.Guid} {status,-9} {musicianPart,-35} -> {detailsPart}");
        }

        return sb.ToString();
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
        var predictedUsers = new List<PredictedRecord>();
        const string fileName = "predicted.csv";
        if (!File.Exists(fileName))
        {
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
        return predictedUsers;
    }

    private Dictionary<string, DateTime> GetLastSeenMap()
    {
        var lastSeenMap = new Dictionary<string, DateTime>();
        const string fileName = "timeTogetherLastUpdates.json";
        try
        {
            var json = File.ReadAllText(fileName);
            var records = JsonSerializer.Deserialize<List<LastUpdateRecordRaw>>(json);
            if (records == null) return lastSeenMap;

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
            return allUpdates
                .GroupBy(u => u.Guid)
                .ToDictionary(g => g.Key, g => g.Max(item => item.LastSeen));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GetLastSeenMap] CRITICAL ERROR reading or parsing {fileName}. Error: {ex.Message}");
            return lastSeenMap;
        }
    }

    private HashSet<string> GetLongSessionGuids()
    {
        var guids = new HashSet<string>();
        const string fileName = "timeTogether.json";
        try
        {
            var json = File.ReadAllText(fileName);
            var records = JsonSerializer.Deserialize<List<TimeRecord>>(json);
            if (records == null) return guids;

            foreach (var record in records)
            {
                if (record.Key.Length != 64) continue;
                guids.Add(record.Key.Substring(0, 32));
                guids.Add(record.Key.Substring(32, 32));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GetLongSessionGuids] CRITICAL ERROR reading or parsing {fileName}. Error: {ex.Message}");
        }
        return guids;
    }

    private List<MusicianRecord> GetMusicianRecords(HashSet<string> guidsToFind, double userLat, double userLon)
    {
        var records = new List<MusicianRecord>();
        try
        {
            var lines = File.ReadAllLines("join-events.csv");
            foreach (var line in lines)
            {
                var fields = line.Split(new[] { ',' }, 13);
                if (fields.Length != 13) continue;

                var guid = fields[2].Trim();
                if (!guidsToFind.Contains(guid)) continue;

                if (long.TryParse(fields[0].Trim(), out long minutesSinceEpoch) &&
                    double.TryParse(fields[9].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double lat) &&
                    double.TryParse(fields[10].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double lon) &&
                    !string.IsNullOrWhiteSpace(fields[11].Trim()) && fields[11].Trim() != "-")
                {
                    string userLocationString = WebUtility.UrlDecode(fields[5].Trim()).Split(',')[0].Trim();
                    string finalUserCity = userLocationString; 
                    
                    var locationParts = userLocationString.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                    if (locationParts.Length > 1)
                    {
                        var lastPart = locationParts.Last();
                        if (UsStateAbbreviations.Contains(lastPart))
                        {
                            finalUserCity = string.Join(" ", locationParts.Take(locationParts.Length - 1));
                        }
                    }

                    records.Add(new MusicianRecord
                    {
                        MinutesSinceEpoch = minutesSinceEpoch,
                        Guid = guid,
                        Name = WebUtility.UrlDecode(fields[3].Trim()),
                        Instrument = fields[4].Trim(),
                        UserCity = finalUserCity,
                        Lat = lat,
                        Lon = lon,
                        IpAddress = fields[11].Trim()
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GetMusicianRecords] Error reading join-events.csv: {ex.Message}");
        }

        return records
            .GroupBy(r => r.Guid)
            .Select(g => g.OrderByDescending(r => r.MinutesSinceEpoch).First())
            .Select(r =>
            {
                r.DistanceKm = CalculateDistance(userLat, userLon, r.Lat, r.Lon);
                return r;
            })
            .Where(r => r.DistanceKm < 5000)
            .ToList();
    }

    private async Task<IpApiDetails> GetIpDetailsAsync(string ip)
    {
        string ipAddress = ip.Contains("::ffff:") ? ip.Substring(ip.LastIndexOf(':') + 1) : ip.Split(':')[0];

        if (IpCache.TryGetValue(ipAddress, out var cachedItem) && !cachedItem.IsExpired)
        {
            return cachedItem.Data;
        }

        int maxRetries = 4;
        int delay = 2000;

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                await Task.Delay(delay);
                var response = await HttpClient.GetStringAsync($"http://ip-api.com/json/{ipAddress}");
                var details = JsonSerializer.Deserialize<IpApiDetails>(response);

                if (details?.status == "success")
                {
                    Console.WriteLine($"[GetIpDetailsAsync] API SUCCESS for {ipAddress}: City={details.city}, Lat={details.lat}, Lon={details.lon}");
                    IpCache[ipAddress] = new CacheItem<IpApiDetails>(details);
                    return details;
                }
            }
            catch (HttpRequestException ex)
            {
                 Console.WriteLine($"[GetIpDetailsAsync] Error on attempt {i + 1} for IP {ipAddress}. Exception: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetIpDetailsAsync] General error on attempt {i + 1} for IP {ipAddress}. Exception: {ex.Message}");
            }
            delay *= 2;
        }
        return new IpApiDetails();
    }

    // --- UTILITY METHODS ---
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

    private static double ToRadians(double angle) => Math.PI * angle / 180.0;
}
