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

        // ── Alt-source: blocked servers via explorer.jamulus.io ──────────────
        // Servers that chronically block 137.184.43.255 are polled here instead,
        // using the explorer.jamulus.io per-server API (London-1 then London-2).
        // Data flows into census.csv / server.csv exactly like primary data.
        // No join-events (correlation engine) for these servers — expected degradation.
        public static HashSet<string> BlockedServerKeys = new HashSet<string>(StringComparer.Ordinal);
        private static DateTime _blockedListFetchedAt = DateTime.MinValue;
        private static readonly Dictionary<string, JamulusServers> _altSourceCache =
            new Dictionary<string, JamulusServers>(StringComparer.Ordinal);
        private static int _altRoundRobinIdx = 0;
        // Synthetic LastReportedList key — ProcessServerListsAsync iterates LastReportedList.Keys
        // so alt-source servers appear in the web UI without any changes to that code path.
        public const string AltSourceKey = "_alt";
        // Real server metadata for blocked servers: name, city, country, and which JamulusListURLs
        // key (e.g. "Any Genre 1") they belong to — populated from London full-directory fetches.
        public static readonly Dictionary<string, (string Name, string City, string Country, string DirectoryKey)>
            AltServerMeta = new Dictionary<string, (string, string, string, string)>(StringComparer.Ordinal);

        // GUIDs already persisted to censusgeo.csv — prevents unbounded file growth from duplicate appends
        private static readonly HashSet<string> _censusgeoWritten = new HashSet<string>(StringComparer.Ordinal);
        private static bool _censusgeoWrittenLoaded = false;

        private static void EnsureCensusgeoWrittenLoaded()
        {
            if (_censusgeoWrittenLoaded) return;
            _censusgeoWrittenLoaded = true;
            const string path = "data/censusgeo.csv";
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadLines(path))
            {
                int comma = line.IndexOf(',');
                if (comma > 0) _censusgeoWritten.Add(line.Substring(0, comma));
            }
        }

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

        // ── Blocked-server alt-source helpers ─────────────────────────────────

        private async Task RefreshBlockedListAsync(CancellationToken ct)
        {
            try
            {
                var text = await s_refreshClient.GetStringAsync("http://137.184.43.255/blocked.php", ct);
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var line in text.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.Length > 0 && !t.StartsWith('#')) keys.Add(t);
                }
                BlockedServerKeys = keys;
                // Evict cache entries for servers no longer on the blocked list.
                foreach (var stale in _altSourceCache.Keys.Except(keys).ToList())
                    _altSourceCache.Remove(stale);

                // Fetch real server metadata (name/city/country/directory) from London full-directory
                // responses. The per-server API only returns the IP as name; the directory response
                // has the full metadata. Run after updating BlockedServerKeys so we know what to seek.
                foreach (var (dirLabel, primaryUrl) in JamulusListURLs)
                {
                    var parts = primaryUrl.Split("/servers_data/");
                    if (parts.Length < 2) continue;
                    string dirHost = parts[1].Split('/')[0];
                    try
                    {
                        var json = await s_refreshClient.GetStringAsync(
                            $"https://explorer.jamulus.io/servers.php?directory={dirHost}", ct);
                        var servers = JsonSerializer.Deserialize<List<JamulusServers>>(json);
                        if (servers == null) continue;
                        foreach (var s in servers)
                        {
                            string addr = s.ip + ":" + s.port;
                            if (keys.Contains(addr) && s.name?.Length > 0 && s.name != s.ip)
                                AltServerMeta[addr] = (s.name, s.city ?? "", s.country ?? "", dirLabel);
                        }
                    }
                    catch { }
                }
                // Remove metadata for servers no longer blocked.
                foreach (var stale in AltServerMeta.Keys.Except(keys).ToList())
                    AltServerMeta.Remove(stale);

                Console.WriteLine($"[ALT] Blocked list refreshed: {keys.Count} servers, meta={AltServerMeta.Count}, altCached={_altSourceCache.Count}.");
            }
            catch (Exception ex) { Console.WriteLine($"[ALT] blocked.php fetch failed: {ex.Message}"); }
        }

        // Poll one blocked server round-robin via explorer.jamulus.io per-server API.
        // London-1 is tried first; London-2 is the fallback. The result updates _altSourceCache
        // and republishes LastReportedList[AltSourceKey] so the web UI and census loop see it.
        private async Task PollOneAltSourceServerAsync(CancellationToken ct)
        {
            var blocked = BlockedServerKeys;
            if (blocked.Count == 0) return;
            var list = blocked.ToList();
            string serverKey = list[_altRoundRobinIdx % list.Count];
            _altRoundRobinIdx++;

            JamulusServers srv = null;
            string endpoint = null;
            foreach (var (baseUrl, label) in new[] {
                ("https://explorer.jamulus.io/servers.php?server=",     "lon1"),
                ("https://explorer.jamulus.io/servers-lon2.php?server=","lon2") })
            {
                try
                {
                    var json = await s_refreshClient.GetStringAsync(baseUrl + serverKey, ct);
                    var parsed = JsonSerializer.Deserialize<List<JamulusServers>>(json);
                    if (parsed?.Count > 0 && parsed[0].ping >= 0) { srv = parsed[0]; endpoint = label; break; }
                }
                catch { }
            }
            if (srv == null)
            {
                Console.WriteLine($"[ALT] {serverKey}: no response from either explorer endpoint");
                return;
            }

            // Apply real metadata (name/city/country) from the hourly directory fetch.
            // The per-server API always returns the IP as name with no city/country.
            if (AltServerMeta.TryGetValue(serverKey, out var meta))
            {
                srv.name    = meta.Name;
                srv.city    = meta.City;
                srv.country = meta.Country;
            }
            else
            {
                srv.name    = (srv.name?.Length > 0) ? srv.name : serverKey;
                srv.city    ??= "";
                srv.country ??= "";
            }
            srv.ipaddrs ??= "";

            _altSourceCache[serverKey] = srv;
            Console.WriteLine($"[ALT] {serverKey}: {srv.clients?.Length ?? 0} clients via {endpoint}");

            // Publish to LastReportedList so ProcessServerListsAsync picks up new entries.
            var altJson = JsonSerializer.Serialize(_altSourceCache.Values.ToList());
            await m_serializerMutex.WaitAsync(ct);
            try { LastReportedList[AltSourceKey] = altJson; }
            finally { m_serializerMutex.Release(); }
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
                    // Refresh blocked list hourly; poll two blocked servers per cycle (round-robin)
                    // so the full rotation completes in ~37s instead of ~75s, keeping census samples
                    // within the 60-second per-minute window.
                    if ((DateTime.UtcNow - _blockedListFetchedAt).TotalHours >= 1)
                    {
                        await RefreshBlockedListAsync(stoppingToken);
                        _blockedListFetchedAt = DateTime.UtcNow;
                    }
                    await Task.WhenAll(
                        PollOneAltSourceServerAsync(stoppingToken),
                        PollOneAltSourceServerAsync(stoppingToken));

                    var serverStates = new Dictionary<string, Task<string>>();
                    var fetchStarted = DateTime.UtcNow;

                    foreach (var key in JamulusListURLs.Keys)
                        serverStates.Add(key, s_refreshClient.GetStringAsync(JamulusListURLs[key], stoppingToken));

                    await Task.WhenAll(serverStates.Values);

                    var fetchMs = (DateTime.UtcNow - fetchStarted).TotalMilliseconds;
                    if (fetchMs > 5000)
                        Console.WriteLine($"[WARN] Slow fetch: all {serverStates.Count} URLs took {fetchMs:F0}ms at {DateTime.UtcNow:u}");

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
                        EnsureCensusgeoWrittenLoaded();

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
                                    _tracker.NotateWhoHere(server.ip + ":" + server.port, stringHashOfGuy);

                                    censusCsvBuilder.Append(MinutesSince2023() + ","
                                        + stringHashOfGuy + ","
                                        + server.ip + ":" + server.port
                                        + Environment.NewLine);

                                    if (_censusgeoWritten.Add(stringHashOfGuy))
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

                        // Alt-source census: blocked servers polled from explorer.jamulus.io.
                        // Same CSV output as primary; no encounter tracking (no join-events on these servers).
                        // Double-census guard: skip any server already present in primary data this cycle.
                        var primaryServerKeys = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var k in JamulusListURLs.Keys)
                            if (m_deserializedCache.TryGetValue(k, out var sl))
                                foreach (var sv in sl) primaryServerKeys.Add(sv.ip + ":" + sv.port);

                        int altActive = 0, altClients = 0;
                        foreach (var server in _altSourceCache.Values.ToList())
                        {
                            int people = server.clients?.Length ?? 0;
                            if (people < 1) continue;
                            string addr = server.ip + ":" + server.port;
                            if (primaryServerKeys.Contains(addr)) continue; // server unblocked; primary has it
                            altActive++;
                            altClients += people;
                            serverCsvBuilder.Append(addr + ","
                                + System.Web.HttpUtility.UrlEncode(server.name) + ","
                                + System.Web.HttpUtility.UrlEncode(server.city) + ","
                                + System.Web.HttpUtility.UrlEncode(server.country)
                                + Environment.NewLine);
                            foreach (var guy in server.clients)
                            {
                                string hash = EncounterTracker.GetHash(guy.name, guy.country, guy.instrument);
                                _tracker.NotateWhoHere(addr, hash);
                                censusCsvBuilder.Append(MinutesSince2023() + "," + hash + "," + addr + Environment.NewLine);
                                if (_censusgeoWritten.Add(hash))
                                    censusGeoCsvBuilder.Append(hash + ","
                                        + System.Web.HttpUtility.UrlEncode(guy.name) + ","
                                        + guy.instrument + ","
                                        + System.Web.HttpUtility.UrlEncode(guy.city) + ","
                                        + System.Web.HttpUtility.UrlEncode(guy.country)
                                        + Environment.NewLine);
                            }
                        }
                        if (_altSourceCache.Count > 0)
                            Console.WriteLine($"[ALT-census] cached={_altSourceCache.Count} active={altActive} clients={altClients}");

                        if (serverCsvBuilder.Length > 0)
                            await File.AppendAllTextAsync("data/server.csv", serverCsvBuilder.ToString(), stoppingToken);
                        if (censusCsvBuilder.Length > 0)
                            await File.AppendAllTextAsync("data/census.csv", censusCsvBuilder.ToString(), stoppingToken);
                        if (censusGeoCsvBuilder.Length > 0)
                            await File.AppendAllTextAsync("data/censusgeo.csv", censusGeoCsvBuilder.ToString(), stoppingToken);

                        stopwatch.Stop();
                        if (stopwatch.ElapsedMilliseconds > 100)
                            Console.WriteLine($"[DIAGNOSTIC] Background processing took {stopwatch.ElapsedMilliseconds}ms while holding m_serializerMutex.");

                        // ── Memory diagnostics ──────────────────────────────────────────
                        Console.WriteLine($"[MEM-bg] serverFirstSeen={m_serverFirstSeen.Count} censusgeoWritten={_censusgeoWritten.Count}");

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
                        foreach (var server in _altSourceCache.Values)
                        {
                            string addr = server.ip + ":" + server.port;
                            if (!m_serverFirstSeen.ContainsKey(addr))
                                m_serverFirstSeen.Add(addr, DateTime.Now);
                        }
                    }
                    finally
                    {
                        m_serializerMutex.Release();
                    }

                    var rssLine = System.IO.File.ReadAllLines("/proc/self/status").FirstOrDefault(l => l.StartsWith("VmRSS"));
                    var gcKb = GC.GetTotalMemory(false) / 1024;
                    var threadCount = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
                    Console.WriteLine($"[RSS-bg] {DateTime.UtcNow:u} {rssLine?.Split(':')[1].Trim() ?? "?"}  gc={gcKb}kB  threads={threadCount}");
                    int secs = fMissingSamplePresent ? 2 : 5;
                    await Task.Delay(secs * 1000, stoppingToken);
                }
                catch (OperationCanceledException ex) when (ex.CancellationToken != stoppingToken)
                {
                    Console.WriteLine($"[WARN] RefreshThreadTask: HTTP timeout/cancel at {DateTime.UtcNow:u} — restarting loop.");
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
