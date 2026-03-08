using JamFan22.Models;
using JamFan22.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
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
        public static Dictionary<string, string> JamulusListURLs = new Dictionary<string, string>()
        {
            {"Any Genre 1", "http://24.199.107.192:5001/servers_data/anygenre1.jamulus.io:22124/cached_data" },
            {"Any Genre 2", "http://24.199.107.192:5001/servers_data/anygenre2.jamulus.io:22224/cached_data" },
            {"Any Genre 3", "http://24.199.107.192:5001/servers_data/anygenre3.jamulus.io:22624/cached_data" },
            {"Genre Rock",  "http://24.199.107.192:5001/servers_data/rock.jamulus.io:22424/cached_data" },
            {"Genre Jazz",  "http://24.199.107.192:5001/servers_data/jazz.jamulus.io:22324/cached_data" },
            {"Genre Classical/Folk",  "http://24.199.107.192:5001/servers_data/classical.jamulus.io:22524/cached_data" },
            {"Genre Choral/BBShop",  "http://24.199.107.192:5001/servers_data/choral.jamulus.io:22724/cached_data" }
        };

        static Dictionary<string, List<JamulusServers>> m_deserializedCache = new Dictionary<string, List<JamulusServers>>();
        static Dictionary<string, string> m_jsonCacheSource = new Dictionary<string, string>();
        // Tracks the exact timestamp each directory was last proven to be freshly scraped
        public static Dictionary<string, DateTime> DirectoryLastUpdated = new Dictionary<string, DateTime>();
        public static Dictionary<string, string> LastReportedList = new Dictionary<string, string>();
        static DateTime? LastReportedListGatheredAt = null;
        public static List<string> ListServicesOffline = new List<string>();

        protected static SemaphoreSlim m_serializerMutex = new SemaphoreSlim(1, 1);

        private static readonly HttpClient s_refreshClient = new HttpClient(
            new HttpClientHandler 
            { 
                ServerCertificateCustomValidationCallback = (message, cert, chain, ssl) => true 
            }
        );

        public static int MinutesSince2023AsInt()
        {
            var now = DateTime.UtcNow;
            var then = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            TimeSpan span = now - then;
            var diff = span.TotalMinutes;
            int mins = (int)(diff);
            return mins;
        }

        public static string MinutesSince2023()
        {
            var mins = MinutesSince2023AsInt();
            return mins.ToString("D7");
        }

        public static async Task<List<string>> LoadLinesFromHttpTextFile(string url)
        {
            using (HttpClient client = new HttpClient())
            {
                try
                {
                    HttpResponseMessage response = await client.GetAsync(url);

                    if (response.IsSuccessStatusCode)
                    {
                        List<string> lines = new List<string>();
                        using (HttpContent content = response.Content)
                        {
                            // Read the content as a stream of lines
                            using (System.IO.Stream stream = await content.ReadAsStreamAsync())
                            using (System.IO.StreamReader reader = new System.IO.StreamReader(stream))
                            {
                                string line;
                                while ((line = await reader.ReadLineAsync()) != null)
                                {
                                    lines.Add(line);
                                }
                            }
                        }
                        return lines;
                    }
                    else
                    {
                        Console.WriteLine("Failed to retrieve the file. Status code: " + response.StatusCode);
                    }
                }
                catch (HttpRequestException e)
                {
                    Console.WriteLine("HttpRequestException: " + e.Message);
                }
            }

            return new List<string>(); // Return an empty list if there's an error
        }

        public static async Task RefreshThreadTask(CancellationToken stoppingToken)
        {
            // --- LOCAL HELPER: Generates "livestatus.json" ---
            async Task GenerateLiveStatusJsonAsync(Dictionary<string, Task<string>> serverStates)
            {
                // Structure: Key = IP:Port, Value = Object { name, clients[] }
                var liveStatus = new Dictionary<string, object>();

                foreach (var kvp in serverStates)
                {
                    try
                    {
                        string json = await kvp.Value;

                        // Unwrap Python API wrapper if present
                        if (json.TrimStart().StartsWith("{"))
                        {
                            try
                            {
                                using (JsonDocument doc = JsonDocument.Parse(json))
                                {
                                    if (doc.RootElement.TryGetProperty("servers_data", out var serversData))
                                    {
                                        json = serversData.GetRawText();
                                    }
                                }
                            }
                            catch { }
                        }

                        var servers = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(json);
                        if (servers == null) continue;

                        foreach (var server in servers)
                        {
                            if (server.clients == null || server.clients.Length == 0) continue;

                            string serverKey = $"{server.ip}:{server.port}";
                            var guids = new List<string>();

                            foreach (var client in server.clients)
                            {
                                // Calculate GUID hash
                                string hash = GetHash(client.name, client.country, client.instrument);
                                guids.Add(hash);
                            }

                            if (guids.Count > 0)
                            {
                                // STRICT SCHEMA: Only Name and GUIDs
                                liveStatus[serverKey] = new
                                {
                                    name = server.name,
                                    clients = guids
                                };
                            }
                        }
                    }
                    catch
                    {
                        // Ignore individual list failures
                    }
                }

                try
                {
                    // Overwrite the file atomically
                    string jsonOutput = System.Text.Json.JsonSerializer.Serialize(liveStatus);
                    await System.IO.File.WriteAllTextAsync("wwwroot/livestatus.json", jsonOutput, stoppingToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to write livestatus.json: {ex.Message}");
                }
            }
            // -------------------------------------------------

            while (!stoppingToken.IsCancellationRequested)
            {
                bool fMissingSamplePresent = false;

                try
                {
                    var serverStates = new Dictionary<string, Task<string>>();

                    // 1. Start all downloads in parallel
                    foreach (var key in JamulusListURLs.Keys)
                    {
                        serverStates.Add(key, s_refreshClient.GetStringAsync(JamulusListURLs[key], stoppingToken));
                    }

                    // 2. Wait for all downloads to complete
                    await Task.WhenAll(serverStates.Values);

                    // 3. GENERATE THE LIVE STATUS FILE IMMEDIATELY
                    await GenerateLiveStatusJsonAsync(serverStates);

                    // 4. Process data for internal lists (Legacy Logic)
                    DateTime query_started = DateTime.Now;
                    foreach (var key in JamulusListURLs.Keys)
                    {
                        string newReportedList = null;
                        try
                        {
                            newReportedList = await serverStates[key];

                            // Handle Python API Wrapper & Extract Timestamp
                            if (newReportedList.TrimStart().StartsWith("{"))
                            {
                                try
                                {
                                    using (JsonDocument doc = JsonDocument.Parse(newReportedList))
                                    {
                                        if (doc.RootElement.TryGetProperty("servers_data", out var serversData))
                                        {
                                            newReportedList = serversData.GetRawText();
                                        }
                                        
                                        // Extract upstream timestamp if available
                                        if (doc.RootElement.TryGetProperty("timestamp", out var ts))
                                        {
                                            DirectoryLastUpdated[key] = DateTimeOffset.FromUnixTimeSeconds((long)ts.GetDouble()).UtcDateTime.ToLocalTime();
                                        }
                                    }
                                }
                                catch { }
                            }

                            if (newReportedList[0] == 'C')
                            {
                                Console.WriteLine("Indication of data failure: " + newReportedList);
                                await Task.Delay(1000, stoppingToken);
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Exception handling {key}: {ex.Message}");
                            await Task.Delay(1000, stoppingToken);
                            break; 
                        }

                        if (newReportedList[0] != 'C')
                        {
                            await m_serializerMutex.WaitAsync(stoppingToken);
                            try
                            {
                                if (LastReportedList.ContainsKey(key) && LastReportedList[key] == newReportedList)
                                {
                                    // Data unchanged. Do not update DirectoryLastUpdated unless upstream timestamp was explicitly caught above.
                                }
                                else
                                {
                                    // Data physically changed. Update master list and record freshness.
                                    if (LastReportedList.ContainsKey(key))
                                    {
                                        DetectJoiners(LastReportedList[key], newReportedList);
                                    }
                                    LastReportedList[key] = newReportedList;
                                    
                                    DirectoryLastUpdated[key] = DateTime.Now; 
                                }
                            }
                            finally
                            {
                                m_serializerMutex.Release();
                            }
                        }
                    } 

                    await m_serializerMutex.WaitAsync(stoppingToken);
                    try
                    {
                        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                        TimeSpan durationBetweenSamples = (null != LastReportedListGatheredAt)
                            ? DateTime.Now.Subtract((DateTime)LastReportedListGatheredAt)
                            : new TimeSpan();

                        LastReportedListGatheredAt = DateTime.Now;

                        ListServicesOffline.Clear();
                        foreach (var keyHere in JamulusListURLs.Keys)
                        {
                            var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(LastReportedList[keyHere]);
                            if (serversOnList.Count == 0)
                            {
                                ListServicesOffline.Add(keyHere);
                                fMissingSamplePresent = true;
                            }
                        }

                        HashSet<string> alreadyPushed = new HashSet<string>();

                        StringBuilder serverCsvBuilder = new StringBuilder();
                        StringBuilder censusCsvBuilder = new StringBuilder();
                        StringBuilder censusGeoCsvBuilder = new StringBuilder();

                        foreach (var key in JamulusListURLs.Keys)
                        {
                            string currentJson = LastReportedList[key];
                            List<JamulusServers> serversOnList = null;

                            if (m_jsonCacheSource.TryGetValue(key, out var cachedJson) && cachedJson == currentJson)
                            {
                                serversOnList = m_deserializedCache[key];
                            }
                            else
                            {
                                serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(currentJson);
                                m_deserializedCache[key] = serversOnList;
                                m_jsonCacheSource[key] = currentJson;
                            }

                            foreach (var server in serversOnList)
                            {
                                int people = server.clients?.Length ?? 0;
                                if (people < 1) continue;

                                serverCsvBuilder.Append(server.ip + ":" + server.port + ","
                                    + System.Web.HttpUtility.UrlEncode(server.name) + ","
                                    + System.Web.HttpUtility.UrlEncode(server.city) + ","
                                    + System.Web.HttpUtility.UrlEncode(server.country)
                                    + Environment.NewLine);

                                foreach (var guy in server.clients)
                                {
                                    string stringHashOfGuy = GetHash(guy.name, guy.country, guy.instrument);

                                    censusCsvBuilder.Append(MinutesSince2023() + ","
                                        + stringHashOfGuy + ","
                                        + server.ip + ":" + server.port
                                        + Environment.NewLine);

                                    censusGeoCsvBuilder.Append(stringHashOfGuy + ","
                                        + System.Web.HttpUtility.UrlEncode(guy.name) + ","
                                        + guy.instrument + ","
                                        + System.Web.HttpUtility.UrlEncode(guy.city) + ","
                                        + System.Web.HttpUtility.UrlEncode(guy.country)
                                        + Environment.NewLine);

                                    if (false == m_userServerViewTracker.ContainsKey(stringHashOfGuy))
                                        m_userServerViewTracker[stringHashOfGuy] = new HashSet<string>();
                                    m_userServerViewTracker[stringHashOfGuy].Add(server.ip + ":" + server.port);

                                    if (false == m_userConnectDuration.ContainsKey(stringHashOfGuy))
                                        m_userConnectDuration[stringHashOfGuy] = new TimeSpan();
                                    m_userConnectDuration[stringHashOfGuy] =
                                        m_userConnectDuration[stringHashOfGuy].Add(durationBetweenSamples.Divide(server.clients.Count()));

                                    if (false == m_userConnectDurationPerServer.ContainsKey(stringHashOfGuy))
                                        m_userConnectDurationPerServer[stringHashOfGuy] = new Dictionary<string, TimeSpan>();
                                    var fullIP = server.ip + ":" + server.port;
                                    var theGuyServer = m_userConnectDurationPerServer[stringHashOfGuy];
                                    if (false == theGuyServer.ContainsKey(fullIP))
                                        theGuyServer.Add(fullIP, TimeSpan.Zero);
                                    theGuyServer[fullIP] = theGuyServer[fullIP].Add(durationBetweenSamples.Divide(server.clients.Count()));

                                    foreach (var otherguy in server.clients)
                                    {
                                        if (false == m_userConnectDurationPerUser.ContainsKey(stringHashOfGuy))
                                            m_userConnectDurationPerUser[stringHashOfGuy] = new Dictionary<string, TimeSpan>();
                                        if (otherguy == guy) continue;

                                        string stringHashOfOtherGuy = GetHash(otherguy.name, otherguy.country, otherguy.instrument);
                                        var theGuyUser = m_userConnectDurationPerUser[stringHashOfGuy];
                                        if (false == theGuyUser.ContainsKey(stringHashOfOtherGuy))
                                            theGuyUser.Add(stringHashOfOtherGuy, TimeSpan.Zero);
                                        theGuyUser[stringHashOfOtherGuy] = theGuyUser[stringHashOfOtherGuy].Add(durationBetweenSamples.Divide(server.clients.Count()));

                                        string us = CanonicalTwoHashes(stringHashOfGuy, stringHashOfOtherGuy);
                                        if (false == m_everywhereWeHaveMet.ContainsKey(us))
                                            m_everywhereWeHaveMet[us] = new HashSet<string>();
                                        m_everywhereWeHaveMet[us].Add(server.ip + ":" + server.port);

                                        if (durationBetweenSamples.TotalSeconds > 0)
                                        {
                                            if (stringHashOfGuy != stringHashOfOtherGuy)
                                            {
                                                if (false == alreadyPushed.Contains(us))
                                                {
                                                    alreadyPushed.Add(us);
                                                    ReportPairTogether(us, durationBetweenSamples);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        if (serverCsvBuilder.Length > 0)
                            await System.IO.File.AppendAllTextAsync("data/server.csv", serverCsvBuilder.ToString(), stoppingToken);
                        if (censusCsvBuilder.Length > 0)
                            await System.IO.File.AppendAllTextAsync("data/census.csv", censusCsvBuilder.ToString(), stoppingToken);
                        if (censusGeoCsvBuilder.Length > 0)
                            await System.IO.File.AppendAllTextAsync("data/censusgeo.csv", censusGeoCsvBuilder.ToString(), stoppingToken);

                        stopwatch.Stop();
                        if (stopwatch.ElapsedMilliseconds > 100)
                        {
                            Console.WriteLine($"[DIAGNOSTIC] Background data processing and CSV disk writes took {stopwatch.ElapsedMilliseconds}ms while holding m_serializerMutex lock.");
                        }

                        foreach (var key in JamulusListURLs.Keys)
                        {
                            var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(LastReportedList[key]);
                            foreach (var server in serversOnList)
                            {
                                if (false == m_serverFirstSeen.ContainsKey(server.ip + ":" + server.port))
                                    m_serverFirstSeen.Add(server.ip + ":" + server.port, DateTime.Now);
                            }
                        }
                    }
                    finally
                    {
                        m_serializerMutex.Release();
                    }

                    int secs = 5;
                    if (fMissingSamplePresent) secs = 2;
                    await Task.Delay(secs * 1000, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ERROR in RefreshThreadTask: {ex.Message}. Restarting loop in 30s.");
                    await Task.Delay(30000, stoppingToken);
                }
            }
        }
    }
}
