using JamFan22.Pages;
using JamFan22.Models;
using Newtonsoft.Json.Linq;
using System;
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

        public static Dictionary<string, string> m_discreetLinks = new Dictionary<string, string>();
        public static Dictionary<string, string> m_songTitle = new Dictionary<string, string>();
        public static Dictionary<string, string> m_songTitleAtAddr = new Dictionary<string, string>();
        public static int m_timeToLive = 0;

        static void DiscreetLinkForServer(string url)
        {
            string where = JamFan22.Pages.IndexModel.m_connectedLounges["https://hear.jamulus.live"];
            m_discreetLinks[where] = url;

            m_timeToLive = JamFan22.Pages.IndexModel.MinutesSince2023AsInt() + 3;
        }

        static void ShortLivedTitleForServer(string title, string url)
        {
            if (title.Length > 0)
            {
                string where = "1.2.3.4";
                if(false == JamFan22.Pages.IndexModel.IsDebuggingOnWindows)
                    where = JamFan22.Pages.IndexModel.m_connectedLounges["https://hear.jamulus.live"];
                m_songTitle[where] = title;

                m_timeToLive = JamFan22.Pages.IndexModel.MinutesSince2023AsInt() + 2;

                System.IO.File.AppendAllText("data/urls.csv",
                    JamFan22.Pages.IndexModel.MinutesSince2023AsInt() + ","
                    + where + ","
                    + System.Web.HttpUtility.UrlEncode(url)
                    + Environment.NewLine);
            }
        }

        static void ShortLivedTitleForServerAtAddr(string title, string serverAddr)
        {
            if (title.Length > 0)
            {
                m_songTitleAtAddr[serverAddr] = title;
                m_timeToLive = JamFan22.Pages.IndexModel.MinutesSince2023AsInt() + 10;
            }
        }

        static Dictionary<string, string> m_lastLineMap = new Dictionary<string, string>();

        public static async Task HarvestLoop2025(CancellationToken stoppingToken)
        {
            await Task.Delay(60 * 1000, stoppingToken);

            HttpClient httpClient = new HttpClient();

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(5000, stoppingToken);
            }
        }

        static async Task<string> ScrapeTitleAsync(string url)
        {
            try
            {
                using (HttpClient theclient = new HttpClient())
                {
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
                    else if (url.ToLower().Contains("https://tabs.ultimate-guitar.com/"))
                    {
                        Match m = Regex.Match(s, @"<title>\s*(.+?)\s*</title>");
                        if (m.Success)
                        {
                            title = m.Groups[1].Value
                                .Replace("@ Ultimate - Guitar.Com", "")
                                .Replace("@ Ultimate-Guitar.Com", "")
                                .Replace("@ Ultimate-Guitar.com", "")
                                .Replace("CHORDS", "")
                                .Replace("(ver 2)", "")
                                .Replace("(ver 3)", "")
                                .Replace("(ver 4)", "")
                                .Replace("(ver 5)", "")
                                .Replace("(ver 6)", "")
                                .Replace("(ver 7)", "")
                                .Replace("(ver 8)", "")
                                .Replace("(ver 9)", "")
                                .Replace("(ver 10)", "")
                                .Replace("(ver 11)", "")
                                .Replace("by Misc", "")
                                .Replace("Soundtrack", "")
                                .Replace(" TAB", "")
                                .Replace("Ultimate Guitar Pro - Play like a Pro", "")
                                .Replace("at Ultimate-Guitar", "")
                                .Replace("ACOUSTIC", "")
                                .Replace("for guitar, ukulele, piano", "")
                                .Replace("Chords & Lyrics", "")
                                .Replace("Tabs & Lyrics", "")
                                .Replace("UNNAMED ARTIST — ", "")
                                .Replace("Originals", "")
                                .Replace("by Praise and Harmony", "");
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

        public static async Task HarvestLoop()
        {
            HttpClient client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            string url = $"http://hear.jamulus.live:88/events";

            while (true)
            {
                try
                {
                    if (JamFan22.Pages.IndexModel.MinutesSince2023AsInt() > m_timeToLive)
                    {
                        m_timeToLive = JamFan22.Pages.IndexModel.MinutesSince2023AsInt();
                        var rng = new Random();

                        for (int iPos = m_songTitle.Count - 1; iPos >= 0; iPos--)
                        {
                            if (0 == rng.Next(3))
                                m_songTitle.Remove(m_songTitle.ElementAt(iPos).Key);
                        }

                        for (int iPos = m_discreetLinks.Count - 1; iPos >= 0; iPos--)
                        {
                            if (0 == rng.Next(4))
                                m_discreetLinks.Remove(m_discreetLinks.ElementAt(iPos).Key);
                        }
                    }

                    Console.WriteLine("Establishing connection");
                    using (var streamReader = new StreamReader(await client.GetStreamAsync(url)))
                    {
                        while (!streamReader.EndOfStream)
                        {
                            var message = await streamReader.ReadLineAsync();
                            message = message.Replace("data: ", "");
                            if (message.Contains("newChatMessage"))
                            {
                                message = message.Replace("{\"newChatMessage\":", "");
                                message = message.Replace("}}", "}");
                                var eventMessage = System.Text.Json.JsonSerializer.Deserialize<ChatMessage>(message);

                                var iPosStart = eventMessage.message.LastIndexOf("]") + 2;
                                var chatSubstance = eventMessage.message.Substring(iPosStart);

                                string pattern = @"https:\/\/[\w\-\.]+(\:\d+)?(\/[^\s]*)?";
                                Match match = Regex.Match(chatSubstance, pattern);

                                if (match.Success)
                                {
                                    string inlineURL = match.Value;
                                    Console.WriteLine("URL TO SNIFF: " + inlineURL);

                                    string lowerUrl = inlineURL.ToLower();
                                    if (lowerUrl.Contains("https://vdo.ninja/") || 
                                        lowerUrl.Contains("https://meet.google.com/") || 
                                        lowerUrl.Contains(".zoom.us/") || 
                                        lowerUrl.Contains("https://meet.jit.si/"))
                                    {
                                        DiscreetLinkForServer(inlineURL);
                                    }
                                    
                                    string title = await ScrapeTitleAsync(inlineURL);
                                    if (title != null)
                                    {
                                        ShortLivedTitleForServer(title, inlineURL);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                    Console.WriteLine("Retrying in 5 seconds");
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }
        }
    }
}
