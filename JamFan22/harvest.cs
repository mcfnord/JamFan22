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
            @"(?:[a-z]{2}\.)?(?:tabs\.)?ultimate-guitar\.com/tab/([^/]+)/(.+?)(?:-(chords?|tabs?|bass-tabs?|ukulele|drum-tabs?|power-tabs?|guitar-pro|official|fingerstyle|classical))?-\d+$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static void AppendAcceptedLog(string url, string source, string serverAddr)
        {
            string title = "";
            var ugMatch = s_ugTitleRegex.Match(url);
            if (ugMatch.Success)
            {
                var tc = System.Globalization.CultureInfo.InvariantCulture.TextInfo;
                string artist = tc.ToTitleCase(ugMatch.Groups[1].Value.Replace('-', ' ').ToLower());
                string songTitle = tc.ToTitleCase(ugMatch.Groups[2].Value.Replace('-', ' ').ToLower());
                title = $"{songTitle} — {artist}";
            }
            File.AppendAllText("data/urls.csv",
                JamulusCacheManager.MinutesSince2023AsInt() + "," + source + ","
                + serverAddr + "," + System.Web.HttpUtility.UrlEncode(url) + ","
                + System.Web.HttpUtility.UrlEncode(title) + Environment.NewLine);
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
            if (title != null)
            {
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
            m_songTitleExpiry[key] = DateTime.UtcNow.AddMinutes(8);

        }

        static void ShortLivedTitleForServerAtAddr(string title, string serverAddr)
        {
            if (title.Length > 0)
            {
                m_songTitleAtAddr[serverAddr] = title;
                m_songTitleExpiry[serverAddr] = DateTime.UtcNow.AddMinutes(8);
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

                    retryDelay = 5;
                    clientNames = new List<string>();
                    response.EnsureSuccessStatusCode();
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
                        if (title != null)
                            ShortLivedTitleForServer(title, inlineURL, loungeUrl);
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


