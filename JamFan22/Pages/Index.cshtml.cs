// #define WINDOWS

// testing

// using IPGeolocation;
using JamFan22.Models;
using JamFan22.Services;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Microsoft.VisualBasic;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq.Expressions;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using static System.Reflection.Metadata.BlobBuilder;
using static System.Runtime.InteropServices.JavaScript.JSType;
// using MongoDB.Driver;

namespace JamFan22.Pages
{
    public partial class IndexModel : PageModel
    {
        public static bool IsDebuggingOnWindows
        {
            get
            {
#if WINDOWS
                return true;
#else
                return false ;
#endif
            }
        }

// --- PASTE THIS AT THE TOP OF THE CLASS ---
private static readonly SemaphoreSlim _logLock = new SemaphoreSlim(1, 1);


// --- PASTE THIS HELPER METHOD ANYWHERE ---
/*
private async Task LogVisitAsync(string ip, string countryCode)
{
    // FILTER: Only log if the country is Thailand ("TH")
    if (countryCode != "TH") return;

    // Format: "2025-12-31 14:00:05, 192.168.1.5, TH"
    string logLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}, {ip}, {countryCode}{Environment.NewLine}";

    // Thread-safe write
    await _logLock.WaitAsync();
    try
    {
        await System.IO.File.AppendAllTextAsync(LogFilePath, logLine);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Logging failed: {ex.Message}");
    }
    finally
    {
        _logLock.Release();
    }
}
*/




        private readonly ILogger<IndexModel> _logger;
        private readonly GeolocationService _geoService;

        public IndexModel(ILogger<IndexModel> logger, GeolocationService geoService)
        {
            _logger = logger;
            _geoService = geoService;
        }

        //public void OnGet()         {         }
        // ADD THIS NEW PROPERTY
        public string IpHashForView { get; private set; }

        // REPLACE your 'public void OnGet()' or 'public IActionResult OnGet()'
        // with this 'public async Task<IActionResult> OnGetAsync()'
public async Task<IActionResult> OnGetAsync()
{
    // 1. Start the stopwatch to measure total request cost
    var sw = Stopwatch.StartNew();

    // 2. Severe Serialization: Wait here. 
    // This creates a single-file line. Only one user passes this point at a time.
    await m_serializerMutex.WaitAsync();
    
    try
    {
        // 3. Perform the heavy lifting while we hold the "conch shell"
        // We call the methods directly. We will REMOVE the locks inside these methods
        // in the next steps to avoid deadlocks.
        IpHashForView = await GetIPDerivedHashAsync();

        RightNowForView = await GetRightNowAsync();

        ShowServerByIPPortForView = await GetShowServerByIPPortAsync();
    }
    finally
    {
        // 4. Release the lock immediately so the next user in line can proceed
        m_serializerMutex.Release();
        
        sw.Stop();

        // 5. Diagnostic: If a request takes > 500ms, log it.
        // This tells you exactly which "occasional processes" are clogging the CPU.
        if (sw.ElapsedMilliseconds > 500)
        {
            Console.WriteLine($"[SLOW REQUEST] Serialization Duration: {sw.ElapsedMilliseconds}ms");
        }
    }

    return Page();
}


        // Detect every joiner, and using key of actor->target, add IP:PORT to hashset
        // Then I will light up targets this actor joins.
        // i will know how many unique where actor joined target,
        // a strong indicator of following.

        public static JamulusServers? GetServer(List<JamulusServers> serverList, string ip, long port)
        {
            foreach (var server in serverList)
            {
                if (server.ip == ip)
                    if (server.port == port)
                        return server;
            }
            return null;
        }

        public static bool SameGuy(Client guyBefore, Client guyAfter)
        {
            if (guyBefore.name == guyAfter.name)
                if (guyBefore.instrument == guyAfter.instrument)
                    if (guyBefore.country == guyAfter.country)
                        return true;
            return false;
        }

        public static Client[] Joined(Client[] before, Client[] after)
        {
            List<Client> joiners = new List<Client>();
            foreach (var guyAfter in after)
            {
                bool fSeenBefore = false;
                foreach (var guyBefore in before)
                {
                    if (SameGuy(guyBefore, guyAfter))
                    {
                        fSeenBefore = true;
                        break;
                    }
                }
                if (false == fSeenBefore)
                {
                    joiners.Add(guyAfter);
                }
            }
            return joiners.ToArray();
        }

        public static string ToHex(byte[] bytes, bool upperCase)
        {
            StringBuilder result = new StringBuilder(bytes.Length * 2);

            for (int i = 0; i < bytes.Length; i++)
                result.Append(bytes[i].ToString(upperCase ? "X2" : "x2"));

            return result.ToString();
        }


        // I AM PROBABLY CALLING THIS AT THE WRONG POINT. and isnow isn't used! one-time update call?

        /* EXPERIMENTAL CODE
        public static void DetectLeavers(string isnow)
        {
            // Detect leavers by finding the most recent timestamp of someone NOT on that server.
            // Older than 2.5 minutes? Discard.
            // Young enough?
            // Then look for them on any OTHER server.
            // If found elsewhere, and just left here,
            // In a dictionary for this server's IP:PORT as key, add just that name?
            // I kinda want "all the names" but this is much simpler.
            // Just most recent leaver (if within last 2.5 mins)
            // it'll be kool

            var leavers = new Dictionary<string, List<string>>();

            foreach (var key in JamulusListURLs.Keys)
            {
                if (false == LastReportedList.ContainsKey(key))
                    continue;
                var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(LastReportedList[key]);
                foreach (var server in serversOnList)
                {
                    if (server.clients != null)
                    {
                        foreach (var guy in server.clients)
                        {
                            var stringHashOfGuy = GetHash(guy.name, guy.country, guy.instrument);

                            // so i grab every user guid in sequence? and for each I ask if they're on any other server's bye!?
                            // yeah, by searching for the guid in the keys!
                            // if more than 1, then label somethin. right?
                            // but i wonder which server they were on longest.
                            // maybe just any one they left in the last 2.5 mins!

                            foreach (var sighting in m_connectionLatestSighting)
                            {
                                string svr = server.ip + ":" + server.port;

                                // is this sighting about this user?
                                if (sighting.Key.Contains(stringHashOfGuy))
                                {
                                    // i don't care what this guy did on this server, only on others?
                                    if (sighting.Key.Contains(svr))
                                        continue;
                                }
                                // if sighting is on the server i'm on now,
                                // then i don't care about it.

                                // I ONLY CARE IF THEY LEFTI N THE LAST 3 MINS
                                if (sighting.Value.AddMinutes(5) < DateTime.Now)
                                {
                                    if (false == leavers.ContainsKey(svr))
                                        leavers[svr] = new List<string>();
                                    leavers[svr].Add(stringHashOfGuy);
                                }
                            }
                        }
                    }
                }
            }

            // Do we know all the leavers now? 
            // and when we prepare a leaver card, include their names?
            // in a list? but not visible? and with user guids invisible?
            // and i can turn them on based on whether the user knows them.
            // put a dot where tho?
            // box 'em! i can stack those up fine right?
        }
        */



        static int m_secondsPause = 12;

        /*
        protected async Task MineLists()
        {
            if (LastReportedListGatheredAt != null)
            {
                if (DateTime.Now < LastReportedListGatheredAt.Value.AddSeconds(25))
                {
                    //                    Console.WriteLine("Data is less than 60 seconds old, and cached data is adequate.");
                    return; // data we have was gathered within the last minute.
                }
            }
        }
        */


        //            Console.WriteLine("    Refreshin...");

        // the code that was here was moved to a new thread.

        /*
        // Each time we mine the list, save the ACTIVE ip:port set in a local file.
        List<string> svrIpPort = new List<string>();
        List<string> svrActivesIpPort = new List<string>();
        foreach (var key in JamulusListURLs.Keys)
        {
            var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(LastReportedList[key]);
            foreach (var server in serversOnList)
            {
                svrIpPort.Add(server.ip + ":" + server.port);

                if (server.clients != null)
                    if (server.clients.GetLength(0) > 0)
                        svrActivesIpPort.Add(server.ip + ":" + server.port);
            }
        }
        await System.IO.File.WriteAllLinesAsync("allSvrIpPorts.txt", svrIpPort);
        await System.IO.File.WriteAllLinesAsync("activeSvrIpPorts.txt", svrActivesIpPort);
        */

        // We only push a canonical pair once.
        // so we track whether we've pushed each before



