using JamFan22.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace JamFan22.Services
{
    public class EncounterTracker
    {
        // ── Utility helpers (moved from Index.cshtml.cs) ──────────────────────

        public static JamulusServers? GetServer(List<JamulusServers> serverList, string ip, long port)
        {
            foreach (var server in serverList)
            {
                if (server.ip == ip && server.port == port)
                    return server;
            }
            return null;
        }

        public static bool SameGuy(Client guyBefore, Client guyAfter)
        {
            return guyBefore.name == guyAfter.name
                && guyBefore.instrument == guyAfter.instrument
                && guyBefore.country == guyAfter.country;
        }

        public static Client[] Joined(Client[] before, Client[] after)
        {
            var joiners = new List<Client>();
            foreach (var guyAfter in after)
            {
                bool fSeenBefore = false;
                foreach (var guyBefore in before)
                {
                    if (SameGuy(guyBefore, guyAfter)) { fSeenBefore = true; break; }
                }
                if (!fSeenBefore) joiners.Add(guyAfter);
            }
            return joiners.ToArray();
        }

        public static string ToHex(byte[] bytes, bool upperCase)
        {
            var result = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
                result.Append(bytes[i].ToString(upperCase ? "X2" : "x2"));
            return result.ToString();
        }

        // ── Hashing ───────────────────────────────────────────────────────────

        public static Dictionary<string, string> m_guidNamePairs = new Dictionary<string, string>();

        public static string GetHash(string name, string country, string instrument)
        {
            int totalLen = name.Length + country.Length + instrument.Length;
            Span<char> inputChars = totalLen < 1024 ? stackalloc char[totalLen] : new char[totalLen];

            var pos = inputChars;
            name.AsSpan().CopyTo(pos); pos = pos.Slice(name.Length);
            country.AsSpan().CopyTo(pos); pos = pos.Slice(country.Length);
            instrument.AsSpan().CopyTo(pos);

            int byteCount = System.Text.Encoding.UTF8.GetByteCount(inputChars);
            Span<byte> inputBytes = byteCount < 1024 ? stackalloc byte[byteCount] : new byte[byteCount];
            System.Text.Encoding.UTF8.GetBytes(inputChars, inputBytes);

            Span<byte> hashBytes = stackalloc byte[System.Security.Cryptography.MD5.HashSizeInBytes];
            System.Security.Cryptography.MD5.HashData(inputBytes, hashBytes);

            string hashString = Convert.ToHexString(hashBytes).ToLowerInvariant();

            if (!m_guidNamePairs.ContainsKey(hashString))
                m_guidNamePairs[hashString] = System.Web.HttpUtility.HtmlEncode(name);

            return hashString;
        }

        // ── Joiner tracking ───────────────────────────────────────────────────

        public static Dictionary<string, HashSet<string>> m_everywhereIveJoinedYou = new Dictionary<string, HashSet<string>>();

        public static void NoteJoinerTargetServer(Client actor, Client target, string server, long port)
        {
            string hashOfActor = GetHash(actor.name, actor.country, actor.instrument);
            string hashOfTarget = GetHash(target.name, target.country, target.instrument);
            if (hashOfActor == hashOfTarget) return;

            string key = hashOfActor + hashOfTarget;
            if (!m_everywhereIveJoinedYou.ContainsKey(key))
                m_everywhereIveJoinedYou[key] = new HashSet<string>();
            m_everywhereIveJoinedYou[key].Add(server + ":" + port);
        }

        public static void DetectJoiners(string was, string isnow)
        {
            List<JamulusServers> serverListThen;
            List<JamulusServers> serverListNow;
            try
            {
                serverListThen = JsonSerializer.Deserialize<List<JamulusServers>>(was);
                serverListNow  = JsonSerializer.Deserialize<List<JamulusServers>>(isnow);
            }
            catch (JsonException e)
            {
                Console.WriteLine("A fatal data ingestion error has occured.");
                throw e;
            }

            foreach (var server in serverListNow)
            {
                var serverWas = GetServer(serverListThen, server.ip, server.port);
                if (serverWas?.clients != null && server.clients != null)
                {
                    var joiners = Joined(serverWas.clients, server.clients);
                    foreach (var actor in joiners)
                        foreach (var guyHere in server.clients)
                            NoteJoinerTargetServer(actor, guyHere, server.ip, server.port);
                }
            }
        }

        /// <summary>
        /// Look up a hash in the live server lists. Callers supply the list data to avoid
        /// a circular dependency with JamulusCacheManager.
        /// </summary>
        public static bool DetailsFromHash(string hash, ref string theirName, ref string theirInstrument,
            Dictionary<string, string> jamulusListURLs, Dictionary<string, string> lastReportedList)
        {
            foreach (var key in jamulusListURLs.Keys)
            {
                if (!lastReportedList.TryGetValue(key, out string json)) continue;
                var serversOnList = JsonSerializer.Deserialize<List<JamulusServers>>(json);
                if (serversOnList == null) continue;
                foreach (var server in serversOnList)
                {
                    if (server.clients == null) continue;
                    foreach (var guy in server.clients)
                    {
                        if (hash == GetHash(guy.name, guy.country, guy.instrument))
                        {
                            theirName = guy.name;
                            theirInstrument = guy.instrument;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        // ── Time-together persistence ─────────────────────────────────────────

        static readonly string TIME_TOGETHER = "timeTogether.json";
        static readonly string TIME_TOGETHER_UPDATED_AT = "timeTogetherLastUpdates.json";
        static readonly string GUID_NAME_PAIRS = "guidNamePairs.json";

        public static Dictionary<string, TimeSpan> m_timeTogether = null;
        public static Dictionary<string, DateTime> m_timeTogetherUpdated = null;
        static int? m_lastSaveMinuteNumber = null;
        static int m_lastDayNotched = 0;

        public static void ReportPairTogether(string us, TimeSpan durationBetweenSamples)
        {
            if (m_timeTogether == null)
            {
                m_timeTogether = new Dictionary<string, TimeSpan>();
                m_timeTogetherUpdated = new Dictionary<string, DateTime>();

                {
                    string s = "[]";
                    try { s = File.ReadAllText(TIME_TOGETHER); }
                    catch (FileNotFoundException) { Console.WriteLine("timeTogether.json not found, starting fresh."); }
                    var a = JsonSerializer.Deserialize<KeyValuePair<string, TimeSpan>[]>(s);
                    foreach (var item in a) m_timeTogether[item.Key] = item.Value;
                }

                {
                    string s = "[]";
                    try { s = File.ReadAllText(TIME_TOGETHER_UPDATED_AT); }
                    catch (FileNotFoundException) { Console.WriteLine("timeTogetherLastUpdates.json not found."); }
                    var a = JsonSerializer.Deserialize<KeyValuePair<string, DateTime>[]>(s);
                    foreach (var item in a) m_timeTogetherUpdated[item.Key] = item.Value;
                    foreach (var item in m_timeTogether)
                        if (!m_timeTogetherUpdated.ContainsKey(item.Key))
                            m_timeTogetherUpdated[item.Key] = DateTime.Now;
                }

                Console.WriteLine(m_timeTogether.Count + " pairs loaded.");
            }

            if (!m_timeTogether.ContainsKey(us)) m_timeTogether[us] = new TimeSpan();
            m_timeTogether[us] += durationBetweenSamples;
            m_timeTogetherUpdated[us] = DateTime.Now;

            if (m_lastSaveMinuteNumber == null)
                m_lastSaveMinuteNumber = DateTime.Now.Minute;
            else if (m_lastSaveMinuteNumber != DateTime.Now.Minute)
            {
                m_lastSaveMinuteNumber = DateTime.Now.Minute;

                {
                    var newTT = new Dictionary<string, TimeSpan>();
                    var newTTU = new Dictionary<string, DateTime>();
                    try
                    {
                        foreach (var item in m_timeTogetherUpdated)
                        {
                            if (item.Value.AddDays(21) > DateTime.Now)
                            {
                                newTT[item.Key] = m_timeTogether[item.Key];
                                newTTU[item.Key] = item.Value;
                            }
                        }
                        m_timeTogether = newTT;
                        m_timeTogetherUpdated = newTTU;
                    }
                    catch (KeyNotFoundException) { throw; }
                }

                // ── Evict stale sighting records (unseen for >2 hours) ────────────
                {
                    var staleKeys = m_connectionLatestSighting
                        .Where(kvp => kvp.Value < DateTime.Now.AddHours(-2))
                        .Select(kvp => kvp.Key)
                        .ToList();
                    foreach (var key in staleKeys)
                    {
                        m_connectionFirstSighting.Remove(key);
                        m_connectionLatestSighting.Remove(key);
                    }
                }

                // ── Evict inactive users from O(N²) co-jammer and meeting dicts ──
                {
                    // m_timeTogetherUpdated keys are 64-char strings: GUID_A (32) + GUID_B (32).
                    // Extract all GUIDs that still appear in at least one active pair.
                    var activeGuids = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var key in m_timeTogetherUpdated.Keys)
                    {
                        activeGuids.Add(key.Substring(0, 32));
                        activeGuids.Add(key.Substring(32, 32));
                    }

                    foreach (var guid in m_userConnectDurationPerUser.Keys
                                          .Where(g => !activeGuids.Contains(g)).ToList())
                        m_userConnectDurationPerUser.Remove(guid);

                    foreach (var key in m_everywhereWeHaveMet.Keys
                                         .Where(k => !m_timeTogetherUpdated.ContainsKey(k)).ToList())
                        m_everywhereWeHaveMet.Remove(key);
                }

                {
                    var sortedByLongest = m_timeTogether.OrderByDescending(x => x.Value).ToList();
                    string jsonString = JsonSerializer.Serialize(sortedByLongest);
                    File.WriteAllText(TIME_TOGETHER, jsonString);

                    if (DateTime.Now.DayOfYear != m_lastDayNotched)
                    {
                        m_lastDayNotched = DateTime.Now.DayOfYear;
                        File.AppendAllText("wwwroot/paircount.csv",
                            JamulusCacheManager.MinutesSince2023() + "," + sortedByLongest.Count + Environment.NewLine);
                    }
                }

                try
                {
                    string jsonString = JsonSerializer.Serialize(m_timeTogetherUpdated.ToList());
                    File.WriteAllText(TIME_TOGETHER_UPDATED_AT, jsonString);
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(1000);
                    File.WriteAllText(TIME_TOGETHER_UPDATED_AT, JsonSerializer.Serialize(m_timeTogetherUpdated.ToList()));
                }

                try
                {
                    var sortedByAlpha = m_guidNamePairs.OrderBy(x => x.Value).ToList();
                    File.WriteAllText(GUID_NAME_PAIRS, JsonSerializer.Serialize(sortedByAlpha));
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(1000);
                    File.WriteAllText(GUID_NAME_PAIRS, JsonSerializer.Serialize(m_guidNamePairs.OrderBy(x => x.Value).ToList()));
                }

                // ── Memory diagnostic snapshot (once per minute) ──────────────────
                int perUserInnerTotal = 0;
                foreach (var v in m_userConnectDurationPerUser.Values) perUserInnerTotal += v.Count;
                Console.WriteLine(
                    $"[MEM] timeTogether={m_timeTogether.Count} " +
                    $"sightings={m_connectionFirstSighting.Count} " +
                    $"everywhereWeHaveMet={m_everywhereWeHaveMet.Count} " +
                    $"everywhereIveJoinedYou={m_everywhereIveJoinedYou.Count} " +
                    $"perUser.outer={m_userConnectDurationPerUser.Count} perUser.innerTotal={perUserInnerTotal} " +
                    $"perServer.outer={m_userConnectDurationPerServer.Count} " +
                    $"connectDuration={m_userConnectDuration.Count} " +
                    $"serverViewTracker={m_userServerViewTracker.Count} " +
                    $"guidNames={m_guidNamePairs.Count}");
            }
        }

        // ── Session sighting / duration ───────────────────────────────────────

        public static Dictionary<string, DateTime> m_connectionFirstSighting  = new Dictionary<string, DateTime>();
        public static Dictionary<string, DateTime> m_connectionLatestSighting = new Dictionary<string, DateTime>();

        public void NotateWhoHere(string server, string who)
        {
            System.Diagnostics.Debug.Assert(who.Length == "b707dc8fc6516826fbe9b4aa84d1553a".Length);
            string hash = who + server;

            try
            {
                if (!m_connectionFirstSighting.ContainsKey(hash))
                {
                    m_connectionFirstSighting[hash] = DateTime.Now;
                    return;
                }
                if (DateTime.Now > m_connectionLatestSighting[hash].AddMinutes(10))
                    m_connectionFirstSighting[hash] = DateTime.Now;
            }
            finally
            {
                m_connectionLatestSighting[hash] = DateTime.Now;
            }
        }

        public double DurationHereInMins(string server, string who)
        {
            System.Diagnostics.Debug.Assert(who.Length == "b707dc8fc6516826fbe9b4aa84d1553a".Length);
            string hash = who + server;
            if (!m_connectionFirstSighting.ContainsKey(hash)) return -1;

            TimeSpan ts = DateTime.Now.Subtract(m_connectionFirstSighting[hash]);

            if (File.Exists("non-signals.txt"))
            {
                var nonSignals = File.ReadAllLines("non-signals.txt").ToList();
                if (nonSignals.Contains(who))
                    ts = ts.Add(TimeSpan.FromMinutes(60.0 * 5.75));
            }

            return ts.TotalMinutes;
        }

        // ── Aggregate tracking collections ───────────────────────────────────

        public static Dictionary<string, HashSet<string>>              m_userServerViewTracker      = new Dictionary<string, HashSet<string>>();
        public static Dictionary<string, TimeSpan>                     m_userConnectDuration        = new Dictionary<string, TimeSpan>();
        public static Dictionary<string, Dictionary<string, TimeSpan>> m_userConnectDurationPerServer = new Dictionary<string, Dictionary<string, TimeSpan>>();
        public static Dictionary<string, Dictionary<string, TimeSpan>> m_userConnectDurationPerUser   = new Dictionary<string, Dictionary<string, TimeSpan>>();
        public static Dictionary<string, HashSet<string>>              m_everywhereWeHaveMet        = new Dictionary<string, HashSet<string>>();

        public static string CanonicalTwoHashes(string hash1, string hash2)
        {
            return hash1.CompareTo(hash2) < 0 ? hash1 + hash2 : hash2 + hash1;
        }
    }
}
