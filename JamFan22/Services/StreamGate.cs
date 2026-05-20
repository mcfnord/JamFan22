using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using JamFan22.Models;
using JamFan22.Services;

namespace JamFan22
{
    public static class StreamGate
    {
        private const string RemoteHost    = "root@147.182.199.22";
        private const string RemoteEnvFile = "/opt/jamulus-lounge/.env";
        private const string RemoteCompose    = "/opt/jamulus-lounge/docker-compose.yml";
        private const string RemoteConfigFile = "/opt/jamulus-lounge/server/config.json";

        class GateState
        {
            public string ActiveIp      { get; set; } = "";
            public string JamulusServer { get; set; } = "";
            public DateTime ExpiryUtc   { get; set; } = DateTime.MinValue;
        }

        class WeeklyReservation
        {
            public string    Ip            { get; set; } = "";
            public string    JamulusServer { get; set; } = "";
            public DayOfWeek DayOfWeek     { get; set; }
            public int       StartHour     { get; set; }
            public int       DurationHours { get; set; } = 4;
        }

        private static GateState _currentState = new();
        private static List<WeeklyReservation> _reservations = new();

        public static string ActiveJamulusServer =>
            !string.IsNullOrEmpty(_currentState.JamulusServer) ? _currentState.JamulusServer : null;
        private static readonly object _gateLock = new object();
        private const string StateFile        = "data/stream-gate.json";
        private const string ReservationsFile = "data/stream-reservations.json";
        private const string TargetFile       = "wwwroot/stream-target.txt";

        public static void Load()
        {
            try
            {
                if (!File.Exists(StateFile)) return;
                var json   = File.ReadAllText(StateFile);
                var loaded = JsonSerializer.Deserialize<GateState>(json);
                if (loaded != null) _currentState = loaded;
                Console.WriteLine($"[StreamGate] Loaded: ActiveIp={_currentState.ActiveIp} Server={_currentState.JamulusServer} Expiry={_currentState.ExpiryUtc:u}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StreamGate] Load failed, starting fresh: {ex.Message}");
            }

            LoadReservations();
            RestoreFromWeeklyIfNeeded();
        }

        private static void LoadReservations()
        {
            try
            {
                if (!File.Exists(ReservationsFile)) return;
                var json   = File.ReadAllText(ReservationsFile);
                var loaded = JsonSerializer.Deserialize<List<WeeklyReservation>>(json);
                if (loaded != null) _reservations = loaded;
                Console.WriteLine($"[StreamGate] Loaded {_reservations.Count} weekly reservation(s).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StreamGate] Reservations load failed: {ex.Message}");
            }
        }

        private static void RestoreFromWeeklyIfNeeded()
        {
            if (DateTime.UtcNow < _currentState.ExpiryUtc) return;

            var now    = DateTime.UtcNow;
            var active = _reservations.FirstOrDefault(r => IsWeeklyWindowActive(r, now));
            if (active == null) return;

            _currentState.ActiveIp      = active.Ip;
            _currentState.JamulusServer = active.JamulusServer;
            _currentState.ExpiryUtc     = GetWeeklyWindowEnd(active, now);
            SaveState();
            File.WriteAllText(TargetFile, active.JamulusServer);
            Console.WriteLine($"[StreamGate] Restored weekly reservation for {active.Ip} -> {active.JamulusServer}, expires {_currentState.ExpiryUtc:u}");
            _ = ConnectGojamAsync(active.JamulusServer);
        }

        private static void SaveState()
        {
            var json = JsonSerializer.Serialize(_currentState, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StateFile, json);
        }

        private static void SaveReservations()
        {
            var json = JsonSerializer.Serialize(_reservations, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ReservationsFile, json);
        }

        private static bool IsWeeklyWindowActive(WeeklyReservation r, DateTime utcNow)
        {
            var windowStart = MostRecentOccurrence(r.DayOfWeek, r.StartHour, utcNow);
            return utcNow < windowStart.AddHours(r.DurationHours);
        }

