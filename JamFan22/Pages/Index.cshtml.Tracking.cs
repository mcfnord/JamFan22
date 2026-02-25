using JamFan22.Models;
using JamFan22.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace JamFan22.Pages
{
    public partial class IndexModel : PageModel
    {
        public static Dictionary<string, string> m_guidNamePairs = new Dictionary<string, string>();

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

        public static Dictionary<string, HashSet<string>> m_everywhereIveJoinedYou = new Dictionary<string, HashSet<string>>();

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
        }

        public static void DetectJoiners(string was, string isnow)
        {
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
                throw e;
            }

            foreach (var server in serverListNow)
            {
                var serverWas = GetServer(serverListThen, server.ip, server.port);
                if (serverWas != null)
                {
                    if (serverWas.clients != null)
                        if (server.clients != null)
                        {
                            var joiners = Joined(serverWas.clients, server.clients);
                            foreach (var actor in joiners)
                            {
                                foreach (var guyHere in server.clients)
                                {
                                    NoteJoinerTargetServer(actor, guyHere, server.ip, server.port);
                                }
                            }
                        }
                }
            }
        }

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
        static int m_lastDayNotched = 0; 

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

                    foreach (var item in m_timeTogether)
                    {
                        if (false == m_timeTogetherUpdated.ContainsKey(item.Key))
                        {
                            m_timeTogetherUpdated[item.Key] = DateTime.Now;
                        }
                    }
                }

                Console.WriteLine(m_timeTogether.Count + " pairs loaded.");
            }

            if (false == m_timeTogether.ContainsKey(us))
                m_timeTogether[us] = new TimeSpan();
            m_timeTogether[us] += durationBetweenSamples;
            m_timeTogetherUpdated[us] = DateTime.Now;

            if (null == m_lastSaveMinuteNumber)
                m_lastSaveMinuteNumber = DateTime.Now.Minute;
            else
            {
                if (m_lastSaveMinuteNumber != DateTime.Now.Minute)
                {
                    m_lastSaveMinuteNumber = DateTime.Now.Minute;
                    {
                        var newTimeTogether = new Dictionary<string, TimeSpan>();
                        var newTimeTogetherUpdated = new Dictionary<string, DateTime>();

                        try
                        {
                            foreach (var item in m_timeTogetherUpdated)
                            {
                                if (item.Value.AddDays(21) > DateTime.Now)
                                {
                                    newTimeTogether[item.Key] = m_timeTogether[item.Key]; 
                                    newTimeTogetherUpdated[item.Key] = item.Value;
                                }
                            }
                            m_timeTogether = newTimeTogether;
                            m_timeTogetherUpdated = newTimeTogetherUpdated;
                        }
                        catch (KeyNotFoundException)
                        {
                            throw; 
                        }
                    }
                    {
                        var sortedByLongest = m_timeTogether.OrderByDescending(x => x.Value).ToList();
                        string jsonString = JsonSerializer.Serialize(sortedByLongest);
                        System.IO.File.WriteAllText(TIME_TOGETHER, jsonString);

                        if (DateTime.Now.DayOfYear != m_lastDayNotched)
                        {
                            m_lastDayNotched = DateTime.Now.DayOfYear;

                            System.IO.File.AppendAllText("wwwroot/paircount.csv",
                                MinutesSince2023() + ","
                                    + sortedByLongest.Count
                                    + Environment.NewLine);
                        }
                    }

                    try
                    {
                        string jsonString = JsonSerializer.Serialize(m_timeTogetherUpdated.ToList());
                        System.IO.File.WriteAllText(TIME_TOGETHER_UPDATED_AT, jsonString);
                    }
                    catch (System.IO.IOException)
                    {
                        System.Threading.Thread.Sleep(1000);
                        string jsonString = JsonSerializer.Serialize(m_timeTogetherUpdated.ToList());
                        System.IO.File.WriteAllText(TIME_TOGETHER_UPDATED_AT, jsonString);
                    }

                    try
                    {
                        var sortedByAlpha = m_guidNamePairs.OrderBy(x => x.Value).ToList();
                        string jsonString = JsonSerializer.Serialize(sortedByAlpha);
                        System.IO.File.WriteAllText(GUID_NAME_PAIRS, jsonString);
                    }
                    catch (System.IO.IOException)
                    {
                        System.Threading.Thread.Sleep(1000);
                        var sortedByAlpha = m_guidNamePairs.OrderBy(x => x.Value).ToList();
                        string jsonString = JsonSerializer.Serialize(sortedByAlpha);
                        System.IO.File.WriteAllText(GUID_NAME_PAIRS, jsonString);
                    }
                }
            }
        }

        public static Dictionary<string, DateTime> m_connectionFirstSighting = new Dictionary<string, DateTime>();
        public static Dictionary<string, DateTime> m_connectionLatestSighting = new Dictionary<string, DateTime>();

        protected void NotateWhoHere(string server, string who)
        {
            Debug.Assert(who.Length == "b707dc8fc6516826fbe9b4aa84d1553a".Length);
            string hash = who + server;

            try
            {
                if (false == m_connectionFirstSighting.ContainsKey(hash))
                {
                    m_connectionFirstSighting[hash] = DateTime.Now;
                    return; 
                }

                if (DateTime.Now > m_connectionLatestSighting[hash].AddMinutes(10))
                {
                    m_connectionFirstSighting[hash] = DateTime.Now;
                }
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
            return -1; 
        }

        public static Dictionary<string, HashSet<string>> m_userServerViewTracker = new Dictionary<string, HashSet<string>>();
        public static Dictionary<string, TimeSpan> m_userConnectDuration = new Dictionary<string, TimeSpan>();
        public static Dictionary<string, Dictionary<string, TimeSpan>> m_userConnectDurationPerServer = new Dictionary<string, Dictionary<string, TimeSpan>>();
        public static Dictionary<string, Dictionary<string, TimeSpan>> m_userConnectDurationPerUser = new Dictionary<string, Dictionary<string, TimeSpan>>();
        public static Dictionary<string, HashSet<string>> m_everywhereWeHaveMet = new Dictionary<string, HashSet<string>>();

        public static string CanonicalTwoHashes(string hash1, string hash2)
        {
            if (hash1.CompareTo(hash2) < 0)
                return hash1 + hash2;
            return hash2 + hash1;
        }
    }
}
