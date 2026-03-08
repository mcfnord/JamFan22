using JamFan22.Models;
using JamFan22.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JamFan22.Pages
{
    public partial class IndexModel : PageModel
    {
        public static Dictionary<string, string> m_connectedLounges = new Dictionary<string, string>();
        public static List<string> m_listenLinkDeployment = new List<string>();
        public static int m_snippetsDeployed = 0;

        protected async Task LoadConnectedLoungesAsync()
        {
            try
            {
                using var client = new HttpClient();
                var response = await client.GetAsync("https://jamulus.live/lounges.json");
                response.EnsureSuccessStatusCode();

                string responseBody = await response.Content.ReadAsStringAsync();
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(responseBody);
                foreach (var kvp in data)
                {
                    m_connectedLounges[kvp.Value] = kvp.Key; // swap 'em
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load lounges.json: {ex.Message}");
            }

            // Add hardcoded lounges. Listen only appears when an obby or blank name is present
            m_connectedLounges["https://lobby.jam.voixtel.net.br/"] = "179.228.137.154:22124";
            m_connectedLounges["http://1.onj.me:32123/"] = "139.162.251.38:22124";
            m_connectedLounges["http://3.onj.me:8000/jamulus4"] = "69.164.213.250:22124";
            m_connectedLounges["https://StudioD.live"] = "	24.199.127.71:22224";
        }

        private async Task<string> GenerateServerListHtmlAsync(IEnumerable<ServersForMe> sortedServers, PreloadedData data)
        {
            var output = new System.Text.StringBuilder();

            // Load lounge data once before looping
            await LoadConnectedLoungesAsync();

            foreach (var s in sortedServers)
            {
                // --- Server Block List Checks ---
                string serverIpPort = s.serverIpAddress + ":" + s.serverPort;
                if (data.ErasedServerNames.Any(erased => s.name.ToLower().Contains(erased)))
                {
                    continue;
                }
                if (data.NoPingIpPartial.Any(ip => serverIpPort.Contains(ip)))
                {
                    continue;
                }

                // --- FIX: Await the new async method ---
                string asn = await AsnOfThisIpAsync(s.serverIpAddress);
                
                if (data.BlockedServerARNs.Contains(asn))
                {
                    Console.WriteLine(s.serverIpAddress + " blocked because in asn " + asn);
                    continue;
                }
                // --- End Block List Checks ---

                // Get a cleaner city name
                string evenSmarterCity = await GetSmarterCityAsync(s.city, s.serverIpAddress);

                // Re-filter users based on NukeThisUsername
                var filteredUsers = s.whoObjectFromSourceData
                    .Where(cat => !NukeThisUsername(cat.name, cat.instrument, s.name.ToLower().Contains("cbvb")))
                    .ToList();

                int s_myUserCount = filteredUsers.Count;

                if (s_myUserCount > 1)
                {
                    output.Append(await BuildMultiUserServerCardAsync(s, filteredUsers, evenSmarterCity));
                }
                else
                {
                    output.Append(BuildSingleUserServerCard(s, filteredUsers, evenSmarterCity, data.GoodGuids));
                }
            }
            return output.ToString();
        }

        private async Task<string> BuildMultiUserServerCardAsync(ServersForMe s, List<Client> filteredUsers, string evenSmarterCity)
        {
            string serverAddress = s.serverIpAddress + ":" + s.serverPort;

            // --- 1. Timeout/Suppression & "Just Gathered" Detection ---
            int iTimeoutPeriod = s.name.ToLower().Contains("priv") ? (4 * 60) : (8 * 60);
            bool fSuppress = true;
            
            bool allJustGathered = true; 

            foreach (var user in filteredUsers)
            {
                double duration = DurationHereInMins(serverAddress, GetHash(user.name, user.country, user.instrument));
                
                if (duration < iTimeoutPeriod)
                {
                    fSuppress = false;
                }

                if (duration >= 14.0) 
                {
                    allJustGathered = false;
                }
            }

            if (fSuppress) return ""; // Skip inactive servers

            var newline = new System.Text.StringBuilder();

            string newJamFlag = GetNewJamFlag(s, filteredUsers);
            string smartcity = SmartCity(evenSmarterCity, filteredUsers.ToArray());
            string smartNations = SmartNations(filteredUsers.ToArray(), s.country);
            string listenNow = await GetListenHtmlAsync(s);
            string liveSnippet = (listenNow.Length == 0) ? await GetSnippetHtmlAsync(serverAddress) : "";
            string htmlForVideoUrl = GetVideoHtml(serverAddress);
            string titleToShow = GetSongTitleHtml(s.serverIpAddress + "-" + s.serverPort);
            string leaversHtml = GetLeaversHtml(s);
            string soonHtml = GetSoonHtml(serverAddress);
            string activeJitsi = FindActiveJitsiOfJSvr(serverAddress);

            string divStyle = BackgroundByZone(s.zone);
            if (string.IsNullOrEmpty(divStyle)) divStyle = " style=\"position:relative;\"";
            else divStyle = divStyle.Replace("style=\"", "style=\"position:relative; ");

            newline.Append($"<div id=\"{serverAddress}\"{divStyle}><center>");

            newline.Append($"<a href='https://jamulus.live/jamgroup-map.html?server={serverAddress}' " +
                            $"target='_blank' " +
                            $"style='position:absolute; right:5px; top:5px; z-index:10; text-decoration:none; font-size:1.5em; cursor:pointer; opacity:0; transition:opacity 0.2s;' " +
                            $"onmouseover=\"this.style.opacity='1'\" " +
                            $"onmouseout=\"this.style.opacity='0'\">👀</a>");

            if (s.name.Length > 0)
            {
                string name = s.name.Contains("CBVB") ? s.name + " (Regional)" : s.name;
                
                if (allJustGathered)
                {
                    newline.Append($"<span class='just-arrived'>{System.Web.HttpUtility.HtmlEncode(name)}</span><br>");
                }
                else
                {
                    newline.Append(System.Web.HttpUtility.HtmlEncode(name) + "<br>");
                }
            }

            if (smartcity.Length > 0) newline.Append("<b>" + smartcity + "</b><br>");

            newline.Append($"<font size='-1'>{s.category.Replace("Genre ", "").Replace(" ", "&nbsp;")}</font><br>");
            
            if (newJamFlag.Length > 0) newline.Append(newJamFlag + "<br>");
            
            if (activeJitsi.Length > 0) newline.Append($"<b><a target='_blank' href='{activeJitsi}'>Jitsi Video</a></b>");
            if (NoticeNewbs(serverAddress)) newline.Append(LocalizedText("(New server.)", "(新伺服器)", "(เซิร์ฟเวอร์ใหม่)", "(neuer Server)", "(Nuovo server.)") + "<br>");

            newline.Append(liveSnippet);
            newline.Append(listenNow);
            newline.Append(htmlForVideoUrl);
            newline.Append(titleToShow);
            newline.Append("</center><hr>");
            newline.Append(s.who); 
            newline.Append(leaversHtml);

            if (smartcity != smartNations)
            {
                newline.Append($"<center><font size='-2'>{smartNations.Trim()}</font></center>");
            }

            if (soonHtml.Length > 0) newline.Append($"<center>{soonHtml}</center>");

            newline.Append("</div>");

            return newline.ToString();
        }

        private string BuildSingleUserServerCard(ServersForMe s, List<Client> filteredUsers, string evenSmarterCity, HashSet<string> goodGuids)
        {
            var firstUser = filteredUsers.FirstOrDefault();
            if (firstUser == null) return ""; // Should not happen if s_myUserCount=1, but safe

            string userHash = GetHash(firstUser.name, firstUser.country, firstUser.instrument);
            string serverAddress = $"{s.serverIpAddress}:{s.serverPort}"; // Moved up for access
            double howLong = DurationHereInMins(serverAddress, userHash);

            if (!goodGuids.Contains(userHash))
            {
                if (howLong > 60.0) 
                {
                    return ""; 
                }
            }

            const int maxDurationMinutes = 6 * 60; // 6 hours
            if (howLong > maxDurationMinutes)
            {
                return ""; // Silently hide loiterers > 6h
            }

            var excludedServerNames = new HashSet<string> { "JamPad", "portable" };
            if (excludedServerNames.Contains(s.name))
            {
                return "";
            }

            var htmlBuilder = new System.Text.StringBuilder();
            string smartCity = SmartCity(evenSmarterCity, filteredUsers.ToArray());
            string whoStringNoBreaks = s.who.Replace("<br/>", " "); // Original logic
            string categoryDisplay = s.category.Replace("Genre ", "").Replace(" ", "&nbsp;");

            htmlBuilder.Append($"<div {BackgroundByZone(s.zone)}><center>");

            if (!string.IsNullOrEmpty(s.name))
            {
                htmlBuilder.Append($"{System.Web.HttpUtility.HtmlEncode(s.name)}<br>");
            }
            if (!string.IsNullOrEmpty(smartCity))
            {
                htmlBuilder.Append($"<b>{smartCity}</b><br>");
            }

            htmlBuilder.Append($"<font size='-1'>{categoryDisplay}</font><br>");

            if (NoticeNewbs(serverAddress))
            {
                htmlBuilder.Append("(New server.)<br>");
            }

            htmlBuilder.Append("</center><hr>");
            htmlBuilder.Append(whoStringNoBreaks); // The 'who' string
            htmlBuilder.Append(DurationHere(serverAddress, userHash));
            htmlBuilder.Append("</div>");

            return htmlBuilder.ToString();
        }

        private string GetNewJamFlag(ServersForMe s, List<Client> users)
        {
            string newJamFlag = "";
            bool allNew = true;
            foreach (var user in users)
            {
                if (DurationHereInMins(s.serverIpAddress + ":" + s.serverPort, GetHash(user.name, user.country, user.instrument)) >= 14)
                {
                    allNew = false;
                    break;
                }
            }

            if (allNew)
            {
                string translatedPhrase = LocalizedText("Just&nbsp;gathered.", "成員皆剛加入", "เพิ่งรวมตัว", "soeben&nbsp;angekommen.", "appena&nbsp;connessi.");
                newJamFlag = "(" + ((s.usercount == s.maxusercount) ? LocalizedText("Full. ", "滿房。 ", "เต็ม ", "Volls. ", "Pieno. ") : "") + translatedPhrase + ")";
            }
            else if (s.usercount == s.maxusercount)
            {
                newJamFlag = "<b>(" + LocalizedText("Full", "滿房", "เต็ม", "Voll", "piena") + ")</b>";
            }
            else if (s.usercount + 1 == s.maxusercount)
            {
                newJamFlag = LocalizedText("(Almost full)", "(即將滿房)", "(เกือบเต็ม)", "(fast voll)", "(quasi pieno)");
            }
            return newJamFlag;
        }

        protected async Task<string> GetListenHtmlAsync(ServersForMe s)
        {
            string ipport = s.serverIpAddress + ":" + s.serverPort;

            foreach (var url in m_connectedLounges.Keys)
            {
                if (m_connectedLounges[url].Contains(ipport))
                {
                    foreach (var user in s.whoObjectFromSourceData)
                    {
                        if (user.name.Contains("obby") || user.name == "")
                        {
                            string num = "";
                            var iPos = user.name.IndexOf("[");
                            if (iPos > 0 && '0' != user.name[iPos + 1])
                            {
                                num = "<sub> " + user.name[iPos + 1] + "</sub>";
                            }
                            m_listenLinkDeployment.Add(ipport);
                            return $"<b><a class='listenlink listenalready' target='_blank' href='{url}'>Listen</a></b>{num}</br>";
                        }
                    }
                }
            }

            return "";
        }

        private async Task<string> GetSnippetHtmlAsync(string serverAddress)
        {
            string DIR = "/root/JamFan22/JamFan22/wwwroot/mp3s/";

            string silPath = System.IO.Path.Combine(DIR, serverAddress + ".sil");
            if (System.IO.File.Exists(silPath))
            {
                return "(Silent)";
            }

            try
            {
                string mp3Path = System.IO.Path.Combine(DIR, serverAddress + ".mp3");
                if (System.IO.File.Exists(mp3Path))
                {
                    m_snippetsDeployed++;
                    return $"<audio class='playa' controls style='width: 150px;' src='mp3s/{serverAddress}.mp3' />";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking local mp3s: {ex.Message}");
            }

            return "";
        }

        private string GetVideoHtml(string serverAddress)
        {
            if (harvest.m_discreetLinks.TryGetValue(serverAddress, out string videoUrl))
            {
                if (videoUrl.ToLower().Contains("zoom"))
                    return $"<a class='vid' href='{videoUrl}'><b>Zoom Video</b></a><br>";
                if (videoUrl.ToLower().Contains("https://meet."))
                    return $"<a class='vid' href='{videoUrl}'><b>Meet Video</b></a><br>";
                if (videoUrl.ToLower().Contains("jit.si"))
                    return "<b>Jitsi Video</b><br>";
                if (videoUrl.ToLower().Contains("vdo.ninja"))
                    return $"<a class='vid' href='{videoUrl}'><b>VDO.Ninja Video</b></a><br>";
            }
            return "";
        }

        private string GetSongTitleHtml(string serverAddressWithDash)
        {
            if (harvest.m_songTitleAtAddr.TryGetValue(serverAddressWithDash, out string title) &&
                MinutesSince2023AsInt() < harvest.m_timeToLive &&
                title.Length > 0)
            {
                if (title.Length > 25)
                {
                    if (title.Contains(" by "))
                    {
                        title = title.Replace("  ", " ").Replace(" ", "&nbsp;").Replace("&nbsp;by&nbsp;", " by&nbsp;");
                    }
                    else if (title.Contains(" BY "))
                    {
                        title = title.Replace("  ", " ").Replace(" ", "&nbsp;").Replace("&nbsp;BY&nbsp;", " BY&nbsp;");
                    }
                }
                return $"<font size='-2'><i>{title}</i></font><br>";
            }
            return "";
        }

        private string GetLeaversHtml(ServersForMe s)
        {
            string leavers = "";
            foreach (var entry in m_connectionLatestSighting)
            {
                if (!entry.Key.Contains(s.serverIpAddress + ":" + s.serverPort))
                {
                    continue;
                }

                if (entry.Value.AddMinutes(4) > DateTime.Now && entry.Value.AddMinutes(1) < DateTime.Now)
                {
                    string guid = entry.Key.Substring(0, "f2c26681da4d0013563cfd8c0619cfc7".Length);
                    if (m_guidNamePairs.TryGetValue(guid, out string name) &&
                        name != "No Name" && name != "Ear" && !name.Contains("obby") &&
                        !leavers.Replace("&nbsp;", " ").Contains(name))
                    {
                        bool fFound = false;
                        foreach (var user in s.whoObjectFromSourceData)
                        {
                            if (user.name == name ||
                               (user.name.Length > 3 && name.Length > 3 && user.name.ToLower().Substring(0, 3) == name.ToLower().Substring(0, 3)))
                            {
                                fFound = true;
                                break;
                            }
                        }
                        if (!fFound)
                        {
                            leavers += name.Replace(" ", "&nbsp;") + WholeMiddotString;
                        }
                    }
                }
            }

            if (leavers.Length > 0)
            {
                leavers = leavers.Substring(0, leavers.Length - WholeMiddotString.Length);
                return $"<center><font color='gray' size='-2'><i>{LocalizedText("Bye", "再見", "บ๊ายบาย", "Tschüss", "Ciao")} {leavers}</i></font></center>";
            }
            return "";
        }

        private string GetSoonHtml(string serverAddress)
        {
            if (m_predicted.ContainsKey(serverAddress))
            {
                string soonNames = "";
                foreach (var dude in m_predicted[serverAddress])
                {
                    if (m_guidNamePairs.ContainsKey(dude))
                    {
                        soonNames += m_guidNamePairs[dude] + " &#8226; ";
                    }
                    else
                    {
                        foreach (var line in System.IO.File.ReadLines("data/censusgeo.csv"))
                        {
                            var fields = line.Split(',');
                            if (fields.Length >= 2 && fields[0] == dude)
                            {
                                soonNames += fields[1] + " &#8226; ";
                                break;
                            }
                        }
                    }
                }

                if (false && soonNames.Length > 0) 
                {
                    soonNames = "<hr>Soon: " + soonNames.Substring(0, soonNames.Length - " &#8226; ".Length);
                    return soonNames;
                }
            }
            return "";
        }

        /// <summary>
        /// Iterates through the master server lists and processes each server.
        /// Populates the 'm_allMyServers' list.
        /// </summary>
        protected async Task ProcessServerListsAsync(PreloadedData data)
        {
            Console.WriteLine($"[DEBUG] ProcessServerListsAsync started. LastReportedList keys: {string.Join(", ", LastReportedList.Keys)}");
            int totalProcessed = 0;
            int totalAdded = 0;

            foreach (var key in LastReportedList.Keys)
            {
                var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(LastReportedList[key]);
                Console.WriteLine($"[DEBUG] Processing {key}: {serversOnList.Count} servers.");

                foreach (var server in serversOnList)
                {
                    totalProcessed++;
                    int people = server.clients?.GetLength(0) ?? 0;

                    if (ShouldSkipServer(server, people))
                    {
                        continue;
                    }

                    // Process all clients on this server
                    var clientResult = ProcessServerClients(server, data);
                    if (string.IsNullOrEmpty(clientResult.WhoHtml))
                    {
                        continue; // All clients were filtered out
                    }

                    // Determine server and user locations
                    var (place, usersPlace, serverCountry) = GetServerAndUserLocation(server, clientResult.UserCountries);

                    // Get distance and zone (NOW ASYNC)
                    var (dist, zone) = await CalculateServerDistanceAndZoneAsync(place, usersPlace, server.ip);

                    // --- CULTURAL OVERRIDE ---
                    // Force Mexico and Central America to use the South American UI color ('S')
                    string[] latAmOverrides = { "Mexico", "Guatemala", "Belize", "Honduras", "El Salvador", "Nicaragua", "Costa Rica", "Panama" };
                    if (latAmOverrides.Contains(serverCountry, StringComparer.OrdinalIgnoreCase))
                    {
                        zone = 'S'; 
                    }
                    // -------------------------

                    // Apply distance boost for solo users
                    dist = CalculateBoostedDistance(server, dist, clientResult.FirstUserHash);

                    if (dist < 250) dist = 250;

                    m_allMyServers.Add(new ServersForMe(
                        key, server.ip, server.port, server.name, server.city, serverCountry,
                        dist, zone, clientResult.WhoHtml, server.clients, people, (int)server.maxclients
                    ));
                    totalAdded++;
                }
            }
            Console.WriteLine($"[DEBUG] ProcessServerListsAsync finished. Processed: {totalProcessed}, Added: {totalAdded}");
        }

        /// <summary>
        /// Checks if a server should be skipped based on initial filter rules.
        /// </summary>
        protected bool ShouldSkipServer(JamulusServers server, int people)
        {
            if (server.name.ToLower().Contains("script") ||
                server.city.ToLower().Contains("script") ||
                server.name.ToLower().Contains("jxw") ||
                server.city.ToLower().Contains("peterborough") ||
                server.name.ToLower().Contains("peachjam3"))
            {
                return true;
            }

            if (people < 1)
            {
                return true; // Don't care about empty servers
            }

            return false;
        }

        /// <summary>
        /// Processes all clients on a server, filters them, and builds the 'Who' HTML string.
        /// </summary>
        protected (string WhoHtml, List<string> UserCountries, string FirstUserHash) ProcessServerClients(JamulusServers server, PreloadedData data)
        {
            string who = "";
            List<string> userCountries = new List<string>();
            string firstUserHash = null;

            // --- SORTING LOGIC ---
            // 1. Calculate the hash and duration for every user.
            // 2. Sort DESCENDING: Large numbers (Longest time) -> Small numbers (Newest time).
            var sortedClients = server.clients
                .OrderByDescending(guy =>
                {
                    string h = GetHash(guy.name, guy.country, guy.instrument);
                    double d = DurationHereInMins(server.ip + ":" + server.port, h);

                    // If d is -1 (just spotted this second), treat it as 0 so they are grouped with new arrivals.
                    return (d < 0) ? 0 : d;
                })
                .ThenBy(guy => guy.name) // Secondary sort: Alphabetical for people with same duration
                .ToList();
            // ---------------------

            foreach (var guy in sortedClients) // Loop through our newly sorted list
            {
                if (guy.name.ToLower().Contains("script"))
                {
                    continue;
                }

                string musicianHash = GetHash(guy.name, guy.country, guy.instrument);

                // Check if this client's most recent ASN is blocked
                if (IsClientASNBlocked(musicianHash, data.JoinEventsLines, data.BlockedASNs))
                {
                    continue;
                }

                NotateWhoHere(server.ip + ":" + server.port, musicianHash);

                if (NukeThisUsername(guy.name, guy.instrument, server.name.ToUpper().Contains("CBVB")))
                {
                    continue;
                }

                if (firstUserHash == null)
                {
                    firstUserHash = musicianHash;
                }

                // Build the HTML for this specific client
                // (This method adds the "just-arrived" class to the people at the bottom of this list)
                string clientHtml = BuildClientHtml(guy, server, musicianHash);
                if (string.IsNullOrEmpty(clientHtml))
                {
                    continue;
                }

                who += clientHtml;
                userCountries.Add(guy.country.ToUpper());
            }

            return (who, userCountries, firstUserHash);
        }

        /// <summary>
        /// Determines the server's location and the most common user location.
        /// </summary>
        protected (string Place, string UsersPlace, string ServerCountry) GetServerAndUserLocation(JamulusServers server, List<string> userCountries)
        {
            string place = "";
            string serverCountry = "";
            string usersPlace = "Moon";

            if (server.city.Length > 1)
            {
                place = server.city;
            }
            if (server.country.Length > 1)
            {
                if (place.Length > 1) place += ", ";
                place += server.country;
                serverCountry = server.country;
            }

            if (userCountries.Count > 0)
            {
                var mostCommons = userCountries.GroupBy(x => x)
                                               .OrderByDescending(g => g.Count())
                                               .Select(x => x.Key)
                                               .ToArray();
                string usersCountry = mostCommons[0];

                List<string> cities = new List<string>();
                foreach (var guy in server.clients)
                {
                    if (guy.country.ToUpper() == usersCountry && guy.city.Length > 0)
                    {
                        cities.Add(guy.city.ToUpper());
                    }
                }

                string usersCity = "";
                if (cities.Count > 0)
                {
                    usersCity = cities.GroupBy(x => x)
                                      .OrderByDescending(g => g.Count())
                                      .Select(x => x.Key)
                                      .FirstOrDefault();
                }

                usersPlace = usersCountry;
                if (usersCity.Length > 1)
                {
                    usersPlace = usersCity + ", " + usersCountry;
                }
            }

            if (place.Contains("208, "))
            {
                place = place.Replace("208, ", "");
            }

            return (place, usersPlace, serverCountry);
        }

        /// <summary>
        /// Calculates the distance and time zone for a server.
        /// </summary>
        // 1. Signature changed: no 'out' params, returns a 'Task' with a tuple
        protected async Task<(int dist, char zone)> CalculateServerDistanceAndZoneAsync(string place, string usersPlace, string serverIp)
        {
            // 2. Call the new async method and await its 'LatLong' result
            LatLong location = await _geoService.PlaceToLatLonAsync(place.ToUpper(), usersPlace.ToUpper(), serverIp);

            // 3. Initialize local variables
            int dist = 0;
            char zone = ' ';

            // 4. Use the 'LatLong' object's properties
            if (location != null && (location.lat.Length > 1 || location.lon.Length > 1))
            {
                // This is the changed line
                dist = await _geoService.DistanceFromClientAsync(location.lat, location.lon);

                zone = _geoService.ContinentOfLatLong(location.lat, location.lon);
            }
            
            // 5. Return the tuple result
            return (dist, zone);
        }
        
        /// <summary>
        /// Applies a "boost" to the distance (making it seem closer) if a user is solo.
        /// </summary>
        protected int CalculateBoostedDistance(JamulusServers server, int initialDistance, string firstUserHash)
        {
            if (server.clients.Length != 1 || firstUserHash == null)
            {
                return initialDistance;
            }

            double boost = DurationHereInMins(server.ip + ":" + server.port, firstUserHash);
            if (boost < 3.0)
            {
                boost = 3.0;
            }

            // starts hella close
            return (int)((double)initialDistance * (boost / 6));
        }

        private string BuildClientHtml(Client guy, JamulusServers server, string encodedHashOfGuy)
        {
            // 1. Standard instrument cleanup
            string slimmerInstrument = (guy.instrument == "-") ? "" : guy.instrument;
            if (slimmerInstrument.Length > 0) slimmerInstrument = " " + slimmerInstrument;

            // 2. Standard name cleanup
            var nam = guy.name.Trim().Replace("  ", " ").Replace("<", "");
            if (nam.Length == 0 && (slimmerInstrument == "" || slimmerInstrument == " Streamer")) return null;

            // 3. Determine font size
            string font = "<font size='+0'>";
            if (server.clients.GetLength(0) > 11) font = "<font size='-1'>";
            else
            {
                foreach (var longguy in server.clients)
                    if (longguy.name.Length > 14 && slimmerInstrument.Length > 0) { font = "<font size='-1'>"; break; }
            }

            // 4. --- THE NEW MAGIC --- 
            // Calculate if they have been here less than 3 minutes
            double minsHere = DurationHereInMins(server.ip + ":" + server.port, encodedHashOfGuy);

            // If minsHere is >= 0 (found) and < 3.0, they get the class
            string arrivalClass = (minsHere >= 0 && minsHere < 3.0) ? " just-arrived" : "";
            // ------------------------

            string hash = guy.name + guy.country;

            // 5. Inject the class into the span
            var newpart = "<span class=\"musician " +
                server.ip + " " + encodedHashOfGuy + arrivalClass + "\"" + // <--- injected here
                " id =\"" + hash + "\"" +
                " onmouseover=\"this.style.cursor='pointer'\" onmouseout=\"this.style.cursor='default'\" onclick=\"toggle('" + hash + "')\";>" +
                font +
                "<b>" + nam + "</b>" +
                "<i>" + slimmerInstrument + "</i></font></span>\n";

            if (server.clients.GetLength(0) < 17) newpart += "<br>";
            else if (guy != server.clients[server.clients.GetLength(0) - 1]) newpart += " · ";

            return newpart;
        }

        /// <summary>
        /// Efficiently checks if a client's ASN is on the block list.
        /// </summary>
        private bool IsClientASNBlocked(string musicianHash, string[] joinEvents, HashSet<string> blockedASNs)
        {
            string mostLikelyASN = null;
            const int asnColumnIndex = 12;

            for (int i = joinEvents.Length - 1; i >= 0; i--)
            {
                string candidateLine = joinEvents[i];

                if (!candidateLine.Contains(musicianHash) || string.IsNullOrWhiteSpace(candidateLine))
                {
                    continue;
                }

                string[] fields = candidateLine.Split(',');
                if (fields.Length > asnColumnIndex)
                {
                    string fullAsnField = fields[asnColumnIndex].Trim();
                    if (fullAsnField.StartsWith("AS"))
                    {
                        string asnIdentifier = fullAsnField.Split(' ')[0];
                        if (asnIdentifier.Length > 0)
                        {
                            mostLikelyASN = asnIdentifier;
                            break; // Found it
                        }
                    }
                }
            }

            if (mostLikelyASN != null && blockedASNs.Contains(mostLikelyASN))
            {
                return true;
            }

            return false;
        }
    }
}