        private static DateTime GetWeeklyWindowEnd(WeeklyReservation r, DateTime utcNow)
            => MostRecentOccurrence(r.DayOfWeek, r.StartHour, utcNow).AddHours(r.DurationHours);

        private static DateTime NextWindowStart(WeeklyReservation r, DateTime utcNow)
            => MostRecentOccurrence(r.DayOfWeek, r.StartHour, utcNow).AddDays(7);

        // Returns the most recent past (or current) UTC DateTime at (dow, startHour:00).
        private static DateTime MostRecentOccurrence(DayOfWeek dow, int startHour, DateTime utcNow)
        {
            var candidate = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, startHour, 0, 0, DateTimeKind.Utc);
            int daysBack  = ((int)utcNow.DayOfWeek - (int)dow + 7) % 7;
            candidate     = candidate.AddDays(-daysBack);
            if (candidate > utcNow) candidate = candidate.AddDays(-7);
            return candidate;
        }

        // Strip ::ffff: prefix so we get a plain IPv4 address for the Jamulus server.
        private static string BareIp(string ip)
            => ip.StartsWith("::ffff:") ? ip.Substring(7) : ip;

        private static int FindPortInDirectory(string bareIp)
        {
            foreach (var json in JamulusCacheManager.LastReportedList.Values)
            {
                try
                {
                    var servers = JsonSerializer.Deserialize<List<JamulusServers>>(json);
                    if (servers == null) continue;
                    var match = servers.FirstOrDefault(s => s.ip == bareIp);
                    if (match != null) return (int)match.port;
                }
                catch { }
            }
            return 22124;
        }

        private static (int uniqueGuids, int activeDays) ReadServerHistory(string bareIp)
        {
            const string CensusFile = "data/census.csv";
            var guids = new HashSet<string>();
            var days  = new HashSet<int>();
            if (!File.Exists(CensusFile)) return (0, 0);
            foreach (var line in File.ReadLines(CensusFile))
            {
                var parts = line.Split(',');
                if (parts.Length < 3) continue;
                var ipPort   = parts[2].Trim();
                var colonIdx = ipPort.LastIndexOf(':');
                if (colonIdx < 0) continue;
                if (ipPort.Substring(0, colonIdx) != bareIp) continue;
                guids.Add(parts[1].Trim());
                if (int.TryParse(parts[0].Trim(), out int minute))
                    days.Add(minute / 1440);
            }
            return (guids.Count, days.Count);
        }

