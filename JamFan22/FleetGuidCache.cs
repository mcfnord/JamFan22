using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace JamFan22
{
    public static class FleetGuidCache
    {
        private static readonly ConcurrentDictionary<string, (string ip, DateTime lastSeen, int hitCount)> _guidCache = new();
        private static readonly ConcurrentDictionary<string, List<string>> _ipToGuids = new();
        // key: "guid|ip", value: set of "yyyy-MM-dd" UTC day strings
        private static readonly ConcurrentDictionary<string, HashSet<string>> _guidIpDays = new();
        // key: guid, value: (ip, timestamp_minutes) of most recent non-blocked entry
        private static readonly ConcurrentDictionary<string, (string ip, long minutes)> _guidBestNonBlocked = new();
        private static readonly object _lock = new object();
        private static readonly object _csvLock = new object();

        private static readonly DateTime _epoch = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static void UpsertGuid(string guid, string ip, string serverIp, bool blocked)
        {
            _guidCache.AddOrUpdate(guid,
                _ => (ip, DateTime.UtcNow, 1),
                (_, old) => (ip, DateTime.UtcNow, old.hitCount + 1));

            _ipToGuids.AddOrUpdate(ip,
                _ => new List<string> { guid },
                (_, list) =>
                {
                    lock (_lock) { if (!list.Contains(guid)) list.Add(guid); }
                    return list;
                });

            string dayKey = $"{guid}|{ip}";
            string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            _guidIpDays.AddOrUpdate(dayKey,
                _ => new HashSet<string> { today },
                (_, set) => { lock (_lock) { set.Add(today); } return set; });

            long minutes = (long)(DateTime.UtcNow - _epoch).TotalMinutes;
            if (!blocked)
            {
                _guidBestNonBlocked.AddOrUpdate(guid,
                    _ => (ip, minutes),
                    (_, old) => minutes >= old.minutes ? (ip, minutes) : old);
            }
            lock (_csvLock)
                File.AppendAllText("data/fleet-guid-ip.csv", $"{minutes},{guid},{ip},{serverIp.Replace("::ffff:", "")},{(blocked ? 1 : 0)}\n");

            int hitCount = GetHitCount(guid);
            int synth = Math.Min(hitCount * 4, 12);
            Console.WriteLine($"[FLEET-CACHE] ip={ip} guid={guid} hitCount={hitCount} synthStrength={synth}");
        }

        public static void HydrateFromCsv()
        {
            const string path = "data/fleet-guid-ip.csv";
            if (!File.Exists(path)) return;
            int loaded = 0;
            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split(',');
                if (parts.Length < 4) continue;
                string guid = parts[1].Trim();
                string clientIp = parts[2].Trim();
                string serverIp = parts[3].Trim();
                if (string.IsNullOrEmpty(guid) || string.IsNullOrEmpty(clientIp)) continue;

                _guidCache.AddOrUpdate(guid,
                    _ => (clientIp, DateTime.UtcNow, 1),
                    (_, old) => (clientIp, old.lastSeen, old.hitCount + 1));

                _ipToGuids.AddOrUpdate(clientIp,
                    _ => new List<string> { guid },
                    (_, list) =>
                    {
                        lock (_lock) { if (!list.Contains(guid)) list.Add(guid); }
                        return list;
                    });

                bool rowBlocked = parts.Length > 4 && parts[4].Trim() == "1";
                if (long.TryParse(parts[0].Trim(), out long rowMinutes))
                {
                    string day = _epoch.AddMinutes(rowMinutes).ToString("yyyy-MM-dd");
                    string dayKey = $"{guid}|{clientIp}";
                    _guidIpDays.AddOrUpdate(dayKey,
                        _ => new HashSet<string> { day },
                        (_, set) => { lock (_lock) { set.Add(day); } return set; });

                    if (!rowBlocked)
                    {
                        _guidBestNonBlocked.AddOrUpdate(guid,
                            _ => (clientIp, rowMinutes),
                            (_, old) => rowMinutes >= old.minutes ? (clientIp, rowMinutes) : old);
                    }
                }
                loaded++;
            }
            Console.WriteLine($"[FLEET-CACHE] Hydrated {loaded} rows from {path}");
        }

        public static List<string> GetGuidsByIp(string ip)
            => _ipToGuids.TryGetValue(ip, out var list) ? list : new List<string>();

        public static int GetHitCount(string guid)
            => _guidCache.TryGetValue(guid, out var entry) ? entry.hitCount : 0;

        public static int GetCalendarDayCount(string guid, string ip)
        {
            string key = $"{guid}|{ip}";
            return _guidIpDays.TryGetValue(key, out var days) ? days.Count : 0;
        }

        public static List<string> GetHighConfidenceIpsByGuid(string guid, int minDays = 3)
        {
            var result = new List<string>();
            foreach (var kv in _guidIpDays)
            {
                if (!kv.Key.StartsWith(guid + "|")) continue;
                if (kv.Value.Count >= minDays)
                    result.Add(kv.Key.Substring(guid.Length + 1));
            }
            return result;
        }

        // Returns the most recent non-blocked client IP for a GUID, or null if none.
        public static string GetBestNonBlockedIpByGuid(string guid)
            => _guidBestNonBlocked.TryGetValue(guid, out var entry) ? entry.ip : null;

        // Returns all IPs ever observed for a GUID from fleet, with their day counts.
        public static List<(string ip, int days)> GetAllFleetIpsByGuid(string guid)
        {
            var result = new List<(string, int)>();
            foreach (var kv in _guidIpDays)
            {
                if (!kv.Key.StartsWith(guid + "|")) continue;
                result.Add((kv.Key.Substring(guid.Length + 1), kv.Value.Count));
            }
            return result;
        }
    }
}
