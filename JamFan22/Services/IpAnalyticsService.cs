using JamFan22.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace JamFan22.Services
{
    public class IpAnalyticsService
    {
        // ── Shared HTTP client ────────────────────────────────────────────────

        private static readonly HttpClient httpClient = new HttpClient();

        // ── Unified ip-api.com gate (shared by all callers) ──────────────────

        // ── Swap this one line to redirect all geolocation traffic to a new provider ──
        private static string _geoEndpoint = "http://ip-api.com/json/";

        private static readonly object _ipApiLock = new object();
        private static DateTime _ipApiNextAllowed = DateTime.MinValue;
        private static int _ipApiBackoffSeconds = 1;
        private static readonly ConcurrentDictionary<string, (JObject Json, DateTime Expiry)> _ipApiCache
            = new ConcurrentDictionary<string, (JObject, DateTime)>();

        /// <summary>
        /// Single shared gate for all ip-api.com lookups. Returns null if throttled or on error.
        /// Results are cached for 48 hours — callers never need their own ip-api caches.
        /// </summary>
        public static async Task<JObject> FetchIpApiAsync(string ip, CancellationToken ct = default, bool waitIfThrottled = false)
        {
            ip = ip.Replace("::ffff:", "");

            if (_ipApiCache.TryGetValue(ip, out var cached) && DateTime.UtcNow < cached.Expiry)
                return cached.Json;

            TimeSpan throttleWait;
            lock (_ipApiLock)
            {
                var remaining = _ipApiNextAllowed - DateTime.UtcNow;
                if (remaining > TimeSpan.Zero)
                {
                    if (!waitIfThrottled)
                    {
                        Console.WriteLine($"[ip-api] THROTTLED: {ip}, retry after {remaining.TotalSeconds:F0}s");
                        return null;
                    }
                    throttleWait = remaining;
                }
                else
                {
                    throttleWait = TimeSpan.Zero;
                    _ipApiNextAllowed = DateTime.UtcNow.AddSeconds(_ipApiBackoffSeconds);
                }
            }

            if (throttleWait > TimeSpan.Zero)
            {
                Console.WriteLine($"[ip-api] DEFENSE WAIT: {ip}, delaying {throttleWait.TotalSeconds:F1}s");
                await Task.Delay(throttleWait, ct);
                return await FetchIpApiAsync(ip, ct, waitIfThrottled);
            }

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromSeconds(3));

                var response = await httpClient.GetAsync($"{_geoEndpoint}{ip}", linked.Token);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    lock (_ipApiLock) { _ipApiBackoffSeconds *= 2; _ipApiNextAllowed = DateTime.UtcNow.AddSeconds(_ipApiBackoffSeconds); }
                    Console.WriteLine($"[ip-api] 429 for {ip}, backoff now {_ipApiBackoffSeconds}s");
                    return null;
                }

                response.EnsureSuccessStatusCode();
                string body = await response.Content.ReadAsStringAsync(ct);
                var json = JObject.Parse(body);

                if (json["status"]?.ToString() != "success")
                {
                    lock (_ipApiLock) { _ipApiBackoffSeconds *= 2; _ipApiNextAllowed = DateTime.UtcNow.AddSeconds(_ipApiBackoffSeconds); }
                    Console.WriteLine($"[ip-api] non-success for {ip}: {json["message"]}, backoff now {_ipApiBackoffSeconds}s");
                    return null;
                }

                lock (_ipApiLock) { _ipApiBackoffSeconds = 1; _ipApiNextAllowed = DateTime.UtcNow.AddSeconds(1); }
                _ipApiCache[ip] = (json, DateTime.UtcNow.AddHours(48));
                Console.WriteLine($"[ip-api] success for {ip}: {json["city"]}, {json["countryCode"]}");
                return json;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[ip-api] timeout for {ip}");
                return null;
            }
            catch (Exception ex)
            {
                lock (_ipApiLock) { _ipApiBackoffSeconds *= 2; _ipApiNextAllowed = DateTime.UtcNow.AddSeconds(_ipApiBackoffSeconds); }
                Console.WriteLine($"[ip-api] error for {ip}: {ex.Message}, backoff now {_ipApiBackoffSeconds}s");
                return null;
            }
        }

        // ── Country code cache (shared with ApiModel) ─────────────────────────

        public const string WholeMiddotString = " &#xB7; ";

        public static ConcurrentDictionary<string, (string Code, DateTime Expiry)> _countryCodeCache
            = new ConcurrentDictionary<string, (string, DateTime)>();

        // ── Block list cache ──────────────────────────────────────────────────

        private static (string[] Lines, DateTime Expiry, string Hash) _blockListCache = (Array.Empty<string>(), DateTime.MinValue, "");
        private static readonly SemaphoreSlim _blockListSemaphore = new SemaphoreSlim(1, 1);

        // ── Per-IP allowed cache (48h TTL) ────────────────────────────────────

        private static readonly ConcurrentDictionary<string, (bool Blocked, DateTime Expiry)> _ipAllowedCache
            = new ConcurrentDictionary<string, (bool, DateTime)>();

        public static async Task<bool> IsIpAllowedAsync(string ip, CancellationToken ct = default)
        {
            string cleanIp = ip.Replace("::ffff:", "");
            if (_ipAllowedCache.TryGetValue(cleanIp, out var cached) && DateTime.UtcNow < cached.Expiry)
                return !cached.Blocked;
            bool blocked = await IsIpBlockedAsync(ip, ct);
            _ipAllowedCache[cleanIp] = (blocked, DateTime.UtcNow.AddHours(48));
            return !blocked;
        }

        public static async Task<bool> IsIpBlockedAsync(string ip, CancellationToken ct = default)
        {
            var ipData = await FetchIpApiAsync(ip, ct, waitIfThrottled: true);
            if (ipData == null) return false;

            string rawAsn   = ipData["as"]?.ToString() ?? "";
            string shortAsn = rawAsn.Split(' ')[0];
            string cleanIp  = ip.Replace("::ffff:", "");

            string[] lines = _blockListCache.Lines;
            if (DateTime.UtcNow >= _blockListCache.Expiry)
            {
                await _blockListSemaphore.WaitAsync(ct);
                try
                {
                    if (DateTime.UtcNow >= _blockListCache.Expiry)
                    {
                        lines = File.Exists("wwwroot/asn-ip-client-blocks.txt")
                            ? await File.ReadAllLinesAsync("wwwroot/asn-ip-client-blocks.txt", ct)
                            : Array.Empty<string>();
                        string newHash = string.Join('\n', lines).GetHashCode().ToString();
                        if (newHash != _blockListCache.Hash)
                        {
                            foreach (var key in _ipAllowedCache.Keys.ToList())
                                if (_ipAllowedCache.TryGetValue(key, out var entry) && !entry.Blocked)
                                    _ipAllowedCache.TryRemove(key, out _);
                            Console.WriteLine("[blocklist] content changed — evicted allowed-IP cache entries");
                        }
                        _blockListCache = (lines, DateTime.UtcNow.AddMinutes(1), newHash);
                    }
                    else { lines = _blockListCache.Lines; }
                }
                finally { _blockListSemaphore.Release(); }
            }

            if (!System.Net.IPAddress.TryParse(cleanIp, out var ipAddr)) return false;

            var exceptions   = new List<System.Net.IPNetwork>();
            var blockedAsns  = new HashSet<string>();
            var blockedCidrs = new List<System.Net.IPNetwork>();

            foreach (var line in lines)
            {
                string t = line.Trim();
                if (string.IsNullOrEmpty(t)) continue;
                if (t.StartsWith("!"))
                { if (System.Net.IPNetwork.TryParse(t.Substring(1), out var net)) exceptions.Add(net); }
                else if (t.StartsWith("AS"))
                    blockedAsns.Add(t.Split(' ')[0]);
                else if (System.Net.IPNetwork.TryParse(t, out var net))
                    blockedCidrs.Add(net);
            }

            foreach (var ex in exceptions)
                if (ex.Contains(ipAddr)) return false;

            if (shortAsn.Length > 0 && blockedAsns.Contains(shortAsn)) return true;

            foreach (var cidr in blockedCidrs)
                if (cidr.Contains(ipAddr)) return true;

            return false;
        }

        // ── ASN cache ─────────────────────────────────────────────────────────

        public static Dictionary<string, DateTime> m_ArnOfIpGoodUntil = new Dictionary<string, DateTime>();
        public static Dictionary<string, string>   m_ArnOfIp          = new Dictionary<string, string>();

        // ── IP detail lookup ──────────────────────────────────────────────────

        public static async Task<JObject> GetClientIPDetailsAsync(string clientIP)
            => await FetchIpApiAsync(clientIP);

        public static async Task<string> AsnOfThisIpAsync(string ip)
        {
        RE_SAMPLE:
            if (!m_ArnOfIpGoodUntil.ContainsKey(ip))
            {
                var jsonGeo = await FetchIpApiAsync(ip);
                if (jsonGeo == null)
                {
                    m_ArnOfIp[ip] = null;
                    m_ArnOfIpGoodUntil[ip] = DateTime.Now.AddMinutes(5);
                    return null;
                }
                var rnd = new Random();
                m_ArnOfIp[ip] = jsonGeo["as"]?.ToString();
                m_ArnOfIpGoodUntil[ip] = DateTime.Now.AddMinutes(rnd.Next(60 * 22, 60 * 26));
                return m_ArnOfIp[ip];
            }
            else
            {
                if (m_ArnOfIpGoodUntil[ip] > DateTime.Now)
                    return m_ArnOfIp[ip];
                m_ArnOfIpGoodUntil.Remove(ip);
                goto RE_SAMPLE;
            }
        }

        // ── Nation string builder ─────────────────────────────────────────────

        public string SmartNations(Client[] whoObject, string servercountry)
        {
            string smartNations = "";
            bool fNeeded = false;

            int iCountryless = 0;
            foreach (var who in whoObject)
                if (who.country == "World" || who.country.Length < 2) iCountryless++;

            if (((float)iCountryless / (float)whoObject.GetLength(0)) >= 0.5) return "";

            foreach (var who in whoObject)
                if (who.country.Length > 1 && who.country != servercountry) { fNeeded = true; break; }

            if (fNeeded)
                foreach (var who in whoObject)
                    if (who.country.Length > 1 && !smartNations.Contains(who.country) && who.country != "World")
                        smartNations += who.country + WholeMiddotString;

            int totalNations = smartNations.Count(s => s == '&');
            if (totalNations > 2)
            {
                smartNations = smartNations.Replace("United States", "USA");
                smartNations = smartNations.Replace("United Kingdom", "UK");
                smartNations = smartNations.Replace("Hong Kong", "HK");
            }

            if (smartNations.Length > 0)
                smartNations = smartNations.Substring(0, smartNations.Length - WholeMiddotString.Length);

            if (totalNations > 3)
            {
                string squishedNations = smartNations.Replace(WholeMiddotString, "");
                if (!squishedNations.Contains(" "))
                    smartNations = smartNations.Replace(WholeMiddotString, " ");
            }

            if (smartNations == servercountry) return "";
            return smartNations;
        }

        // ── City name cleanup ─────────────────────────────────────────────────

        public async Task<string> GetSmarterCityAsync(string city, string ipAddress)
        {
            string evenSmarterCity = city.Replace(" Vultr", "");

            if (evenSmarterCity == "AWS" || evenSmarterCity == "Linode Cloud")
            {
                var json = await FetchIpApiAsync(ipAddress);
                if (json?["city"] != null)
                    return json["city"].ToString();
            }

            return evenSmarterCity;
        }
    }
}