        /*
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes("mcfnord" + "United States" + "-");
            var hashOfGuy = System.Security.Cryptography.MD5.HashData(bytes);
            string stringHashOfMe = System.Convert.ToBase64String(hashOfGuy);

            // 1) with me by duration
            if (m_userConnectDurationPerUser.ContainsKey(stringHashOfMe))
                {
                var allMyDurations = m_userConnectDurationPerUser[stringHashOfMe];

                var sortedByLongestTime = allMyDurations.OrderBy(dude => dude.Value);
                foreach (var them in sortedByLongestTime)
                {
                    //                    Console.WriteLine(them.Key + " " + them.Value.ToString());
                }

                /////////////////////////////////////////////////////////////////////////////////
                /////////////////////////////////////////////////////////////////////////////////
                /////////////////////////////////////////////////////////////////////////////////
                /////////////////////////////////////////////////////////////////////////////////
                // Create a new duration that is actually the old one multiplied by # of servers where i joined you
                {
                    var cookedDurations = new Dictionary<string, TimeSpan>();
                    foreach (var someoneElse in m_userConnectDurationPerUser[stringHashOfMe])
                    {

                        // Just start duration with total time togetther,
                        // regardless of who joined who.
                        cookedDurations[someoneElse.Key] = someoneElse.Value;
                        // and this shoots up for people I've joined, but even true north accrues here.
                        // even if he just joined me.

                        // make a key with me as actor, you as target
                        string us = stringHashOfMe + someoneElse.Key;
                        if (m_everywhereIveJoinedYou.ContainsKey(us))
                        {
                            var newCookedDuration = m_everywhereIveJoinedYou[us].Count * someoneElse.Value;
                            cookedDurations[someoneElse.Key] += newCookedDuration;
                        }
                    }

                    var orderedCookedDurations = cookedDurations.OrderByDescending(dude => dude.Value);
                    foreach (var guy in orderedCookedDurations)
                    {
                        Console.Write(NameFromHash(guy.Key) + " " + guy.Value + " ");
                        string us = stringHashOfMe + guy.Key;
                        if (m_everywhereIveJoinedYou.ContainsKey(us))
                        {
                            Console.Write(m_everywhereIveJoinedYou[us].Count);
                        }
                        Console.WriteLine();
                    }
                }
            }
        }
        }
*/






















        /* I'm not snippeting so who cares?

                static int m_lastRefreshSnippetingHalos = 0;
                static List<string> m_halos_snippeting = new List<string>();


                static bool AnyoneBlockSnippeting(string ipport)
                {
                    if (m_lastRefreshSnippetingHalos != MinutesSince2023AsInt())
                    {
                        m_lastRefreshSnippetingHalos = MinutesSince2023AsInt();

                        string url = "https://jamulus.live/halo-snippeting.txt";
                        System.Threading.Tasks.Task<List<string>> task = LoadLinesFromHttpTextFile(url);
                        task.Wait();
                        m_halos_snippeting = task.Result;
                    }

                    // determine if any halos are on ipport
                    foreach (var key in JamulusListURLs.Keys)
                    {
                        var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(LastReportedList[key]);
                        foreach (var server in serversOnList)
                        {
                            string fulladdress = server.ip + ":" + server.port;
                            if (fulladdress == ipport)
                            {
                                if (server.clients != null)
                                {
                                    foreach (var guy in server.clients)
                                    {
                                        var stringHashOfGuy = GetHash(guy.name, guy.country, guy.instrument);
                                        if (m_halos_snippeting.Contains(stringHashOfGuy))
                                        {
                                            Console.WriteLine("A halo has prevented the snippeting option at " + ipport);
                                            return true;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    return false;
                }
        */


        /* For now, nobody's gonna block streaming
                static int m_lastRefreshStreamingHalos = 0;
                static List<string> m_halos_streaming = new List<string>();
                public static bool AnyoneBlockStreaming(string ipport)
                {
                    if (m_lastRefreshStreamingHalos != MinutesSince2023AsInt())
                    {
                        m_lastRefreshStreamingHalos = MinutesSince2023AsInt();

                        string url = "https://jamulus.live/halo-streaming.txt";
                        System.Threading.Tasks.Task<List<string>> task = LoadLinesFromHttpTextFile(url);
                        task.Wait();
                        m_halos_streaming = task.Result;
                    }

                    // determine if any halos are on ipport
                    foreach (var key in JamulusListURLs.Keys)
                    {
                        var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(LastReportedList[key]);
                        foreach (var server in serversOnList)
                        {
                            string fulladdress = server.ip + ":" + server.port;
                            if (fulladdress == ipport)
                            {
                                if (server.clients != null)
                                {
                                    foreach (var guy in server.clients)
                                    {
                                        var stringHashOfGuy = GetHash(guy.name, guy.country, guy.instrument);
                                        if (m_halos_streaming.Contains(stringHashOfGuy))
                                        {
                                            Console.WriteLine("A halo has prevented streaming as an option at " + ipport);
                                            return true;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    return false;
                }
                */


        /*
        protected int DistanceFromMe(string ipThem)
        {
            string clientIP = HttpContext.Connection.RemoteIpAddress.ToString();
            if (clientIP.Length < 5)
                clientIP = "104.215.148.63"; //microsoft as test 

            double clientLatitude = 0.0, clientLongitude = 0.0, serverLatitude = 0.0, serverLongitude = 0.0;
            SmartGeoLocate(clientIP, ref clientLatitude, ref clientLongitude);
            SmartGeoLocate(ipThem, ref serverLatitude, ref serverLongitude);

            // https://www.simongilbert.net/parallel-haversine-formula-dotnetcore/
            const double EquatorialRadiusOfEarth = 6371D;
            const double DegreesToRadians = (Math.PI / 180D);
            var deltalat = (serverLatitude - clientLatitude) * DegreesToRadians;
            var deltalong = (serverLongitude - clientLongitude) * DegreesToRadians;
            var a = Math.Pow(
                Math.Sin(deltalat / 2D), 2D) +
                Math.Cos(clientLatitude * DegreesToRadians) *
                Math.Cos(serverLatitude * DegreesToRadians) *
                Math.Pow(Math.Sin(deltalong / 2D), 2D);
            var c = 2D * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1D - a));
            var d = EquatorialRadiusOfEarth * c;
            return Convert.ToInt32(d);
        }
        */

        /*
        public string HighlightUserSearchTerms(string str)
        {
            if(SearchTerms != null)
                if(SearchTerms.Length > 0)
                {
                    foreach (var term in SearchTerms.Split(' '))
                        if(term.Length > 0) // yeah i guess split cares about um two spaces, gives you a blank word yo
                            str = str.Replace(term, string.Format("<font size='+2' color='green'>{0}</font>", term), true, null); 
                }
            return str;
        }
        */

        protected static string LocalizedText(string english, string chinese, string thai, string german, string italian)
        {
            switch (m_TwoLetterNationCode)
            {
                case "CN":
                    return chinese;
                case "TW":
                    return chinese;
                case "HK":
                    return chinese;
                case "TH":
                    return thai;
                //                case "BRA":
                //                    return english; // can't return portguese yet. don't know portguese.
                case "DE":
                    return german; // first support DEU.
                case "IT":
                    return italian;
            }
            return english; //uber alles
        }
        protected string DurationHere(string server, string who)
        {
            Debug.Assert(who.Length == "b707dc8fc6516826fbe9b4aa84d1553a".Length);
            string hash = who + server;
            if (false == m_connectionFirstSighting.ContainsKey(hash))
                return "";

            string show = "";
            while (true)
            {
                TimeSpan ts = DateTime.Now.Subtract(m_connectionFirstSighting[hash]);

                /*
                if (ts.Days > 0)
                {
                    show = ts.Days.ToString() + "d";
                    break;
                }
                if (ts.Hours > 0)
                {
                    show = ts.Hours.ToString() + "h";
                    break;
                }
                */

                if (ts.TotalMinutes > 99) // once 99m has elapsed, don't show nothin.
                    break;

                if (ts.TotalMinutes > 5)
                {
                    int tot = (int)ts.TotalMinutes;
                    show = "<p align='right'>(" + tot.ToString() + "m)</p>";
                    break;
                }

                // on the very first notice, i don't want this indicator, cuz it's gonna frustrate me with saw-just-onces
                if (ts.TotalMinutes > 1) // so let's see them for 1 minute before we show anything fancy
                {
                    string phrase = LocalizedText("just&nbsp;arrived", "剛加入", "เพิ่งมา", "gerade&nbsp;angekommen", "appena&nbsp;arrivato");
                    show = "<b><p align='right'>(" + phrase + ")</p></b>"; // after 1 minute, until 6th minute, they've Just Arrived
                }
                else
                {
                    int i = (int)ts.TotalMinutes;
                    show = "<p align='right'>(" + i + "m)</p>";
                }

                /*
                // total mins is five or less. show server most recently visited.
                Dictionary<string, TimeSpan> whereSeen = new Dictionary<string, TimeSpan>();
                foreach (var key in LastReportedList.Keys)
                {
                    var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(LastReportedList[key]);
                    foreach (var aServer in serversOnList)
                    {
                        if (aServer.name == server)
                            continue;

                        whereSeen[aServer.name] = DateTime.Now - cls[who];
                    }
                }
                */

                break;
            }

            return " <font size='-1'><i>" + show + "</i></font>";
        }


        static List<ServersForMe> m_allMyServers = null;


        // if we find this server address in the activity report, show its url



        // if we find this server address in the activity report, show its url
        public string FindActiveJitsiOfJSvr(string serverAddress)
        {
            const string JITSI_LIVE_LIST = "/root/jitsimon/latest-activity-report.txt";
            string line = string.Empty;
            string theString = "";
            if (System.IO.File.Exists(JITSI_LIVE_LIST))
            {
                // Read the file and display it line by line.
                System.IO.StreamReader file = new System.IO.StreamReader(JITSI_LIVE_LIST);
                while ((line = file.ReadLine()) != null)
                {
                    if (line.Contains(serverAddress))
                        theString = line;
                }
                file.Close();
            }

            if (theString.Length > 0)
                return theString.Substring(theString.LastIndexOf(' ') + 1);

            return "";
        }

        // didn't work        public static Dictionary<string, string> m_ipToGuid = new Dictionary<string, string>();

