using JamFan22.Pages;
using Newtonsoft.Json.Linq;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.ConstrainedExecution;
using System.Text.RegularExpressions;

namespace JamFan22
{

    public class forbidder
    {
        public static Dictionary<string, string> m_dockRequestor = new Dictionary<string, string>();
        public static List<string> m_forbiddenIsp = new List<string>();

//        public static List<string> m_forbade = new List<string>();
//        protected static int hourForbade = -1 ;

        public static Task ForbidThem(HttpContext context, string ip)
        {
            Console.WriteLine("Forbidden act.");
            context.Response.StatusCode = 403;
            return context.Response.WriteAsync("Forbidden");
        }
    }



    public class hashSupport
    {
        public static string GetIpPort(string destinationHash)
        {
            foreach (var key in JamFan22.Pages.IndexModel.JamulusListURLs.Keys)
            {
                var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(JamFan22.Pages.IndexModel.LastReportedList[key]);
                foreach (var server in serversOnList)
                {
                    byte[] preHash1 = System.Text.Encoding.UTF8.GetBytes(server.ip + ":" + server.port + DateTime.UtcNow.Hour);
                    var interimStep1 = System.Security.Cryptography.MD5.HashData(preHash1);
                    var saltedHash1 = JamFan22.Pages.IndexModel.ToHex(interimStep1, false).Substring(0, 4);

                    var priorHour = DateTime.UtcNow.Hour - 1;
                    if (-1 == priorHour)
                        priorHour = 23;
                    byte[] preHash2 = System.Text.Encoding.UTF8.GetBytes(server.ip + ":" + server.port + priorHour);
                    var interimStep2 = System.Security.Cryptography.MD5.HashData(preHash2);
                    var saltedHash2 = JamFan22.Pages.IndexModel.ToHex(interimStep2, false).Substring(0, 4);

                    if ((saltedHash1 == destinationHash) || (saltedHash2 == destinationHash))
                    {
                        if (saltedHash1 == destinationHash)
                            Console.WriteLine("Salt from current hour.");
                        else
                            Console.WriteLine("Salt from previous hour.");

                        Console.WriteLine("Dock request matched " + server.ip + ":" + server.port + "... " + server.name );
                        var deHashedDestination = server.ip + ":" + server.port;
                        return deHashedDestination;
                    }
                }
            }
            return null;
        }
    }


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
        public static int m_minuteOfLastActivity = 0;

        static void DiscreetLinkForServer(string url) // ttl isn't built in
        {
            string where = JamFan22.Pages.IndexModel.m_connectedLounges["https://hear.jamulus.live"];
            m_discreetLinks[where] = url;

            m_minuteOfLastActivity = JamFan22.Pages.IndexModel.MinutesSince2023AsInt() + 3;

        }

