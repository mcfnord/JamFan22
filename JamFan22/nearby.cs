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
    // --- CONFIGURATION ---
    private static readonly string[] Urls =
    {
        "http://137.184.177.58/servers.php?central=anygenre1.jamulus.io:22124",
        "http://137.184.177.58/servers.php?central=anygenre2.jamulus.io:22224",
        "https://explorer.jamulus.io/servers.php?central=anygenre3.jamulus.io:22624",
        "https://explorer.jamulus.io/servers.php?central=rock.jamulus.io:22424",
        "http://137.184.177.58/servers.php?central=jazz.jamulus.io:22324",
        "http://137.184.177.58/servers.php?central=classical.jamulus.io:22524",
        "https://explorer.jamulus.io/servers.php?central=choral.jamulus.io:22724",
    };

    private static readonly HttpClient HttpClient = new HttpClient();
    private static readonly ConcurrentDictionary<string, CacheItem<IpApiDetails>> IpCache = new ConcurrentDictionary<string, CacheItem<IpApiDetails>>();
    private int _apiCallCounter; // Class-level counter for API calls per run.

    /// <summary>
    /// Initializes a new instance of the MusicianFinder.
    /// </summary>
    public MusicianFinder()
    {
        // Constructor is now parameterless.
    }


    // --- DATA MODELS ---
    private class JamulusServer { public List<ApiClient> clients { get; set; } = new List<ApiClient>(); }
    private class ApiClient { public string name { get; set; } public string country { get; set; } public string instrument { get; set; } public string city { get; set; } }
    private class TimeRecord { public string Key { get; set; } public string Value { get; set; } }
    // Modified to handle flexible date formats by reading the date as a string first.
    private class LastUpdateRecordRaw { public string Key { get; set; } public string Value { get; set; } }
    private class IpApiDetails { public string status { get; set; } public string city { get; set; } public string regionName { get; set; } public string countryCode { get; set; } public double lat { get; set; } public double lon { get; set; } }
    private class CacheItem<T> { public T Data { get; } public DateTime Expiration { get; } public CacheItem(T data) { Data = data; Expiration = DateTime.UtcNow.AddHours(1); } public bool IsExpired => DateTime.UtcNow > Expiration; }
    private class MusicianRecord
    {
        public long MinutesSinceEpoch { get; set; }
        public string Guid { get; set; }
        public string Name { get; set; }
        public string Instrument { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public string IpAddress { get; set; }
        public double DistanceKm { get; set; }
        public DateTime LastSeen { get; set; }
        public IpApiDetails Location { get; set; }
    }

    /// <summary>
    /// Finds nearby musicians based on an IP address and returns an HTML table.
    /// </summary>
    /// <param name="userIp">Your IP address.</param>
    /// <returns>An HTML table string.</returns>
    public async Task<string> FindMusiciansHtmlAsync(string userIp)
    {
        _apiCallCounter = 0; // Reset counter at the start of each public call.

        if (string.IsNullOrWhiteSpace(userIp))
        {
            return "<table><tr><td>Error: IP address cannot be empty.</td></tr></table>";
        }

        var userLocation = await GetIpDetailsAsync(userIp);

        if (userLocation?.status != "success")
        {
            return $"<table><tr><td>Error: Could not determine your location from IP address {userIp}.</td></tr></table>";
        }

        // Call the private method with the looked-up coordinates
        return await FindMusiciansHtmlAsync(userLocation.lat, userLocation.lon);
    }

    /// <summary>
    /// Finds nearby musicians based on coordinates and returns an HTML table.
    /// </summary>
    private async Task<string> FindMusiciansHtmlAsync(double userLat, double userLon)
    {
        // Check for required files before proceeding.
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

        // Step 1: Get live and recent users
        var liveGuids = await GetLiveGuidsFromApiAsync();
        var lastSeenMap = GetLastSeenMap();
        var recentGuids = lastSeenMap
            .Where(kvp => kvp.Value > DateTime.UtcNow.AddMinutes(-60))
            .Select(kvp => kvp.Key)
            .ToHashSet();
        var allRelevantGuids = liveGuids.Union(recentGuids).ToHashSet();

        // Step 2: Get GUIDs with significant session time
        var longSessionGuids = GetLongSessionGuids();

        // Step 3: Final intersection of all criteria
        var finalGuids = allRelevantGuids.Intersect(longSessionGuids).ToHashSet();

        if (!finalGuids.Any())
        {
            return "<table><tr><td>No musicians found matching all criteria in this cycle.</td></tr></table>";
        }

        // Step 4: Process join-events.csv to get musician details
        var musicianRecords = GetMusicianRecords(finalGuids, userLat, userLon);

        // Step 5: Get location details from IP API and determine final "Last Seen" status
        foreach (var record in musicianRecords)
        {
            record.Location = await GetIpDetailsAsync(record.IpAddress);
            // Default to now, then check the map for a more accurate recent time.
            record.LastSeen = DateTime.UtcNow;
            if (lastSeenMap.TryGetValue(record.Guid, out var seenDate))
            {
                record.LastSeen = seenDate;
            }
        }

        return BuildHtmlTable(musicianRecords, liveGuids);
    }

    private string BuildHtmlTable(IEnumerable<MusicianRecord> records, HashSet<string> liveGuids)
    {
        var sb = new StringBuilder();
        // Add CSS styles for borders, bulbs, grayed-out rows, and column widths
        sb.AppendLine("<style>");
        sb.AppendLine("  table, th, td { border: 1px solid #ccc; border-collapse: collapse; table-layout: fixed; }");
        sb.AppendLine("  th, td { padding: 4px; text-align: left; word-wrap: break-word; }");
        sb.AppendLine("  th { white-space: nowrap; }");
        sb.AppendLine("  tbody tr:hover { background-color: #f5f5f5; }");
        sb.AppendLine("  .bulb { height: 12px; width: 12px; border-radius: 50%; display: inline-block; margin-right: 8px; vertical-align: middle; }");
        sb.AppendLine("  .green { background-color: #28a745; }");
        sb.AppendLine("  .gray { background-color: #adb5bd; }");
        sb.AppendLine("  .not-here { color: #6c757d; }");
        sb.AppendLine("  summary { cursor: pointer; color: #007bff; padding: 8px; }");
        sb.AppendLine("  .col-musician { width: 28%; }");
        sb.AppendLine("  .col-instrument { width: 17%; }");
        sb.AppendLine("  .col-city { width: 25%; }");
        sb.AppendLine("  .col-region { width: 20%; }");
        sb.AppendLine("  .col-country { width: 10%; }");
        sb.AppendLine("  .small-font { font-size: 0.9em; font-family: sans-serif-condensed, Arial Narrow, sans-serif; }");
        sb.AppendLine("</style>");

        sb.AppendLine("<table style='width: 100%;'>");
        sb.AppendLine("  <thead>");
        sb.AppendLine("    <tr><th class='col-musician'>Musician</th><th class='col-instrument small-font'>Instrument</th><th class='col-city'>City</th><th class='col-region small-font'>Region</th><th class='col-country small-font'>Nation</th></tr>");
        sb.AppendLine("  </thead>");
        sb.AppendLine("  <tbody>");

        var orderedRecords = records.OrderBy(r => r.DistanceKm).ToList();
        var nearbyRecords = orderedRecords.Where(r => r.DistanceKm < 3500).ToList();
        var distantRecords = orderedRecords.Where(r => r.DistanceKm >= 3500).ToList();

        // Render nearby musicians
        foreach (var record in nearbyRecords)
        {
            bool isLive = liveGuids.Contains(record.Guid);
            string rowClass = isLive ? "" : " class='not-here'";
            string bulbClass = isLive ? "green" : "gray";
            sb.AppendLine($"    <tr{rowClass}>");
            sb.AppendLine($"      <td class='col-musician'><span class='bulb {bulbClass}'></span>{record.Name}</td>");
            sb.AppendLine($"      <td class='col-instrument small-font'>{record.Instrument}</td>");
            sb.AppendLine($"      <td class='col-city'>{record.Location?.city ?? ""}</td>");
            sb.AppendLine($"      <td class='col-region small-font'>{record.Location?.regionName ?? ""}</td>");
            sb.AppendLine($"      <td class='col-country small-font'>{record.Location?.countryCode ?? ""}</td>");
            sb.AppendLine("    </tr>");
        }

        // If there are distant musicians, create the collapsible section
        if (distantRecords.Any())
        {
            sb.AppendLine("    <tr>");
            sb.AppendLine("      <td colspan='5' style='padding: 0; border: none;'>");
            sb.AppendLine("        <details>");
            sb.AppendLine("          <summary>see further</summary>");
            sb.AppendLine("          <table style='width: 100%; border-collapse: collapse;'>");
            sb.AppendLine("            <tbody>");

            foreach (var record in distantRecords)
            {
                bool isLive = liveGuids.Contains(record.Guid);
                string rowClass = isLive ? "" : " class='not-here'";
                string bulbClass = isLive ? "green" : "gray";
                sb.AppendLine($"              <tr{rowClass}>");
                sb.AppendLine($"                <td class='col-musician'><span class='bulb {bulbClass}'></span>{record.Name}</td>");
                sb.AppendLine($"                <td class='col-instrument small-font'>{record.Instrument}</td>");
                sb.AppendLine($"                <td class='col-city'>{record.Location?.city ?? ""}</td>");
                sb.AppendLine($"                <td class='col-region small-font'>{record.Location?.regionName ?? ""}</td>");
                sb.AppendLine($"                <td class='col-country small-font'>{record.Location?.countryCode ?? ""}</td>");
                sb.AppendLine("              </tr>");
            }

            sb.AppendLine("            </tbody>");
            sb.AppendLine("          </table>");
            sb.AppendLine("        </details>");
            sb.AppendLine("      </td>");
            sb.AppendLine("    </tr>");
        }

        sb.AppendLine("  </tbody>");
        sb.AppendLine("</table>");
        return sb.ToString();
    }

    private async Task<HashSet<string>> GetLiveGuidsFromApiAsync()
    {
        var liveGuids = new HashSet<string>();
        foreach (var url in Urls)
        {
            try
            {
                var response = await HttpClient.GetStringAsync(url);
                var servers = JsonSerializer.Deserialize<List<JamulusServer>>(response);
                if (servers == null) continue;

                // ADDED: Filter out servers where the 'clients' list is null to prevent NullReferenceException.
                foreach (var client in servers.Where(s => s != null && s.clients != null).SelectMany(s => s.clients))
                {
                    liveGuids.Add(GetGuid(client.name, client.country, client.instrument));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetLiveGuidsFromApiAsync] Error processing URL {url}. Exception: {ex.Message}");
            }
        }
        return liveGuids;
    }

    private Dictionary<string, DateTime> GetLastSeenMap()
    {
        var lastSeenMap = new Dictionary<string, DateTime>();
        const string fileName = "timeTogetherLastUpdates.json";
        try
        {
            var json = File.ReadAllText(fileName);
            // Read into a raw format first to avoid strict date parsing errors
            var records = JsonSerializer.Deserialize<List<LastUpdateRecordRaw>>(json);
            if (records == null)
            {
                Console.WriteLine($"[GetLastSeenMap] Parsed {fileName} but it resulted in a null object.");
                return lastSeenMap;
            }

            var allUpdates = new List<(string Guid, DateTime LastSeen)>();
            int failedParses = 0;
            foreach (var record in records)
            {
                if (record.Key.Length != 64) continue;

                // Try to parse the date string manually for flexibility
                if (DateTime.TryParse(record.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsedDate))
                {
                    allUpdates.Add((record.Key.Substring(0, 32), parsedDate));
                    allUpdates.Add((record.Key.Substring(32, 32), parsedDate));
                }
                else
                {
                    failedParses++;
                }
            }
            if (failedParses > 0) Console.WriteLine($"[GetLastSeenMap] Failed to parse the date for {failedParses} records.");

            var resultingMap = allUpdates
                .GroupBy(u => u.Guid)
                .ToDictionary(g => g.Key, g => g.Max(item => item.LastSeen));

            Console.WriteLine($"[GetLastSeenMap] Successfully processed {records.Count - failedParses} valid records from {fileName}, resulting in {resultingMap.Count} unique users with last seen times.");
            return resultingMap;
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
            if (records == null)
            {
                Console.WriteLine($"[GetLongSessionGuids] Parsed {fileName} but it resulted in a null object.");
                return guids;
            }

            foreach (var record in records)
            {
                if (record.Key.Length != 64) continue;
                guids.Add(record.Key.Substring(0, 32));
                guids.Add(record.Key.Substring(32, 32));
            }
            Console.WriteLine($"[GetLongSessionGuids] Successfully processed {records.Count} records from {fileName}, resulting in {guids.Count} unique users with session data.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GetLongSessionGuids] CRITICAL ERROR reading or parsing {fileName}. Error: {ex.Message}");
        }
        return guids;
    }

    private List<MusicianRecord> GetMusicianRecords(HashSet<string> finalGuids, double userLat, double userLon)
    {
        Console.WriteLine($"[GetMusicianRecords] Starting with {finalGuids.Count} final GUIDs.");
        var records = new List<MusicianRecord>();
        try
        {
            var lines = File.ReadAllLines("join-events.csv");
            Console.WriteLine($"[GetMusicianRecords] Read {lines.Length} lines from join-events.csv.");
            foreach (var line in lines)
            {
                var fields = line.Split(new[] { ',' }, 13);
                if (fields.Length != 13) continue;

                var guid = fields[2].Trim();
                if (!finalGuids.Contains(guid)) continue;

                if (long.TryParse(fields[0].Trim(), out long minutesSinceEpoch) &&
                    double.TryParse(fields[9].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double lat) &&
                    double.TryParse(fields[10].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double lon) &&
                    !string.IsNullOrWhiteSpace(fields[11].Trim()) && fields[11].Trim() != "-")
                {
                    records.Add(new MusicianRecord
                    {
                        MinutesSinceEpoch = minutesSinceEpoch,
                        Guid = guid,
                        Name = WebUtility.UrlDecode(fields[3].Trim()),
                        Instrument = fields[4].Trim(),
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

        Console.WriteLine($"[GetMusicianRecords] Found {records.Count} total valid, matching records in join-events.csv.");

        var groupedRecords = records
            .GroupBy(r => r.Guid)
            .Select(g => g.OrderByDescending(r => r.MinutesSinceEpoch).First())
            .ToList();
        Console.WriteLine($"[GetMusicianRecords] {groupedRecords.Count} unique musicians after selecting the most recent entry for each.");

        var distancedRecords = groupedRecords.Select(r =>
        {
            r.DistanceKm = CalculateDistance(userLat, userLon, r.Lat, r.Lon);
            return r;
        }).ToList();
        Console.WriteLine($"[GetMusicianRecords] Calculated distance for {distancedRecords.Count} musicians.");

        var finalRecords = distancedRecords
            .Where(r => r.DistanceKm < 4000)
            .ToList();
        Console.WriteLine($"[GetMusicianRecords] {finalRecords.Count} musicians remaining after distance filter (< 4000km).");

        return finalRecords;
    }

    private async Task<IpApiDetails> GetIpDetailsAsync(string ip)
    {
        var ipAddress = ip.Split(':')[0];
        if (IpCache.TryGetValue(ipAddress, out var cachedItem) && !cachedItem.IsExpired)
        {
            return cachedItem.Data;
        }

        // If this is the 3rd call or later (index 2+), pause for 1 second.
        if (_apiCallCounter >= 2)
        {
            await Task.Delay(1000); // 1 second pause
        }
        _apiCallCounter++; // Increment the counter for this run

        try
        {
            var response = await HttpClient.GetStringAsync($"http://ip-api.com/json/{ipAddress}");
            var details = JsonSerializer.Deserialize<IpApiDetails>(response);

            if (details?.status == "success")
            {
                IpCache[ipAddress] = new CacheItem<IpApiDetails>(details);
                return details;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GetIpDetailsAsync] Error looking up IP {ipAddress}. Exception: {ex.Message}");
        }

        var failureResult = new IpApiDetails();
        IpCache[ipAddress] = new CacheItem<IpApiDetails>(failureResult); // Cache failures too
        return failureResult;
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

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalMinutes < 1) return "now";
        return $"{(int)ts.TotalMinutes} mins";
    }
}

