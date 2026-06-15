using System.Collections.Concurrent;

/// <summary>
/// One-time startup index over census.csv.
/// Scanned once on first use; all subsequent queries are O(1) dictionary lookups.
/// New ticks are fed in via AddTick so the index stays current without a full rebuild.
/// census.csv format: minutes,guid,ip:port
/// </summary>
public static class CensusIndex
{
    // serverKey → guid → (totalMinutes, lastMinute)
    private static Dictionary<string, Dictionary<string, (int Total, int LastMinute)>> _byServer = new();
    // guid → serverKey → (totalMinutes, lastMinute)
    private static Dictionary<string, Dictionary<string, (int Total, int LastMinute)>> _byGuid = new();
    // guid → set of day numbers (minutes / 1440) on which the guid appeared
    private static Dictionary<string, HashSet<int>> _byGuidDays = new();
    // guid → serverKey → set of UTC day numbers when guid was on that server
    private static Dictionary<string, Dictionary<string, HashSet<int>>> _byGuidServerDays = new();

    // hour number (minutes/60) → distinct GUID count in that hour
    private static Dictionary<int, int> _hourlyGuidCounts = new();

    // Historical audibility from census.csv col 3: guid → (silentTicks, audibleTicks)
    private static Dictionary<string, (int Silent, int Audible)> _snapshotAudible = new();

    // Live audible delta: "guid:server" → (silentTicks, audibleTicks) since startup
    private static readonly ConcurrentDictionary<string, (int Silent, int Audible)> _deltaAudible = new(StringComparer.Ordinal);
    // Live delta: ticks added since startup (after the initial build completes)
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, (int Total, int LastMinute)>>
        _deltaByServer = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, (int Total, int LastMinute)>>
        _deltaByGuid = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<int, byte>>
        _deltaGuidDays = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentDictionary<int, byte>>>
        _deltaGuidServerDays = new(StringComparer.Ordinal);

    private static volatile bool _built = false;
    private static readonly object _lock = new();

    public static void EnsureBuilt()
    {
        if (_built) return;
        lock (_lock)
        {
            if (_built) return;
            Build();
            _built = true;
        }
    }

    /// <summary>
    /// Called by JamulusCacheManager each time a tick is written to census.csv.
    /// Skipped until the initial build completes to avoid double-counting with the snapshot.
    /// </summary>
    public static void AddTick(int minutes, string guid, string serverKey, string audible = "")
    {
        if (!_built || guid.Length != 32) return;

        if (audible.Length > 0)
        {
            string key = guid + ":" + serverKey;
            bool isAudible = audible != "0";
            _deltaAudible.AddOrUpdate(key,
                _ => isAudible ? (0, 1) : (1, 0),
                (_, old) => isAudible ? (old.Silent, old.Audible + 1) : (old.Silent + 1, old.Audible));
        }

        var sDict = _deltaByServer.GetOrAdd(serverKey, _ => new ConcurrentDictionary<string, (int, int)>(StringComparer.Ordinal));
        sDict.AddOrUpdate(guid, _ => (1, minutes), (_, old) => (old.Total + 1, Math.Max(old.LastMinute, minutes)));

        var gDict = _deltaByGuid.GetOrAdd(guid, _ => new ConcurrentDictionary<string, (int, int)>(StringComparer.Ordinal));
        gDict.AddOrUpdate(serverKey, _ => (1, minutes), (_, old) => (old.Total + 1, Math.Max(old.LastMinute, minutes)));

        _deltaGuidDays.GetOrAdd(guid, _ => new ConcurrentDictionary<int, byte>()).TryAdd(minutes / 1440, 0);

        _deltaGuidServerDays
            .GetOrAdd(guid, _ => new ConcurrentDictionary<string, ConcurrentDictionary<int, byte>>(StringComparer.Ordinal))
            .GetOrAdd(serverKey, _ => new ConcurrentDictionary<int, byte>())
            .TryAdd(minutes / 1440, 0);
    }

    /// <summary>
    /// Returns (silentTicks, audibleTicks) accumulated since startup for the given guid+server.
    /// Returns (0,0) if no data yet.
    /// </summary>
    public static (int Silent, int Audible) GetLiveAudibleCounts(string guid, string serverKey) =>
        _deltaAudible.TryGetValue(guid + ":" + serverKey, out var v) ? v : (0, 0);

