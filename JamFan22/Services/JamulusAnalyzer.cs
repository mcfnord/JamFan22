// #define WINDOWS

using JamFan22.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JamFan22.Services
{
    public class JamulusAnalyzer
    {
        private readonly JamulusCacheManager _cacheManager;
        private readonly EncounterTracker    _tracker;
        private readonly IpAnalyticsService  _ipAnalytics;
        private readonly GeolocationService  _geoService;
        private readonly Microsoft.Extensions.Logging.ILogger<JamulusAnalyzer> _logger;

        public JamulusAnalyzer(
            JamulusCacheManager cacheManager,
            EncounterTracker tracker,
            IpAnalyticsService ipAnalytics,
            GeolocationService geoService,
            Microsoft.Extensions.Logging.ILogger<JamulusAnalyzer> logger)
        {
            _cacheManager = cacheManager;
            _tracker      = tracker;
            _ipAnalytics  = ipAnalytics;
            _geoService   = geoService;
            _logger       = logger;
        }

        // ── Shared HTTP client ────────────────────────────────────────────────

        public static readonly HttpClient httpClient = new HttpClient();

        // ── Server/snapshot lists (re-initialized per request) ────────────────

        public static List<ServersForMe> m_allMyServers    = null;
        public static List<ServersForMe> m_safeServerSnapshot = new List<ServersForMe>();

        // ── Lounge / listen-link state (static, shared) ───────────────────────

        public static Dictionary<string, string> m_connectedLounges   = new Dictionary<string, string>();
        private static DateTime _loungesCacheExpiry = DateTime.MinValue;
        private static readonly SemaphoreSlim _loungeLock = new SemaphoreSlim(1, 1);
        public static List<string>               m_listenLinkDeployment = new List<string>();
        public static int                        m_snippetsDeployed    = 0;

        // ── Per-request state fields ──────────────────────────────────────────

        static int m_secondsPause = 12;

        // ── Server tracking ───────────────────────────────────────────────────

        static DateTime firstSample = DateTime.Now;

        public bool NoticeNewbs(string server)
        {
            if (DateTime.Now < firstSample.AddHours(1)) return false;
            if (!JamulusCacheManager.m_serverFirstSeen.ContainsKey(server)) return false;
            return JamulusCacheManager.m_serverFirstSeen[server] >= DateTime.Now.AddHours(-1);
        }

        public string FindActiveJitsiOfJSvr(string serverAddress)
        {
            const string JITSI_LIVE_LIST = "/root/jitsimon/latest-activity-report.txt";
            string theString = "";
            if (File.Exists(JITSI_LIVE_LIST))
            {
                using var file = new StreamReader(JITSI_LIVE_LIST);
                string line;
                while ((line = file.ReadLine()) != null)
                    if (line.Contains(serverAddress)) theString = line;
            }
            if (theString.Length > 0)
                return theString.Substring(theString.LastIndexOf(' ') + 1);
            return "";
        }

        // ── Username / bot filters ────────────────────────────────────────────

        public static bool NukeThisUsername(string name, string instrument, bool CBVB)
        {
            var trimmed = name.Trim();

            if (CBVB && name.ToLower().Contains("feed")) return true;
            if (trimmed.Contains("LowBot")) return true;

            switch (trimmed.ToUpper())
            {
                case "BIT A BIT":    case "JAMONET":      case "JAMONET'":
                case "JAMSUCKER":    case "JAM FEED":     case "STUDIO BRIDGE":
                case "CLICK":        case "LOBBY [0]":    case "LOBBY [1]":
                case "LOBBY [2]":    case "LOBBY [3]":    case "LOBBY [4]":
                case "LOBBY [5]":    case "LOBBY [6]":    case "LOBBY [7]":
                case "LOBBY [8]":    case "LOBBY [9]":    case "LOBBY[0]":
                case "LOBBY":        case "JAMULUS TH":   case "DISCORD.EXE":
                case "REFERENCE":    case "PRIVATE":      case "BOT?":
                    return true;
                case "":
                    return instrument == "Streamer" || instrument == "-";
                case "PLAYER":
                    return instrument == "Conductor";
                default:
                    return false;
            }
        }

        // ── Localisation ──────────────────────────────────────────────────────

        public static string LocalizedText(string nationCode, string english, string chinese, string thai, string german, string italian)
        {
            switch (nationCode)
            {
                case "CN": case "TW": case "HK": return chinese;
                case "TH": return thai;
                case "DE": return german;
                case "IT": return italian;
            }
            return english;
        }

        // ── Duration display ──────────────────────────────────────────────────

public string DurationHere(string server, string who, string nationCode)
{
    Debug.Assert(who.Length == "b707dc8fc6516826fbe9b4aa84d1553a".Length);
    string hash = who + server;
    if (!EncounterTracker.m_connectionFirstSighting.ContainsKey(hash)) return "";

    string show = "";
    while (true)
    {
        TimeSpan ts = DateTime.Now.Subtract(EncounterTracker.m_connectionFirstSighting[hash]);

        if (ts.TotalMinutes > 99) break;

        if (ts.TotalMinutes > 5)
        {
            int tot = (int)ts.TotalMinutes;
            show = "<div style='text-align: right; font-size: 0.8em;'>(" + tot + "m)</div>";
            break;
        }

        if (ts.TotalMinutes > 1)
        {
            string phrase = LocalizedText(nationCode, "just&nbsp;arrived", "剛加入", "เพิ่งมา", "gerade&nbsp;angekommen", "appena&nbsp;arrivato");
            show = "<b><div style='text-align: right; font-size: 0.8em;'>(" + phrase + ")</div></b>";
        }
        else
        {
            show = "<div style='text-align: right; font-size: 0.8em;'>(" + (int)ts.TotalMinutes + "m)</div>";
        }
        break;
    }

    // NEW: Return empty string if there's nothing to show
    if (string.IsNullOrEmpty(show)) return "";

    return " <font size='-1'><i>" + show + "</i></font>";
}


        // ── Listen-link helpers ───────────────────────────────────────────────

        static bool HasListenLink(string ipport)
        {
            foreach (var ipp in m_listenLinkDeployment)
                if (ipp == ipport) return true;
            return false;
        }

        public async Task LoadConnectedLoungesAsync()
        {
            if (DateTime.UtcNow < _loungesCacheExpiry && m_connectedLounges.Count > 0)
                return;

            await _loungeLock.WaitAsync();
            try
            {
                if (DateTime.UtcNow < _loungesCacheExpiry && m_connectedLounges.Count > 0)
                    return;

                var freshLounges = new Dictionary<string, string>();

                // mjth.live format: { "IP:PORT": "https://..." }
                try
                {
                    using var client = new HttpClient();
                    var response = await client.GetAsync("https://mjth.live/lounges.json");
                    response.EnsureSuccessStatusCode();
                    string body = await response.Content.ReadAsStringAsync();
                    var data = JsonSerializer.Deserialize<Dictionary<string, string>>(body);
                    if (data != null)
                    {
                        foreach (var kvp in data)
                            freshLounges[kvp.Value] = kvp.Key; // swap: URL → IP:PORT
                    }
                    Console.WriteLine($"[LoadConnectedLoungesAsync] Loaded {data?.Count ?? 0} entries from mjth.live/lounges.json");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to load mjth.live/lounges.json: {ex.Message}");
                }

                // Hard-coded fallbacks (only added if the remote source didn't already supply them)
                freshLounges.TryAdd("https://lobby.jam.voixtel.net.br/", "179.228.137.154:22124");
                freshLounges.TryAdd("http://1.onj.me:32123/",            "139.162.251.38:22124");
                freshLounges.TryAdd("http://3.onj.me:8000/jamulus4",     "69.164.213.250:22124");
                freshLounges.TryAdd("https://StudioD.live",               "24.199.127.71:22224");

                m_connectedLounges = freshLounges;
                _loungesCacheExpiry = DateTime.UtcNow.AddHours(24);
                Console.WriteLine($"[LoadConnectedLoungesAsync] Cached {m_connectedLounges.Count} total lounge mappings for 24 hours.");
            }
            finally
            {
                _loungeLock.Release();
            }
        }

        public async Task<string> GetListenHtmlAsync(ServersForMe s)
        {
            string ipport = s.serverIpAddress + ":" + s.serverPort;
            foreach (var url in m_connectedLounges.Keys)
            {
                if (!m_connectedLounges[url].Contains(ipport)) continue;
                foreach (var user in s.whoObjectFromSourceData)
                {
                    if (user.name.Contains("obby") || user.name == "")
                    {
                        string num = "";
                        var iPos = user.name.IndexOf("[");
                        if (iPos > 0 && '0' != user.name[iPos + 1])
                            num = "<sub> " + user.name[iPos + 1] + "</sub>";
                        m_listenLinkDeployment.Add(ipport);
                        return $"<b><a class='listenlink listenalready' target='_blank' href='{url}'>Listen</a></b>{num}</br>";
                    }
                }
            }
            return "";
        }

        // ── Server list processing pipeline ──────────────────────────────────

        public async Task ProcessServerListsAsync(PreloadedData data)
        {
            Console.WriteLine($"[DEBUG] ProcessServerListsAsync started. LastReportedList keys: {string.Join(", ", JamulusCacheManager.LastReportedList.Keys)}");
            int totalProcessed = 0, totalAdded = 0;

            foreach (var key in JamulusCacheManager.LastReportedList.Keys)
            {
                var serversOnList = JsonSerializer.Deserialize<List<JamulusServers>>(JamulusCacheManager.LastReportedList[key]);
                Console.WriteLine($"[DEBUG] Processing {key}: {serversOnList.Count} servers.");

                foreach (var server in serversOnList)
                {
                    totalProcessed++;
                    int people = server.clients?.GetLength(0) ?? 0;

                    if (ShouldSkipServer(server, people)) continue;

                    var clientResult = ProcessServerClients(server, data);
                    if (clientResult.UserCountries.Count == 0) continue;

                    var (place, usersPlace, serverCountry) = GetServerAndUserLocation(server, clientResult.UserCountries);
                    var (dist, zone) = await CalculateServerDistanceAndZoneAsync(place, usersPlace, server.ip);

                    string[] latAmOverrides = { "Mexico", "Guatemala", "Belize", "Honduras", "El Salvador", "Nicaragua", "Costa Rica", "Panama" };
                    if (latAmOverrides.Contains(serverCountry, StringComparer.OrdinalIgnoreCase))
                        zone = 'S';

                    int trueDist = dist;

                    dist = CalculateBoostedDistance(server, dist, clientResult.FirstUserHash);
                    if (dist < 250) dist = 250;

                    m_allMyServers.Add(new ServersForMe(
                        key, server.ip, server.port, server.name, server.city, serverCountry,
                        dist, trueDist, zone, clientResult.WhoHtml, server.clients, people, (int)server.maxclients
                    ));
                    totalAdded++;
                }
            }
            Console.WriteLine($"[DEBUG] ProcessServerListsAsync finished. Processed: {totalProcessed}, Added: {totalAdded}");
        }

        public bool ShouldSkipServer(JamulusServers server, int people)
        {
            if (server.name.ToLower().Contains("script") || server.city.ToLower().Contains("script") ||
                server.name.ToLower().Contains("jxw")    || server.city.ToLower().Contains("peterborough") ||
                server.name.ToLower().Contains("peachjam3"))
                return true;
            return people < 1;
        }

        public (string WhoHtml, List<string> UserCountries, string FirstUserHash) ProcessServerClients(JamulusServers server, PreloadedData data)
        {
            string who = "";
            var userCountries = new List<string>();
            string firstUserHash = null;

            var sortedClients = server.clients
                .OrderByDescending(guy =>
                {
                    string h = EncounterTracker.GetHash(guy.name, guy.country, guy.instrument);
                    double d = _tracker.DurationHereInMins(server.ip + ":" + server.port, h);
                    return d < 0 ? 0 : d;
                })
                .ThenBy(guy => guy.name)
                .ToList();

            foreach (var guy in sortedClients)
            {
                if (guy.name.ToLower().Contains("script")) continue;

                string musicianHash = EncounterTracker.GetHash(guy.name, guy.country, guy.instrument);

                if (IsClientASNBlocked(musicianHash, data.JoinEventsLines, data.BlockedASNs)) continue;

                _tracker.NotateWhoHere(server.ip + ":" + server.port, musicianHash);

                if (NukeThisUsername(guy.name, guy.instrument, server.name.ToUpper().Contains("CBVB"))) continue;

                if (firstUserHash == null) firstUserHash = musicianHash;

                userCountries.Add(guy.country.ToUpper());
            }

            return (who, userCountries, firstUserHash);
        }

        public (string Place, string UsersPlace, string ServerCountry) GetServerAndUserLocation(JamulusServers server, List<string> userCountries)
        {
            string place = "", serverCountry = "", usersPlace = "Moon";

            if (server.city.Length > 1)    place = server.city;
            if (server.country.Length > 1)
            {
                if (place.Length > 1) place += ", ";
                place += server.country;
                serverCountry = server.country;
            }

            if (userCountries.Count > 0)
            {
                var mostCommons = userCountries.GroupBy(x => x).OrderByDescending(g => g.Count()).Select(x => x.Key).ToArray();
                string usersCountry = mostCommons[0];

                var cities = new List<string>();
                foreach (var guy in server.clients)
                    if (guy.country.ToUpper() == usersCountry && guy.city.Length > 0)
                        cities.Add(guy.city.ToUpper());

                string usersCity = cities.Count > 0
                    ? cities.GroupBy(x => x).OrderByDescending(g => g.Count()).Select(x => x.Key).FirstOrDefault()
                    : "";

                usersPlace = usersCity.Length > 1 ? usersCity + ", " + usersCountry : usersCountry;
            }

            if (place.Contains("208, ")) place = place.Replace("208, ", "");

            return (place, usersPlace, serverCountry);
        }

        public async Task<(int dist, char zone)> CalculateServerDistanceAndZoneAsync(string place, string usersPlace, string serverIp)
        {
            LatLong location = await _geoService.PlaceToLatLonAsync(place.ToUpper(), usersPlace.ToUpper(), serverIp);
            int dist = 0; char zone = ' ';

            if (location != null && (location.lat.Length > 1 || location.lon.Length > 1))
            {
                dist = await _geoService.DistanceFromClientAsync(location.lat, location.lon);
                zone = _geoService.ContinentOfLatLong(location.lat, location.lon);
            }
            return (dist, zone);
        }

        public int CalculateBoostedDistance(JamulusServers server, int initialDistance, string firstUserHash)
        {
            if (server.clients.Length != 1 || firstUserHash == null) return initialDistance;

            double boost = _tracker.DurationHereInMins(server.ip + ":" + server.port, firstUserHash);
            if (boost < 3.0) boost = 3.0;
            return (int)((double)initialDistance * (boost / 6));
        }

        private bool IsClientASNBlocked(string musicianHash, string[] joinEvents, HashSet<string> blockedASNs)
        {
            const int asnColumnIndex = 12;
            for (int i = joinEvents.Length - 1; i >= 0; i--)
            {
                string candidateLine = joinEvents[i];
                if (!candidateLine.Contains(musicianHash) || string.IsNullOrWhiteSpace(candidateLine)) continue;

                string[] fields = candidateLine.Split(',');
                if (fields.Length > asnColumnIndex)
                {
                    string fullAsnField = fields[asnColumnIndex].Trim();
                    if (fullAsnField.StartsWith("AS"))
                    {
                        string asnIdentifier = fullAsnField.Split(' ')[0];
                        if (asnIdentifier.Length > 0 && blockedASNs.Contains(asnIdentifier))
                            return true;
                    }
                }
            }
            return false;
        }

        // ── Request state helpers ─────────────────────────────────────────────

        public void InitializeGutsRequest()
        {
            m_allMyServers = new List<ServersForMe>();
            m_listenLinkDeployment.Clear();
            m_snippetsDeployed = 0;
        }

        public class Prediction
        {
            public DateTime ArrivalTime { get; set; }
            public string Guid { get; set; }
            public string Name { get; set; }
            public string Server { get; set; }
        }

        private static List<Prediction> _cachedPredictions = new List<Prediction>();
        private static DateTime _predictionsCacheTime = DateTime.MinValue;
        private static readonly SemaphoreSlim _predLock = new SemaphoreSlim(1, 1);

        public async Task<List<Prediction>> GetActivePredictionsAsync()
        {
            if (DateTime.UtcNow < _predictionsCacheTime.AddSeconds(30)) return _cachedPredictions;

            await _predLock.WaitAsync();
            try
            {
                if (DateTime.UtcNow < _predictionsCacheTime.AddSeconds(30)) return _cachedPredictions;

                var preds = new List<Prediction>();
                if (File.Exists("predicted.csv"))
                {
                    var epoch = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    var lines = await File.ReadAllLinesAsync("predicted.csv");
                    foreach (var line in lines)
                    {
                        var fields = line.Split(',');
                        // Field 0: Mins, Field 1: Guid, Field 2: Name, Field 3: ServerName
                        if (fields.Length > 3 && long.TryParse(fields[0].Trim(), out long mins))
                        {
                            preds.Add(new Prediction
                            {
                                ArrivalTime = epoch.AddMinutes(mins),
                                Guid = fields[1].Trim(),
                                Name = System.Net.WebUtility.UrlDecode(fields[2].Trim()),
                                Server = System.Net.WebUtility.UrlDecode(fields[3].Trim())
                            });
                        }
                    }
                }
                _cachedPredictions = preds;
                _predictionsCacheTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetActivePredictionsAsync] Error reading predicted.csv: {ex.Message}");
            }
            finally { _predLock.Release(); }

            return _cachedPredictions;
        }

        // ── Preloaded data cache ──────────────────────────────────────────────

        public record PreloadedData(
            HashSet<string> BlockedASNs,
            string[]        JoinEventsLines,
            HashSet<string> ErasedServerNames,
            HashSet<string> NoPingIpPartial,
            HashSet<string> BlockedServerARNs,
            HashSet<string> GoodGuids
        );

        private static PreloadedData m_cachedPreloadedData;
        private static DateTime      m_lastDataLoadTime = DateTime.MinValue;
        private const  double        CacheDurationSeconds = 300.0;
        private static readonly SemaphoreSlim _dataLoadLock = new SemaphoreSlim(1, 1);

        public async Task<PreloadedData> GetCachedPreloadedDataAsync()
        {
            if (DateTime.Now <= m_lastDataLoadTime.AddSeconds(CacheDurationSeconds))
                return m_cachedPreloadedData;

            await _dataLoadLock.WaitAsync();
            try
            {
                if (DateTime.Now > m_lastDataLoadTime.AddSeconds(CacheDurationSeconds))
                {
                    Console.WriteLine($"Cache expired ({CacheDurationSeconds}s). Reloading all data files...");
                    m_cachedPreloadedData = await LoadPreloadedDataAsync();
                    m_lastDataLoadTime = DateTime.Now;
                    Console.WriteLine("Data file reload complete.");

                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    GC.WaitForPendingFinalizers();
                    Console.WriteLine("Garbage Collection complete.");
                }
            }
            finally { _dataLoadLock.Release(); }

            return m_cachedPreloadedData;
        }

        public async Task<PreloadedData> LoadPreloadedDataAsync()
        {
            var blockedASNs = new HashSet<string>(
                (await File.ReadAllLinesAsync("wwwroot/asn-blocks.txt"))
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .Select(line => line.Trim().Split(' ')[0])
            );

            var joinEventsLines = File.Exists("join-events.csv")
                ? await File.ReadAllLinesAsync("join-events.csv")
                : new string[0];

            var erasedServerNames = new HashSet<string>(
                File.Exists("erased.txt")
                    ? (await File.ReadAllLinesAsync("erased.txt")).Select(l => l.Trim().ToLower())
                    : new string[0]
            );

            var noPingIpPartial = new HashSet<string>(
                File.Exists("no-ping.txt")
                    ? (await File.ReadAllLinesAsync("no-ping.txt")).Where(l => l.Trim().Length > 0)
                    : new string[0]
            );

            var blockedServerARNs = new HashSet<string>(
                File.Exists("arn-servers-blocked.txt")
                    ? await File.ReadAllLinesAsync("arn-servers-blocked.txt")
                    : new string[0]
            );

            var goodGuids = await GetGoodGuidsSetAsync();

            return new PreloadedData(blockedASNs, joinEventsLines, erasedServerNames, noPingIpPartial, blockedServerARNs, goodGuids);
        }

        // ── Good-GUIDs cache ──────────────────────────────────────────────────

        private static HashSet<string>    _goodGuidsCache = null;
        private static DateTime           _cacheExpiry    = DateTime.MinValue;
        private static readonly TimeSpan  _cacheDuration  = TimeSpan.FromMinutes(1);
        private static readonly SemaphoreSlim _cacheLock  = new SemaphoreSlim(1, 1);

        private static async Task<HashSet<string>> LoadGoodGuidsFromFileAsync()
        {
            var goodGuids = new HashSet<string>();
            try
            {
                string[] lines = await File.ReadAllLinesAsync("join-events.csv");
                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    string[] parts = line.Split(',').Select(p => p.Trim()).ToArray();
                    if (parts.Length > 12)
                    {
                        string guid = parts[2];
                        string flag = parts[12];
                        if (flag != "0" && !string.IsNullOrEmpty(guid))
                            goodGuids.Add(guid);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading join-events.csv: {ex.Message}");
            }
            return goodGuids;
        }

        private static async Task<HashSet<string>> GetGoodGuidsSetAsync()
        {
            if (_goodGuidsCache != null && DateTime.UtcNow < _cacheExpiry)
                return _goodGuidsCache;

            await _cacheLock.WaitAsync();
            try
            {
                if (_goodGuidsCache != null && DateTime.UtcNow < _cacheExpiry) return _goodGuidsCache;
                _goodGuidsCache = await LoadGoodGuidsFromFileAsync();
                _cacheExpiry = DateTime.UtcNow.Add(_cacheDuration);
                return _goodGuidsCache;
            }
            finally { _cacheLock.Release(); }
        }

        // ── Visitor statistics ────────────────────────────────────────────────

        public static Dictionary<string, DateTime>      m_clientIPLastVisit    = new Dictionary<string, DateTime>();
        public static Dictionary<string, DateTime>      m_clientIPsDeemedLegit = new Dictionary<string, DateTime>();
        public static Dictionary<string, DateTime>      m_countriesDeemedLegit = new Dictionary<string, DateTime>();
        public static Dictionary<string, int>           m_countryRefreshCounts = new Dictionary<string, int>();
        public static Dictionary<string, HashSet<string>> m_bucketUniqueIPsByCountry = new Dictionary<string, HashSet<string>>();

        public class MyUserGeoCandy
        {
            public string city;
            public string countryCode2;
        }

        static Dictionary<string, MyUserGeoCandy> userIpCachedItems = new Dictionary<string, MyUserGeoCandy>();
        static int m_conditionsDelta = 0;
        static string GEOAPIFY_MYSTERY_STRING = null;

        public void UpdateUserStatistics(string ipAddress, MyUserGeoCandy geoData)
        {
            Console.Write($"{geoData.city}, {geoData.countryCode2}");

            if (m_clientIPLastVisit.TryGetValue(ipAddress, out var lastRefresh))
            {
                var secondsSince = (DateTime.Now - lastRefresh).TotalSeconds;
                var lowerBound   = 120 + m_conditionsDelta - 30;
                var upperBound   = 120 + m_conditionsDelta + 30;

                if (secondsSince > lowerBound && secondsSince < upperBound)
                {
                    Console.Write(" :)");
                    m_clientIPsDeemedLegit[ipAddress] = DateTime.Now;
                    m_countriesDeemedLegit[geoData.countryCode2] = DateTime.Now;

                    m_countryRefreshCounts.TryGetValue(geoData.countryCode2, out int count);
                    m_countryRefreshCounts[geoData.countryCode2] = count + 1;

                    if (!m_bucketUniqueIPsByCountry.TryGetValue(geoData.countryCode2, out var ipSet))
                    {
                        ipSet = new HashSet<string>();
                        m_bucketUniqueIPsByCountry[geoData.countryCode2] = ipSet;
                    }
                    ipSet.Add(ipAddress);
                }
            }
            m_clientIPLastVisit[ipAddress] = DateTime.Now;
            Console.WriteLine($"  ({m_clientIPsDeemedLegit.Count} confirmed users)");
        }

        public void AdjustPerformanceDelta(TimeSpan duration)
        {
            if (duration.TotalSeconds > 3)      { Console.WriteLine($"Slow response: {duration.TotalSeconds:F2}s. Increasing delta."); m_conditionsDelta++; }
            else if (duration.TotalSeconds < 1 && m_conditionsDelta > 0) m_conditionsDelta--;
        }

        public async Task<MyUserGeoCandy> GetOrAddUserGeoDataAsync(string ipAddress)
        {
            if (userIpCachedItems.TryGetValue(ipAddress, out var cachedCandy))
                return cachedCandy;

            try
            {
                GEOAPIFY_MYSTERY_STRING ??= await File.ReadAllTextAsync("secretGeoApifykey.txt");

                string ipv4Address = ipAddress.Replace("::ffff:", "");
                string endpoint    = $"https://api.geoapify.com/v1/ipinfo?ip={ipv4Address}&apiKey={GEOAPIFY_MYSTERY_STRING}";
                string jsonResponse = await httpClient.GetStringAsync(endpoint);
                JObject jsonGeo    = JObject.Parse(jsonResponse);

                var newCandy = new MyUserGeoCandy
                {
                    city          = (string)jsonGeo["city"]?["name"],
                    countryCode2  = (string)jsonGeo["country"]?["iso_code"]
                };

                userIpCachedItems[ipAddress] = newCandy;
                Console.WriteLine($"Cached new location: {newCandy.city}, {newCandy.countryCode2}");
                return newCandy;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching geolocation for {ipAddress}: {ex.Message}");
                return null;
            }
        }

        // ── IP→GUID resolution ────────────────────────────────────────────────

        private static readonly ConcurrentDictionary<string, string>   ipToGuidCache = new();
        private static readonly ConcurrentDictionary<string, DateTime> ipCacheTime   = new();
        private static readonly TimeSpan                               cacheDuration  = TimeSpan.FromMinutes(5);

        private static readonly SemaphoreSlim _censusLock = new SemaphoreSlim(1, 1);
        private static Dictionary<string, (string City, string Nation, string Instrument)> _censusCache = null;
        private static DateTime _censusCacheTime = DateTime.MinValue;

        public async Task<string> GetIPDerivedHashAsync(string ipAddress)
        {
#if WINDOWS
            ipAddress = "97.186.6.197";
#endif
            var candidatesTask  = GuidFromIpAsync(ipAddress);
            var geoTask         = GetOrAddUserGeoDataAsync(ipAddress);
            var asnTask         = IpAnalyticsService.AsnOfThisIpAsync(ipAddress);
            var blockListTask   = GetCachedPreloadedDataAsync();

            await Task.WhenAll(candidatesTask, geoTask, asnTask, blockListTask);

            var candidates = candidatesTask.Result;
            var geo        = geoTask.Result;
            string rawAsn  = asnTask.Result ?? "";
            var blockData  = blockListTask.Result;

            string shortAsn  = rawAsn.Split(' ')[0];
            bool   isBlocked = blockData.BlockedASNs.Contains(shortAsn);

            if (candidates.Count > 0 || isBlocked)
            {
                var sortedCandidates = candidates.OrderByDescending(c => c.IsOnline)
                                                 .ThenByDescending(c => c.Signal)
                                                 .ThenByDescending(c => c.Timestamp)
                                                 .ToList();

                var bestMatch        = sortedCandidates.FirstOrDefault();
                string inferredIdentity = "Unknown";
                string matchReason      = "Insufficient Data";
                bool veto = false, overrideTriggered = false;

                if (bestMatch != default && bestMatch.IsOnline && bestMatch.Signal < 2)
                {
                    var heavyHitter = candidates.Where(c => !c.IsOnline && c.Signal >= 28)
                                               .OrderByDescending(c => c.Timestamp).FirstOrDefault();
                    if (heavyHitter != default)
                    {
                        bestMatch = heavyHitter;
                        matchReason = $"OVERRIDE: Weak Online (Sig 0) replaced by Iron-Clad Regular (Sig {heavyHitter.Signal})";
                        inferredIdentity = !string.IsNullOrWhiteSpace(heavyHitter.Name) ? heavyHitter.Name : "No Name";
                        overrideTriggered = true;
                    }
                }

                if (!overrideTriggered)
                {
                    if (bestMatch != default && bestMatch.IsOnline && bestMatch.Signal == 0 &&
                        candidates.Any(c => !c.IsOnline && c.Signal >= 28))
                    { matchReason = "VETO: Online (Sig 0) vs Heavy Offline (Sig 28+)."; inferredIdentity = "None (Vetoed)"; veto = true; }
                    else if (bestMatch != default && bestMatch.Signal < 3 && candidates.Any(c => c.Signal >= 3))
                    { matchReason = "VETO: Weak match vs Strong History."; inferredIdentity = "None (Vetoed)"; veto = true; }
                    else if (candidates.Count > 4 && bestMatch != default && bestMatch.Signal == 0)
                    { matchReason = "VETO: Crowded IP."; inferredIdentity = "None (Ambiguous)"; veto = true; }
                }

                if (!veto && !overrideTriggered && bestMatch != default)
                {
                    inferredIdentity = !string.IsNullOrWhiteSpace(bestMatch.Name) ? bestMatch.Name : "No Name";
                    matchReason = bestMatch.IsOnline ? "Active (ONLINE)" : "Most Recent (Offline)";
                }

                string primaryLoc = geo != null ? $"{geo.city}, {geo.countryCode2}" : "Unknown";
                Console.WriteLine("________________________________________________________________________________");
                Console.WriteLine($"Client IP:       {ipAddress}");
                Console.WriteLine($"   GeoLoc:       {primaryLoc}");
                Console.WriteLine($"   Network:       {rawAsn}");
                if (isBlocked)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"   WARNING:       MATCHES BLOCKED NETWORK ({shortAsn})");
                    Console.ResetColor();
                }
                Console.WriteLine($"\n   => MATCH:      {inferredIdentity}");
                Console.WriteLine($"   => REASON:     {matchReason}\n");

                if (candidates.Count > 0)
                {
                    Console.WriteLine($"{"TIME",-8} {"SIG",-4} {"STATUS",-8} {"GUID",-6} {"INSTRUMENT",-16} IDENTITY");
                    foreach (var c in sortedCandidates)
                    {
                        string guidFragment = c.Guid.Length > 5 ? c.Guid.Substring(c.Guid.Length - 5) : c.Guid;
                        string locRaw = string.IsNullOrEmpty(c.City) && string.IsNullOrEmpty(c.Nation) ? "-" : $"{c.City}, {c.Nation}";
                        string inst   = string.IsNullOrEmpty(c.Instrument) ? "-" : c.Instrument;
                        if (inst.Length > 15) inst = inst.Substring(0, 13) + "..";

                        Console.Write($" {c.Timestamp,-8} {c.Signal,-4} ");
                        if (c.IsOnline) { Console.ForegroundColor = ConsoleColor.Green; Console.Write($"{"ONLINE",-8} "); Console.ResetColor(); }
                        else
                        {
                            Console.ForegroundColor = c.Signal >= 3 ? ConsoleColor.Yellow : ConsoleColor.Gray;
                            Console.Write($"{"OFFLINE",-8} ");
                            Console.ResetColor();
                        }
                        Console.WriteLine($"{guidFragment,-6} {inst,-16} {c.Name} [{locRaw}]");
                    }
                    Console.WriteLine("");
                }
                else { Console.WriteLine("   (No historical GUID matches found for this IP)"); }
            }

            var onlineCandidate = candidates.FirstOrDefault(c => c.IsOnline);

            if (onlineCandidate != default && onlineCandidate.Signal < 2)
            {
                var heavyHitters = candidates.Where(c => !c.IsOnline && c.Signal >= 28)
                                             .OrderByDescending(c => c.Timestamp).ToList();
                if (heavyHitters.Any()) return "\"" + heavyHitters.First().Guid + "\";";
            }

            if (onlineCandidate != default)
            {
                if (onlineCandidate.Signal == 0 && candidates.Any(c => !c.IsOnline && c.Signal >= 28)) return "null;";
                if (onlineCandidate.Signal < 3  && candidates.Any(c => c.Signal >= 3)) return "null;";
                if (candidates.Count > 4 && onlineCandidate.Signal == 0) return "null;";
                return "\"" + onlineCandidate.Guid + "\";";
            }
            else
            {
                var offlineWinner = candidates.OrderByDescending(c => c.Signal).ThenByDescending(c => c.Timestamp).FirstOrDefault();
                if (offlineWinner != default && offlineWinner.Signal > 0)
                    return "\"" + offlineWinner.Guid + "\";";
            }
            return "null;";
        }

        public async Task<List<(string Guid, string Name, string City, string Nation, string Instrument, int Signal, bool IsOnline, long Timestamp)>> GuidFromIpAsync(string ipAddress)
        {
            string ipClean = ipAddress.Replace("::ffff:", "").Trim();

            var activeGuids = new HashSet<string>();
            var keys = JamulusCacheManager.LastReportedList.Keys.ToList();
            foreach (var key in keys)
            {
                if (_cacheManager.m_deserializedCache.TryGetValue(key, out var servers) && servers != null)
                {
                    foreach (var server in servers)
                        if (server.clients != null)
                            foreach (var client in server.clients)
                                activeGuids.Add(EncounterTracker.GetHash(client.name, client.country, client.instrument));
                }
                else if (JamulusCacheManager.LastReportedList.TryGetValue(key, out string json))
                {
                    try
                    {
                        var manualServers = JsonSerializer.Deserialize<List<JamulusServers>>(json);
                        foreach (var server in manualServers)
                            if (server.clients != null)
                                foreach (var client in server.clients)
                                    activeGuids.Add(EncounterTracker.GetHash(client.name, client.country, client.instrument));
                    }
                    catch { continue; }
                }
            }

            var guidMaxSignals = new Dictionary<string, int>();
            var guidNames      = new Dictionary<string, string>();
            var guidRecency    = new Dictionary<string, long>();
            var guidTimestamp  = new Dictionary<string, long>();
            long lineIndex = 0;

            if (File.Exists("join-events.csv"))
            {
                try
                {
                    using var fs = new FileStream("join-events.csv", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs);
                    string line;
                    while ((line = await sr.ReadLineAsync()) != null)
                    {
                        lineIndex++;
                        if (!line.Contains(ipClean)) continue;
                        var parts = line.Split(',');
                        if (parts.Length > 12)
                        {
                            string clientIpPort = parts[11].Trim();
                            if (clientIpPort.StartsWith(ipClean + ":"))
                            {
                                string candidateGuid = parts[2].Trim();
                                string rawName       = parts[3].Trim();
                                string decodedName   = System.Web.HttpUtility.UrlDecode(rawName);
                                long.TryParse(parts[0], out long ts);

                                if (int.TryParse(parts[12], out int signal))
                                {
                                    guidRecency[candidateGuid]   = lineIndex;
                                    guidTimestamp[candidateGuid] = ts;

                                    if (!guidMaxSignals.ContainsKey(candidateGuid))
                                    { guidMaxSignals[candidateGuid] = signal; guidNames[candidateGuid] = decodedName; }
                                    else
                                    {
                                        if (signal > guidMaxSignals[candidateGuid]) guidMaxSignals[candidateGuid] = signal;
                                        guidNames[candidateGuid] = decodedName;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (IOException ex) { Console.WriteLine($"[GuidFromIpAsync] Join Read Error: {ex.Message}"); }
            }

            var richCache = await GetCensusCacheAsync();
            var results   = new List<(string Guid, string Name, string City, string Nation, string Instrument, int Signal, bool IsOnline, long Timestamp)>();

            foreach (var kvp in guidMaxSignals)
            {
                string guid    = kvp.Key;
                int    signal  = kvp.Value;
                string name    = guidNames.ContainsKey(guid) ? guidNames[guid] : "Unknown";
                bool   isOnline = activeGuids.Contains(guid);
                long   ts      = guidTimestamp.ContainsKey(guid) ? guidTimestamp[guid] : 0;
                string city = "", nation = "", inst = "";

                if (richCache.TryGetValue(guid, out var info)) { city = info.City; nation = info.Nation; inst = info.Instrument; }

                results.Add((guid, name, city, nation, inst, signal, isOnline, ts));
            }

            return results.OrderByDescending(x => x.IsOnline).ThenByDescending(x => x.Signal).ThenByDescending(x => x.Timestamp).ToList();
        }

        public async Task<Dictionary<string, (string City, string Nation, string Instrument)>> GetCensusCacheAsync()
        {
            if (_censusCache != null && DateTime.Now < _censusCacheTime.AddMinutes(60))
                return _censusCache;

            await _censusLock.WaitAsync();
            try
            {
                if (_censusCache != null && DateTime.Now < _censusCacheTime.AddMinutes(60))
                    return _censusCache;

                Console.WriteLine("Loading Census Data into RAM Cache...");
                var newCache = new Dictionary<string, (string City, string Nation, string Instrument)>();

                if (File.Exists("data/censusgeo.csv"))
                {
                    using var fs = new FileStream("data/censusgeo.csv", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs);
                    string line;
                    while ((line = await sr.ReadLineAsync()) != null)
                    {
                        var parts = line.Split(',');
                        if (parts.Length >= 5)
                        {
                            string rowGuid = parts[0].Trim();
                            string inst    = parts[2].Trim();
                            string city    = System.Web.HttpUtility.UrlDecode(parts[3].Trim());
                            string nation  = System.Web.HttpUtility.UrlDecode(parts[4].Trim());

                            if (newCache.TryGetValue(rowGuid, out var existing))
                            { if (!string.IsNullOrWhiteSpace(city)) newCache[rowGuid] = (city, nation, inst); }
                            else
                            { newCache[rowGuid] = (city, nation, inst); }
                        }
                    }
                }

                Console.WriteLine($"Census Cache Loaded. {newCache.Count} unique profiles.");
                _censusCache     = newCache;
                _censusCacheTime = DateTime.Now;
                GC.Collect(2, GCCollectionMode.Forced, true, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetCensusCacheAsync] Error: {ex.Message}");
                if (_censusCache == null) _censusCache = new Dictionary<string, (string City, string Nation, string Instrument)>();
            }
            finally { _censusLock.Release(); }

            return _censusCache;
        }
    }
}
