using JamFan22.Models;
using JamFan22.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace JamFan22.Pages
{
    public partial class IndexModel : PageModel
    {
        private static DateTime _backoffUntil = DateTime.MinValue;
        private static int _currentBackoffMs = 1000;
        private static readonly object _backoffLock = new object();

        private static async Task<string> FetchWithBackoffAsync(HttpClient client, string url, CancellationToken cancellationToken = default)
        {
            lock (_backoffLock)
            {
                if (DateTime.Now < _backoffUntil)
                {
                    throw new Exception($"Rate limit active. Fast-failing request to {url} until {_backoffUntil}.");
                }
            }

            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(url, cancellationToken);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                lock (_backoffLock)
                {
                    _backoffUntil = DateTime.Now.AddMilliseconds(_currentBackoffMs);
                    _currentBackoffMs *= 2;
                }
                throw new Exception($"429 Too Many Requests exception for {url}. Backing off.", ex);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                lock (_backoffLock)
                {
                    _backoffUntil = DateTime.Now.AddMilliseconds(_currentBackoffMs);
                    _currentBackoffMs *= 2;
                }
                throw new Exception($"429 Too Many Requests for {url}. Backing off.");
            }

            response.EnsureSuccessStatusCode();

            lock (_backoffLock)
            {
                _currentBackoffMs = 1000; // Reset on success
            }

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }

        const string WholeMiddotString = " &#xB7; ";

        // This cache makes sure we don't query ip-api.com for the same user twice in 48 hours
        private static System.Collections.Concurrent.ConcurrentDictionary<string, (string Code, DateTime Expiry)> _countryCodeCache 
            = new System.Collections.Concurrent.ConcurrentDictionary<string, (string, DateTime)>();        

        public static Dictionary<string, JObject> m_ipapiOutputs = new Dictionary<string, JObject>();
        static int m_hourLastFlushed = -1;

        public static Dictionary<string, DateTime> m_ArnOfIpGoodUntil = new Dictionary<string, DateTime>();
        public static Dictionary<string, string> m_ArnOfIp = new Dictionary<string, string>();

        static string m_TwoLetterNationCode = "US";

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
                // Use a CancellationTokenSource for the 1-second timeout
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
                {
                    // Use the static shared httpClient and await the non-blocking call
                    Console.WriteLine("1 Requesting: http://ip-api.com/json/" + clientIP);
                    string st = await FetchWithBackoffAsync(httpClient, "http://ip-api.com/json/" + clientIP, cts.Token);
                    JObject json = JObject.Parse(st);
                    m_ipapiOutputs[clientIP] = json;
                    return json;
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"ip-api.com lookup TIMED OUT for {clientIP}.");
                return null; // Return null on timeout
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
            if (false == m_ArnOfIpGoodUntil.ContainsKey(ip))
            {
                Console.WriteLine("2 Requesting: http://ip-api.com/json/" + ip);

                string endpoint = "http://ip-api.com/json/" + ip;
                using var client = new HttpClient();

                string st;
                try
                {
                    // --- FIX: Await the async call instead of blocking ---
                    st = await FetchWithBackoffAsync(client, endpoint);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ip-api.com ASN lookup failed for {ip}: {ex.Message}");
                    m_ArnOfIp[ip] = null;
                    m_ArnOfIpGoodUntil[ip] = DateTime.Now.AddMinutes(5);
                    return null;
                }

                JObject jsonGeo = JObject.Parse(st);
                Random rnd = new Random();
                m_ArnOfIp[ip] = jsonGeo["as"]?.ToString();
                m_ArnOfIpGoodUntil[ip] = DateTime.Now.AddMinutes(rnd.Next(60 * 22, 60 * 26));
                return m_ArnOfIp[ip];
            }
            else
            {
                if (m_ArnOfIpGoodUntil[ip] > DateTime.Now)
                    return m_ArnOfIp[ip];

                // mapping is stale.
                m_ArnOfIpGoodUntil.Remove(ip);
                goto RE_SAMPLE;
            }
        }

        public string SmartCity(string city, Client[] users)
        {
            string smartCity = city;

            if ((smartCity == "") || (smartCity == "yourCity")) // this didn't work: || (smartCity == "-"))
            {
                // Hate these. Estimate a city based on participation.
                List<string> cities = new List<string>();
                List<string> nations = new List<string>();
                foreach (var u in users)
                {
                    cities.Add(u.city);
                    nations.Add(u.country);
                }

                if (cities.Count > 0)
                {
                    var citiGroup = cities.GroupBy(x => x);
                    var maxCountr = citiGroup.Max(g => g.Count());
                    var mostCommonCity = citiGroup.Where(x => x.Count() == maxCountr).Select(x => x.Key).ToArray();
                    if (mostCommonCity.GetLength(0) > 0)
                        smartCity = mostCommonCity[0];

                    // I still fuckin hate blanks. 
                    if ((smartCity == "") || (smartCity == "-"))
                    {
                        var nationGroup = nations.GroupBy(x => x);
                        var maxCountry = nationGroup.Max(g => g.Count());
                        var mostCommonCountry = nationGroup.Where(x => x.Count() == maxCountry).Select(x => x.Key).ToArray();
                        if (mostCommonCountry.GetLength(0) > 0)
                            smartCity = mostCommonCountry[0];
                    }
                }
            }

            if (smartCity == "-")
                smartCity = "";

            string pattern = @"\([^)]*\)";
            string textWithoutParentheses = Regex.Replace(smartCity, pattern, "");


            return textWithoutParentheses;
        }

        public string SmartNations(Client[] whoObject, string servercountry)
        {
            string smartNations = "";
            bool fNeeded = false;

            // if more than 49% of users specify no nation, then there's no smartNation.
            int iCountryless = 0;
            foreach (var who in whoObject)
            {
                if (who.country == "World" || who.country.Length < 2)
                {
                    iCountryless++;
                }
            }

            int itotalpeeps = whoObject.GetLength(0);

            if (((float)iCountryless / (float)itotalpeeps) >= 0.5)
                return "";

            foreach (var who in whoObject)
            {
                if (who.country.Length > 1)
                    if (who.country != servercountry)
                    {
                        fNeeded = true;
                        break;
                    }
            }

            if (fNeeded)
                foreach (var who in whoObject)
                {
                    if (who.country.Length > 1)
                        if (false == smartNations.Contains(who.country))
                            if (who.country != "World") // annoying!
                                smartNations += who.country + WholeMiddotString; // middle dot
                }

            // if contains more middle-dots, represnted here by the &, abbreviate to UK and US
            int totalNations = smartNations.Count(s => s == '&');
            if (totalNations > 2)
            {
                smartNations = smartNations.Replace("United States", "USA");
                smartNations = smartNations.Replace("United Kingdom", "UK");
                smartNations = smartNations.Replace("Hong Kong", "HK");
            }

            // chlop the trailing dot...
            if (smartNations.Length > 0)
                smartNations = smartNations.Substring(0, smartNations.Length - WholeMiddotString.Length);

            // If there are no spaces in any country names,
            // and more than 3 countries,
            // I will replace the space-dot-space with just a space
            // to find this out, replace dots-and-spaces before testing...
            if (totalNations > 3)
            {
                string squishedNations = smartNations.Replace(WholeMiddotString, "");
                if (false == squishedNations.Contains(" "))
                    smartNations = smartNations.Replace(WholeMiddotString, " ");
            }

            // hey, who knows why, but once i saw the city as Canada, with the nationality as Canada.
            // add this code back when i can test it! my Canada duplication scenario disappeared...
            // and i can't reall extract a onesie among many scenario right now. just a total match of one country
            if (smartNations == servercountry)
                return "";

            return smartNations;
        }

        string BackgroundByZone(char zone)
        {
            switch (zone) 
            {
                case 'E':
                    return " style=\"background: #AFFFC6\""; // Europe (Mint Green)

                case 'N':
                    return " style=\"background: #C1F1FF\""; // North America (Light Blue)

                case 'A':
                    // Pushed to a truer, brighter pale yellow (removes the warm/red tint)
                    return " style=\"background: #FFF0AA\""; 

                case 'S':
                    // Shifted completely away from pink into a soft lilac/purple
                    return " style=\"background: #E8D8F8\""; 
            }
            return "";
        }

        private async Task<string> GetSmarterCityAsync(string city, string ipAddress)
        {
            string evenSmarterCity = city.Replace(" Vultr", "");

            if (("AWS" == evenSmarterCity) || ("Linode Cloud" == evenSmarterCity))
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
                        return evenSmarterCity; // return original on failure
                    }
                }

                if (m_ipapiOutputs.TryGetValue(ipAddress, out JObject json) && json["city"] != null)
                {
                    return json["city"].ToString();
                }
            }

            return evenSmarterCity;
        }
    }
}