        static bool NukeThisUsername(string name, string instrument, bool CBVB)
        {
            var trimmed = name.Trim();

            if (CBVB)
                if (name.ToLower().Contains("feed"))
                    return true;

            if (trimmed.Contains("LowBot"))
                return true;

            switch (trimmed.ToUpper())
            {
                case "BIT A BIT": return true;
                case "JAMONET": return true;
                case "JAMONET'": return true;
                case "JAMSUCKER": return true;
                case "JAM FEED": return true;
                case "STUDIO BRIDGE": return true;
                case "CLICK": return true;
                case "LOBBY [0]": return true;
                case "LOBBY [1]": return true;
                case "LOBBY [2]": return true;
                case "LOBBY [3]": return true;
                case "LOBBY [4]": return true;
                case "LOBBY [5]": return true;
                case "LOBBY [6]": return true;
                case "LOBBY [7]": return true;
                case "LOBBY [8]": return true;
                case "LOBBY [9]": return true;
                case "LOBBY[0]": return true;
                case "LOBBY": return true;
                case "JAMULUS TH": return true;
                case "DISCORD.EXE": return true;
                case "REFERENCE": return true;
                // case "JAMULUS   TH": return true;
                // case "PETCH   BRB": return true;
                case "PRIVATE": return true;
                case "BOT?": return true;
                case "":
                    if (instrument == "Streamer")
                        return true;
                    if (instrument == "-")
                        return true;
                    return false;
                case "PLAYER":
                    if (instrument == "Conductor")
                        return true;
                    return false;

                default:
                    return false;
            }
        }

        static DateTime firstSample = DateTime.Now;
        public bool NoticeNewbs(string server)
        {
            if (DateTime.Now < firstSample.AddHours(1))
                return false; // just ignore everyone for an hour.

            // was this server was first sighted over an hour ago?
            if (false == m_serverFirstSeen.ContainsKey(server))
                return false;

            if (m_serverFirstSeen[server] < DateTime.Now.AddHours(-1))
                return false; // not a noob.

            return true;
        }

        static int minuteOfSample = -1;
        static string cachedResult = "";
        public string Noobs
        {
            get
            {
                if (DateTime.Now.Minute == minuteOfSample)
                    return cachedResult;

                if (m_timeTogether == null)
                    return "";

                Dictionary<string, TimeSpan> durations = new Dictionary<string, TimeSpan>();
                foreach (var key in LastReportedList.Keys)
                {
                    var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(LastReportedList[key]);
                    foreach (var server in serversOnList)
                    {
                        if (server.clients == null)
                            continue;

                        foreach (var guy in server.clients)
                        {
                            // For each client how many apperances?
                            // cuz maybe the lowest just wins. No interacties.
                            string guid = GetHash(guy.name, guy.country, guy.instrument);
                            TimeSpan duration = new TimeSpan();
                            try
                            {
                                foreach (var pair in m_timeTogether)
                                {
                                    if (pair.Key.Contains(guid))
                                    {
                                        duration += pair.Value;
                                    }
                                }
                                durations[guid] = duration;
                            }
                            catch (InvalidOperationException e)
                            {
                                Console.WriteLine("no noobs cuz collection modified.");
                                return "";
                            }
                        }
                    }
                }

                var sortedByRarest = durations.OrderBy(x => x.Value).ToList();

                // const cars = ["Saab", "Volvo", "BMW"];
                // so return that string subsection

                string s = "";

                foreach (var bro in sortedByRarest.Take(3))
                {
                    s += "'" + bro.Key + "',";

                }

                if (s.Count() < 1)
                {
                    Console.WriteLine("no noobs cuz no nobody.");
                    return "";
                }
                s = s.Substring(0, s.Count() - 1);

                cachedResult = s;
                minuteOfSample = DateTime.Now.Minute;

                return cachedResult;
            }
        }


        // Switching to 2 second cache cuz i'm concerned this request is bottlenecking us

        static Dictionary<string, int> twoSecondZoneOfLastSample = new Dictionary<string, int>();
        static Dictionary<string, bool> freeStatusCache = new Dictionary<string, bool>();

        /*
        public static bool InstanceIsFree(string url, string currentDock)
        {
            // is this dock creator's ISP allowed to create leases?
            if (null != currentDock)
            {
                //Console.WriteLine("CurrentDock: " + currentDock) ;
                if (forbidder.m_forbiddenIsp.Contains(forbidder.m_dockRequestor[currentDock]))
                {
                    Console.WriteLine("The lease is free because the current dock was made by a forbidden ISP.");
                    return true;
                }
            }

            // ok, is it free?
            if (twoSecondZoneOfLastSample.ContainsKey(url))
                if (twoSecondZoneOfLastSample[url] == DateTime.Now.Second / 2)
                    return freeStatusCache[url];

            bool result = false;
            using (var httpClient = new HttpClient())
            {
                var contents = httpClient.GetStringAsync(url).Result;
                if (contents.Contains("True"))
                {
                    result = true;
                }
            }
            freeStatusCache[url] = result;
            twoSecondZoneOfLastSample[url] = DateTime.Now.Second / 2;
            return result;
        }
        */



                static Dictionary<string, List<string>> m_predicted = new Dictionary<string, List<string>>();
                static int m_lastMinSampledPredictions = -1;
        


        // --- Caching Fields for Preloaded Data ---
        // These fields will store the file data and reload it every 15 MINUTES (900 seconds).
        private static PreloadedData m_cachedPreloadedData;
        private static DateTime m_lastDataLoadTime = DateTime.MinValue;
        
        // [FIX 1] Increased from 65.0 to 900.0 (15 minutes)
        private const double CacheDurationSeconds = 300.0; 
        
        private static readonly SemaphoreSlim _dataLoadLock = new SemaphoreSlim(1, 1);



        //################################################################################
        // NEW CACHING HELPER METHOD (With Manual GC Injection)
        //################################################################################

        /// <summary>
        /// Gets the preloaded data, refreshing it from disk if the cache is older than 900 seconds.
        /// </summary>
        private async Task<PreloadedData> GetCachedPreloadedDataAsync()
        {
            // First, check without a lock (fast path)
            if (DateTime.Now <= m_lastDataLoadTime.AddSeconds(CacheDurationSeconds))
            {
                return m_cachedPreloadedData;
            }

            // Cache is expired, so acquire the async-friendly semaphore
            await _dataLoadLock.WaitAsync();
            try
            {
                // Now that we have the lock, double-check
                if (DateTime.Now > m_lastDataLoadTime.AddSeconds(CacheDurationSeconds))
                {
                    Console.WriteLine($"Cache expired ({CacheDurationSeconds}s). Reloading all data files...");

                    // 1. Load the data (High memory pressure event)
                    m_cachedPreloadedData = await LoadPreloadedDataAsync();

                    m_lastDataLoadTime = DateTime.Now;
                    Console.WriteLine("Data file reload complete.");

                    // [FIX 2] FORCE MANUAL GARBAGE COLLECTION
                    // We just created millions of string objects parsing CSVs. 
                    // We force the cleanup NOW, while the thread is already paused, 
                    // to prevent the OS from thrashing Swap later during a user request.
                    Console.WriteLine("Performing manual Garbage Collection to prevent swap thrashing...");
                    
                    // Compact the Large Object Heap (LOH) to reduce fragmentation
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    
                    // Force a full blocking collection (Gen 2, Forced, Blocking, Compacting)
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    GC.WaitForPendingFinalizers();
                    
                    Console.WriteLine("Garbage Collection complete. Memory stabilized.");
                }
            }
            finally
            {
                _dataLoadLock.Release();
            }

            return m_cachedPreloadedData;
        }

        //################################################################################
        // PRIVATE HELPER METHODS (Unchanged from previous refactor)
        //################################################################################

        /// <summary>
        /// A private record to hold all the data we load from files at the start.
        /// </summary>
        private record PreloadedData(
            HashSet<string> BlockedASNs,
            string[] JoinEventsLines,
            HashSet<string> ErasedServerNames,
            HashSet<string> NoPingIpPartial,
            HashSet<string> BlockedServerARNs,
            HashSet<string> GoodGuids
        );

        /// <summary>
        /// Resets the lists and counters for this request.
        /// </summary>
        private void InitializeGutsRequest()
        {
            m_allMyServers = new List<ServersForMe>(); // new list!
            m_listenLinkDeployment.Clear();
            m_snippetsDeployed = 0;
        }

        /// <summary>
        /// Fetches the 'soon.json' prediction file if the cache minute has expired.
        /// </summary>
        private async Task UpdatePredictionsIfNeededAsync()
        {
            if (m_lastMinSampledPredictions == DateTime.Now.Minute)
            {
                return;
            }

            if ((DateTime.Now.Minute % 5) == 4) // Simplified from the original switch
            {
                m_lastMinSampledPredictions = DateTime.Now.Minute;
                try
                {
                    using var http = new HttpClient();
                    string json = await http.GetStringAsync("https://jamulus.live/soon.json");
                    // m_predicted = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to update predictions: {ex.Message}");
                    // Don't reset the minute if we failed, so we can try again next time
                    m_lastMinSampledPredictions = -1;
                }
            }
        }

