using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using JamFan22.Models;
using JamFan22.Services;

public record GatherResult(
    string Context,
    Dictionary<string, string> NameColors,
    List<int> RoomChannelIds,
    bool IsGroupNoteworthy,
    bool HasWebUser = false,
    string RoomLanguage = "English");

public static class WelcomeContext
{
    private static readonly JsonSerializerOptions _opts = new() { PropertyNameCaseInsensitive = true };

    // Web user IP cache — refreshed hourly from telemetry.log
    private static HashSet<string> _webUserIps = new();
    private static DateTime _webUserIpsRefreshedAt = DateTime.MinValue;
    private static readonly SemaphoreSlim _webUserIpsLock = new(1, 1);

    private static async Task<HashSet<string>> GetWebUserIpsAsync()
    {
        var now = DateTime.UtcNow;
        if ((now - _webUserIpsRefreshedAt).TotalHours < 1.0) return _webUserIps;
        await _webUserIpsLock.WaitAsync();
        try
        {
            if ((now - _webUserIpsRefreshedAt).TotalHours < 1.0) return _webUserIps;
            var ips = new HashSet<string>();
            try
            {
                foreach (var line in File.ReadLines("data/telemetry.log"))
                {
                    var idx = line.IndexOf(',');
                    if (idx < 0) continue;
                    var end = line.IndexOf(',', idx + 1);
                    var ip = end < 0 ? line[(idx + 1)..] : line[(idx + 1)..end];
                    if (!string.IsNullOrEmpty(ip)) ips.Add(ip);
                }
            }
            catch { }
            _webUserIps = ips;
            _webUserIpsRefreshedAt = now;
            Console.WriteLine($"[WELCOME] WebUserIpCache refreshed: {ips.Count} IPs");
            return ips;
        }
        finally { _webUserIpsLock.Release(); }
    }