    /// <summary>
    /// Merges historical (census.csv col 3) and live delta audible counts for a GUID across all servers.
    /// Returns (0,0) when no audibility data exists (most GUIDs on non-fleet servers).
    /// </summary>
    public static (int Silent, int Audible) GetGuidAudibility(string guid)
    {
        _snapshotAudible.TryGetValue(guid, out var snap);
        int s = snap.Silent, a = snap.Audible;
        string prefix = guid + ":";
        foreach (var kv in _deltaAudible)
            if (kv.Key.StartsWith(prefix, StringComparison.Ordinal)) { s += kv.Value.Silent; a += kv.Value.Audible; }
        return (s, a);
    }

    private const int AudibilityMinTicks = 20;

    /// <summary>True when a GUID has enough audibility data and is almost always silent (listener / lurker).</summary>
    public static bool IsListener(string guid)
    {
        var (s, a) = GetGuidAudibility(guid);
        int total = s + a;
        return total >= AudibilityMinTicks && (double)a / total < 0.15;
    }

    /// <summary>True when a GUID has enough audibility data and is frequently audible (active player).</summary>
    public static bool IsActivePlayer(string guid)
    {
        var (s, a) = GetGuidAudibility(guid);
        int total = s + a;
        return total >= AudibilityMinTicks && (double)a / total > 0.5;
    }

    private static void Build()
    {
        var byServer         = new Dictionary<string, Dictionary<string, (int, int)>>(StringComparer.Ordinal);
        var byGuid           = new Dictionary<string, Dictionary<string, (int, int)>>(StringComparer.Ordinal);
        var byGuidDays       = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var byGuidServerDays = new Dictionary<string, Dictionary<string, HashSet<int>>>(StringComparer.Ordinal);
        var hourGuids        = new Dictionary<int, HashSet<string>>();
        var snapshotAudible  = new Dictionary<string, (int Silent, int Audible)>(StringComparer.Ordinal);

        int lines = 0;
        try
        {
            foreach (var line in File.ReadLines("data/census.csv"))
            {
                lines++;
                var i1 = line.IndexOf(',');
                if (i1 < 0) continue;
                var i2 = line.IndexOf(',', i1 + 1);
                if (i2 < 0) continue;

                if (!int.TryParse(line.AsSpan(0, i1), out int mins)) continue;
                string guid   = line.Substring(i1 + 1, i2 - i1 - 1);
                var i3 = line.IndexOf(',', i2 + 1);
                string server = i3 >= 0 ? line.Substring(i2 + 1, i3 - i2 - 1) : line.Substring(i2 + 1).TrimEnd();
                if (guid.Length != 32) continue;

                // col 3: audible flag (0/1) — only present for fleet-polled ticks
                if (i3 >= 0)
                {
                    var col3 = line.AsSpan(i3 + 1).Trim();
                    if (col3.Length == 1)
                    {
                        bool isAud = col3[0] == '1';
                        bool isSil = col3[0] == '0';
                        if (isAud || isSil)
                        {
                            snapshotAudible.TryGetValue(guid, out var av);
                            snapshotAudible[guid] = isAud ? (av.Silent, av.Audible + 1) : (av.Silent + 1, av.Audible);
                        }
                    }
                }

                // byServer
                if (!byServer.TryGetValue(server, out var sDict))
                    byServer[server] = sDict = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
                if (sDict.TryGetValue(guid, out var sv))
                    sDict[guid] = (sv.Item1 + 1, Math.Max(sv.Item2, mins));
                else
                    sDict[guid] = (1, mins);

                // byGuid
                if (!byGuid.TryGetValue(guid, out var gDict))
                    byGuid[guid] = gDict = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
                if (gDict.TryGetValue(server, out var gv))
                    gDict[server] = (gv.Item1 + 1, Math.Max(gv.Item2, mins));
                else
                    gDict[server] = (1, mins);

                // byGuidDays
                if (!byGuidDays.TryGetValue(guid, out var dSet))
                    byGuidDays[guid] = dSet = new HashSet<int>();
                dSet.Add(mins / 1440);

                // byGuidServerDays
                if (!byGuidServerDays.TryGetValue(guid, out var gsdDict))
                    byGuidServerDays[guid] = gsdDict = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
                if (!gsdDict.TryGetValue(server, out var sdSet))
                    gsdDict[server] = sdSet = new HashSet<int>();
                sdSet.Add(mins / 1440);

                // hourlyGuidCounts
                int hourKey = mins / 60;
                if (!hourGuids.TryGetValue(hourKey, out var hSet))
                    hourGuids[hourKey] = hSet = new HashSet<string>(StringComparer.Ordinal);
                hSet.Add(guid);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CENSUS-INDEX] Build failed at line {lines}: {ex.Message}");
        }

        _byServer         = byServer;
        _byGuid           = byGuid;
        _byGuidDays       = byGuidDays;
        _byGuidServerDays = byGuidServerDays;
        _snapshotAudible  = snapshotAudible;
        var hc = new Dictionary<int, int>(hourGuids.Count);
        foreach (var kv in hourGuids) hc[kv.Key] = kv.Value.Count;
        _hourlyGuidCounts = hc;
        Console.WriteLine($"[CENSUS-INDEX] Built: {lines} rows, {byServer.Count} servers, {byGuid.Count} guids, {snapshotAudible.Count} with audibility data");
    }

