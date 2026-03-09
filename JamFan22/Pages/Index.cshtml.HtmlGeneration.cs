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
                    if (clientResult.UserCountries.Count == 0)
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
