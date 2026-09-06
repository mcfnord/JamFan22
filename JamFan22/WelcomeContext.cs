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
    string RoomLanguage = "English",
    Dictionary<string, string>? NameEmojis = null,
    List<string>? EventNames = null,
    List<string>? RoomGuids = null,
    string ArrivingName = "",
    string ArrivingInstrument = "",
    int ArrivingDistKm = 0,
    List<int>? LobbyChannelIds = null,
    string ServerCountryCode = "");

public static class WelcomeContext
{
    private static readonly JsonSerializerOptions _opts = new() { PropertyNameCaseInsensitive = true };

    // Web user IP cache — refreshed hourly from telemetry.log
    private static HashSet<string> _webUserIps = new();
    private static DateTime _webUserIpsRefreshedAt = DateTime.MinValue;
    private static readonly SemaphoreSlim _webUserIpsLock = new(1, 1);

    // Tracks last arrival time per server for URL stability gate
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastArrivalAt = new();

    // Rapid-rejoin memory: census ticks accrue ~1/min, so a genuine first-timer who
    // rejoins within minutes-to-hours still shows zero server history. Keyed guid:serverKey.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _recentWelcomes = new();
    private static readonly TimeSpan _rejoinWindow = TimeSpan.FromHours(12);

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
        [property: JsonPropertyName("lon")] double? Lon = null,
        [property: JsonPropertyName("notes")] string? Notes = null);

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
        ["TH"]="Thai",["VN"]="Vietnamese",["ID"]="Indonesian",["MY"]="Malay",["BN"]="Malay",
        ["PH"]="Filipino",["BD"]="Bengali",
        ["US"]="English",["GB"]="English",["AU"]="English",["CA"]="English",
        ["NZ"]="English",["IE"]="English",["ZA"]="English",["IN"]="English",
    };
    private static string LanguageFor(string nationCode) =>
        _countryLanguage.TryGetValue(nationCode, out var lang) ? lang : "English";

    // Dormant-essay language rule: use the language of the joiner's flag choice when we
    // recognize it; otherwise fall back to the language of the country the server lives in.
    public static string EssayLanguage(string joinerNationCode, string serverCountryCode) =>
        _countryLanguage.TryGetValue(joinerNationCode, out var jl) ? jl
        : _countryLanguage.TryGetValue(serverCountryCode, out var sl) ? sl
        : "English";

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

    private static readonly string[] _dowNames =
        { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };

    /// <summary>Guard suffix for lore text naming a weekday other than the server-local today —
    /// stops the LLM implying a recurring day's gathering is happening tonight.</summary>
    private static string WeekdayMismatchGuard(string text, string todayDowName)
    {
        foreach (var d in _dowNames)
            if (!d.Equals(todayDowName, StringComparison.OrdinalIgnoreCase) &&
                text.Contains(d, StringComparison.OrdinalIgnoreCase))
                return $" (recurring {d} identity — today is {todayDowName}, NOT {d}; never imply the {d} gathering is happening tonight)";
        return "";
    }

    // Reads key=value pairs from data/welcome-config.txt; missing keys get defaults.
    private static (int crewMinMins, int forecastMinMins, int forecastSightingHours, int forecastMaxEntries, HashSet<string> crewExcludeGuids)
        ReadConfig()
    {
        int crewMin = 30, forecastMin = 60, sightHours = 2, maxEntries = 4;
        var excludeGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    case "crew_exclude_guid":         if (v.Length > 0) excludeGuids.Add(v); break;
                }
            }
        }
        catch { }
        return (crewMin, forecastMin, sightHours, maxEntries, excludeGuids);
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
        int minMins = 30, HashSet<string>? excludeGuids = null)
    {
        var results = new List<(string Name, string Guid, string ServerKey, string ServerName, int MinsTogether)>();
        foreach (var (guid, mins) in TopCoJammerPairs(arrivingGuid, minMins))
        {
            if (excludeGuids != null && excludeGuids.Contains(guid)) continue;
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
        int forecastMinMins, int forecastSightingHours, int forecastMaxEntries,
        HashSet<string>? excludeGuids = null)
    {
        var sightingCutoff = DateTime.Now.AddHours(-forecastSightingHours);
        var results = new List<CoJammerForecast>();

        foreach (var (guid, mins) in TopCoJammerPairs(arrivingGuid, minMins: forecastMinMins))
        {
            if (excludeGuids != null && excludeGuids.Contains(guid)) continue;
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
    internal static readonly Dictionary<string, string> InstrumentEmoji = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Drums"] = "🥁",           ["Djembe"] = "🥁",          ["Bodhran"] = "🥁",
        ["Bongo"] = "🪘",           ["Congas"] = "🪘",           ["Scratching"] = "🎧",
        ["Electric Guitar"] = "🎸", ["Acoustic Guitar"] = "🪕",  ["Guitar"] = "🎸",
        ["Guitar Vocal"] = "🎸",    ["Ukulele"] = "🪕",          ["Bass Ukulele"] = "🪕",
        ["Mountain Dulcimer"] = "🪕", ["Banjo"] = "🪕",          ["Mandolin"] = "🪕",
        ["Bass Guitar"] = "🎸",     ["Double Bass"] = "🎻",
        ["Keyboard"] = "🎹",        ["Grand Piano"] = "🎹",      ["Synthesizer"] = "🎹",
        ["Accordion"] = "🪗",       ["Keyboard Vocal"] = "🎹",   ["Vibraphone"] = "🎹",
        ["Vocal"] = "🎤",           ["Microphone"] = "🎙️",       ["Vocal Bass"] = "🎤",
        ["Vocal Baritone"] = "🎤",  ["Vocal Lead"] = "🎤",       ["Rapping"] = "🎤",
        ["Vocal Soprano"] = "🎤",   ["Vocal Alto"] = "🎤",       ["Vocal Tenor"] = "🎤",
        ["Harmonica"] = "🎵",       ["Harp"] = "🎵",
        ["Trumpet"] = "🎺",         ["Trombone"] = "🎺",         ["French Horn"] = "🎺",
        ["Tuba"] = "🎺",
        ["Saxophone"] = "🎷",
        ["Clarinet"] = "🎵",        ["Flute"] = "🎵",            ["Bassoon"] = "🎵",
        ["Oboe"] = "🎵",            ["Recorder"] = "🎵",
        ["Violin"] = "🎻",          ["Viola"] = "🎻",             ["Cello"] = "🎻",
        ["Streamer"] = "📻",        ["Listener"] = "👂",
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

    // Pro-tier gate (operator, 2026-08-30): Pro writing only for the most known, most returning
    // guests. "Known" means this server's own lore already names them — whole-word, case-insensitive
    // match against server-lore.json notes/themes/tagline or session-regulars.json regulars for this
    // server key. One source of truth: maintaining the lore (lore-evidence.py, ~90 days) maintains
    // the Pro allowlist. Never substring: "Z" must not match "Zach".
    // Every knob is in welcome-config.txt (hot, no restart):
    //   pro_gate=lore,regulars,list   which sources count (default lore,regulars); `off` disables Pro
    //   pro_names=Alice,Bob           explicit names for the `list` source, any server, exact match
    //   pro_min_fleet_minutes=120     RETURN FLOOR every source must pass (operator 2026-08-30: "No Pro
    //                                 essays for strangers. Only for people who return to our servers
    //                                 again and again"): census minutes on fleet servers, rolling window.
    public static bool IsLoreGuest(string serverKey, string arrivingName, string arrivingGuid = "")
    {
        if (string.IsNullOrWhiteSpace(serverKey) || string.IsNullOrWhiteSpace(arrivingName)) return false;
        var name = arrivingName.Trim();
        if (name.Length < 2) return false;
        try
        {
            var gate = (WelcomeMessageGenerator.ReadConfigValue("pro_gate") ?? "lore,regulars")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(g => g.ToLowerInvariant()).ToHashSet();
            if (gate.Contains("off")) return false;
            int minFleet = int.TryParse(WelcomeMessageGenerator.ReadConfigValue("pro_min_fleet_minutes"), out var mf) ? mf : 120;
            if (minFleet > 0 && FleetMinutes(arrivingGuid) < minFleet) return false;   // fail closed: unknown guid = stranger
            if (gate.Contains("list"))
            {
                var listed = (WelcomeMessageGenerator.ReadConfigValue("pro_names") ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (listed.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) return true;
            }
            var parts = new List<string>();
            if (gate.Contains("lore"))
            {
                var lore = LoadServerLore(serverKey);
                if (lore != null)
                {
                    if (lore.Notes != null) parts.Add(lore.Notes);
                    if (lore.Tagline != null) parts.Add(lore.Tagline);
                    if (lore.Themes != null) parts.AddRange(lore.Themes);
                }
            }
            if (gate.Contains("regulars") && LoadSessionRegulars().TryGetValue(serverKey, out var sess) && sess.Regulars != null)
                parts.AddRange(sess.Regulars);
            if (parts.Count == 0) return false;
            var rx = new System.Text.RegularExpressions.Regex(
                @"(?<![\p{L}\p{N}])" + System.Text.RegularExpressions.Regex.Escape(name) + @"(?![\p{L}\p{N}])",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return parts.Any(p => rx.IsMatch(p));
        }
        catch { return false; }
    }

    // Census minutes this GUID has spent on OUR servers (fleet-server-ips.txt + fleet-dormant-ips.txt),
    // rolling census window. The "returns again and again" measure behind the Pro floor. 0 for an unknown guid.
    public static int FleetMinutes(string guid)
    {
        if (string.IsNullOrWhiteSpace(guid)) return 0;
        try
        {
            var fleetIps = new HashSet<string>();
            foreach (var f in new[] { "data/fleet-server-ips.txt", "data/fleet-dormant-ips.txt" })
            {
                if (!File.Exists(f)) continue;
                foreach (var raw in File.ReadLines(f))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    fleetIps.Add(line.Split(':')[0].Trim());
                }
            }
            CensusIndex.EnsureBuilt();
            int total = 0;
            foreach (var (key, mins, _) in CensusIndex.GetGuidServers(guid))
            {
                var colon = key.LastIndexOf(':');
                var ip = colon > 0 ? key[..colon] : key;
                if (fleetIps.Contains(ip)) total += mins;
            }
            return total;
        }
        catch { return 0; }
    }

    // Song-artist clues (operator, 2026-08-30: "I don't see any song-artist context/clues"). url-guids.csv
    // records every chart URL posted in a room and WHO WAS PRESENT (not who posted). For the arriving
    // GUID: ultimate-guitar slugs become "Artist – Song", chordtabs.in.th ids resolve through
    // data/chart-titles.txt (id|Artist – Song), chords69cl room links become the chart-room name.
    // Ranked by distinct DATES, because a song that comes back across sessions is the real signal.
    private static readonly object _chartsLock = new();
    private static DateTime _chartsMtime = DateTime.MinValue;
    private static Dictionary<string, List<(string date, string label)>> _chartsByGuid = new();
    private static string TitleCase(string slug) =>
        string.Join(" ", slug.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w));
    private static void EnsureChartsLoaded()
    {
        const string path = "data/url-guids.csv";
        if (!File.Exists(path)) return;
        var mtime = File.GetLastWriteTimeUtc(path);
        lock (_chartsLock)
        {
            if (mtime == _chartsMtime) return;
            var titles = new Dictionary<string, string>();
            try
            {
                if (File.Exists("data/chart-titles.txt"))
                    foreach (var raw in File.ReadLines("data/chart-titles.txt"))
                    {
                        var bar = raw.IndexOf('|');
                        if (bar > 0) titles[raw[..bar].Trim()] = raw[(bar + 1)..].Trim();
                    }
            }
            catch { }
            var map = new Dictionary<string, List<(string, string)>>();
            var epoch = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            foreach (var raw in File.ReadLines(path))
            {
                var p = raw.Split(',');
                if (p.Length < 5 || !int.TryParse(p[0], out var min)) continue;
                var url = Uri.UnescapeDataString(p[2]);
                string? label = null;
                var ug = System.Text.RegularExpressions.Regex.Match(url, @"ultimate-guitar\.com/tab/([^/]+)/([^/?#]+)");
                if (ug.Success)
                {
                    var song = System.Text.RegularExpressions.Regex.Replace(ug.Groups[2].Value, @"-(chords|tab|tabs|official|ukulele|bass|drums|power|pro)?-?\d+$", "");
                    song = System.Text.RegularExpressions.Regex.Replace(song, @"-(chords|official|tab|tabs)$", "");
                    label = $"{TitleCase(ug.Groups[1].Value)} – {TitleCase(song)}";
                }
                else
                {
                    var ct = System.Text.RegularExpressions.Regex.Match(url, @"chordtabs\.in\.th/(\d+)");
                    if (ct.Success) label = titles.TryGetValue(ct.Groups[1].Value, out var t) ? t : null;
                    else
                    {
                        var room = System.Text.RegularExpressions.Regex.Match(url, @"chords69cl\.vercel\.app/[^?]*\?room=([^&]+)");
                        if (room.Success) label = $"chart room '{Uri.UnescapeDataString(room.Groups[1].Value)}'";
                    }
                }
                if (label == null) continue;
                var date = epoch.AddMinutes(min).ToString("yyyy-MM-dd");
                foreach (var g in p[4].Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!map.TryGetValue(g, out var list)) map[g] = list = new();
                    list.Add((date, label));
                }
            }
            _chartsByGuid = map; _chartsMtime = mtime;
        }
    }
    public static string? SharedChartsNote(string arrivingGuid, int max = 6)
    {
        try
        {
            EnsureChartsLoaded();
            List<(string date, string label)>? rows;
            lock (_chartsLock) { if (!_chartsByGuid.TryGetValue(arrivingGuid, out rows)) return null; }
            // Operator 2026-08-30: "Must be at least TWO sessions before it's a signal about someone."
            // charts_min_dates= in welcome-config.txt (hot); a song seen on fewer distinct dates is not a clue,
            // and if nothing qualifies the line is omitted entirely.
            int minDates = int.TryParse(WelcomeMessageGenerator.ReadConfigValue("charts_min_dates"), out var md) && md > 0 ? md : 2;
            var ranked = rows.GroupBy(r => r.label)
                .Select(g => (label: g.Key, days: g.Select(r => r.date).Distinct().Count(), last: g.Max(r => r.date)))
                .Where(x => x.days >= minDates)
                .OrderByDescending(x => x.days).ThenByDescending(x => x.last).Take(max).ToList();
            if (ranked.Count == 0) return null;
            var dates = rows.Select(r => r.date).Distinct().OrderBy(d => d).ToList();
            var items = string.Join("; ", ranked.Select(x => $"{x.label} (on {x.days} dates)"));
            return $"Charts that recur while the arriving player is in the room (seen on at least {minDates} different dates each; last {ranked.Max(x => x.last)}): {items}. " +
                   "These are song/artist clues about what this player jams to — weave one or two in naturally when it fits; never list them all, and do not claim they personally posted them.";
        }
        catch { return null; }
    }

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

    private const string DormantIpCachePath = "/root/dormant-ip-cache.json";

    // Returns "instanceId:port" if the given "ip:port" matches a known dormant instance.
    private static string? ResolveInstanceKey(string serverKey)
    {
        var lastColon = serverKey.LastIndexOf(':');
        if (lastColon < 0) return null;
        var ip   = serverKey[..lastColon];
        var port = serverKey[(lastColon + 1)..];
        try
        {
            using var cache = JsonDocument.Parse(File.ReadAllText(DormantIpCachePath));
            foreach (var prop in cache.RootElement.EnumerateObject())
                if (prop.Value.GetString() == ip)
                    return $"{prop.Name}:{port}";
        }
        catch { }
        return null;
    }

    private static LoreEntry? LoadServerLore(string serverKey)
    {
        try
        {
            var json = File.ReadAllText("data/server-lore.json");
            var doc  = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(serverKey, out var el))
                return JsonSerializer.Deserialize<LoreEntry>(el.GetRawText(), _opts);
            var instanceKey = ResolveInstanceKey(serverKey);
            if (instanceKey != null && doc.RootElement.TryGetProperty(instanceKey, out var el2))
                return JsonSerializer.Deserialize<LoreEntry>(el2.GetRawText(), _opts);
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

    private static string FormatSessionRegulars(List<string>? rawRegulars)
    {
        var regulars = rawRegulars?.Where(r => !IsLobbyBot(r)).ToList();
        return regulars == null || regulars.Count == 0 ? "" :
            regulars.Count == 1 ? $" — with {regulars[0]}" :
            regulars.Count == 2 ? $" — with {regulars[0]} and {regulars[1]}" :
            $" — with {string.Join(", ", regulars)}";
    }

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

    private static bool IsRockDirectoryServer(string serverKey)
    {
        try
        {
            if (JamulusCacheManager.LastReportedList.TryGetValue("Genre Rock", out var json))
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

    public static async Task<GatherResult> GatherAsync(string arrivingGuid, string serverKey, int rpcPort, string nationCode, int arrivingChannelId = -1, string? playerIp = null, Func<Task<string?>>? wsGetClients = null)
    {
        var (crewMinMins, forecastMinMins, forecastSightHours, forecastMaxEntries, crewExcludeGuids) = ReadConfig();
        string serverIp = serverKey.Contains(':') ? serverKey.Split(':')[0] : serverKey;
        var streamState = ReadStreamState();
        bool isStudioD = serverKey == "24.199.127.71:22224";
        var serverLore = LoadServerLore(serverKey);
        bool streamActiveHere = streamState.IsActive && streamState.ActiveServer == serverKey;

        // Primary: LastReportedList (instant, works for any server)
        var playersFromList = GetPlayersFromLastReportedList(serverKey);

        // Supplement: WS channel preferred (no --jsonrpcport needed); fall back to raw TCP
        Task<List<RpcClientEntry>>? rpcTask = wsGetClients != null
            ? GetClientsViaWsAsync(wsGetClients)
            : (rpcPort > 0 ? GetClientsAsync(serverIp, rpcPort) : null);

        var (serverName, serverCity) = LookupServer(serverKey);
        var serverKeys = serverKey.Contains(':') ? new List<string> { serverKey } : new List<string>();
        int nowMinutes = JamulusCacheManager.MinutesSince2023AsInt();

        // Band fleet invite — detection + throttle only, no message yet (TODO.md: Band Fleet Invites)
        var bandForGuid = JamFan22.BandIndex.FindBandForGuid(arrivingGuid);
        if (bandForGuid != null && JamFan22.BandInviteTracker.TryMarkEligible(bandForGuid.Value.BandId))
            Console.WriteLine($"[BAND-INVITE-ELIGIBLE] band_id={bandForGuid.Value.BandId} band_name={bandForGuid.Value.BandName ?? "-"} guid={arrivingGuid}");

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
        if ((wsGetClients != null || rpcPort > 0) && arrivingChannelId >= 0)
        {
            var arriver = players.FirstOrDefault(p => p.ChannelId == arrivingChannelId);
            if (arriver == null || string.IsNullOrEmpty(arriver.Name))
            {
                Console.WriteLine($"[WELCOME-CTX] arriver absent/unidentified (channelId={arrivingChannelId}), retrying in 500ms");
                await Task.Delay(500);
                try
                {
                    var retryPlayers = wsGetClients != null
                        ? await GetClientsViaWsAsync(wsGetClients)
                        : await GetClientsAsync(serverIp, rpcPort);
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
        var usualCrewElsewhere = FindUsualCrewElsewhere(arrivingGuid, serverKeys, liveStatus, crewMinMins, crewExcludeGuids);
        var coJammerForecast = GatherCoJammerForecast(arrivingGuid, serverKeys, liveStatus, predictions.ByGuid,
            forecastMinMins, forecastSightHours, forecastMaxEntries, crewExcludeGuids);

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

        // Language vote: arriving player + each room member + server (1 vote each).
        // On a tie, pick randomly among the tied languages — unless the server's own
        // language is one of them, in which case the tie goes to the server.
        var langVotes = new Dictionary<string, int>(StringComparer.Ordinal);
        void AddVote(string lang) { langVotes[lang] = langVotes.GetValueOrDefault(lang) + 1; }
        AddVote(LanguageFor(nationCode));
        foreach (var p in others)
            AddVote(p.Country.Length <= 3 ? LanguageFor(p.Country) : LanguageForCountryName(p.Country));
        var serverCountryName = serverIpApiJson?["countryName"]?.ToString();
        string? serverLanguage = serverCountryName != null ? LanguageForCountryName(serverCountryName) : null;
        if (serverLanguage != null) AddVote(serverLanguage);
        string chosenLanguage = "English";
        if (langVotes.Count > 0)
        {
            int maxVotes = langVotes.Values.Max();
            var tied = langVotes.Where(kv => kv.Value == maxVotes).Select(kv => kv.Key).ToList();
            chosenLanguage = (serverLanguage != null && tied.Contains(serverLanguage))
                ? serverLanguage
                : tied[Random.Shared.Next(tied.Count)];
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

        var nameEmojis = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in others)
        {
            var pn = p.Name?.Trim() ?? "";
            if (pn.Length > 0 && !IsNoName(pn) && InstrumentEmoji.TryGetValue(p.Instrument ?? "", out var em))
                nameEmojis[pn] = em;
        }
        foreach (var c in usualCrewElsewhere)
        {
            var n = c.Name.Trim();
            if (n.Length > 0 && coBios.TryGetValue(c.Guid, out var bio) && InstrumentEmoji.TryGetValue(bio.Instrument, out var em))
                nameEmojis[n] = em;
        }
        foreach (var c in coJammerForecast)
        {
            var n = c.Name.Trim();
            if (n.Length > 0 && !nameEmojis.ContainsKey(n) && coBios.TryGetValue(c.Guid, out var bio) && InstrumentEmoji.TryGetValue(bio.Instrument, out var em))
                nameEmojis[n] = em;
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
        string serverLocalStr = serverTz != null
            ? $" / Server local: {TimeZoneInfo.ConvertTimeFromUtc(utcNowDt, serverTz):dddd HH:mm}"
            : "";
        sb.AppendLine($"Current UTC: {DateTime.UtcNow:dddd HH:mm}{serverLocalStr}");
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
        if (!isDefaultName && !IsNoName(arrivingName) && !nameColors.ContainsKey(arrivingName))
            nameColors[arrivingName] = "";

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

        bool isFirstFleetVisit = !isDefaultName && !CensusIndex.GetGuidServers(arrivingGuid)
            .Any(s => FleetIpAllowlist.Contains(s.ServerKey.Contains(':') ? s.ServerKey.Split(':')[0] : s.ServerKey));

        int instrumentNetworkCount = arrivingInstrument.Length > 0 ? GetNetworkInstrumentCount(arrivingInstrument) : 0;
        bool isRareInstrument = instrumentNetworkCount > 0 && instrumentNetworkCount < 15;




        if (serverName.Length > 0 || serverCity.Length > 0)
            sb.AppendLine($"Server: {serverName}" + (serverCity.Length > 0 ? $" ({serverCity})" : ""));
        if (serverLore != null)
        {
            if (serverLore.Tagline?.Length > 0)
                sb.AppendLine($"Server identity: {serverLore.Tagline}{WeekdayMismatchGuard(serverLore.Tagline, serverLocalDowName)}");
            if (serverLore.Themes?.Count > 0)
            {
                var themesStr = string.Join(", ", serverLore.Themes);
                sb.AppendLine($"Server themes: {themesStr}{WeekdayMismatchGuard(themesStr, serverLocalDowName)}");
            }
            if (serverLore.Notes?.Length > 0)
                sb.AppendLine($"Server note: {serverLore.Notes}{WeekdayMismatchGuard(serverLore.Notes, serverLocalDowName)}");
            if (serverLore.Events?.Count > 0)
            {
                var evNow = DateTime.UtcNow;
                foreach (var ev in serverLore.Events)
                {
                    string timing = "";
                    if (ev.Weekday != null && ev.Hour != null)
                    {
                        var evDow = (DayOfWeek)((ev.Weekday.Value + 1) % 7);
                        var next  = NextSessionOccurrence(evDow, ev.Hour.Value, evNow);
                        double hrs = (next - evNow).TotalHours;
                        if (hrs <= 1)       timing = "happening now — ";
                        else if (hrs <= 6)  timing = $"in {(int)hrs}h — ";
                        else if (hrs <= 20) timing = "tonight — ";
                        else if (ev.Auto == true) continue; // auto events > 20h away: skip
                    }
                    var parts = new List<string>();
                    if (ev.Schedule?.Length > 0)    parts.Add(ev.Schedule);
                    if (ev.Description?.Length > 0) parts.Add(ev.Description);
                    if (ev.ListenUrl?.Length > 0)   parts.Add($"listen: {ev.ListenUrl}");
                    sb.AppendLine($"Event — {ev.Name}: {timing}{string.Join("; ", parts)}");
                }
            }
        }
        if (IsJazzDirectoryServer(serverKey))
            sb.AppendLine("Server genre: Jazz (inferred — players here may not actually be playing jazz, so keep any reference low-key)");
        if (IsRockDirectoryServer(serverKey))
            sb.AppendLine("Server genre: Rock (inferred — players here may not actually be playing rock, so keep any reference low-key)");
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
            // Use "tonight" if within 6h — catches cross-UTC-midnight sessions that are still "tonight" locally
            string sessDay = (next - utcNow).TotalHours < 6 ? "tonight" : next.ToString("dddd");
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
                    if (hours < 0 || hours > 2) continue;
                    string label = ev.Name;
                    var (nearbyLoreName, _) = LookupServer(loreKey);
                    sb.AppendLine($"Nearby session in {(int)hours}h on {nearbyLoreName}: {label} — {ev.Schedule}");
                    Console.WriteLine($"[NEARBY-EVENT] fleet={serverKey} lore={loreKey} event=\"{label}\" hours={hours:F1}");
                }
            }
        }

        // URL stability gate: fire URLs only when crowd has settled (5-min lull since last arrival,
        // at least 1 other already present), then enter a 20-min quiet period.
        string urlGateKey = $"url-gate:{serverKey}";
        bool urlsAllowed = false;
        if (!WelcomeCache.TryGet(urlGateKey, out _))
        {
            if (_lastArrivalAt.TryGetValue(serverKey, out DateTime lastArrival)
                && (utcNow - lastArrival).TotalMinutes >= 5
                && others.Count >= 1)
            {
                urlsAllowed = true;
                WelcomeCache.Set(urlGateKey, "", 20);
            }
        }
        _lastArrivalAt[serverKey] = utcNow;

        // Global tip: Marsha K's Jazz Jam first-hour promotion to any fleet server worldwide
        if (!isStudioD && IsStudioDFirstHour(utcNow))
        {
            string occ = others.Count == 0 ? "empty right now" :
                         others.Count == 1 ? "just 1 other player here" :
                         $"{others.Count} players here";
            if (urlsAllowed)
                sb.AppendLine($"Global hot tip: Marsha K's Jazz Jam just started on Studio D — live right now, runs at least another hour. This server is {occ}. Invite the player to join (it's Studio D on Jamulus) but note that many just listen in at https://ear.jamulus.live — it's pretty far for most. Tip them to catch it while it's hot.");
            else
                sb.AppendLine($"Global hot tip: Marsha K's Jazz Jam just started on Studio D — live right now, runs at least another hour. This server is {occ}. Mention the Jam but do not include any listen URL.");
        }

        int? minsUntilLobbyStream = JamFan22.StreamGate.MinutesUntilNextScheduledStream(serverKey);
        string streamUrl = serverLore?.Events?.FirstOrDefault(e => e.ListenUrl?.Length > 0)?.ListenUrl
                           ?? "https://ear.jamulus.live";
        // When the stream is live/leased here, the link is delivery, not promotion — always
        // include it in this private welcome (every arrival deserves their own copy). linkIncluded
        // lets the group path dedup (ear-link token below) and suppresses the [NO-URLS] block.
        bool linkIncluded = false;
        if (streamActiveHere)
        {
            sb.AppendLine($"This server is streaming live right now at {streamUrl}. Include this line in your message: \"Share/record at {streamUrl}!\"");
            linkIncluded = true;
        }
        else if (lobbyPresent)
        {
            sb.AppendLine($"Lobby client connected — include this line verbatim: \"Share/record at {streamUrl}. Works in most browsers.\"");
            linkIncluded = true;
        }
        else if (minsUntilLobbyStream.HasValue && urlsAllowed)
            sb.AppendLine($"Stream starts in {minsUntilLobbyStream.Value} minutes at {streamUrl} — mention this, not /stream.");
        else if (minsUntilLobbyStream.HasValue)
            sb.AppendLine($"Stream starts in {minsUntilLobbyStream.Value} minutes — do not include the URL (recently mentioned).");
        else if (streamState.IsFree && JamFan22.StreamGate.IsEligibleServer(serverIp) && others.Count >= 2 && urlsAllowed)
        {
            if (playerGeoNote != null)
                sb.AppendLine("Stream slot: free (faraway player) — frame /stream as: if they'd rather just listen in, type /stream and they'll get a listen link. Don't frame it as sharing with friends.");
            else
                sb.AppendLine("Stream slot: free — tell the arriving player they can type /stream to let their non-Jamulus friends listen in.");
        }
        // Tell the group room-announcement path the share link already went out to this server,
        // so it won't repeat it inside the 20-min quiet window. One and done.
        if (linkIncluded)
            WelcomeCache.Set($"ear-link:{serverKey}", "", 20);
        if (lobbyAudience > 0)
            sb.AppendLine($"Listening audience: {lobbyAudience} {(lobbyAudience == 1 ? "person is" : "people are")} tuned in to this server's live stream right now");
        if (!urlsAllowed && !linkIncluded)
            sb.AppendLine("[NO-URLS: crowd still forming or URLs recently sent — omit streaming links and /stream from this message: https://ear.jamulus.live, /stream. The https://jamulus.live orientation link is still allowed.]");
        string arrivingCityNote = arrivingCity.Length > 0 ? $" (city: {arrivingCity})" : "";
        string nameLabel = arrivingName != "(unknown)" ? $": {arrivingName}" : "";
        sb.AppendLine($"Arriving musician{nameLabel}" +
                      (arrivingInstrument.Length > 0 ? $" ({arrivingInstrument})" : "") +
                      (arrivingCountry.Length > 0 ? $" from {arrivingCountry}" : "") +
                      arrivingCityNote + arrivingExp);
        if (playerGeoNote != null)
            sb.AppendLine($"Arriving player's location: {playerGeoNote}.");
        { var charts = SharedChartsNote(arrivingGuid); if (charts != null) sb.AppendLine(charts); }

        sb.AppendLine($"Language to use for message: {LanguageFor(nationCode)}");
        if (arrivingIsWebUser)
            sb.AppendLine("Known web user: yes — this player has explored the network's public tools. Skip the https://jamulus.live link (they already know it). Skip any generic orientation. Speak with specificity about this server and who's here.");
        else if (isFirstFleetVisit)
            sb.AppendLine("First visit to this network: yes — include <a href='https://jamulus.live'>https://jamulus.live</a> regardless of room signals.");
        sb.AppendLine();

        // Server history for arriving player — suppressed for default-named players (unreliable GUID)
        bool rejoinNoted = false;
        if (!isDefaultName)
        {
            string rejoinKey = $"{arrivingGuid}:{serverKey}";
            bool recentlyWelcomed = _recentWelcomes.TryGetValue(rejoinKey, out var lastWelcomedUtc)
                && (DateTime.UtcNow - lastWelcomedUtc) < _rejoinWindow;
            _recentWelcomes[rejoinKey] = DateTime.UtcNow;
            if (_recentWelcomes.Count > 2000)
                foreach (var kv in _recentWelcomes)
                    if ((DateTime.UtcNow - kv.Value) > _rejoinWindow)
                        _recentWelcomes.TryRemove(kv.Key, out _);

            if (history.TotalMinutes > 0)
            {
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
            else if (recentlyWelcomed)
            {
                rejoinNoted = true;
                sb.AppendLine("Arriving player rejoined — they were welcomed here within the past few hours. Do NOT say it's their first time; a brief welcome-back tone is fine.");
                if (crossStreakMembers.Count >= 1)
                {
                    string names = string.Join(", ", crossStreakMembers);
                    sb.AppendLine($"Cross-server pattern: {names} and the arriving player have jammed together on Jamulus every {serverLocalDowName} for multiple weeks.");
                }
            }
            else
            {
                if (ResolveInstanceKey(serverKey) == null)
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
                sb.AppendLine($"Player's home server (Jamulus server name, not a location): \"{homeLabel}\" ({homeServer.Total / 60}h there vs. {history.TotalMinutes} min here).");
            }
        }
        if (isRareInstrument)
            sb.AppendLine($"Instrument note: {arrivingInstrument} is rare on Jamulus — only {instrumentNetworkCount} distinct players with this instrument have ever been seen.");






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
                string instrCity = (p.Instrument.Length > 0 ? p.Instrument : "") +
                                   (p.City.Length > 0 ? (p.Instrument.Length > 0 ? $", {p.City}" : p.City) : "");
                string profileStr = p.Name + (instrCity.Length > 0 ? $" ({instrCity})" : "");
                sb.AppendLine($"Just left: {profileStr} — left {agoStr}.");
                if (!IsNoName(p.Name) && !nameColors.ContainsKey(p.Name)) nameColors[p.Name] = "";
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
                    if (!nameColors.ContainsKey(p2.Name)) nameColors[p2.Name] = "";
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
            var upcomingOthers = predictions.Others
                .Where(o => o.MinutesFromNow > 0)
                .OrderBy(o => o.MinutesFromNow)
                .Take(3)
                .ToList();
            foreach (var o in upcomingOthers)
                if (!IsNoName(o.Name) && !nameColors.ContainsKey(o.Name)) nameColors[o.Name] = "";
            var upcomingList = string.Join(", ", upcomingOthers.Select(o => $"{o.Name} in ~{o.MinutesFromNow} min"));
            if (upcomingList.Length > 0)
                sb.AppendLine($"Players predicted to arrive soon: {upcomingList}");
        }



        sb.AppendLine();

        bool sameCityInRoom = false;
        bool hasBandNote = false;
        if (others.Count == 0)
        {
            sb.AppendLine("Server is currently empty — arriving musician will be alone.");

        }
        else
        {
            sb.AppendLine($"Other players on server ({others.Count}):");
            // How much time the arriving player has logged with each person here (runtime + census).
            var coJamMins = TopCoJammerPairs(arrivingGuid, 1)
                .ToDictionary(x => x.guid, x => x.mins, StringComparer.Ordinal);
            var roomMinsHere = new List<int>();
            foreach (var p in others)
            {
                var pGuid = EncounterTracker.GetHash(p.Name, p.Country, p.Instrument);
                int sharedMins = coJamMins.TryGetValue(pGuid, out var sm) ? sm : 0;

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
                string cityNoteP = coBios.TryGetValue(pGuid, out var pbio) && pbio.City.Length > 0
                    ? $", {pbio.City}" : "";
                if (!sameCityInRoom && arrivingCity.Length > 0 && pbio.City.Length > 0
                    && pbio.City.Equals(arrivingCity, StringComparison.OrdinalIgnoreCase))
                    sameCityInRoom = true;
                string listenerTag = CensusIndex.IsListener(pGuid) ? " [listener]" : "";
                // 10–500h = a real musical bond; 1–10h = have jammed before; 20–60min = crossed paths.
                // >500h is likely a bot pair — don't feature it as a friendship.
                string togetherNote = sharedMins is >= 600 and < 30000 ? ", a familiar musical partner of the arriving player (many hours together)"
                    : sharedMins is >= 60 and < 600 ? ", has jammed with the arriving player before"
                    : sharedMins is >= 20 and < 60 ? ", has crossed paths with the arriving player before" : "";
                sb.AppendLine($"  - {DisplayName(p.Name)} ({p.Instrument}{country}{sessionNote}{cityNoteP}){listenerTag}{togetherNote}");
            }
            // Spine for the joiner-centered essay: who here the arriving player actually knows,
            // strongest bond first. Excludes likely-bot pairs (>500h).
            var sharedHere = others
                .Select(p => (name: DisplayName(p.Name),
                              mins: coJamMins.TryGetValue(EncounterTracker.GetHash(p.Name, p.Country, p.Instrument), out var m) ? m : 0))
                .Where(x => x.mins is >= 20 and < 30000)
                .OrderByDescending(x => x.mins)
                .ToList();
            if (sharedHere.Count > 0)
            {
                var parts = sharedHere.Select(x =>
                    x.mins >= 600 ? $"{x.name} (many hours together — a familiar partner)"
                  : x.mins >= 60  ? $"{x.name} (have jammed together before)"
                  :                 $"{x.name} (have crossed paths before)");
                sb.AppendLine($"People here the arriving player knows, strongest bond first: {string.Join("; ", parts)}");
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



        var essayParas = DailyEssayService.GetRelevantEssayContext(
            arrivingName != "(unknown)" ? arrivingName : "");
        bool hasEssayMention = essayParas.Count > 0;
        if (hasEssayMention)
            foreach (var (ep, reason) in essayParas)
                sb.AppendLine($"From the recent network essay — covers the past 24 hours, events may be from yesterday ({reason}): \"{ep}\"");

        // Band canary: if a known canary is already on this server, hint that their crew may follow
        bool hasBandCanary = false;
        if (others.Count > 0)
        {
            var otherGuids = new HashSet<string>(others.Select(p => EncounterTracker.GetHash(p.Name, p.Country, p.Instrument)));
            var otherNames = new HashSet<string>(others.Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
            var canary = JamFan22.BandIndex.GetBandSoon(otherGuids, otherNames, serverKey);
            if (canary != null && canary.Missing.Count > 0)
            {
                hasBandCanary = true;
                int ratePct = (int)Math.Round(canary.CanaryTriggerRate * 100);
                sb.AppendLine($"Band canary: {canary.CanaryNames} is here. Crew often expected: {string.Join(", ", canary.Missing)}. Assembly rate: {ratePct}%.");
            }
        }

        // Determine richness — is there anything worth saying beyond a bare welcome?
        bool hasPrediction = predictions.IsPredicted || predictions.Others.Count > 0;
        bool hasOthers = others.Count > 0;
        bool hasCrossStreak = crossStreakMembers.Count > 0;
        if (!hasPrediction && !hasOthers && !hasCrossStreak
            && !isHomeServer && !hasDistinctHomeServer && !isRareInstrument
            && !hasBandNote && !hasEssayMention && !hasBandCanary)
            sb.AppendLine("Nothing notable — use a short one-line welcome only.");

        bool isGroupNoteworthy = (hasBandNote && others.Count >= 2)
            || sameCityInRoom
            || hasEssayMention
            || (playerDistKm.HasValue && playerDistKm.Value >= 5000);
        var roomChannelIds = others.Select(p => p.ChannelId).ToList();
        var lobbyChannelIds = players
            .Where(p => !string.IsNullOrEmpty(p.Name) && IsLobbyBot(p.Name))
            .Select(p => p.ChannelId).ToList();

        // Compact signals summary for event log (appended externally after message is known)
        var sigs = new System.Text.StringBuilder();
        if (isStudioD) sigs.Append("studioD" + (streamActiveHere ? ":live" : minsUntilLobbyStream.HasValue ? $":in{minsUntilLobbyStream}m" : streamState.IsFree ? ":free" : ":elsewhere"));
        if (hasOthers) sigs.Append($"|room:{others.Count}");
        if (hasPrediction) sigs.Append("|predicted");
        if (isNetworkNewcomer) sigs.Append("|newcomer");
        if (isHomeServer) sigs.Append("|home");
        if (hasDistinctHomeServer) sigs.Append("|home-elsewhere");
        if (isRareInstrument) sigs.Append($"|rare-instrument:{instrumentNetworkCount}");
        if (hasEssayMention) sigs.Append($"|essay:{essayParas.Count}");
        if (crossStreakMembers.Count > 0) sigs.Append($"|cross-streak:{crossStreakMembers.Count}");
        if (currentNetworkClients > 0 && typicalNetworkClients > 0)
        {
            double r = (double)currentNetworkClients / typicalNetworkClients;
            if (r >= 1.3) sigs.Append($"|busy:{currentNetworkClients}v{typicalNetworkClients}");
            else if (r <= 0.7) sigs.Append($"|quiet:{currentNetworkClients}v{typicalNetworkClients}");
        }
        if (lobbyAudience > 0) sigs.Append($"|audience:{lobbyAudience}");
        if (hasBandNote) sigs.Append("|band-note");
        if (hasBandCanary) sigs.Append("|band-canary");
        if (sameCityInRoom) sigs.Append("|same-city");
        if (isVeryExperienced) sigs.Append("|veteran");
        if (playerGeoNote != null) sigs.Append($"|faraway:{(playerDistKm.HasValue ? $"{playerDistKm.Value / 100 * 100}km" : "tz")}");
        if (arrivingIsWebUser) sigs.Append("|web-user");
        if (isFirstFleetVisit) sigs.Append("|1st-fleet");
        if (rejoinNoted) sigs.Append("|rejoin");
        if (sigs.Length > 0 && sigs[0] == '|') sigs.Remove(0, 1);
        string signalsSummary = sigs.Length > 0 ? sigs.ToString() : "none";

        Console.WriteLine($"[WELCOME-CTX] server={serverName} player={arrivingName} nation={nationCode} rich={(!signalsSummary.Equals("none") ? 1 : 0)} signals={signalsSummary}");

        // Stash signals summary so the caller can include it in the event log
        _lastSignals[arrivingGuid] = signalsSummary;

        var eventNames = serverLore?.Events?.Select(e => e.Name).Where(n => !string.IsNullOrEmpty(n)).ToList() ?? new List<string>();
        var roomGuidList = others.Select(p => EncounterTracker.GetHash(p.Name, p.Country, p.Instrument)).ToList();
        return new GatherResult(sb.ToString(), nameColors, roomChannelIds, isGroupNoteworthy, hasWebUser, chosenLanguage, nameEmojis, eventNames, roomGuidList,
            arrivingName == "(unknown)" ? "" : arrivingName, arrivingInstrument, playerDistKm ?? 0, lobbyChannelIds,
            serverCountryCode);
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

    private static async Task<List<RpcClientEntry>> GetClientsViaWsAsync(Func<Task<string?>> wsGetClients)
    {
        var response = await wsGetClients();
        if (response == null) return new();
        var envelope = JsonSerializer.Deserialize<RpcEnvelope<RpcGetClientsResult>>(response, _opts);
        return (envelope?.Result?.Clients ?? new())
            .Select(c => new RpcClientEntry(
                c.ChannelId, c.Name ?? "", c.Address ?? "",
                c.InstrumentCode >= 0 && c.InstrumentCode < _instrumentNames.Length ? _instrumentNames[c.InstrumentCode] : "-",
                c.CountryName ?? ""))
            .ToList();
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
