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
        private static DateTime _lastGeolocCacheFlush = DateTime.Now;
        private static DateTime _lastRequestTimestamp = DateTime.Now;
        private static readonly object _flushLock = new object();

public GeolocationService(HttpClient httpClient, IHttpContextAccessor httpContextAccessor)
        {
            _httpClient = httpClient;
            _httpContextAccessor = httpContextAccessor;
        }

        private async Task<LatLong?> GetIpApiLatLonAsync(string ip)
        {
            if (string.IsNullOrEmpty(ip) || ip.Length < 5) return null;

            var json = await Services.IpAnalyticsService.FetchIpApiAsync(ip);
            if (json == null) return null;
            return new LatLong(json["lat"].ToString(), json["lon"].ToString());
        }

        public async Task<string> GetCityFromIpAsync(string ip)
        {
            var json = await Services.IpAnalyticsService.FetchIpApiAsync(ip);
            return json?["city"]?.ToString() ?? "";
        }

        public async Task<LatLong> SmartGeoLocateAsync(string ip)
        {
            if (m_ipAddrToLatLong.TryGetValue(ip, out var cached))
                return cached;

            var loc = await GetIpApiLatLonAsync(ip);
            if (loc != null) m_ipAddrToLatLong[ip] = loc;
            return loc;
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

            LatLong? location = await SmartGeoLocateAsync(clientIP);
            if (location == null) return 0;

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

            if (minDist == distFromNA) return 'N'; // Changed to 'N' for North America
            if (minDist == distFromEU) return 'E';
            if (minDist == distFromAS) return 'A'; // Changed to 'A' for Asia
            if (minDist == distFromSA) return 'S'; // Added 'S' for South America
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
            // Removed the userPlace fallback that was spoofing server locations
            if (m_ipAddrToLatLong.TryGetValue(ipAddr, out var cachedIp)) return cachedIp;

            LatLong loc = await GetIpApiLatLonAsync(ipAddr);
            if (loc != null) m_ipAddrToLatLong[ipAddr] = loc;
            return loc;
        }

    }
}
