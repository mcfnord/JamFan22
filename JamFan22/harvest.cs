// VIDEO URL SYSTEM
// Video URLs are stored in m_discreetLinks (keyed by ip:port). Three ingest sources:
//   1. Lounge monitor instances
//   2. Fleet servers via POST /chat-url-server (raw text match)
//   3. Custom client via POST /chat-url-client
//
// URL LOGGING (data/urls.csv and data/urls-rejected.csv)
// Every URL arriving via any path is logged:
//   - Passes chat-patterns.txt  → data/urls.csv  (minutes, source, server, encoded_url)
//   - Fails  chat-patterns.txt  → data/urls-rejected.csv (same schema)
// Source values: "lounge", "server", "client". Lounge rejects use the lounge hostname as addr.
//
// Api.cshtml.cs reads m_discreetLinks and sets apiSvr.videoUrl — but only for ip-allowed
// visitors (visitorIsAllowed, checked once per request with 48h per-IP cache via
// IpAnalyticsService.IsIpAllowedAsync). Client.cshtml renders the video icon and gates
// clicks on the my-active-server CSS class (blue line = connected to that server).
//
// SONG TITLE SCRAPING
// After storing a URL, ScrapeTitleAsync(url) stores result in m_songTitleAtAddr.
// Special cases in order:
//   1. ultimate-guitar (tabs.ultimate-guitar.com/tab/{artist}/{title-slug}-{type}-{id}):
//      parsed directly from URL slug — no HTTP request. Strips known type suffixes
//      (chords, tabs, bass-tabs, ukulele, drum-tabs, power-tabs, guitar-pro, official,
//      fingerstyle, classical) and numeric ID, then title-cases both parts.
//      Returns "Song Title — Artist Name". Site returns 403 to all scrapers.
//   2. chords69cl (chords69cl.vercel.app): queries Firebase RTDB for the room's title field.
//   3. busk.town, chordtabs.in.th, designbetrieb.de: HTML scrape with site-specific <title>.
//   4. YouTube/oEmbed: oEmbed JSON title field.
//   5. Generic: HTTP GET + <title> parse.

using JamFan22.Models;
using JamFan22.Services;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ConstrainedExecution;
using System.Text.RegularExpressions;

namespace JamFan22
{
    public class harvest
    {
        class ChatMessage
        {
            public string id { get; set; }
            public string message { get; set; }
            public string timestamp { get; set; }
        }

