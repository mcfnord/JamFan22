using JamFan22.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JamFan22.Services
{
    public class JamulusCacheManager
    {
        private readonly EncounterTracker _tracker;

        public JamulusCacheManager(EncounterTracker tracker)
        {
            _tracker = tracker;
        }

        // ── Platform helpers ──────────────────────────────────────────────────

        public static bool IsDebuggingOnWindows
        {
            get
            {
#if WINDOWS
                return true;
#else
                return false;
#endif
            }
        }

        // ── Directory URLs ────────────────────────────────────────────────────

        public static Dictionary<string, string> JamulusListURLs = new Dictionary<string, string>()
        {
            {"Any Genre 1",       "http://24.199.107.192:5001/servers_data/anygenre1.jamulus.io:22124/cached_data"},
            {"Any Genre 2",       "http://24.199.107.192:5001/servers_data/anygenre2.jamulus.io:22224/cached_data"},
            {"Any Genre 3",       "http://24.199.107.192:5001/servers_data/anygenre3.jamulus.io:22624/cached_data"},
            {"Genre Rock",        "http://24.199.107.192:5001/servers_data/rock.jamulus.io:22424/cached_data"},
            {"Genre Jazz",        "http://24.199.107.192:5001/servers_data/jazz.jamulus.io:22324/cached_data"},
            {"Genre Classical/Folk", "http://24.199.107.192:5001/servers_data/classical.jamulus.io:22524/cached_data"},
            {"Genre Choral/BBShop",  "http://24.199.107.192:5001/servers_data/choral.jamulus.io:22724/cached_data"}
        };

        // ── Shared cache state ────────────────────────────────────────────────

        public Dictionary<string, List<JamulusServers>> m_deserializedCache = new Dictionary<string, List<JamulusServers>>();
        public Dictionary<string, string>               m_jsonCacheSource   = new Dictionary<string, string>();

        public static Dictionary<string, DateTime> DirectoryLastUpdated = new Dictionary<string, DateTime>();
        public static Dictionary<string, string>   LastReportedList     = new Dictionary<string, string>();
        static DateTime? LastReportedListGatheredAt = null;
        public static List<string> ListServicesOffline = new List<string>();

        public static Dictionary<string, DateTime> m_serverFirstSeen = new Dictionary<string, DateTime>();

        public static SemaphoreSlim m_serializerMutex = new SemaphoreSlim(1, 1);

        private static readonly HttpClient s_refreshClient = new HttpClient(
            new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, ssl) => true
            }
        );

        // ── Time helpers ──────────────────────────────────────────────────────

        public static int MinutesSince2023AsInt()
        {
            var now  = DateTime.UtcNow;
            var then = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (int)(now - then).TotalMinutes;
        }

        public static string MinutesSince2023() => MinutesSince2023AsInt().ToString("D7");

        public static async Task<List<string>> LoadLinesFromHttpTextFile(string url)
        {
            using var client = new HttpClient();
            try
            {
                HttpResponseMessage response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    var lines = new List<string>();
                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var reader = new System.IO.StreamReader(stream);
                    string line;
                    while ((line = await reader.ReadLineAsync()) != null)
                        lines.Add(line);
                    return lines;
                }
                Console.WriteLine("Failed to retrieve the file. Status code: " + response.StatusCode);
            }
            catch (HttpRequestException e)
            {
                Console.WriteLine("HttpRequestException: " + e.Message);
            }
            return new List<string>();
        }

        // ── Background refresh loop ───────────────────────────────────────────

        public async Task RefreshThreadTask(CancellationToken stoppingToken)
        {
            async Task GenerateLiveStatusJsonAsync(Dictionary<string, Task<string>> serverStates)
            {
                var liveStatus = new Dictionary<string, object>();

                foreach (var kvp in serverStates)
                {
                    try
                    {
                        string json = await kvp.Value;

                        if (json.TrimStart().StartsWith("{"))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(json);
                                if (doc.RootElement.TryGetProperty("servers_data", out var serversData))
                                    json = serversData.GetRawText();
                            }
                            catch { }
                        }

                        var servers = JsonSerializer.Deserialize<List<JamulusServers>>(json);
                        if (servers == null) continue;

                        foreach (var server in servers)
                        {
                            if (server.clients == null || server.clients.Length == 0) continue;
                            string serverKey = $"{server.ip}:{server.port}";
                            var guids = new List<string>();
                            foreach (var client in server.clients)
                                guids.Add(EncounterTracker.GetHash(client.name, client.country, client.instrument));
                            if (guids.Count > 0)
                                liveStatus[serverKey] = new { name = server.name, clients = guids };
                        }
                    }
                    catch { }
                }

                try
                {
                    string jsonOutput = JsonSerializer.Serialize(liveStatus);
                    await File.WriteAllTextAsync("wwwroot/livestatus.json", jsonOutput, stoppingToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to write livestatus.json: {ex.Message}");
                }
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                bool fMissingSamplePresent = false;

                try
                {
                    var serverStates = new Dictionary<string, Task<string>>();

                    foreach (var key in JamulusListURLs.Keys)
                        serverStates.Add(key, s_refreshClient.GetStringAsync(JamulusListURLs[key], stoppingToken));

                    await Task.WhenAll(serverStates.Values);
                    await GenerateLiveStatusJsonAsync(serverStates);

                    foreach (var key in JamulusListURLs.Keys)
                    {
                        string newReportedList = null;
                        try
                        {
                            newReportedList = await serverStates[key];

                            if (newReportedList.TrimStart().StartsWith("{"))
                            {
                                try
                                {
                                    using var doc = JsonDocument.Parse(newReportedList);
                                    if (doc.RootElement.TryGetProperty("servers_data", out var serversData))
                                        newReportedList = serversData.GetRawText();
                                    if (doc.RootElement.TryGetProperty("timestamp", out var ts))
                                        DirectoryLastUpdated[key] = DateTimeOffset.FromUnixTimeSeconds((long)ts.GetDouble()).UtcDateTime.ToLocalTime();
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
                                    // unchanged
                                }
                                else
                                {
                                    if (LastReportedList.ContainsKey(key))
                                        EncounterTracker.DetectJoiners(LastReportedList[key], newReportedList);
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
                        TimeSpan durationBetweenSamples = (LastReportedListGatheredAt != null)
                            ? DateTime.Now.Subtract((DateTime)LastReportedListGatheredAt)
                            : new TimeSpan();

                        LastReportedListGatheredAt = DateTime.Now;

                        var newOfflineList = new List<string>();
                        foreach (var keyHere in JamulusListURLs.Keys)
                        {
                            var serversOnList = JsonSerializer.Deserialize<List<JamulusServers>>(LastReportedList[keyHere]);
                            if (serversOnList.Count == 0) { newOfflineList.Add(keyHere); fMissingSamplePresent = true; }
                        }
                        ListServicesOffline = newOfflineList;

                        var alreadyPushed = new HashSet<string>();
                        var serverCsvBuilder  = new System.Text.StringBuilder();
                        var censusCsvBuilder  = new System.Text.StringBuilder();
                        var censusGeoCsvBuilder = new System.Text.StringBuilder();

                        foreach (var key in JamulusListURLs.Keys)
                        {
                            string currentJson = LastReportedList[key];
                            List<JamulusServers> serversOnList;

                            if (m_jsonCacheSource.TryGetValue(key, out var cachedJson) && cachedJson == currentJson)
                            {
                                serversOnList = m_deserializedCache[key];
                            }
                            else
                            {
                                serversOnList = JsonSerializer.Deserialize<List<JamulusServers>>(currentJson);
                                m_deserializedCache[key] = serversOnList;
                                m_jsonCacheSource[key]   = currentJson;
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
                                    string stringHashOfGuy = EncounterTracker.GetHash(guy.name, guy.country, guy.instrument);

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

                                    if (!EncounterTracker.m_userServerViewTracker.ContainsKey(stringHashOfGuy))
                                        EncounterTracker.m_userServerViewTracker[stringHashOfGuy] = new HashSet<string>();
                                    EncounterTracker.m_userServerViewTracker[stringHashOfGuy].Add(server.ip + ":" + server.port);

                                    if (!EncounterTracker.m_userConnectDuration.ContainsKey(stringHashOfGuy))
                                        EncounterTracker.m_userConnectDuration[stringHashOfGuy] = new TimeSpan();
                                    EncounterTracker.m_userConnectDuration[stringHashOfGuy] =
                                        EncounterTracker.m_userConnectDuration[stringHashOfGuy].Add(durationBetweenSamples.Divide(server.clients.Count()));

                                    if (!EncounterTracker.m_userConnectDurationPerServer.ContainsKey(stringHashOfGuy))
                                        EncounterTracker.m_userConnectDurationPerServer[stringHashOfGuy] = new Dictionary<string, TimeSpan>();
                                    var fullIP      = server.ip + ":" + server.port;
                                    var theGuyServer = EncounterTracker.m_userConnectDurationPerServer[stringHashOfGuy];
                                    if (!theGuyServer.ContainsKey(fullIP)) theGuyServer.Add(fullIP, TimeSpan.Zero);
                                    theGuyServer[fullIP] = theGuyServer[fullIP].Add(durationBetweenSamples.Divide(server.clients.Count()));

                                    foreach (var otherguy in server.clients)
                                    {
                                        if (!EncounterTracker.m_userConnectDurationPerUser.ContainsKey(stringHashOfGuy))
                                            EncounterTracker.m_userConnectDurationPerUser[stringHashOfGuy] = new Dictionary<string, TimeSpan>();
                                        if (otherguy == guy) continue;

                                        string stringHashOfOtherGuy = EncounterTracker.GetHash(otherguy.name, otherguy.country, otherguy.instrument);
                                        var theGuyUser = EncounterTracker.m_userConnectDurationPerUser[stringHashOfGuy];
                                        if (!theGuyUser.ContainsKey(stringHashOfOtherGuy))
                                            theGuyUser.Add(stringHashOfOtherGuy, TimeSpan.Zero);
                                        theGuyUser[stringHashOfOtherGuy] = theGuyUser[stringHashOfOtherGuy].Add(durationBetweenSamples.Divide(server.clients.Count()));

                                        string us = EncounterTracker.CanonicalTwoHashes(stringHashOfGuy, stringHashOfOtherGuy);
                                        if (!EncounterTracker.m_everywhereWeHaveMet.ContainsKey(us))
                                            EncounterTracker.m_everywhereWeHaveMet[us] = new HashSet<string>();
                                        EncounterTracker.m_everywhereWeHaveMet[us].Add(server.ip + ":" + server.port);

                                        if (durationBetweenSamples.TotalSeconds > 0
                                            && stringHashOfGuy != stringHashOfOtherGuy
                                            && !alreadyPushed.Contains(us))
                                        {
                                            alreadyPushed.Add(us);
                                            EncounterTracker.ReportPairTogether(us, durationBetweenSamples);
                                        }
                                    }
                                }
                            }
                        }

                        if (serverCsvBuilder.Length > 0)
                            await File.AppendAllTextAsync("data/server.csv", serverCsvBuilder.ToString(), stoppingToken);
                        if (censusCsvBuilder.Length > 0)
                            await File.AppendAllTextAsync("data/census.csv", censusCsvBuilder.ToString(), stoppingToken);
                        if (censusGeoCsvBuilder.Length > 0)
                            await File.AppendAllTextAsync("data/censusgeo.csv", censusGeoCsvBuilder.ToString(), stoppingToken);

                        stopwatch.Stop();
                        if (stopwatch.ElapsedMilliseconds > 100)
                            Console.WriteLine($"[DIAGNOSTIC] Background processing took {stopwatch.ElapsedMilliseconds}ms while holding m_serializerMutex.");

                        foreach (var key in JamulusListURLs.Keys)
                        {
                            var serversOnList = JsonSerializer.Deserialize<List<JamulusServers>>(LastReportedList[key]);
                            foreach (var server in serversOnList)
                            {
                                string addr = server.ip + ":" + server.port;
                                if (!m_serverFirstSeen.ContainsKey(addr))
                                    m_serverFirstSeen.Add(addr, DateTime.Now);
                            }
                        }
                    }
                    finally
                    {
                        m_serializerMutex.Release();
                    }

                    int secs = fMissingSamplePresent ? 2 : 5;
                    await Task.Delay(secs * 1000, stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"ERROR in RefreshThreadTask: {ex.Message}. Restarting loop in 30s.");
                    await Task.Delay(30000, stoppingToken);
                }
            }
        }
    }
}