        static void ShortLivedTitleForServer(string title, string url)
        {
            if (title.Length > 0) // when i do get an empty title, don't overwrite.
            {
                string where = "1.2.3.4";
                if(false == JamFan22.Pages.IndexModel.IsDebuggingOnWindows)
                    where = JamFan22.Pages.IndexModel.m_connectedLounges["https://hear.jamulus.live"];
                m_songTitle[where] = title;

                m_minuteOfLastActivity = JamFan22.Pages.IndexModel.MinutesSince2023AsInt() + 2;

                System.IO.File.AppendAllText("data/urls.csv",
                    JamFan22.Pages.IndexModel.MinutesSince2023AsInt() + ","
                    + where + ","
                    + System.Web.HttpUtility.UrlEncode(url)
                    + Environment.NewLine);
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
                    // Relocating this to the top of the loop, so that it's not dependent on the stream, which hangs.
                    // Every new minute, i might kill some entries
                    if (JamFan22.Pages.IndexModel.MinutesSince2023AsInt() > m_minuteOfLastActivity)
                    {
                        m_minuteOfLastActivity = JamFan22.Pages.IndexModel.MinutesSince2023AsInt();
                        var rng = new Random();

                        for (int iPos = m_songTitle.Count - 1; iPos >= 0; iPos--)
                        {
                            // 1:3 odds that I remove this entry
                            if (0 == rng.Next(3))
                                m_songTitle.Remove(m_songTitle.ElementAt(iPos).Key);
                        }

                        for (int iPos = m_discreetLinks.Count - 1; iPos >= 0; iPos--)
                        {
                            // 1:4 odds that I remove this entry
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
                            //                        Console.WriteLine($"{message}");
                            message = message.Replace("data: ", "");
                            if (message.Contains("newChatMessage"))
                            {
                                message = message.Replace("{\"newChatMessage\":", "");
                                message = message.Replace("}}", "}");
                                var eventMessage = System.Text.Json.JsonSerializer.Deserialize<ChatMessage>(message);

                                // Get all string after the second instance of a close-bracket
                                var iPosStart = eventMessage.message.LastIndexOf("]") + 2;
                                var chatSubstance = eventMessage.message.Substring(iPosStart);

                                string pattern = @"https:\/\/[\w\-\.]+(\:\d+)?(\/[^\s]*)?";

                                Match match = Regex.Match(chatSubstance, pattern);

                                if (match.Success)
                                {
                                    string inlineURL = match.Value;
                                    Console.WriteLine("URL TO SNIFF: " + inlineURL);

                                    if (inlineURL.ToLower().Contains("https://vdo.ninja/"))
                                    {
                                        DiscreetLinkForServer(inlineURL);
                                    }

                                    if (inlineURL.ToLower().Contains("https://meet.google.com/"))
                                    {
                                        DiscreetLinkForServer(inlineURL);
                                    }
                                    if (inlineURL.ToLower().Contains(".zoom.us/"))
                                    {
                                        DiscreetLinkForServer(inlineURL);
                                    }
                                    if (inlineURL.ToLower().Contains("https://meet.jit.si/"))
                                    {
                                        DiscreetLinkForServer(inlineURL);
                                    }

                                    if (inlineURL.ToLower().Contains("https://chordtabs.in.th/"))
                                    {
                                        using (HttpClient theclient = new HttpClient())
                                        {
                                            string s = await theclient.GetStringAsync(inlineURL);

                                            Match m = Regex.Match(s, @"<title>\s*(.+?)\s*</title>");
                                            if (m.Success)
                                            {
                                                var title = m.Groups[1].Value;
                                                Console.WriteLine("Title I'll publish: " + title);
                                                ShortLivedTitleForServer(title, inlineURL); 
                                            }
                                        }
                                    }


                                    if (inlineURL.ToLower().Contains("https://designbetrieb.de/"))
                                    {
                                        using (HttpClient theclient = new HttpClient())
                                        {
                                            string s = await theclient.GetStringAsync(inlineURL);

                                            Match m = Regex.Match(s, @"<TITLE>\s*(.+?)\s*</title>");
                                            if (m.Success)
                                            {
                                                var title = m.Groups[1].Value;
                                                title = title.Replace(".jpg", "");
                                                Console.WriteLine("Title I'll publish: " + title);
                                                ShortLivedTitleForServer(title, inlineURL);
                                            }
                                        }
                                    }

                                    if (inlineURL.ToLower().Contains("https://www.follner-music.de/Jamu/"))
                                    {
                                        using (HttpClient theclient = new HttpClient())
                                        {
                                            string s = await theclient.GetStringAsync(inlineURL);

                                            // test with https://www.follner-music.de/Jamu/Hold_on_to_me.pdf
                                            Match m = Regex.Match(s, @"<title>\s*(.+?)\s*</title>");

                                            if (m.Success)
                                            {
                                                var title = m.Groups[1].Value;
                                                Console.WriteLine("Title I'll publish: " + title);
                                                ShortLivedTitleForServer(title, inlineURL);
                                            }
                                        }
                                    }



                                    if (inlineURL.ToLower().Contains("https://tabs.ultimate-guitar.com/"))
                                    {

                                        /*
                                        IWebDriver driver = new ChromeDriver();
                                        driver.Navigate().GoToUrl(inlineURL);
                                        var title = driver.FindElement(By.CssSelector("h1"));
                                        Console.WriteLine(title);
                                        */

                                        using (HttpClient theclient = new HttpClient())
                                        {
                                            string s = await theclient.GetStringAsync(inlineURL);

                                            Match m = Regex.Match(s, @"<title>\s*(.+?)\s*</title>");
                                            if (m.Success)
                                            {
                                                var title = m.Groups[1].Value;
                                                title = title.Replace("@ Ultimate - Guitar.Com", "");
                                                title = title.Replace("@ Ultimate-Guitar.Com", "");
                                                title = title.Replace("@ Ultimate-Guitar.com", "");
                                                title = title.Replace("CHORDS", "");
                                                title = title.Replace("(ver 2)", "");
                                                title = title.Replace("(ver 3)", "");
                                                title = title.Replace("(ver 4)", "");
                                                title = title.Replace("(ver 5)", "");
                                                title = title.Replace("(ver 6)", "");
                                                title = title.Replace("(ver 7)", "");
                                                title = title.Replace("(ver 8)", "");
                                                title = title.Replace("(ver 9)", "");
                                                title = title.Replace("(ver 10)", "");
                                                title = title.Replace("(ver 11)", "");
                                                title = title.Replace("by Misc", "");
                                                title = title.Replace("Soundtrack", "");
                                                title = title.Replace(" TAB", "");
                                                title = title.Replace("Ultimate Guitar Pro - Play like a Pro", "");
                                                title = title.Replace("at Ultimate-Guitar", "");
                                                title = title.Replace("ACOUSTIC", "");
                                                title = title.Replace("for guitar, ukulele, piano", "");
                                                title = title.Replace("Chords & Lyrics", "");
                                                title = title.Replace("Tabs & Lyrics", "");
                                                title = title.Replace("UNNAMED ARTIST — ", "");
                                                title = title.Replace("Originals", "");
                                                title = title.Replace("by Praise and Harmony", "");
                                                Console.WriteLine("Title I'll publish: " + title);

                                                // Show to all, but let it live for just 3 minutes. (probably shown once, maybe twice)
                                                //m_liveTitles.Add(title, DateTime.Now.AddMinutes(10)); // hmmm, TTL on birth?
                                                ShortLivedTitleForServer(title, inlineURL); //, DateTime.Now.AddMinutes(3)); // hmmm, TTL on birth?
                                            }
                                        }
                                    }
                                }
                                else
                                {
//                                   Console.WriteLine("No HTTPS URL found in the string.");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    //Here you can check for 
                    //specific types of errors before continuing
                    //Since this is a simple example, i'm always going to retry
                    Console.WriteLine($"Error: {ex.Message}");
                    Console.WriteLine("Retrying in 5 seconds");
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }
        }
    }
}
