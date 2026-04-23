// VIDEO URL SYSTEM
// Video URLs are stored in m_discreetLinks (keyed by ip:port). Three ingest sources:
//   1. Lounge monitor instances
//   2. Fleet servers via POST /chat-url-server (raw text match)
//   3. Custom client via POST /chat-url-client
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

        public static ConcurrentDictionary<string, (string Url, DateTime Stored)> m_discreetLinks = new();
        public static Dictionary<string, string> m_songTitle = new Dictionary<string, string>();
        public static Dictionary<string, string> m_songTitleAtAddr = new Dictionary<string, string>();
        public static int m_timeToLive = 0;

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

        public static async Task IngestChatUrlAsync(string url, string serverAddr)
        {
            string lowerUrl = url.ToLower();
            if (lowerUrl.Contains("vdo.ninja") || lowerUrl.Contains("meet.google.com") ||
                lowerUrl.Contains(".zoom.us") || lowerUrl.Contains("meet.jit.si"))
            {
                m_discreetLinks[serverAddr] = (url, DateTime.UtcNow);
                m_timeToLive = JamulusCacheManager.MinutesSince2023AsInt() + 3;
                Console.WriteLine($"[Harvest] Discreet link at {serverAddr}: {url}");
            }

            string title = await ScrapeTitleAsync(url);
            if (title != null)
                ShortLivedTitleForServerAtAddr(title, serverAddr.Replace(':', '-'));
        }

        static void DiscreetLinkForServer(string loungeUrl, string url)
        {
            if (!JamulusAnalyzer.m_connectedLounges.TryGetValue(loungeUrl, out string where))
                return;
            m_discreetLinks[where] = (url, DateTime.UtcNow);
            m_timeToLive = JamulusCacheManager.MinutesSince2023AsInt() + 3;
        }

        static void ShortLivedTitleForServer(string title, string url, string loungeUrl)
        {
            if (title.Length == 0) return;
            string where = "1.2.3.4";
            if (!JamulusCacheManager.IsDebuggingOnWindows)
            {
                if (!JamulusAnalyzer.m_connectedLounges.TryGetValue(loungeUrl, out where))
                {
                    Console.WriteLine($"[Harvest] WARN: no IP mapping for lounge '{loungeUrl}' (keys: {string.Join(", ", JamulusAnalyzer.m_connectedLounges.Keys)})");
                    return;
                }
            }
            Console.WriteLine($"[Harvest] Storing title '{title}' at key '{where.Replace(':', '-')}'");
            m_songTitleAtAddr[where.Replace(':', '-')] = title;
            m_timeToLive = JamulusCacheManager.MinutesSince2023AsInt() + 5;
            System.IO.File.AppendAllText("data/urls.csv",
                JamulusCacheManager.MinutesSince2023AsInt() + ","
                + where + ","
                + System.Web.HttpUtility.UrlEncode(url)
                + Environment.NewLine);
        }

        static void ShortLivedTitleForServerAtAddr(string title, string serverAddr)
        {
            if (title.Length > 0)
            {
                m_songTitleAtAddr[serverAddr] = title;
                m_timeToLive = JamulusCacheManager.MinutesSince2023AsInt() + 10;
            }
        }

        static readonly List<string> m_staticLounges = new List<string>
        {
            "https://lobby.jam.voixtel.net.br/",
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
                if (JamulusCacheManager.MinutesSince2023AsInt() > m_timeToLive)
                {
                    m_timeToLive = JamulusCacheManager.MinutesSince2023AsInt();
                    var rng = new Random();
                    for (int i = m_songTitleAtAddr.Count - 1; i >= 0; i--)
                        if (0 == rng.Next(3)) m_songTitleAtAddr.Remove(m_songTitleAtAddr.ElementAt(i).Key);
                    var expireBefore = DateTime.UtcNow.AddMinutes(-20);
                    foreach (var key in m_discreetLinks.Keys.ToList())
                        if (m_discreetLinks.TryGetValue(key, out var entry) && entry.Stored < expireBefore)
                            m_discreetLinks.TryRemove(key, out _);
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

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    Console.WriteLine($"[Harvest] Connecting to {sseUrl}");
                    using var response = await client.GetAsync(
                        sseUrl, HttpCompletionOption.ResponseHeadersRead, stoppingToken);
                    using var streamReader = new StreamReader(
                        await response.Content.ReadAsStreamAsync(stoppingToken));

                    while (!streamReader.EndOfStream && !stoppingToken.IsCancellationRequested)
                    {
                        var line = await streamReader.ReadLineAsync();
                        if (line == null || !line.StartsWith("data: "))
                            continue;

                        var jsonStr = line.Substring("data: ".Length);
                        string chatText;
                        try
                        {
                            var root = JObject.Parse(jsonStr);
                            var msgToken = root["newChatMessage"]?["message"];
                            if (msgToken == null) continue;
                            chatText = TagCleaner.Replace(msgToken.Value<string>(), "");
                        }
                        catch (Newtonsoft.Json.JsonException) { continue; }

                        Match match = Regex.Match(chatText, urlPattern);
                        if (!match.Success) continue;

                        string inlineURL = match.Value;
                        Console.WriteLine($"[Harvest:{new Uri(loungeUrl).Host}] URL: {inlineURL}");

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
                    Console.WriteLine($"[Harvest:{new Uri(loungeUrl).Host}] {ex.Message}. Retrying in 5s");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }

        static async Task<string> ScrapeTitleAsync(string url)
        {
            try
            {
                // chords69cl: query Firebase RTDB directly — no HTML scrape needed.
                var chords69Match = Regex.Match(url, @"chords69cl\.vercel\.app/shared-view\?room=([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase);
                if (chords69Match.Success)
                {
                    string room = chords69Match.Groups[1].Value;
                    string fbUrl = $"https://ai-art-b26e7-default-rtdb.asia-southeast1.firebasedatabase.app/rooms/{room}/sharedView.json";
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
                var ugMatch = Regex.Match(url, @"tabs\.ultimate-guitar\.com/tab/([^/]+)/(.+?)(?:-(chords?|tabs?|bass-tabs?|ukulele|drum-tabs?|power-tabs?|guitar-pro|official|fingerstyle|classical))?-\d+$", RegexOptions.IgnoreCase);
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
                        Match m = Regex.Match(s, @"<title>\s*(.+?)\s*</title>");
                        if (m.Success)
                        {
                            title = m.Groups[1].Value.Replace("คอร์ด", "");
                            if (title.Contains("|"))
                            {
                                title = title.Split('|')[1].Trim();
                            }
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
                    else if (url.ToLower().Contains("https://tabs.ultimate-guitar.com/tab/"))
                    {
                        // URL format: /tab/{artist}/{song-slug}-{type}-{id}
                        // Parse directly — UG blocks server-side HTTP requests with 403.
                        var m = Regex.Match(url, @"/tab/([^/]+)/(.+)-(?:chords|tabs?|bass|ukulele|drums|guitar_pro|power_tab|official)-\d+", RegexOptions.IgnoreCase);
                        if (m.Success)
                        {
                            string artist = m.Groups[1].Value.Replace("-", " ");
                            string song = m.Groups[2].Value.Replace("-", " ");
                            artist = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(artist);
                            song = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(song);
                            title = $"{song} — {artist}";
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
