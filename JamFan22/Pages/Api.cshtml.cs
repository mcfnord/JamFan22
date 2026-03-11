using JamFan22.Models;
using JamFan22.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace JamFan22.Pages
{
    public class ApiServer
    {
        public string category { get; set; }
        public string serverIpAddress { get; set; }
        public long serverPort { get; set; }
        public string name { get; set; }
        public string city { get; set; }
        public string country { get; set; }
        public int distanceAway { get; set; }
        public char zone { get; set; }
        public int usercount { get; set; }
        public int maxusercount { get; set; }

        public string activeJitsi { get; set; }
        public string videoUrl { get; set; }
        public string songTitle { get; set; }
        public string newJamFlag { get; set; }
        public string listenHtml { get; set; }
        public string serverDurationHtml { get; set; }
        public string leaversHtml { get; set; }
        public List<string> soonNames { get; set; } = new List<string>();

        public string smartNations { get; set; }
        public string newServerHtml { get; set; }

        public List<ApiClient> clients { get; set; } = new List<ApiClient>();
    }

    public class ApiClient
    {
        public string name { get; set; }
        public string country { get; set; }
        public string instrument { get; set; }
        public string skill { get; set; }
        public string city { get; set; }
        public bool isNewArrival { get; set; }
        public string hash { get; set; }
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public class ApiModel : PageModel
    {
        private readonly JamulusAnalyzer    _analyzer;
        private readonly JamulusCacheManager _cacheManager;
        private readonly EncounterTracker   _tracker;
        private readonly IpAnalyticsService _ipAnalytics;
        private readonly GeolocationService _geoService;

        // Per-request nation code
        private string m_TwoLetterNationCode = "US";

        private static readonly HttpClient httpClient = new HttpClient();

        public ApiModel(
            JamulusAnalyzer analyzer,
            JamulusCacheManager cacheManager,
            EncounterTracker tracker,
            IpAnalyticsService ipAnalytics,
            GeolocationService geoService)
        {
            _analyzer     = analyzer;
            _cacheManager = cacheManager;
            _tracker      = tracker;
            _ipAnalytics  = ipAnalytics;
            _geoService   = geoService;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            bool isDiagMode = Request.Query.ContainsKey("diag");
            System.Text.StringBuilder diagLog = null;
            if (isDiagMode)
            {
                diagLog = new System.Text.StringBuilder();
                diagLog.AppendLine($"Visibility Diagnostics generated at {DateTime.Now}");
                diagLog.AppendLine("=====================================================");
            }

            await JamulusCacheManager.m_serializerMutex.WaitAsync();
            try
            {
                string ipAddress = GetClientIpAddress();
                string UserNationCode = "US";

                if (!string.IsNullOrEmpty(ipAddress))
                {
                    if (IpAnalyticsService._countryCodeCache.TryGetValue(ipAddress, out var cached) && DateTime.Now < cached.Expiry)
                    {
                        UserNationCode = cached.Code;
                    }
                    else
                    {
                        try
                        {
                            string url      = $"http://ip-api.com/json/{ipAddress}";
                            string response = await httpClient.GetStringAsync(url);
                            var json        = Newtonsoft.Json.Linq.JObject.Parse(response);
                            string code     = (string)json["countryCode"];

                            if (!string.IsNullOrEmpty(code))
                            {
                                UserNationCode = code;
                                IpAnalyticsService._countryCodeCache[ipAddress] = (code, DateTime.Now.AddHours(48));
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Geo lookup failed in API: {ex.Message}");
                        }
                    }

                    if (Request.Query.ContainsKey("lang"))
                        UserNationCode = Request.Query["lang"].ToString().ToUpper();

                    m_TwoLetterNationCode = UserNationCode;

                    JamulusAnalyzer.MyUserGeoCandy geoData = await _analyzer.GetOrAddUserGeoDataAsync(ipAddress);
                    if (geoData != null)
                        _analyzer.UpdateUserStatistics(ipAddress, geoData);
                }

                _analyzer.InitializeGutsRequest();
                await _analyzer.UpdatePredictionsIfNeededAsync();
                var preloadedData = await _analyzer.GetCachedPreloadedDataAsync();
                await _analyzer.ProcessServerListsAsync(preloadedData);

                var sortedByDistanceAway = JamulusAnalyzer.m_allMyServers.OrderBy(svr => svr.distanceAway).ToList();
                var apiResponse = new List<ApiServer>();

                string ipDerivedHashWithSemicolon = await _analyzer.GetIPDerivedHashAsync(ipAddress);
                string cleanHash = ipDerivedHashWithSemicolon.Replace("\"", "").Replace(";", "");
                if (cleanHash != "null" && !string.IsNullOrEmpty(cleanHash))
                    Response.Headers.Append("X-IP-Derived-Hash", cleanHash);

                if (JamulusCacheManager.ListServicesOffline.Count > 0)
                {
                    string status = "<b>Oops!</b> Couldn't get updates for: ";
                    foreach (var list in JamulusCacheManager.ListServicesOffline)
                        status += list + ", ";
                    status = status.Substring(0, status.Length - 2);
                    Response.Headers.Append("X-System-Status", status);
                }
                else
                {
                    Response.Headers.Append("X-System-Status", "OK");
                }

                sortedByDistanceAway = JamulusAnalyzer.m_allMyServers
                    .OrderByDescending(svr => svr.whoObjectFromSourceData != null &&
                                              svr.whoObjectFromSourceData.Any(c => EncounterTracker.GetHash(c.name, c.country, c.instrument) == cleanHash))
                    .ThenBy(svr => svr.distanceAway)
                    .ToList();

                var globalActiveGuids = new HashSet<string>();
                foreach (var svr in JamulusAnalyzer.m_allMyServers)
                {
                    if (svr.whoObjectFromSourceData != null)
                        foreach (var c in svr.whoObjectFromSourceData)
                            if (!JamulusAnalyzer.NukeThisUsername(c.name, c.instrument, svr.name.ToLower().Contains("cbvb")))
                                globalActiveGuids.Add(EncounterTracker.GetHash(c.name, c.country, c.instrument));
                }

                var richCache = await _analyzer.GetCensusCacheAsync();
                await _analyzer.LoadConnectedLoungesAsync();

                foreach (var s in sortedByDistanceAway)
                {
                    string serverAddress          = s.serverIpAddress + ":" + s.serverPort;
                    string serverAddressWithDash   = s.serverIpAddress + "-" + s.serverPort;

                    // ── Persistence bubble ────────────────────────────────────
                    var bubbleUsers = new List<ApiClient>();
                    foreach (var entry in EncounterTracker.m_connectionLatestSighting)
                    {
                        if (entry.Key.Length == 32 + serverAddress.Length && entry.Key.EndsWith(serverAddress))
                        {
                            string guid = entry.Key.Substring(0, 32);
                            if (!globalActiveGuids.Contains(guid))
                            {
                                DateTime timeUserDisappeared = entry.Value;
                                var absoluteWait = (DateTime.Now - timeUserDisappeared).TotalSeconds;

                                bool networkHasCaughtUp = true;
                                foreach (var dirKey in JamulusCacheManager.JamulusListURLs.Keys)
                                {
                                    if (!JamulusCacheManager.DirectoryLastUpdated.ContainsKey(dirKey) || JamulusCacheManager.DirectoryLastUpdated[dirKey] <= timeUserDisappeared)
                                    { networkHasCaughtUp = false; break; }
                                }

                                if ((!networkHasCaughtUp && absoluteWait < 180) || absoluteWait <= 15)
                                {
                                    string rawName   = EncounterTracker.m_guidNamePairs.ContainsKey(guid) ? System.Web.HttpUtility.HtmlDecode(EncounterTracker.m_guidNamePairs[guid]) : "Unknown";
                                    string cleanName = (rawName ?? "").Trim().Replace("  ", " ").Replace("<", "");
                                    string inst = "", country = "", city = "";
                                    if (richCache.TryGetValue(guid, out var info)) { city = info.City; country = info.Nation; inst = info.Instrument; }

                                    string slimmerInst = (inst == "-" || inst == "Streamer") ? "" : inst;
                                    if (slimmerInst.Length > 0) slimmerInst = " " + slimmerInst;

                                    if (cleanName.Length == 0 && (slimmerInst == "" || slimmerInst == " Streamer")) continue;
                                    if (JamulusAnalyzer.NukeThisUsername(cleanName, inst, s.name.ToLower().Contains("cbvb"))) continue;

                                    bubbleUsers.Add(new ApiClient {
                                        name = cleanName, country = country, instrument = slimmerInst.Trim(),
                                        city = city, isNewArrival = false, hash = guid
                                    });
                                }
                            }
                        }
                    }

                    // ── Server suppression ────────────────────────────────────
                    var filteredUsersForRules = s.whoObjectFromSourceData?
                        .Where(cat => !JamulusAnalyzer.NukeThisUsername(cat.name, cat.instrument, s.name.ToLower().Contains("cbvb")))
                        .ToList() ?? new List<Client>();

                    int  totalVisibleUsers = filteredUsersForRules.Count + bubbleUsers.Count;
                    bool fSuppress = false;

                    if (isDiagMode)
                    {
                        diagLog.AppendLine($"\nServer: {s.name} ({serverAddress})");
                        diagLog.AppendLine($"- Total Visible: {totalVisibleUsers} (Filtered: {filteredUsersForRules.Count}, Bubble: {bubbleUsers.Count})");
                    }

                    if (totalVisibleUsers == 1)
                    {
                        string userHash = filteredUsersForRules.Count == 1
                            ? EncounterTracker.GetHash(filteredUsersForRules[0].name, filteredUsersForRules[0].country, filteredUsersForRules[0].instrument)
                            : bubbleUsers[0].hash;
                        double howLong = _tracker.DurationHereInMins(serverAddress, userHash);

                        if (isDiagMode) diagLog.AppendLine($"- 1 User Rule. Hash: {userHash}, Mins: {howLong:F1}");

                        if (!preloadedData.GoodGuids.Contains(userHash) && howLong > 60.0) 
                        { fSuppress = true; if (isDiagMode) diagLog.AppendLine("- ACTION: Suppressed (Not GoodGuid, > 60 mins)"); }
                        else if (howLong > 360.0) 
                        { fSuppress = true; if (isDiagMode) diagLog.AppendLine("- ACTION: Suppressed (> 360 mins)"); }

                        var excludedServerNames = new HashSet<string> { "JamPad", "portable" };
                        if (excludedServerNames.Contains(s.name)) 
                        { fSuppress = true; if (isDiagMode) diagLog.AppendLine("- ACTION: Suppressed (Excluded Name)"); }
                    }
                    else if (totalVisibleUsers > 1)
                    {
                        int iTimeoutPeriod = s.name.ToLower().Contains("priv") ? (4 * 60) : (8 * 60);
                        if (isDiagMode) diagLog.AppendLine($"- >1 User Rule. Timeout period: {iTimeoutPeriod} mins");

                        fSuppress = true;
                        foreach (var user in filteredUsersForRules)
                        {
                            string userHash = EncounterTracker.GetHash(user.name, user.country, user.instrument);
                            double mins = _tracker.DurationHereInMins(serverAddress, userHash);
                            if (isDiagMode) diagLog.AppendLine($"  - User: {user.name}, Hash: {userHash}, Mins: {mins:F1}");

                            if (mins < iTimeoutPeriod)
                            {
                                fSuppress = false;
                                if (isDiagMode) diagLog.AppendLine($"  - KEEPALIVE: User {user.name} is active (under timeout)");
                                break;
                            }
                        }
                        if (fSuppress)
                        {
                            foreach (var bu in bubbleUsers)
                            {
                                double mins = _tracker.DurationHereInMins(serverAddress, bu.hash);
                                if (isDiagMode) diagLog.AppendLine($"  - BubbleUser: {bu.hash}, Mins: {mins:F1}");

                                if (mins < iTimeoutPeriod)
                                {
                                    fSuppress = false;
                                    if (isDiagMode) diagLog.AppendLine($"  - KEEPALIVE: Bubble user is active (under timeout)");
                                    break;
                                }
                            }
                        }
                        if (fSuppress && isDiagMode) diagLog.AppendLine($"- ACTION: Suppressed (All {totalVisibleUsers} users exceeded {iTimeoutPeriod} mins)");
                    }
                    else 
                    { 
                        fSuppress = true; 
                        if (isDiagMode) diagLog.AppendLine("- ACTION: Suppressed (0 users)");
                    }

                    if (fSuppress) continue;

                    // ── Build API server object ───────────────────────────────
                    var apiSvr = new ApiServer {
                        category      = s.category,
                        serverIpAddress = s.serverIpAddress,
                        serverPort    = s.serverPort,
                        name          = s.name,
                        city          = s.city,
                        country       = s.country,
                        distanceAway  = s.distanceAway,
                        zone          = s.zone,
                        usercount     = s.usercount,
                        maxusercount  = s.maxusercount,
                        activeJitsi   = _analyzer.FindActiveJitsiOfJSvr(serverAddress),
                        newServerHtml = _analyzer.NoticeNewbs(serverAddress)
                            ? $"({JamulusAnalyzer.LocalizedText(m_TwoLetterNationCode, "New server", "新伺服器", "เซิร์ฟเวอร์ใหม่", "Neuer Server", "Nuovo server")}.)"
                            : "",
                        listenHtml    = await _analyzer.GetListenHtmlAsync(s)
                    };

                    if (harvest.m_songTitleAtAddr.TryGetValue(serverAddressWithDash, out string title) &&
                        JamulusCacheManager.MinutesSince2023AsInt() < harvest.m_timeToLive && title.Length > 0)
                    {
                        if (title.Contains(" by ") || title.Contains(" BY "))
                            title = title.Replace("  ", " ").Replace("&nbsp;", " ");
                        apiSvr.songTitle = title;
                    }

                    if (harvest.m_discreetLinks.TryGetValue(serverAddress, out string videoUrl))
                        apiSvr.videoUrl = videoUrl;

                    var currentLeavers = new List<string>();

                    foreach (var entry in EncounterTracker.m_connectionLatestSighting)
                    {
                        if (!entry.Key.Contains(serverAddress)) continue;
                        if (entry.Value.AddMinutes(4) > DateTime.Now && entry.Value.AddMinutes(1) < DateTime.Now)
                        {
                            string guid = entry.Key.Substring(0, "f2c26681da4d0013563cfd8c0619cfc7".Length);
                            if (EncounterTracker.m_guidNamePairs.TryGetValue(guid, out string leaverName) &&
                                !string.IsNullOrWhiteSpace(leaverName) && leaverName != "No Name" &&
                                leaverName != "Ear" && !leaverName.Contains("obby") &&
                                !currentLeavers.Contains(leaverName))
                            {
                                bool fFound = false;
                                if (s.whoObjectFromSourceData != null)
                                    foreach (var user in s.whoObjectFromSourceData)
                                        if (user.name == leaverName ||
                                           (user.name.Length > 3 && leaverName.Length > 3 &&
                                            user.name.ToLower().Substring(0, 3) == leaverName.ToLower().Substring(0, 3)))
                                        { fFound = true; break; }
                                if (!fFound) currentLeavers.Add(leaverName);
                            }
                        }
                    }

                    if (currentLeavers.Count > 0)
                    {
                        string byeText = JamulusAnalyzer.LocalizedText(m_TwoLetterNationCode, "Bye", "再見", "ลาก่อน", "Tschüss", "Ciao");
                        apiSvr.leaversHtml = $"<div style=\"color:gray; font-size:0.7em;\"><i>{byeText} {string.Join("&nbsp;&middot;&nbsp;", currentLeavers)}</i></div>";
                    }

                    if (_analyzer.Predicted.TryGetValue(serverAddress, out var predictedList))
                        foreach (var dude in predictedList)
                            if (EncounterTracker.m_guidNamePairs.TryGetValue(dude, out string soonName) && !string.IsNullOrWhiteSpace(soonName))
                                apiSvr.soonNames.Add(soonName);

                    if (s.whoObjectFromSourceData != null)
                    {
                        var sortedFilteredUsers = s.whoObjectFromSourceData
                            .Where(cat => !JamulusAnalyzer.NukeThisUsername(cat.name, cat.instrument, s.name.ToLower().Contains("cbvb")))
                            .OrderByDescending(guy => { string h = EncounterTracker.GetHash(guy.name, guy.country, guy.instrument); double d = _tracker.DurationHereInMins(serverAddress, h); return d < 0 ? 0 : d; })
                            .ThenBy(guy => guy.name)
                            .ToList();

                        apiSvr.smartNations = _ipAnalytics.SmartNations(sortedFilteredUsers.ToArray(), s.country);

                        bool allNew = true;
                        string firstUserHash = null;
                        foreach (var c in sortedFilteredUsers)
                        {
                            string userHash = EncounterTracker.GetHash(c.name, c.country, c.instrument);
                            if (firstUserHash == null) firstUserHash = userHash;
                            if (_tracker.DurationHereInMins(serverAddress, userHash) >= 14.0) allNew = false;
                        }

                        if (sortedFilteredUsers.Count > 1)
                        {
                            string translatedPhrase = JamulusAnalyzer.LocalizedText(m_TwoLetterNationCode, "Just&nbsp;gathered.", "成員皆剛加入", "เพิ่งรวมตัว", "soeben&nbsp;angekommen.", "appena&nbsp;connessi.");
                            if (allNew && sortedFilteredUsers.Count > 0)
                                apiSvr.newJamFlag = "(" + (s.usercount == s.maxusercount ? JamulusAnalyzer.LocalizedText(m_TwoLetterNationCode, "Full. ", "滿房。 ", "เต็ม ", "Volls. ", "Pieno. ") : "") + translatedPhrase + ")";
                            else if (s.usercount == s.maxusercount)
                                apiSvr.newJamFlag = "<b>(" + JamulusAnalyzer.LocalizedText(m_TwoLetterNationCode, "Full", "滿房", "เต็ม", "Voll", "piena") + ")</b>";
                            else if (s.usercount + 1 == s.maxusercount)
                                apiSvr.newJamFlag = JamulusAnalyzer.LocalizedText(m_TwoLetterNationCode, "(Almost full)", "(即將滿房)", "(เกือบเต็ม)", "(fast voll)", "(quasi pieno)");
                            else
                                apiSvr.newJamFlag = "";
                            apiSvr.serverDurationHtml = "";
                        }
                        else
                        {
                            apiSvr.newJamFlag = "";
                            apiSvr.serverDurationHtml = firstUserHash != null
                                ? _analyzer.DurationHere(serverAddress, firstUserHash, m_TwoLetterNationCode)
                                : "";
                        }

                        foreach (var c in sortedFilteredUsers)
                        {
                            var nam = (c.name ?? "").Trim().Replace("  ", " ").Replace("<", "");
                            string slimmerInstrument = (c.instrument == "-" || c.instrument == "Streamer") ? "" : c.instrument;
                            if (slimmerInstrument.Length > 0) slimmerInstrument = " " + slimmerInstrument;
                            if (nam.Length == 0 && (slimmerInstrument == "" || slimmerInstrument == " Streamer")) continue;

                            string encodedHashOfGuy = EncounterTracker.GetHash(c.name, c.country, c.instrument);
                            double minsHere         = _tracker.DurationHereInMins(serverAddress, encodedHashOfGuy);
                            bool   isNewArrival     = minsHere >= 0 && minsHere < 3.0;

                            apiSvr.clients.Add(new ApiClient {
                                name = nam, country = c.country, instrument = slimmerInstrument.Trim(),
                                skill = c.skill, city = c.city, isNewArrival = isNewArrival, hash = encodedHashOfGuy
                            });
                        }
                    }

                    if (bubbleUsers.Count > 0) apiSvr.clients.AddRange(bubbleUsers);

                    apiResponse.Add(apiSvr);
                }

                if (isDiagMode && diagLog != null)
                {
                    try { System.IO.File.WriteAllText("wwwroot/server-visibility.txt", diagLog.ToString()); }
                    catch (Exception ex) { Console.WriteLine($"Failed to write diagnostics: {ex.Message}"); }
                }

                stopwatch.Stop();
                _analyzer.AdjustPerformanceDelta(stopwatch.Elapsed);

                JamulusAnalyzer.m_safeServerSnapshot = JamulusAnalyzer.m_allMyServers.ToList();
                return new JsonResult(apiResponse);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CRITICAL ERROR] API Exception: {ex.Message}\n{ex.StackTrace}");
                return StatusCode(500, new { error = ex.Message, stack = ex.StackTrace });
            }
            finally
            {
                JamulusCacheManager.m_serializerMutex.Release();
            }
        }

        private string GetClientIpAddress()
        {
            var remoteIp = HttpContext.Connection.RemoteIpAddress;
            string ipString = (remoteIp != null && System.Net.IPAddress.IsLoopback(remoteIp))
                ? HttpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault()
                : remoteIp?.ToString();

            ipString ??= "24.18.55.230";
            return ipString.StartsWith("::ffff:") ? ipString : $"::ffff:{ipString}";
        }
    }
}
