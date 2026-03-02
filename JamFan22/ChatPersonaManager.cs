using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JamFan22
{
    public static class ChatPersonaManager
    {
        private static Dictionary<string, (string Name, string Guid)> _ipToPersona = new Dictionary<string, (string Name, string Guid)>();
        private static DateTime _lastRead = DateTime.MinValue;
        private static readonly object _lock = new object();

        public static bool IsFeatureEnabled => !File.Exists("killchat.txt");

        public static string GetPersona(string ip, bool testMode = false)
        {
            var details = GetPersonaDetails(ip, testMode);
            return details?.Name;
        }

        public static (string Name, string Guid)? GetPersonaDetails(string ip, bool testMode = false)
        {
            if (!IsFeatureEnabled)
                return null;

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
                            foreach (var line in File.ReadLines(file))
                            {
                                var parts = line.Split(',');
                                if (parts.Length > 12)
                                {
                                    if (int.TryParse(parts[12].Trim(), out int strength) && strength >= 28)
                                    {
                                        if (long.TryParse(parts[0].Trim(), out long minute))
                                        {
                                            var guid = parts[2].Trim();
                                            var nameRaw = parts[3].Trim();
                                            var name = Uri.UnescapeDataString(nameRaw).Replace("+", " ");
                                            if (string.IsNullOrWhiteSpace(name) || name == "-" || name.ToLower().Contains("no name"))
                                                continue;

                                            var clientIpPort = parts[11].Trim();
                                            var clientIp = clientIpPort.Split(':')[0];

                                            if (!dict.ContainsKey(clientIp) || dict[clientIp].Minute < minute)
                                            {
                                                dict[clientIp] = (name, guid, minute);
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
                            _lastRead = lastWrite;
                        }
                    }
                }
            }

            if (ipClean != null && _ipToPersona.ContainsKey(ipClean))
                return _ipToPersona[ipClean];

            if (testMode)
                return (GetMadeUpName(ipClean ?? "unknown"), "test-guid-" + Math.Abs((ipClean ?? "unknown").GetHashCode()));

            return null;
        }

        private static string GetMadeUpName(string ip)
        {
            var hash = Math.Abs(ip.GetHashCode());
            string[] adjectives = { "Cool", "Funky", "Groovy", "Chill", "Jamming", "Electric", "Smooth", "Bright" };
            string[] nouns = { "Drummer", "Bassist", "Singer", "Pianist", "Guitarist", "Saxophonist", "Fiddler", "Trumpeter" };
            return adjectives[hash % adjectives.Length] + " " + nouns[(hash / adjectives.Length) % nouns.Length];
        }
    }
}