        private static readonly Regex TagCleaner = new Regex("<.*?>", RegexOptions.Compiled);
        private static readonly Regex LobbyPattern = new Regex(@"lobby", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static System.Collections.Concurrent.ConcurrentDictionary<string, bool> m_loungeIsQuiet = new();
        // Keyed by "ip:jamulusport". True = silent. Absent = unknown or no UDP response yet.
        public static ConcurrentDictionary<string, bool> m_fleetSilenceStatus = new();
        // Keyed by "ip:jamulusport". Value = per-slot audio levels indexed by chanid.
        public static ConcurrentDictionary<string, int[]> m_fleetSlotLevels = new();
        // Keyed by "ip:jamulusport". Value = playerName → audio level (from 1013/1015 cross-join).
        public static ConcurrentDictionary<string, Dictionary<string, int>> m_fleetClientLevels = new();
        // UTC time of last successful 1013 poll per fleet server.
        public static ConcurrentDictionary<string, DateTime> m_fleetClientLevelsAt = new();

        public static ConcurrentDictionary<string, (string Url, DateTime Stored)> m_discreetLinks = new();
        public static Dictionary<string, string> m_songTitle = new Dictionary<string, string>();
        public static Dictionary<string, string> m_songTitleAtAddr = new Dictionary<string, string>();
        static Dictionary<string, DateTime> m_songTitleExpiry = new Dictionary<string, DateTime>();
        static DateTime m_nextCleanup = DateTime.MinValue;
        // serverAddr-with-dashes → chords69cl room URL; kept alive until a non-room URL displaces it.
        public static ConcurrentDictionary<string, string> m_activeRoomUrls = new();
        // Last title fetched from a chords69cl room; used to detect stale re-polls after expiry.
        static ConcurrentDictionary<string, string> m_lastRoomTitle = new();

        private static (string[] Lines, DateTime Expiry) _chatPatternsCache = (Array.Empty<string>(), DateTime.MinValue);

        public static async Task<bool> UrlMatchesChatPatternsAsync(string url)
        {
            if (DateTime.UtcNow >= _chatPatternsCache.Expiry)
            {
                var lines = File.Exists("wwwroot/chat-patterns.txt")
                    ? await File.ReadAllLinesAsync("wwwroot/chat-patterns.txt")
                    : Array.Empty<string>();
                _chatPatternsCache = (lines, DateTime.UtcNow.AddMinutes(5));
            }
            foreach (var line in _chatPatternsCache.Lines)
            {
                string t = line.Trim();
                if (string.IsNullOrEmpty(t) || t.StartsWith("#")) continue;
                if (Regex.IsMatch(url, t, RegexOptions.IgnoreCase)) return true;
            }
            return false;
        }

        static readonly Regex s_ugTitleRegex = new Regex(
            @"(?:[a-z]{2}\.)?(?:tabs\.)?ultimate-guitar\.com/tab/([^/]+)/(.+?)(?:-(chords?|tabs?|bass-tabs?|ukulele|drum-tabs?|power-tabs?|guitar-pro|official|fingerstyle|classical))?-\d+(?:[?#][^\s]*)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static void AppendAcceptedLog(string url, string source, string serverAddr)
        {
            string songTitle = "", artist = "";
            var ugMatch = s_ugTitleRegex.Match(url);
            if (ugMatch.Success)
            {
                var tc = System.Globalization.CultureInfo.InvariantCulture.TextInfo;
                artist    = tc.ToTitleCase(ugMatch.Groups[1].Value.Replace('-', ' ').ToLower());
                songTitle = tc.ToTitleCase(ugMatch.Groups[2].Value.Replace('-', ' ').ToLower());
            }
            int nowMins = JamulusCacheManager.MinutesSince2023AsInt();
            File.AppendAllText("data/urls.csv",
                nowMins + "," + source + ","
                + serverAddr + "," + System.Web.HttpUtility.UrlEncode(url) + ","
                + System.Web.HttpUtility.UrlEncode(songTitle) + ","
                + System.Web.HttpUtility.UrlEncode(artist) + Environment.NewLine);
        }

        // Called after ScrapeTitleAsync resolves so chords69cl Firebase titles are captured.
        static void AppendGuidLog(string url, string serverAddr, string title)
        {
            if (string.IsNullOrEmpty(title)) return;
            var svrSnapshot = JamulusAnalyzer.m_allMyServers;
            if (svrSnapshot == null) return;
            var guids = new List<string>();
            foreach (var svr in svrSnapshot)
            {
                if (svr.serverIpAddress + ":" + svr.serverPort != serverAddr) continue;
                if (svr.whoObjectFromSourceData != null)
                    foreach (var c in svr.whoObjectFromSourceData)
                        guids.Add(EncounterTracker.GetHash(c.name, c.country, c.instrument));
                break;
            }
            if (guids.Count > 0)
                File.AppendAllText("data/url-guids.csv",
                    JamulusCacheManager.MinutesSince2023AsInt() + "," + serverAddr + ","
                    + System.Web.HttpUtility.UrlEncode(url) + ","
                    + System.Web.HttpUtility.UrlEncode(title) + ","
                    + string.Join("|", guids) + Environment.NewLine);
        }

        public static void AppendRejectedLog(string url, string source, string addr) =>
            File.AppendAllText("data/urls-rejected.csv",
                JamulusCacheManager.MinutesSince2023AsInt() + "," + source + ","
                + addr + "," + System.Web.HttpUtility.UrlEncode(url) + Environment.NewLine);

        public static async Task IngestChatUrlAsync(string url, string serverAddr, string source)
        {
            AppendAcceptedLog(url, source, serverAddr);
            string lowerUrl = url.ToLower();
            if (lowerUrl.Contains("vdo.ninja") || lowerUrl.Contains("meet.google.com") ||
                lowerUrl.Contains(".zoom.us") || lowerUrl.Contains("meet.jit.si"))
            {
                m_discreetLinks[serverAddr] = (url, DateTime.UtcNow);
                Console.WriteLine($"[Harvest] Discreet link at {serverAddr}: {url}");
            }

            string addrKey = serverAddr.Replace(':', '-');
            bool isRoomUrl = Regex.IsMatch(url, @"chords69cl\.vercel\.app/[^?]*\?.*room=", RegexOptions.IgnoreCase);

            string title = await ScrapeTitleAsync(url);
            AppendGuidLog(url, serverAddr, title);
            if (title != null)
            {
                if (!s_ugTitleRegex.IsMatch(url))
                {
                    int sep = title.IndexOf(" — ", StringComparison.Ordinal);
                    string scrapedSong   = sep > 0 ? title.Substring(0, sep)       : title;
                    string scrapedArtist = sep > 0 ? title.Substring(sep + 3)      : "";
                    int nowMins2 = JamulusCacheManager.MinutesSince2023AsInt();
                    File.AppendAllText("data/urls.csv",
                        nowMins2 + "," + source + ","
                        + serverAddr + "," + System.Web.HttpUtility.UrlEncode(url) + ","
                        + System.Web.HttpUtility.UrlEncode(scrapedSong) + ","
                        + System.Web.HttpUtility.UrlEncode(scrapedArtist) + Environment.NewLine);
                }
                ShortLivedTitleForServerAtAddr(title, addrKey);
                if (isRoomUrl)
                {
                    m_activeRoomUrls[addrKey] = url;
                    m_lastRoomTitle[addrKey] = title;
                }
                else
                    m_activeRoomUrls.TryRemove(addrKey, out _);
            }
        }

        static void DiscreetLinkForServer(string loungeUrl, string url)
        {
            string where = JamulusAnalyzer.m_connectedLounges.FirstOrDefault(x => x.Value == loungeUrl).Key;
            if (where == null) return;
            m_discreetLinks[where] = (url, DateTime.UtcNow);
        }

        static void ShortLivedTitleForServer(string title, string url, string loungeUrl)
        {
            if (title.Length == 0) return;
            string where = "1.2.3.4";
            if (!JamulusCacheManager.IsDebuggingOnWindows)
            {
                where = JamulusAnalyzer.m_connectedLounges.FirstOrDefault(x => x.Value == loungeUrl).Key;
                if (where == null)
                {
                    Console.WriteLine($"[Harvest] WARN: no IP mapping for lounge '{loungeUrl}'");
                    return;
                }
            }
            Console.WriteLine($"[Harvest] Storing title '{title}' at key '{where.Replace(':', '-')}'");
            string key = where.Replace(':', '-');
            m_songTitleAtAddr[key] = title;
            m_songTitleExpiry[key] = DateTime.UtcNow.AddMinutes(16);

        }

        static void ShortLivedTitleForServerAtAddr(string title, string serverAddr)
        {
            if (title.Length > 0)
            {
                m_songTitleAtAddr[serverAddr] = title;
                m_songTitleExpiry[serverAddr] = DateTime.UtcNow.AddMinutes(16);
            }
        }

        static readonly List<string> m_staticLounges = new List<string>
        {
            "https://lobby.jam.voixtel.net.br/",
            "https://StudioD.live",
        };

        static Dictionary<string, string> m_lastLineMap = new Dictionary<string, string>();

        public static async Task HarvestLoop2025(CancellationToken stoppingToken)
        {
            // Wait for the app to finish initializing before we start connecting.
            await Task.Delay(60 * 1000, stoppingToken);

            // Seed m_connectedLounges so lounge→IP lookups work without needing a browser hit.
            await JamulusAnalyzer.LoadConnectedLoungesAsync();

            _ = Task.Run(() => ChannelLevelPollLoopAsync(stoppingToken));
            _ = Task.Run(() => NonFleetSilencePoller.PollLoopAsync(stoppingToken));

            var monitors = new Dictionary<string, Task>();

            while (!stoppingToken.IsCancellationRequested)
            {
                // TTL cleanup
                if (DateTime.UtcNow > m_nextCleanup)
                {
                    m_nextCleanup = DateTime.UtcNow.AddMinutes(1);
                    var now = DateTime.UtcNow;
                    foreach (var key in m_songTitleExpiry.Keys.ToList())
                        if (now > m_songTitleExpiry[key])
                        {
                            m_songTitleAtAddr.Remove(key);
                            m_songTitleExpiry.Remove(key);
                        }
                    var expireBefore = DateTime.UtcNow.AddMinutes(-20);
                    foreach (var key in m_discreetLinks.Keys.ToList())
                        if (m_discreetLinks.TryGetValue(key, out var entry) && entry.Stored < expireBefore)
                            m_discreetLinks.TryRemove(key, out _);
                }

                // Re-poll any active chords69cl room URLs to pick up song changes.
                foreach (var kvp in m_activeRoomUrls.ToArray())
                {
                    string addrKey = kvp.Key;
                    string roomUrl = kvp.Value;
                    try
                    {
                        string newTitle = await ScrapeTitleAsync(roomUrl);
                        if (newTitle != null)
                        {
                            bool isShowing = m_songTitleExpiry.TryGetValue(addrKey, out var exp) && DateTime.UtcNow < exp;
                            m_lastRoomTitle.TryGetValue(addrKey, out string lastTitle);
                            if (!isShowing && newTitle == lastTitle)
                            {
                                Console.WriteLine($"[RoomPoll] {addrKey}: same song '{newTitle}' after expiry, stopping poll");
                                m_activeRoomUrls.TryRemove(addrKey, out _);
                            }
                            else
                            {
                                m_songTitleAtAddr.TryGetValue(addrKey, out string oldTitle);
                                if (oldTitle != newTitle)
                                    Console.WriteLine($"[RoomPoll] {addrKey}: '{oldTitle}' → '{newTitle}'");
                                m_lastRoomTitle[addrKey] = newTitle;
                                ShortLivedTitleForServerAtAddr(newTitle, addrKey);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[RoomPoll] {addrKey}: {ex.Message}");
                    }
                }

                // Fetch the current Thai lounge list and merge with static lounges.
                var thaiLounges = await FetchThaiLoungeUrlsAsync();
                var allLounges = thaiLounges.Union(m_staticLounges);
                foreach (var loungeUrl in allLounges)
                {
                    if (!monitors.TryGetValue(loungeUrl, out var t) || t.IsCompleted)
                    {
                        var captured = loungeUrl;
                        monitors[loungeUrl] = Task.Run(
                            () => MonitorLoungeAsync(captured, stoppingToken), stoppingToken);
                    }
                }

                await Task.Delay(60 * 1000, stoppingToken);
            }
        }

        // PROTMESSID_CLM_REQ_CHANNEL_LEVEL_LIST = 1028 = 0x0404 LE
        // PROTMESSID_CLM_REQ_CONN_CLIENTS_LIST  = 1014 = 0x03F6 LE
        // CRC: CCITT poly=0x1021, init=0xFFFF, inverted, stored LE
        private static readonly byte[] s_clmReqFrame = BuildUdpFrame(0x04, 0x04);
        private static readonly byte[] s_clmReqClientsFrame = BuildUdpFrame(0xF6, 0x03);
        private static readonly byte[] s_clmReqServerListFrame = BuildUdpFrame(0xEF, 0x03); // CLM_REQ_SERVER_LIST = 1007
        private static readonly ConcurrentDictionary<string, System.Net.Sockets.UdpClient> s_serverSockets = new();
        // Adaptive punch tracking: missing=unknown (try direct), true=OCI/needs punch, false=confirmed direct
        private static readonly ConcurrentDictionary<string, bool> s_requiresPunch = new();

        private static byte[] BuildUdpFrame(byte idLo, byte idHi)
        {
            byte[] f = new byte[9];
            f[0]=0x00; f[1]=0x00; // TAG
            f[2]=idLo; f[3]=idHi; // ID LE
            f[4]=0x00;             // counter
            f[5]=0x00; f[6]=0x00; // body length = 0
            ushort crc = JamulusCrc(f, 7);
            f[7]=(byte)(crc & 0xFF); f[8]=(byte)(crc >> 8); // CRC LE
            return f;
        }

        private static ushort JamulusCrc(byte[] data, int len)
        {
            uint crc = 0xFFFF;
            for (int i = 0; i < len; i++)
            {
                crc ^= (uint)data[i] << 8;
                for (int b = 0; b < 8; b++)
                    crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021u) & 0xFFFF : (crc << 1) & 0xFFFF;
            }
            return (ushort)(~crc & 0xFFFF);
        }

        static async Task ChannelLevelPollLoopAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await PollFleetLevelsAsync(); }
                catch (Exception ex) { Console.WriteLine($"[LEVEL-POLL] loop error: {ex.Message}"); }
                await Task.Delay(50000, stoppingToken);
            }
        }

