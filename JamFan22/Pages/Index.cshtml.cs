// #define WINDOWS

// testing

// using IPGeolocation;
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
    public class JamulusServers
    {
        public long numip { get; set; }
        public long port { get; set; }
        public string? country { get; set; }
        public long maxclients { get; set; }
        public long perm { get; set; }
        public string name { get; set; }
        public string ipaddrs { get; set; }
        public string city { get; set; }
        public string ip { get; set; }
        public long ping { get; set; }
        public Os ps { get; set; }
        public string version { get; set; }
        public string versionsort { get; set; }
        public long nclients { get; set; }
        public long index { get; set; }
        public Client[] clients { get; set; }
        public long port2 { get; set; }
    }

    public class Client
    {
        public long chanid { get; set; }
        public string country { get; set; }
        public string instrument { get; set; }
        public string skill { get; set; }
        public string name { get; set; }
        public string city { get; set; }
    }

    public enum Os { Linux, MacOs, Windows };



    public class IndexModel : PageModel
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

        private readonly ILogger<IndexModel> _logger;

        public IndexModel(ILogger<IndexModel> logger)
        {
            _logger = logger;
        }

        //public void OnGet()         {         }
        // ADD THIS NEW PROPERTY
        public string IpHashForView { get; private set; }

        // REPLACE your 'public void OnGet()' or 'public IActionResult OnGet()'
        // with this 'public async Task<IActionResult> OnGetAsync()'
        public async Task<IActionResult> OnGetAsync()
        {
            IpHashForView = await GetIPDerivedHashAsync();

            RightNowForView = await GetRightNowAsync();

            ShowServerByIPPortForView = await GetShowServerByIPPortAsync();

            return Page();
        }

        public static Dictionary<string, string> JamulusListURLs = new Dictionary<string, string>()
        {

/*
{"Any Genre 1", "http://137.184.177.58/servers.php?central=anygenre1.jamulus.io:22124" }
,{"Any Genre 2", "http://137.184.177.58/servers.php?central=anygenre2.jamulus.io:22224" }
,{"Any Genre 3", "https://explorer.jamulus.io/servers.php?central=anygenre3.jamulus.io:22624" }
,{"Genre Rock",  "https://explorer.jamulus.io/servers.php?central=rock.jamulus.io:22424" }
,{"Genre Jazz",  "http://137.184.177.58/servers.php?central=jazz.jamulus.io:22324" }
//,{"Genre Jazz",  "https://explorer.jamulus.io/servers.php?central=jazz.jamulus.io:22324" }
// ,{"Genre Classical/Folk",  "https://explorer.jamulus.io/servers.php?central=classical.jamulus.io:22524" }
,{"Genre Classical/Folk",  "http://137.184.177.58/servers.php?central=classical.jamulus.io:22524" }
,{"Genre Choral/BBShop",  "https://explorer.jamulus.io/servers.php?central=choral.jamulus.io:22724" }
*/

            {"Any Genre 1", "http://24.199.107.192:5001/servers_data/anygenre1.jamulus.io:22124/cached_data" },
            {"Any Genre 2", "http://24.199.107.192:5001/servers_data/anygenre2.jamulus.io:22224/cached_data" },
            {"Any Genre 3", "http://24.199.107.192:5001/servers_data/anygenre3.jamulus.io:22624/cached_data" },
            {"Genre Rock",  "http://24.199.107.192:5001/servers_data/rock.jamulus.io:22424/cached_data" },
            {"Genre Jazz",  "http://24.199.107.192:5001/servers_data/jazz.jamulus.io:22324/cached_data" },
            {"Genre Classical/Folk",  "http://24.199.107.192:5001/servers_data/classical.jamulus.io:22524/cached_data" },
            {"Genre Choral/BBShop",  "http://24.199.107.192:5001/servers_data/choral.jamulus.io:22724/cached_data" }
        };


        // Cache to avoid re-deserializing the same JSON string repeatedly
        static Dictionary<string, List<JamulusServers>> m_deserializedCache = new Dictionary<string, List<JamulusServers>>();
        static Dictionary<string, string> m_jsonCacheSource = new Dictionary<string, string>();

        public static Dictionary<string, string> LastReportedList = new Dictionary<string, string>();
        static DateTime? LastReportedListGatheredAt = null;

        static List<string> ListServicesOffline = new List<string>();

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

        public static Dictionary<string, string> m_guidNamePairs = new Dictionary<string, string>();
/*
        public static string GetHash(string name, string country, string instrument)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(name + country + instrument);
            var hashOfGuy = System.Security.Cryptography.MD5.HashData(bytes);
            //var h = System.Convert.ToBase64String(hashOfGuy);
            var h = ToHex(hashOfGuy, false);
            m_guidNamePairs[h] = System.Web.HttpUtility.HtmlEncode(name); // This is the name map for JammerMap
            return h;
        }
        */

