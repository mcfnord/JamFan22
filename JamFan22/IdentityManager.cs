using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JamFan22
{
    public static class IdentityManager
    {
        private static Dictionary<string, (string Name, string Guid)> _ipToPersona = new Dictionary<string, (string Name, string Guid)>();
        private static Dictionary<string, HashSet<string>> _ipToAllAssociatedGuids = new Dictionary<string, HashSet<string>>();
        private static DateTime _lastRead = DateTime.MinValue;
        private static readonly object _lock = new object();

        public static string GetPersona(string ip)
        {
            var details = GetPersonaDetails(ip);
            return details?.Name;
        }

        // MORE LIBERAL CUTOFF: Returns guids with strength >= 16 on this IP
        public static HashSet<string> GetAllAssociatedGuids(string ip)
        {
            GetPersonaDetails(ip); // Trigger a refresh if file was updated
            var ipClean = ip?.Replace("::ffff:", "").Trim();
            if (ipClean != null && _ipToAllAssociatedGuids.ContainsKey(ipClean))
                return _ipToAllAssociatedGuids[ipClean];
            return new HashSet<string>();
        }

        public static (string Name, string Guid)? GetPersonaDetails(string ip)
        {
            var ipClean = ip?.Replace("::ffff:", "").Trim();
            var file = "join-events.csv";

            if (File.Exists(file))
            {
                var lastWrite = File.GetLastWriteTimeUtc(file);
                if (lastWrite > _lastRead)
                {
                    lock (_lock)
                    {
                        if (lastWrite > _lastRead)
                        {
                            var dict = new Dictionary<string, (string Name, string Guid, long Minute)>();
                            var allAssociated = new Dictionary<string, HashSet<string>>();

                            foreach (var line in File.ReadLines(file))
                            {
                                var parts = line.Split(',');
                                if (parts.Length > 12)
                                {
                                    if (int.TryParse(parts[12].Trim(), out int strength))
                                    {
                                        var guid = parts[2].Trim();
                                        var clientIpPort = parts[11].Trim();
                                        var clientIp = clientIpPort.Split(':')[0];

                                        // BROAD NET: Track guids with >= 13 strength for the Hide feature
                                        if (strength >= 13 && !string.IsNullOrWhiteSpace(guid))
                                        {
                                            if (!allAssociated.ContainsKey(clientIp)) allAssociated[clientIp] = new HashSet<string>();
                                            allAssociated[clientIp].Add(guid);
                                        }

                                        // STRICT NET: Only >= 28 strength for UI Enablement and Chat Identity
                                        if (strength >= 28)
                                        {
                                            if (long.TryParse(parts[0].Trim(), out long minute))
                                            {
                                                var nameRaw = parts[3].Trim();
                                                var name = Uri.UnescapeDataString(nameRaw).Replace("+", " ");
                                                if (string.IsNullOrWhiteSpace(name) || name == "-" || name.ToLower().Contains("no name"))
                                                    continue;

                                                if (!dict.ContainsKey(clientIp) || dict[clientIp].Minute < minute)
                                                {
                                                    dict[clientIp] = (name, guid, minute);
                                                }
                                            }
                                        }
                                    }
                                }
                            }

                            var newIpToPersona = new Dictionary<string, (string Name, string Guid)>();
                            foreach (var kvp in dict)
                            {
                                newIpToPersona[kvp.Key] = (kvp.Value.Name, kvp.Value.Guid);
                            }
                            _ipToPersona = newIpToPersona;
                            _ipToAllAssociatedGuids = allAssociated;
                            _lastRead = lastWrite;
                        }
                    }
                }
            }

            if (ipClean != null && _ipToPersona.ContainsKey(ipClean))
                return _ipToPersona[ipClean];

            return null;
        }
    }
}