        static async Task PollFleetLevelsAsync()
        {
            string[] lines;
            try { lines = await File.ReadAllLinesAsync("data/fleet-server-ips.txt"); }
            catch (Exception ex) { Console.WriteLine($"[LEVEL-POLL] Cannot read fleet file: {ex.Message}"); return; }

            var tasks = new List<Task>();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var parts = line.Split(':');
                if (parts.Length < 2 || !int.TryParse(parts[1], out int port)) continue;
                string ip = parts[0];
                string? dirHost = parts.Length >= 5 ? parts[3] : null;
                int dirPort = parts.Length >= 5 && int.TryParse(parts[4], out int dp) ? dp : 0;
                tasks.Add(PollServerLevelsAsync(ip, port, dirHost, dirPort));
            }
            await Task.WhenAll(tasks);
        }

        static async Task PollServerLevelsAsync(string ip, int port, string? dirHost, int dirPort)
        {
            string ipport = $"{ip}:{port}";
            var serverAddr = System.Net.IPAddress.Parse(ip);
            var serverEp   = new System.Net.IPEndPoint(serverAddr, port);

            // Persistent unconnected socket — same local port across all polls so OCI
            // stateful entries created by hole-punches remain valid on subsequent cycles.
            var udp = s_serverSockets.GetOrAdd(ipport, _ => new System.Net.Sockets.UdpClient());

            bool hasDirInfo = dirHost != null && dirPort > 0;
            // Punch before every poll for confirmed-OCI servers; try direct first for unknown/confirmed-direct.
            bool requiresPunch = hasDirInfo && s_requiresPunch.GetValueOrDefault(ipport, false);

            try
            {
                if (requiresPunch)
                {
                    try
                    {
                        var dirAddrs = await System.Net.Dns.GetHostAddressesAsync(dirHost!);
                        var dirEp = new System.Net.IPEndPoint(dirAddrs[0], dirPort);
                        await udp.SendAsync(s_clmReqServerListFrame, s_clmReqServerListFrame.Length, dirEp);
                        await Task.Delay(400);
                        Console.WriteLine($"[LEVEL-POLL] {ipport}: hole-punch via {dirHost}:{dirPort}");
                    }
                    catch (Exception ex) { Console.WriteLine($"[LEVEL-POLL] {ipport}: hole-punch failed: {ex.Message}"); }
                }

                using var cts = new CancellationTokenSource(2000);

                // Step 1: 1014 → 1013 (who is here, keyed by channel slot)
                // Receive loop skips noise: 1009 hole-punch empties and packets from other senders.
                await udp.SendAsync(s_clmReqClientsFrame, s_clmReqClientsFrame.Length, serverEp);
                var r1013 = await udp.ReceiveAsync(cts.Token);
                while (!r1013.RemoteEndPoint.Address.Equals(serverAddr)
                       || r1013.Buffer.Length < 4
                       || (ushort)(r1013.Buffer[2] | r1013.Buffer[3] << 8) != 1013)
                    r1013 = await udp.ReceiveAsync(cts.Token);
                var clients = Parse1013Body(r1013.Buffer);

                // Step 2: 1028 → 1015 (channel level nibbles)
                await udp.SendAsync(s_clmReqFrame, s_clmReqFrame.Length, serverEp);
                var r1015 = await udp.ReceiveAsync(cts.Token);
                while (!r1015.RemoteEndPoint.Address.Equals(serverAddr)
                       || r1015.Buffer.Length < 4
                       || (ushort)(r1015.Buffer[2] | r1015.Buffer[3] << 8) != 1015)
                    r1015 = await udp.ReceiveAsync(cts.Token);
                byte[] buf = r1015.Buffer;

                // Frame: TAG(2) + ID(2 LE) + counter(1) + bodylen(2 LE) + body(N) + CRC(2 LE)
                if (buf.Length < 9) return;
                ushort bodyLen = (ushort)(buf[5] | buf[6] << 8);
                bool quiet = true;
                var levelList = new List<int>();
                if (bodyLen > 0)
                {
                    // Low nibble = even client index, high nibble = odd; 0xF high nibble = sentinel.
                    int end = Math.Min(7 + bodyLen, buf.Length - 2);
                    for (int i = 7; i < end; i++)
                    {
                        int lo = buf[i] & 0x0F;
                        levelList.Add(lo);
                        if (lo > 0) quiet = false;
                        int hi = (buf[i] >> 4) & 0x0F;
                        if (hi == 0x0F) break;
                        levelList.Add(hi);
                        if (hi > 0) quiet = false;
                    }
                }

                // Cross-join: name → level by channel slot
                var nameLevel = new Dictionary<string, int>(clients.Count);
                foreach (var (channelId, name) in clients)
                {
                    int level = channelId < levelList.Count ? levelList[channelId] : 0;
                    nameLevel[name] = level;
                }

                // If no named clients, treat as quiet regardless of level nibbles
                // (spurious 1015 data can appear when server was just emptied)
                if (clients.Count == 0) quiet = true;

                if (!m_fleetSilenceStatus.TryGetValue(ipport, out bool prev) || prev != quiet)
                    Console.WriteLine($"[LEVEL-POLL] {ipport}: quiet={quiet} clients={clients.Count}");
                m_fleetSilenceStatus[ipport] = quiet;
                m_fleetSlotLevels[ipport] = levelList.ToArray();
                m_fleetClientLevels[ipport] = nameLevel;
                m_fleetClientLevelsAt[ipport] = DateTime.UtcNow;

                // Direct poll succeeded — remember so we skip punching next cycle
                if (hasDirInfo && !requiresPunch)
                    s_requiresPunch[ipport] = false;
            }
            catch (OperationCanceledException) when (!requiresPunch && hasDirInfo)
            {
                // Direct attempt timed out — punch and retry in this same cycle.
                Console.WriteLine($"[LEVEL-POLL] {ipport}: direct timeout, retrying with hole-punch");
                try
                {
                    var dirAddrs2 = await System.Net.Dns.GetHostAddressesAsync(dirHost!);
                    await udp.SendAsync(s_clmReqServerListFrame, s_clmReqServerListFrame.Length, new System.Net.IPEndPoint(dirAddrs2[0], dirPort));
                    await Task.Delay(400);
                    Console.WriteLine($"[LEVEL-POLL] {ipport}: hole-punch via {dirHost}:{dirPort}");
                }
                catch (Exception pex) { Console.WriteLine($"[LEVEL-POLL] {ipport}: hole-punch failed: {pex.Message}"); }

                try
                {
                    using var cts2 = new CancellationTokenSource(2000);
                    await udp.SendAsync(s_clmReqClientsFrame, s_clmReqClientsFrame.Length, serverEp);
                    var rb1013 = await udp.ReceiveAsync(cts2.Token);
                    while (!rb1013.RemoteEndPoint.Address.Equals(serverAddr) || rb1013.Buffer.Length < 4
                           || (ushort)(rb1013.Buffer[2] | rb1013.Buffer[3] << 8) != 1013)
                        rb1013 = await udp.ReceiveAsync(cts2.Token);
                    var clients2 = Parse1013Body(rb1013.Buffer);

                    await udp.SendAsync(s_clmReqFrame, s_clmReqFrame.Length, serverEp);
                    var rb1015 = await udp.ReceiveAsync(cts2.Token);
                    while (!rb1015.RemoteEndPoint.Address.Equals(serverAddr) || rb1015.Buffer.Length < 4
                           || (ushort)(rb1015.Buffer[2] | rb1015.Buffer[3] << 8) != 1015)
                        rb1015 = await udp.ReceiveAsync(cts2.Token);
                    byte[] buf2 = rb1015.Buffer;

                    if (buf2.Length < 9) return;
                    ushort bl2 = (ushort)(buf2[5] | buf2[6] << 8);
                    bool quiet2 = true;
                    var ll2 = new List<int>();
                    if (bl2 > 0)
                        for (int i = 7; i < Math.Min(7 + bl2, buf2.Length - 2); i++)
                        { int lo = buf2[i] & 0x0F; ll2.Add(lo); if (lo > 0) quiet2 = false;
                          int hi = (buf2[i] >> 4) & 0x0F; if (hi == 0x0F) break; ll2.Add(hi); if (hi > 0) quiet2 = false; }
                    var nl2 = new Dictionary<string, int>();
                    foreach (var (cid, nm) in clients2) nl2[nm] = cid < ll2.Count ? ll2[cid] : 0;
                    if (clients2.Count == 0) quiet2 = true;

                    if (!m_fleetSilenceStatus.TryGetValue(ipport, out bool p2) || p2 != quiet2)
                        Console.WriteLine($"[LEVEL-POLL] {ipport}: quiet={quiet2} clients={clients2.Count}");
                    m_fleetSilenceStatus[ipport] = quiet2;
                    m_fleetSlotLevels[ipport] = ll2.ToArray();
                    m_fleetClientLevels[ipport] = nl2;
                    m_fleetClientLevelsAt[ipport] = DateTime.UtcNow;

                    s_requiresPunch[ipport] = true; // confirmed OCI — punch every future poll
                    Console.WriteLine($"[LEVEL-POLL] {ipport}: marked as requiring hole-punch");
                }
                catch (OperationCanceledException)
                {
                    if (m_fleetSilenceStatus.TryRemove(ipport, out _))
                        Console.WriteLine($"[LEVEL-POLL] {ipport}: no response even with hole-punch (removed)");
                    m_fleetSlotLevels.TryRemove(ipport, out _); m_fleetClientLevels.TryRemove(ipport, out _); m_fleetClientLevelsAt.TryRemove(ipport, out _);
                    if (s_serverSockets.TryRemove(ipport, out var bs)) try { bs.Dispose(); } catch { }
                }
            }
            catch (OperationCanceledException)
            {
                if (m_fleetSilenceStatus.TryRemove(ipport, out _))
                    Console.WriteLine($"[LEVEL-POLL] {ipport}: no response (removed)");
                m_fleetSlotLevels.TryRemove(ipport, out _);
                m_fleetClientLevels.TryRemove(ipport, out _);
                m_fleetClientLevelsAt.TryRemove(ipport, out _);
                if (s_serverSockets.TryRemove(ipport, out var badSock))
                    try { badSock.Dispose(); } catch { }
            }
            catch (Exception ex)
            {
                if (m_fleetSilenceStatus.TryRemove(ipport, out _))
                    Console.WriteLine($"[LEVEL-POLL] {ipport}: {ex.GetType().Name} (removed)");
                m_fleetSlotLevels.TryRemove(ipport, out _);
                m_fleetClientLevels.TryRemove(ipport, out _);
                m_fleetClientLevelsAt.TryRemove(ipport, out _);
                if (s_serverSockets.TryRemove(ipport, out var badSock))
                    try { badSock.Dispose(); } catch { }
            }
        }