        /// <summary>
        /// Loads all data from local files (block lists, etc.) into memory.
        /// This method is now called by the caching manager.
        /// </summary>
        private async Task<PreloadedData> LoadPreloadedDataAsync()
        {
            // 1. Converted to ReadAllLinesAsync
            var blockedASNs = new HashSet<string>(
                (await System.IO.File.ReadAllLinesAsync("wwwroot/asn-blocks.txt"))
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .Select(line => line.Trim().Split(' ')[0])
            );

            // 2. Converted to ReadAllLinesAsync
            // (File.Exists is synchronous but very fast, so it's fine)
            var joinEventsLines = System.IO.File.Exists("join-events.csv")
                ? await System.IO.File.ReadAllLinesAsync("join-events.csv")
                : new string[0];

            // 3. Converted to ReadAllLinesAsync
            var erasedServerNames = new HashSet<string>(
                System.IO.File.Exists("erased.txt")
                    ? (await System.IO.File.ReadAllLinesAsync("erased.txt")).Select(l => l.Trim().ToLower())
                    : new string[0]
            );

            // 4. Converted to ReadAllLinesAsync
            var noPingIpPartial = new HashSet<string>(
                System.IO.File.Exists("no-ping.txt")
                    ? (await System.IO.File.ReadAllLinesAsync("no-ping.txt")).Where(l => l.Trim().Length > 0)
                    : new string[0]
            );

            // 5. Converted to ReadAllLinesAsync
            var blockedServerARNs = new HashSet<string>(
                System.IO.File.Exists("arn-servers-blocked.txt")
                    ? await System.IO.File.ReadAllLinesAsync("arn-servers-blocked.txt")
                    : new string[0]
            );

            // 6. IMPORTANT: This method must also be made async
            // This will be our *next* error to fix.
            var goodGuids = await GetGoodGuidsSetAsync();

            return new PreloadedData(
                blockedASNs,
                joinEventsLines,
                erasedServerNames,
                noPingIpPartial,
                blockedServerARNs,
                goodGuids
            );
        }
    
        // --- Start: Caching logic for Join-Events ---
        

        // Static cache fields to hold the GUIDs and manage expiry
        private static HashSet<string> _goodGuidsCache = null;
        private static DateTime _cacheExpiry = DateTime.MinValue;

        // 1-minute cache duration
        private static readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(1);