    private static bool HasDay(string guid, int dayNum)
    {
        if (_byGuidDays.TryGetValue(guid, out var days) && days.Contains(dayNum)) return true;
        if (_deltaGuidDays.TryGetValue(guid, out var dDays) && dDays.ContainsKey(dayNum)) return true;
        return false;
    }

    private static bool HasServerDay(string guid, string serverKey, int dayNum)
    {
        if (_byGuidServerDays.TryGetValue(guid, out var gsd) && gsd.TryGetValue(serverKey, out var days) && days.Contains(dayNum)) return true;
        if (_deltaGuidServerDays.TryGetValue(guid, out var dgsd) && dgsd.TryGetValue(serverKey, out var dDays) && dDays.ContainsKey(dayNum)) return true;
        return false;
    }

    /// <summary>Returns (totalMinutes, lastMinute, isTopVisitor) for a GUID on a server key set.</summary>
    public static (int Total, int LastMinute, bool IsTop) GetGuidOnServer(string guid, IEnumerable<string> serverKeys)
    {
        int total = 0, last = -1;
        int serverMax = 0;

        foreach (var key in serverKeys)
        {
            if (_byServer.TryGetValue(key, out var sDict))
            {
                foreach (var kv in sDict)
                    if (kv.Value.Item1 > serverMax) serverMax = kv.Value.Item1;
                if (sDict.TryGetValue(guid, out var v))
                {
                    total += v.Item1;
                    if (v.Item2 > last) last = v.Item2;
                }
            }

            if (_deltaByServer.TryGetValue(key, out var dsDict))
            {
                foreach (var kv in dsDict)
                {
                    int combinedTotal = kv.Value.Total;
                    if (_byServer.TryGetValue(key, out var snap) && snap.TryGetValue(kv.Key, out var snapV))
                        combinedTotal += snapV.Item1;
                    if (combinedTotal > serverMax) serverMax = combinedTotal;
                }
                if (dsDict.TryGetValue(guid, out var dv))
                {
                    total += dv.Total;
                    if (dv.LastMinute > last) last = dv.LastMinute;
                }
            }
        }

        return (total, last, total > 0 && total >= serverMax);
    }