        public static string TryRequestStream(string requestIp, bool isWeekly)
        {
            bool   allocated = false;
            string message;
            string previousServer = "";
            string bareIp        = BareIp(requestIp);
            int    port          = FindPortInDirectory(bareIp);
            string jamulusServer = $"{bareIp}:{port}";

            // Only check history if the slot is free — if occupied, let them see the "in use" message
            // so a prohibited server thinks they just need to wait.
            bool slotFree;
            lock (_gateLock) { slotFree = DateTime.UtcNow >= _currentState.ExpiryUtc; }
            if (slotFree)
            {
                var (uniqueGuids, activeDays) = ReadServerHistory(bareIp);
                if (uniqueGuids < 16 || activeDays < 3)
                    return "Prohibited.\n";
            }

            lock (_gateLock)
            {
                if (DateTime.UtcNow < _currentState.ExpiryUtc)
                {
                    int minsLeft = (int)Math.Ceiling((_currentState.ExpiryUtc - DateTime.UtcNow).TotalMinutes);
                    if (_currentState.ActiveIp == requestIp)
                    {
                        if (isWeekly)
                        {
                            SaveWeeklyReservation(requestIp, jamulusServer);
                            return $"Already streaming. {minsLeft} minutes remaining. Repeats weekly.\n";
                        }
                        return $"Already streaming. {minsLeft} minutes remaining.\n";
                    }

                    return $"In use by another server for {minsLeft} more minutes.\n";
                }

                previousServer              = _currentState.JamulusServer;
                _currentState.ActiveIp      = requestIp;
                _currentState.JamulusServer = jamulusServer;

                // Cap lease to avoid blocking an upcoming weekly reservation from another IP.
                var proposedExpiry = DateTime.UtcNow.AddHours(4);
                string? cappedBy = null;
                foreach (var res in _reservations)
                {
                    if (res.Ip == requestIp || IsWeeklyWindowActive(res, DateTime.UtcNow)) continue;
                    var nextStart = NextWindowStart(res, DateTime.UtcNow);
                    if (nextStart < proposedExpiry) { proposedExpiry = nextStart; cappedBy = res.Ip; }
                }
                _currentState.ExpiryUtc = proposedExpiry;

                SaveState();
                File.WriteAllText(TargetFile, jamulusServer);
                allocated = true;

                int minsAllocated = (int)(_currentState.ExpiryUtc - DateTime.UtcNow).TotalMinutes;
                string capNote = cappedBy != null ? $" (capped — reservation starts then)" : "";
                if (isWeekly)
                {
                    SaveWeeklyReservation(requestIp, jamulusServer);
                    message = $"Stream allocated for {minsAllocated} minutes{capNote}. This time slot repeats each week.\n";
                }
                else
                {
                    message = $"Stream allocated for {minsAllocated} minutes{capNote}.\n";
                }
            }

            if (allocated)
            {
                if (!string.IsNullOrEmpty(previousServer) && previousServer != jamulusServer)
                    _ = DisconnectThenConnectAsync(jamulusServer);
                else
                    _ = ConnectGojamAsync(jamulusServer);
            }

            return message;
        }

        private static void SaveWeeklyReservation(string ip, string jamulusServer)
        {
            var now      = DateTime.UtcNow;
            var idx      = _reservations.FindIndex(r => r.Ip == ip);
            var newEntry = new WeeklyReservation
            {
                Ip            = ip,
                JamulusServer = jamulusServer,
                DayOfWeek     = now.DayOfWeek,
                StartHour     = now.Hour,
                DurationHours = 4,
            };
            if (idx >= 0)
                _reservations[idx] = newEntry;
            else
                _reservations.Add(newEntry);
            SaveReservations();
            Console.WriteLine($"[StreamGate] Weekly reservation saved for {ip}: every {now.DayOfWeek} {now.Hour:00}:00 UTC");
        }

        // Update this URL when the lounge moves to a new address.
        private const string LoungeAnnouncement = "Hear and record this jam at https://ear.jamulus.live/";

        private static async Task ConnectGojamAsync(string jamulusServer)
        {
            string envContent    = $"JAMULUS_SERVER={jamulusServer}\n";
            string serverName    = LookupServerName(jamulusServer);
            string fleetServerIp = jamulusServer.Split(':')[0];
            string configContent = System.Text.Json.JsonSerializer.Serialize(new { title = serverName, chatOnConnect = LoungeAnnouncement, fleetServerIp });
            await RunSshAsync($"printf '%s' {ShellEscape(envContent)} > {RemoteEnvFile} && printf '%s' {ShellEscape(configContent)} > {RemoteConfigFile} && docker compose -f {RemoteCompose} up -d gojam");
        }

        public static string ResetStream(string requestIp)
        {
            lock (_gateLock)
            {
                if (_currentState.ActiveIp != requestIp)
                    return "You do not currently own the active stream slot.\n";

                _currentState.ExpiryUtc     = DateTime.MinValue;
                _currentState.ActiveIp      = "";
                _currentState.JamulusServer = "";
                SaveState();
                File.WriteAllText(TargetFile, "");
            }

            _ = DisconnectGojamAsync();
            return "Your stream has been stopped and the slot is now free.\n";
        }