public static string GetHash(string name, string country, string instrument)
{
    // 1. Calculate the total length needed
    int totalLen = name.Length + country.Length + instrument.Length;

    // 2. Allocate on the Stack (Very fast, 0 Memory Pressure)
    // Note: If strings are massive (>1024 chars), this falls back to ArrayPool automatically 
    // to prevent stack overflow, but for names/countries, it stays on stack.
    Span<char> inputChars = totalLen < 1024 
        ? stackalloc char[totalLen] 
        : new char[totalLen];

    // 3. Copy the parts into the stack buffer
    var currentPos = inputChars;
    name.AsSpan().CopyTo(currentPos);
    currentPos = currentPos.Slice(name.Length);
    
    country.AsSpan().CopyTo(currentPos);
    currentPos = currentPos.Slice(country.Length);
    
    instrument.AsSpan().CopyTo(currentPos);

    // 4. Hash directly from Stack memory
    int byteCount = System.Text.Encoding.UTF8.GetByteCount(inputChars);
    Span<byte> inputBytes = byteCount < 1024 
        ? stackalloc byte[byteCount] 
        : new byte[byteCount];
        
    System.Text.Encoding.UTF8.GetBytes(inputChars, inputBytes);

    Span<byte> hashBytes = stackalloc byte[System.Security.Cryptography.MD5.HashSizeInBytes];
    System.Security.Cryptography.MD5.HashData(inputBytes, hashBytes);

    // 5. Convert to Hex String
    // We use ToLowerInvariant to match your original format (MD5 produces uppercase by default in .NET)
    string hashString = Convert.ToHexString(hashBytes).ToLowerInvariant();

    // 6. Populate the Dictionary (Protected by your Mutex)
    if (!m_guidNamePairs.ContainsKey(hashString))
    {
        m_guidNamePairs[hashString] = System.Web.HttpUtility.HtmlEncode(name);
    }

    return hashString;
}


        public static void NoteJoinerTargetServer(Client actor, Client target, string server, long port)
        {
            string hashOfActor = GetHash(actor.name, actor.country, actor.instrument);
            string hashOfTarget = GetHash(target.name, target.country, target.instrument);
            if (hashOfActor == hashOfTarget)
                return; // don't track me joining me

            string key = hashOfActor + hashOfTarget;
            if (false == m_everywhereIveJoinedYou.ContainsKey(key))
                m_everywhereIveJoinedYou[key] = new HashSet<string>();
            m_everywhereIveJoinedYou[key].Add(server + ":" + port);

            //            Console.Write(actor.name + " joined " + target.name + " | ");
        }

        public static void DetectJoiners(string was, string isnow)
        {

            // determine servers where actor joined target
            // This count, multiplied by how long actor and target are together,
            // is strength of signal to provide actor about target.
            List<JamulusServers> serverListThen = null;
            List<JamulusServers> serverListNow = null;
            try
            {
                serverListThen = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(was);
                serverListNow = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(isnow);
            }
            catch (System.Text.Json.JsonException e)
            {
                Console.WriteLine("A fatal data ingestion error has occured.");
                Console.WriteLine("was: " + was);
                Console.WriteLine("isnow: " + isnow);
                throw e;
            }

            // for each server, find it in the other set, and notice user additions only. joiners.
            foreach (var server in serverListNow)
            {
                // find same server was:
                var serverWas = GetServer(serverListThen, server.ip, server.port);
                if (serverWas != null)
                {
                    if (serverWas.clients != null)
                        if (server.clients != null)
                        {
                            var joiners = Joined(serverWas.clients, server.clients);
                            foreach (var actor in joiners)
                            {
                                // assure the actor->target key contains this ip:port in its hashset.
                                //                                Console.Write("On " + server.name + ": ");
                                foreach (var guyHere in server.clients)
                                {
                                    NoteJoinerTargetServer(actor, guyHere, server.ip, server.port);
                                }
                                //                                Console.WriteLine();
                            }
                        }
                }
            }
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



        // just do our best finding the name on this LIVE DATA
        public static bool DetailsFromHash(string hash, ref string theirName, ref string theirInstrument)
        {
            foreach (var key in JamulusListURLs.Keys)
            {
                var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(LastReportedList[key]);
                foreach (var server in serversOnList)
                {
                    if (server.clients != null)
                    {
                        foreach (var guy in server.clients)
                        {
                            var stringHashOfGuy = GetHash(guy.name, guy.country, guy.instrument);
                            /*
                            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(guy.name + guy.country + guy.instrument);
                            var hashOfGuy = System.Security.Cryptography.MD5.HashData(bytes);
                            string stringHashOfGuy = System.Convert.ToBase64String(hashOfGuy);
                            */
                            if (hash == stringHashOfGuy)
                            {
                                theirName = guy.name;
                                theirInstrument = guy.instrument;
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }

        static string TIME_TOGETHER = "timeTogether.json";
        static string TIME_TOGETHER_UPDATED_AT = "timeTogetherLastUpdates.json";
        static string GUID_NAME_PAIRS = "guidNamePairs.json";
        public static Dictionary<string, TimeSpan> m_timeTogether = null;
        public static Dictionary<string, DateTime> m_timeTogetherUpdated = null;
        static int? m_lastSaveMinuteNumber = null;
        //static HashSet<string> m_updatedPairs = new HashSet<string>(); // RAM memory of pairs I've saved
        static DateTime m_lastSift = DateTime.Now; // Time since boot or since last culling of unmentioned pairs
        static int m_lastDayNotched = 0; // At least once a day, record the number of pairs in the last, dunno, 21 days

        protected static void ReportPairTogether(string us, TimeSpan durationBetweenSamples)
        {
            if (null == m_timeTogether)
            {
                m_timeTogether = new Dictionary<string, TimeSpan>();
                m_timeTogetherUpdated = new Dictionary<string, DateTime>();
                {
                    string s = "[]";
                    try
                    {
                        s = System.IO.File.ReadAllText(TIME_TOGETHER);
                    }
                    catch (FileNotFoundException)
                    {
                        Console.WriteLine("The load file was not found, so starting from nothing.");
                    }
                    var a = JsonSerializer.Deserialize<KeyValuePair<string, TimeSpan>[]>(s);

                    foreach (var item in a)
                    {
                        m_timeTogether[item.Key] = item.Value;
                    }
                }

                {
                    string s = "[]";
                    try
                    {
                        s = System.IO.File.ReadAllText(TIME_TOGETHER_UPDATED_AT);
                    }
                    catch (FileNotFoundException)
                    {
                        Console.WriteLine("The load file updated at was not found, so starting from nothing.");
                    }
                    var a = JsonSerializer.Deserialize<KeyValuePair<string, DateTime>[]>(s);

                    foreach (var item in a)
                    {
                        m_timeTogetherUpdated[item.Key] = item.Value;
                    }

                    // If a duration has no updated time, then set it to now.
                    foreach (var item in m_timeTogether)
                    {
                        if (false == m_timeTogetherUpdated.ContainsKey(item.Key))
                        {
                            m_timeTogetherUpdated[item.Key] = DateTime.Now;
                            Console.WriteLine("Adding " + item.Key + " at Now.");
                        }
                    }

                    // Finally, find m_timeTogetherUpdated older than 21 days and remove them.
                    // WE CAN'T DO THIS AT LOAD, MUST DO IT AT SAVE.
                    /*
                    var newTimeTogether = new Dictionary<string, TimeSpan>();
                    var newTimeTogetherUpdated = new Dictionary<string, DateTime>();

                    foreach (var item in m_timeTogetherUpdated)
                    { 
                        if(item.Value.AddDays(21) > DateTime.Now)
                        {
                            newTimeTogether[item.Key] = m_timeTogether[item.Key];
                            newTimeTogetherUpdated[item.Key] = item.Value;
                        }
                        else
                            Console.WriteLine("Removing " + item.Key + " because it was last updated " + item.Value);
                    }
                    m_timeTogether = newTimeTogether;
                    m_timeTogetherUpdated = newTimeTogetherUpdated;
                    */
                }

                Console.WriteLine(m_timeTogether.Count + " pairs loaded.");
            }

            if (false == m_timeTogether.ContainsKey(us))
                m_timeTogether[us] = new TimeSpan();
            m_timeTogether[us] += durationBetweenSamples;
            m_timeTogetherUpdated[us] = DateTime.Now;
            //m_updatedPairs.Add(us);

            // Note current hour on first pass.
            // Then note if hour has changed.
            if (null == m_lastSaveMinuteNumber)
                m_lastSaveMinuteNumber = DateTime.Now.Minute;
            else
            {
                if (m_lastSaveMinuteNumber != DateTime.Now.Minute)
                {
                    m_lastSaveMinuteNumber = DateTime.Now.Minute;

                    /*
                    // If 21 days of uptime pass,
                    // I will remove pairs that haven't been updated in that period
                    //                    if (m_lastSift.AddMonths(1) < DateTime.Now)
                    if (m_lastSift.AddDays(21) < DateTime.Now)
                    {
                        m_lastSift = DateTime.Now;
                        // First, kill every entry that doesn't appear in our running list of updated pairs
                        var newTimeTogether = new Dictionary<string, TimeSpan>();
                        foreach (var pair in m_timeTogether)
                        {
                            if (m_updatedPairs.Contains(pair.Key))
                            {
                                newTimeTogether[pair.Key] = pair.Value;
                                m_updatedPairs.Remove(pair.Key);
                            }
                        }
                        m_updatedPairs = new HashSet<string>();
                        m_timeTogether = newTimeTogether;
                    }
                    */

// If 21 days of uptime HAVE PASSED, kill the data.
                        {
                            var newTimeTogether = new Dictionary<string, TimeSpan>();
                            var newTimeTogetherUpdated = new Dictionary<string, DateTime>();

                            // --- ADD THIS 'try' BLOCK ---
                            try
                            {
                                Console.WriteLine("DIAGNOSTIC: Starting 21-day cull logic...");

                                foreach (var item in m_timeTogetherUpdated)
                                {
                                    if (item.Value.AddDays(21) > DateTime.Now)
                                    {
                                        // This is the line I suspect is crashing
                                        newTimeTogether[item.Key] = m_timeTogether[item.Key]; 
                                        newTimeTogetherUpdated[item.Key] = item.Value;
                                    }
                                    else
                                        Console.WriteLine("Removing " + item.Key + " because it was last updated " + item.Value);
                                }
                                
                                // If you see this log, my hypothesis is WRONG
                                Console.WriteLine("DIAGNOSTIC: Cull logic completed successfully.");
                                m_timeTogether = newTimeTogether;
                                m_timeTogetherUpdated = newTimeTogetherUpdated;
                            }
                            // --- ADD THIS 'catch' BLOCK ---
                            catch (KeyNotFoundException knfe)
                            {
                                // If you see this log, my hypothesis is CORRECT
                                Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
                                Console.WriteLine("DIAGNOSTIC CONFIRMED: KeyNotFoundException in culling logic.");
                                Console.WriteLine($"Exception Details: {knfe.Message}"); 
                                Console.WriteLine("This exception is preventing data from being saved.");
                                Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
                                
                                // Re-throw the exception to mimic the current behavior (crashing up to the fatal handler)
                                // This ensures we don't accidentally save a partially-built (or empty) list.
                                throw; 
                            }
                        }
                    {
                        var sortedByLongest = m_timeTogether.OrderByDescending(x => x.Value).ToList();
                        string jsonString = JsonSerializer.Serialize(sortedByLongest);
                        System.IO.File.WriteAllText(TIME_TOGETHER, jsonString);
                        Console.WriteLine(sortedByLongest.Count + " pair durations saved.");

                        if (DateTime.Now.DayOfYear != m_lastDayNotched)
                        {
                            m_lastDayNotched = DateTime.Now.DayOfYear;

                            System.IO.File.AppendAllText("wwwroot/paircount.csv",
                                MinutesSince2023() + ","
                                    + sortedByLongest.Count
                                    + Environment.NewLine);

                            // tell me a time span
                            TimeSpan totalGlobalDuration = TimeSpan.Zero;
                            foreach (TimeSpan duration in m_timeTogether.Values)
                            {
                                totalGlobalDuration += duration;
                            }
                            Console.WriteLine("Total global duration: " + (int)totalGlobalDuration.TotalDays + " days.");
                        }

                    }

                    // First file write operation
                    try
                    {
                        string jsonString = JsonSerializer.Serialize(m_timeTogetherUpdated.ToList());
                        System.IO.File.WriteAllText(TIME_TOGETHER_UPDATED_AT, jsonString);
                    }
                    catch (System.IO.IOException ex)
                    {
                        // Alert that the first attempt failed and a retry is happening.
                        Console.WriteLine($"First attempt to write to {TIME_TOGETHER_UPDATED_AT} failed: {ex.Message}. Retrying in 1 second...");

                        // Wait for 1 second.
                        System.Threading.Thread.Sleep(1000);

                        // Second attempt. If this fails, the exception will be thrown.
                        string jsonString = JsonSerializer.Serialize(m_timeTogetherUpdated.ToList());
                        System.IO.File.WriteAllText(TIME_TOGETHER_UPDATED_AT, jsonString);
                    }

                    // Second file write operation
                    try
                    {
                        // Each time we save the durations, we also save the guid-name pairs.
                        var sortedByAlpha = m_guidNamePairs.OrderBy(x => x.Value).ToList();
                        string jsonString = JsonSerializer.Serialize(sortedByAlpha);
                        System.IO.File.WriteAllText(GUID_NAME_PAIRS, jsonString);
                    }
                    catch (System.IO.IOException ex)
                    {
                        // Alert that the first attempt failed and a retry is happening.
                        Console.WriteLine($"First attempt to write to {GUID_NAME_PAIRS} failed: {ex.Message}. Retrying in 1 second...");

                        // Wait for 1 second.
                        System.Threading.Thread.Sleep(1000);

                        // Second attempt. If this fails, the exception will be thrown.
                        var sortedByAlpha = m_guidNamePairs.OrderBy(x => x.Value).ToList();
                        string jsonString = JsonSerializer.Serialize(sortedByAlpha);
                        System.IO.File.WriteAllText(GUID_NAME_PAIRS, jsonString);
                    }
                }
            }
        }


        static int m_secondsPause = 12;


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

// 1. ADD THIS STATIC HTTPCLIENT at your class level (e.g., near m_serializerMutex)
    // This client is created ONCE and re-used to prevent socket exhaustion.
    // It includes your custom SSL validation bypass.
    private static readonly HttpClient s_refreshClient = new HttpClient(
        new HttpClientHandler 
        { 
            ServerCertificateCustomValidationCallback = (message, cert, chain, ssl) => true 
        }
    );

        // DELETE your old RefreshThreadTask and REPLACE it with this entire method
        public static async Task RefreshThreadTask()
        {
            while (true) // This was your 'JUST_TRY_AGAIN' loop
            {
                bool fMissingSamplePresent = false;

                try
                {
                    var serverStates = new Dictionary<string, Task<string>>();

                    foreach (var key in JamulusListURLs.Keys)
                    {
                        // Use the new shared static client
                        serverStates.Add(key, s_refreshClient.GetStringAsync(JamulusListURLs[key]));
                    }

                    DateTime query_started = DateTime.Now;
                    foreach (var key in JamulusListURLs.Keys)
                    {
                        string newReportedList = null;
                        try
                        {
                            newReportedList = await serverStates[key];

                            // --- NEW FIX: Unwrap the Python API response ---
                            // The new API returns { "servers_data": [ ... ] }, but we just want [ ... ]
                            try 
                            {
                                // Only attempt if it looks like a JSON object (starts with {)
                                if (newReportedList.TrimStart().StartsWith("{"))
                                {
                                    using (JsonDocument doc = JsonDocument.Parse(newReportedList))
                                    {
                                        if (doc.RootElement.TryGetProperty("servers_data", out var serversData))
                                        {
                                            // Overwrite the string with JUST the array part
                                            newReportedList = serversData.GetRawText();
                                        }
                                    }
                                }
                            }
                            catch (Exception) 
                            { 
                                // If parsing fails, ignore and treat as legacy/raw format
                            }
                            // --- END NEW FIX ---

                            if (newReportedList[0] == 'C')
                            {
                                Console.WriteLine("Indication of data failure: " + newReportedList);
                                await Task.Delay(1000); 
                                continue; 
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Exception handling {key}: {ex.Message}");
                            await Task.Delay(1000); // Non-blocking delay
                            break; // Breaks to the outer 'while(true)' loop
                        }

                        if (newReportedList[0] != 'C')
                        {
                            // FIXED: Replaced 'WaitOne'
                            await m_serializerMutex.WaitAsync();
                            try
                            {
                                if (newReportedList != "CRC mismatch in received message")
                                {
                                    // OPTIMIZATION: If we have data and it hasn't changed, do nothing.
                                    if (LastReportedList.ContainsKey(key) && LastReportedList[key] == newReportedList)
                                    {
                                        // Data is identical to cache; skip joiner detection and assignment
                                    }
                                    else
                                    {
                                        // Data is new or has changed
                                        if (LastReportedList.ContainsKey(key))
                                        {
                                            DetectJoiners(LastReportedList[key], newReportedList);
                                        }
                                        LastReportedList[key] = newReportedList;
                                    }
                                }                                
                                else
                                {
                                    Console.WriteLine("CRC mismatch in received message");
                                    await Task.Delay(1000); // Non-blocking delay
                                    break; // Breaks to the outer 'while(true)' loop
                                }
                            }
                            finally
                            {
                                // FIXED: Replaced 'ReleaseMutex'
                                m_serializerMutex.Release();
                            }
                        }
                    } // end foreach (var key in JamulusListURLs.Keys)

//                    Console.WriteLine("Refreshing all seven directories took " + (DateTime.Now - query_started).TotalMilliseconds + "ms");

                    // FIXED: Replaced 'WaitOne'
                    await m_serializerMutex.WaitAsync();
                    try
                    {
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

                        foreach (var key in JamulusListURLs.Keys)
                        {
                            string currentJson = LastReportedList[key];
                            List<JamulusServers> serversOnList = null;

                            // CPU SAVER: Only deserialize if the JSON string content has changed
                            if (m_jsonCacheSource.TryGetValue(key, out var cachedJson) && cachedJson == currentJson)
                            {
                                // Use the cached object (Fast!)
                                serversOnList = m_deserializedCache[key];
                            }
                            else
                            {
                                // Data changed (or first run): Deserialize and update cache
                                serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(currentJson);
                                m_deserializedCache[key] = serversOnList;
                                m_jsonCacheSource[key] = currentJson;
                            }
                            foreach (var server in serversOnList)
                            {
                                int people = server.clients?.Length ?? 0;
                                if (people < 1)
                                    continue;

                                // FIXED: Replaced 'File.AppendAllText' with 'await File.AppendAllTextAsync'
                                await System.IO.File.AppendAllTextAsync("data/server.csv",
                                    server.ip + ":" + server.port + ","
                                    + System.Web.HttpUtility.UrlEncode(server.name) + ","
                                    + System.Web.HttpUtility.UrlEncode(server.city) + ","
                                    + System.Web.HttpUtility.UrlEncode(server.country)
                                    + Environment.NewLine);

                                foreach (var guy in server.clients)
                                {
                                    string stringHashOfGuy = GetHash(guy.name, guy.country, guy.instrument);

                                    // FIXED: Replaced 'File.AppendAllText'
                                    await System.IO.File.AppendAllTextAsync("data/census.csv", MinutesSince2023() + ","
                                        + stringHashOfGuy + ","
                                        + server.ip + ":" + server.port
                                        + Environment.NewLine);

                                    // FIXED: Replaced 'File.AppendAllText'
                                    await System.IO.File.AppendAllTextAsync("data/censusgeo.csv",
                                        stringHashOfGuy + ","
                                        + System.Web.HttpUtility.UrlEncode(guy.name) + ","
                                        + guy.instrument + ","
                                        + System.Web.HttpUtility.UrlEncode(guy.city) + ","
                                        + System.Web.HttpUtility.UrlEncode(guy.country)
                                        + Environment.NewLine);

                                    // ... (all your dictionary logic is synchronous and fine) ...
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
                                        if (otherguy == guy)
                                            continue;

                                        string stringHashOfOtherGuy = GetHash(otherguy.name, otherguy.country, otherguy.instrument);
                                        var theGuyUser = m_userConnectDurationPerUser[stringHashOfGuy];
                                        if (false == theGuyUser.ContainsKey(stringHashOfOtherGuy))
                                            theGuyUser.Add(stringHashOfOtherGuy, TimeSpan.Zero);
                                        theGuyUser[stringHashOfOtherGuy] = theGuyUser[stringHashOfOtherGuy].Add(durationBetweenSamples.Divide(server.clients.Count()));

                                        string us = CanonicalTwoHashes(stringHashOfGuy, stringHashOfOtherGuy);
                                        if (false == m_everywhereWeHaveMet.ContainsKey(us))
                                            m_everywhereWeHaveMet[us] = new HashSet<string>();
                                        m_everywhereWeHaveMet[us].Add(server.ip + ":" + server.port);
                                    }

                                    if (durationBetweenSamples.TotalSeconds > 0)
                                    {
                                        foreach (var otherguy in server.clients)
                                        {


                                
                                
                                            string stringHashOfOtherGuy = GetHash(otherguy.name, otherguy.country, otherguy.instrument);
                                            if (stringHashOfGuy != stringHashOfOtherGuy)
                                            {
                                                string us = CanonicalTwoHashes(stringHashOfGuy, stringHashOfOtherGuy); 
                                                
                                                // --- DIAGNOSTIC INJECTION START ---
                                                if (false == alreadyPushed.Contains(us))
                                                {
                                                    // Check if this key is known. 
                                                    if (m_timeTogether != null && !m_timeTogether.ContainsKey(us))
                                                    {
                                                        Console.WriteLine($"★ NEW 2-GUID KEY CREATED: {guy.name} + {otherguy.name}");
                                                        Console.WriteLine($"   On Server: {server.name} ({server.city})");
                                                        Console.WriteLine($"   Key: {us}");
                                                        Console.WriteLine("--------------------------------------------------");
                                                    }

                                                    alreadyPushed.Add(us);
                                                    ReportPairTogether(us, durationBetweenSamples);
                                                }
                                                // --- DIAGNOSTIC INJECTION END ---
                                            }



                                        }
                                    }
                                }
                            }
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
                        // FIXED: Replaced 'ReleaseMutex'
                        m_serializerMutex.Release();
                    }

                    // Force a fixed 5 second delay
                    int secs = 5; 
                    
                    // Optional: Keep the error retry logic if you want
                    if (fMissingSamplePresent) secs = 2; 

                    await Task.Delay(secs * 1000);
                }
                catch (Exception ex)
                {
                    // Catch any unexpected exceptions from the loop
                    Console.WriteLine($"FATAL ERROR in RefreshThreadTask: {ex.Message}. Restarting loop in 30s.");
                    await Task.Delay(30000); // Wait 30s before restarting the whole loop
                }
            } // end while(true)
        }
  
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


        // 1. Signature changed:
        // - Added 'async'
        // - Returns 'Task<LatLong>' (assuming the class is 'LatLong')
        // - Removed 'ref' parameters (NOT allowed in async methods)
        protected async Task<LatLong> SmartGeoLocateAsync(string ip)
        {
            // Check cache first (using TryGetValue is safer)
            if (m_ipAddrToLatLong.TryGetValue(ip, out var cached))
            {
                // 2. Return the cached object directly
                // The compiler will wrap this in a completed Task
                return cached;
            }

            // Not in cache, fetch it.
            try
            {
                string ip4 = ip.Replace("::ffff:", "");
                string endpoint = "https://api.geoapify.com/v1/ipinfo?ip=" + ip4 + "&apiKey=" + GEOAPIFY_MYSTERY_STRING;

                // 3. Use 'await' and the SHARED 'httpClient'
                // Do NOT use 'new HttpClient()' here!
                string s = await httpClient.GetStringAsync(endpoint);

                JObject jsonGeo = JObject.Parse(s);
                double latitude = Convert.ToDouble(jsonGeo["location"]["latitude"]);
                double longitude = Convert.ToDouble(jsonGeo["location"]["longitude"]);

                // 4. Create, cache, and return the new object
                var newLocation = new LatLong(latitude.ToString(), longitude.ToString());
                m_ipAddrToLatLong[ip] = newLocation;

                Console.WriteLine("A client IP has been cached: " + ip + " " + jsonGeo["city"] + " " + latitude + " " + longitude);

                return newLocation;
            }
            catch (Exception e)
            {
                Console.WriteLine("Error getting geolocation for " + ip + ": " + e.Message);

                // 5. Create, cache, and return an 'error' object
                var errorLocation = new LatLong("0", "0");
                m_ipAddrToLatLong[ip] = errorLocation;
                return errorLocation;
            }
        }

        //        protected static Dictionary<string, LatLong> m_ipAddrToLatLong = new Dictionary<string, LatLong>();


        // 1. SIGNATURE CHANGE:
        // Renamed to '...Async' and changed from 'int' to 'async Task<int>'
        protected async Task<int> DistanceFromClientAsync(string lat, string lon)
        {
            var serverLatitude = float.Parse(lat);
            var serverLongitude = float.Parse(lon);

            string clientIP = HttpContext.Connection.RemoteIpAddress.ToString();
            if ((clientIP.Length < 5) || clientIP.Contains("127.0.0.1"))
            {
                // ... (all your X-Forwarded-For logic is correct) ...
                if (clientIP.Contains("127.0.0.1") || clientIP.Contains("::1"))
                {
                    var xff = (string)HttpContext.Request.HttpContext.Request.Headers["X-Forwarded-For"];
                    if (null != xff)
                    {
                        if (false == xff.Contains("::ffff"))
                            xff = "::ffff:" + xff;
                        clientIP = xff;
                    }
                    else
                    {
                        clientIP = "75.172.123.21";
                    }
                }
            }

            // 2. 'someIp' VARIABLE FIX:
            // Changed 'someIp' to 'clientIP', which is the correct variable
            LatLong location = await SmartGeoLocateAsync(clientIP);

            // You can now get the values from the returned object
            double clientLatitude = double.Parse(location.lat);
            double clientLongitude = double.Parse(location.lon);

            // ... (all your distance calculation logic is correct) ...
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
    
        protected int Distance(double lat1, double lon1, double lat2, double lon2)
        {
            // https://www.simongilbert.net/parallel-haversine-formula-dotnetcore/
            const double EquatorialRadiusOfEarth = 6371D;
            const double DegreesToRadians = (Math.PI / 180D);
            var deltalat = (lat2 - lat2) * DegreesToRadians;
            var deltalong = (lon2 - lon1) * DegreesToRadians;
            var a = Math.Pow(
                Math.Sin(deltalat / 2D), 2D) +
                Math.Cos(lat2 * DegreesToRadians) *
                Math.Cos(lat1 * DegreesToRadians) *
                Math.Pow(Math.Sin(deltalong / 2D), 2D);
            var c = 2D * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1D - a));
            var d = EquatorialRadiusOfEarth * c;
            return Convert.ToInt32(d);

        }


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

        class ServersForMe
        {
            public ServersForMe(string cat, string ip, long port, string na, string ci, string cou, int distance, char earthZone, string w, Client[] originallyWho, int peoplenow, int maxpeople)
            {
                category = cat;
                serverIpAddress = ip;
                serverPort = port;
                name = na;
                city = ci;
                country = cou;
                distanceAway = distance;
                zone = earthZone;
                who = w;
                whoObjectFromSourceData = originallyWho;
                usercount = peoplenow;
                maxusercount = maxpeople;
            }
            public string category;
            public string serverIpAddress;
            public long serverPort;
            public string name;
            public string city;
            public string country;
            public int distanceAway;
            public char zone;
            public string who;
            public Client[] whoObjectFromSourceData; // just to get the hash to work later. the who string is decorated but this is just data.
            public int usercount;
            public int maxusercount;
        }

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

        public class LatLong
        {
            public LatLong(string la, string lo) { lat = la; lon = lo; }
            public string lat;
            public string lon;
        }

        protected static Dictionary<string, LatLong> m_PlaceNameToLatLong = new Dictionary<string, LatLong>();
        public static Dictionary<string, LatLong> m_ipAddrToLatLong = new Dictionary<string, LatLong>();

        /*
        public static Dictionary<string, string> m_latFromDisclosedCityCountry = new Dictionary<string, string>();
        public static Dictionary<string, string> m_lonFromDisclosedCityCountry = new Dictionary<string, string>();
        */

        static Dictionary<string, LatLong> m_openCageCache = new Dictionary<string, LatLong>();

        // 1. Signature changed to async Task with a tuple result.
        // 'ref' parameters are removed.
        public static async Task<(bool Success, string Lat, string Lon)> CallOpenCageCachedAsync(string placeName)
        {
            if (m_openCageCache.ContainsKey(placeName))
            {
                var cachedLocation = m_openCageCache[placeName];
                if (cachedLocation == null)
                {
                    // We cached a failure, so just return failure
                    return (false, null, null);
                }

                // We cached a success, return the cached data
                return (true, cachedLocation.lat, cachedLocation.lon);
            }

            // 2. Not in cache, so 'await' the async network call
            var (success, lat, lon) = await CallOpenCageAsync(placeName);

            if (success)
            {
                // 3. Cache the successful result
                m_openCageCache[placeName] = new LatLong(lat, lon);
                return (true, lat, lon);
            }

            // 4. Cache the failure as 'null' so we don't ask again
            m_openCageCache[placeName] = null;
            return (false, null, null);
        }


        // 1. Signature changed to return an async Task with a ValueTuple
        // This replaces 'bool' and the 'ref' parameters.
        public static async Task<(bool Success, string Lat, string Lon)> CallOpenCageAsync(string placeName)
        {
            // 2. Early returns are now tuples
            if (placeName.Length < 3)
                return (false, null, null);
            if (placeName == "MOON")
                return (false, null, null);
            if (false == Regex.IsMatch(placeName, "[a-zA-Z]"))
                return (false, null, null);

            // 6. Add try/catch for network operations
            try
            {
                string encodedplace = System.Web.HttpUtility.UrlEncode(placeName);
                string endpoint = string.Format("https://api.opencagedata.com/geocode/v1/json?q={0}&key=4fc3b2001d984815a8a691e37a28064c", encodedplace);

                // 3. Use the shared 'httpClient' and 'await'
                string s = await httpClient.GetStringAsync(endpoint);

                JObject latLongJson = JObject.Parse(s);
                if (latLongJson["results"].HasValues)
                {
                    string typeOfMatch = (string)latLongJson["results"][0]["components"]["_type"];
                    if (("neighbourhood" == typeOfMatch) ||
                        ("village" == typeOfMatch) ||
                        ("city" == typeOfMatch) ||
                        ("county" == typeOfMatch) ||
                        ("municipality" == typeOfMatch) ||
                        ("administrative" == typeOfMatch) ||
                        ("state" == typeOfMatch) ||
                        ("boundary" == typeOfMatch) ||
                        ("country" == typeOfMatch))
                    {
                        // 4. Declare local variables and return them
                        string lat = (string)latLongJson["results"][0]["geometry"]["lat"];
                        string lon = (string)latLongJson["results"][0]["geometry"]["lng"];
                        // m_PlaceNameToLatLong[placeName.ToUpper()] = new LatLong(lat, lon);
                        return (true, lat, lon);
                    }
                }

                // 5. No match found
                return (false, null, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error calling OpenCage for {placeName}: {ex.Message}");
                return (false, null, null);
            }
        }

        // 1. Signature changed: No 'ref' params, returns a 'Task<LatLong>'
        public async Task<LatLong> PlaceToLatLonAsync(string serverPlace, string userPlace, string ipAddr)
        {
            ipAddr = ipAddr.Trim();
            serverPlace = serverPlace.Trim();
            userPlace = userPlace.Trim();

            System.Diagnostics.Debug.Assert(serverPlace.ToUpper() == serverPlace);
            System.Diagnostics.Debug.Assert(userPlace.ToUpper() == userPlace);

            // ... (Your cache flushing logic remains unchanged) ...
            var rng = new Random();
            if (0 == rng.Next(20000))
            {
                Console.WriteLine("Want to flush cached lat-longs, but they are even more scarce now, so only if things are not full-tilt.");
                if (m_secondsPause > 20)
                {
                    Console.WriteLine("Detected relative slowdown and flushed.");
                    m_PlaceNameToLatLong.Clear();
                    m_ipAddrToLatLong.Clear();
                }
            }

            // 2. Cache checks now return the LatLong object directly.
            if (m_PlaceNameToLatLong.TryGetValue(serverPlace, out var cachedServerPlace))
            {
                return cachedServerPlace;
            }

            if (m_PlaceNameToLatLong.TryGetValue(userPlace, out var cachedUserPlace))
            {
                return cachedUserPlace;
            }

            if (m_ipAddrToLatLong.TryGetValue(ipAddr, out var cachedIp))
            {
                return cachedIp;
            }

            // --- All checks below are now async ---

            bool fServerLLSuccess = false;
            string serverLat = "";
            string serverLon = "";

            if (serverPlace.Length > 1 && serverPlace != "yourCity")
            {
                // 3. Await the async call. Renamed tuple vars for clarity.
                var serverResult = await CallOpenCageAsync(serverPlace);
                if (serverResult.Success)
                {
                    serverLat = serverResult.Lat;
                    serverLon = serverResult.Lon;
                    Console.WriteLine("Used server location: " + serverPlace);
                    fServerLLSuccess = true;
                }
            }

            bool fUserLLSuccess = false;
            string userLat = "";
            string userLon = "";

            // 4. Await the second async call
            var userResult = await CallOpenCageAsync(userPlace);
            if (userResult.Success)
            {
                userLat = userResult.Lat;
                userLon = userResult.Lon;
                Console.WriteLine("Used user location: " + userPlace);
                fUserLLSuccess = true;
            }

            bool fServerIPLLSuccess = false;
            string serverIPLat = "";
            string serverIPLon = "";

            if (ipAddr.Length > 5)
            {
                // 5. This block is now fully async, uses the shared httpClient,
                //    and has proper error handling.
                try
                {
                    string ip4Addr = ipAddr.Replace("::ffff:", "");
                    string endpoint = "https://api.geoapify.com/v1/ipinfo?ip=" + ip4Addr + "&apiKey=" + GEOAPIFY_MYSTERY_STRING;

                    // 6. FIXED: Use shared 'httpClient' and 'await'
                    string s = await httpClient.GetStringAsync(endpoint);

                    JObject jsonGeo = JObject.Parse(s);
                    serverIPLat = (string)jsonGeo["location"]["latitude"];
                    serverIPLon = (string)jsonGeo["location"]["longitude"];

                    fServerIPLLSuccess = true;
                    var newIpLocation = new LatLong(serverIPLat, serverIPLon);
                    m_ipAddrToLatLong[ipAddr] = newIpLocation;
                    Console.WriteLine("AN IP geo has been cached: " + serverIPLat + " " + serverIPLon);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error fetching IP geolocation for {ipAddr}: {ex.Message}");
                    m_ipAddrToLatLong[ipAddr] = new LatLong("", ""); // Cache failure
                }
            }
            else
            {
                m_ipAddrToLatLong[ipAddr] = new LatLong("", ""); // Cache empty IP
            }

            // --- Final Logic: Decide which LatLong to return ---

            if (fServerIPLLSuccess)
            {
                if (fUserLLSuccess)
                {
                    char serverIPContinent = ContinentOfLatLong(serverIPLat, serverIPLon);
                    char userContinent = ContinentOfLatLong(userLat, userLon);
                    if (serverIPContinent == userContinent)
                    {
                        // Action: Use User's Place
                        var location = new LatLong(userLat, userLon);
                        m_PlaceNameToLatLong[serverPlace.ToUpper()] = location;
                        return location; // 7. Return the 'LatLong' object
                    }
                }

                if (false == fServerLLSuccess)
                {
                    // Action: Use Server IP's location
                    // (Already cached in m_ipAddrToLatLong, but we return it)
                    return m_ipAddrToLatLong[ipAddr];
                }
            }

            // Default Action: Use Server's Place
            Debug.Assert(serverLat != null);
            Debug.Assert(serverLon != null); // Original code had 'lat != null' twice

            var serverLocation = new LatLong(serverLat, serverLon);
            m_PlaceNameToLatLong[serverPlace.ToUpper()] = serverLocation;
            return serverLocation;
        }
    
        // Here we note who s.who is, because we care how long a person has been on a server. Nothing more than that for now.
        protected void NotateWhoHere(string server, string who)
        {
            Debug.Assert(who.Length == "b707dc8fc6516826fbe9b4aa84d1553a".Length);
            //            Console.WriteLine(server, who);
            string hash = who + server;

            try
            {
                // maybe we never heard of them.
                if (false == m_connectionFirstSighting.ContainsKey(hash))
                {
                    m_connectionFirstSighting[hash] = DateTime.Now;
                    return; // don't forget the finally!
                }

                // ok, we heard of them. Have 10 minutes elapsed since we saw them last? Like, maybe nobody has run my app. So ten mins.
                if (DateTime.Now > m_connectionLatestSighting[hash].AddMinutes(10))
                {
                    // Yeah? Restart their initial sighting clock.
                    m_connectionFirstSighting[hash] = DateTime.Now;
                }

                // we saw them recently. Just update their last Time Last Seen...
            }
            finally
            {
                m_connectionLatestSighting[hash] = DateTime.Now;
            }
        }


        protected double DurationHereInMins(string server, string who)
        {
            Debug.Assert(who.Length == "b707dc8fc6516826fbe9b4aa84d1553a".Length);
            string hash = who + server;
            if (m_connectionFirstSighting.ContainsKey(hash))
            {
                TimeSpan ts = DateTime.Now.Subtract(m_connectionFirstSighting[hash]);

                var nonSignals = System.IO.File.ReadAllLines("non-signals.txt").ToList();
                if (nonSignals.Contains(who))
                    ts = ts.Add(TimeSpan.FromMinutes(60.0 * 5.75));

                return ts.TotalMinutes;
            }
            return -1; //
        }


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

        public string SmartCity(string city, Client[] users)
        {
            string smartCity = city;

            if ((smartCity == "") || (smartCity == "yourCity")) // this didn't work: || (smartCity == "-"))
            {
                // Hate these. Estimate a city based on participation.
                List<string> cities = new List<string>();
                List<string> nations = new List<string>();
                foreach (var u in users)
                {
                    cities.Add(u.city);
                    nations.Add(u.country);
                }

                if (cities.Count > 0)
                {
                    var citiGroup = cities.GroupBy(x => x);
                    var maxCountr = citiGroup.Max(g => g.Count());
                    var mostCommonCity = citiGroup.Where(x => x.Count() == maxCountr).Select(x => x.Key).ToArray();
                    if (mostCommonCity.GetLength(0) > 0)
                        smartCity = mostCommonCity[0];

                    // I still fuckin hate blanks. 
                    if ((smartCity == "") || (smartCity == "-"))
                    {
                        var nationGroup = nations.GroupBy(x => x);
                        var maxCountry = nationGroup.Max(g => g.Count());
                        var mostCommonCountry = nationGroup.Where(x => x.Count() == maxCountry).Select(x => x.Key).ToArray();
                        if (mostCommonCountry.GetLength(0) > 0)
                            smartCity = mostCommonCountry[0];
                    }
                }
            }

            if (smartCity == "-")
                smartCity = "";

            string pattern = @"\([^)]*\)";
            string textWithoutParentheses = Regex.Replace(smartCity, pattern, "");


            return textWithoutParentheses;
        }


        static List<ServersForMe> m_allMyServers = null;

        const string WholeMiddotString = " &#xB7; ";


        public string SmartNations(Client[] whoObject, string servercountry)
        {
            string smartNations = "";
            bool fNeeded = false;

            // if more than 49% of users specify no nation, then there's no smartNation.
            int iCountryless = 0;
            foreach (var who in whoObject)
            {
                if (who.country == "World" || who.country.Length < 2)
                {
                    iCountryless++;
                }
            }

            int itotalpeeps = whoObject.GetLength(0);

            if (((float)iCountryless / (float)itotalpeeps) >= 0.5)
                return "";

            foreach (var who in whoObject)
            {
                if (who.country.Length > 1)
                    if (who.country != servercountry)
                    {
                        fNeeded = true;
                        break;
                    }
            }

            if (fNeeded)
                foreach (var who in whoObject)
                {
                    if (who.country.Length > 1)
                        if (false == smartNations.Contains(who.country))
                            if (who.country != "World") // annoying!
                                smartNations += who.country + WholeMiddotString; // middle dot
                }

            // if contains more middle-dots, represnted here by the &, abbreviate to UK and US
            int totalNations = smartNations.Count(s => s == '&');
            if (totalNations > 2)
            {
                smartNations = smartNations.Replace("United States", "USA");
                smartNations = smartNations.Replace("United Kingdom", "UK");
                smartNations = smartNations.Replace("Hong Kong", "HK");
            }

            // chlop the trailing dot...
            if (smartNations.Length > 0)
                smartNations = smartNations.Substring(0, smartNations.Length - WholeMiddotString.Length);

            // If there are no spaces in any country names,
            // and more than 3 countries,
            // I will replace the space-dot-space with just a space
            // to find this out, replace dots-and-spaces before testing...
            if (totalNations > 3)
            {
                string squishedNations = smartNations.Replace(WholeMiddotString, "");
                if (false == squishedNations.Contains(" "))
                    smartNations = smartNations.Replace(WholeMiddotString, " ");
            }

            // hey, who knows why, but once i saw the city as Canada, with the nationality as Canada.
            // add this code back when i can test it! my Canada duplication scenario disappeared...
            // and i can't reall extract a onesie among many scenario right now. just a total match of one country
            if (smartNations == servercountry)
                return "";

            return smartNations;
        }




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



        string BackgroundByZone(char zone)
        {
            switch (zone) // #D9F9F9 is default
            {
                case 'E':
                    return " style=\"background: #AFFFC6\"";

                case 'N':
                    return " style=\"background: #C1F1FF\"";

                case 'A':
                    return " style=\"background: #E7FFFF\"";

                case 'S':
                    return " style=\"background: #F9EAEA\"";
            }
            return "";
        }




        char ContinentOfLatLong(string lat, string lon)
        {
            double latD = Convert.ToDouble(lat);
            double lonD = Convert.ToDouble(lon);

            char zone = 'X';

            // hardcode with the latitude and longitude of New York City
            double latNA = 40.7128;
            double longNA = -74.0060;
            int distFromNA = Distance(latNA, longNA, latD, lonD);
            // hardcode with latiutde and longitude of Moscow
            double latEU = 55.7558;
            double longEU = 37.6173;
            int distFromEU = Distance(latEU, longEU, latD, lonD);
            // hardcode with latitude and longitude of Okinowa
            double latAS = 26.2125;
            double longAS = 127.6800;
            int distFromAS = Distance(latAS, longAS, latD, lonD);
            // hardcode with lat long of Manaus
            double latSA = 4.57;
            double longSA = -74.0217;
            int distFromSA = Distance(latSA, longSA, latD, lonD);

            if (distFromNA < distFromEU)
                if (distFromNA < distFromAS)
                    if (distFromNA < distFromSA)
                    {
                        if (latD < 25.0) // i don't know why, my formula just sux
                            zone = 'S';
                        else
                            zone = 'N';
                    }

            if (distFromEU < distFromNA)
                if (distFromEU < distFromAS)
                    if (distFromEU < distFromSA)
                        zone = 'E';

            if (distFromAS < distFromNA)
                if (distFromAS < distFromEU)
                    if (distFromAS < distFromSA)
                        zone = 'A';

            if (distFromSA < distFromNA)
                if (distFromSA < distFromEU)
                    if (distFromSA < distFromAS)
                        zone = 'S';

            return zone;
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



        public static Dictionary<string, string> m_connectedLounges = new Dictionary<string, string>();
        public static List<string> m_listenLinkDeployment = new List<string>();
        public static int m_snippetsDeployed = 0;


        public static Dictionary<string, JObject> m_ipapiOutputs = new Dictionary<string, JObject>();
        static int m_hourLastFlushed = -1;

        public static Dictionary<string, DateTime> m_ArnOfIpGoodUntil = new Dictionary<string, DateTime>();
        public static Dictionary<string, string> m_ArnOfIp = new Dictionary<string, string>();



public static async Task<JObject> GetClientIPDetailsAsync(string clientIP)
{
    if (DateTime.Now.Hour != m_hourLastFlushed)
     {
     m_ipapiOutputs.Clear();
     m_hourLastFlushed = DateTime.Now.Hour;
     }

     if (m_ipapiOutputs.ContainsKey(clientIP))
     return m_ipapiOutputs[clientIP];

     try
     {
     // Use a CancellationTokenSource for the 1-second timeout
     using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
     {
     // Use the static shared httpClient and await the non-blocking call
     string st = await httpClient.GetStringAsync("http://ip-api.com/json/" + clientIP, cts.Token);
     JObject json = JObject.Parse(st);
     m_ipapiOutputs[clientIP] = json;
     return json;
     }
     }
     catch (OperationCanceledException)
     {
     Console.WriteLine($"ip-api.com lookup TIMED OUT for {clientIP}.");
     return null; // Return null on timeout
     }
     catch (Exception ex)
     {
     Console.WriteLine($"ip-api.com lookup FAILED for {clientIP}: {ex.Message}");
     return null;
     }
}


        public static async Task<string> AsnOfThisIpAsync(string ip)
        {
        RE_SAMPLE:
            if (false == m_ArnOfIpGoodUntil.ContainsKey(ip))
            {
                string endpoint = "http://ip-api.com/json/" + ip;
                using var client = new HttpClient();

                // --- FIX: Await the async call instead of blocking ---
                string st = await client.GetStringAsync(endpoint);

                JObject jsonGeo = JObject.Parse(st);
                Random rnd = new Random();
                m_ArnOfIp[ip] = jsonGeo["as"]?.ToString();
                m_ArnOfIpGoodUntil[ip] = DateTime.Now.AddMinutes(rnd.Next(60 * 22, 60 * 26));
                return m_ArnOfIp[ip];
            }
            else
            {
                if (m_ArnOfIpGoodUntil[ip] > DateTime.Now)
                    return m_ArnOfIp[ip];

                // mapping is stale.
                m_ArnOfIpGoodUntil.Remove(ip);
                goto RE_SAMPLE;
            }
        }


        static Dictionary<string, List<string>> m_predicted = new Dictionary<string, List<string>>();
        static int m_lastMinSampledPredictions = -1;


        // --- Caching Fields for Preloaded Data ---
        // These fields will store the file data and reload it every 65 seconds.
        private static PreloadedData m_cachedPreloadedData;
        private static DateTime m_lastDataLoadTime = DateTime.MinValue;
        private const double CacheDurationSeconds = 65.0;
        private static readonly SemaphoreSlim _dataLoadLock = new SemaphoreSlim(1, 1);


        public async Task<string> GetGutsRightNow()
        {
            // 1. Initialize state
            InitializeGutsRequest();

            // 2. Fetch remote prediction data
            await UpdatePredictionsIfNeededAsync();

            // 3. Get cached data, refreshing if older than 65 seconds
            var preloadedData = await GetCachedPreloadedDataAsync();

            // 4. Process all servers and clients to build the internal list
            await ProcessServerListsAsync(preloadedData);

            // 5. Sort the processed servers by distance
            IEnumerable<ServersForMe> sortedByDistanceAway = m_allMyServers.OrderBy(svr => svr.distanceAway);

            // 6. Generate the final HTML from the sorted list
            string output = await GenerateServerListHtmlAsync(sortedByDistanceAway, preloadedData);

            return output;
        }

        //################################################################################
        // NEW CACHING HELPER METHOD
        //################################################################################

        /// <summary>
        /// Gets the preloaded data, refreshing it from disk if the cache is older than 65 seconds.
        /// </summary>
        // 1. Signature changed to 'async Task<...>'
        private async Task<PreloadedData> GetCachedPreloadedDataAsync()
        {
            // First, check without a lock (fast path, unchanged)
            if (DateTime.Now <= m_lastDataLoadTime.AddSeconds(CacheDurationSeconds))
            {
                return m_cachedPreloadedData;
            }

            // Cache is expired, so acquire the async-friendly semaphore
            // 2. Replaced 'lock' with 'await WaitAsync'
            await _dataLoadLock.WaitAsync();
            try
            {
                // Now that we have the lock, double-check
                if (DateTime.Now > m_lastDataLoadTime.AddSeconds(CacheDurationSeconds))
                {
                    Console.WriteLine($"Cache expired ({CacheDurationSeconds}s). Reloading all data files...");

                    // 3. Call the ASYNC version of your load method
                    // (We will need to create this method next)
                    m_cachedPreloadedData = await LoadPreloadedDataAsync();

                    m_lastDataLoadTime = DateTime.Now;
                    Console.WriteLine("Data file reload complete.");
                }
            }
            finally
            {
                // 4. Release the semaphore
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
    
        /// <summary>
        /// Iterates through the master server lists and processes each server.
        /// Populates the 'm_allMyServers' list.
        /// </summary>
        private async Task ProcessServerListsAsync(PreloadedData data)
        {
            foreach (var key in LastReportedList.Keys)
            {
                var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(LastReportedList[key]);

                foreach (var server in serversOnList)
                {
                    int people = server.clients?.GetLength(0) ?? 0;

                    if (ShouldSkipServer(server, people))
                    {
                        continue;
                    }

                    // Process all clients on this server
                    var clientResult = ProcessServerClients(server, data);
                    if (string.IsNullOrEmpty(clientResult.WhoHtml))
                    {
                        continue; // All clients were filtered out
                    }

                    // Determine server and user locations
                    var (place, usersPlace, serverCountry) = GetServerAndUserLocation(server, clientResult.UserCountries);

                    // Get distance and zone (NOW ASYNC)
                    // This is the changed line:
                    var (dist, zone) = await CalculateServerDistanceAndZoneAsync(place, usersPlace, server.ip);

                    // Apply distance boost for solo users
                    dist = CalculateBoostedDistance(server, dist, clientResult.FirstUserHash);

                    if (dist < 250) dist = 250;

                    m_allMyServers.Add(new ServersForMe(
                        key, server.ip, server.port, server.name, server.city, serverCountry,
                        dist, zone, clientResult.WhoHtml, server.clients, people, (int)server.maxclients
                    ));
                }
            }
        }

        /// <summary>
        /// Checks if a server should be skipped based on initial filter rules.
        /// </summary>
        private bool ShouldSkipServer(JamulusServers server, int people)
        {
            if (server.name.ToLower().Contains("script") ||
                server.city.ToLower().Contains("script") ||
                server.name.ToLower().Contains("jxw") ||
                server.city.ToLower().Contains("peterborough") ||
                server.name.ToLower().Contains("peachjam3"))
            {
                return true;
            }

            if (people < 1)
            {
                return true; // Don't care about empty servers
            }

            // CBVB logic from original was commented out, so not included here.
            // if (server.name.ToLower().Contains("cbvb")) { ... }

            return false;
        }

        /// <summary>
        /// Processes all clients on a server, filters them, and builds the 'Who' HTML string.
        /// </summary>
        private (string WhoHtml, List<string> UserCountries, string FirstUserHash) ProcessServerClients(JamulusServers server, PreloadedData data)
        {
            string who = "";
            List<string> userCountries = new List<string>();
            string firstUserHash = null;

            foreach (var guy in server.clients)
            {
                if (guy.name.ToLower().Contains("script"))
                {
                    continue;
                }

                string musicianHash = GetHash(guy.name, guy.country, guy.instrument);

                // Check if this client's most recent ASN is blocked
                if (IsClientASNBlocked(musicianHash, data.JoinEventsLines, data.BlockedASNs))
                {
                    continue;
                }

                NotateWhoHere(server.ip + ":" + server.port, musicianHash);

                if (NukeThisUsername(guy.name, guy.instrument, server.name.ToUpper().Contains("CBVB")))
                {
                    continue;
                }

                if (firstUserHash == null)
                {
                    firstUserHash = musicianHash;
                }

                // Build the HTML for this specific client
                string clientHtml = BuildClientHtml(guy, server, musicianHash);
                if (string.IsNullOrEmpty(clientHtml))
                {
                    continue;
                }

                who += clientHtml;
                userCountries.Add(guy.country.ToUpper());
            }

            return (who, userCountries, firstUserHash);
        }

        /// <summary>
        /// Builds the HTML snippet for a single client.
        /// </summary>
        private string BuildClientHtml(Client guy, JamulusServers server, string encodedHashOfGuy)
        {
            string slimmerInstrument = (guy.instrument == "-") ? "" : guy.instrument;
            if (slimmerInstrument.Length > 0)
            {
                slimmerInstrument = " " + slimmerInstrument;
            }

            var nam = guy.name.Trim().Replace("  ", " ").Replace("  ", " ").Replace("  ", " ").Replace("<", "");

            // Filter out nameless/streamer clients
            if (nam.Length == 0 && (slimmerInstrument == "" || slimmerInstrument == " Streamer"))
            {
                return null;
            }

            // Determine font size
            string font = "<font size='+0'>";
            if (server.clients.GetLength(0) > 11)
            {
                font = "<font size='-1'>";
            }
            else
            {
                foreach (var longguy in server.clients)
                {
                    if (longguy.name.Length > 14 && slimmerInstrument.Length > 0)
                    {
                        font = "<font size='-1'>";
                        break;
                    }
                }
            }

            string hash = guy.name + guy.country; // Used for JS toggle

            var newpart = "<span class=\"musician " +
                server.ip + " " + encodedHashOfGuy + "\"" +
                " id =\"" + hash + "\"" +
                " onmouseover=\"this.style.cursor='pointer'\" onmouseout=\"this.style.cursor='default'\" onclick=\"toggle('" + hash + "')\";>" +
                font +
                "<b>" + nam + "</b>" +
                "<i>" + slimmerInstrument + "</i></font></span>\n";

            if (server.clients.GetLength(0) < 17)
            {
                newpart += "<br>";
            }
            else if (guy != server.clients[server.clients.GetLength(0) - 1])
            {
                newpart += " · ";
            }

            return newpart;
        }


        /// <summary>
        /// Efficiently checks if a client's ASN is on the block list.
        /// </summary>
        private bool IsClientASNBlocked(string musicianHash, string[] joinEvents, HashSet<string> blockedASNs)
        {
            string mostLikelyASN = null;
            const int asnColumnIndex = 12;

            for (int i = joinEvents.Length - 1; i >= 0; i--)
            {
                string candidateLine = joinEvents[i];

                if (!candidateLine.Contains(musicianHash) || string.IsNullOrWhiteSpace(candidateLine))
                {
                    continue;
                }

                string[] fields = candidateLine.Split(',');
                if (fields.Length > asnColumnIndex)
                {
                    string fullAsnField = fields[asnColumnIndex].Trim();
                    if (fullAsnField.StartsWith("AS"))
                    {
                        string asnIdentifier = fullAsnField.Split(' ')[0];
                        if (asnIdentifier.Length > 0)
                        {
                            mostLikelyASN = asnIdentifier;
                            break; // Found the most recent ASN
                        }
                    }
                }
            }

            return mostLikelyASN != null && blockedASNs.Contains(mostLikelyASN);
        }

        /// <summary>
        /// Determines the server's location and the most common user location.
        /// </summary>
        private (string Place, string UsersPlace, string ServerCountry) GetServerAndUserLocation(JamulusServers server, List<string> userCountries)
        {
            string place = "";
            string serverCountry = "";
            string usersPlace = "Moon";

            if (server.city.Length > 1)
            {
                place = server.city;
            }
            if (server.country.Length > 1)
            {
                if (place.Length > 1) place += ", ";
                place += server.country;
                serverCountry = server.country;
            }

            if (userCountries.Count > 0)
            {
                var mostCommons = userCountries.GroupBy(x => x)
                                               .OrderByDescending(g => g.Count())
                                               .Select(x => x.Key)
                                               .ToArray();
                string usersCountry = mostCommons[0];

                List<string> cities = new List<string>();
                foreach (var guy in server.clients)
                {
                    if (guy.country.ToUpper() == usersCountry && guy.city.Length > 0)
                    {
                        cities.Add(guy.city.ToUpper());
                    }
                }

                string usersCity = "";
                if (cities.Count > 0)
                {
                    usersCity = cities.GroupBy(x => x)
                                      .OrderByDescending(g => g.Count())
                                      .Select(x => x.Key)
                                      .FirstOrDefault();
                }

                usersPlace = usersCountry;
                if (usersCity.Length > 1)
                {
                    usersPlace = usersCity + ", " + usersCountry;
                }
            }

            if (place.Contains("208, "))
            {
                place = place.Replace("208, ", "");
            }

            return (place, usersPlace, serverCountry);
        }

        /// <summary>
        /// Calculates the distance and time zone for a server.
        /// </summary>
        // 1. Signature changed: no 'out' params, returns a 'Task' with a tuple
        private async Task<(int dist, char zone)> CalculateServerDistanceAndZoneAsync(string place, string usersPlace, string serverIp)
        {
            // 2. Call the new async method and await its 'LatLong' result
            LatLong location = await PlaceToLatLonAsync(place.ToUpper(), usersPlace.ToUpper(), serverIp);

            // 3. Initialize local variables
            int dist = 0;
            char zone = ' ';

            // 4. Use the 'LatLong' object's properties
            if (location != null && (location.lat.Length > 1 || location.lon.Length > 1))
            {
                // This is the changed line
                dist = await DistanceFromClientAsync(location.lat, location.lon);

                zone = ContinentOfLatLong(location.lat, location.lon);
            }
            
            // 5. Return the tuple result
            return (dist, zone);
        }
        
        /// <summary>
        /// Applies a "boost" to the distance (making it seem closer) if a user is solo.
        /// </summary>
        private int CalculateBoostedDistance(JamulusServers server, int initialDistance, string firstUserHash)
        {
            if (server.clients.Length != 1 || firstUserHash == null)
            {
                return initialDistance;
            }

            double boost = DurationHereInMins(server.ip + ":" + server.port, firstUserHash);
            if (boost < 3.0)
            {
                boost = 3.0;
            }

            // starts hella close
            return (int)((double)initialDistance * (boost / 6));
        }

        /// <summary>
        /// Loads the 'm_connectedLounges' map from the remote JSON.
        /// </summary>
        private async Task LoadConnectedLoungesAsync()
        {
            if (m_connectedLounges.Count > 0)
            {
                return; // Already loaded
            }

            try
            {
                HttpClient client = new HttpClient();
                HttpResponseMessage response = await client.GetAsync("https://mjth.live/lounges.json");
                response.EnsureSuccessStatusCode();
                string responseBody = await response.Content.ReadAsStringAsync();
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(responseBody);
                foreach (var kvp in data)
                {
                    m_connectedLounges[kvp.Value] = kvp.Key; // swap 'em
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load lounges.json: {ex.Message}");
            }

            // Add hardcoded lounges
            m_connectedLounges["https://lobby.jam.voixtel.net.br/"] = "179.228.137.154:22124";
            m_connectedLounges["https://openjam.klinkanha.com/"] = "43.208.146.31:22124";
            m_connectedLounges["https://icecast.voixtel.net.br:8000/stream"] = "189.126.207.3:22124";
            m_connectedLounges["http://1.onj.me:32123/"] = "139.162.251.38:22124";
            m_connectedLounges["http://3.onj.me:8000/jamulus4"] = "69.164.213.250:22124";
        }


        /// <summary>
        /// Generates the final HTML output by iterating over the sorted server list.
        /// </summary>
        private async Task<string> GenerateServerListHtmlAsync(IEnumerable<ServersForMe> sortedServers, PreloadedData data)
        {
            var output = new System.Text.StringBuilder();

            // Load lounge data once before looping
            await LoadConnectedLoungesAsync();

            foreach (var s in sortedServers)
            {
                // --- Server Block List Checks ---
                string serverIpPort = s.serverIpAddress + ":" + s.serverPort;
                if (data.ErasedServerNames.Any(erased => s.name.ToLower().Contains(erased)))
                {
                    continue;
                }
                if (data.NoPingIpPartial.Any(ip => serverIpPort.Contains(ip)))
                {
                    continue;
                }

                // --- FIX: Await the new async method ---
                string asn = await AsnOfThisIpAsync(s.serverIpAddress);
                
                if (data.BlockedServerARNs.Contains(asn))
                {
                    Console.WriteLine(s.serverIpAddress + " blocked because in asn " + asn);
                    continue;
                }
                // --- End Block List Checks ---

                // Get a cleaner city name
                string evenSmarterCity = await GetSmarterCityAsync(s.city, s.serverIpAddress);

                // Re-filter users based on NukeThisUsername
                var filteredUsers = s.whoObjectFromSourceData
                    .Where(cat => !NukeThisUsername(cat.name, cat.instrument, s.name.ToLower().Contains("cbvb")))
                    .ToList();

                int s_myUserCount = filteredUsers.Count;

                if (s_myUserCount > 1)
                {
                    output.Append(await BuildMultiUserServerCardAsync(s, filteredUsers, evenSmarterCity));
                }
                else
                {
                    output.Append(BuildSingleUserServerCard(s, filteredUsers, evenSmarterCity, data.GoodGuids));
                }
            }
            return output.ToString();
        }


        /// <summary>
        /// Gets a cleaner city name, querying ip-api.com if necessary.
        /// </summary>
        private async Task<string> GetSmarterCityAsync(string city, string ipAddress)
        {
            string evenSmarterCity = city.Replace(" Vultr", "");

            if (("AWS" == evenSmarterCity) || ("Linode Cloud" == evenSmarterCity))
            {
                if (!m_ipapiOutputs.ContainsKey(ipAddress))
                {
                    try
                    {
                        var client = new HttpClient();
                        string st = await client.GetStringAsync("http://ip-api.com/json/" + ipAddress);
                        m_ipapiOutputs[ipAddress] = JObject.Parse(st);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"ip-api.com failed: {ex.Message}");
                        return evenSmarterCity; // return original on failure
                    }
                }

                if (m_ipapiOutputs.TryGetValue(ipAddress, out JObject json) && json["city"] != null)
                {
                    return json["city"].ToString();
                }
            }

            return evenSmarterCity;
        }

        /// <summary>
        /// Builds the HTML card for a server with multiple users.
        /// </summary>
        private async Task<string> BuildMultiUserServerCardAsync(ServersForMe s, List<Client> filteredUsers, string evenSmarterCity)
        {
            // --- Timeout/Suppression Check ---
            int iTimeoutPeriod = s.name.ToLower().Contains("priv") ? (4 * 60) : (8 * 60);
            bool fSuppress = true;
            foreach (var user in filteredUsers)
            {
                if (DurationHereInMins(s.serverIpAddress + ":" + s.serverPort, GetHash(user.name, user.country, user.instrument)) < iTimeoutPeriod)
                {
                    fSuppress = false;
                    break;
                }
            }
            if (fSuppress) return ""; // Skip it
            // --- End Suppression Check ---

            var newline = new System.Text.StringBuilder();
            string serverAddress = s.serverIpAddress + ":" + s.serverPort;

            string newJamFlag = GetNewJamFlag(s, filteredUsers);
            string smartcity = SmartCity(evenSmarterCity, filteredUsers.ToArray());
            string smartNations = SmartNations(filteredUsers.ToArray(), s.country);
            string listenNow = await GetListenHtmlAsync(s);
            string liveSnippet = (listenNow.Length == 0) ? await GetSnippetHtmlAsync(serverAddress) : "";
            string htmlForVideoUrl = GetVideoHtml(serverAddress);
            string titleToShow = GetSongTitleHtml(s.serverIpAddress + "-" + s.serverPort);
            string leaversHtml = GetLeaversHtml(s);
            string soonHtml = GetSoonHtml(serverAddress);
            string activeJitsi = FindActiveJitsiOfJSvr(serverAddress);

            newline.Append($"<div id=\"{serverAddress}\" {BackgroundByZone(s.zone)}><center>");

            if (s.name.Length > 0)
            {
                string name = s.name.Contains("CBVB") ? s.name + " (Regional)" : s.name;
                newline.Append(System.Web.HttpUtility.HtmlEncode(name) + "<br>");
            }

            if (smartcity.Length > 0) newline.Append("<b>" + smartcity + "</b><br>");

            newline.Append($"<font size='-1'>{s.category.Replace("Genre ", "").Replace(" ", "&nbsp;")}</font><br>");
            if (newJamFlag.Length > 0) newline.Append(newJamFlag + "<br>");
            if (activeJitsi.Length > 0) newline.Append($"<b><a target='_blank' href='{activeJitsi}'>Jitsi Video</a></b>");
            if (NoticeNewbs(serverAddress)) newline.Append(LocalizedText("(New server.)", "(新伺服器)", "(เซิร์ฟเวอร์ใหม่)", "(neuer Server)", "(Nuovo server.)") + "<br>");

            newline.Append(liveSnippet);
            newline.Append(listenNow);
            newline.Append(htmlForVideoUrl);
            newline.Append(titleToShow);
            newline.Append("</center><hr>");
            newline.Append(s.who); // The 'who' string built during server processing
            newline.Append(leaversHtml);

            if (smartcity != smartNations)
            {
                newline.Append($"<center><font size='-2'>{smartNations.Trim()}</font></center>");
            }

            if (soonHtml.Length > 0) newline.Append($"<center>{soonHtml}</center>");

            newline.Append("</div>");

            return newline.ToString();
        }

        /// <summary>
        /// Builds the HTML card for a server with only one (filtered) user.
        /// </summary>
        private string BuildSingleUserServerCard(ServersForMe s, List<Client> filteredUsers, string evenSmarterCity, HashSet<string> goodGuids)
        {
            var firstUser = filteredUsers.FirstOrDefault();
            if (firstUser == null) return ""; // Should not happen if s_myUserCount=1, but safe

            string userHash = GetHash(firstUser.name, firstUser.country, firstUser.instrument);

            // --- GUID and Timeout Check ---
            if (!goodGuids.Contains(userHash))
            {
                return "";
            }

            const int maxDurationMinutes = 6 * 60; // 6 hours
            string serverAddress = $"{s.serverIpAddress}:{s.serverPort}";
            if (DurationHereInMins(serverAddress, userHash) > maxDurationMinutes)
            {
                return ""; // Sat there for 6 hours
            }
            // --- End Check ---

            var excludedServerNames = new HashSet<string> { "JamPad", "portable" };
            if (excludedServerNames.Contains(s.name))
            {
                return "";
            }

            var htmlBuilder = new System.Text.StringBuilder();
            string smartCity = SmartCity(evenSmarterCity, filteredUsers.ToArray());
            string whoStringNoBreaks = s.who.Replace("<br/>", " "); // Original logic
            string categoryDisplay = s.category.Replace("Genre ", "").Replace(" ", "&nbsp;");

            htmlBuilder.Append($"<div {BackgroundByZone(s.zone)}><center>");

            if (!string.IsNullOrEmpty(s.name))
            {
                htmlBuilder.Append($"{System.Web.HttpUtility.HtmlEncode(s.name)}<br>");
            }
            if (!string.IsNullOrEmpty(smartCity))
            {
                htmlBuilder.Append($"<b>{smartCity}</b><br>");
            }

            htmlBuilder.Append($"<font size='-1'>{categoryDisplay}</font><br>");

            if (NoticeNewbs(serverAddress))
            {
                htmlBuilder.Append("(New server.)<br>");
            }

            htmlBuilder.Append("</center><hr>");
            htmlBuilder.Append(whoStringNoBreaks); // The 'who' string
            htmlBuilder.Append(DurationHere(serverAddress, userHash));
            htmlBuilder.Append("</div>");

            return htmlBuilder.ToString();
        }

        // --- Multi-User CardBuilder Sub-Helpers ---

        private string GetNewJamFlag(ServersForMe s, List<Client> users)
        {
            string newJamFlag = "";
            bool allNew = true;
            foreach (var user in users)
            {
                if (DurationHereInMins(s.serverIpAddress + ":" + s.serverPort, GetHash(user.name, user.country, user.instrument)) >= 14)
                {
                    allNew = false;
                    break;
                }
            }

            if (allNew)
            {
                string translatedPhrase = LocalizedText("Just&nbsp;gathered.", "成員皆剛加入", "เพิ่งรวมตัว", "soeben&nbsp;angekommen.", "appena&nbsp;connessi.");
                newJamFlag = "(" + ((s.usercount == s.maxusercount) ? LocalizedText("Full. ", "滿房。 ", "เต็ม ", "Volls. ", "Pieno. ") : "") + translatedPhrase + ")";
            }
            else if (s.usercount == s.maxusercount)
            {
                newJamFlag = "<b>(" + LocalizedText("Full", "滿房", "เต็ม", "Voll", "piena") + ")</b>";
            }
            else if (s.usercount + 1 == s.maxusercount)
            {
                newJamFlag = LocalizedText("(Almost full)", "(即將滿房)", "(เกือบเต็ม)", "(fast voll)", "(quasi pieno)");
            }
            return newJamFlag;
        }

        private async Task<string> GetListenHtmlAsync(ServersForMe s)
        {
            string ipport = s.serverIpAddress + ":" + s.serverPort;

            // Check m_connectedLounges (loaded once)
            foreach (var url in m_connectedLounges.Keys)
            {
                if (m_connectedLounges[url].Contains(ipport))
                {
                    foreach (var user in s.whoObjectFromSourceData)
                    {
                        if (user.name.Contains("obby") || user.name == "")
                        {
                            string num = "";
                            var iPos = user.name.IndexOf("[");
                            if (iPos > 0 && '0' != user.name[iPos + 1])
                            {
                                num = "<sub> " + user.name[iPos + 1] + "</sub>";
                            }
                            m_listenLinkDeployment.Add(ipport);
                            return $"<b><a class='listenlink listenalready' target='_blank' href='{url}'>Listen</a></b>{num}</br>";
                        }
                    }
                }
            }

            // --- Fallback: Check for dockable instance ---
            string currentHear = m_connectedLounges.ContainsKey("https://hear.jamulus.live/") ? m_connectedLounges["https://hear.jamulus.live/"] : null;
            bool a = false; // InstanceIsFree("http://hear.jamulus.live/free.txt", currentHear); // Original was false
            bool b = false; // InstanceIsFree("http://radio.jamulus.live/free.txt"); // Original was false

            if (!a && !b) return ""; // No free instances
            if (s.name.ToLower().Contains("priv") || s.usercount >= s.maxusercount) return ""; // Can't dock

            try
            {
                using var httpClient2 = new HttpClient();
                var contents2 = await httpClient2.GetStringAsync("https://jamulus.live/can-dock.txt");
                //if (contents2.Contains(ipport) && !AnyoneBlockStreaming(ipport))
                if (false) // not happening now
                {
                    var lobby = System.IO.File.ReadAllLines("lobby.txt").ToList();
                    if (lobby.Count <= 1)
                    {
                        string clientIP = HttpContext.Connection.RemoteIpAddress.ToString();
                        JObject json = await GetClientIPDetailsAsync(clientIP);
                        if (json != null && !forbidder.m_forbiddenIsp.Contains(json["as"].ToString()))
                        {
                            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(ipport + DateTime.UtcNow.Hour);
                            var interimStep = System.Security.Cryptography.MD5.HashData(bytes);
                            var saltedHashOfDestination = ToHex(interimStep, false).Substring(0, 4); // Assumes ToHex helper

                            m_listenLinkDeployment.Add(ipport);
                            return $"<a class='listenlink listen' target='_blank' href='https://jamulus.live/dock/{saltedHashOfDestination}'>Listen</a></br>";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking can-dock.txt: {ex.Message}");
            }

            return "";
        }

        private async Task<string> GetSnippetHtmlAsync(string serverAddress)
        {
            string DIR = "";
#if WINDOWS
            DIR = "C:\\Users\\User\\JamFan22\\JamFan22\\wwwroot\\mp3s\\";
#else
            DIR = "/root/JamFan22/JamFan22/wwwroot/mp3s/";
#endif

            // Check for remote .sil file
            string url = $"https://jamulus.live/mp3s/{serverAddress}.sil";
            using (var httpClient = new HttpClient())
            {
                try
                {
                    var response = await httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
                    if (response.IsSuccessStatusCode)
                    {
                        return "(Silent)";
                    }
                }
                catch (HttpRequestException ex)
                {
                    Console.WriteLine($"Error checking file at URL: {ex.Message}");
                }
            }

            // Check for local .mp3 file
            try
            {
                var files = Directory.GetFiles(DIR, serverAddress + ".mp3");
                if (files.Length > 0)
                {
                    string myFile = Path.GetFileName(files[0]);
                    m_snippetsDeployed++;
                    return $"<audio class='playa' controls style='width: 150px;' src='mp3s/{myFile}' />";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking local mp3s: {ex.Message}");
            }

            return "";
        }

        private string GetVideoHtml(string serverAddress)
        {
            if (harvest.m_discreetLinks.TryGetValue(serverAddress, out string videoUrl))
            {
                if (videoUrl.ToLower().Contains("zoom"))
                    return $"<a class='vid' href='{videoUrl}'><b>Zoom Video</b></a><br>";
                if (videoUrl.ToLower().Contains("https://meet."))
                    return $"<a class='vid' href='{videoUrl}'><b>Meet Video</b></a><br>";
                if (videoUrl.ToLower().Contains("jit.si"))
                    return "<b>Jitsi Video</b><br>";
                if (videoUrl.ToLower().Contains("vdo.ninja"))
                    return $"<a class='vid' href='{videoUrl}'><b>VDO.Ninja Video</b></a><br>";
            }
            return "";
        }

        private string GetSongTitleHtml(string serverAddressWithDash)
        {
            if (harvest.m_songTitleAtAddr.TryGetValue(serverAddressWithDash, out string title) &&
                MinutesSince2023AsInt() < harvest.m_timeToLive &&
                title.Length > 0)
            {
                Console.WriteLine("Song title found at that address:" + title);

                if (title.Length > 25)
                {
                    if (title.Contains(" by "))
                    {
                        title = title.Replace("  ", " ").Replace(" ", "&nbsp;").Replace("&nbsp;by&nbsp;", " by&nbsp;");
                    }
                    else if (title.Contains(" BY "))
                    {
                        title = title.Replace("  ", " ").Replace(" ", "&nbsp;").Replace("&nbsp;BY&nbsp;", " BY&nbsp;");
                    }
                }
                return $"<font size='-2'><i>{title}</i></font><br>";
            }
            return "";
        }

        private string GetLeaversHtml(ServersForMe s)
        {
            string leavers = "";
            foreach (var entry in m_connectionLatestSighting)
            {
                if (!entry.Key.Contains(s.serverIpAddress + ":" + s.serverPort))
                {
                    continue;
                }

                if (entry.Value.AddMinutes(4) > DateTime.Now && entry.Value.AddMinutes(1) < DateTime.Now)
                {
                    string guid = entry.Key.Substring(0, "f2c26681da4d0013563cfd8c0619cfc7".Length);
                    if (m_guidNamePairs.TryGetValue(guid, out string name) &&
                        name != "No Name" && name != "Ear" && !name.Contains("obby") &&
                        !leavers.Replace("&nbsp;", " ").Contains(name))
                    {
                        // see if this name is someone on this server now (changed instrument maybe)
                        bool fFound = false;
                        foreach (var user in s.whoObjectFromSourceData)
                        {
                            if (user.name == name ||
                               (user.name.Length > 3 && name.Length > 3 && user.name.ToLower().Substring(0, 3) == name.ToLower().Substring(0, 3)))
                            {
                                fFound = true;
                                break;
                            }
                        }
                        if (!fFound)
                        {
                            leavers += name.Replace(" ", "&nbsp;") + WholeMiddotString;
                        }
                    }
                }
            }

            if (leavers.Length > 0)
            {
                leavers = leavers.Substring(0, leavers.Length - WholeMiddotString.Length);
                return $"<center><font color='gray' size='-2'><i>{LocalizedText("Bye", "再見", "บ๊ายบาย", "Tschüss", "Ciao")} {leavers}</i></font></center>";
            }
            return "";
        }

        private string GetSoonHtml(string serverAddress)
        {
            if (m_predicted.ContainsKey(serverAddress))
            {
                string soonNames = "";
                foreach (var dude in m_predicted[serverAddress])
                {
                    if (m_guidNamePairs.ContainsKey(dude))
                    {
                        soonNames += m_guidNamePairs[dude] + " &#8226; ";
                    }
                    else
                    {
                        Console.WriteLine("GUID not mapped to a name. Only long runtimes see everyone.");
                        // ... (logic to get from censusgeo.csv) ...
                        foreach (var line in System.IO.File.ReadLines("data/censusgeo.csv"))
                        {
                            var fields = line.Split(',');
                            if (fields.Length >= 2 && fields[0] == dude)
                            {
                                soonNames += fields[1] + " &#8226; ";
                                break;
                            }
                        }
                    }
                }

                if (false && soonNames.Length > 0) // 'false' was in original
                {
                    soonNames = "<hr>Soon: " + soonNames.Substring(0, soonNames.Length - " &#8226; ".Length);
                    return soonNames;
                }
            }
            return "";
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
        public static Dictionary<string, HashSet<string>> m_userServerViewTracker = new Dictionary<string, HashSet<string>>();
        public static Dictionary<string, TimeSpan> m_userConnectDuration = new Dictionary<string, TimeSpan>();
        public static Dictionary<string, Dictionary<string, TimeSpan>> m_userConnectDurationPerServer = new Dictionary<string, Dictionary<string, TimeSpan>>();
        public static Dictionary<string, Dictionary<string, TimeSpan>> m_userConnectDurationPerUser = new Dictionary<string, Dictionary<string, TimeSpan>>();
        public static Dictionary<string, HashSet<string>> m_everywhereWeHaveMet = new Dictionary<string, HashSet<string>>();
        public static Dictionary<string, HashSet<string>> m_everywhereIveJoinedYou = new Dictionary<string, HashSet<string>>();
        public static Dictionary<string, DateTime> m_serverFirstSeen = new Dictionary<string, DateTime>();
        public static Dictionary<string, DateTime> m_connectionFirstSighting = new Dictionary<string, DateTime>(); // connection, first sighting
        public static Dictionary<string, DateTime> m_connectionLatestSighting = new Dictionary<string, DateTime>(); // connection, latest sighting



        public static string CanonicalTwoHashes(string hash1, string hash2)
        {
            if (0 < string.Compare(hash1, hash2))
                return hash2 + hash1;
            return hash1 + hash2;
        }


        //        public static Dictionary<string, DateTime> countryLastVisit = new Dictionary<string, DateTime>();

        private static SemaphoreSlim m_serializerMutex = new SemaphoreSlim(1, 1);

        //        static string m_ThreeLetterNationCode = "USA";
        static string m_TwoLetterNationCode = "US";

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
            // This is the new, non-blocking 'await'
            await m_serializerMutex.WaitAsync();
            try
            {
                string ret = "<table><tr><th>Server<th>Server Address</tr>\n";

                // Your original logic is unchanged.
                // Using .ToList() here is good as it copies the list
                // so you can release the lock quickly.
                foreach (var s in m_allMyServers.OrderBy(x => x.name).ToList())
                {
                    ret += "<tr><td>" + s.name + "<td>" +
                           s.serverIpAddress +
                           ":" +
                           s.serverPort +
                           "</tr>\n";
                }
                ret += "</table>\n";

                return ret;
            }
            finally
            {
                // This is the new, non-blocking 'Release'
                m_serializerMutex.Release();
            }
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

            ipString ??= "75.172.123.21";
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

public string RightNowForView { get; private set; }

        // 1. Converted from a 'string' property to an 'async Task<string>' method
        public async Task<string> GetIPDerivedHashAsync()
        {
            string ipAddress = GetClientIpAddress();
#if WINDOWS
        // Testing on windows doesn't give real data so I do this.
        ipAddress = "97.186.6.197";
#endif
            // 2. This is the fix: 'await' the async method
            var likelyGuidOfUser = await GuidFromIpAsync(ipAddress); // Might be null!

            if (null != likelyGuidOfUser)
            {
                Console.WriteLine(likelyGuidOfUser + " is the likely guid of " + ipAddress);
                if (m_guidNamePairs.ContainsKey(likelyGuidOfUser))
                    Console.WriteLine("  AKA: " + m_guidNamePairs[likelyGuidOfUser]);
            }

            if (null == likelyGuidOfUser)
                return "null;";
            else
                return "\"" + likelyGuidOfUser + "\";";
        }


        // MAKE SURE YOUR 'GuidFromIpAsync' METHOD IS ALSO IN THIS FILE:
        public async Task<string> GuidFromIpAsync(string ipAddress)
        {
            string ipv4Address = ipAddress.Replace("::ffff:", "");

            // 1. Check for a valid, non-expired cache entry (logic is unchanged).
            if (ipCacheTime.TryGetValue(ipv4Address, out var cacheTime) &&
                DateTime.UtcNow - cacheTime < cacheDuration &&
                ipToGuidCache.TryGetValue(ipv4Address, out var cachedGuid))
            {
                return cachedGuid;
            }

            string url = $"http://24.199.107.192:5000/lookup_guid/{ipv4Address}";

            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
                {
                    string guid = await httpClient.GetStringAsync(url, cts.Token);

                    if (!string.IsNullOrEmpty(guid))
                    {
                        ipToGuidCache[ipv4Address] = guid;
                        ipCacheTime[ipv4Address] = DateTime.UtcNow;
                    }
                    return guid;
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"An error occurred: GUID lookup timed out for {ipv4Address}.");
                return null;
            }
            catch (Exception e)
            {
                Console.WriteLine($"An error occurred: {e.Message}");
                return null;
            }
        }
    

        /*
        //[Route("{daysForward}")]
        @page "item"
        [HttpGet]
        public IActionResult Get(int daysForward)
        {
            var rng = new Random();
            return new JsonResult( new string (daysForward.ToString() + "noob")) ; //  "yeah";
                /* "new JsonResult(new WeatherForecast
            {
                Date = DateTime.Now.AddDays(daysForward),
                TemperatureC = rng.Next(-20, 55),
                Summary = Summaries[rng.Next(Summaries.Length)]
            });
                */


        public string PrettyTimeTilString(int iMins)
        {
            if (iMins < 10)
                return "<font size='-1'>(Soon)</font>";

            if (iMins < 60)
                return "<font size='-1'>(" + iMins.ToString() + "&nbsp;minutes)</font>";

            // divide by 60 and show one decimal place
            double dHours = iMins / 60.0;
            string sHours = dHours.ToString("0");
            double fractional = dHours - (int)(dHours);
            bool fPlus = (fractional > 0.1 ? true : false);
            return "<font size='-1'>(" + sHours + (fPlus ? "+" : "") + "&nbsp;hour" + (dHours > 2 ? "s)" : ")") + "</font>";
        }

        public string Unbreakable(string s)
        {
            for(int i=0; i<10; i++)
                s = s.Replace("  ", " ");
            return s.Replace(" ", "&nbsp;");
        }

        public static JArray recommended;
        public static int iMinuteOfSample = 0;


/*
        public string ComingUp
        {
            get
            {
                // if current minute isn't iMinuteOfSample, then re-sample
                if (MinutesSince2023AsInt() != iMinuteOfSample)
                {
                    iMinuteOfSample = MinutesSince2023AsInt();

                    string endpoint = "http://35.89.188.108/predicted.json";
                    using var client = new HttpClient();
                    System.Threading.Tasks.Task<string> task = client.GetStringAsync(endpoint);
                    task.Wait();
                    string s = task.Result;
                    //                    s = s.Substring(1);
                    //                    s = s.Substring(0, s.Length - 1);
                    recommended = JArray.Parse(s);
                }

if(null == recommended)
{
Console.WriteLine("Just failed to get prediction.") ;
return "";
}

                if(null != recommended)
                if (recommended.HasValues)
                {
                    string output = 
//                        "<h2>Soon</h2>" + 
                        "<table border='0'>";
                    //

                    for (int iEntry = 0; iEntry < recommended.Count; iEntry++)
                    {

                        int iMins = recommended[iEntry]["MinutesUntil"].ToObject<int>();

                        output += "<tr><td style='border-right: 1px solid gray; border-bottom: 1px solid gray'><p align='right'> ";

                        if (System.Web.HttpUtility.UrlDecode(recommended[iEntry]["ServerName"].ToString()).Length > 0)
                            output += "<u><b>" 
                                + Unbreakable(System.Web.HttpUtility.UrlDecode(recommended[iEntry]["ServerName"].ToString())) + "</u></b><br>";

                        output += PrettyTimeTilString(iMins);

                        output += "</p><td style='border-bottom: 1px solid gray'>";

                        int count = recommended[iEntry]["People"].Count();

                        for (int iPos = 0; iPos < count; iPos++)
                        {
                            var person = recommended[iEntry]["People"][iPos];
                            output += "<b>" + Unbreakable(System.Web.HttpUtility.UrlDecode(person["Name"].ToString())) + "</b>"
                                + (person["Instrument"].ToString().Length > 0 ? "(" + Unbreakable(person["Instrument"].ToString()) + ")" : "");
                            if (iPos < count - 1)
                                output += ", ";
                        }
                        output += "</tr>";
                    }
                    output += "</table><br>";
                    return output;
                }
                return "";
            }
            set { }
        }
        */
    }
}