        // Thread-safe lock object for updating the cache
        private static readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Reads join-events.csv and builds a HashSet of all GUIDs
        /// that have a non-zero value in the 13th column.
        /// </summary>
        // 1. Signature changed to 'async Task<HashSet<string>>'
        private static async Task<HashSet<string>> LoadGoodGuidsFromFileAsync()
        {
            var goodGuids = new HashSet<string>();
            string filePath = "join-events.csv";

            try
            {
                // 2. This is the non-blocking file read.
                // It reads all lines into an array asynchronously.
                string[] lines = await System.IO.File.ReadAllLinesAsync(filePath);

                // 3. Loop over the array (this part is fast and fine)
                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    // Split the line, trimming whitespace from each part.
                    string[] parts = line.Split(',')
                                         .Select(part => part.Trim())
                                         .ToArray();

                    // Check if we have enough columns (13th column is index 12)
                    if (parts.Length > 12)
                    {
                        string guid = parts[2]; // GUID is 3rd column (index 2)
                        string flag = parts[12]; // Flag is 13th column (index 12)

                        // If the flag is not "0" and we have a GUID, add it.
                        if (flag != "0" && !string.IsNullOrEmpty(guid))
                        {
                            goodGuids.Add(guid);
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                // Log the error so you know if the file is missing or permissions are wrong.
                System.Diagnostics.Debug.WriteLine($"Error loading join-events.csv: {ex.Message}");
                // Return an empty set on failure
                return new HashSet<string>();
            }

            return goodGuids;
        }
    
        /// <summary>
        /// Gets the set of "good" GUIDs, loading from file and
        /// caching the result for 5 minutes.
        /// </summary>
        // 1. Signature changed to 'async Task<HashSet<string>>'
        private static async Task<HashSet<string>> GetGoodGuidsSetAsync()
        {
            // 1. Check if the cache is valid (fast path, no change).
            if (_goodGuidsCache != null && DateTime.UtcNow < _cacheExpiry)
            {
                return _goodGuidsCache;
            }

            // 2. If cache is invalid, acquire the ASYNC lock.
            await _cacheLock.WaitAsync();
            try
            {
                // 3. Double-check if another thread updated the cache while we waited.
                if (_goodGuidsCache != null && DateTime.UtcNow < _cacheExpiry)
                {
                    return _goodGuidsCache;
                }

                // 4. This thread will update the cache by calling the ASYNC file loader.
                //    (This will be our *next* error to fix).
                _goodGuidsCache = await LoadGoodGuidsFromFileAsync();
                _cacheExpiry = DateTime.UtcNow.Add(_cacheDuration);

                return _goodGuidsCache;
            }
            finally
            {
                // 5. Release the async lock.
                _cacheLock.Release();
            }
        }
    
        // --- End: Caching logic for Join-Events ---

        public static Dictionary<string, DateTime> m_clientIPLastVisit = new Dictionary<string, DateTime>();
        public static Dictionary<string, DateTime> m_clientIPsDeemedLegit = new Dictionary<string, DateTime>();
        public static Dictionary<string, DateTime> m_countriesDeemedLegit = new Dictionary<string, DateTime>();
        //      public static Dictionary<DateTime, int> m_usersCounted = new Dictionary<DateTime, int>();
        public static Dictionary<string, int> m_countryRefreshCounts = new Dictionary<string, int>();
        public static Dictionary<string, HashSet<string>> m_bucketUniqueIPsByCountry = new Dictionary<string, HashSet<string>>();
        public static Dictionary<string, DateTime> m_serverFirstSeen = new Dictionary<string, DateTime>();



        //        public static Dictionary<string, DateTime> countryLastVisit = new Dictionary<string, DateTime>();

        //        static string m_ThreeLetterNationCode = "USA";

        class MyUserGeoCandy
        {
            public string city;
            public string countryCode2;
        }

        static Dictionary<string, MyUserGeoCandy> userIpCachedItems = new Dictionary<string, MyUserGeoCandy>();

        static string lastUpdate = "";
        static int lastRefreshByCountryUpdate = -1;

        public string SinceNowInText(DateTime dt)
        {
            if (DateTime.Now < dt.AddHours(1))
                return "Minutes ago, ";
            if (DateTime.Now < dt.AddHours(2))
                return "An hour ago, ";
            if (DateTime.Now < dt.AddHours(18))
                return "Earlier today, ";

            return "Almost a day ago, ";
        }

        protected static string m_lastUniqueIPRevealed = "";

        public string UniqueIPsByCountry()
        {
            var sortedBuckets = m_bucketUniqueIPsByCountry.OrderByDescending(x => x.Value.Count).ToList();

            string ret = "";

            foreach (var kvp in sortedBuckets)
            {
                ret += kvp.Key + ":" + kvp.Value.Count + ", ";
            }

            /*
            foreach(var key in m_bucketUniqueIPsByCountry.Keys)
            {
                ret += key + ":" + m_bucketUniqueIPsByCountry[key].Count + ", ";
            }
            */

            if (ret != m_lastUniqueIPRevealed)
            {
                m_lastUniqueIPRevealed = ret;
                Console.WriteLine(ret);
            }

            return ret;
        }

        // To simplify, I count Very Probable Legit Refreshes, but should instead count unique ip's by nation.
        // one ip could be one very devoted user. brazil might be one guy who is always connected.
        // it's not where i'd like to decide i'm translating!

        public string CountryRefreshCountSummary()
        {
            string ret = "";
            int iTot = 0;

            foreach (var key in m_countryRefreshCounts.Keys)
            {
                iTot += m_countryRefreshCounts[key];
            }

            foreach (var key in m_countryRefreshCounts.Keys)
            {
                ret += key + ":" + (int)(m_countryRefreshCounts[key] * 100 / iTot) + "%, ";
            }

            // show this in the log once every 3rd minute
            if (DateTime.Now.Minute / 5 != lastRefreshByCountryUpdate)
            {
                lastRefreshByCountryUpdate = DateTime.Now.Minute / 5;
                Console.WriteLine("Refreshes by nation since startup: " + ret);
            }

            //            ret = ret.Substring(0, ret.Length - 3);
            return ret;
        }

        public string GetNameGivenFullIP(string fullip)
        {
            foreach (var key in LastReportedList.Keys)
            {
                var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(LastReportedList[key]);
                foreach (var server in serversOnList)
                {
                    string matchy = server.ip + ":" + server.port;
                    if (matchy == fullip)
                        return server.name;
                }
            }
            return "";
        }

        public string TopFewHangs(string hash, string htmlAboutUser)
        {
            // try to be sexy and crowdpleasing
            var theGuy = m_userConnectDurationPerServer[hash];

            // order by duration, which is the value in this dictionary
            var sortedByDuration = theGuy.OrderByDescending(x => x.Value).ToList();

            // Reveal the first two that I can match
            foreach (var kvp in sortedByDuration)
            {
                string place = GetNameGivenFullIP(kvp.Key);
                if (place.Length > 0)
                {
                    if (htmlAboutUser.Contains("<td>" + place))
                        continue;
                    return place; // just one
                }
            }
            return "";
        }

        public string FindAndRemoveBestFriend(Dictionary<string, string> justNameOnNow, string key)
        {
            var allMyDurations = m_userConnectDurationPerUser[key];
            TimeSpan topTs = TimeSpan.Zero;
            var who = "";
            string whoHash = "";
            foreach (var otherGuy in allMyDurations.Keys)
            {
                if (allMyDurations[otherGuy] > topTs)
                    if (justNameOnNow.ContainsKey(otherGuy)) // remember, live listing only
                        if (justNameOnNow[otherGuy] != "") // I just hate blanks ok?
                        {
                            topTs = allMyDurations[otherGuy];
                            who = justNameOnNow[otherGuy];
                            whoHash = otherGuy;
                        }
            }

            justNameOnNow.Remove(whoHash); // as a sneaky trick, i dare to kill off this guy so NOBODY can be their best friend after once?
            return who;
        }


        // Show active users sorted by how many servers I've seen them on since startup

        public string UniqueServerCountOfEveryActiveUser
        {
            get
            {
                var finder = new MusicianFinder();

                string htmlResult = finder.FindMusiciansHtmlAsync(GetClientIpAddress()).GetAwaiter().GetResult();

                return htmlResult;
            }
        }

        // Example usage:
        // double myLat = 47.6295;
        // double myLon = -122.3165;
        // string tableHtml = GetMusicianHtmlSynchronously(myLat, myLon);
        // Console.WriteLine(tableHtml);

        /*
        public string UniqueServerCountOfEveryActiveUser
        {
            get
            {
                m_serializerMutex.WaitOne();
                try
                {
                    string ret = "";

                    // map md5 key to contents for active online now
                    Dictionary<string, string> peopleOnNow = new Dictionary<string, string>();
                    Dictionary<string, string> justNameOnNow = new Dictionary<string, string>();


                    foreach (var key in LastReportedList.Keys)
                    {
                        var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(LastReportedList[key]);
                        foreach (var server in serversOnList)
                        {
                            int peepCount = 0;
                            if (server.clients != null)
                                peepCount = server.clients.GetLength(0);
                            if (peepCount < 1)
                                continue; // just fuckin don't care about 0 or even 1. MAYBE I DO WANNA NOTICE MY FRIEND ALL ALONE SOMEWHERE THO!!!!

                            foreach (var guy in server.clients)
                            {
                                if (guy.name == "")
                                    continue;
                                if (guy.name == "No Name")
                                    continue;
                                if (guy.name == "jammer")
                                    continue;

                                string encodedHashOfGuy = GetHash(guy.name, guy.country, guy.instrument);

                                if (false == peopleOnNow.ContainsKey(encodedHashOfGuy))
                                {
                                    peopleOnNow.Add(encodedHashOfGuy,
                                        "<td class='musicianInList'"
                                        + " id='" + guy.name + guy.country + "'" + ">"
                                        + guy.name
                                        + "<td>" + guy.country
                                        + "<td>" + guy.instrument
                                        + "<td>" + server.name);

                                    justNameOnNow.Add(encodedHashOfGuy, guy.name);
                                }
                            }
                        }
                    }

                    // here we examine every hash that has a server list, ordered by server count.
                    // And show the user every user that matches, and what their server count is.
                    var usersSortedByNumOfServersSeenOn = m_userServerViewTracker.OrderByDescending(x => x.Value.Count).ToList();

                    foreach (var kvp in usersSortedByNumOfServersSeenOn)
                    {
                        if (peopleOnNow.ContainsKey(kvp.Key))
                            if (kvp.Value.Count > 0) // of course it is. previously i just showed >1 counts
                            {
                                bool fDayOrMore = true;
                                if (m_userConnectDuration[kvp.Key].TotalHours < 12)
                                    fDayOrMore = false;

                                var sexierDuration = m_userConnectDuration[kvp.Key].ToString("g");
                                sexierDuration = sexierDuration.Substring(0, sexierDuration.LastIndexOf(":"));
                                ret += "<tr>"
                                    + peopleOnNow[kvp.Key]
                                    + "<td>"
                                    + ((kvp.Value.Count > 2) ? TopFewHangs(kvp.Key, peopleOnNow[kvp.Key]) : "") // only show often-seen-on if they've joined more than 2 servers
                                    + "<td>"
                                    + ((fDayOrMore && (kvp.Value.Count > 1)) ? FindAndRemoveBestFriend(justNameOnNow, kvp.Key) : "") // only show best-friend if they've joined more than 1 server
                                    + "<td>"
                                    + kvp.Value.Count
                                    + "<td>"
                                    + sexierDuration
                                    + "</tr>";
                            }
                    }
                    if (ret.Length > 0)
                        ret = "<table border='1'><tr><th>Name<th>Country<th>Instrument<th>Now On<th>Often On<th>Often With<th>Svrs Joined<th>Tot. Time</tr>"
                            + ret
                            + "</table>";
                    return ret;

                }
                finally
                {
                    m_serializerMutex.ReleaseMutex();

                }
            }
        }
        */

        /*
        static bool InMapFile(string fullAddress)
        {
            var httpClient = new HttpClient();
            var response = httpClient.GetStringAsync("https://jamulus.live/map.txt").Result;
            return response.Contains(fullAddress);
        }
        */



        static bool HasListenLink(string ipport)
        {
            foreach (var ipp in m_listenLinkDeployment)
            {
                if (ipp == ipport)
                    return true;
            }
            return false;
        }

/*
        public string HowManyUsers
        {
            get
            {
                m_serializerMutex.WaitOne();
                try
                {


                    List<string> svrActivesIpPort = new List<string>();

                    // If iActiveJamFans is active, list all active servers to a file
                    if (false)  //  if (iActiveJamFans < 10)
                    {
                        // later i might assure the top two by activity are always listed... but at this point it's just off
                        svrActivesIpPort.Add("");
                        System.IO.File.WriteAllLines("serversToSample.txt", svrActivesIpPort);
                    }
                    else
                    {
                        //                        Console.WriteLine("I wanna choose which server to sample.");

                        foreach (var key in LastReportedList.Keys)
                        {
                            var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(LastReportedList[key]);
                            foreach (var server in serversOnList)
                            {
                                int peepCount = 0;
                                if (server.clients != null)
                                    peepCount = server.clients.GetLength(0);
                                if (peepCount < 3)
                                    continue; // just fuckin don't care about 0 or even 1 or even 2!

                                bool fAnyLobby = false;
                                foreach (var client in server.clients)
                                {
                                    if (client.name.Contains("obby"))
                                        fAnyLobby = true;
                                }
                                if (fAnyLobby)
                                    continue;

                                if (server.name.ToLower().Contains("blues/rock")) // never sample "Blues/Rock"
                                    continue;
                                if (server.name.ToLower().Contains("zeel")) // never sample "Zeeland"
                                    continue;
                                if (server.name.ToLower().Contains("immy")) // never sample "Timmy"
                                    continue;
                                if (server.name.ToLower().Contains("oscv")) // never sample "OSCvev"
                                    continue;
                                if (server.name.ToLower().Contains("priv")) // don't sample self-described private areas
                                    continue;
                                // if (server.country.Contains("Germany")) // don't sample German servers... wait, it's people, not servers
                                //   continue;

                                string fullAddress = server.ip + ":" + server.port;

                                // Don't want to re-sample if this one's sampled now:
#if WINDOWS
                                string DIR = "C:\\Users\\User\\JamFan22\\JamFan22\\wwwroot\\mp3s\\"; // for WINDOWS debug
#else
                                string DIR = "/root/JamFan22/JamFan22/wwwroot/mp3s/"; // for prod
#endif
                                string wildcard = fullAddress + "*";
                                var files = Directory.GetFiles(DIR, wildcard);
                                if (files.GetLength(0) == 0) // if we don't have a sample for this now, add it to the running.
                                {
                                    // if (false == System.IO.File.Exists("/root/JamFan22/JamFan22/wwwroot/mp3s/" + fullAddress + ".mp3"))  

                                    // I don't want to sample any addresses that have a Listen link
                                    if (false == HasListenLink(fullAddress))
                                    //                                        if (false == AnyoneBlockSnippeting(fullAddress))
                                    {
                                        List<string> recents = new List<string>();
                                        recents = System.IO.File.ReadAllLines("last-snippet.txt").ToList();
                                        bool okToAdd = true;
                                        foreach (var lin in recents)
                                            if (lin.Contains(fullAddress))
                                                okToAdd = false;
                                        if (okToAdd)
                                            svrActivesIpPort.Add(fullAddress);
                                    }
                                }

                                // else
                                // {
                                //   Console.WriteLine(fullAddress + "has an active sample already.");
                                // }
                            }
                        }
                    }

                    // apparently only the first line gets processed
                    // so gimme one rando line please
                    System.IO.File.WriteAllLines("serversToSample.txt", new string[] { "" });

                    var rng = new Random();
                    if (m_snippetsDeployed < 2) // I guess having one is ok to have another. I guess.
                    {
                        if (svrActivesIpPort.Count > 1) // Do I see more than one unsampled active (2+) server?
                        {
                            // Do I feel lucky about creating a sample?
                            if (0 != rng.Next(svrActivesIpPort.Count))
                            {
                                string chosen = svrActivesIpPort[rng.Next(svrActivesIpPort.Count)];
                                System.IO.File.WriteAllLines("serversToSample.txt", new string[] { chosen });
                                //                            Console.WriteLine("I chose: " + chosen);
                            }
                        }
                    }

                    string ret = "";

                    {
                        int iActiveJamFansOver24Hours = 0;
                        int iActiveJamFansOver1Hour = 0;
                        foreach (var timeski in m_clientIPsDeemedLegit.Values)
                        {
                            //                          if (DateTime.Now < timeski.AddMinutes(60))
                            if (DateTime.Now < timeski.AddDays(1))
                                iActiveJamFansOver24Hours++;
                            if (DateTime.Now < timeski.AddHours(1))
                                iActiveJamFansOver1Hour++;
                        }

                        ret += "" +
                        //                                ". " +
                        //                        SinceNowInText(timeOfTopCount) +
                        " Over the last day, " + iActiveJamFansOver24Hours.ToString() + " musicians watched this page";

                        if (iActiveJamFansOver1Hour > 0)
                            ret += ", including " + iActiveJamFansOver1Hour.ToString() + " now";
                    }
                    //                    }

                    if (ret != lastUpdate)
                    {
                        lastUpdate = ret;
                        Console.WriteLine(ret);
                    }
                    CountryRefreshCountSummary(); // call it, discard output, it appears in the log and not UI.
                                                  //                    return ret + ". " + UniqueIPsByCountry();
                    return ret + ".";
                }
                finally { m_serializerMutex.ReleaseMutex(); }
            }
        }
        
        */


// 1. ADD THIS NEW "VIEW" PROPERTY
    // This simple string will be safely read by your Razor page.
    public string ShowServerByIPPortForView { get; private set; }


        // 2. ADD THIS NEW ASYNC METHOD
        // This is the non-blocking version of your old property.
        // It correctly uses 'await WaitAsync' and 'Release'.
public async Task<string> GetShowServerByIPPortAsync()
{
    // REMOVED: await m_serializerMutex.WaitAsync();
    // REMOVED: try { ... } finally { Release } wrappers

    // Just strictly do the work:
    StringBuilder ret = new StringBuilder("<table><tr><th>Server<th>Server Address</tr>\n");

    // We can safely read m_allMyServers because OnGetAsync ensures we are the only reader
    foreach (var s in m_allMyServers.OrderBy(x => x.name).ToList())
    {
        ret.Append("<tr><td>" + s.name + "<td>" +
                s.serverIpAddress +
                ":" +
                s.serverPort +
                "</tr>\n");
    }
    ret.Append("</table>\n");

    return ret.ToString();
}


        static int m_conditionsDelta = 0;

        public string 	RefreshDuration
        {
            get
            {
var rand = new Random();
return (120 + rand.Next(-9,9)).ToString();
/*
                // never refresh more frequently than 90 seconds
                if (m_conditionsDelta < -30)
                    m_conditionsDelta = -30;
                int iRefreshDelay = 120 + m_conditionsDelta;
                var rand = new Random();
                iRefreshDelay += rand.Next(-9, 9);

                if (ListServicesOffline.Count > 0)
                {
                    // Refresh accelerated because at least one directory sample was blank.
                    iRefreshDelay /= 2;
                }

                // this just seems unwise. So I'm not doing it.
                //                if (ListServicesOffline.Count > 0)
                //                    iRefreshDelay /= 2;

                return iRefreshDelay.ToString();
*/
            }
        }

        public string SystemStatus
        {
            get
            {
                if (ListServicesOffline.Count == 0)
                    return "";
                string ret = "<b>Oops!</b> Couldn't get updates for: ";
                foreach (var list in ListServicesOffline)
                    ret += list + ", ";
                return ret.Substring(0, ret.Length - 2); // chop comma
            }
            set { }
        }


        static List<string> eachIpIveSeenAndDescribed = new List<string>();


        static string GEOAPIFY_MYSTERY_STRING = null;


        static bool m_bUserWaiting = false;

        // Recommended: Use a single static HttpClient instance to avoid socket exhaustion.
        private static readonly HttpClient httpClient = new HttpClient();
        // private static string GEOAPIFY_MYSTERY_STRING; // Should be loaded from config once

        // The original Mutex for synchronous locking.
        // private readonly Mutex m_serializerMutex = new Mutex();



        /// <summary>
        /// Updates all user activity logs and statistics.
        /// </summary>
        private void UpdateUserStatistics(string ipAddress, MyUserGeoCandy geoData)
        {
            Console.Write($"{geoData.city}, {geoData.countryCode2}");

            if (m_clientIPLastVisit.TryGetValue(ipAddress, out var lastRefresh))
            {
                var secondsSince = (DateTime.Now - lastRefresh).TotalSeconds;
                var lowerBound = 120 + m_conditionsDelta - 30;
                var upperBound = 120 + m_conditionsDelta + 30;

                if (secondsSince > lowerBound && secondsSince < upperBound)
                {
                    Console.Write(" :)");
                    m_clientIPsDeemedLegit[ipAddress] = DateTime.Now;
                    m_countriesDeemedLegit[geoData.countryCode2] = DateTime.Now;

                    // Increment country refresh count
                    m_countryRefreshCounts.TryGetValue(geoData.countryCode2, out int count);
                    m_countryRefreshCounts[geoData.countryCode2] = count + 1;

                    // Add unique IP to country bucket
                    if (!m_bucketUniqueIPsByCountry.TryGetValue(geoData.countryCode2, out var ipSet))
                    {
                        ipSet = new HashSet<string>();
                        m_bucketUniqueIPsByCountry[geoData.countryCode2] = ipSet;
                    }
                    ipSet.Add(ipAddress);
                }
            }
            m_clientIPLastVisit[ipAddress] = DateTime.Now;
            Console.WriteLine($"  ({m_clientIPsDeemedLegit.Count} confirmed users)");
        }

        /// <summary>
        /// Adjusts a performance delta based on request duration.
        /// </summary>
        private void AdjustPerformanceDelta(TimeSpan duration)
        {
            if (duration.TotalSeconds > 3)
            {
                Console.WriteLine($"Slow response: {duration.TotalSeconds:F2}s. Increasing delta.");
                m_conditionsDelta++;
            }
            else if (duration.TotalSeconds < 1 && m_conditionsDelta > 0)
            {
                m_conditionsDelta--;
            }
        }


        private string GetClientIpAddress()
        {
            var remoteIp = HttpContext.Connection.RemoteIpAddress;
            string ipString = (remoteIp != null && System.Net.IPAddress.IsLoopback(remoteIp))
                ? HttpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault()
                : remoteIp?.ToString();

            ipString ??= "24.18.55.230";
            return ipString.StartsWith("::ffff:") ? ipString : $"::ffff:{ipString}";
        }

        // 1. Signature changed to 'async Task<MyUserGeoCandy>'
        private async Task<MyUserGeoCandy> GetOrAddUserGeoDataAsync(string ipAddress)
        {
            if (userIpCachedItems.TryGetValue(ipAddress, out var cachedCandy))
            {
                // Compiler handles wrapping this in a completed Task
                return cachedCandy;
            }

            try
            {
                // 2. Switched to async file read to avoid blocking
                GEOAPIFY_MYSTERY_STRING ??= await System.IO.File.ReadAllTextAsync("secretGeoApifykey.txt");

                string ipv4Address = ipAddress.Replace("::ffff:", "");
                string endpoint = $"https://api.geoapify.com/v1/ipinfo?ip={ipv4Address}&apiKey={GEOAPIFY_MYSTERY_STRING}";

                // 3. This is the main fix: await the async call
                string jsonResponse = await httpClient.GetStringAsync(endpoint);
                JObject jsonGeo = JObject.Parse(jsonResponse);

                var newCandy = new MyUserGeoCandy
                {
                    city = (string)jsonGeo["city"]?["name"],
                    countryCode2 = (string)jsonGeo["country"]?["iso_code"]
                };

                userIpCachedItems[ipAddress] = newCandy;
                Console.WriteLine($"Cached new location: {newCandy.city}, {newCandy.countryCode2}");
                return newCandy;
            }
            catch (Exception ex)
            {
                // 4. 'await' unwraps AggregateException. 
                // 'ex' is now the real exception (e.g., HttpRequestException).
                Console.WriteLine($"Error fetching geolocation for {ipAddress}: {ex.Message}");
                return null;
            }
        }
    
        private static readonly ConcurrentDictionary<string, string> ipToGuidCache = new();
        private static readonly ConcurrentDictionary<string, DateTime> ipCacheTime = new();
        private static readonly TimeSpan cacheDuration = TimeSpan.FromMinutes(5);

        // 1. Signature changed to 'async Task<string>'

        //        static string m_likelyGuidOfUser = null;

        /*
                public string RightNow
                {
                    get
                    {
                        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                        m_serializerMutex.WaitOne();
                        try
                        {
                            string ipAddress = GetClientIpAddress();
                            if (string.IsNullOrEmpty(ipAddress)) return string.Empty;

                            // var geoData = GetOrAddUserGeoData(ipAddress);
                            MyUserGeoCandy geoData = await GetOrAddUserGeoDataAsync(ipAddress);
                            if (geoData != null)
                            {
                                UpdateUserStatistics(ipAddress, geoData);
                                m_TwoLetterNationCode = geoData.countryCode2;
                            }


                            // Call the synchronous version or block the async version
                            var task = GetGutsRightNow(); // Assuming GetGutsRightNow returns a Task<string>
                            task.Wait();
                            return task.Result;
                        }
                        finally
                        {
                            stopwatch.Stop();
                            AdjustPerformanceDelta(stopwatch.Elapsed);
                            m_serializerMutex.ReleaseMutex();
                        }
                    }

                    set
                    {
                    }
                }
                */

/*
        // ADD THIS METHOD (The new async version of 'RightNow')
        public async Task<string> GetRightNowAsync()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // This is the new ASYNC, NON-BLOCKING wait
            await m_serializerMutex.WaitAsync();

            try
            {
                string ipAddress = GetClientIpAddress();
                if (string.IsNullOrEmpty(ipAddress)) return string.Empty;

                // This is your 'await' from the error message
                MyUserGeoCandy geoData = await GetOrAddUserGeoDataAsync(ipAddress);
                if (geoData != null)
                {
                    UpdateUserStatistics(ipAddress, geoData);
                    m_TwoLetterNationCode = geoData.countryCode2;
                }

                // This now 'awaits' the result instead of blocking
                // (Make sure 'GetGutsRightNow' is 'async Task<string>')
                return await GetGutsRightNow();
            }
            finally
            {
                stopwatch.Stop();
                AdjustPerformanceDelta(stopwatch.Elapsed);

                // This is the new ASYNC, NON-BLOCKING release
                m_serializerMutex.Release();
            }
        }
        */


// 
public async Task<string> GetGutsRightNow()
{
    // 1. Initialize state for this request
    InitializeGutsRequest();

    // 2. Fetch remote prediction data (if needed)
    await UpdatePredictionsIfNeededAsync();

    // 3. Get cached data (Servers, Blocklists, etc.), refreshing if older than 5 mins
    var preloadedData = await GetCachedPreloadedDataAsync();

    // 4. Process all servers and clients to build the internal list (m_allMyServers)
    await ProcessServerListsAsync(preloadedData);

    // 5. Sort the processed servers by distance
    // (This works because m_allMyServers was populated by step 4)
    IEnumerable<ServersForMe> sortedByDistanceAway = m_allMyServers.OrderBy(svr => svr.distanceAway);

    // 6. Generate the final HTML from the sorted list
    string output = await GenerateServerListHtmlAsync(sortedByDistanceAway, preloadedData);

    return output;
}

// ---------------------------------------------------------
        // NEW THROTTLING FIELDS 
        // ---------------------------------------------------------
        private static DateTime _lastGeoCheck = DateTime.MinValue;
        private static readonly object _geoCheckLock = new object();

        // ---------------------------------------------------------
        // REPLACES 'GetRightNowAsync'
        // ---------------------------------------------------------
        // 1. NEW CACHE: Thread-safe, stores Code + Expiration
        // "ConcurrentDictionary" handles locking for us automatically.
//        private static System.Collections.Concurrent.ConcurrentDictionary<string, (string Code, DateTime Expiry)> _countryCodeCache
//            = new System.Collections.Concurrent.ConcurrentDictionary<string, (string, DateTime)>();

// --- REPLACE YOUR EXISTING 'GetRightNowAsync' WITH THIS ---
public async Task<string> GetRightNowAsync()
{
    try
    {
        string ipAddress = GetClientIpAddress();
        string UserNationCode = "US"; // Default if lookup fails

        if (!string.IsNullOrEmpty(ipAddress))
        {
            // 1. CHECK CACHE (Is this user in RAM?)
            if (_countryCodeCache.TryGetValue(ipAddress, out var cached) && DateTime.Now < cached.Expiry)
            {
                UserNationCode = cached.Code;
            }
            else
            {
                // 2. NEW USER (Fetch from API)
                try
                {

                    string url = $"http://ip-api.com/json/{ipAddress}";
Console.WriteLine("4 requesting " + url);
                    // We use the shared 'httpClient' you already have defined
                    string response = await httpClient.GetStringAsync(url);
                    
                    // Parse the JSON to get the Country Code
                    var json = Newtonsoft.Json.Linq.JObject.Parse(response);
                    string code = (string)json["countryCode"];

                    if (!string.IsNullOrEmpty(code))
                    {
                        UserNationCode = code;
                        // Save to RAM for 48 hours
                        _countryCodeCache[ipAddress] = (code, DateTime.Now.AddHours(48));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Geo lookup failed: {ex.Message}");
                }
            }

            // 3. LOG TO DISK (Fire and Forget)
            // The method itself now checks for "TH" before writing.
//            _ = LogVisitAsync(ipAddress, UserNationCode);
        }

        // 4. Return the HTML content
        return await GetGutsRightNow();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in GetRightNowAsync: {ex.Message}");
        return "";
    }
}

        public string RightNowForView { get; private set; }

// --- Caching Fields for Census Data (1M+ Lines) ---
        // Definition includes Instrument to prevent CS0102 errors
        private static Dictionary<string, (string City, string Nation, string Instrument)> _censusCache = null;
        private static DateTime _censusCacheTime = DateTime.MinValue;
        private static readonly SemaphoreSlim _censusLock = new SemaphoreSlim(1, 1);

public async Task<string> GetIPDerivedHashAsync()
{
    string ipAddress = GetClientIpAddress();
#if WINDOWS
    // Debugging override
    ipAddress = "97.186.6.197"; 
#endif
    
    // 1. GATHER INTEL
    var candidatesTask = GuidFromIpAsync(ipAddress);
    var geoTask = GetOrAddUserGeoDataAsync(ipAddress); 
    var asnTask = AsnOfThisIpAsync(ipAddress); 
    var blockListTask = GetCachedPreloadedDataAsync(); 
    
    await Task.WhenAll(candidatesTask, geoTask, asnTask, blockListTask);
    
    var candidates = candidatesTask.Result;
    var geo = geoTask.Result;
    string rawAsn = asnTask.Result ?? "";
    var blockData = blockListTask.Result;

    // 2. ANALYZE THREATS
    string shortAsn = rawAsn.Split(' ')[0]; 
    bool isBlocked = blockData.BlockedASNs.Contains(shortAsn);

    // 3. PRINT DIAGNOSTICS
    if (candidates.Count > 0 || isBlocked)
    {
        // A. SORT CANDIDATES
        var sortedCandidates = candidates.OrderByDescending(c => c.IsOnline)
                                         .ThenByDescending(c => c.Signal)
                                         .ThenByDescending(c => c.Timestamp)
                                         .ToList();

        var bestMatch = sortedCandidates.FirstOrDefault();
        string inferredIdentity = "Unknown";
        string matchReason = "Insufficient Data";

        bool veto = false;
        bool overrideTriggered = false;

        // B. CALCULATE DECISION (For Display)
        
        // CHECK: IRON-CLAD OVERRIDE (Diamond Bunny Logic)
        if (bestMatch != default && bestMatch.IsOnline && bestMatch.Signal < 2)
        {
            var heavyHitter = candidates.Where(c => !c.IsOnline && c.Signal >= 28)
                                        .OrderByDescending(c => c.Timestamp)
                                        .FirstOrDefault();
            
            if (heavyHitter != default)
            {
                bestMatch = heavyHitter;
                matchReason = $"OVERRIDE: Weak Online (Sig 0) replaced by Iron-Clad Regular (Sig {heavyHitter.Signal})";
                inferredIdentity = !string.IsNullOrWhiteSpace(heavyHitter.Name) ? heavyHitter.Name : "No Name";
                overrideTriggered = true; 
            }
        }

        if (!overrideTriggered)
        {
            // Standard Veto Checks
            if (bestMatch != default && bestMatch.IsOnline && bestMatch.Signal == 0 && 
                candidates.Any(c => !c.IsOnline && c.Signal >= 28)) 
            {
                matchReason = "VETO: Online (Sig 0) vs Heavy Offline (Sig 28+).";
                inferredIdentity = "None (Vetoed)";
                veto = true;
            }
            else if (bestMatch != default && bestMatch.Signal < 3 && candidates.Any(c => c.Signal >= 3))
            {
                matchReason = "VETO: Weak match vs Strong History.";
                inferredIdentity = "None (Vetoed)";
                veto = true;
            }
            else if (candidates.Count > 4 && bestMatch != default && bestMatch.Signal == 0)
            {
                matchReason = "VETO: Crowded IP.";
                inferredIdentity = "None (Ambiguous)";
                veto = true;
            }
        }

        if (!veto && !overrideTriggered && bestMatch != default)
        {
            inferredIdentity = !string.IsNullOrWhiteSpace(bestMatch.Name) ? bestMatch.Name : "No Name";
            matchReason = bestMatch.IsOnline ? "Active (ONLINE)" : "Most Recent (Offline)";
        }

        // C. CONSOLE OUTPUT - EXECUTIVE SUMMARY
        string primaryLoc = (geo != null) ? $"{geo.city}, {geo.countryCode2}" : "Unknown";
        Console.WriteLine("________________________________________________________________________________");
        Console.WriteLine($"Client IP:       {ipAddress}");
        Console.WriteLine($"   GeoLoc:       {primaryLoc}");
        Console.WriteLine($"   Network:       {rawAsn}");
        
        if (isBlocked)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"   WARNING:       MATCHES BLOCKED NETWORK ({shortAsn})");
            Console.ResetColor();
        }

        Console.WriteLine("");
        Console.WriteLine($"   => MATCH:      {inferredIdentity}");
        Console.WriteLine($"   => REASON:     {matchReason}");
        Console.WriteLine("");

        // D. CONSOLE OUTPUT - THE CANDIDATE TABLE (Restored!)
        if (candidates.Count > 0)
        {
            Console.WriteLine($"{"TIME",-8} {"SIG",-4} {"STATUS",-8} {"GUID",-6} {"INSTRUMENT",-16} IDENTITY");

            foreach (var c in sortedCandidates) 
            {
                string time = c.Timestamp.ToString();
                string sig = c.Signal.ToString();
                string guidFragment = c.Guid.Length > 5 ? c.Guid.Substring(c.Guid.Length - 5) : c.Guid;
                
                string name = c.Name;
                string locRaw = string.IsNullOrEmpty(c.City) && string.IsNullOrEmpty(c.Nation) 
                    ? "-" 
                    : $"{c.City}, {c.Nation}";
                string identity = $"{name} [{locRaw}]";
                
                string inst = string.IsNullOrEmpty(c.Instrument) ? "-" : c.Instrument;
                if (inst.Length > 15) inst = inst.Substring(0, 13) + "..";

                Console.Write($" {time,-8} {sig,-4} ");

                if (c.IsOnline) 
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write($"{"ONLINE",-8} ");
                    Console.ResetColor();
                }
                else
                {
                    // Highlight Heavy Hitters (Yellow for >= 3)
                    if (c.Signal >= 3) Console.ForegroundColor = ConsoleColor.Yellow;
                    else Console.ForegroundColor = ConsoleColor.Gray;
                    
                    Console.Write($"{"OFFLINE",-8} ");
                    Console.ResetColor();
                }

                Console.WriteLine($"{guidFragment,-6} {inst,-16} {identity}");
            }
            Console.WriteLine("");
        }
        else
        {
            Console.WriteLine("   (No historical GUID matches found for this IP)");
        }
    }

    // 4. THE DECIDER (RETURN LOGIC)
    var onlineCandidate = candidates.FirstOrDefault(c => c.IsOnline);

    // --- IRON-CLAD OVERRIDE (Sig 28) ---
    if (onlineCandidate != default && onlineCandidate.Signal < 2)
    {
        var heavyHitters = candidates.Where(c => !c.IsOnline && c.Signal >= 28)
                                     .OrderByDescending(c => c.Timestamp)
                                     .ToList();

        if (heavyHitters.Any())
        {
            return "\"" + heavyHitters.First().Guid + "\";";
        }
    }

    // --- STANDARD LOGIC ---
    if (onlineCandidate != default)
    {
        // Veto if Heavy Offline exists
        if (onlineCandidate.Signal == 0 && candidates.Any(c => !c.IsOnline && c.Signal >= 28)) return "null;";
        // Veto if Strong History exists
        if (onlineCandidate.Signal < 3 && candidates.Any(c => c.Signal >= 3)) return "null;";
        // Veto if Crowded
        if (candidates.Count > 4 && onlineCandidate.Signal == 0) return "null;";

        return "\"" + onlineCandidate.Guid + "\";";
    }
    else
    {
        // Offline Fallback
        var offlineWinner = candidates.OrderByDescending(c => c.Signal)
                                      .ThenByDescending(c => c.Timestamp)
                                      .FirstOrDefault();

        if (offlineWinner != default && offlineWinner.Signal > 0) 
            return "\"" + offlineWinner.Guid + "\";";
    }

    return "null;"; 
}

