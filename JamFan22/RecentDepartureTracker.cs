using System;
using System.Collections.Generic;
using System.Linq;

public static class RecentDepartureTracker
{
    private record PresenceRecord(int FirstMinute, int LastMinute);

    // serverKey → guid → presence record
    private static Dictionary<string, Dictionary<string, PresenceRecord>> _active = new(StringComparer.Ordinal);

    public record DepartureRecord(string Guid, string ServerKey, int DepartureMinute, int SessionMinutes);

    private static readonly List<DepartureRecord> _departed = new();
    private static readonly object _lock = new();

    private const int KeepMinutes = 120;

    public static void UpdateSnapshot(string serverKey, IEnumerable<string> currentGuids, int nowMinutes)
    {
        lock (_lock)
        {
            var currentSet = new HashSet<string>(currentGuids, StringComparer.Ordinal);

            if (!_active.TryGetValue(serverKey, out var presences))
                _active[serverKey] = presences = new Dictionary<string, PresenceRecord>(StringComparer.Ordinal);

            // Detect departures
            var toRemove = new List<string>();
            foreach (var (guid, rec) in presences)
            {
                if (!currentSet.Contains(guid))
                {
                    toRemove.Add(guid);
                    int sessionMins = rec.LastMinute - rec.FirstMinute + 1;
                    _departed.Add(new DepartureRecord(guid, serverKey, nowMinutes, sessionMins));
                }
            }
            foreach (var g in toRemove) presences.Remove(g);

            // Update/add active presences
            foreach (var guid in currentSet)
            {
                if (presences.TryGetValue(guid, out var rec))
                    presences[guid] = rec with { LastMinute = nowMinutes };
                else
                    presences[guid] = new PresenceRecord(nowMinutes, nowMinutes);
            }

            // Prune old departures
            _departed.RemoveAll(d => nowMinutes - d.DepartureMinute > KeepMinutes);
        }
    }

    // Returns recent departures from the given servers, newest first.
    public static List<DepartureRecord> GetRecentDepartures(
        IEnumerable<string> serverKeys, int nowMinutes, int maxAgoMinutes = KeepMinutes, int minSessionMinutes = 1)
    {
        lock (_lock)
        {
            var serverSet = new HashSet<string>(serverKeys, StringComparer.Ordinal);
            return _departed
                .Where(d => serverSet.Contains(d.ServerKey)
                         && nowMinutes - d.DepartureMinute <= maxAgoMinutes
                         && d.SessionMinutes >= minSessionMinutes)
                .OrderByDescending(d => d.DepartureMinute)
                .ToList();
        }
    }
}
