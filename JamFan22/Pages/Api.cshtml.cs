using JamFan22.Models;
using JamFan22.Services;
using Microsoft.AspNetCore.Mvc;
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
        public List<string> leavers { get; set; } = new List<string>();
        public List<string> soonNames { get; set; } = new List<string>();

        public string smartNations { get; set; }
        public bool isNewServer { get; set; }

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

    public class ApiModel : IndexModel
    {
        public ApiModel(ILogger<IndexModel> logger, GeolocationService geoService)
            : base(logger, geoService)
        {
        }

        public override async Task<IActionResult> OnGetAsync()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            await m_serializerMutex.WaitAsync();
            try
            {
                string ipAddress = GetClientIpAddress();
                string UserNationCode = "US"; 

                if (!string.IsNullOrEmpty(ipAddress))
                {
                    if (_countryCodeCache.TryGetValue(ipAddress, out var cached) && DateTime.Now < cached.Expiry)
                    {
                        UserNationCode = cached.Code;
                    }
                    else
                    {
                        try
                        {
                            string url = $"http://ip-api.com/json/{ipAddress}";
                            string response = await httpClient.GetStringAsync(url);
                            var json = Newtonsoft.Json.Linq.JObject.Parse(response);
                            string code = (string)json["countryCode"];

                            if (!string.IsNullOrEmpty(code))
                            {
                                UserNationCode = code;
                                _countryCodeCache[ipAddress] = (code, DateTime.Now.AddHours(48));
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Geo lookup failed in API: {ex.Message}");
                        }
                    }

                    m_TwoLetterNationCode = UserNationCode;

                    MyUserGeoCandy geoData = await GetOrAddUserGeoDataAsync(ipAddress);
                    if (geoData != null)
                    {
                        UpdateUserStatistics(ipAddress, geoData);
                    }
                }

                InitializeGutsRequest();
                await UpdatePredictionsIfNeededAsync();
                var preloadedData = await GetCachedPreloadedDataAsync();
                await ProcessServerListsAsync(preloadedData);

                var sortedByDistanceAway = m_allMyServers.OrderBy(svr => svr.distanceAway).ToList();
                var apiResponse = new List<ApiServer>();

                string ipDerivedHashWithSemicolon = await GetIPDerivedHashAsync();
                string cleanHash = ipDerivedHashWithSemicolon.Replace("\"", "").Replace(";", "");
                if (cleanHash != "null" && !string.IsNullOrEmpty(cleanHash))
                {
                    Response.Headers.Append("X-IP-Derived-Hash", cleanHash);
                }

                if (ListServicesOffline.Count > 0)
                {
                    string status = "<b>Oops!</b> Couldn't get updates for: ";
                    foreach (var list in ListServicesOffline)
                        status += list + ", ";
                    status = status.Substring(0, status.Length - 2); // chop comma
                    Response.Headers.Append("X-System-Status", status);
                }
                else
                {
                    Response.Headers.Append("X-System-Status", "");
                }

                // Re-sort the list so the active server is explicitly at the top
                sortedByDistanceAway = m_allMyServers
                    .OrderByDescending(svr => svr.whoObjectFromSourceData != null && 
                                              svr.whoObjectFromSourceData.Any(c => GetHash(c.name, c.country, c.instrument) == cleanHash))
                    .ThenBy(svr => svr.distanceAway)
                    .ToList();

                // ---------------------------------------------------------
                // 1. Build a master roster of every GUID currently active 
                //    anywhere in this exact Epoch to prevent ghost duplication
                // ---------------------------------------------------------
                var globalActiveGuids = new HashSet<string>();
                foreach (var svr in m_allMyServers)
                {
                    if (svr.whoObjectFromSourceData != null)
                    {
                        foreach (var c in svr.whoObjectFromSourceData)
                        {
                            if (!NukeThisUsername(c.name, c.instrument, svr.name.ToLower().Contains("cbvb")))
                            {
                                globalActiveGuids.Add(GetHash(c.name, c.country, c.instrument));
                            }
                        }
                    }
                }

                // We need the Census cache to reconstruct names/instruments of our artificially persistent users
                var richCache = await GetCensusCacheAsync();

                // Load lounge data once before looping
                await LoadConnectedLoungesAsync();

                foreach (var s in sortedByDistanceAway)
                {
                    string serverAddress = s.serverIpAddress + ":" + s.serverPort;
                    string serverAddressWithDash = s.serverIpAddress + "-" + s.serverPort;

                    // ---------------------------------------------------------
                    // 2. THE PERSISTENCE BUBBLE: Dynamic Watermark Wait
                    // ---------------------------------------------------------
                    var bubbleUsers = new List<ApiClient>();
                    foreach (var entry in m_connectionLatestSighting)
                    {
                        // Look for tracking keys belonging to this specific server
                        if (entry.Key.Length == 32 + serverAddress.Length && entry.Key.EndsWith(serverAddress))
                        {
                            string guid = entry.Key.Substring(0, 32);

                            // Only inject if they are NOT currently active on ANY server
                            if (!globalActiveGuids.Contains(guid))
                            {
                                DateTime timeUserDisappeared = entry.Value;
                                var absoluteWait = (DateTime.Now - timeUserDisappeared).TotalSeconds;

                                // CHECK THE WATERMARK: Have all directories updated since this user left?
                                bool networkHasCaughtUp = true;
                                foreach (var dirKey in JamulusListURLs.Keys)
                                {
                                    if (!DirectoryLastUpdated.ContainsKey(dirKey) || DirectoryLastUpdated[dirKey] <= timeUserDisappeared)
                                    {
                                        networkHasCaughtUp = false;
                                        break; // Found at least one directory still lagging
                                    }
                                }

                                // KEEP THE GHOST IF:
                                // 1. Network hasn't caught up (waiting for round-robin) AND < 180s (fail-safe)
                                // 2. OR it has been <= 15s (guaranteed minimum animation buffer)
                                if ((!networkHasCaughtUp && absoluteWait < 180) || absoluteWait <= 15)
                                {
                                    string name = m_guidNamePairs.ContainsKey(guid) ? m_guidNamePairs[guid] : "Unknown";
                                    string inst = "";
                                    string country = "";
                                    string city = "";

                                    if (richCache.TryGetValue(guid, out var info))
                                    {
                                        city = info.City;
                                        country = info.Nation;
                                        inst = info.Instrument;
                                    }

                                    bubbleUsers.Add(new ApiClient {
                                        name = name,
                                        country = country,
                                        instrument = inst,
                                        city = city,
                                        isNewArrival = false, 
                                        hash = guid
                                    });
                                }
                            }
                        }
                    }

                    // ---------------------------------------------------------
                    // 3. SERVER SUPPRESSION RULES
                    // ---------------------------------------------------------
                    var filteredUsersForRules = s.whoObjectFromSourceData?
                        .Where(cat => !NukeThisUsername(cat.name, cat.instrument, s.name.ToLower().Contains("cbvb")))
                        .ToList() ?? new List<Client>();

                    // We combine the real users and the bubble users to decide if this server stays alive
                    int totalVisibleUsers = filteredUsersForRules.Count + bubbleUsers.Count;
                    bool fSuppress = false;

                    if (totalVisibleUsers == 1)
                    {
                        // Rules 1 & 2: Singletons
                        string userHash = filteredUsersForRules.Count == 1 
                            ? GetHash(filteredUsersForRules[0].name, filteredUsersForRules[0].country, filteredUsersForRules[0].instrument) 
                            : bubbleUsers[0].hash;

                        double howLong = DurationHereInMins(serverAddress, userHash);

                        if (!preloadedData.GoodGuids.Contains(userHash) && howLong > 60.0)
                        {
                            fSuppress = true; // Uncorrelated singleton over 1 hour
                        }
                        else if (howLong > 360.0)
                        {
                            fSuppress = true; // Any singleton over 6 hours
                        }
                        
                        var excludedServerNames = new HashSet<string> { "JamPad", "portable" };
                        if (excludedServerNames.Contains(s.name))
                        {
                            fSuppress = true;
                        }
                    }
                    else if (totalVisibleUsers > 1)
                    {
                        // Rule 3: Multi-user stale servers
                        int iTimeoutPeriod = s.name.ToLower().Contains("priv") ? (4 * 60) : (8 * 60);
                        fSuppress = true; // Assume stale until proven otherwise

                        foreach (var user in filteredUsersForRules)
                        {
                            if (DurationHereInMins(serverAddress, GetHash(user.name, user.country, user.instrument)) < iTimeoutPeriod)
                            {
                                fSuppress = false; break;
                            }
                        }

                        // Check bubble users if the server still looks stale
                        if (fSuppress)
                        {
                            foreach (var bu in bubbleUsers)
                            {
                                if (DurationHereInMins(serverAddress, bu.hash) < iTimeoutPeriod)
                                {
                                    fSuppress = false; break;
                                }
                            }
                        }
                    }
                    else 
                    {
                        fSuppress = true; // 0 users remaining
                    }

                    if (fSuppress)
                    {
                        continue; // Skip adding this server to the API response
                    }

                    // ---------------------------------------------------------
                    // 4. BUILD THE API SERVER OBJECT
                    // ---------------------------------------------------------
                    var apiSvr = new ApiServer {
                        category = s.category,
                        serverIpAddress = s.serverIpAddress,
                        serverPort = s.serverPort,
                        name = s.name,
                        city = s.city,
                        country = s.country,
                        distanceAway = s.distanceAway,
                        zone = s.zone,
                        usercount = s.usercount,
                        maxusercount = s.maxusercount,
                        activeJitsi = FindActiveJitsiOfJSvr(serverAddress),
                        isNewServer = NoticeNewbs(serverAddress),
                        listenHtml = await GetListenHtmlAsync(s)
                    };

                    if (harvest.m_songTitleAtAddr.TryGetValue(serverAddressWithDash, out string title) &&
                        MinutesSince2023AsInt() < harvest.m_timeToLive &&
                        title.Length > 0)
                    {
                        if (title.Contains(" by ")) title = title.Replace("  ", " ").Replace("&nbsp;", " ");
                        else if (title.Contains(" BY ")) title = title.Replace("  ", " ").Replace("&nbsp;", " ");
                        apiSvr.songTitle = title;
                    }

                    if (harvest.m_discreetLinks.TryGetValue(serverAddress, out string videoUrl))
                    {
                        apiSvr.videoUrl = videoUrl;
                    }

                    foreach (var entry in m_connectionLatestSighting)
                    {
                        if (!entry.Key.Contains(serverAddress)) continue;

                        if (entry.Value.AddMinutes(4) > DateTime.Now && entry.Value.AddMinutes(1) < DateTime.Now)
                        {
                            string guid = entry.Key.Substring(0, "f2c26681da4d0013563cfd8c0619cfc7".Length);
                            if (m_guidNamePairs.TryGetValue(guid, out string leaverName) &&
                                !string.IsNullOrWhiteSpace(leaverName) &&
                                leaverName != "No Name" && leaverName != "Ear" && !leaverName.Contains("obby") &&
                                !apiSvr.leavers.Contains(leaverName))
                            {
                                bool fFound = false;
                                if (s.whoObjectFromSourceData != null)
                                {
                                    foreach (var user in s.whoObjectFromSourceData)
                                    {
                                        if (user.name == leaverName ||
                                           (user.name.Length > 3 && leaverName.Length > 3 && user.name.ToLower().Substring(0, 3) == leaverName.ToLower().Substring(0, 3)))
                                        {
                                            fFound = true;
                                            break;
                                        }
                                    }
                                }
                                
                                if (!fFound)
                                {
                                    apiSvr.leavers.Add(leaverName);
                                }
                            }
                        }
                    }

                    if (m_predicted.TryGetValue(serverAddress, out var predictedList))
                    {
                        foreach (var dude in predictedList)
                        {
                            if (m_guidNamePairs.TryGetValue(dude, out string soonName) && !string.IsNullOrWhiteSpace(soonName))
                            {
                                apiSvr.soonNames.Add(soonName);
                            }
                        }
                    }

                    if (s.whoObjectFromSourceData != null)
                    {
                        var sortedFilteredUsers = s.whoObjectFromSourceData
                            .Where(cat => !NukeThisUsername(cat.name, cat.instrument, s.name.ToLower().Contains("cbvb")))
                            .OrderByDescending(guy =>
                            {
                                string h = GetHash(guy.name, guy.country, guy.instrument);
                                double d = DurationHereInMins(serverAddress, h);
                                return (d < 0) ? 0 : d;
                            })
                            .ThenBy(guy => guy.name)
                            .ToList();
                        
                        apiSvr.smartNations = SmartNations(sortedFilteredUsers.ToArray(), s.country);

                        bool allNew = true;
                        string firstUserHash = null;
                        
                        foreach (var c in sortedFilteredUsers)
                        {
                            string userHash = GetHash(c.name, c.country, c.instrument);
                            if (firstUserHash == null) firstUserHash = userHash;
                            
                            double durationMins = DurationHereInMins(serverAddress, userHash);
                            if (durationMins >= 14.0)
                            {
                                allNew = false;
                            }
                        }
                        
                        if (sortedFilteredUsers.Count > 1)
                        {
                            string translatedPhrase = LocalizedText("Just&nbsp;gathered.", "成員皆剛加入", "เพิ่งรวมตัว", "soeben&nbsp;angekommen.", "appena&nbsp;connessi.");
                            if (allNew && sortedFilteredUsers.Count > 0)
                            {
                                apiSvr.newJamFlag = "(" + ((s.usercount == s.maxusercount) ? LocalizedText("Full. ", "滿房。 ", "เต็ม ", "Volls. ", "Pieno. ") : "") + translatedPhrase + ")";
                            }
                            else if (s.usercount == s.maxusercount)
                            {
                                apiSvr.newJamFlag = "<b>(" + LocalizedText("Full", "滿房", "เต็ม", "Voll", "piena") + ")</b>";
                            }
                            else if (s.usercount + 1 == s.maxusercount)
                            {
                                apiSvr.newJamFlag = LocalizedText("(Almost full)", "(即將滿房)", "(เกือบเต็ม)", "(fast voll)", "(quasi pieno)");
                            }
                            else
                            {
                                apiSvr.newJamFlag = "";
                            }
                            apiSvr.serverDurationHtml = "";
                        }
                        else
                        {
                            apiSvr.newJamFlag = "";
                            if (firstUserHash != null)
                            {
                                apiSvr.serverDurationHtml = DurationHere(serverAddress, firstUserHash);
                            }
                            else
                            {
                                apiSvr.serverDurationHtml = "";
                            }
                        }

                        // Add real clients
                        foreach (var c in sortedFilteredUsers)
                        {
                            var nam = (c.name ?? "").Trim().Replace("  ", " ").Replace("<", "");
                            string slimmerInstrument = (c.instrument == "-" || c.instrument == "Streamer") ? "" : c.instrument;
                            if (slimmerInstrument.Length > 0) slimmerInstrument = " " + slimmerInstrument;
                            
                            if (nam.Length == 0 && (slimmerInstrument == "" || slimmerInstrument == " Streamer")) continue;

                            string encodedHashOfGuy = GetHash(c.name, c.country, c.instrument);
                            
                            double minsHere = DurationHereInMins(serverAddress, encodedHashOfGuy);
                            bool isNewArrival = (minsHere >= 0 && minsHere < 3.0);

                            apiSvr.clients.Add(new ApiClient {
                                name = nam,
                                country = c.country,
                                instrument = slimmerInstrument.Trim(),
                                skill = c.skill,
                                city = c.city,
                                isNewArrival = isNewArrival,
                                hash = encodedHashOfGuy
                            });
                        }
                    }

                    // Append the persistence bubble users (Ghosts) to the client list
                    if (bubbleUsers.Count > 0)
                    {
                        apiSvr.clients.AddRange(bubbleUsers);
                    }

                    apiResponse.Add(apiSvr);
                }

                stopwatch.Stop();
                AdjustPerformanceDelta(stopwatch.Elapsed);

                return new JsonResult(apiResponse);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CRITICAL ERROR] API Exception: {ex.Message}\n{ex.StackTrace}");
                return StatusCode(500, new { error = ex.Message, stack = ex.StackTrace });
            }
            finally
            {
                m_serializerMutex.Release();
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