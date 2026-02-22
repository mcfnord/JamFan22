using JamFan22.Models;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace JamFan22.Services
{
    public class GeolocationService
    {
        private readonly HttpClient _httpClient;
        private readonly IHttpContextAccessor _httpContextAccessor;

        // Statics preserved for global caching across requests
        public static Dictionary<string, LatLong> m_PlaceNameToLatLong = new Dictionary<string, LatLong>();
        public static Dictionary<string, LatLong> m_ipAddrToLatLong = new Dictionary<string, LatLong>();
        private static Dictionary<string, LatLong> m_openCageCache = new Dictionary<string, LatLong>();
        
        private static DateTime _lastGeolocCacheFlush = DateTime.Now;
        private static DateTime _lastRequestTimestamp = DateTime.Now;
        private static readonly object _flushLock = new object();

        private const string GEOAPIFY_MYSTERY_STRING = "4fc3b2001d984815a8a691e37a28064c"; // Placeholder: Verify correct key

        public GeolocationService(HttpClient httpClient, IHttpContextAccessor httpContextAccessor)
        {
            _httpClient = httpClient;
            _httpContextAccessor = httpContextAccessor;
        }

        public async Task<LatLong> SmartGeoLocateAsync(string ip)
        {
            if (m_ipAddrToLatLong.TryGetValue(ip, out var cached))
                return cached;

            try
            {
                string ip4 = ip.Replace("::ffff:", "");
                string endpoint = "https://api.geoapify.com/v1/ipinfo?ip=" + ip4 + "&apiKey=" + GEOAPIFY_MYSTERY_STRING;

                string s = await _httpClient.GetStringAsync(endpoint);

                JObject jsonGeo = JObject.Parse(s);
                double latitude = Convert.ToDouble(jsonGeo["location"]["latitude"]);
                double longitude = Convert.ToDouble(jsonGeo["location"]["longitude"]);

                var newLocation = new LatLong(latitude.ToString(), longitude.ToString());
                m_ipAddrToLatLong[ip] = newLocation;

                Console.WriteLine("A client IP has been cached: " + ip + " " + jsonGeo["city"] + " " + latitude + " " + longitude);

                return newLocation;
            }
            catch (Exception e)
            {
                Console.WriteLine("Error getting geolocation for " + ip + ": " + e.Message);
                var errorLocation = new LatLong("0", "0");
                m_ipAddrToLatLong[ip] = errorLocation;
                return errorLocation;
            }
        }

        public async Task<int> DistanceFromClientAsync(string lat, string lon)
        {
            var serverLatitude = float.Parse(lat);
            var serverLongitude = float.Parse(lon);

            var context = _httpContextAccessor.HttpContext;
            string clientIP = context.Connection.RemoteIpAddress.ToString();
            
            if ((clientIP.Length < 5) || clientIP.Contains("127.0.0.1") || clientIP.Contains("::1"))
            {
                var xff = (string)context.Request.Headers["X-Forwarded-For"];
                if (!string.IsNullOrEmpty(xff))
                {
                    if (!xff.Contains("::ffff"))
                        xff = "::ffff:" + xff;
                    clientIP = xff;
                }
                else
                {
                    clientIP = "24.18.55.230"; // Default test IP
                }
            }

            LatLong location = await SmartGeoLocateAsync(clientIP);

            double clientLatitude = double.Parse(location.lat);
            double clientLongitude = double.Parse(location.lon);

            return Distance(clientLatitude, clientLongitude, serverLatitude, serverLongitude);
        }

        public int Distance(double lat1, double lon1, double lat2, double lon2)
        {
            const double EquatorialRadiusOfEarth = 6371D;
            const double DegreesToRadians = (Math.PI / 180D);
            var deltalat = (lat2 - lat1) * DegreesToRadians;
            var deltalong = (lon2 - lon1) * DegreesToRadians;
            var a = Math.Pow(Math.Sin(deltalat / 2D), 2D) +
                Math.Cos(lat1 * DegreesToRadians) *
                Math.Cos(lat2 * DegreesToRadians) *
                Math.Pow(Math.Sin(deltalong / 2D), 2D);
            var c = 2D * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1D - a));
            return Convert.ToInt32(EquatorialRadiusOfEarth * c);
        }

        public char ContinentOfLatLong(string lat, string lon)
        {
            double latD = double.Parse(lat);
            double lonD = double.Parse(lon);

            double latNA = 40.0, longNA = -100.0;
            double latEU = 50.0, longEU = 10.0;
            double latAS = 35.0, longAS = 105.0;
            double latSA = -15.0, longSA = -60.0;

            int distFromNA = Distance(latNA, longNA, latD, lonD);
            int distFromEU = Distance(latEU, longEU, latD, lonD);
            int distFromAS = Distance(latAS, longAS, latD, lonD);
            int distFromSA = Distance(latSA, longSA, latD, lonD);

            int minDist = Math.Min(distFromNA, Math.Min(distFromEU, Math.Min(distFromAS, distFromSA)));

            if (minDist == distFromNA) return 'A';
            if (minDist == distFromEU) return 'E';
            if (minDist == distFromAS) return 'S';
            return 'O';
        }

        public async Task<LatLong> PlaceToLatLonAsync(string serverPlace, string userPlace, string ipAddr)
        {
            ipAddr = ipAddr.Trim();
            serverPlace = serverPlace.Trim();
            userPlace = userPlace.Trim();

            var now = DateTime.Now;
            var timeSinceLastRequest = now - _lastRequestTimestamp;
            _lastRequestTimestamp = now;

            if (timeSinceLastRequest.TotalSeconds > 30 && (now - _lastGeolocCacheFlush).TotalHours > 6)
            {
                lock (_flushLock)
                {
                    if ((now - _lastGeolocCacheFlush).TotalHours > 6)
                    {
                        Console.WriteLine($"[Cache Cleanup] Server silent for {timeSinceLastRequest.TotalSeconds:F1}s. Flushing Geolocation Cache.");
                        m_PlaceNameToLatLong.Clear();
                        m_ipAddrToLatLong.Clear();
                        _lastGeolocCacheFlush = now;
                    }
                }
            }

            if (m_PlaceNameToLatLong.TryGetValue(serverPlace.ToUpper(), out var cachedServerPlace)) return cachedServerPlace;
            if (m_PlaceNameToLatLong.TryGetValue(userPlace.ToUpper(), out var cachedUserPlace)) return cachedUserPlace;
            if (m_ipAddrToLatLong.TryGetValue(ipAddr, out var cachedIp)) return cachedIp;

            bool fServerLLSuccess = false;
            string serverLat = "", serverLon = "";

            if (serverPlace.Length > 1 && serverPlace != "yourCity")
            {
                var result = await CallOpenCageAsync(serverPlace);
                if (result.Success)
                {
                    serverLat = result.Lat;
                    serverLon = result.Lon;
                    fServerLLSuccess = true;
                }
            }

            bool fUserLLSuccess = false;
            string userLat = "", userLon = "";
            var userResult = await CallOpenCageAsync(userPlace);
            if (userResult.Success)
            {
                userLat = userResult.Lat;
                userLon = userResult.Lon;
                fUserLLSuccess = true;
            }

            bool fServerIPLLSuccess = false;
            string serverIPLat = "", serverIPLon = "";

            if (ipAddr.Length > 5)
            {
                try
                {
                    string ip4Addr = ipAddr.Replace("::ffff:", "");
                    string endpoint = $"https://api.geoapify.com/v1/ipinfo?ip={ip4Addr}&apiKey={GEOAPIFY_MYSTERY_STRING}";
                    
                    using (var cts = new System.Threading.CancellationTokenSource(2000))
                    {
                        string s = await _httpClient.GetStringAsync(endpoint, cts.Token);
                        JObject jsonGeo = JObject.Parse(s);
                        serverIPLat = (string)jsonGeo["location"]["latitude"];
                        serverIPLon = (string)jsonGeo["location"]["longitude"];
                        fServerIPLLSuccess = true;
                        m_ipAddrToLatLong[ipAddr] = new LatLong(serverIPLat, serverIPLon);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error fetching IP geo for {ipAddr}: {ex.Message}");
                    m_ipAddrToLatLong[ipAddr] = new LatLong("", ""); 
                }
            }
            else
            {
                m_ipAddrToLatLong[ipAddr] = new LatLong("", "");
            }

            if (fServerIPLLSuccess)
            {
                if (fUserLLSuccess)
                {
                    char serverIPContinent = ContinentOfLatLong(serverIPLat, serverIPLon);
                    char userContinent = ContinentOfLatLong(userLat, userLon);
                    if (serverIPContinent == userContinent)
                    {
                        var loc = new LatLong(userLat, userLon);
                        m_PlaceNameToLatLong[serverPlace.ToUpper()] = loc;
                        return loc;
                    }
                }
                if (!fServerLLSuccess) return m_ipAddrToLatLong[ipAddr];
            }

            if (string.IsNullOrEmpty(serverLat) || string.IsNullOrEmpty(serverLon)) return new LatLong("0", "0");

            var serverLocation = new LatLong(serverLat, serverLon);
            m_PlaceNameToLatLong[serverPlace.ToUpper()] = serverLocation;
            return serverLocation;
        }

        public async Task<(bool Success, string Lat, string Lon)> CallOpenCageAsync(string placeName)
        {
            if (placeName.Length < 3 || placeName == "MOON" || !Regex.IsMatch(placeName, "[a-zA-Z]"))
                return (false, null, null);

            try
            {
                string encodedplace = System.Web.HttpUtility.UrlEncode(placeName);
                string endpoint = string.Format("https://api.opencagedata.com/geocode/v1/json?q={0}&key=4fc3b2001d984815a8a691e37a28064c", encodedplace);

                string s = await _httpClient.GetStringAsync(endpoint);

                JObject latLongJson = JObject.Parse(s);
                if (latLongJson["results"].HasValues)
                {
                    string typeOfMatch = (string)latLongJson["results"][0]["components"]["_type"];
                    string[] validTypes = { "neighbourhood", "village", "city", "county", "municipality", "administrative", "state", "boundary", "country" };
                    if (Array.IndexOf(validTypes, typeOfMatch) >= 0)
                    {
                        string lat = (string)latLongJson["results"][0]["geometry"]["lat"];
                        string lon = (string)latLongJson["results"][0]["geometry"]["lng"];
                        return (true, lat, lon);
                    }
                }
                return (false, null, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error calling OpenCage for {placeName}: {ex.Message}");
                return (false, null, null);
            }
        }

        public async Task<(bool Success, string Lat, string Lon)> CallOpenCageCachedAsync(string placeName)
        {
            if (m_openCageCache.TryGetValue(placeName, out var cached))
            {
                return cached == null ? (false, null, null) : (true, cached.lat, cached.lon);
            }

            var (success, lat, lon) = await CallOpenCageAsync(placeName);

            if (success)
            {
                m_openCageCache[placeName] = new LatLong(lat, lon);
                return (true, lat, lon);
            }

            m_openCageCache[placeName] = null;
            return (false, null, null);
        }
    }
}
