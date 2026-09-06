using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace JamFan22
{
    /// <summary>
    /// Throttles band-fleet-invite eligibility to at most once per band per week.
    /// Logging-only for now — see TODO.md "Band Fleet Invites". Records an eligible
    /// hit per band_id; does not yet send anything to the player.
    /// </summary>
    public static class BandInviteTracker
    {
        private static readonly ConcurrentDictionary<int, DateTime> _lastEligible = new();
        private static readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);
        private const string FilePath = "data/band-invite-log.json";
        private static readonly TimeSpan Throttle = TimeSpan.FromDays(7);

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var json = File.ReadAllText(FilePath);
                var dict = JsonSerializer.Deserialize<Dictionary<int, DateTime>>(json);
                if (dict == null) return;
                foreach (var kvp in dict)
                    _lastEligible[kvp.Key] = kvp.Value;
                Console.WriteLine($"[BandInviteTracker] Loaded {_lastEligible.Count} band(s).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BandInviteTracker] Load failed, starting empty: {ex.Message}");
            }
        }

        private static void Save()
        {
            _fileLock.Wait();
            try
            {
                var snapshot = new Dictionary<int, DateTime>(_lastEligible);
                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BandInviteTracker] Save failed: {ex.Message}");
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>Returns true and records "now" if this band hasn't been marked eligible
        /// in the last 7 days. Returns false (no-op) if still within the throttle window.</summary>
        public static bool TryMarkEligible(int bandId)
        {
            var now = DateTime.UtcNow;
            if (_lastEligible.TryGetValue(bandId, out var last) && now - last < Throttle)
                return false;
            _lastEligible[bandId] = now;
            Save();
            return true;
        }
    }
}
