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

        private static readonly string GEOAPIFY_MYSTERY_STRING = System.IO.File.Exists("secretGeoApifykey.txt") ? System.IO.File.ReadAllText("secretGeoApifykey.txt").Trim() : "";

        public GeolocationService(HttpClient httpClient, IHttpContextAccessor httpContextAccessor)
        {
            _httpClient = httpClient;
            _httpContextAccessor = httpContextAccessor;
        }

        private static readonly System.Threading.SemaphoreSlim _ipApiSemaphore = new System.Threading.SemaphoreSlim(1, 1);
        private static DateTime _lastIpApiCall = DateTime.MinValue;
        public static Dictionary<string, (LatLong Location, DateTime Expiry)> m_ipApiCache48 = new Dictionary<string, (LatLong, DateTime)>();

        private async Task<LatLong> GetIpApiLatLonAsync(string ip)
        {
            if (string.IsNullOrEmpty(ip) || ip.Length < 5) return new LatLong("0", "0");
            
            string ip4 = ip.Replace("::ffff:", "");
            
            if (m_ipApiCache48.TryGetValue(ip4, out var cached))
            {
                if (DateTime.Now < cached.Expiry)
                    return cached.Location;
            }

            await _ipApiSemaphore.WaitAsync();
            try
            {
                if (m_ipApiCache48.TryGetValue(ip4, out var cachedDoubleCheck))
                {
                    if (DateTime.Now < cachedDoubleCheck.Expiry)
                        return cachedDoubleCheck.Location;
                }

                var timeSinceLast = DateTime.Now - _lastIpApiCall;
                if (timeSinceLast.TotalSeconds < 1.5)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1.5) - timeSinceLast);
                }

                string endpoint = "http://ip-api.com/json/" + ip4;
                _lastIpApiCall = DateTime.Now;

                using (var cts = new System.Threading.CancellationTokenSource(2000))
                {
                    string s = await _httpClient.GetStringAsync(endpoint, cts.Token);
                    JObject jsonGeo = JObject.Parse(s);
                    if (jsonGeo["status"]?.ToString() == "success")
                    {
                        var loc = new LatLong(jsonGeo["lat"].ToString(), jsonGeo["lon"].ToString());
                        m_ipApiCache48[ip4] = (loc, DateTime.Now.AddHours(48));
                        return loc;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching IP geo for {ip}: {ex.Message}");
            }
            finally
            {
                _ipApiSemaphore.Release();
            }
            
            return new LatLong("0", "0");
        }

        public async Task<LatLong> SmartGeoLocateAsync(string ip)
        {
            if (m_ipAddrToLatLong.TryGetValue(ip, out var cached))
                return cached;

            var loc = await GetIpApiLatLonAsync(ip);
            
            if (loc.lat != "0" || loc.lon != "0") 
            {
                m_ipAddrToLatLong[ip] = loc;
                return loc;
            }
            
            var errorLocation = new LatLong("0", "0");
            m_ipAddrToLatLong[ip] = errorLocation;
            return errorLocation;
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

            // Entirely rely on ip-api.com for server location, ignoring stated city.
            LatLong loc = await GetIpApiLatLonAsync(ipAddr);
            m_ipAddrToLatLong[ipAddr] = loc;
            
            return loc;
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
