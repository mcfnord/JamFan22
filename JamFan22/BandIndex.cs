using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace JamFan22
{
    /// <summary>
    /// Reads data/bands.json (written weekly by band-finder.py) and detects
    /// when a band "canary" member is present on a server, returning the
    /// missing members' names to add to the Soon field.
    /// </summary>
    public static class BandIndex
    {
        record BandMember(string Guid, string Name, string CanaryLevel, double TriggerRate);
        record Band(int Id, string? BandName, string? PrimaryServer, List<BandMember> Members,
                    int SessionCount, double SpanWeeks, double CoreStability);

        /// <summary>Returned when a canary triggers.</summary>
        public record BandSoonResult(
            int BandId,
            string? BandName,
            string CanaryNames,         // e.g. "Susan" or "KAV + Pascal"
            List<string> Missing,       // members not yet on server, not yet in Soon
            double CanaryTriggerRate,   // solo_trigger_rate of the highest-rate triggering canary
            bool HasLore);              // eligible for a band lore paragraph

        static List<Band>? _bands;
        static DateTime _lastLoaded = DateTime.MinValue;
        const string Path = "data/bands.json";

        static void RefreshIfStale()
        {
            if ((DateTime.UtcNow - _lastLoaded).TotalHours < 12) return;
            try
            {
                if (!File.Exists(Path)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(Path));
                var bands = new List<Band>();
                foreach (var b in doc.RootElement.GetProperty("bands").EnumerateArray())
                {
                    var members = b.GetProperty("members").EnumerateArray()
                        .Select(m => new BandMember(
                            m.GetProperty("guid").GetString()!,
                            m.GetProperty("name").GetString()!,
                            m.GetProperty("canary_level").GetString()!,
                            m.TryGetProperty("solo_trigger_rate", out var tr) ? tr.GetDouble() : 1.0))
                        .ToList();
                    if (b.TryGetProperty("disabled", out var dis) && dis.GetBoolean()) continue;
                    string? bandName = b.TryGetProperty("band_name", out var bn) ? bn.GetString() : null;
                    string? primaryServer = b.TryGetProperty("primary_server", out var ps) ? ps.GetString() : null;
                    int sessionCount = b.TryGetProperty("session_count", out var sc) ? sc.GetInt32() : 0;
                    double spanWeeks = b.TryGetProperty("span_weeks", out var sw) ? sw.GetDouble() : 0;
                    double coreStability = b.TryGetProperty("core_stability", out var cs) ? cs.GetDouble() : 0;
                    bands.Add(new Band(b.GetProperty("id").GetInt32(), bandName, primaryServer, members,
                                       sessionCount, spanWeeks, coreStability));
                }
                _bands = bands;
                _lastLoaded = DateTime.UtcNow;
            }
            catch { /* missing or malformed — skip silently */ }
        }

        /// <summary>
        /// Checks whether any current player is a band canary.
        /// Returns null if nothing triggers.
        /// The caller is responsible for de-duplication against existing soonNames and logging.
        /// recentlyDepartedGuids: GUIDs that just left this server — suppresses them from Missing.
        /// </summary>
        public static BandSoonResult? GetBandSoon(
            HashSet<string> currentGuids,
            HashSet<string> currentNames,
            string currentServer,
            HashSet<string>? recentlyDepartedGuids = null)
        {
            RefreshIfStale();
            if (_bands == null) return null;

            foreach (var band in _bands)
            {
                if (band.PrimaryServer != null &&
                    !string.Equals(band.PrimaryServer, currentServer, StringComparison.OrdinalIgnoreCase))
                    continue;
                var present = band.Members.Where(m => currentGuids.Contains(m.Guid)).ToList();
                if (present.Count == 0) continue;

                bool triggered =
                    present.Any(m => m.CanaryLevel == "strong") ||
                    present.Count(m => m.CanaryLevel is "strong" or "pair") >= 2;
                if (!triggered) continue;

                var missing = band.Members
                    .Where(m => !currentGuids.Contains(m.Guid)
                             && !currentNames.Contains(m.Name, StringComparer.OrdinalIgnoreCase)
                             && !string.IsNullOrWhiteSpace(m.Name)
                             && !m.Name.Equals("No Name", StringComparison.OrdinalIgnoreCase)
                             && !(recentlyDepartedGuids?.Contains(m.Guid) ?? false))
                    .Select(m => m.Name)
                    .ToList();

                var triggeringCanaries = present.Where(m => m.CanaryLevel is "strong" or "pair").ToList();
                string canaryNames = string.Join(" + ", triggeringCanaries.Select(m => m.Name));
                double topRate = triggeringCanaries.Max(m => m.TriggerRate);
                bool hasLore = band.SessionCount >= 4 && band.SpanWeeks >= 3.0 && band.CoreStability >= 0.75;

                return new BandSoonResult(band.Id, band.BandName, canaryNames, missing, topRate, hasLore);
            }

            return null;
        }

        /// <summary>Looks up which band a guid belongs to, if any. Used to detect band members
        /// on arrival for fleet-invite eligibility — independent of canary/current-server state.</summary>
        public static (int BandId, string? BandName)? FindBandForGuid(string guid)
        {
            RefreshIfStale();
            if (_bands == null) return null;

            foreach (var band in _bands)
                if (band.Members.Any(m => m.Guid == guid))
                    return (band.Id, band.BandName);

            return null;
        }
    }
}