    /// <summary>Returns guids active on any of the server keys since cutoffMinute.</summary>
    public static List<string> GetRecentVisitors(IEnumerable<string> serverKeys, int cutoffMinute, string excludeGuid)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in serverKeys)
        {
            if (_byServer.TryGetValue(key, out var sDict))
                foreach (var kv in sDict)
                    if (kv.Key != excludeGuid && kv.Value.Item2 >= cutoffMinute)
                        result.Add(kv.Key);
            if (_deltaByServer.TryGetValue(key, out var dsDict))
                foreach (var kv in dsDict)
                    if (kv.Key != excludeGuid && kv.Value.LastMinute >= cutoffMinute)
                        result.Add(kv.Key);
        }
        return result.ToList();
    }

    /// <summary>Returns the server keys this GUID has visited, sorted by total minutes descending.</summary>
    public static List<(string ServerKey, int Total, int LastMinute)> GetGuidServers(string guid)
    {
        var merged = new Dictionary<string, (int Total, int LastMinute)>(StringComparer.Ordinal);
        if (_byGuid.TryGetValue(guid, out var gDict))
            foreach (var kv in gDict) merged[kv.Key] = (kv.Value.Item1, kv.Value.Item2);
        if (_deltaByGuid.TryGetValue(guid, out var dgDict))
            foreach (var kv in dgDict)
            {
                if (merged.TryGetValue(kv.Key, out var ex))
                    merged[kv.Key] = (ex.Total + kv.Value.Total, Math.Max(ex.LastMinute, kv.Value.LastMinute));
                else
                    merged[kv.Key] = (kv.Value.Total, kv.Value.LastMinute);
            }
        return merged
            .Select(kv => (kv.Key, kv.Value.Total, kv.Value.LastMinute))
            .OrderByDescending(x => x.Total)
            .ToList();
    }

    /// <summary>Returns total census ticks across all servers for a GUID (90-day window).</summary>
    public static int GetGuidLifetime(string guid)
    {
        int total = 0;
        if (_byGuid.TryGetValue(guid, out var gDict))
            foreach (var kv in gDict) total += kv.Value.Item1;
        if (_deltaByGuid.TryGetValue(guid, out var dgDict))
            foreach (var kv in dgDict) total += kv.Value.Total;
        return total;
    }

    /// <summary>Returns the most recent census minute for a GUID across all servers, or 0 if never seen.</summary>
    public static int GetGuidLastSeenMinute(string guid)
    {
        int last = 0;
        if (_byGuid.TryGetValue(guid, out var gDict))
            foreach (var kv in gDict) if (kv.Value.LastMinute > last) last = kv.Value.LastMinute;
        if (_deltaByGuid.TryGetValue(guid, out var dgDict))
            foreach (var kv in dgDict) if (kv.Value.LastMinute > last) last = kv.Value.LastMinute;
        return last;
    }

    /// <summary>Returns the number of consecutive calendar days (ending today) on which the GUID was seen.
    /// nowMinutes is minutes since the census epoch (same as census.csv values).</summary>
    public static int GetGuidStreak(string guid, int nowMinutes)
    {
        int today = nowMinutes / 1440;
        int streak = 0;
        while (HasDay(guid, today - streak))
            streak++;
        return streak;
    }

    private static DayOfWeek UtcDayToDow(int dayNum) =>
        new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(dayNum).DayOfWeek;

    /// <summary>Consecutive weeks (ending this week) where guid appeared on serverKeys on targetDow.
    /// Returns 0 if today (UTC) is not targetDow. Always counts the current week as 1.</summary>
    public static int GetGuidWeekdayStreak(string guid, IEnumerable<string> serverKeys, DayOfWeek targetDow, int nowMinutes)
    {
        int nowDay = nowMinutes / 1440;
        if (UtcDayToDow(nowDay) != targetDow) return 0;

        var matchWeeks = new HashSet<int>();
        var keyList = serverKeys.ToList();
        foreach (var key in keyList)
        {
            if (_byGuidServerDays.TryGetValue(guid, out var gsd) && gsd.TryGetValue(key, out var days))
                foreach (var d in days)
                    if (UtcDayToDow(d) == targetDow) matchWeeks.Add(d / 7);
            if (_deltaGuidServerDays.TryGetValue(guid, out var dgsd) && dgsd.TryGetValue(key, out var dDays))
                foreach (var d in dDays.Keys)
                    if (UtcDayToDow(d) == targetDow) matchWeeks.Add(d / 7);
        }

        int nowWeek = nowDay / 7;
        int streak = 1; // count today
        for (int w = nowWeek - 1; w >= 0; w--)
        {
            if (matchWeeks.Contains(w)) streak++;
            else break;
        }
        return streak;
    }

    /// <summary>Consecutive weeks (ending this week) where guidA and guidB both appeared on
    /// any shared server on targetDow. Returns 0 if today (UTC) is not targetDow.</summary>
    public static int GetPairWeekdayStreak(string guidA, string guidB, DayOfWeek targetDow, int nowMinutes)
    {
        int nowDay = nowMinutes / 1440;
        if (UtcDayToDow(nowDay) != targetDow) return 0;

        // Build guidB's server→days lookup (snapshot + delta)
        var bMap = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        if (_byGuidServerDays.TryGetValue(guidB, out var bSnap))
            foreach (var kv in bSnap) bMap[kv.Key] = kv.Value;
        if (_deltaGuidServerDays.TryGetValue(guidB, out var bDelta))
            foreach (var kv in bDelta)
            {
                if (!bMap.TryGetValue(kv.Key, out var set)) bMap[kv.Key] = set = new HashSet<int>();
                foreach (var d in kv.Value.Keys) set.Add(d);
            }

        var sharedWeeks = new HashSet<int>();
        void Scan(string server, IEnumerable<int> days)
        {
            if (!bMap.TryGetValue(server, out var bDays)) return;
            foreach (var d in days)
                if (UtcDayToDow(d) == targetDow && bDays.Contains(d))
                    sharedWeeks.Add(d / 7);
        }
        if (_byGuidServerDays.TryGetValue(guidA, out var aSnap))
            foreach (var kv in aSnap) Scan(kv.Key, kv.Value);
        if (_deltaGuidServerDays.TryGetValue(guidA, out var aDelta))
            foreach (var kv in aDelta) Scan(kv.Key, kv.Value.Keys);

        int nowWeek = nowDay / 7;
        int streak = 1;
        for (int w = nowWeek - 1; w >= 0; w--)
        {
            if (sharedWeeks.Contains(w)) streak++;
            else break;
        }
        return streak;
    }

    /// <summary>
    /// Returns approximate co-jammer minutes for a GUID using census cross-product estimation.
    /// For each shared server: approx_together = guidA_ticks * guidB_ticks / server_total_ticks.
    /// Accurate when two players dominate a server; conservative for busy servers.
    /// Covers the full census window (90 days) unlike m_timeTogether which resets on restart.
    /// </summary>
    /// <summary>Average distinct GUID count for utcHour (0-23) over the past 7 complete days.
    /// Returns 0 if insufficient history.</summary>
    public static int GetTypicalNetworkSizeAtHour(int utcHour, int nowMinutes)
    {
        int nowHour = nowMinutes / 60;
        int todayStart = nowHour - (nowHour % 24);
        var counts = new List<int>(7);
        for (int d = 1; d <= 7; d++)
        {
            int h = todayStart - (d - 1) * 24 + utcHour;
            if (h < 0) break;
            if (_hourlyGuidCounts.TryGetValue(h, out int c) && c > 0)
                counts.Add(c);
        }
        return counts.Count > 0 ? (int)counts.Average() : 0;
    }

    public static List<(string Guid, int ApproxMins)> GetApproxCoJammers(string guid, int minApproxMins = 1)
    {
        var myServers = new Dictionary<string, (int Total, int LastMinute)>(StringComparer.Ordinal);
        if (_byGuid.TryGetValue(guid, out var snapServers))
            foreach (var kv in snapServers) myServers[kv.Key] = (kv.Value.Item1, kv.Value.Item2);
        if (_deltaByGuid.TryGetValue(guid, out var deltaServers))
            foreach (var kv in deltaServers)
            {
                if (myServers.TryGetValue(kv.Key, out var ex))
                    myServers[kv.Key] = (ex.Total + kv.Value.Total, Math.Max(ex.LastMinute, kv.Value.LastMinute));
                else
                    myServers[kv.Key] = (kv.Value.Total, kv.Value.LastMinute);
            }
        if (myServers.Count == 0) return new();

        var coPres = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (serverKey, myTotal, _) in myServers.Select(kv => (kv.Key, kv.Value.Total, kv.Value.LastMinute)))
        {
            // Merge snapshot + delta for this server
            var serverPlayers = new Dictionary<string, int>(StringComparer.Ordinal);
            if (_byServer.TryGetValue(serverKey, out var sSnap))
                foreach (var kv in sSnap) serverPlayers[kv.Key] = kv.Value.Item1;
            if (_deltaByServer.TryGetValue(serverKey, out var sDelta))
                foreach (var kv in sDelta)
                    serverPlayers[kv.Key] = (serverPlayers.TryGetValue(kv.Key, out var ex) ? ex : 0) + kv.Value.Total;

            int serverTotal = 0;
            foreach (var kv in serverPlayers) serverTotal += kv.Value;
            if (serverTotal == 0) continue;

            foreach (var (otherGuid, otherTotal) in serverPlayers.Select(kv => (kv.Key, kv.Value)))
            {
                if (otherGuid == guid) continue;
                int approx = myTotal * otherTotal / serverTotal;
                if (approx < 1) continue;
                coPres[otherGuid] = (coPres.TryGetValue(otherGuid, out var existing) ? existing : 0) + approx;
            }
        }

        return coPres
            .Where(kv => kv.Value >= minApproxMins)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }
}