        // Return Type: List of candidates (Guid, Name, City, Nation, Instrument, Signal, IsOnline, Timestamp)
        public async Task<List<(string Guid, string Name, string City, string Nation, string Instrument, int Signal, bool IsOnline, long Timestamp)>> GuidFromIpAsync(string ipAddress)
        {
            string ipClean = ipAddress.Replace("::ffff:", "").Trim();

            // 1. Harvest Active GUIDs
            HashSet<string> activeGuids = new HashSet<string>();
            var keys = LastReportedList.Keys.ToList();

            foreach (var key in keys)
            {
                if (m_deserializedCache.TryGetValue(key, out var servers))
                {
                    if (servers != null)
                        foreach (var server in servers)
                            if (server.clients != null)
                                foreach (var client in server.clients)
                                    activeGuids.Add(GetHash(client.name, client.country, client.instrument));
                }
                else if (LastReportedList.TryGetValue(key, out string json))
                {
                    try 
                    { 
                        var manualServers = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(json);
                        foreach (var server in manualServers)
                            if (server.clients != null)
                                foreach (var client in server.clients)
                                    activeGuids.Add(GetHash(client.name, client.country, client.instrument));
                    }
                    catch { continue; }
                }
            }

            // 2. Live Scan of CSV Data
            var guidMaxSignals = new Dictionary<string, int>();
            var guidNames = new Dictionary<string, string>(); 
            var guidRecency = new Dictionary<string, long>();
            var guidTimestamp = new Dictionary<string, long>();
            
            string joinPath = "join-events.csv";
            long lineIndex = 0;

            if (System.IO.File.Exists(joinPath))
            {
                try 
                {
                    using (var fs = new FileStream(joinPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs))
                    {
                        string line;
                        while ((line = await sr.ReadLineAsync()) != null)
                        {
                            lineIndex++;
                            if (!line.Contains(ipClean)) continue;

                            var parts = line.Split(',');
                            if (parts.Length > 12)
                            {
                                string clientIpPort = parts[11].Trim(); 
                                if (clientIpPort.StartsWith(ipClean + ":"))
                                {
                                    string candidateGuid = parts[2].Trim();
                                    string rawName = parts[3].Trim();
                                    string decodedName = System.Web.HttpUtility.UrlDecode(rawName);
                                    
                                    long.TryParse(parts[0], out long ts);

                                    if (int.TryParse(parts[12], out int signal))
                                    {
                                        guidRecency[candidateGuid] = lineIndex; 
                                        guidTimestamp[candidateGuid] = ts;

                                        if (!guidMaxSignals.ContainsKey(candidateGuid))
                                        {
                                            guidMaxSignals[candidateGuid] = signal;
                                            guidNames[candidateGuid] = decodedName;
                                        }
                                        else 
                                        {
                                            if (signal > guidMaxSignals[candidateGuid])
                                                guidMaxSignals[candidateGuid] = signal;
                                            
                                            guidNames[candidateGuid] = decodedName;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (IOException ex) { Console.WriteLine($"[GuidFromIpAsync] Join Read Error: {ex.Message}"); }
            }

            // 3. Scan Census Data via Cache
            // IMPORTANT: Ensure you have the updated GetCensusCacheAsync below!
            var richCache = await GetCensusCacheAsync();

            // 4. Build & Sort
            var results = new List<(string Guid, string Name, string City, string Nation, string Instrument, int Signal, bool IsOnline, long Timestamp)>();

            foreach (var kvp in guidMaxSignals)
            {
                string guid = kvp.Key;
                int signal = kvp.Value;
                string name = guidNames.ContainsKey(guid) ? guidNames[guid] : "Unknown";
                bool isOnline = activeGuids.Contains(guid);
                long recency = guidRecency.ContainsKey(guid) ? guidRecency[guid] : 0;
                long ts = guidTimestamp.ContainsKey(guid) ? guidTimestamp[guid] : 0;
                
                string city = "";
                string nation = "";
                string inst = "";

                if (richCache.TryGetValue(guid, out var info))
                {
                    city = info.City;
                    nation = info.Nation;
                    inst = info.Instrument;
                }

                results.Add((guid, name, city, nation, inst, signal, isOnline, ts));
            }

            return results.OrderByDescending(x => x.IsOnline)
                          .ThenByDescending(x => x.Signal)
                          .ThenByDescending(x => x.Timestamp)
                          .ToList();
        }

        // --- UPDATED CACHE LOADER (Includes Instrument) ---
        private async Task<Dictionary<string, (string City, string Nation, string Instrument)>> GetCensusCacheAsync()
        {
            if (_censusCache != null && DateTime.Now < _censusCacheTime.AddMinutes(60))
            {
                return _censusCache;
            }

            await _censusLock.WaitAsync();
            try
            {
                if (_censusCache != null && DateTime.Now < _censusCacheTime.AddMinutes(60))
                {
                    return _censusCache;
                }

                Console.WriteLine("Loading Census Data (1M+ lines) into RAM Cache...");
                var newCache = new Dictionary<string, (string City, string Nation, string Instrument)>();
                string censusPath = "data/censusgeo.csv";

                if (System.IO.File.Exists(censusPath))
                {
                    using (var fs = new FileStream(censusPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs))
                    {
                        string line;
                        while ((line = await sr.ReadLineAsync()) != null)
                        {
                            var parts = line.Split(',');
                            if (parts.Length >= 5)
                            {
                                string rowGuid = parts[0].Trim();
                                string inst = parts[2].Trim(); 
                                string city = System.Web.HttpUtility.UrlDecode(parts[3].Trim());
                                string nation = System.Web.HttpUtility.UrlDecode(parts[4].Trim());

                                if (newCache.TryGetValue(rowGuid, out var existing))
                                {
                                    if (!string.IsNullOrWhiteSpace(city))
                                    {
                                        newCache[rowGuid] = (city, nation, inst);
                                    }
                                }
                                else
                                {
                                    newCache[rowGuid] = (city, nation, inst);
                                }
                            }
                        }
                    }
                }
                
                Console.WriteLine($"Census Cache Loaded. {newCache.Count} unique profiles.");
                _censusCache = newCache;
                _censusCacheTime = DateTime.Now;
                GC.Collect(2, GCCollectionMode.Forced, true, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetCensusCacheAsync] Error: {ex.Message}");
                if (_censusCache == null) _censusCache = new Dictionary<string, (string City, string Nation, string Instrument)>();
            }
            finally
            {
                _censusLock.Release();
            }

            return _censusCache;
        }
    }
}