        private static bool ServerHasActivity(string ipport)
        {
            var colonIdx = ipport.LastIndexOf(':');
            if (colonIdx < 0) return false;
            string ip = ipport.Substring(0, colonIdx);

            foreach (var json in JamulusCacheManager.LastReportedList.Values)
            {
                try
                {
                    var servers = JsonSerializer.Deserialize<List<JamulusServers>>(json);
                    if (servers == null) continue;
                    var match = servers.FirstOrDefault(s => s.ip == ip);
                    if (match?.clients == null) continue;
                    if (match.clients.Any(c => c.name != null && c.name != "" && !c.name.Contains("obby")))
                        return true;
                }
                catch { }
            }
            return false;
        }

        public static async Task PostLeaseMonitorAsync()
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(20));

                RestoreFromWeeklyIfNeeded();

                string orphanedServer;
                lock (_gateLock)
                {
                    if (_currentState.JamulusServer == "" || DateTime.UtcNow < _currentState.ExpiryUtc)
                        continue;
                    orphanedServer = _currentState.JamulusServer;
                }

                bool hasActivity = ServerHasActivity(orphanedServer);
                bool shouldDisconnect = !hasActivity || Random.Shared.Next(2) == 0;
                Console.WriteLine($"[StreamGate] Post-lease check: server={orphanedServer} activity={hasActivity} disconnect={shouldDisconnect}");

                if (shouldDisconnect)
                {
                    lock (_gateLock)
                    {
                        if (_currentState.JamulusServer == orphanedServer)
                        {
                            _currentState.JamulusServer = "";
                            SaveState();
                        }
                    }
                    _ = DisconnectGojamAsync();
                }
            }
        }

        private static async Task DisconnectGojamAsync()
        {
            // Point gojam at an unreachable address so it stops trying to connect.
            string envContent    = "JAMULUS_SERVER=127.0.0.1:0\n";
            string configContent = "{\"title\":\"Studio D\"}";
            await RunSshAsync($"printf '%s' {ShellEscape(envContent)} > {RemoteEnvFile} && printf '%s' {ShellEscape(configContent)} > {RemoteConfigFile} && docker compose -f {RemoteCompose} up -d gojam");
        }

        private static async Task DisconnectThenConnectAsync(string newServer)
        {
            await DisconnectGojamAsync();
            await ConnectGojamAsync(newServer);
        }

        private static string LookupServerName(string jamulusServer)
        {
            var colonIdx = jamulusServer.LastIndexOf(':');
            if (colonIdx < 0) return "Studio D";
            string ip = jamulusServer.Substring(0, colonIdx);

            foreach (var json in JamulusCacheManager.LastReportedList.Values)
            {
                try
                {
                    var servers = JsonSerializer.Deserialize<List<JamulusServers>>(json);
                    if (servers == null) continue;
                    var match = servers.FirstOrDefault(s => s.ip == ip);
                    if (match?.name != null && match.name != "") return match.name;
                }
                catch { }
            }
            return "Studio D";
        }

        private static async Task RunSshAsync(string remoteCommand)
        {
            try
            {
                var psi = new ProcessStartInfo("ssh")
                {
                    ArgumentList           = { "-o", "BatchMode=yes", "-o", "ConnectTimeout=15", RemoteHost, remoteCommand },
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                };
                using var proc = Process.Start(psi)!;
                string stdout = await proc.StandardOutput.ReadToEndAsync();
                string stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();
                Console.WriteLine($"[StreamGate] SSH exit={proc.ExitCode} stdout={stdout.Trim()} stderr={stderr.Trim()}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StreamGate] SSH failed: {ex.Message}");
            }
        }

        // Wrap a string in single quotes safe for a POSIX shell argument.
        private static string ShellEscape(string value)
            => "'" + value.Replace("'", "'\\''") + "'";
    }
}