    private record RpcClientEntry(
        [property: JsonPropertyName("channelId")] int ChannelId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("address")] string Address,
        string Instrument,
        string Country);

    private record RpcJsonClient(
        [property: JsonPropertyName("id")] int ChannelId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("address")] string Address,
        [property: JsonPropertyName("countryName")] string CountryName,
        [property: JsonPropertyName("instrumentCode")] int InstrumentCode);

    private record RpcGetClientsResult(
        [property: JsonPropertyName("clients")] List<RpcJsonClient>? Clients,
        [property: JsonPropertyName("connections")] int Connections);

    private record RpcEnvelope<T>(
        [property: JsonPropertyName("result")] T? Result);

    private record LoreEvent(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("schedule")] string? Schedule,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("listen_url")] string? ListenUrl,
        [property: JsonPropertyName("auto")] bool? Auto = null,
        [property: JsonPropertyName("weekday")] int? Weekday = null,
        [property: JsonPropertyName("hour")] int? Hour = null);

    private record LoreEntry(
        [property: JsonPropertyName("tagline")] string? Tagline,
        [property: JsonPropertyName("themes")] List<string>? Themes,
        [property: JsonPropertyName("events")] List<LoreEvent>? Events,
        [property: JsonPropertyName("lat")] double? Lat = null,
        [property: JsonPropertyName("lon")] double? Lon = null);

    private record SessionEntry(
        [property: JsonPropertyName("session_name")] string? SessionName,
        [property: JsonPropertyName("day_of_week")] int DayOfWeek,
        [property: JsonPropertyName("start_hour")] int StartHour,
        [property: JsonPropertyName("duration_hours")] int DurationHours,
        [property: JsonPropertyName("regulars")] List<string>? Regulars);

    private static readonly string[] _instrumentNames =
    {
        "-", "Drums", "Djembe", "Electric Guitar", "Acoustic Guitar", "Bass Guitar",
        "Keyboard", "Synthesizer", "Grand Piano", "Accordion", "Vocal", "Microphone",
        "Harmonica", "Trumpet", "Trombone", "French Horn", "Tuba", "Saxophone",
        "Clarinet", "Flute", "Violin", "Cello", "Double Bass", "Recorder",
        "Streamer", "Listener", "Guitar Vocal", "Keyboard Vocal", "Bodhran",
        "Bassoon", "Oboe", "Harp", "Viola", "Congas", "Bongo", "Vocal Bass",
        "Vocal Tenor", "Vocal Alto", "Vocal Soprano", "Banjo", "Mandolin",
        "Ukulele", "Bass Ukulele", "Vocal Baritone", "Vocal Lead", "Mountain Dulcimer",
        "Scratching", "Rapping", "Vibraphone", "Conductor"
    };

    private static readonly Dictionary<string, string> _countryLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AT"]="German",["DE"]="German",["CH"]="German",["LI"]="German",
        ["FR"]="French",["BE"]="French",["LU"]="French",["MC"]="French",
        ["ES"]="Spanish",["MX"]="Spanish",["AR"]="Spanish",["CL"]="Spanish",["CO"]="Spanish",
        ["PE"]="Spanish",["VE"]="Spanish",["EC"]="Spanish",["BO"]="Spanish",["UY"]="Spanish",
        ["IT"]="Italian",["SM"]="Italian",["VA"]="Italian",
        ["PT"]="Portuguese",["BR"]="Portuguese",["AO"]="Portuguese",["MZ"]="Portuguese",
        ["NL"]="Dutch",["SR"]="Dutch",
        ["RU"]="Russian",["BY"]="Russian",["KZ"]="Russian",
        ["PL"]="Polish",["CZ"]="Czech",["SK"]="Slovak",["HU"]="Hungarian",
        ["RO"]="Romanian",["HR"]="Croatian",["SI"]="Slovenian",["RS"]="Serbian",
        ["BG"]="Bulgarian",["UA"]="Ukrainian",["LT"]="Lithuanian",["LV"]="Latvian",["EE"]="Estonian",
        ["SE"]="Swedish",["NO"]="Norwegian",["DK"]="Danish",["FI"]="Finnish",["IS"]="Icelandic",
        ["GR"]="Greek",["TR"]="Turkish",["IL"]="Hebrew",["SA"]="Arabic",["EG"]="Arabic",["AE"]="Arabic",["MA"]="Arabic",["DZ"]="Arabic",["IQ"]="Arabic",
        ["JP"]="Japanese",["KR"]="Korean",["CN"]="Chinese",["TW"]="Chinese",["HK"]="Chinese",
        ["TH"]="Thai",["VN"]="Vietnamese",["ID"]="Indonesian",["MS"]="Malay",["MY"]="Malay",
        ["PH"]="Filipino",["HI"]="Hindi",["BN"]="Bengali",["TA"]="Tamil",
        ["US"]="English",["GB"]="English",["AU"]="English",["CA"]="English",
        ["NZ"]="English",["IE"]="English",["ZA"]="English",["IN"]="English",
    };
    private static string LanguageFor(string nationCode) =>
        _countryLanguage.TryGetValue(nationCode, out var lang) ? lang : "English";

    private static readonly Dictionary<string, string> _countryNameToLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Germany"]="German",["Austria"]="German",["Switzerland"]="German",["Liechtenstein"]="German",
        ["France"]="French",["Belgium"]="French",["Luxembourg"]="French",["Monaco"]="French",
        ["Spain"]="Spanish",["Mexico"]="Spanish",["Argentina"]="Spanish",["Chile"]="Spanish",["Colombia"]="Spanish",
        ["Peru"]="Spanish",["Venezuela"]="Spanish",["Ecuador"]="Spanish",["Bolivia"]="Spanish",["Uruguay"]="Spanish",
        ["Italy"]="Italian",["San Marino"]="Italian",["Vatican City"]="Italian",
        ["Portugal"]="Portuguese",["Brazil"]="Portuguese",["Angola"]="Portuguese",["Mozambique"]="Portuguese",
        ["Netherlands"]="Dutch",["Suriname"]="Dutch",
        ["Russia"]="Russian",["Belarus"]="Russian",["Kazakhstan"]="Russian",
        ["Poland"]="Polish",["Czech Republic"]="Czech",["Czechia"]="Czech",["Slovakia"]="Slovak",["Hungary"]="Hungarian",
        ["Romania"]="Romanian",["Croatia"]="Croatian",["Slovenia"]="Slovenian",["Serbia"]="Serbian",
        ["Bulgaria"]="Bulgarian",["Ukraine"]="Ukrainian",["Lithuania"]="Lithuanian",["Latvia"]="Latvian",["Estonia"]="Estonian",
        ["Sweden"]="Swedish",["Norway"]="Norwegian",["Denmark"]="Danish",["Finland"]="Finnish",["Iceland"]="Icelandic",
        ["Greece"]="Greek",["Turkey"]="Turkish",["Israel"]="Hebrew",
        ["Saudi Arabia"]="Arabic",["Egypt"]="Arabic",["United Arab Emirates"]="Arabic",["Morocco"]="Arabic",["Algeria"]="Arabic",["Iraq"]="Arabic",
        ["Japan"]="Japanese",["South Korea"]="Korean",["China"]="Chinese",["Taiwan"]="Chinese",["Hong Kong"]="Chinese",
        ["Thailand"]="Thai",["Vietnam"]="Vietnamese",["Indonesia"]="Indonesian",["Malaysia"]="Malay",
        ["Philippines"]="Filipino",["India"]="Hindi",["Bangladesh"]="Bengali",
        ["United States"]="English",["United Kingdom"]="English",["Australia"]="English",["Canada"]="English",
        ["New Zealand"]="English",["Ireland"]="English",["South Africa"]="English",
    };
    private static string LanguageForCountryName(string? cn) =>
        cn != null && _countryNameToLanguage.TryGetValue(cn, out var lang) ? lang : "English";

    // Reads key=value pairs from data/welcome-config.txt; missing keys get defaults.
    private static (int crewMinMins, int forecastMinMins, int forecastSightingHours, int forecastMaxEntries)
        ReadConfig()
    {
        int crewMin = 30, forecastMin = 60, sightHours = 2, maxEntries = 4;
        try
        {
            foreach (var raw in File.ReadLines("data/welcome-config.txt"))
            {
                var line = raw.Trim();
                if (line.StartsWith('#') || !line.Contains('=')) continue;
                var eq = line.IndexOf('=');
                var k = line[..eq].Trim();
                var v = line[(eq + 1)..].Trim();
                switch (k)
                {
                    case "crew_elsewhere_min_mins":   int.TryParse(v, out crewMin);     break;
                    case "forecast_min_mins":         int.TryParse(v, out forecastMin); break;
                    case "forecast_sighting_hours":   int.TryParse(v, out sightHours);  break;
                    case "forecast_max_entries":      int.TryParse(v, out maxEntries);  break;
                }
            }
        }
        catch { }
        return (crewMin, forecastMin, sightHours, maxEntries);
    }

    // Returns the last censusgeo.csv entry for a GUID: (name, instrument, city).
    private static (string Name, string Instrument, string City) GetCensusgeoEntry(string guid)
    {
        string name = "", instrument = "", city = "";
        try
        {
            foreach (var line in File.ReadLines("data/censusgeo.csv"))
            {
                var cols = line.Split(',');
                if (cols.Length < 4 || cols[0].Trim() != guid) continue;
                name       = HttpUtility.UrlDecode(cols[1].Trim().Replace("+", " "));
                instrument = cols.Length > 2 ? HttpUtility.UrlDecode(cols[2].Trim().Replace("+", " ")) : "";
                city       = HttpUtility.UrlDecode(cols[3].Trim().Replace("+", " "));
            }
        }
        catch { }
        return (name, instrument, city);
    }

    private static string GetArrivingCity(string guid) => GetCensusgeoEntry(guid).City;

    private static Dictionary<string, (string Name, string Instrument, string City)> GetCensusgeoEntries(IEnumerable<string> guids)
    {
        var targets = new HashSet<string>(guids, StringComparer.Ordinal);
        var result = new Dictionary<string, (string Name, string Instrument, string City)>(StringComparer.Ordinal);
        if (targets.Count == 0) return result;
        try
        {
            foreach (var line in File.ReadLines("data/censusgeo.csv"))
            {
                var cols = line.Split(',');
                if (cols.Length < 4) continue;
                string g = cols[0].Trim();
                if (!targets.Contains(g)) continue;
                result[g] = (
                    HttpUtility.UrlDecode(cols[1].Trim().Replace("+", " ")),
                    cols.Length > 2 ? HttpUtility.UrlDecode(cols[2].Trim().Replace("+", " ")) : "",
                    HttpUtility.UrlDecode(cols[3].Trim().Replace("+", " "))
                );
            }
        }
        catch { }
        return result;
    }

    // Looks up name/city for an exact ip:port key in server.csv.
    public static (string name, string city) LookupServer(string serverKey)
    {
        if (serverKey.Length == 0) return ("", "");
        bool hasPort = serverKey.Contains(':');
        string foundName = "", foundCity = "";
        try
        {
            foreach (var line in File.ReadLines("data/server.csv"))
            {
                var cols = line.Split(',');
                if (cols.Length < 3) continue;
                var key = cols[0].Trim();
                bool match = hasPort ? key == serverKey : key.StartsWith(serverKey + ":");
                if (match)
                {
                    foundName = HttpUtility.UrlDecode(cols[1].Trim().Replace("+", " "));
                    foundCity = HttpUtility.UrlDecode(cols[2].Trim().Replace("+", " "));
                }
            }
        }
        catch { }
        if (foundName.Length > 0) return (foundName, foundCity);
        // Fall back to live name from LastReportedList
        try
        {
            foreach (var json in JamulusCacheManager.LastReportedList.Values)
            {
                var servers = JsonSerializer.Deserialize<List<JamFan22.Models.JamulusServers>>(json, _opts);
                if (servers == null) continue;
                foreach (var s in servers)
                {
                    string sKey = $"{s.ip}:{s.port}";
                    bool liveMatch = hasPort ? sKey == serverKey : s.ip == serverKey;
                    if (liveMatch && s.name?.Length > 0)
                        return (s.name, s.city ?? "");
                }
            }
        }
        catch { }
        return ("", "");
    }

    private record ServerHistory(
        int TotalMinutes,
        int MinutesToday,
        int MinutesThisWeek,
        int LastVisitDaysAgo,       // -1 = never
        bool IsTopVisitor,
        List<string> TodayOtherGuids,
        int WeekdayStreak,          // 0 = not applicable; ≥2 = consecutive weeks on WeekdayStreakDow
        DayOfWeek WeekdayStreakDow);

    private static ServerHistory GatherServerHistory(string arrivingGuid, List<string> serverKeys, int nowMinutes)
    {
        if (serverKeys.Count == 0) return new(0, 0, 0, -1, false, new(), 0, DayOfWeek.Sunday);

        CensusIndex.EnsureBuilt();

        var (total, lastMinute, isTop) = CensusIndex.GetGuidOnServer(arrivingGuid, serverKeys);
        int lastDaysAgo = lastMinute < 0 ? -1 : (nowMinutes - lastMinute) / 1440;

        // "today" and "this week" approximate from last-minute only — good enough for context
        int minsAgo = lastMinute < 0 ? int.MaxValue : nowMinutes - lastMinute;
        int minutesToday  = minsAgo < 1440  ? Math.Min(total, 1440)  : 0;
        int minutesWeek   = minsAgo < 10080 ? Math.Min(total, 10080) : 0;

        var todayOthers = CensusIndex.GetRecentVisitors(serverKeys, nowMinutes - 1440, arrivingGuid);

        int weekdayStreak = 0;
        var weekdayDow = DayOfWeek.Sunday;
        if (total > 0 && serverKeys.Count > 0)
        {
            var ptDow = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddDays(nowMinutes / 1440).DayOfWeek;
            int s = CensusIndex.GetGuidWeekdayStreak(arrivingGuid, serverKeys, ptDow, nowMinutes);
            if (s >= 2) { weekdayStreak = s; weekdayDow = ptDow; }
        }

        return new(total, minutesToday, minutesWeek, lastDaysAgo, isTop, todayOthers, weekdayStreak, weekdayDow);
    }

    private record PredictionInfo(bool IsPredicted, int MinutesFromNow,
        List<(string Name, int MinutesFromNow)> Others,
        Dictionary<string, int> ByGuid,           // guid → minutesFromNow, this server only
        Dictionary<string, (int Offset, string ServerName)> ElsewhereByGuid); // guid → (offset, server) at other servers

    private static PredictionInfo GatherPredictions(string arrivingGuid, string serverName, int nowMinutes)
    {
        bool isPredicted = false;
        int predictedOffset = 0;
        var others = new List<(string Name, int MinutesFromNow)>();
        var byGuid = new Dictionary<string, int>(StringComparer.Ordinal);
        var elsewhereByGuid = new Dictionary<string, (int, string)>(StringComparer.Ordinal);
        if (serverName.Length == 0) return new(false, 0, others, byGuid, elsewhereByGuid);

        try
        {
            foreach (var line in File.ReadLines("predicted.csv"))
            {
                var cols = line.Split(',');
                if (cols.Length < 4) continue;
                if (!int.TryParse(cols[0].Trim(), out int predictedMinute)) continue;

                string predServer = HttpUtility.UrlDecode(cols[3].Trim().Replace("+", " "));
                int offset = predictedMinute - nowMinutes;
                string guid = cols[1].Trim();
                string predName = DisplayName(HttpUtility.UrlDecode(cols[2].Trim().Replace("+", " ")));

                if (predServer.Equals(serverName, StringComparison.OrdinalIgnoreCase))
                {
                    if (guid == arrivingGuid)
                    {
                        isPredicted = true;
                        predictedOffset = offset;
                    }
                    else if (Math.Abs(offset) <= 180)
                    {
                        others.Add((predName, offset));
                        if (!byGuid.ContainsKey(guid)) byGuid[guid] = offset;
                    }
                }
                else if (guid != arrivingGuid && offset is > 0 and <= 120)
                {
                    if (!elsewhereByGuid.ContainsKey(guid))
                        elsewhereByGuid[guid] = (offset, predServer);
                }
            }
        }
        catch { }

        return new(isPredicted, predictedOffset, others, byGuid, elsewhereByGuid);
    }

    // Reads livestatus.json and returns a guid→serverKey map of currently active players.
    private static Dictionary<string, string> ReadLiveStatus()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var json = File.ReadAllText("wwwroot/livestatus.json");
            var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var server in doc.RootElement.EnumerateObject())
            {
                if (server.Value.TryGetProperty("clients", out var clients))
                    foreach (var client in clients.EnumerateArray())
                    {
                        var guid = client.GetString();
                        if (guid != null && !result.ContainsKey(guid))
                            result[guid] = server.Name;
                    }
            }
        }
        catch { }
        return result;
    }

    private static List<(string guid, int mins)> TopCoJammerPairs(string arrivingGuid, int minMins = 30)
    {
        // Start with runtime-accumulated data (precise but resets on restart)
        var combined = new Dictionary<string, int>(StringComparer.Ordinal);
        if (EncounterTracker.m_timeTogether != null)
        {
            foreach (var kvp in EncounterTracker.m_timeTogether)
            {
                if (kvp.Key.Length != 64) continue;
                if (kvp.Key.Substring(0, 32) != arrivingGuid && kvp.Key.Substring(32, 32) != arrivingGuid) continue;
                string other = kvp.Key.Substring(0, 32) == arrivingGuid ? kvp.Key.Substring(32, 32) : kvp.Key.Substring(0, 32);
                combined[other] = (int)kvp.Value.TotalMinutes;
            }
        }

        // Supplement with 90-day census approximation for any pair with no runtime data
        foreach (var (otherGuid, approxMins) in CensusIndex.GetApproxCoJammers(arrivingGuid))
        {
            if (!combined.ContainsKey(otherGuid))
                combined[otherGuid] = approxMins;
        }

        return combined
            .Where(kv => kv.Value >= minMins)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => (kv.Key, kv.Value))
            .Take(10)
            .ToList();
    }

    private static bool IsLobbyBot(string name) =>
        name.Contains("lobby", StringComparison.OrdinalIgnoreCase);

    private static bool IsBluesRockServer(string serverName) =>
        serverName.Contains("Blues/Rock", StringComparison.OrdinalIgnoreCase);

    private static string OrdinalSuffix(int n) =>
        n % 100 is >= 11 and <= 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };

    private static int HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371;
        double dLat = (lat2 - lat1) * Math.PI / 180;
        double dLon = (lon2 - lon1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return (int)(R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a)));
    }

    private static bool IsNoName(string name) =>
        name.Trim().Equals("No Name", StringComparison.OrdinalIgnoreCase);

    private static string DisplayName(string name) =>
        IsNoName(name) ? "unnamed player" : name;

    // Co-jammers currently active on a different server (shown when players ARE in the room).
    private static List<(string Name, string Guid, string ServerKey, string ServerName, int MinsTogether)> FindUsualCrewElsewhere(
        string arrivingGuid, List<string> currentServerKeys, Dictionary<string, string> liveStatus,
        int minMins = 30)
    {
        var results = new List<(string Name, string Guid, string ServerKey, string ServerName, int MinsTogether)>();
        foreach (var (guid, mins) in TopCoJammerPairs(arrivingGuid, minMins))
        {
            if (!liveStatus.TryGetValue(guid, out var sightingServer)) continue;
            if (currentServerKeys.Contains(sightingServer)) continue;
            string name = EncounterTracker.m_guidNamePairs.TryGetValue(guid, out var n)
                          ? System.Web.HttpUtility.HtmlDecode(n).Trim()
                          : GetCensusgeoEntry(guid).Name;
            if (name.Length == 0 || IsLobbyBot(name) || IsNoName(name)) continue;
            string serverName = LookupServer(sightingServer).name;
            if (IsBluesRockServer(serverName)) continue;
            results.Add((name, guid, sightingServer, serverName, mins));
            if (results.Count >= 3) break;
        }
        return results;
    }

    private record CoJammerForecast(string Guid, string Name, int MinsTogether, string? LiveServer, string? LiveServerName,
        int? PredictedHere, int? LastSeenMinsAgo, int? LastTogetherDaysAgo);

    // For empty servers: top co-jammers with strong signals (currently live, predicted here, or seen recently).
    private static List<CoJammerForecast> GatherCoJammerForecast(
        string arrivingGuid, List<string> currentServerKeys,
        Dictionary<string, string> liveStatus, Dictionary<string, int> predictedAtServer,
        int forecastMinMins, int forecastSightingHours, int forecastMaxEntries)
    {
        var sightingCutoff = DateTime.Now.AddHours(-forecastSightingHours);
        var results = new List<CoJammerForecast>();

        foreach (var (guid, mins) in TopCoJammerPairs(arrivingGuid, minMins: forecastMinMins))
        {
            string? liveServer = liveStatus.TryGetValue(guid, out var s) ? s : null;
            int? predictedHere = predictedAtServer.TryGetValue(guid, out var p) ? p : null;

            // Recent sighting: most recent m_connectionLatestSighting entry for this guid within window
            int? lastSeenMinsAgo = null;
            if (liveServer == null)
            {
                var recent = EncounterTracker.m_connectionLatestSighting
                    .Where(kvp => kvp.Key.StartsWith(guid) && kvp.Value >= sightingCutoff)
                    .OrderByDescending(kvp => kvp.Value)
                    .FirstOrDefault();
                if (recent.Key != null)
                    lastSeenMinsAgo = (int)(DateTime.Now - recent.Value).TotalMinutes;
            }

            if (liveServer == null && predictedHere == null && lastSeenMinsAgo == null) continue;
            if (liveServer != null && currentServerKeys.Contains(liveServer)) continue;

            string name = EncounterTracker.m_guidNamePairs.TryGetValue(guid, out var n)
                          ? System.Web.HttpUtility.HtmlDecode(n).Trim()
                          : GetCensusgeoEntry(guid).Name;
            if (name.Length == 0 || IsLobbyBot(name) || IsNoName(name)) continue;

            string? liveServerName = liveServer != null ? LookupServer(liveServer).name : null;
            if (liveServerName != null && IsBluesRockServer(liveServerName)) continue;

            int? lastTogetherDaysAgo = null;
            if (EncounterTracker.m_timeTogetherUpdated != null)
            {
                var pairKey = EncounterTracker.CanonicalTwoHashes(arrivingGuid, guid);
                if (EncounterTracker.m_timeTogetherUpdated.TryGetValue(pairKey, out var lastDate))
                    lastTogetherDaysAgo = (int)(DateTime.Now - lastDate).TotalDays;
            }

            results.Add(new(guid, name, mins, liveServer, liveServerName, predictedHere, lastSeenMinsAgo, lastTogetherDaysAgo));
            if (results.Count >= forecastMaxEntries) break;
        }
        return results;
    }

    private static volatile Dictionary<string, int>? _instrumentCounts = null;
    private static DateTime _instrumentCountsBuiltAt = DateTime.MinValue;

    private static readonly Dictionary<string, string> _instrumentCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Guitar"] = "strings",          ["Electric Guitar"] = "strings", ["Acoustic Guitar"] = "strings",
        ["Guitar Vocal"] = "strings",    ["Violin"] = "strings",          ["Viola"] = "strings",
        ["Cello"] = "strings",           ["Mandolin"] = "strings",        ["Banjo"] = "strings",
        ["Ukulele"] = "strings",         ["Bass Ukulele"] = "strings",    ["Mountain Dulcimer"] = "strings",
        ["Harp"] = "strings",
        ["Keyboard"] = "keys",           ["Grand Piano"] = "keys",        ["Synthesizer"] = "keys",
        ["Accordion"] = "keys",          ["Keyboard Vocal"] = "keys",     ["Vibraphone"] = "keys",
        ["Drums"] = "drums",             ["Bongo"] = "drums",             ["Congas"] = "drums",
        ["Djembe"] = "drums",            ["Bodhran"] = "drums",           ["Scratching"] = "drums",
        ["Bass Guitar"] = "bass",        ["Double Bass"] = "bass",
        ["Flute"] = "wind",              ["Saxophone"] = "wind",          ["Trumpet"] = "wind",
        ["Clarinet"] = "wind",           ["Recorder"] = "wind",           ["French Horn"] = "wind",
        ["Trombone"] = "wind",           ["Tuba"] = "wind",               ["Bassoon"] = "wind",
        ["Oboe"] = "wind",               ["Harmonica"] = "wind",
        ["Vocal"] = "voice",             ["Vocal Soprano"] = "voice",     ["Vocal Alto"] = "voice",
        ["Vocal Tenor"] = "voice",       ["Vocal Baritone"] = "voice",    ["Vocal Lead"] = "voice",
        ["Vocal Bass"] = "voice",        ["Microphone"] = "voice",        ["Rapping"] = "voice",
    };
    private static readonly Dictionary<string, string> _categoryLabel = new()
    {
        ["strings"] = "string/guitar player", ["keys"] = "keyboard/piano player",
        ["drums"] = "drummer",                ["bass"] = "bass player",
        ["wind"] = "wind player",             ["voice"] = "vocalist",
    };

    private static int GetNetworkInstrumentCount(string instrument)
    {
        if (instrument.Length == 0) return 0;
        if (_instrumentCounts == null || (DateTime.UtcNow - _instrumentCountsBuiltAt).TotalHours > 1)
        {
            var seen = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var line in File.ReadLines("data/censusgeo.csv"))
                {
                    var cols = line.Split(',');
                    if (cols.Length < 3) continue;
                    string guid = cols[0].Trim();
                    string inst = System.Web.HttpUtility.UrlDecode(cols[2].Trim().Replace("+", " ")).Trim();
                    if (inst.Length == 0) continue;
                    if (!seen.TryGetValue(inst, out var guids)) seen[inst] = guids = new HashSet<string>(StringComparer.Ordinal);
                    guids.Add(guid);
                }
            }
            catch { }
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in seen) counts[kv.Key] = kv.Value.Count;
            _instrumentCounts = counts;
            _instrumentCountsBuiltAt = DateTime.UtcNow;
        }
        return _instrumentCounts.TryGetValue(instrument, out var c) ? c : 0;
    }

    // Returns true when context is rich enough to justify an LLM call.
    public static bool IsRich(string contextText) =>
        contextText.Length > 0 && !contextText.Contains("Nothing notable");

    private record StreamState(bool IsActive, bool IsFree, string ActiveServer);

    private static StreamState ReadStreamState()
    {
        try
        {
            var json = File.ReadAllText("data/stream-gate.json");
            var doc = JsonDocument.Parse(json);
            string activeIp = doc.RootElement.GetProperty("ActiveIp").GetString() ?? "";
            string activeServer = doc.RootElement.GetProperty("JamulusServer").GetString() ?? "";
            var expiry = doc.RootElement.GetProperty("ExpiryUtc").GetDateTime();
            bool expired = DateTime.UtcNow >= expiry;
            return new(!expired && activeServer.Length > 0, expired || activeServer.Length == 0, activeServer);
        }
        catch { return new(false, true, ""); }
    }

    private static LoreEntry? LoadServerLore(string serverKey)
    {
        try
        {
            var json = File.ReadAllText("data/server-lore.json");
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(serverKey, out var el))
                return JsonSerializer.Deserialize<LoreEntry>(el.GetRawText(), _opts);
        }
        catch { }
        return null;
    }

    private static Dictionary<string, LoreEntry> LoadAllServerLore()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, LoreEntry>>(
            File.ReadAllText("data/server-lore.json"), _opts) ?? new(); }
        catch { return new(); }
    }

    private static Dictionary<string, SessionEntry> LoadSessionRegulars()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, SessionEntry>>(
            File.ReadAllText("data/session-regulars.json"), _opts) ?? new(); }
        catch { return new(); }
    }

    private static DateTime NextSessionOccurrence(DayOfWeek dow, int startHour, DateTime utcNow)
    {
        int days = ((int)dow - (int)utcNow.DayOfWeek + 7) % 7;
        var t = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, startHour, 0, 0, DateTimeKind.Utc).AddDays(days);
        return t <= utcNow ? t.AddDays(7) : t;
    }

    private static string FormatSessionRegulars(List<string>? regulars) =>
        regulars == null || regulars.Count == 0 ? "" :
        regulars.Count == 1 ? $" — with {regulars[0]}" :
        regulars.Count == 2 ? $" — with {regulars[0]} and {regulars[1]}" :
        $" — with {string.Join(", ", regulars)}";

    private static bool IsStudioDFirstHour(DateTime utcNow)
    {
        try
        {
            var arr = JsonSerializer.Deserialize<List<JsonElement>>(
                File.ReadAllText("data/stream-reservations.json"), _opts);
            if (arr == null) return false;
            foreach (var r in arr)
            {
                if (!r.TryGetProperty("JamulusServer", out var js)) continue;
                if (js.GetString() != "24.199.127.71:22224") continue;
                int dow = r.GetProperty("DayOfWeek").GetInt32();
                int startHour = r.GetProperty("StartHour").GetInt32();
                return (int)utcNow.DayOfWeek == dow && utcNow.Hour == startHour;
            }
        }
        catch { }
        return false;
    }

    private static bool IsJazzDirectoryServer(string serverKey)
    {
        try
        {
            if (JamulusCacheManager.LastReportedList.TryGetValue("Genre Jazz", out var json))
            {
                var servers = JsonSerializer.Deserialize<List<JamFan22.Models.JamulusServers>>(json);
                if (servers != null)
                    foreach (var s in servers)
                        if ($"{s.ip}:{s.port}" == serverKey) return true;
            }
        }
        catch { }
        return false;
    }

    public static async Task<GatherResult> GatherAsync(string arrivingGuid, string serverKey, int rpcPort, string nationCode, int arrivingChannelId = -1, string? playerIp = null)
    {
        var (crewMinMins, forecastMinMins, forecastSightHours, forecastMaxEntries) = ReadConfig();
        string serverIp = serverKey.Contains(':') ? serverKey.Split(':')[0] : serverKey;
        var streamState = ReadStreamState();
        bool isStudioD = serverKey == "24.199.127.71:22224";
        var serverLore = LoadServerLore(serverKey);
        bool streamActiveHere = streamState.IsActive && streamState.ActiveServer.StartsWith(serverIp + ":");

        // Primary: LastReportedList (instant, works for any server)
        var playersFromList = GetPlayersFromLastReportedList(serverKey);

        // Supplement: fleet RPC gives fresher data for fleet servers (2s timeout, non-blocking)
        Task<List<RpcClientEntry>>? rpcTask = rpcPort > 0
            ? GetClientsAsync(serverIp, rpcPort)
            : null;

        var (serverName, serverCity) = LookupServer(serverKey);
        var serverKeys = serverKey.Contains(':') ? new List<string> { serverKey } : new List<string>();
        int nowMinutes = JamulusCacheManager.MinutesSince2023AsInt();

        var historyTask = Task.Run(() => GatherServerHistory(arrivingGuid, serverKeys, nowMinutes));
        var homeServerTask = Task.Run(() => {
            CensusIndex.EnsureBuilt();
            return CensusIndex.GetGuidServers(arrivingGuid).FirstOrDefault();
        });
        var cityTask = Task.Run(() => GetArrivingCity(arrivingGuid));
        var serverTzTask = IpAnalyticsService.FetchIpApiAsync(serverIp);
        var playerGeoTask = !string.IsNullOrEmpty(playerIp)
            ? IpAnalyticsService.FetchIpApiAsync(playerIp)
            : Task.FromResult<Newtonsoft.Json.Linq.JObject>(null!);
        var predictions = GatherPredictions(arrivingGuid, serverName, nowMinutes);

        // Prefer RPC result when available (includes fresh channelId data); fall back to list
        var players = playersFromList;
        if (rpcTask != null)
        {
            try
            {
                var rpcPlayers = await rpcTask;
                if (rpcPlayers.Count > 0) players = rpcPlayers;
            }
            catch (Exception ex) { Console.WriteLine($"[WELCOME-CTX] getClients fallback to LastReportedList: {ex.Message}"); }
        }

        // Retry once if arriver is absent or has blank metadata (CHANNEL_INFO race, ~20-200ms window)
        if (rpcPort > 0 && arrivingChannelId >= 0)
        {
            var arriver = players.FirstOrDefault(p => p.ChannelId == arrivingChannelId);
            if (arriver == null || string.IsNullOrEmpty(arriver.Name))
            {
                Console.WriteLine($"[WELCOME-CTX] arriver absent/unidentified (channelId={arrivingChannelId}), retrying in 500ms");
                await Task.Delay(500);
                try
                {
                    var retryPlayers = await GetClientsAsync(serverIp, rpcPort);
                    if (retryPlayers.Count > 0) players = retryPlayers;
                }
                catch (Exception ex) { Console.WriteLine($"[WELCOME-CTX] retry getClients failed: {ex.Message}"); }
            }
        }

        var history = await historyTask;
        var homeServer = await homeServerTask;
        bool isHomeServer = homeServer.ServerKey != null && serverKeys.Contains(homeServer.ServerKey) && homeServer.Total >= 60;
        bool hasDistinctHomeServer = homeServer.ServerKey != null && !serverKeys.Contains(homeServer.ServerKey) && homeServer.Total >= 60;
        var arrivingCity = await cityTask;

        var serverIpApiJson = await serverTzTask;
        var playerGeoJson = await playerGeoTask;
        TimeZoneInfo? serverTz = null;
        var serverTzId = serverIpApiJson?["timezone"]?.ToString();
        if (serverTzId != null)
            try { serverTz = TimeZoneInfo.FindSystemTimeZoneById(serverTzId); } catch { }
        var utcNowDt = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(nowMinutes);
        string serverLocalDowName = (serverTz != null
            ? TimeZoneInfo.ConvertTimeFromUtc(utcNowDt, serverTz)
            : utcNowDt).DayOfWeek.ToString();

        // Player geolocation: distance from server and local time
        double? serverLat = null, serverLon = null;
        if (serverIpApiJson != null &&
            double.TryParse(serverIpApiJson["lat"]?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double _sLat) &&
            double.TryParse(serverIpApiJson["lon"]?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double _sLon))
        { serverLat = _sLat; serverLon = _sLon; }

        int? playerDistKm = null;
        string? playerGeoNote = null;
        if (playerGeoJson != null)
        {
            if (serverLat.HasValue &&
                double.TryParse(playerGeoJson["lat"]?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double pLat) &&
                double.TryParse(playerGeoJson["lon"]?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double pLon))
            {
                playerDistKm = HaversineKm(pLat, pLon, serverLat.Value, serverLon!.Value);
            }
            var playerTzId = playerGeoJson["timezone"]?.ToString();
            if (playerTzId != null)
            {
                try
                {
                    var playerTz = TimeZoneInfo.FindSystemTimeZoneById(playerTzId);
                    var playerLocalTime = TimeZoneInfo.ConvertTimeFromUtc(utcNowDt, playerTz);
                    int playerOff = (int)playerTz.GetUtcOffset(utcNowDt).TotalHours;
                    int serverOff = serverTz != null ? (int)serverTz.GetUtcOffset(utcNowDt).TotalHours : 0;
                    bool isFar = playerDistKm.HasValue && playerDistKm.Value >= 5000;
                    bool isTzFar = Math.Abs(playerOff - serverOff) >= 5;
                    if (isFar || isTzFar)
                    {
                        string timeStr = playerLocalTime.ToString("h:mmtt").ToLower();
                        int roundedKm = playerDistKm.HasValue ? ((playerDistKm.Value + 250) / 500) * 500 : 0;
                        string distStr = isFar ? $"~{roundedKm:N0} km from server" : "";
                        string timeNote = isTzFar ? $"it's {timeStr} for them" : "";
                        playerGeoNote = (distStr.Length > 0 && timeNote.Length > 0)
                            ? $"{distStr} — {timeNote}"
                            : distStr.Length > 0 ? distStr : timeNote;
                    }
                }
                catch { }
            }
        }

        var liveStatus = ReadLiveStatus();
        var usualCrewElsewhere = FindUsualCrewElsewhere(arrivingGuid, serverKeys, liveStatus, crewMinMins);
        var coJammerForecast = GatherCoJammerForecast(arrivingGuid, serverKeys, liveStatus, predictions.ByGuid,
            forecastMinMins, forecastSightHours, forecastMaxEntries);

        var others = players
            .Where(p => !string.IsNullOrEmpty(p.Name) && !IsLobbyBot(p.Name)
                     && EncounterTracker.GetHash(p.Name, p.Country, p.Instrument) != arrivingGuid)
            .ToList();

        int lobbyAudience = players
            .Where(p => IsLobbyBot(p.Name))
            .Sum(p => { var m = System.Text.RegularExpressions.Regex.Match(p.Name, @"\[(\d+)\]");
                        return m.Success ? int.Parse(m.Groups[1].Value) : 0; });
        bool lobbyPresent = players.Any(p => !string.IsNullOrEmpty(p.Name) && IsLobbyBot(p.Name));

        var webIps = await GetWebUserIpsAsync();
        bool arrivingIsWebUser = !string.IsNullOrEmpty(playerIp) && webIps.Contains(playerIp);
        bool hasWebUser = arrivingIsWebUser
            || others.Any(p => !string.IsNullOrEmpty(p.Address) && webIps.Contains(p.Address.Split(':')[0]));

        // Language vote: arriving player + each room member + server (1 vote each), English on tie
        var langVotes = new Dictionary<string, int>(StringComparer.Ordinal);
        void AddVote(string lang) { langVotes[lang] = langVotes.GetValueOrDefault(lang) + 1; }
        AddVote(LanguageFor(nationCode));
        foreach (var p in others)
            AddVote(p.Country.Length <= 3 ? LanguageFor(p.Country) : LanguageForCountryName(p.Country));
        var serverCountryName = serverIpApiJson?["countryName"]?.ToString();
        if (serverCountryName != null) AddVote(LanguageForCountryName(serverCountryName));
        string chosenLanguage = "English";
        if (langVotes.Count > 0)
        {
            int maxVotes = langVotes.Values.Max();
            var tied = langVotes.Where(kv => kv.Value == maxVotes).Select(kv => kv.Key).ToList();
            chosenLanguage = tied[Random.Shared.Next(tied.Count)];
        }

        var coGeoGuids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in usualCrewElsewhere) coGeoGuids.Add(c.Guid);
        foreach (var c in coJammerForecast) coGeoGuids.Add(c.Guid);
        foreach (var p in others) coGeoGuids.Add(EncounterTracker.GetHash(p.Name, p.Country, p.Instrument));
        var coBios = GetCensusgeoEntries(coGeoGuids);

        var nameColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in usualCrewElsewhere)
        { var n = c.Name.Trim(); if (n.Length > 0) nameColors[n] = "#FFA500"; }
        foreach (var c in coJammerForecast)
        {
            var n = c.Name.Trim();
            if (n.Length == 0) continue;
            if (c.LiveServer != null || c.PredictedHere != null)
                nameColors[n] = "#FFA500";
            else if (c.LastSeenMinsAgo != null && !nameColors.ContainsKey(n))
                nameColors[n] = "#aaa";
        }
        foreach (var p in others)
        {
            var pn = p.Name?.Trim() ?? "";
            if (pn.Length == 0 || IsNoName(pn)) continue;
            nameColors[pn] = "#7EC8E3";
            // Also register the first word so LLM-truncated names like "Gioca69" from "Gioca69 no mic" still get colored
            var firstWord = pn.Split(' ')[0];
            if (firstWord.Length > 1 && firstWord != pn && !nameColors.ContainsKey(firstWord))
                nameColors[firstWord] = "#7EC8E3";
        }

        // Group weekday streak: min individual streak among regulars with ≥5 consecutive weeks
        int groupWeekdayStreak = 0;
        int groupWeekdayCount = 0;
        if (history.WeekdayStreak >= 2 && coGeoGuids.Count > 0)
        {
            var streaks = new List<int> { history.WeekdayStreak };
            foreach (var g in coGeoGuids)
            {
                int s = CensusIndex.GetGuidWeekdayStreak(g, serverKeys, history.WeekdayStreakDow, nowMinutes);
                if (s >= 5) streaks.Add(s);
            }
            if (streaks.Count >= 2) { groupWeekdayStreak = streaks.Min(); groupWeekdayCount = streaks.Count; }
        }

        // Cross-server pair streak: any shared server on today's DOW (doesn't require same server)
        var crossStreakMembers = new List<string>(); // display names of members with pair streak
        if (coGeoGuids.Count > 0 && arrivingGuid.Length == 32)
        {
            DayOfWeek todayDow = DateTime.UtcNow.DayOfWeek;
            foreach (var g in coGeoGuids)
            {
                int ps = CensusIndex.GetPairWeekdayStreak(arrivingGuid, g, todayDow, nowMinutes);
                if (ps >= 2 && coBios.TryGetValue(g, out var bio) && bio.Name.Length > 0)
                    crossStreakMembers.Add(bio.Name);
            }
        }

        int currentNetworkClients = JamulusCacheManager.NetworkClientCount;
        int typicalNetworkClients = CensusIndex.GetTypicalNetworkSizeAtHour(DateTime.UtcNow.Hour, nowMinutes);

        var sb = new StringBuilder();
        sb.AppendLine($"Current UTC: {DateTime.UtcNow:dddd HH:mm}");
        if (currentNetworkClients > 0 && typicalNetworkClients > 0)
        {
            double ratio = (double)currentNetworkClients / typicalNetworkClients;
            string busyness = ratio >= 1.3 ? $"busier than usual ({currentNetworkClients} players, typical {typicalNetworkClients})" :
                              ratio <= 0.7 ? $"quieter than usual ({currentNetworkClients} players, typical {typicalNetworkClients})" :
                              $"normal activity ({currentNetworkClients} players on the network)";
            sb.AppendLine($"Network: {busyness}");
        }
        else if (currentNetworkClients > 0)
        {
            sb.AppendLine($"Network: {currentNetworkClients} players currently on Jamulus");
        }
        sb.AppendLine();

        // Identify arriving player
        string arrivingName = "(unknown)";
        string arrivingInstrument = "";
        string arrivingCountry = nationCode;
        foreach (var p in players)
        {
            if (EncounterTracker.GetHash(p.Name, p.Country, p.Instrument) == arrivingGuid)
            {
                arrivingName = (p.Name.Trim().Length == 0 || IsNoName(p.Name)) ? "(unknown)" : p.Name;
                arrivingInstrument = p.Instrument;
                arrivingCountry = p.Country.Length > 0 ? p.Country : nationCode;
                break;
            }
        }
        if (arrivingName == "(unknown)")
        {
            if (EncounterTracker.m_guidNamePairs.TryGetValue(arrivingGuid, out var knownName))
                arrivingName = (knownName.Trim().Length == 0 || IsNoName(knownName)) ? "(unknown)" : knownName;
            else
            {
                var cgeo = GetCensusgeoEntry(arrivingGuid);
                if (cgeo.Name.Length > 0 && !IsNoName(cgeo.Name)) { arrivingName = cgeo.Name; arrivingInstrument = cgeo.Instrument; }
            }
        }

        // Lobby bots must never receive a welcome or appear in room relationships
        if (IsLobbyBot(arrivingName))
        {
            Console.WriteLine($"[WARN-WELCOME-EMPTY-CTX] lobby-bot guard: name={arrivingName} guid={arrivingGuid} server={serverKey}");
            return new GatherResult("", new Dictionary<string, string>(), new List<int>(), false);
        }
        // Detect hash-mismatch self-in-room (RPC returning empty country/instrument for arriving player)
        if (arrivingName != "(unknown)")
        {
            foreach (var p in others.Where(p => string.Equals(p.Name?.Trim(), arrivingName, StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine($"[WARN-WELCOME-SELF-IN-ROOM] name={p.Name} rpc_country=\"{p.Country}\" rpc_instrument=\"{p.Instrument}\" expected_guid={arrivingGuid} rpc_hash={EncounterTracker.GetHash(p.Name, p.Country, p.Instrument)} server={serverKey}");
        }

        bool isDefaultName = arrivingName == "(unknown)";

        string arrivingExp = "";
        int lifetimeMins = !isDefaultName ? CensusIndex.GetGuidLifetime(arrivingGuid) : 0;
        bool isNetworkNewcomer = lifetimeMins > 0 && lifetimeMins < 30;
        bool isVeryExperienced = lifetimeMins >= 18000; // 300+ hours
        if (lifetimeMins == 0)
            arrivingExp = "";
        else if (isNetworkNewcomer)
            arrivingExp = $" — NEWCOMER TO JAMULUS: only {lifetimeMins} min total on the whole network";
        else if (lifetimeMins < 120)
            arrivingExp = $" — very new: {lifetimeMins} min total on Jamulus";
        else
        {
            int hours = lifetimeMins / 60;
            int days = lifetimeMins / 1440;
            string vetTag = isVeryExperienced ? " — HIGHLY EXPERIENCED MUSICIAN" : "";
            arrivingExp = days >= 2 ? $" — {days} days on Jamulus{vetTag}" : $" — {hours}h on Jamulus{vetTag}";
        }

        int instrumentNetworkCount = arrivingInstrument.Length > 0 ? GetNetworkInstrumentCount(arrivingInstrument) : 0;
        bool isRareInstrument = instrumentNetworkCount > 0 && instrumentNetworkCount < 15;

        int visitStreak = !isDefaultName ? CensusIndex.GetGuidStreak(arrivingGuid, nowMinutes) : 0;

        // Room friends — placed first so model leads with who is present in the room
        // Skip for default-named players: time-together data is unreliable for "No Name"
        var significantRoom = isDefaultName ? new List<(string Name, int Mins)>() : others
            .Select(p => {
                var pg = EncounterTracker.GetHash(p.Name, p.Country, p.Instrument);
                var pk = EncounterTracker.CanonicalTwoHashes(arrivingGuid, pg);
                int mins = 0;
                if (EncounterTracker.m_timeTogether != null &&
                    EncounterTracker.m_timeTogether.TryGetValue(pk, out var ts))
                    mins = (int)ts.TotalMinutes;
                return (Name: p.Name?.Trim() ?? "", Mins: mins);
            })
            .Where(x => x.Name.Length > 0 && x.Mins >= 60 && !IsNoName(x.Name))
            .OrderByDescending(x => x.Mins)
            .ToList();
        if (significantRoom.Count > 0)
        {
            var roomDesc = string.Join(", ", significantRoom.Select(x =>
                $"{x.Name} ({(x.Mins >= 60 ? $"{x.Mins / 60}h" : $"{x.Mins}min")} together)"));
            sb.AppendLine($"IN THE ROOM: {roomDesc}");
            sb.AppendLine();
        }

        // Crew elsewhere — after room presence
        if (usualCrewElsewhere.Count > 0 && !isDefaultName)
        {
            var names = usualCrewElsewhere.Select(c => {
                string minsStr = c.MinsTogether >= 60 ? $"{c.MinsTogether / 60}h together" : $"{c.MinsTogether} min together";
                bool sameCity = arrivingCity.Length > 0 && coBios.TryGetValue(c.Guid, out var cbio)
                    && cbio.City.Equals(arrivingCity, StringComparison.OrdinalIgnoreCase);
                string cityNote = sameCity ? $", also from {arrivingCity}" : "";
                return $"{c.Name} ({minsStr}{cityNote})";
            });
            sb.AppendLine($"Also online right now: {string.Join(", ", names)}");
            sb.AppendLine();
        }

        if (serverName.Length > 0 || serverCity.Length > 0)
            sb.AppendLine($"Server: {serverName}" + (serverCity.Length > 0 ? $" ({serverCity})" : ""));
        if (serverLore != null)
        {
            if (serverLore.Tagline?.Length > 0)
                sb.AppendLine($"Server identity: {serverLore.Tagline}");
            if (serverLore.Themes?.Count > 0)
                sb.AppendLine($"Server themes: {string.Join(", ", serverLore.Themes)}");
            if (serverLore.Events?.Count > 0)
            {
                foreach (var ev in serverLore.Events)
                {
                    var parts = new List<string>();
                    if (ev.Schedule?.Length > 0) parts.Add(ev.Schedule);
                    if (ev.Description?.Length > 0) parts.Add(ev.Description);
                    if (ev.ListenUrl?.Length > 0) parts.Add($"listen: {ev.ListenUrl}");
                    sb.AppendLine($"Event — {ev.Name}: {string.Join("; ", parts)}");
                }
            }
        }
        if (IsJazzDirectoryServer(serverKey))
            sb.AppendLine("Server genre: Jazz (inferred — players here may not actually be playing jazz, so keep any reference low-key)");
        // Session hype — inject for the arriving server and same-host fleet servers
        var sessionRegulars = LoadSessionRegulars();
        var utcNow = DateTime.UtcNow;
        foreach (var (sessKey, sess) in sessionRegulars)
        {
            string sessIp = sessKey.Contains(':') ? sessKey.Split(':')[0] : sessKey;
            bool isOwn    = sessKey == serverKey;
            bool isNearby = !isOwn && sessIp == serverIp;
            if (!isOwn && !isNearby) continue;

            var next = NextSessionOccurrence((DayOfWeek)sess.DayOfWeek, sess.StartHour, utcNow);
            if ((next - utcNow).TotalHours is < 0 or > 16) continue;

            string regPhrase = FormatSessionRegulars(sess.Regulars);
            string sessDay = next.DayOfWeek == utcNow.DayOfWeek ? "tonight" : next.ToString("dddd");
            if (isOwn)
                sb.AppendLine($"Upcoming session {sessDay}: {sess.SessionName}{regPhrase}");
            else
            {
                var (nearbyName, _) = LookupServer(sessKey);
                sb.AppendLine($"{(sessDay == "tonight" ? "Tonight" : sessDay)} on nearby {nearbyName}: {sess.SessionName}{regPhrase}");
            }
        }

        // Nearby non-fleet server auto-events within 800 km starting in the next 6 hours
        if (serverLore?.Lat != null && serverLore?.Lon != null)
        {
            var allLore = LoadAllServerLore();
            foreach (var (loreKey, loreEntry) in allLore)
            {
                if (loreKey == serverKey) continue;
                if (loreEntry.Lat == null || loreEntry.Lon == null) continue;
                if (HaversineKm(serverLore.Lat.Value, serverLore.Lon.Value, loreEntry.Lat.Value, loreEntry.Lon.Value) > 800) continue;
                if (loreEntry.Events == null) continue;
                foreach (var ev in loreEntry.Events)
                {
                    if (ev.Auto != true || ev.Weekday == null || ev.Hour == null) continue;
                    var evDow = (DayOfWeek)((ev.Weekday.Value + 1) % 7);
                    var next = NextSessionOccurrence(evDow, ev.Hour.Value, utcNow);
                    double hours = (next - utcNow).TotalHours;
                    if (hours < 0 || hours > 6) continue;
                    string label = ev.Name;
                    sb.AppendLine($"Nearby session in {(int)hours}h: {label} — {ev.Schedule}");
                }
            }
        }

        // Global tip: Marsha K's Jazz Jam first-hour promotion to any fleet server worldwide
        if (!isStudioD && IsStudioDFirstHour(utcNow))
        {
            string occ = others.Count == 0 ? "empty right now" :
                         others.Count == 1 ? "just 1 other player here" :
                         $"{others.Count} players here";
            sb.AppendLine($"Global hot tip: Marsha K's Jazz Jam just started on Studio D — live right now, runs at least another hour. This server is {occ}. Invite the player to join (it's Studio D on Jamulus) but note that many just listen in at https://StudioD.live — it's pretty far for most. Tip them to catch it while it's hot.");
        }

        int? minsUntilLobbyStream = JamFan22.StreamGate.MinutesUntilNextScheduledStream(serverKey);
        string streamUrl = serverLore?.Events?.FirstOrDefault(e => e.ListenUrl?.Length > 0)?.ListenUrl
                           ?? "https://ear.jamulus.live";
        if (streamActiveHere)
            sb.AppendLine($"This server is streaming live right now at {streamUrl}. Include this line in your message: \"Non-Jamulus fans can listen at {streamUrl}!\"");
        else if (lobbyPresent)
            sb.AppendLine($"A lobby client is holding the streaming slot open on this server. When live, non-Jamulus friends can listen at {streamUrl}. Mention this naturally — e.g., \"if your friends want to follow along from outside Jamulus, they can listen at {streamUrl} when we're streaming.\"");
        else if (minsUntilLobbyStream.HasValue)
            sb.AppendLine($"Stream starts in {minsUntilLobbyStream.Value} minutes at {streamUrl} — mention this, not /stream.");
        else if (streamState.IsFree && JamFan22.StreamGate.IsEligibleServer(serverIp) && others.Count >= 2)
        {
            if (playerGeoNote != null)
                sb.AppendLine("Stream slot: free (faraway player) — frame /stream as: if they'd rather just listen in, type /stream and they'll get a listen link. Don't frame it as sharing with friends.");
            else
                sb.AppendLine("Stream slot: free — tell the arriving player they can type /stream to let their non-Jamulus friends listen in.");
        }
        if (lobbyAudience > 0)
            sb.AppendLine($"Listening audience: {lobbyAudience} {(lobbyAudience == 1 ? "person is" : "people are")} tuned in to this server's live stream right now");
        string arrivingCityNote = arrivingCity.Length > 0 ? $" (city: {arrivingCity})" : "";
        string nameLabel = arrivingName != "(unknown)" ? $": {arrivingName}" : "";
        sb.AppendLine($"Arriving musician{nameLabel}" +
                      (arrivingInstrument.Length > 0 ? $" ({arrivingInstrument})" : "") +
                      (arrivingCountry.Length > 0 ? $" from {arrivingCountry}" : "") +
                      arrivingCityNote + arrivingExp);
        if (playerGeoNote != null)
            sb.AppendLine($"Arriving player's location: {playerGeoNote}.");
        sb.AppendLine($"Language to use for message: {LanguageFor(nationCode)}");
        if (arrivingIsWebUser)
            sb.AppendLine("Known web user: yes — this player has explored the network's public tools. Skip the https://jamulus.live link (they already know it). Skip any generic orientation. Speak with specificity about this server and who's here.");
        sb.AppendLine();

        // Server history for arriving player — suppressed for default-named players (unreliable GUID)
        if (!isDefaultName)
        {
            if (history.TotalMinutes > 0)
            {
                string topNote = history.IsTopVisitor ? " — all-time top player on this server" : "";
                if (history.WeekdayStreak >= 2)
                {
                    string streakPhrase;
                    if (history.WeekdayStreak == 2)
                        streakPhrase = $"second {serverLocalDowName} here";
                    else if (history.WeekdayStreak <= 4)
                    {
                        string[] spelled = { "", "first", "second", "third", "fourth" };
                        streakPhrase = $"{spelled[history.WeekdayStreak]} {serverLocalDowName} here";
                    }
                    else
                    {
                        string ord = OrdinalSuffix(history.WeekdayStreak);
                        streakPhrase = $"{history.WeekdayStreak}{ord} {serverLocalDowName} in a row";
                    }
                    sb.AppendLine($"Arriving player's history: {streakPhrase}{topNote}.");
                }
                else
                {
                    string lastVisit = history.LastVisitDaysAgo == 0 ? "earlier today" :
                                       history.LastVisitDaysAgo == 1 ? "yesterday" :
                                       history.LastVisitDaysAgo <= 14 ? $"{history.LastVisitDaysAgo} days ago" :
                                       $"{history.LastVisitDaysAgo / 7} weeks ago";
                    string totalStr = history.TotalMinutes >= 60 ? $"{history.TotalMinutes / 60}h total on this server, " : "";
                    string todayStr = history.MinutesToday > 0 ? $", {history.MinutesToday} min here today"
                        : history.MinutesThisWeek > 0 ? $", {history.MinutesThisWeek} min here this week" : "";
                    sb.AppendLine($"Arriving player's history: {totalStr}last visit: {lastVisit}{todayStr}{topNote}");
                }
                if (groupWeekdayStreak >= 2)
                {
                    sb.AppendLine($"Group note: {groupWeekdayCount} of the players here (including the arriving player) have each attended at least {groupWeekdayStreak} {serverLocalDowName}s in a row on this server.");
                }
                if (crossStreakMembers.Count >= 2)
                {
                    string names = string.Join(", ", crossStreakMembers);
                    sb.AppendLine($"Cross-server pattern: {names} and the arriving player have jammed together on Jamulus every {serverLocalDowName} for multiple weeks (across different servers).");
                }
                else if (crossStreakMembers.Count == 1)
                {
                    sb.AppendLine($"Cross-server pattern: {crossStreakMembers[0]} and the arriving player have jammed together on Jamulus every {serverLocalDowName} for multiple weeks (across different servers).");
                }
            }
            else
            {
                sb.AppendLine("Arriving player has never visited this server before — first time here.");
                if (crossStreakMembers.Count >= 1)
                {
                    string names = string.Join(", ", crossStreakMembers);
                    sb.AppendLine($"Cross-server pattern: though new here, {names} and the arriving player have jammed together on Jamulus every {serverLocalDowName} for multiple weeks.");
                }
            }
        }

        if (!isDefaultName && isHomeServer)
            sb.AppendLine($"This is the player's home server ({homeServer.Total / 60}h here total).");
        else if (!isDefaultName && hasDistinctHomeServer)
        {
            var (homeName, _) = LookupServer(homeServer.ServerKey);
            if (!IsBluesRockServer(homeName))
            {
                string homeLabel = homeName.Length > 0 ? homeName : homeServer.ServerKey;
                sb.AppendLine($"Player's home server: {homeLabel} ({homeServer.Total / 60}h there vs. {history.TotalMinutes} min here).");
            }
        }
        if (isRareInstrument)
            sb.AppendLine($"Instrument note: {arrivingInstrument} is rare on Jamulus — only {instrumentNetworkCount} distinct players with this instrument have ever been seen.");
        if (visitStreak >= 5)
            sb.AppendLine($"Visit streak: {visitStreak} days in a row on Jamulus.");

        // Multi-server hopper
        var hopperCutoff = DateTime.Now.AddHours(-2);
        var hopperCount = EncounterTracker.m_connectionLatestSighting
            .ToList()
            .Where(kv => kv.Key.Length > 32 && kv.Key.StartsWith(arrivingGuid) && kv.Value >= hopperCutoff)
            .Select(kv => kv.Key.Substring(32))
            .Distinct()
            .Count();
        bool isHopper = !isDefaultName && hopperCount >= 2;
        if (isHopper)
            sb.AppendLine($"Active tonight: already visited {hopperCount} servers in the last 2h.");

        // Today's other visitors
        if (history.TodayOtherGuids.Count > 0)
        {
            var names = history.TodayOtherGuids
                .Take(5)
                .Select(g => EncounterTracker.m_guidNamePairs.TryGetValue(g, out var n) ? HttpUtility.HtmlDecode(n).Trim() : null)
                .Where(n => n != null && n.Length > 0 && !IsNoName(n))
                .ToList();
            if (names.Count > 0)
                sb.AppendLine($"Others seen on this server today: {string.Join(", ", names)}" +
                              (history.TodayOtherGuids.Count > 5 ? $" and {history.TodayOtherGuids.Count - 5} more" : ""));
        }

        // Recently departed players
        {
            var allDepartures = RecentDepartureTracker.GetRecentDepartures(serverKeys, nowMinutes, maxAgoMinutes: 120, minSessionMinutes: 15);
            var profiles = GetCensusgeoEntries(allDepartures.Select(d => d.Guid));

            // "Just left" — departed within 15 min
            var justLeft = allDepartures.Where(d => nowMinutes - d.DepartureMinute <= 15).ToList();
            foreach (var dep in justLeft.Take(3))
            {
                if (!profiles.TryGetValue(dep.Guid, out var p)) continue;
                if (IsNoName(p.Name)) continue;
                int minsAgo = nowMinutes - dep.DepartureMinute;
                string agoStr = minsAgo >= 90 ? "over an hour ago" :
                                minsAgo >= 45 ? "about an hour ago" :
                                minsAgo >= 20 ? "about a half hour ago" :
                                minsAgo >= 8  ? "about 10 minutes ago" :
                                                "just now";
                string pairKey = EncounterTracker.CanonicalTwoHashes(arrivingGuid, dep.Guid);
                int minsTogether = 0;
                if (EncounterTracker.m_timeTogether != null &&
                    EncounterTracker.m_timeTogether.TryGetValue(pairKey, out var ts2))
                    minsTogether = (int)ts2.TotalMinutes;
                string histNote = minsTogether >= 30 ? $" — you two have {minsTogether / 60}h+ together" : "";
                string instrCity = (p.Instrument.Length > 0 ? p.Instrument : "") +
                                   (p.City.Length > 0 ? (p.Instrument.Length > 0 ? $", {p.City}" : p.City) : "");
                string profileStr = p.Name + (instrCity.Length > 0 ? $" ({instrCity})" : "");
                sb.AppendLine($"Just left: {profileStr} — left {agoStr}{histNote}.");
            }

            // Larger recent crowd (15 min to 2h ago)
            var recentGroup = allDepartures.Where(d => nowMinutes - d.DepartureMinute > 15).ToList();
            if (recentGroup.Count >= 3)
            {
                int mostRecentAgo = nowMinutes - recentGroup.Max(d => d.DepartureMinute);
                string when = mostRecentAgo < 60 ? "recently" : $"about {mostRecentAgo / 60}h ago";
                var groupNames = new List<string>();
                foreach (var dep in recentGroup.Take(5))
                {
                    if (!profiles.TryGetValue(dep.Guid, out var p2) || IsNoName(p2.Name)) continue;
                    string ic = (p2.Instrument.Length > 0 ? p2.Instrument : "") +
                                (p2.City.Length > 0 ? (p2.Instrument.Length > 0 ? $", {p2.City}" : p2.City) : "");
                    groupNames.Add(p2.Name + (ic.Length > 0 ? $" ({ic})" : ""));
                }
                string extra = recentGroup.Count > 5 ? $" and {recentGroup.Count - 5} more" : "";
                if (groupNames.Count >= 2)
                    sb.AppendLine($"Recent crowd: {recentGroup.Count} players were here {when} — {string.Join(", ", groupNames)}{extra}.");
            }
        }

        // Predictions
        if (predictions.IsPredicted)
        {
            string timing = predictions.MinutesFromNow < -30 ? $"{-predictions.MinutesFromNow} min late" :
                            predictions.MinutesFromNow > 30 ? $"{predictions.MinutesFromNow} min early" :
                            "right on schedule";
            sb.AppendLine($"Prediction: this player was predicted to arrive at this server ({timing})");
        }
        if (predictions.Others.Count > 0)
        {
            var upcoming = predictions.Others
                .Where(o => o.MinutesFromNow > 0)
                .OrderBy(o => o.MinutesFromNow)
                .Take(3)
                .Select(o => $"{o.Name} in ~{o.MinutesFromNow} min");
            var upcomingList = string.Join(", ", upcoming);
            if (upcomingList.Length > 0)
                sb.AppendLine($"Players predicted to arrive soon: {upcomingList}");
        }

        // Co-jammers predicted elsewhere (other servers) — only mention if arriving player knows them
        if (!isDefaultName && predictions.ElsewhereByGuid.Count > 0)
        {
            foreach (var (coGuid, (offset, predServer)) in predictions.ElsewhereByGuid.OrderBy(kv => kv.Value.Offset))
            {
                string pairKey = EncounterTracker.CanonicalTwoHashes(arrivingGuid, coGuid);
                int minsTog = 0;
                if (EncounterTracker.m_timeTogether != null &&
                    EncounterTracker.m_timeTogether.TryGetValue(pairKey, out var tog))
                    minsTog = (int)tog.TotalMinutes;
                if (minsTog < 30) continue;
                var (coName, _, _) = GetCensusgeoEntry(coGuid);
                if (IsNoName(coName)) continue;
                sb.AppendLine($"Co-jammer predicted elsewhere: {coName} expected on {predServer} in ~{offset} min (you two have {minsTog / 60}h+ together).");
            }
        }

        sb.AppendLine();

        bool anyReunion = false;
        bool anyOldFriendInRoom = false;
        bool sameCityInRoom = false;
        bool hasBandNote = false;
        if (others.Count == 0)
        {
            sb.AppendLine("Server is currently empty — arriving musician will be alone.");
            if (usualCrewElsewhere.Count > 0 && !isDefaultName)
                sb.AppendLine("Crew note: their usual co-jammers are active on other servers right now — they're here alone while the crew is elsewhere. Feel free to note this with light humor.");
            if (coJammerForecast.Count > 0 && !isDefaultName)
            {
                sb.AppendLine("Co-jammer forecast (top regulars with strong signals):");
                foreach (var c in coJammerForecast)
                {
                    string minsStr = c.MinsTogether >= 60 ? $"{c.MinsTogether / 60}h together" : $"{c.MinsTogether} min together";
                    string serverDesc = c.LiveServerName?.Length > 0 ? $" on {c.LiveServerName}" : " on another server";
                    string live = c.LiveServer != null ? $"currently live{serverDesc}" : "";
                    string pred = c.PredictedHere != null
                        ? (c.PredictedHere < -30 ? $"predicted here ~{-c.PredictedHere} min ago" :
                           c.PredictedHere <= 0  ? "predicted here right about now" :
                                                   $"predicted to arrive here in ~{c.PredictedHere} min")
                        : "";
                    string recent = c.LastSeenMinsAgo != null ? $"was active ~{c.LastSeenMinsAgo} min ago" : "";
                    string reunion = c.LastTogetherDaysAgo != null
                        ? (c.LastTogetherDaysAgo == 0 ? "last jammed today" :
                           c.LastTogetherDaysAgo == 1 ? "last jammed yesterday" :
                           c.LastTogetherDaysAgo <= 14 ? $"last jammed {c.LastTogetherDaysAgo} days ago" :
                           $"last jammed {c.LastTogetherDaysAgo / 7} weeks ago")
                        : "";
                    string signals = string.Join("; ", new[] { live, pred, recent, reunion }.Where(s => s.Length > 0));
                    bool sameCityF = arrivingCity.Length > 0 && coBios.TryGetValue(c.Guid, out var fbio)
                        && fbio.City.Equals(arrivingCity, StringComparison.OrdinalIgnoreCase);
                    string cityNoteF = sameCityF ? $", also from {arrivingCity}" : "";
                    sb.AppendLine($"  - {c.Name} ({minsStr}{cityNoteF}): {signals}");
                }
            }
        }
        else
        {
            sb.AppendLine($"Other players on server ({others.Count}):");
            var roomMinsHere = new List<int>();
            int newcomerCount = 0;
            foreach (var p in others)
            {
                var pGuid = EncounterTracker.GetHash(p.Name, p.Country, p.Instrument);
                var pairKey = EncounterTracker.CanonicalTwoHashes(arrivingGuid, pGuid);

                int minsTogether = 0;
                int togetherDaysAgo = -1;
                string lastTogether = "";
                if (EncounterTracker.m_timeTogether != null &&
                    EncounterTracker.m_timeTogether.TryGetValue(pairKey, out var ts))
                    minsTogether = (int)ts.TotalMinutes;
                if (EncounterTracker.m_timeTogetherUpdated != null &&
                    EncounterTracker.m_timeTogetherUpdated.TryGetValue(pairKey, out var lastDate))
                {
                    togetherDaysAgo = (int)(DateTime.Now - lastDate).TotalDays;
                    lastTogether = togetherDaysAgo == 0 ? ", last: today" :
                                   togetherDaysAgo == 1 ? ", last: yesterday" :
                                   togetherDaysAgo <= 14 ? $", last: {togetherDaysAgo} days ago" :
                                   $", last: {togetherDaysAgo / 7} weeks ago";
                }

                bool isReunion = minsTogether >= 60 && togetherDaysAgo > 60;
                if (isReunion && !isDefaultName) anyReunion = true;
                if (minsTogether >= 60 && !isDefaultName) anyOldFriendInRoom = true;
                string reunionPrefix = isReunion ? "REUNION — " : "";
                string history2 = minsTogether >= 60 ? $"{reunionPrefix}{minsTogether} min together (old friend){lastTogether}" :
                                  minsTogether >= 10 ? $"{minsTogether} min together{lastTogether}" :
                                  "never shared a server before";

                string pExp = "";
                if (EncounterTracker.m_userConnectDuration.TryGetValue(pGuid, out var pDur))
                    pExp = $", {(int)pDur.TotalMinutes} min lifetime";

                string country = p.Country.Length > 0 ? $", {p.Country}" : "";
                int minsHere = -1;
                if (serverKey.Length > 0)
                {
                    string sightingHash = pGuid + serverKey;
                    if (EncounterTracker.m_connectionFirstSighting.TryGetValue(sightingHash, out var firstSeen))
                        minsHere = (int)(DateTime.Now - firstSeen).TotalMinutes;
                }
                string sessionNote = minsHere >= 1 ? $", here {minsHere} min" : "";
                if (minsHere >= 0) roomMinsHere.Add(minsHere);
                if (CensusIndex.GetGuidLifetime(pGuid) < 60) newcomerCount++;
                string cityNoteP = coBios.TryGetValue(pGuid, out var pbio) && pbio.City.Length > 0
                    ? $", {pbio.City}" : "";
                if (!sameCityInRoom && arrivingCity.Length > 0 && pbio.City.Length > 0
                    && pbio.City.Equals(arrivingCity, StringComparison.OrdinalIgnoreCase))
                    sameCityInRoom = true;
                sb.AppendLine($"  - {DisplayName(p.Name)} ({p.Instrument}{country}{pExp}{sessionNote}{cityNoteP}): {history2}");
            }
            if (roomMinsHere.Count >= 2)
            {
                roomMinsHere.Sort();
                int medianMins = roomMinsHere[roomMinsHere.Count / 2];
                string groupSession = medianMins < 5  ? "everyone just arrived" :
                                      medianMins < 90 ? $"~{medianMins} min in" :
                                                        $"~{medianMins / 60}h in";
                sb.AppendLine($"Jam: {groupSession}");
            }
            if (newcomerCount >= 2)
                sb.AppendLine($"Room newcomers: {newcomerCount} players with under 1h on Jamulus");

            // Instrument complement: note if arriving player fills a missing category
            if (arrivingInstrument.Length > 0 && _instrumentCategory.TryGetValue(arrivingInstrument, out var arrivingCat))
            {
                var roomCats = others
                    .Select(p => _instrumentCategory.TryGetValue(p.Instrument ?? "", out var c) ? c : null)
                    .Where(c => c != null)
                    .ToHashSet();
                if (!roomCats.Contains(arrivingCat))
                {
                    var roomInstruments = others
                        .Select(p => p.Instrument ?? "")
                        .Where(i => i.Length > 0 && i != "Listener" && i != "Streamer" && i != "Unknown" && i != "Conductor")
                        .Distinct()
                        .Take(4)
                        .ToList();
                    if (roomInstruments.Count > 0)
                    {
                        string catLabel = _categoryLabel.TryGetValue(arrivingCat, out var lbl) ? lbl : arrivingCat;
                        sb.AppendLine($"Band note: room has {string.Join(", ", roomInstruments)} — you're the only {catLabel}.");
                        hasBandNote = true;
                    }
                }

                // Almost-full-band: room + arriving player cover 3 of 4 core roles, missing exactly 1
                var allCats = new HashSet<string>(roomCats) { arrivingCat };
                var coreCats = new[] { "strings", "bass", "drums", "keys" };
                var present = coreCats.Where(c => allCats.Contains(c)).ToList();
                var missing = coreCats.Where(c => !allCats.Contains(c)).ToList();
                if (present.Count >= 3 && missing.Count == 1 && others.Count >= 2)
                {
                    string missingLabel = _categoryLabel.TryGetValue(missing[0], out var ml) ? ml : missing[0];
                    sb.AppendLine($"Almost a full band: room has {string.Join(", ", present.Select(c => _categoryLabel.TryGetValue(c, out var l) ? l : c))} — only missing a {missingLabel}.");
                    hasBandNote = true;
                }
            }
        }

        if (sameCityInRoom && arrivingCity.Length > 0)
            sb.AppendLine($"Local connection: {arrivingName} is from {arrivingCity}, same city as someone in the room.");

        // Room geography: IP-based distances for all players (fleet IPs, likely cached)
        string arrivingCountryCode = playerGeoJson?["countryCode"]?.ToString() ?? nationCode;
        string serverCountryCode = serverIpApiJson?["countryCode"]?.ToString() ?? "";
        var memberGeoList = new List<string>();
        if (serverLat.HasValue && others.Count > 0)
        {
            var distTasks = others.Select(async p =>
            {
                string pGuid = EncounterTracker.GetHash(p.Name, p.Country, p.Instrument);
                string memberIp = JamFan22.FleetGuidCache.GetBestNonBlockedIpByGuid(pGuid);
                string country = p.Country?.Trim() ?? "";
                if (string.IsNullOrEmpty(memberIp)) return country.Length > 0 ? $"({country})" : "";
                var geo = await IpAnalyticsService.FetchIpApiAsync(memberIp);
                if (geo == null) return country.Length > 0 ? $"({country})" : "";
                if (double.TryParse(geo["lat"]?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double mLat) &&
                    double.TryParse(geo["lon"]?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double mLon))
                    return $"~{HaversineKm(mLat, mLon, serverLat.Value, serverLon!.Value)}km ({country})";
                return country.Length > 0 ? $"({country})" : "";
            });
            memberGeoList = (await Task.WhenAll(distTasks)).Where(s => s.Length > 0).ToList();
        }
        else
        {
            memberGeoList = others
                .Select(p => p.Country?.Trim() ?? "")
                .Where(c => c.Length > 0)
                .Select(c => $"({c})")
                .ToList();
        }
        {
            string arrivingPart = playerDistKm.HasValue
                ? $"arriving ~{playerDistKm.Value}km ({arrivingCountryCode})"
                : $"arriving ({arrivingCountryCode})";
            string roomPart = memberGeoList.Count > 0 ? $"room: {string.Join(", ", memberGeoList)}" : "";
            string serverPart = serverCountryCode.Length > 0 ? $"server: {serverCountryCode}" : "";
            var gparts = new[] { arrivingPart, roomPart, serverPart }.Where(s => s.Length > 0);
            sb.AppendLine($"Room geography: {string.Join("; ", gparts)}");
        }

        // Song signals — single pass, three buckets: shared / room-only / arriving-history
        var sharedSongs = new List<string>();
        var roomSongs = new List<string>();
        var arrivingHistorySongs = new List<string>();
        string? mostRecentArrivingSong = null;
        int mostRecentArrivingMins = 0;
        string? arrivingDominantArtist = null;
        string? roomDominantArtist = null;
        if (File.Exists("data/url-guids.csv"))
        {
            var roomGuids = others.Count > 0
                ? new HashSet<string>(others.Select(p => EncounterTracker.GetHash(p.Name, p.Country, p.Instrument)))
                : new HashSet<string>();
            int cutoff = nowMinutes - (90 * 24 * 60);
            var sharedCounts = new Dictionary<string, int>();
            var roomCounts = new Dictionary<string, int>();
            var historyCounts = new Dictionary<string, int>();
            foreach (var line in File.ReadLines("data/url-guids.csv"))
            {
                var cols = line.Split(',', 5);
                if (cols.Length < 5) continue;
                if (!int.TryParse(cols[0], out int mins) || mins < cutoff) continue;
                string t = System.Web.HttpUtility.UrlDecode(cols[3]);
                if (string.IsNullOrWhiteSpace(t)) continue;
                var lineGuids = cols[4].Split('|');
                bool hasArriving = lineGuids.Contains(arrivingGuid);
                bool hasRoom = roomGuids.Count > 0 && lineGuids.Any(g => roomGuids.Contains(g));
                if (hasArriving && hasRoom) sharedCounts[t] = sharedCounts.GetValueOrDefault(t, 0) + 1;
                else if (hasRoom) roomCounts[t] = roomCounts.GetValueOrDefault(t, 0) + 1;
                else if (hasArriving)
                {
                    historyCounts[t] = historyCounts.GetValueOrDefault(t, 0) + 1;
                    if (mins > mostRecentArrivingMins) { mostRecentArrivingMins = mins; mostRecentArrivingSong = t; }
                }
            }
            sharedSongs = sharedCounts.OrderByDescending(kv => kv.Value).Take(5).Select(kv => kv.Key).ToList();
            roomSongs = roomCounts.OrderByDescending(kv => kv.Value).Take(5).Select(kv => kv.Key).ToList();
            arrivingHistorySongs = historyCounts.OrderByDescending(kv => kv.Value).Take(5).Select(kv => kv.Key).ToList();

            static string? TopArtist(Dictionary<string, int> counts)
            {
                var artistTotals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in counts)
                {
                    int sep = kv.Key.IndexOf(" — ", StringComparison.Ordinal);
                    if (sep < 0) continue;
                    var artist = kv.Key[(sep + 3)..].Trim();
                    if (!string.IsNullOrEmpty(artist))
                        artistTotals[artist] = artistTotals.GetValueOrDefault(artist, 0) + kv.Value;
                }
                if (artistTotals.Count == 0) return null;
                var top = artistTotals.MaxBy(kv => kv.Value);
                return top.Value >= 2 ? top.Key : null;
            }
            arrivingDominantArtist = TopArtist(historyCounts);
            roomDominantArtist = TopArtist(roomCounts);
        }
        bool hasSharedSongs = sharedSongs.Count > 0;
        bool hasRoomSongs = roomSongs.Count > 0;
        bool hasArrivingHistory = arrivingHistorySongs.Count > 0;
        if (hasSharedSongs)
            sb.AppendLine($"Shared songs with people here: {string.Join(", ", sharedSongs)}");
        if (hasRoomSongs)
            sb.AppendLine($"Songs people here often play: {string.Join(", ", roomSongs)}");
        if (roomDominantArtist != null)
            sb.AppendLine($"Room's dominant artist: {roomDominantArtist}");
        if (hasArrivingHistory)
            sb.AppendLine($"Songs {arrivingName} has played in other sessions: {string.Join(", ", arrivingHistorySongs)}");
        if (arrivingDominantArtist != null)
            sb.AppendLine($"{arrivingName}'s most-played artist (last 90 days): {arrivingDominantArtist}");
        if (mostRecentArrivingSong != null && mostRecentArrivingMins > 0)
        {
            int daysAgo = (nowMinutes - mostRecentArrivingMins) / (24 * 60);
            string when = daysAgo == 0 ? "today" : daysAgo == 1 ? "yesterday" : $"{daysAgo} days ago";
            sb.AppendLine($"Most recently played by {arrivingName}: {mostRecentArrivingSong} ({when})");
        }

        var essayParas = DailyEssayService.GetRelevantEssayContext(
            arrivingName != "(unknown)" ? arrivingName : "",
            serverName,
            others.Select(p => p.Name),
            arrivingCountry, arrivingInstrument);
        bool hasEssayMention = essayParas.Count > 0;
        if (hasEssayMention)
            foreach (var (ep, reason) in essayParas)
                sb.AppendLine($"From today's network essay ({reason}): \"{ep}\"");

        // Determine richness — is there anything worth saying beyond a bare welcome?
        bool hasHistory = history.TotalMinutes > 0;
        bool hasPrediction = predictions.IsPredicted || predictions.Others.Count > 0;
        bool hasOthers = others.Count > 0;
        bool hasTodayVisitors = history.TodayOtherGuids.Count > 0;
        bool hasForecast = coJammerForecast.Count > 0;
        bool hasCrewElsewhere = usualCrewElsewhere.Count > 0;

        bool hasSongContext = hasSharedSongs || hasRoomSongs || hasArrivingHistory
            || arrivingDominantArtist != null || roomDominantArtist != null || mostRecentArrivingSong != null;
        bool hasStreak = visitStreak >= 5;
        if (!hasHistory && !hasPrediction && !hasOthers && !hasTodayVisitors && !hasForecast && !hasCrewElsewhere
            && !isNetworkNewcomer && !isHomeServer && !hasDistinctHomeServer && !isRareInstrument && !isHopper
            && !hasSongContext && !hasStreak && !hasBandNote && !hasEssayMention)
            sb.AppendLine("Nothing notable — use a short one-line welcome only.");

        bool isGroupNoteworthy = anyOldFriendInRoom || anyReunion
            || (hasBandNote && others.Count >= 2)
            || sameCityInRoom
            || (isNetworkNewcomer && others.Count > 0)
            || isVeryExperienced
            || hasSongContext
            || hasEssayMention
            || (playerDistKm.HasValue && playerDistKm.Value >= 5000)
            || (history.IsTopVisitor && others.Count > 0);
        var roomChannelIds = others.Select(p => p.ChannelId).ToList();

        // Compact signals summary for event log (appended externally after message is known)
        var sigs = new System.Text.StringBuilder();
        if (isStudioD) sigs.Append("studioD" + (streamActiveHere ? ":live" : minsUntilLobbyStream.HasValue ? $":in{minsUntilLobbyStream}m" : streamState.IsFree ? ":free" : ":elsewhere"));
        if (hasHistory) sigs.Append(history.WeekdayStreak >= 2
            ? $"|{history.WeekdayStreakDow.ToString().ToLower()}:{history.WeekdayStreak}wks" + (groupWeekdayStreak >= 2 ? $",grp:{groupWeekdayStreak}" : "") + (history.IsTopVisitor ? ",top" : "")
            : $"|history:{history.TotalMinutes / Math.Max(1, 60)}h" + (history.IsTopVisitor ? ",top" : ""));
        if (hasCrewElsewhere) sigs.Append($"|crew:{usualCrewElsewhere.Count}");
        if (hasOthers) sigs.Append($"|room:{others.Count}");
        if (hasForecast) sigs.Append($"|forecast:{coJammerForecast.Count}");
        if (hasPrediction) sigs.Append("|predicted");
        if (isNetworkNewcomer) sigs.Append("|newcomer");
        if (isHomeServer) sigs.Append("|home");
        if (hasDistinctHomeServer) sigs.Append("|home-elsewhere");
        if (isRareInstrument) sigs.Append($"|rare-instrument:{instrumentNetworkCount}");
        if (anyReunion) sigs.Append("|reunion");
        if (isHopper) sigs.Append($"|hopper:{hopperCount}");
        if (hasSharedSongs) sigs.Append($"|songs:{sharedSongs.Count}");
        if (hasRoomSongs) sigs.Append($"|room-songs:{roomSongs.Count}");
        if (roomDominantArtist != null) sigs.Append($"|room-artist:{roomDominantArtist}");
        if (hasArrivingHistory) sigs.Append($"|history-songs:{arrivingHistorySongs.Count}");
        if (arrivingDominantArtist != null) sigs.Append($"|artist:{arrivingDominantArtist}");
        if (mostRecentArrivingSong != null) sigs.Append("|recent-song");
        if (hasEssayMention) sigs.Append($"|essay:{essayParas.Count}");
        if (crossStreakMembers.Count > 0) sigs.Append($"|cross-streak:{crossStreakMembers.Count}");
        if (currentNetworkClients > 0 && typicalNetworkClients > 0)
        {
            double r = (double)currentNetworkClients / typicalNetworkClients;
            if (r >= 1.3) sigs.Append($"|busy:{currentNetworkClients}v{typicalNetworkClients}");
            else if (r <= 0.7) sigs.Append($"|quiet:{currentNetworkClients}v{typicalNetworkClients}");
        }
        if (lobbyAudience > 0) sigs.Append($"|audience:{lobbyAudience}");
        if (hasStreak) sigs.Append($"|streak:{visitStreak}d");
        if (hasBandNote) sigs.Append("|band-note");
        if (sameCityInRoom) sigs.Append("|same-city");
        if (isVeryExperienced) sigs.Append("|veteran");
        if (playerGeoNote != null) sigs.Append($"|faraway:{(playerDistKm.HasValue ? $"{playerDistKm.Value / 100 * 100}km" : "tz")}");
        if (arrivingIsWebUser) sigs.Append("|web-user");
        if (sigs.Length > 0 && sigs[0] == '|') sigs.Remove(0, 1);
        string signalsSummary = sigs.Length > 0 ? sigs.ToString() : "none";

        Console.WriteLine($"[WELCOME-CTX] server={serverName} player={arrivingName} nation={nationCode} rich={(!signalsSummary.Equals("none") ? 1 : 0)} signals={signalsSummary}");

        // Stash signals summary so the caller can include it in the event log
        _lastSignals[arrivingGuid] = signalsSummary;

        return new GatherResult(sb.ToString(), nameColors, roomChannelIds, isGroupNoteworthy, hasWebUser, chosenLanguage);
    }

    // Temporary per-call signal stash (overwritten each call; only used for immediate event logging)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lastSignals = new();

    public static string TakeSignals(string arrivingGuid) =>
        _lastSignals.TryRemove(arrivingGuid, out var s) ? s : "";

    // Returns current players on any server using the polled LastReportedList (no RPC needed).
    private static List<RpcClientEntry> GetPlayersFromLastReportedList(string serverKey)
    {
        foreach (var json in JamulusCacheManager.LastReportedList.Values)
        {
            try
            {
                var servers = JsonSerializer.Deserialize<List<JamulusServers>>(json, _opts);
                if (servers == null) continue;
                foreach (var s in servers)
                {
                    if ($"{s.ip}:{s.port}" != serverKey) continue;
                    if (s.clients == null) return new();
                    return s.clients
                        .Select(c => new RpcClientEntry((int)c.chanid, c.name ?? "", "", c.instrument ?? "", c.country ?? ""))
                        .ToList();
                }
            }
            catch { }
        }
        return new();
    }

    private static async Task<List<RpcClientEntry>> GetClientsAsync(string serverIp, int rpcPort)
    {
        string secret = (await File.ReadAllTextAsync("/secret.txt")).Trim();
        using var tcp = new TcpClient();
        using var cts = new CancellationTokenSource(2000);
        await tcp.ConnectAsync(serverIp, rpcPort, cts.Token);
        var stream = tcp.GetStream();
        var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(stream);
        var writeOpts = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        await writer.WriteLineAsync(JsonSerializer.Serialize(
            new { id = 1, jsonrpc = "2.0", method = "jamulus/apiAuth", @params = new { secret } }, writeOpts).AsMemory(), cts.Token);
        await reader.ReadLineAsync(cts.Token);

        await writer.WriteLineAsync(JsonSerializer.Serialize(
            new { id = 2, jsonrpc = "2.0", method = "jamulusserver/getClients", @params = new { } }, writeOpts).AsMemory(), cts.Token);
        var response = await reader.ReadLineAsync(cts.Token);

        if (response == null) return new();
        var envelope = JsonSerializer.Deserialize<RpcEnvelope<RpcGetClientsResult>>(response, _opts);
        return (envelope?.Result?.Clients ?? new())
            .Select(c => new RpcClientEntry(
                c.ChannelId, c.Name ?? "", c.Address ?? "",
                c.InstrumentCode >= 0 && c.InstrumentCode < _instrumentNames.Length ? _instrumentNames[c.InstrumentCode] : "-",
                c.CountryName ?? ""))
            .ToList();
    }
}
