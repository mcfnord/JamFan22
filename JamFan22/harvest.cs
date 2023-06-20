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
        public static int m_minuteOfLastActivity = 0;

        static void DiscreetLinkForServer(string url) // ttl isn't built in
        {
            string where = JamFan22.Pages.IndexModel.m_connectedLounges["https://hear.jamulus.live"];
            m_discreetLinks[where] = url;

            m_minuteOfLastActivity = JamFan22.Pages.IndexModel.MinuteSince2023AsInt() + 3;

        }

        static void ShortLivedTitleForServer(string title)
        {
            if (title.Length > 0) // when i do get an empty title, don't overwrite.
            {
                string where = JamFan22.Pages.IndexModel.m_connectedLounges["https://hear.jamulus.live"];
                m_songTitle[where] = title;

                m_minuteOfLastActivity = JamFan22.Pages.IndexModel.MinuteSince2023AsInt() + 2;
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
                    Console.WriteLine("Establishing connection");
                    using (var streamReader = new StreamReader(await client.GetStreamAsync(url)))
                    {
                        while (!streamReader.EndOfStream)
                        {
                            // Every new minute, i might kill some entries
                            if(JamFan22.Pages.IndexModel.MinuteSince2023AsInt() > m_minuteOfLastActivity)
                            {
                                m_minuteOfLastActivity = JamFan22.Pages.IndexModel.MinuteSince2023AsInt();
                                var rng = new Random();

                                for (int iPos = m_songTitle.Count - 1; iPos >= 0; iPos--)
                                {
                                    // 1:3 odds that I remove this entry
                                    if (0 == rng.Next(3))
                                        m_songTitle.Remove(m_songTitle.ElementAt(iPos).Key);
                                }

                                for(int iPos = m_discreetLinks.Count - 1; iPos >= 0; iPos--)
                                {
                                    // 1:4 odds that I remove this entry
                                    if (0 == rng.Next(4))
                                        m_discreetLinks.Remove(m_discreetLinks.ElementAt(iPos).Key);
                                }
                            }

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
                                    if (inlineURL.ToLower().Contains("https://us02web.zoom.us/"))
                                    {
                                        DiscreetLinkForServer(inlineURL);
                                    }
                                    if (inlineURL.ToLower().Contains("https://meet.jit.si/"))
                                    {
                                        DiscreetLinkForServer(inlineURL);
                                    }


                                    if (inlineURL.Contains("https://tabs.ultimate-guitar.com/"))
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
                                                title = title.Replace("by Misc", "");
                                                title = title.Replace("Soundtrack", "");
                                                title = title.Replace("Ultimate Guitar Pro - Play like a Pro", "");
                                                title = title.Replace("at Ultimate-Guitar", "");
                                                title = title.Replace("ACOUSTIC", "");
                                                title = title.Replace("for guitar, ukulele, piano", "");
                                                Console.WriteLine("Title I'll publish: " + title);

                                                // Show to all, but let it live for just 3 minutes. (probably shown once, maybe twice)
                                                //m_liveTitles.Add(title, DateTime.Now.AddMinutes(10)); // hmmm, TTL on birth?
                                                ShortLivedTitleForServer(title); //, DateTime.Now.AddMinutes(3)); // hmmm, TTL on birth?
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