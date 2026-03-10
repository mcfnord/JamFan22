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

        // ── Rate-limit back-off ───────────────────────────────────────────────

        private static DateTime _backoffUntil   = DateTime.MinValue;
        private static int      _currentBackoffMs = 1000;
        private static readonly object _backoffLock = new object();

        public static async Task<string> FetchWithBackoffAsync(HttpClient client, string url, CancellationToken cancellationToken = default)
        {
            lock (_backoffLock)
            {
                if (DateTime.Now < _backoffUntil)
                    throw new Exception($"Rate limit active. Fast-failing request to {url} until {_backoffUntil}.");
            }

            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(url, cancellationToken);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                lock (_backoffLock) { _backoffUntil = DateTime.Now.AddMilliseconds(_currentBackoffMs); _currentBackoffMs *= 2; }
                throw new Exception($"429 Too Many Requests exception for {url}. Backing off.", ex);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                lock (_backoffLock) { _backoffUntil = DateTime.Now.AddMilliseconds(_currentBackoffMs); _currentBackoffMs *= 2; }
                throw new Exception($"429 Too Many Requests for {url}. Backing off.");
            }

            response.EnsureSuccessStatusCode();
            lock (_backoffLock) { _currentBackoffMs = 1000; }
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }

        // ── Country code cache (shared with ApiModel) ─────────────────────────

        public const string WholeMiddotString = " &#xB7; ";

        public static ConcurrentDictionary<string, (string Code, DateTime Expiry)> _countryCodeCache
            = new ConcurrentDictionary<string, (string, DateTime)>();

        // ── IP-API caches ─────────────────────────────────────────────────────

        public static Dictionary<string, JObject> m_ipapiOutputs = new Dictionary<string, JObject>();
        static int m_hourLastFlushed = -1;

        public static Dictionary<string, DateTime> m_ArnOfIpGoodUntil = new Dictionary<string, DateTime>();
        public static Dictionary<string, string>   m_ArnOfIp          = new Dictionary<string, string>();

        // ── IP detail lookup ──────────────────────────────────────────────────

        public static async Task<JObject> GetClientIPDetailsAsync(string clientIP)
        {
            if (DateTime.Now.Hour != m_hourLastFlushed)
            {
                m_ipapiOutputs.Clear();
                m_hourLastFlushed = DateTime.Now.Hour;
            }

            if (m_ipapiOutputs.ContainsKey(clientIP))
                return m_ipapiOutputs[clientIP];

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                Console.WriteLine("1 Requesting: http://ip-api.com/json/" + clientIP);
                string st = await FetchWithBackoffAsync(httpClient, "http://ip-api.com/json/" + clientIP, cts.Token);
                JObject json = JObject.Parse(st);
                m_ipapiOutputs[clientIP] = json;
                return json;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"ip-api.com lookup TIMED OUT for {clientIP}.");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ip-api.com lookup FAILED for {clientIP}: {ex.Message}");
                return null;
            }
        }

        public static async Task<string> AsnOfThisIpAsync(string ip)
        {
        RE_SAMPLE:
            if (!m_ArnOfIpGoodUntil.ContainsKey(ip))
            {
                Console.WriteLine("2 Requesting: http://ip-api.com/json/" + ip);
                string endpoint = "http://ip-api.com/json/" + ip;
                using var client = new HttpClient();

                string st;
                try { st = await FetchWithBackoffAsync(client, endpoint); }
                catch (Exception ex)
                {
                    Console.WriteLine($"ip-api.com ASN lookup failed for {ip}: {ex.Message}");
                    m_ArnOfIp[ip] = null;
                    m_ArnOfIpGoodUntil[ip] = DateTime.Now.AddMinutes(5);
                    return null;
                }

                JObject jsonGeo = JObject.Parse(st);
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
                if (!m_ipapiOutputs.ContainsKey(ipAddress))
                {
                    try
                    {
                        var client = new HttpClient();
                        Console.WriteLine("3 Requesting: http://ip-api.com/json/" + ipAddress);
                        string st = await FetchWithBackoffAsync(client, "http://ip-api.com/json/" + ipAddress);
                        m_ipapiOutputs[ipAddress] = JObject.Parse(st);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"ip-api.com failed: {ex.Message}");
                        return evenSmarterCity;
                    }
                }

                if (m_ipapiOutputs.TryGetValue(ipAddress, out JObject json) && json["city"] != null)
                    return json["city"].ToString();
            }

            return evenSmarterCity;
        }
    }
}