        // Parses a 1013 CLM_CONN_CLIENTS_LIST response body.
        // Record layout: ChannelId(1) + CountryId(2) + InstrumentId(4) + SkillLevel(1) + padding(4)
        //                + nameLen(2 LE) + name(nameLen) + cityLen(2 LE) + city(cityLen)
        static List<(int channelId, string name)> Parse1013Body(byte[] buf)
        {
            var result = new List<(int, string)>();
            // Frame header: TAG(2)+ID(2)+counter(1)+bodyLen(2) = 7 bytes; body at offset 7.
            if (buf.Length < 9) return result;
            if (!(buf[2] == 0xF5 && buf[3] == 0x03)) return result; // not 1013
            int bodyLen = buf[5] | buf[6] << 8;
            int pos = 7;
            int end = Math.Min(7 + bodyLen, buf.Length - 2);
            while (pos + 16 <= end) // minimum record = 12 fixed + 2+0 + 2+0 = 16
            {
                int channelId = buf[pos];
                pos += 12; // skip CountryId(2)+InstrumentId(4)+SkillLevel(1)+padding(4)+channelId already read
                if (pos + 2 > end) break;
                int nameLen = buf[pos] | buf[pos + 1] << 8;
                pos += 2;
                if (pos + nameLen > end) break;
                string name = System.Text.Encoding.UTF8.GetString(buf, pos, nameLen);
                pos += nameLen;
                if (pos + 2 > end) break;
                int cityLen = buf[pos] | buf[pos + 1] << 8;
                pos += 2 + cityLen;
                result.Add((channelId, name));
            }
            return result;
        }

