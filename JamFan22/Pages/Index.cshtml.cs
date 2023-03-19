//﻿#define WINDOWS

using IPGeolocation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.VisualBasic;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
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
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(ILogger<IndexModel> logger)
        {
            _logger = logger;
        }

        public void OnGet()
        {

        }


        public static Dictionary<string, string> JamulusListURLs = new Dictionary<string, string>()
        {
            /*
{"Any Genre 1", "https://jamulus.softins.co.uk/servers.php?central=anygenre1.jamulus.io:22124" }
,{"Any Genre 2", "https://jamulus.softins.co.uk/servers.php?central=anygenre2.jamulus.io:22224" }
,{"Any Genre 3", "https://jamulus.softins.co.uk/servers.php?central=anygenre3.jamulus.io:22624" }
,{"Genre Rock",  "https://jamulus.softins.co.uk/servers.php?central=rock.jamulus.io:22424" }
,{"Genre Jazz",  "https://jamulus.softins.co.uk/servers.php?central=jazz.jamulus.io:22324" }
,{"Genre Classical/Folk",  "https://jamulus.softins.co.uk/servers.php?central=classical.jamulus.io:22524" }
,{"Genre Choral/BBShop",  "https://jamulus.softins.co.uk/servers.php?central=choral.jamulus.io:22724" }
            */
{"Any Genre 1", "http://143.198.104.205/servers.php?central=anygenre1.jamulus.io:22124" }
,{"Any Genre 2", "http://143.198.104.205/servers.php?central=anygenre2.jamulus.io:22224" }
,{"Any Genre 3", "http://143.198.104.205/servers.php?central=anygenre3.jamulus.io:22624" }
,{"Genre Rock",  "http://143.198.104.205/servers.php?central=rock.jamulus.io:22424" }
,{"Genre Jazz",  "http://143.198.104.205/servers.php?central=jazz.jamulus.io:22324" }
,{"Genre Classical/Folk",  "http://143.198.104.205/servers.php?central=classical.jamulus.io:22524" }
,{"Genre Choral/BBShop",  "http://143.198.104.205/servers.php?central=choral.jamulus.io:22724" }

        };

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
        public static string GetHash(string name, string country, string instrument)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(name + country + instrument);
            var hashOfGuy = System.Security.Cryptography.MD5.HashData(bytes);
            //var h = System.Convert.ToBase64String(hashOfGuy);
            var h = ToHex(hashOfGuy, false);
            m_guidNamePairs[h] = System.Web.HttpUtility.HtmlEncode(name); // This is the name map for JammerMap
            return h;
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
            var serverListThen = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(was);
            var serverListNow = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(isnow);

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
        public static string NameFromHash(string hash)
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
                                return guy.name;
                        }
                    }
                }
            }
            return hash;
        }

        static string TIME_TOGETHER = "timeTogether.json";
        static string GUID_NAME_PAIRS = "guidNamePairs.json";
        public static Dictionary<string, TimeSpan> m_timeTogether = null;
        static int? m_lastSaveHourNumber = null;
        static HashSet<string> m_updatedPairs = new HashSet<string>(); // RAM memory of pairs I've saved
        static DateTime m_lastSift = DateTime.Now; // Time since boot or since last culling of unmentioned pairs
        protected static void ReportPairTogether(string us, TimeSpan durationBetweenSamples)
        {
            if (null == m_timeTogether)
            {
                m_timeTogether = new Dictionary<string, TimeSpan>();
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

                Console.WriteLine(m_timeTogether.Count + " pairs loaded.");
            }

            if (false == m_timeTogether.ContainsKey(us))
                m_timeTogether[us] = new TimeSpan();
            m_timeTogether[us] += durationBetweenSamples;
            m_updatedPairs.Add(us);

            // Note current hour on first pass.
            // Then note if hour has changed.
            if (null == m_lastSaveHourNumber)
                m_lastSaveHourNumber = DateTime.Now.Hour;
            else
            {
                if (m_lastSaveHourNumber != DateTime.Now.Hour)
                {
                    m_lastSaveHourNumber = DateTime.Now.Hour;

                    // If 30 days of uptime pass,
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

                    {
                        var sortedByLongest = m_timeTogether.OrderByDescending(x => x.Value).ToList();
                        string jsonString = JsonSerializer.Serialize(sortedByLongest);
                        Console.WriteLine(sortedByLongest.Count + " pairs saved.");
                        System.IO.File.WriteAllText(TIME_TOGETHER, jsonString);
                    }

                    {
                        // Each time we save the durations, we also save the guid-name pairs.
                        var sortedByAlpha = m_guidNamePairs.OrderBy(x => x.Value).ToList();
                        string jsonString = JsonSerializer.Serialize(sortedByAlpha);
                        System.IO.File.WriteAllText(GUID_NAME_PAIRS, jsonString);
                    }

                }
            }
        }

        static int m_secondsPause = 12; 
        public static void RefreshThreadTask()
        {
            JUST_TRY_AGAIN:
            while (true)
            {
                bool fMissingSamplePresent = false;
                
                var httpClientHandler = new HttpClientHandler();
                httpClientHandler.ServerCertificateCustomValidationCallback =
                    (message, cert, chain, ssl) =>
                    {
                        return true;
                    };

                using var client = new HttpClient(httpClientHandler);

                var serverStates = new Dictionary<string, Task<string>>();

                foreach (var key in JamulusListURLs.Keys)
                {
                    serverStates.Add(key, client.GetStringAsync(JamulusListURLs[key]));
                }

                DateTime query_started = DateTime.Now;
                foreach (var key in JamulusListURLs.Keys)
                {
                    var newReportedList = serverStates[key].Result; // only proceeds when data arrives

                    if (newReportedList != "CRC mismatch in received message")
                    {
                        m_serializerMutex.WaitOne(); // get the global mutex
                        try
                        {
                            if (LastReportedList.ContainsKey(key))
                            {
                                // Console.WriteLine(key);
                                DetectJoiners(LastReportedList[key], newReportedList);
                            }
                            LastReportedList[key] = newReportedList;
                        }
                        finally
                        {
                            m_serializerMutex.ReleaseMutex();
                        }
                    }
                    else
                    {
                        Console.WriteLine("CRC mismatch in received message");
                        Thread.Sleep(1000);
                        goto JUST_TRY_AGAIN;
                    }
                }

                Console.WriteLine("Refreshing all seven directories took " + (DateTime.Now - query_started).TotalMilliseconds + "ms");

                // get the mutex again
                m_serializerMutex.WaitOne(); // get the global mutex
                try
                {
                    TimeSpan durationBetweenSamples = new TimeSpan();

                    if (null != LastReportedListGatheredAt)
                        durationBetweenSamples = DateTime.Now.Subtract((DateTime)LastReportedListGatheredAt);

                    LastReportedListGatheredAt = DateTime.Now;

                    // I think I need to know what's broken.
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

                    // Each time we mine the list, we construct a hash for every active user
                    // and that hash is a key of a hash list that contains the ip:port servers
                    // where i've seen them.
                    foreach (var key in JamulusListURLs.Keys)
                    {
                        var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(LastReportedList[key]);
                        foreach (var server in serversOnList)
                        {
                            int people = 0;
                            if (server.clients != null)
                                people = server.clients.GetLength(0);
                            if (people < 1)
                                continue; // just fuckin don't care about 0 or even 1. MAYBE I DO WANNA NOTICE MY FRIEND ALL ALONE SOMEWHERE THO!!!!
                            foreach (var guy in server.clients)
                            {
                                string stringHashOfGuy = GetHash(guy.name, guy.country, guy.instrument);
                                /*
                                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(guy.name + guy.country + guy.instrument);
                                var hashOfGuy = System.Security.Cryptography.MD5.HashData(bytes);
                                string stringHashOfGuy = System.Convert.ToBase64String(hashOfGuy);
                                */
                                //////////////////////////////////////////////////////////////////////////
                                if (false == m_userServerViewTracker.ContainsKey(stringHashOfGuy))
                                    m_userServerViewTracker[stringHashOfGuy] = new HashSet<string>();
                                m_userServerViewTracker[stringHashOfGuy].Add(server.ip + ":" + server.port);
                                //////////////////////////////////////////////////////////////////////////
                                if (false == m_userConnectDuration.ContainsKey(stringHashOfGuy))
                                    m_userConnectDuration[stringHashOfGuy] = new TimeSpan();
                                m_userConnectDuration[stringHashOfGuy] =
                                    m_userConnectDuration[stringHashOfGuy].Add(durationBetweenSamples.Divide(server.clients.Count()));
                                //////////////////////////////////////////////////////////////////////////
                                {
                                    if (false == m_userConnectDurationPerServer.ContainsKey(stringHashOfGuy))
                                        m_userConnectDurationPerServer[stringHashOfGuy] = new Dictionary<string, TimeSpan>();
                                    var fullIP = server.ip + ":" + server.port;
                                    var theGuy = m_userConnectDurationPerServer[stringHashOfGuy];
                                    if (false == theGuy.ContainsKey(fullIP))
                                        theGuy.Add(fullIP, TimeSpan.Zero);
                                    theGuy[fullIP] = theGuy[fullIP].Add(durationBetweenSamples.Divide(server.clients.Count()));
                                }
                                ////////////////////////////////////////////////////////////////////////////
                                // The real scheme: for each guy, note the duration spend with EVERY OTHER GUY
                                {
                                    foreach (var otherguy in server.clients)
                                    {
                                        if (false == m_userConnectDurationPerUser.ContainsKey(stringHashOfGuy))
                                            m_userConnectDurationPerUser[stringHashOfGuy] = new Dictionary<string, TimeSpan>();
                                        if (otherguy == guy)
                                            continue; // just don't track me with me. Let the entry exist but fuck it.

                                        string stringHashOfOtherGuy = GetHash(otherguy.name, otherguy.country, otherguy.instrument);
                                        /*
                                        byte[] otherguybytes = System.Text.Encoding.UTF8.GetBytes(otherguy.name + otherguy.country + otherguy.instrument);
                                        var hashOfOtherGuy = System.Security.Cryptography.MD5.HashData(otherguybytes);
                                        string stringHashOfOtherGuy = System.Convert.ToBase64String(hashOfOtherGuy);
                                        */
                                        var theGuy = m_userConnectDurationPerUser[stringHashOfGuy];
                                        if (false == theGuy.ContainsKey(stringHashOfOtherGuy))
                                            theGuy.Add(stringHashOfOtherGuy, TimeSpan.Zero);
                                        theGuy[stringHashOfOtherGuy] = theGuy[stringHashOfOtherGuy].Add(durationBetweenSamples.Divide(server.clients.Count()));

                                        // ANOTHER SCHEME, WHERE key of canonical hashes contain all server:ports where we've met
                                        string us = CanonicalTwoHashes(stringHashOfGuy, stringHashOfOtherGuy);
                                        if (false == m_everywhereWeHaveMet.ContainsKey(us))
                                            m_everywhereWeHaveMet[us] = new HashSet<string>();
                                        m_everywhereWeHaveMet[us].Add(server.ip + ":" + server.port);
                                    }
                                }
                                ///////////////////////////////////////////////////////////////////////////////////////////////////////
                                /// Now use an interface that abstracts and ultimately replaces all of these other ad hoc dictionaries
                                {
                                    if (durationBetweenSamples.TotalSeconds > 0)
                                    {
                                        foreach (var otherguy in server.clients)
                                        {
                                            string stringHashOfOtherGuy = GetHash(otherguy.name, otherguy.country, otherguy.instrument);
                                            /*
                                            byte[] otherguybytes = System.Text.Encoding.UTF8.GetBytes(otherguy.name + otherguy.country + otherguy.instrument);
                                            var hashOfOtherGuy = System.Security.Cryptography.MD5.HashData(otherguybytes);
                                            string stringHashOfOtherGuy = System.Convert.ToBase64String(hashOfOtherGuy);
                                            */

                                            if (stringHashOfGuy != stringHashOfOtherGuy)
                                            {
                                                string us = CanonicalTwoHashes(stringHashOfGuy, stringHashOfOtherGuy);

                                                if (false == alreadyPushed.Contains(us))
                                                {
                                                    alreadyPushed.Add(us);
                                                    ReportPairTogether(us, durationBetweenSamples);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    } 

                    //            TopNoob();

                    // I'm gonna track server sightings in a separate loop.
                    foreach (var key in JamulusListURLs.Keys)
                    {
                        var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(LastReportedList[key]);
                        foreach (var server in serversOnList)
                        {
                            // I care when I FIRST saw this server.
                            if (false == m_serverFirstSeen.ContainsKey(server.ip + ":" + server.port))
                                m_serverFirstSeen.Add(server.ip + ":" + server.port, DateTime.Now);
                        }
                    }
                }
                finally { m_serializerMutex.ReleaseMutex(); }

                m_bUserWaiting = false; // I clear it to see if a user appears while I'm sleepin.
                int secs = m_secondsPause;
                if (fMissingSamplePresent)
                    secs /= 2; // if we're missing a sample, let's rush to the re-sample.
                if((secs < 8) || (secs > 12))
                        Console.WriteLine("Sleeping secs: " + secs);
                Thread.Sleep(secs * 1000);
                if (false == m_bUserWaiting)
                    m_secondsPause *= 2; // if we just slept, and nobody showed up, double our sleep
                else
                    m_secondsPause /= 2; // people want data. let's get some!

                if (m_secondsPause < 10)
                    m_secondsPause = 10;
                if (m_secondsPause > 60)
                    m_secondsPause = 60;
            }
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


        const string MYSTERY_STRING = "0db5e3c2eb494c8b825df53a1a63e80d";

        protected void SmartGeoLocate(string ip, ref double latitude, ref double longitude)
        {
            // for any IP address, use a cached object if it's not too old.
            if (m_ipAddrToLatLong.ContainsKey(ip))
            {
                var cached = m_ipAddrToLatLong[ip];
                //                if (cached.queriedThisDay + 1 < DateTime.Now.DayOfYear)
                {
                    latitude = double.Parse(cached.lat);
                    longitude = double.Parse(cached.lon);
//                    Console.WriteLine(ip + ": " + latitude + ", " + longitude);
                    return;
                }
            }

            // don't have cached data, or it's too old.
            // NOWEVER, THIS SHIT IF OFFLINE

            try
            {
                IPGeolocationAPI api = new IPGeolocationAPI(MYSTERY_STRING);
                GeolocationParams geoParams = new GeolocationParams();
                geoParams.SetIp(ip);
//                geoParams.SetIPAddress(ip); 
                geoParams.SetFields("geo,time_zone,currency");
                Geolocation geolocation = api.GetGeolocation(geoParams);
                latitude = Convert.ToDouble(geolocation.GetLatitude());
                longitude = Convert.ToDouble(geolocation.GetLongitude());
                m_ipAddrToLatLong[ip] = new LatLong(latitude.ToString(), longitude.ToString());
                Console.WriteLine("A client IP has been cached: " + ip + " " + geolocation.GetCity() + " " + latitude + " " + longitude);
            }
            catch (Exception e)
            {
                Console.WriteLine("Error getting geolocation for " + ip + ": " + e.Message);
                m_ipAddrToLatLong[ip] = new LatLong("0", "0");
            }
        }

        //        protected static Dictionary<string, LatLong> m_ipAddrToLatLong = new Dictionary<string, LatLong>();


        protected int DistanceFromClient(string lat, string lon)
        {
            var serverLatitude = float.Parse(lat);
            var serverLongitude = float.Parse(lon);

            string clientIP = HttpContext.Connection.RemoteIpAddress.ToString();
            if ((clientIP.Length < 5) || clientIP.Contains("127.0.0.1"))
            {
//                Console.WriteLine("initial ipaddr: " + clientIP);

                // ::1 appears in local debugging, but also possibly in reverse-proxy :o
                if (clientIP.Contains("127.0.0.1") || clientIP.Contains("::1"))
                {
                    var xff = (string) HttpContext.Request.HttpContext.Request.Headers["X-Forwarded-For"];
                    // xff from the proxy doesn't include the ::ffff: prefix, which I believe causes failures to match.
                    // so I re-add

                    if (null != xff)
                    {
                        if (false == xff.Contains("::ffff"))
                            xff = "::ffff:" + xff;

//                        Console.WriteLine("XFF was non-null, value: " + xff);
                        clientIP = xff;
                    }
                    else
                    {
//                        Console.WriteLine("XFF was null, clientIP hardcoded to 75.172.123.21, Monroe, Louisiana.");
                        clientIP = "75.172.123.21";
                    }
                }
            }
            // clientIP = "75.172.123.21"; // hardcode to make same code outcome as server THIS IS FOR LOCAL DEBUGGING ONLY. DON'T DEPLOY.

            double clientLatitude = 0.0;
            double clientLongitude = 0.0;
            SmartGeoLocate(clientIP, ref clientLatitude, ref clientLongitude);

            //            SmartGeoLocate(ipThem, ref serverLatitude, ref serverLongitude);

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
        public static bool CallOpenCageCached(string placeName, ref string lat, ref string lon)
        {
            if (m_openCageCache.ContainsKey(placeName))
            {
                if (m_openCageCache[placeName] == null)
                    return false;

                lat = m_openCageCache[placeName].lat;
                lon = m_openCageCache[placeName].lon;
                return true;
            }

            if(CallOpenCage(placeName, ref lat, ref lon))
            {
                m_openCageCache[placeName] = new LatLong(lat, lon);
                return true;
            }

            m_openCageCache[placeName] = null; // I couldn't map yuour name to a lat-long, but i store null so i don't keep asking.
            return false;
        }



        public static bool CallOpenCage(string placeName, ref string lat, ref string lon)
        {
            if (placeName.Length < 3)
                return false;
            if (placeName == "MOON")
                return false;
            string encodedplace = System.Web.HttpUtility.UrlEncode(placeName);
            string endpoint = string.Format("https://api.opencagedata.com/geocode/v1/json?q={0}&key=4fc3b2001d984815a8a691e37a28064c", encodedplace);
            using var client = new HttpClient();
            System.Threading.Tasks.Task<string> task = client.GetStringAsync(endpoint);
            task.Wait();
            string s = task.Result;
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
                    lat = (string)latLongJson["results"][0]["geometry"]["lat"];
                    lon = (string)latLongJson["results"][0]["geometry"]["lng"];
//                    m_PlaceNameToLatLong[placeName.ToUpper()] = new LatLong(lat, lon);
                    return true;
                }
            }

//            MaxMind.GeoIP2

            return false;
        }

        public void PlaceToLatLon(string serverPlace, string userPlace, string ipAddr, ref string lat, ref string lon)
        {
            ipAddr = ipAddr.Trim();
            serverPlace = serverPlace.Trim();
            userPlace = userPlace.Trim();

            System.Diagnostics.Debug.Assert(serverPlace.ToUpper() == serverPlace);
            System.Diagnostics.Debug.Assert(userPlace.ToUpper() == userPlace);


            var rng = new Random();
            if (0 == rng.Next(5000))
            {

                Console.WriteLine("Want to flush cached lat-longs, but only if things are not full-tilt.");

                if (m_secondsPause > 20) // This flush technique just sucks. Flushing isn't even that critical. Just do it daily.
                {
                    Console.WriteLine("Detected relative slowdown and flushed.");
                    m_PlaceNameToLatLong.Clear();
                    m_ipAddrToLatLong.Clear();
                }
            }

            if (m_PlaceNameToLatLong.ContainsKey(serverPlace))
            {
                lat = m_PlaceNameToLatLong[serverPlace].lat;
                lon = m_PlaceNameToLatLong[serverPlace].lon;
                return;
            }

            if (m_PlaceNameToLatLong.ContainsKey(userPlace))
            {
                lat = m_PlaceNameToLatLong[userPlace].lat;
                lon = m_PlaceNameToLatLong[userPlace].lon;
                return;
            }

            if (m_ipAddrToLatLong.ContainsKey(ipAddr))
            {
                lat = m_ipAddrToLatLong[ipAddr].lat;
                lon = m_ipAddrToLatLong[ipAddr].lon;
                return;
            }

            bool fServerLLSuccess = false;
            string serverLat = "";
            string serverLon = "";

            if (serverPlace.Length > 1)
                if (serverPlace != "yourCity")
                {
//                    if (serverPlace == "MALLORCA, UNITED STATES")
//                        serverPlace = "MALLORCA, SPAIN";

                    //         serverPlace = serverPlace.Replace(", UNITED STATES", "");
                    if (CallOpenCage(serverPlace, ref serverLat, ref serverLon))
                    {
                        Console.WriteLine("Used server location: " + serverPlace);
                        fServerLLSuccess = true;
                    }
                }

            // consider user location
            bool fUserLLSuccess = false;
            string userLat = "";
            string userLon = "";

            if (CallOpenCage(userPlace, ref userLat, ref userLon))
            {
                Console.WriteLine("Used user location: " + userPlace);
                fUserLLSuccess = true;
            }
            //Console.WriteLine("User location failed: " + userPlace);

            // consider server IP geolocation
            bool fServerIPLLSuccess = false;
            string serverIPLat = "";
            string serverIPLon = "";

            if (ipAddr.Length > 5)
            {
                IPGeolocationAPI api = new IPGeolocationAPI(MYSTERY_STRING);
                GeolocationParams geoParams = new GeolocationParams();
                //geoParams.SetIPAddress(ipAddr);
                geoParams.SetIp(ipAddr);
                geoParams.SetFields("geo,time_zone,currency");
                Geolocation geolocation = api.GetGeolocation(geoParams);
                Console.WriteLine(ipAddr + " " + geolocation.GetCity());
                serverIPLat = geolocation.GetLatitude();
                serverIPLon = geolocation.GetLongitude();
                fServerIPLLSuccess = true;
                m_ipAddrToLatLong[ipAddr] = new LatLong(serverIPLat, serverIPLon);
                Console.WriteLine("AN IP geo has been cached.");
            }
            else
                m_ipAddrToLatLong[ipAddr] = new LatLong("", ""); // so we stop asking?

            // if we have lat-lon based on server's IP,
            // and we have lat-lon based on user's UP,
            // and they are on same continent,
            // when we use user's IP.
            //
            // but if there's no user IP lat-lon
            // and tehre's no server SELF-DESCRIBED LOCATION lat-lon
            // then we use server's IP lat-lon
            //
            // if no serverIP lat-lon, we use server self-described lat-lon

            if (fServerIPLLSuccess)
            {
                if (fUserLLSuccess)
                {
                    // if these two latlongs are on the same continent
                    // then ignore the server's self-stated place.
                    char serverIPContinent = ContinentOfLatLong(serverIPLat, serverIPLon);
                    char userContinent = ContinentOfLatLong(userLat, userLon);
                    if (serverIPContinent == userContinent)
                    {
                        lat = userLat;
                        lon = userLon;
                        m_PlaceNameToLatLong[serverPlace.ToUpper()] = new LatLong(lat, lon);
                        return;
                    }
                }
                if (false == fServerLLSuccess)
                {
                    lat = serverIPLat;
                    lon = serverIPLon;
                    return;
                }
            }

            lat = serverLat;
            lon = serverLon;
            m_PlaceNameToLatLong[serverPlace.ToUpper()] = new LatLong(lat, lon);
        }

        // Here we note who s.who is, because we care how long a person has been on a server. Nothing more than that for now.
        protected void NotateWhoHere(string server, string who)
        {
            Debug.Assert(who.Length == "b707dc8fc6516826fbe9b4aa84d1553a".Length);
            //            Console.WriteLine(server, who);
            string hash = who + server ;

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
            string hash = who + server ;
            if (m_connectionFirstSighting.ContainsKey(hash))
            {
                TimeSpan ts = DateTime.Now.Subtract(m_connectionFirstSighting[hash]);
                return ts.TotalMinutes;
            }
            return -1; //
        }


        protected static string LocalizedText(string english, string chinese, string thai, string german)
        {
            switch (m_ThreeLetterNationCode)
            {
                case "CHN":
                    return chinese;
                case "TWN":
                    return chinese;
                case "HKG":
                    return chinese;
                case "THA":
                    return thai;
                //                case "BRA":
                //                    return english; // can't return portguese yet. don't know portguese.
                case "DEU":
                    return german; // first support DEU.
            }
            return english; //uber alles
        }
        protected string DurationHere(string server, string who)
        {
            Debug.Assert(who.Length == "b707dc8fc6516826fbe9b4aa84d1553a".Length);
            string hash = who + server ;
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
                    string phrase = LocalizedText("just&nbsp;arrived", "剛加入", "เพิ่งมา", "gerade angekommen");
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

            return smartCity;
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
                case "JAMSUCKER": return true;
                case "JAM FEED": return true;
                case "STUDIO BRIDGE": return true;
                case "CLICK": return true;
                case "LOBBY [0]": return true;
                case "REFERENCE": return true;
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
                    return false ; // not a noob.

            return true ;
        }

        static int minuteOfSample = -1 ;
        static string cachedResult = "";
        public string Noobs
        {
            get {
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





    public async Task<string> GetGutsRightNow()
        {
            m_allMyServers = new List<ServersForMe>();  // new list!

//            await MineLists();


            // Now for each last reported list, extract all the hmmm servers for now. all them servers by LIST, NAME, CITY, IP ADDRESS
            // cuz I wanna add a new var: Every distance to this client!
            // so eager, just get them distances!

            foreach (var key in LastReportedList.Keys)
            {
                var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(LastReportedList[key]);
                foreach (var server in serversOnList)
                {
                    if (server.name.ToLower().Contains("script"))
                        continue; // we don't let this happen! XSS attack
                    if (server.city.ToLower().Contains("script"))
                        continue; // we don't let this happen! XSS attack
                    if (server.name.ToLower().Contains("jxw"))
                        continue; // they wanna talk chinese, with no music
                    if (server.city.ToLower().Contains("peterborough"))
                        continue; // bye jimmy

                    int people = 0;
                    if (server.clients != null)
                        people = server.clients.GetLength(0);
                    if (people < 1)
                        continue; // just fuckin don't care about 0 or even 1. MAYBE I DO WANNA NOTICE MY FRIEND ALL ALONE SOMEWHERE THO!!!!
                    /// EMPTY SERVERS CAN KICK ROCKS
                    /// SERVERS WITH ONE PERSON MIGHT BE THE PERSON I'M SEARCHING FOR


                    List<string> userCountries = new List<string>();

                    string who = "";
                    foreach (var guy in server.clients)
                    {
                        if (guy.name.ToLower().Contains("script"))
                            continue; // no XSS please
                        // Here we note who s.who is, because we care how long a person has been on a server. Nothing more than that for now.
                        NotateWhoHere(server.ip + ":" + server.port, GetHash(guy.name, guy.country, guy.instrument)) ;

                        if (NukeThisUsername(guy.name, guy.instrument, server.name.ToUpper().Contains("CBVB")))
                            continue;

                        string slimmerInstrument = guy.instrument;
                        if (slimmerInstrument == "-")
                            slimmerInstrument = "";

                        if (slimmerInstrument.Length > 0) // if there's no length to instrument, don't add a space for it.
                            slimmerInstrument = " " + slimmerInstrument;

                        var nam = guy.name.Trim();
                        nam = nam.Replace("  ", " "); // don't want crazy space names
                        nam = nam.Replace("  ", " "); // don't want crazy space names
                        nam = nam.Replace("  ", " "); // don't want crazy space names

                        // Fuck these namelesses who have no proper instrument.
                        if (nam.Length == 0)
                        {
                            if (slimmerInstrument == "")
                                continue;
                            if (slimmerInstrument == " Streamer")
                                continue;
                        }

                        // If there are a gajillion in here, shrink the font
                        string font = "<font size='+0'>"; // might be bogus sizing
                        if (server.clients.GetLength(0) > 11)
                            font = "<font size='-1'>";

                        // this code is fishy but ok...
                        // if anyone in here has a name with more than 15 characters, shrink the font for everyone
                        foreach (var longguy in server.clients)
                            if (longguy.name.Length > 14)
                                if (slimmerInstrument.Length > 0) // stop tryin with No Name. let it be. || (longguy.name == "No Name"))
                                    font = "<font size='-1'>";

                        // Make a hash out of Name + Nation
                        string hash = guy.name + guy.country;

                        // give the musician a distinctive encoding class
                        string encodedHashOfGuy = GetHash(guy.name, guy.country, guy.instrument);
                        /*
                        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(guy.name + guy.country + guy.instrument);
                        var hashOfGuy = System.Security.Cryptography.MD5.HashData(bytes);
                        string encodedHashOfGuy = System.Convert.ToBase64String(hashOfGuy);
                        */

                        var newpart = "<span class=\"musician " +
                            server.ip + " "
                            + encodedHashOfGuy + "\"" +
                            "id =\"" + hash + "\"" +

                            " onmouseover=\"this.style.cursor='pointer'\" onmouseout=\"this.style.cursor='default'\" onclick=\"toggle('" + hash + "')\";>" +
                            font +
                            "<b>" + nam + "</b>" +
                            "<i>" + slimmerInstrument + "</i></font></span>\n";

                        if (server.clients.GetLength(0) < 17)
                            newpart += "<br>";
                        else
                            if (guy != server.clients[server.clients.GetLength(0) - 1])
                            newpart += " · ";


                        //                        if (newpart.Length > 67)
                        //                            newpart = newpart.Replace("size='0'", "size='-1'");

                        //                        newpart = newpart.Replace(" ", "&nbsp;"); // names and instruments have spaces too
                        who = who + newpart; // l+ ", ";

                        userCountries.Add(guy.country.ToUpper());
                    }
                    //  who = who.Substring(0, who.Length - 2); // chop that last comma!
                    string lat = "";
                    string lon = "";
                    string place = "";
                    string usersPlace = "Moon";
                    string serverCountry = "";

                    if (userCountries.Count > 0) // we snipped Streamers, sometimes leaving nobody.
                    {
                        if (server.city.Length > 1)
                            place = server.city;
                        if (server.country.Length > 1)
                        {
                            if (place.Length > 1)
                                place += ", ";
                            place += server.country;
                            serverCountry = server.country;
                        }

                        // usersCountry is the most common country reported by active users.
                        //var sorted = userCountries.GroupBy(v => v).OrderByDescending(g => g.Count());

                        var nameGroup = userCountries.GroupBy(x => x);
                        var maxCount = nameGroup.Max(g => g.Count());
                        var mostCommons = nameGroup.Where(x => x.Count() == maxCount).Select(x => x.Key).ToArray();
                        string usersCountry = mostCommons[0];


                        List<string> cities = new List<string>();
                        foreach (var guy in server.clients)
                        {
                            if (guy.country.ToUpper() == usersCountry)
                                if (guy.city.Length > 0)
                                    cities.Add(guy.city.ToUpper());
                        }

                        string usersCity = "";
                        if (cities.Count > 0)
                        {
                            var citiGroup = cities.GroupBy(x => x);
                            var maxCountr = citiGroup.Max(g => g.Count());
                            var mostCommonCity = citiGroup.Where(x => x.Count() == maxCountr).Select(x => x.Key).ToArray();
                            if (mostCommonCity.GetLength(0) > 0)
                                usersCity = mostCommonCity[0];
                        }

                        //string
                        usersPlace = usersCountry;
                        if (usersCity.Length > 1)
                            usersPlace = usersCity + ", " + usersCountry;
                        usersCountry = null;
                    }

                    // Ideally, if the users also reveal a city most common among that country, we should add it.

                    //                    IEnumerable<ServersForMe> sortedByDistanceAway = allMyServers.OrderBy(svr => svr.distanceAway);
                    ///  

                    if (place.Contains("208, "))
                        place = place.Replace("208, ", "");

                    PlaceToLatLon(place.ToUpper(), usersPlace.ToUpper(), server.ip, ref lat, ref lon);

                    //                    allMyServers.Add(new ServersForMe(key, server.ip, server.name, server.city, DistanceFromMe(server.ip), who, people));
                    int dist = 0;
                    char zone = ' ';
                    if (lat.Length > 1 || lon.Length > 1)
                    {
                        dist = DistanceFromClient(lat, lon);

//                        Console.Write(place.ToUpper() + " / " + usersPlace.ToUpper() + " / " + server.ip + " / " + lat + ", " + lon);

                        zone = ContinentOfLatLong(lat, lon);
                    }

if(dist < 250)
dist = 250;

                    // In ONE scenario, I cut this distance in half.
                    if (server.clients.Length == 1)
                    {
                        double boost = DurationHereInMins(server.ip + ":" + server.port,
                            GetHash(server.clients[0].name, server.clients[0].country, server.clients[0].instrument));

                        if (boost < 3.0)
                            boost = 3.0;
                        dist = (int) ((double)dist * (boost / 6)); // starts hella close, 
                    }
                    /*
                        if (DurationHereInMins(server.name, server.clients[0].name) < 4) // 3 mins means at least 1 refresh where it's featured
//                            if (DurationHereInMins(server.name, server.clients[0].name) > 1) // 3 mins means at least 1 refresh where it's featured
                            {
                                dist = dist / 2;
                            }
                            else
                            {
                                dist = dist * (int)(DurationHereInMins(server.name, server.clients[0].name));
                            }
                    */


                    m_allMyServers.Add(new ServersForMe(key, server.ip, server.port, server.name, server.city, serverCountry, dist, zone, who, server.clients, people, (int)server.maxclients));
                }
            }

            IEnumerable<ServersForMe> sortedByDistanceAway = m_allMyServers.OrderBy(svr => svr.distanceAway);

            //////////////////////////////////////////////////////////////
            //////////////////////////////////////////////////////////////
            // MY METHOD OF RELOCATING THE SERVER USER'S ON TO THE FRONT
            // CAUSES VERT SCROLL BAR TO GET MESSED UP.
            // BUT I CAN PREVENT THIS IF THE SERVER USER'S ON IS ALREADY FIRST POSITION.
            /*
            string ipaddr = HttpContext.Request.HttpContext.Connection.RemoteIpAddress.ToString();
            if (ipaddr.Contains("127.0.0.1") || ipaddr.Contains("::1"))
            {
                ipaddr = HttpContext.Request.HttpContext.Request.Headers["X-Forwarded-For"];
                if (null != ipaddr)
                    if (false == ipaddr.Contains("::ffff"))
                        ipaddr = "::ffff:" + ipaddr;
            }
            if (ipaddr != null)
            {
                if (m_ipToGuid.ContainsKey(ipaddr))
                {
                    string guidAssocWithIP = m_ipToGuid[ipaddr];
                    foreach (var svr in sortedByDistanceAway)
                    {
                        if (svr.who.Contains(guidAssocWithIP))
                        {
                            sortedByDistanceAway.Prepend(svr); // we see it twice this way?
                            break;
                        }
                    }
                }
            }
            */
            //////////////////////////////////////////////////////////////
            //////////////////////////////////////////////////////////////


            // caused a crash at zero active:
//            Console.WriteLine("First nearest server: " + sortedByDistanceAway.First().city + ", " + sortedByDistanceAway.First().country);

            string output = "";

            //            output += "<center><table class='table table-light table-hover table-striped'><tr><u><th>Genre<th>Name<th>City<th>Who</u></tr>";

            // First all with more than one musician:
            List<Client> myCopyOfWho = new List<Client>();
            foreach (var s in sortedByDistanceAway)
            {
                myCopyOfWho.Clear();
                // Copy to a list I can screw up:
                foreach (var cat in s.whoObjectFromSourceData)
                {
                    if (NukeThisUsername(cat.name, cat.instrument, s.name.ToLower().Contains("cbvb")))
                        continue;
                    myCopyOfWho.Add(cat);
                }
                var s_myUserCount = myCopyOfWho.Count;

                string blocks = "";
                try
                {
                    blocks = System.IO.File.ReadAllText("erased.txt");
                    // if there's a line, i match the first line
                    if (blocks.Length > 0)
                        if (s.name.ToLower().Contains(blocks.Trim().ToLower()))
                            continue;
                }
                catch (FileNotFoundException)
                {
                    Console.WriteLine("The load file was not found, so nothing to do.");
                }

                if (s_myUserCount > 1)
                {
//                  if (s.name == "JamPad") continue;

                    // once in a while, two people park on a single server. let's hide them after 6 hours.
                    bool fSuppress = true;
                    foreach (var user in myCopyOfWho)
                    {
                        if (DurationHereInMins(s.serverIpAddress + ":" + s.serverPort, GetHash(user.name, user.country, user.instrument)) < 8 * 60)
                        {
                            fSuppress = false;
                            break; // someone was here less than 8 hours.
                        }
                    }
                    if (fSuppress)
                        continue; // skip it!

                    // if everyone here got here less than 14 minutes ago, then this is just assembled
                    string newJamFlag = "";
                    foreach (var user in myCopyOfWho)
                    {
                        string translatedPhrase = LocalizedText("Just&nbsp;gathered.", "成員皆剛加入", "เพิ่งรวมตัว", "soeben angekommen.");
                        newJamFlag = "(" + ((s.usercount == s.maxusercount) ? LocalizedText("Full. ", "滿房。 ", "เต็ม ", "Volls. ") : "") + translatedPhrase + ")";
                        if (DurationHereInMins(s.serverIpAddress + ":" + s.serverPort, GetHash(user.name, user.country, user.instrument)) < 14)
                            continue;

                        // I guess Just Gatghered can only appear after the gathering period has elapsed. Maybe that's ok.
                        newJamFlag = "";
                        if (s.usercount == s.maxusercount)
                            newJamFlag = "<b>(" + LocalizedText("Full", "滿房", "เต็ม", "Voll") + ")</b>";
                        else
                        {
                            if (s.usercount + 1 == s.maxusercount)
                                newJamFlag = LocalizedText("(Almost full)", "(即將滿房)", "(เกือบเต็ม)", "(fast voll)");
                        }
                        break;
                    }

                    string smartcity = SmartCity(s.city, myCopyOfWho.ToArray());

                    var serverAddress = s.serverIpAddress + ":" + s.serverPort;

                    var newline = "<div id=\"" + serverAddress + "\"";

                    newline += BackgroundByZone(s.zone);

                    newline += "><center>";
                    //                        "<a class='link-unstyled' title='Copy server address to clipboard' href='javascript:copyToClipboard(&quot;" +
                    //                        serverAddress +
                    if (s.name.Length > 0)
                    {
                        string name = s.name;
                        if (name.Contains("CBVB"))
                            name += " (UNSTABLE)";
                        newline += System.Web.HttpUtility.HtmlEncode(name) + "<br>";
                    }

                    // smart nation returns nations that aren't (probably) obvious by server city.
                    string smartNations = SmartNations(myCopyOfWho.ToArray(), s.country);

                    if (smartcity.Length > 0)
                        newline += "<b>" + smartcity + "</b><br>";

                    // if we find this server address in the activity report, show its url
                    var activeJitsi = FindActiveJitsiOfJSvr(serverAddress);

                    string DIR = "";

#if WINDOWS
                    DIR = "C:\\Users\\User\\JamFan22\\JamFan22\\wwwroot\\mp3s\\";
#else
                    DIR = "/root/JamFan22/JamFan22/wwwroot/mp3s/";
#endif

                    string wildcard = serverAddress + "*";

                    var files = Directory.GetFiles(DIR, wildcard);
                    string myFile = null;
                    bool fSilent = false;
                    if (files.GetLength(0) > 0)
                    {
                        myFile = Path.GetFileName(files[0]);
                        if (myFile != null)
                            if (myFile.Contains("silent"))
                                fSilent = true;
                    }

                    string liveSnippet = "";
                    if (fSilent)
                    {
//                        liveSnippet = "(Silent?)";
                    }
                    else
                    { 
                        liveSnippet =
                            (myFile != null
                                ? "<audio class='playa' controls style='width: 150px;' src='mp3s/" + myFile + "' />"
                                : "");
                    }

                    /*
                    string listenNow = "";
                    if (s.name.Contains("MJTH Lobby"))
                    {
                        listenNow = "<a target='_blank' href='https://lobby.musicjammingth.net/'>Listen Live</a></br>";
                        // Just a hack
                        if (liveSnippet.Length > 0)
                            listenNow = "<br>" + listenNow;
                    }
                    */

                    // For every entry in https://lounge.jamulus.live/map.txt, add Listen link if ip:port matches.
                    string listenNow = "";

                    // Enumerate the text file at https://lounge.jamulus.live/map.txt
                    // and look for a match.

                    using (var httpClient = new HttpClient())
                    {
                        var contents = await httpClient.GetStringAsync("http://lounge.jamulus.live/map.txt");
                        using (var reader = new StringReader(contents))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                // Process each line of the file here
                                if (line.Contains(s.serverIpAddress + ":" + s.serverPort))
                                {
                                    // The URL of the lounge is the second token in the line.
                                    string[] tokens = line.Split(' ');

                                    string url = tokens[1];

                                    // Found a match. Add a link.
                                    listenNow = "<a target='_blank' href='" + url + "'>Listen</a></br>";
                                    // Just a hack
                                    //                            if (liveSnippet.Length > 0)
                                    //                                listenNow = "<br>" + listenNow;
                                }
                            }
                        }
                    }

                    // if listenNow wasn't assigned by the map, maybe assign it due to Free lounge and allowlist.
                    if (listenNow.Length == 0)
                    {
                        // read https://lounge.jamulus.live/free.txt, see if it's True
                        using (var httpClient = new HttpClient())
                        {
                            var contents = await httpClient.GetStringAsync("http://lounge.jamulus.live/free.txt");
                            if (contents.Contains("True"))
                            {
                                // ok, is this ipport on https://jamulus.live/can-dock.txt?
                                string ipport = s.serverIpAddress + ":" + s.serverPort;
                                using (var httpClient2 = new HttpClient())
                                {
                                    var contents2 = await httpClient2.GetStringAsync("http://lounge.jamulus.live/can-dock.txt");
                                    if (contents2.Contains(ipport))
                                    {
                                        // ok, it's free and can dock. Add a link.
                                        listenNow = "<a target='_blank' href='https://jamulus.live/dock/"
                                            + ipport
                                            + "'>Listen</a></br>";
                                    }
                                }
                            }
                        }
                    }

                    if (listenNow.Length > 0) // if there's a listen link
                        liveSnippet = "";     // then don't show an mp3 player

                    newline +=
                    "<font size='-1'>" +
                    s.category.Replace("Genre ", "").Replace(" ", "&nbsp;") + "</font><br>" +
                    newJamFlag +
                    ((newJamFlag.Length > 0) ? "<br>" : "") +
                    ((activeJitsi.Length > 0) ?
                        "<b><a target='_blank' href='" + activeJitsi + "'>Jitsi Video</a></b>" : "") +
                    (NoticeNewbs(s.serverIpAddress + ":" + s.serverPort) ? (LocalizedText("(New server.)", "(新伺服器)", "(เซิร์ฟเวอร์ใหม่)", "(New server.)") + "<br>") : "") +
                    liveSnippet +
                    listenNow +
                    "</center><hr>" +
                    s.who;

                    // show those who have left
                    // If my m_connectionLatestSighting is more than a minute, but less than 5 minutes, then I'm a leaver.
                    string leavers = "";
                    foreach (var entry in m_connectionLatestSighting)
                    {
                        if(entry.Key.Contains(s.serverIpAddress + ":" + s.serverPort))
                        {
                            // someone left htis server. between 1-4 minutes ago?
                            if(entry.Value.AddMinutes(4) > DateTime.Now)
                                if(entry.Value.AddMinutes(1) < DateTime.Now)
                                {
                                    // Get the name of this guid from our lookup (cuz they very well might not be online now)
                                    string guid = entry.Key.Substring(0, "f2c26681da4d0013563cfd8c0619cfc7".Length);
                                    string name = m_guidNamePairs[guid];
                                    if (name != "No Name")
                                    if (name != "Ear")
                                    if(false == leavers.Replace("&nbsp;", " ").Contains(name))
                                    {
                                            // see if this name is someone on this server now (changed instrument maybe)
                                        bool fFound = false;
                                        foreach (var user in  s.whoObjectFromSourceData)
                                        {
                                            if (user.name == name)
                                                fFound = true;

                                            if(user.name.Length > 3)
                                            if(name.Length > 3 )
                                            if (user.name.ToLower().Substring(0, 3) == name.ToLower().Substring(0,3))
                                            {
                                                fFound = true;
                                                break;
                                            }
                                        }
                                        if(false == fFound)
                                            leavers += name.Replace(" ", "&nbsp;") + WholeMiddotString;
                                    }
                                }
                        }
                    }
                    // LocalizedText("Just&nbsp;gathered.", "成員皆剛加入", "เพิ่งรวมตัว", "soeben angekommen.");
                    if (leavers.Length > 0) 
                        newline += "<center><font color='gray' size='-2'><i>" 
                            + LocalizedText("Bye", "再見", "บ๊ายบาย", "Bye") 
                            + " " 
                            + leavers.Substring(0, leavers.Length - WholeMiddotString.Length) + "</i></font></center>";
                    
                    if (smartcity != smartNations) // it happens
                    {
                        newline +=
                            "<center><font size='-2'>" + smartNations.Trim() + "</font></center>";
                    }

                    newline += "</div>";
                    output += newline;
                }
                else
                {
                    myCopyOfWho.Clear();
                    // Copy to a list I can screw up:
                    foreach (var cat in s.whoObjectFromSourceData)
                    {
                        if (NukeThisUsername(cat.name, cat.instrument, s.name.ToLower().Contains("cbvb")))
                            continue;
                        myCopyOfWho.Add(cat);
                    }

                    if (myCopyOfWho.Count > 0)
                    {
                        if (DurationHereInMins(s.serverIpAddress + ":" + s.serverPort, GetHash(myCopyOfWho[0].name, myCopyOfWho[0].country, myCopyOfWho[0].instrument)) > 6 * 60)
                            continue; // if they have sat there for 6 hours, don't show them.
                                      //                    string smartcityforone = SmartCity(s.city, s.whoObjectFromSourceData);

                        if (s.name == "JamPad")
                            continue;
                        if (s.name == "portable")
                            continue;

                        string smartcity = SmartCity(s.city, myCopyOfWho.ToArray());

                        string noBRName = s.who;
                        noBRName = noBRName.Replace("<br/>", " ");

                        // var newline = "<div><center>";


                        var newline = "<div ";
                        newline += BackgroundByZone(s.zone);
                        newline += "><center>";


                        if (s.name.Length > 0)
                        {
                            string name = s.name;
                            if (name.Contains("CBVB"))
                                name += " (UNSTABLE)";

                            newline += System.Web.HttpUtility.HtmlEncode(name) + "<br>";
                        }

                        if (smartcity.Length > 0)
                            newline += "<b>" + smartcity + "</b><br>";

                        newline +=
                            "<font size='-1'>" +
                            s.category.Replace("Genre ", "").Replace(" ", "&nbsp;") + "</font><br>" +
                            (NoticeNewbs(s.serverIpAddress + ":" + s.serverPort) ? "(New server.)<br>" : "") +
                            "</center><hr>" +
                            noBRName +
                            DurationHere(s.serverIpAddress + ":" + s.serverPort, GetHash(myCopyOfWho[0].name, myCopyOfWho[0].country, myCopyOfWho[0].instrument)) + "</div>";  // we know there's just one! i hope!

                        output += newline;
                    }
                }
            }

            /*
            int iActiveJamFans = 0;
            foreach (var timeski in clientIPsDeemedLegit.Values)
            {
                if (DateTime.Now < timeski.AddMinutes(30))
                    iActiveJamFans++;
            }
            if (iActiveJamFans > 1)
                output += "<br><center>" + iActiveJamFans.ToString() + " JamFans</center><br>";
            */


            //            output += "</table></center>";
            return output;
        }

        public static Dictionary<string, DateTime> m_clientIPLastVisit = new Dictionary<string, DateTime>();
        public static Dictionary<string, DateTime> m_clientIPsDeemedLegit = new Dictionary<string, DateTime>();
        public static Dictionary<string, DateTime> m_countriesDeemedLegit = new Dictionary<string, DateTime>();
        public static Dictionary<DateTime, int> m_usersCounted = new Dictionary<DateTime, int>();
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

        public static System.Threading.Mutex m_serializerMutex = new System.Threading.Mutex(false, "MASTER_MUTEX");

        static string m_ThreeLetterNationCode = "USA";

        class MyUserGeoCandy
        {
            public string city;
            public string countryCode3;
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

                    /*
                    if (m_serializerMutex.WaitOne(0)) // returns immediately
                    {
                        Console.WriteLine("YES, I got the Mutex.");
                        m_serializerMutex.ReleaseMutex();
                    }
                    else
                    {
                        Console.WriteLine("NO, I didn't get the mutex.");
                    }
                    */
                }
            }
        }

        static bool InMapFile(string fullAddress)
        {
            var httpClient = new HttpClient();
            var response = httpClient.GetStringAsync("http://lounge.jamulus.live/map.txt").Result;
            return response.Contains(fullAddress);
        }




        public string HowManyUsers
        {
            get
            {
                m_serializerMutex.WaitOne();
                try
                {
                    int iActiveJamFans = 0;
                    foreach (var timeski in m_clientIPsDeemedLegit.Values)
                    {
                        if (DateTime.Now < timeski.AddMinutes(60))
                            iActiveJamFans++;
                    }
                    var iNations = 0;
                    foreach (var timeski in m_countriesDeemedLegit.Values)
                    {
                        if (DateTime.Now < timeski.AddMinutes(60))
                            iNations++;
                    }

                    m_usersCounted.Add(DateTime.Now, iActiveJamFans);


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
                                if (peepCount < 2)
                                    continue; // just fuckin don't care about 0 or even 1
                                if (server.name.ToLower().Contains("oscv")) // never sample "OSCvev"
                                    continue;
                                if (server.name.ToLower().Contains("priv")) // don't sample self-described private areas
                                    continue;

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
                                    // if (false == System.IO.File.Exists("/root/JamFan22/JamFan22/wwwroot/mp3s/" + fullAddress + ".mp3"))  // xxx
                                    
                                    // If this fullAddress is in map.txt, don't consider it for sampling.
                                    if(false == InMapFile(fullAddress))
                                        svrActivesIpPort.Add(fullAddress);
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
                    var rng = new Random();
                    if (svrActivesIpPort.Count > 1) // Do I see more than one unsampled active (2+) server?
                    {
                        // Do I feel lucky about creating a sample?
                        if (0 != rng.Next(svrActivesIpPort.Count))
                        {
                            string chosen = svrActivesIpPort[rng.Next(svrActivesIpPort.Count)];
                            System.IO.File.WriteAllLines("serversToSample.txt", new string[] { chosen });
//                            Console.WriteLine("I chose: " + chosen);
                        }
                        else
                        {
                            // fewer targets means more empty guesses
                            System.IO.File.WriteAllLines("serversToSample.txt", new string[] { "" }); 
                        }    
                            
                    }
                    else
                    {
                        System.IO.File.WriteAllLines("serversToSample.txt", new string[] { "" });
                    }

                    int iTopCount = 0;
                    DateTime timeOfTopCount = DateTime.Now;

                    foreach (var timeski in m_usersCounted.Keys)
                    {
                        if (DateTime.Now < timeski.AddHours(24))
                            if (m_usersCounted[timeski] > iTopCount)
                            {
                                iTopCount = m_usersCounted[timeski];
                                timeOfTopCount = timeski;
                            }
                    }

                    string ret = "";
                    /*
                    iActiveJamFans.ToString() + " musicians " + 
//                        "from " + iNations.ToString() + " countries " +
                        "have viewed this page in the last hour";
                    */


                    /*
                    if (iTopCount > iActiveJamFans)
                    {
                        if (iTopCount == iActiveJamFans + 1)
                            ret += ", which is near today's high";
                        else
                    */


                    {
                        ret += "" +
                        //                                ". " +
                        SinceNowInText(timeOfTopCount) +
                        " there were " + iTopCount.ToString() + " musicians watching this page";
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


        public string ShowServerByIPPort
        {
            get
            {
                {
                    m_serializerMutex.WaitOne();
                    try
                    {

                        string ret = "<table><tr><th>Server<th>Server Address</tr>\n";

                        foreach (var s in m_allMyServers.OrderBy(x => x.name).ToList())
                        {
                            ret += "<tr><td>" + s.name + "<td>" +
                                //                        "<a class='link-unstyled' title='Copy server address to clipboard' href='javascript:copyToClipboard(&quot;" +
                                //                        s.serverIpAddress + ":" + s.serverPort + "&quot;)'>" +
                                s.serverIpAddress +
                                ":" +
                                s.serverPort +
                                //                        "</a>" + 
                                "</tr>\n";
                        }
                        ret += "</table>\n"; // <hr>";

                        return ret;
                    }
                    finally { m_serializerMutex.ReleaseMutex(); }
                }
            }
        }


        static int m_conditionsDelta = 0;

        public string RefreshDuration
        {
            get
            {
                // never refresh more frequently than 90 seconds
                if (m_conditionsDelta < -30)
                    m_conditionsDelta = -30;
                int iRefreshDelay = 120 + m_conditionsDelta ;
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
                return ret.Substring(0, ret.Length - 2) ; // chop comma
            }
            set { }
        }

        static bool m_bUserWaiting = false;
        public string RightNow
        {
            get
            {
                DateTime started = DateTime.Now;
                m_bUserWaiting = true; 
                m_serializerMutex.WaitOne();
                try
                {
                    string ipaddr = HttpContext.Connection.RemoteIpAddress.ToString();

                    if (ipaddr.Contains("127.0.0.1") || ipaddr.Contains("::1"))
                        ipaddr = HttpContext.Request.Headers["X-Forwarded-For"];

                    if (null == ipaddr)
                    {
//                        Console.WriteLine("A null IP address replaced by Microsoft's IP.");
                        ipaddr = "75.172.123.21"; // me now
                    }

                    if (false == ipaddr.Contains("::ffff"))
                        ipaddr = "::ffff:" + ipaddr;

                    Console.WriteLine("Client IP: " + ipaddr);

                    if (ipaddr.Length > 5)
                    {
                        if (false == userIpCachedItems.ContainsKey(ipaddr))
                        {
                            IPGeolocationAPI api = new IPGeolocationAPI(MYSTERY_STRING);
                            try
                            {
                                GeolocationParams geoParams = new GeolocationParams();
                                //geoParams.SetIPAddress(ipaddr);
                                geoParams.SetIp(ipaddr);
                                geoParams.SetFields("geo,time_zone,currency");
                                Geolocation geolocation = api.GetGeolocation(geoParams);
                                // Console.WriteLine("Tick, another IP geolocation query: " + ipaddr);
                                var candy = new MyUserGeoCandy();
                                candy.city = geolocation.GetCity();
                                candy.countryCode3 = geolocation.GetCountryCode3();
                                userIpCachedItems[ipaddr] = candy;
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine("Error in geolocation: " + e.Message);
                            }
                        }
//                        Console.WriteLine(userIpCachedItems[ipaddr].city + ", " + userIpCachedItems[ipaddr].countryCode3);

                        m_ThreeLetterNationCode = userIpCachedItems[ipaddr].countryCode3; // global for this call (cuz of the mutex)

                        /*
                        if (userIpCachedItems.ContainsKey(ipaddr)) // maybe it failed!
                            if (userIpCachedItems[ipaddr].city == "Princeton")
                                m_ThreeLetterNationCode = "THA";
                        */

                        if (userIpCachedItems.ContainsKey(ipaddr)) // maybe it failed!
                            Console.Write(userIpCachedItems[ipaddr].city +
                                            ", " +
                                            userIpCachedItems[ipaddr].countryCode3);

                        // Visually indicate if we last heard from this ipaddr
                        // after about 125 seconds has elapsed
                        if (m_clientIPLastVisit.ContainsKey(ipaddr))
                        {
                            var lastRefresh = m_clientIPLastVisit[ipaddr];
                            //  Console.Write((DateTime.Now - lastRefresh).ToString());
                            if (DateTime.Now < lastRefresh.AddSeconds(120 + m_conditionsDelta + 30))
                                if (DateTime.Now > lastRefresh.AddSeconds(120 + m_conditionsDelta - 30))
                                {
                                    Console.Write(" :)");

                                    // this IP is the key, and the time is the value?
                                    // yeah, cuz each IP counts once.
                                    m_clientIPsDeemedLegit[ipaddr] = DateTime.Now;

                                    m_countriesDeemedLegit[userIpCachedItems[ipaddr].countryCode3]
                                        = DateTime.Now;

                                    // Finally, count each confirmed refresh toward a running tally for each nation
                                    if (false == m_countryRefreshCounts.ContainsKey(m_ThreeLetterNationCode))
                                        m_countryRefreshCounts[m_ThreeLetterNationCode] = 1;
                                    else
                                        m_countryRefreshCounts[m_ThreeLetterNationCode] += 1;

                                    // And count UNIQUE IPs
                                    //HashSet allows only the unique values to the list

                                    if (false == m_bucketUniqueIPsByCountry.ContainsKey(m_ThreeLetterNationCode))
                                        m_bucketUniqueIPsByCountry[m_ThreeLetterNationCode] = new HashSet<string>();
                                    m_bucketUniqueIPsByCountry[m_ThreeLetterNationCode].Add(ipaddr); // has means uniques only
                                }
                        }
                        m_clientIPLastVisit[ipaddr] = DateTime.Now;

                        Console.WriteLine();
                    }

                    var v = GetGutsRightNow();
                    v.Wait();
                    return v.Result;
                }
                finally 
                {
                    m_serializerMutex.ReleaseMutex();
                    TimeSpan duration = DateTime.Now - started;
                    if (duration.TotalSeconds > 6) // double-slowdown for really unacceptable perf
                    {
                        Console.WriteLine("Browser waited " + duration.TotalSeconds + " seconds.");
                        m_conditionsDelta++;
                    }
                    if (duration.TotalSeconds > 3)
                    {
                        Console.WriteLine("Browser waited " + duration.TotalSeconds + " seconds.");
                        m_conditionsDelta++;
                    }
                    if (duration.TotalSeconds < 1)
                        m_conditionsDelta--;
//                    Console.WriteLine("Adding this to everyone's 120-second auto-refresh: " + m_conditionsDelta.ToString());
                }
            }
            set
            {
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
    }
}
