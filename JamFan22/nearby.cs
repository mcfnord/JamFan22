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

    // --- MODIFIED: A simple lock is sufficient for serializing requests ---
    private static readonly object _apiLock = new object();


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
        public string UserCity { get; set; } // MODIFIED: Added to store the user-provided city
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
        var musicianTasks = musicianRecords.Select(async record =>
        {
            record.Location = await GetIpDetailsAsync(record.IpAddress);
            // Default to now, then check the map for a more accurate recent time.
            record.LastSeen = DateTime.UtcNow;
            if (lastSeenMap.TryGetValue(record.Guid, out var seenDate))
            {
                record.LastSeen = seenDate;
            }
            return record;
        });
        var completedMusicianRecords = await Task.WhenAll(musicianTasks);

        // --- MODIFIED: The first diagnostic block that showed musicians by distance has been removed. ---

        return BuildHtmlTable(completedMusicianRecords, liveGuids);
    }


    private string BuildHtmlTable(IEnumerable<MusicianRecord> records, HashSet<string> liveGuids)
    {
        var sb = new StringBuilder();
        // Add CSS styles for borders, bulbs, grayed-out rows, and column widths
        sb.AppendLine("<style>");
        sb.AppendLine("  table, th, td { border: 1px solid #ccc; border-collapse: collapse; table-layout: fixed; }");
        sb.AppendLine("  th, td { padding: 4px; text-align: left; word-wrap: break-word; vertical-align: middle; }");
        sb.AppendLine("  th { white-space: nowrap; }");
        sb.AppendLine("  th.col-musician { padding-left: 24px; }");
        sb.AppendLine("  .musician-table tbody tr:hover td { background-color: #f5f5f5; }");
        sb.AppendLine("  .bulb { height: 12px; width: 12px; border-radius: 50%; display: inline-block; margin-right: 8px; flex-shrink: 0; }");
        sb.AppendLine("  .green { background-color: #28a745; }");
        sb.AppendLine("  .gray { background-color: #adb5bd; }");
        sb.AppendLine("  .not-here { color: #6c757d; }");
        sb.AppendLine("  summary { cursor: pointer; color: #007bff; padding: 8px; }");
        sb.AppendLine("  .col-musician { width: 55%; }");
        sb.AppendLine("  .col-location { width: 45%; }");
        sb.AppendLine("  .sub-line { font-size: 0.85em; font-family: sans-serif-condensed, Arial Narrow, sans-serif; color: #555; }");
        sb.AppendLine("  .musician-cell-content { display: flex; align-items: center; }");
        sb.AppendLine("</style>");

        sb.AppendLine("<table style='width: 100%;' class='musician-table'>");
        sb.AppendLine("  <thead>");
        sb.AppendLine("    <tr><th class='col-musician'>Musician</th><th class='col-location'>Location</th></tr>");
        sb.AppendLine("  </thead>");
        sb.AppendLine("  <tbody>");

        var orderedRecords = records
            .OrderBy(r => r.DistanceKm)
            .GroupBy(r => $"{r.Name}|{r.Location?.city}|{r.Location?.regionName}")
            .Select(g =>
            {
                var liveRecord = g.FirstOrDefault(r => liveGuids.Contains(r.Guid));
                return liveRecord ?? g.First(); // Prioritize live record, otherwise take the closest.
            })
            .ToList();

        var nearbyRecords = orderedRecords.Where(r => r.DistanceKm < 3000).ToList();
        var distantRecords = orderedRecords.Where(r => r.DistanceKm >= 3000).ToList();

        // If there are only a few distant musicians, add them to the main list.
        if (distantRecords.Count <= 2)
        {
            nearbyRecords.AddRange(distantRecords);
            distantRecords.Clear();
        }

        // Render nearby musicians
        foreach (var record in nearbyRecords)
        {
            bool isLive = liveGuids.Contains(record.Guid);
            string rowClass = isLive ? "" : " class='not-here'";
            string bulbClass = isLive ? "green" : "gray";

            string ipDerivedRegion = record.Location?.regionName ?? "";
            string regionHtml = $"<span class='sub-line'>{ipDerivedRegion}</span>";
            string locationDisplay;

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

            string instrumentHtml = "";
            if (record.Instrument != null && record.Instrument.Trim() != "-")
            {
                instrumentHtml = $"<div class='sub-line'>{record.Instrument}</div>";
            }

            sb.AppendLine($"    <tr{rowClass}>");
            sb.AppendLine($"      <td class='col-musician'><div class='musician-cell-content'><span class='bulb {bulbClass}'></span><div>{record.Name}{instrumentHtml}</div></div></td>");
            sb.AppendLine($"      <td class='col-location'>{locationDisplay}</td>");
            sb.AppendLine("    </tr>");
        }

        // If there are still distant musicians, create the collapsible section
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
                bool isLive = liveGuids.Contains(record.Guid);
                string rowClass = isLive ? "" : " class='not-here'";
                string bulbClass = isLive ? "green" : "gray";

                string ipDerivedRegion = record.Location?.regionName ?? "";
                string regionHtml = $"<span class='sub-line'>{ipDerivedRegion}</span>";
                string locationDisplay;

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

                string instrumentHtml = "";
                if (record.Instrument != null && record.Instrument.Trim() != "-")
                {
                    instrumentHtml = $"<div class='sub-line'>{record.Instrument}</div>";
                }

                sb.AppendLine($"              <tr{rowClass}>");
                sb.AppendLine($"                <td class='col-musician'><div class='musician-cell-content'><span class='bulb {bulbClass}'></span><div>{record.Name}{instrumentHtml}</div></div></td>");
                sb.AppendLine($"                <td class='col-location'>{locationDisplay}</td>");
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

        foreach (var record in orderedRecords)
        {
            string musicianPart = (record.Instrument != null && record.Instrument.Trim() != "-")
                ? $"{record.Name} ({record.Instrument})"
                : record.Name;

            string ipDerivedRegion = record.Location?.regionName ?? "";
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

            // MODIFIED: Added the GUID to the beginning of the diagnostic line.
            Console.WriteLine($"{record.Guid} APPEARS AS {musicianPart} -> {locationPart}");
        }

        return sb.ToString();
    }


    private async Task<HashSet<string>> GetLiveGuidsFromApiAsync()
    {
        var liveGuids = new HashSet<string>();
        // This logic assumes that JamFan22.Pages.IndexModel and its static members exist and are accessible.
        // It also assumes 'JamulusServers' is a typo for the internal 'JamulusServer' class.
        foreach (var key in JamFan22.Pages.IndexModel.JamulusListURLs.Keys)
        {
            try
            {
                if (JamFan22.Pages.IndexModel.LastReportedList.TryGetValue(key, out var jsonString) && !string.IsNullOrEmpty(jsonString))
                {
                    var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServer>>(jsonString);

                    if (serversOnList != null)
                    {
                        // Loop through each server object in the deserialized list.
                        foreach (var server in serversOnList)
                        {
                            // Safety check: ensure the server object itself isn't null and its clients list exists.
                            if (server != null && server.clients != null)
                            {
                                // Now, loop through the clients on this specific server.
                                foreach (var client in server.clients)
                                {
                                    // Safety check for the client object.
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
                // Provide context in the error message for better debugging.
                Console.WriteLine($"[GetLiveGuidsFromApiAsync] Error processing data for key '{key}'. Exception: {ex.Message}");
            }
        }

        // This method no longer performs any truly async operations based on your new code,
        // but we keep the signature to match where it's called.
        await Task.CompletedTask;
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
                        // MODIFIED: Split the city string at the comma and take the first part.
                        UserCity = WebUtility.UrlDecode(fields[5].Trim()).Split(',')[0].Trim(),
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
            .Where(r => r.DistanceKm < 5000)
            .ToList();
        Console.WriteLine($"[GetMusicianRecords] {finalRecords.Count} musicians remaining after distance filter (< 5000km).");

        return finalRecords;
    }

    private async Task<IpApiDetails> GetIpDetailsAsync(string ip)
    {
        string ipAddress;
        // Handle IPv4-mapped IPv6 addresses (e.g., ::ffff:123.123.123.123)
        if (ip.Contains("::ffff:"))
        {
            ipAddress = ip.Substring(ip.LastIndexOf(':') + 1);
        }
        else
        {
            ipAddress = ip.Split(':')[0];
        }

        if (IpCache.TryGetValue(ipAddress, out var cachedItem) && !cachedItem.IsExpired)
        {
            return cachedItem.Data;
        }

        int maxRetries = 4;
        int delay = 2000; // Initial delay of 2 seconds

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                await Task.Delay(delay); // Wait before making the call

                var response = await HttpClient.GetStringAsync($"http://ip-api.com/json/{ipAddress}");
                var details = JsonSerializer.Deserialize<IpApiDetails>(response);

                if (details?.status == "success")
                {
                    Console.WriteLine($"[GetIpDetailsAsync] API SUCCESS for {ipAddress}: City={details.city}, Lat={details.lat}, Lon={details.lon}");
                    IpCache[ipAddress] = new CacheItem<IpApiDetails>(details);
                    return details;
                }
                else
                {
                    Console.WriteLine($"[GetIpDetailsAsync] API FAILED for {ipAddress}: Status was '{details?.status}'. Retrying...");
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Console.WriteLine($"[GetIpDetailsAsync] Rate limit hit (429) on attempt {i + 1} for IP {ipAddress}. Increasing delay and retrying.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetIpDetailsAsync] Error on attempt {i + 1} for IP {ipAddress}. Exception: {ex.Message}");
            }

            delay *= 2; // Double the delay for the next retry (exponential backoff)
        }

        Console.WriteLine($"[GetIpDetailsAsync] All retry attempts failed for IP {ipAddress}.");
        // MODIFIED: Do not cache failures.
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

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalMinutes < 1) return "now";
        return $"{(int)ts.TotalMinutes} mins";
    }
}