        static async Task<List<string>> FetchThaiLoungeUrlsAsync()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                string body = await client.GetStringAsync("https://mjth.live/lounges.json");
                var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);
                return data?.Values.ToList() ?? new List<string>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Harvest] Failed to fetch Thai lounge list: {ex.Message}");
                return new List<string>();
            }
        }

        static async Task MonitorLoungeAsync(string loungeUrl, CancellationToken stoppingToken)
        {
            using var client = new HttpClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/event-stream");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cache-Control", "no-cache");

            string sseUrl = loungeUrl.TrimEnd('/') + "/events";
            string urlPattern = @"https://[\w\-\.]+(?::\d+)?(?:/[^\s]*)?";
            var clientNames = new List<string>();

            int retryDelay = 5;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    Console.WriteLine($"[Harvest] Connecting to {sseUrl}");
                    using var response = await client.GetAsync(
                        sseUrl, HttpCompletionOption.ResponseHeadersRead, stoppingToken);
                    using var streamReader = new StreamReader(
                        await response.Content.ReadAsStreamAsync(stoppingToken));

                    response.EnsureSuccessStatusCode();
                    retryDelay = 5;
                    clientNames = new List<string>();
                    while (!streamReader.EndOfStream && !stoppingToken.IsCancellationRequested)
                    {
                        var line = await streamReader.ReadLineAsync();
                        if (line == null || !line.StartsWith("data: "))
                            continue;

                        var jsonStr = line.Substring("data: ".Length);
                        JObject root;
                        try { root = JObject.Parse(jsonStr); }
                        catch (Newtonsoft.Json.JsonException) { continue; }

                        var clientsToken = root["clients"];
                        if (clientsToken != null)
                            clientNames = clientsToken.Select(c => c["name"]?.Value<string>() ?? "").ToList();

                        var levelsToken = root["levels"];
                        if (levelsToken != null)
                        {
                            var levels = levelsToken.Select(l => l.Value<int>()).ToList();
                            var musicianLevels = levels
                                .Where((_, i) => i < clientNames.Count && !LobbyPattern.IsMatch(clientNames[i]))
                                .ToList();
                            bool quiet = musicianLevels.Count == 0 || musicianLevels.All(l => l <= 2);
                            m_loungeIsQuiet[loungeUrl] = quiet;
                        }

                        var msgToken = root["newChatMessage"]?["message"];
                        if (msgToken == null) continue;
                        string chatText = TagCleaner.Replace(msgToken.Value<string>(), "");

                        Match match = Regex.Match(chatText, urlPattern);
                        if (!match.Success) continue;

                        string inlineURL = match.Value;
                        Console.WriteLine($"[Harvest:{new Uri(loungeUrl).Host}] URL: {inlineURL}");

                        if (!await UrlMatchesChatPatternsAsync(inlineURL))
                        {
                            AppendRejectedLog(inlineURL, "lounge", new Uri(loungeUrl).Host);
                            continue;
                        }
                        string loungeServer = JamulusAnalyzer.m_connectedLounges.FirstOrDefault(x => x.Value == loungeUrl).Key ?? "";
                        AppendAcceptedLog(inlineURL, "lounge", loungeServer);

                        string lowerUrl = inlineURL.ToLower();
                        if (lowerUrl.Contains("https://vdo.ninja/") ||
                            lowerUrl.Contains("https://meet.google.com/") ||
                            lowerUrl.Contains(".zoom.us/") ||
                            lowerUrl.Contains("https://meet.jit.si/"))
                        {
                            DiscreetLinkForServer(loungeUrl, inlineURL);
                        }

                        string title = await ScrapeTitleAsync(inlineURL);
                        AppendGuidLog(inlineURL, loungeServer, title);
                        if (title != null)
                        {
                            if (!s_ugTitleRegex.IsMatch(inlineURL))
                            {
                                int sep2 = title.IndexOf(" — ", StringComparison.Ordinal);
                                string scrapedSong2   = sep2 > 0 ? title.Substring(0, sep2)   : title;
                                string scrapedArtist2 = sep2 > 0 ? title.Substring(sep2 + 3)  : "";
                                File.AppendAllText("data/urls.csv",
                                    JamulusCacheManager.MinutesSince2023AsInt() + ",lounge,"
                                    + loungeServer + "," + System.Web.HttpUtility.UrlEncode(inlineURL) + ","
                                    + System.Web.HttpUtility.UrlEncode(scrapedSong2) + ","
                                    + System.Web.HttpUtility.UrlEncode(scrapedArtist2) + Environment.NewLine);
                            }
                            ShortLivedTitleForServer(title, inlineURL, loungeUrl);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Harvest:{new Uri(loungeUrl).Host}] {ex.Message}. Retrying in {retryDelay}s");
                    await Task.Delay(TimeSpan.FromSeconds(retryDelay), stoppingToken);
                    retryDelay = Math.Min(retryDelay * 2, 3600);
                    continue;
                }
                // clean EOF (server closed stream without error) — still back off
                if (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(retryDelay), stoppingToken);
                    retryDelay = Math.Min(retryDelay * 2, 300);
                }
            }
        }

        static async Task<string> ScrapeTitleAsync(string url)
        {
            try
            {
                // chords69cl: query Firebase RTDB directly — no HTML scrape needed.
                var chords69Match = Regex.Match(url, @"chords69cl\.vercel\.app/(?:shared-view/?)?\?room=([^&\s]+)", RegexOptions.IgnoreCase);
                if (chords69Match.Success)
                {
                    string room = Uri.UnescapeDataString(chords69Match.Groups[1].Value);
                    string encodedRoom = Uri.EscapeDataString(room);
                    string fbUrl = $"https://ai-art-b26e7-default-rtdb.asia-southeast1.firebasedatabase.app/rooms/{encodedRoom}/sharedView.json";
                    using var fbClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    string json = await fbClient.GetStringAsync(fbUrl);
                    var root = Newtonsoft.Json.Linq.JObject.Parse(json);
                    string title = root["title"]?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        int sep = title.IndexOf(" - ");
                        if (sep > 0) title = title.Substring(0, sep) + " \u2014 " + title.Substring(sep + 3);
                        Console.WriteLine("Title I'll publish: " + title);
                        return title;
                    }
                    return null;
                }

                // ultimate-guitar: extract title and artist from URL slug — site returns 403 to scrapers
                var ugMatch = s_ugTitleRegex.Match(url);
                if (ugMatch.Success)
                {
                    var tc = System.Globalization.CultureInfo.InvariantCulture.TextInfo;
                    string artist = tc.ToTitleCase(ugMatch.Groups[1].Value.Replace('-', ' ').ToLower());
                    string songTitle = tc.ToTitleCase(ugMatch.Groups[2].Value.Replace('-', ' ').ToLower());
                    string result = $"{songTitle} — {artist}";
                    Console.WriteLine("Title I'll publish: " + result);
                    return result;
                }

                using (HttpClient theclient = new HttpClient())
                {
                    theclient.DefaultRequestHeaders.TryAddWithoutValidation(
                        "User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
                    theclient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                    theclient.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.5");
                    theclient.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
                    theclient.DefaultRequestHeaders.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
                    string s = await theclient.GetStringAsync(url);
                    string title = null;

                    if (url.ToLower().Contains("https://busk.town/"))
                    {
                        Match m = Regex.Match(s, @"<title data-react-helmet=""true"">\s*(.+?)\s*</title>");
                        if (m.Success)
                        {
                            title = m.Groups[1].Value.Replace("คอร์ด", "").Replace(" - Busk", "");
                        }
                    }
                    else if (url.ToLower().Contains("https://chordtabs.in.th/"))
                    {
                        // Title format: "{song}คอร์ด | คอร์ด {song} {artist}"
                        Match m = Regex.Match(s, @"<title>\s*(.+?)\s*</title>");
                        if (m.Success)
                        {
                            string raw = m.Groups[1].Value;
                            var songMatch = Regex.Match(raw, @"^(.+?)คอร์ด");
                            string song = songMatch.Success ? songMatch.Groups[1].Value.Trim() : "";
                            string afterPipe = raw.Contains("|") ? raw.Split('|')[1].Trim() : "";
                            afterPipe = Regex.Replace(afterPipe, @"^คอร์ด\s*", "").Trim();
                            string artist = (song.Length > 0 && afterPipe.StartsWith(song))
                                ? afterPipe.Substring(song.Length).Trim() : "";
                            title = (song.Length > 0 && artist.Length > 0) ? $"{song} — {artist}"
                                  : (song.Length > 0 ? song : afterPipe);
                        }
                    }
                    else if (url.ToLower().Contains("https://designbetrieb.de/"))
                    {
                        Match m = Regex.Match(s, @"<TITLE>\s*(.+?)\s*</title>");
                        if (m.Success)
                        {
                            title = m.Groups[1].Value.Replace(".jpg", "");
                        }
                    }
                    else if (url.ToLower().Contains("https://www.follner-music.de/jamu/"))
                    {
                        Match m = Regex.Match(s, @"<title>\s*(.+?)\s*</title>");
                        if (m.Success)
                        {
                            title = m.Groups[1].Value;
                        }
                    }
                    else if (url.ToLower().Contains("https://www.guitarthai.com/"))
                    {
                        Match m = Regex.Match(s, @"<title>\s*(.+?)\s*</title>");
                        if (m.Success)
                        {
                            // Format: "คอร์ดเพลง <song> - <artist> คอร์ดง่าย ... | GuitarThai"
                            var inner = Regex.Match(m.Groups[1].Value, @"คอร์ดเพลง\s+(.+?)\s+คอร์ดง่าย");
                            if (inner.Success) title = inner.Groups[1].Value;
                        }
                    }
                    else if (url.ToLower().Contains("https://www.virtualsheetmusic.com/"))
                    {
                        Match m = Regex.Match(s, @"<meta[^>]+property=""og:title""[^>]+content=""([^""]+)""", RegexOptions.IgnoreCase);
                        if (!m.Success)
                            m = Regex.Match(s, @"<meta[^>]+content=""([^""]+)""[^>]+property=""og:title""", RegexOptions.IgnoreCase);
                        if (m.Success)
                            title = m.Groups[1].Value.Trim();
                    }
                    else if (url.ToLower().Contains("https://www.dochord.com/"))
                    {
                        Match m = Regex.Match(s, @"<title>\s*(.+?)\s*</title>");
                        if (m.Success)
                        {
                            // Format: "คอร์ดเพลง <song+artist> | dochord.com"
                            var inner = Regex.Match(m.Groups[1].Value, @"คอร์ดเพลง\s+(.+?)\s*\|");
                            if (inner.Success) title = inner.Groups[1].Value;
                        }
                    }
                    else if (Regex.IsMatch(url, @"guitarians\.com/", RegexOptions.IgnoreCase))
                    {
                        Match m = Regex.Match(s, @"<title>\s*(.+?)\s*</title>");
                        if (m.Success)
                        {
                            // Format: "{artist} - {song} 結他譜 Chord譜 ... | Guitarians.com"
                            string raw = m.Groups[1].Value;
                            int cut = raw.IndexOf(" 結他譜", StringComparison.Ordinal);
                            if (cut < 0) cut = raw.IndexOf(" |", StringComparison.Ordinal);
                            if (cut > 0) raw = raw.Substring(0, cut).Trim();
                            title = raw.Replace(" - ", " — ");
                        }
                    }
                    else if (Regex.IsMatch(url, @"jimsrootsandblues\.com/", RegexOptions.IgnoreCase))
                    {
                        Match m = Regex.Match(s, @"<title>\s*(.+?)\s*</title>", RegexOptions.IgnoreCase);
                        if (m.Success)
                        {
                            // Format: "Song Name – Jim's Roots &amp; Blues"
                            string raw = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                            int sep = raw.IndexOf(" – ", StringComparison.Ordinal); // en dash
                            if (sep > 0) raw = raw.Substring(0, sep).Trim();
                            title = raw;
                        }
                    }
                    else if (Regex.IsMatch(url, @"thesession\.org/", RegexOptions.IgnoreCase))
                    {
                        Match m = Regex.Match(s, @"<title>\s*(.+?)\s*</title>", RegexOptions.IgnoreCase);
                        if (m.Success)
                        {
                            // Format: "Tune Name (type) on The Session"
                            string raw = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                            const string suffix = " on The Session";
                            if (raw.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                                raw = raw.Substring(0, raw.Length - suffix.Length).Trim();
                            title = raw;
                        }
                    }

                    if (title != null)
                    {
                        Console.WriteLine("Title I'll publish: " + title);
                        return title;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error scraping title from {url}: {ex.Message}");
            }
            return null;
        }

        static async Task IngestURL(string server, string url)
        {
            string title = await ScrapeTitleAsync(url);
            if (title != null)
            {
                ShortLivedTitleForServerAtAddr(title, server);
            }
        }

    }
}


