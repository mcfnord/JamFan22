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
        // Lounge moved 2026-08-31: DO droplet -> Turin (Oracle Linux, aarch64). `opc@`, not
        // `root@` — Turin has no root login, and an address-only edit fails at runtime.
        private const string RemoteHost    = "opc@92.4.218.204";
        private const string RemoteEnvFile = "/opt/jamulus-lounge/.env";
        private const string RemoteCompose    = "/opt/jamulus-lounge/docker-compose.yml";
        private const string RemoteConfigFile = "/opt/jamulus-lounge/server/config.json";

        class GateState
        {
            public string ActiveIp      { get; set; } = "";
            public string JamulusServer { get; set; } = "";
            public DateTime ExpiryUtc   { get; set; } = DateTime.MinValue;
            public DateTime GrantedUtc  { get; set; } = DateTime.MinValue;
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

        /// Returns minutes until the next scheduled lobby stream for the given server starts,
        /// or null if one is already active or none is upcoming within horizonHours.
        public static int? MinutesUntilNextScheduledStream(string jamulusServer, int horizonHours = 3)
        {
            var now = DateTime.UtcNow;
            foreach (var res in _reservations)
            {
                if (res.JamulusServer != jamulusServer) continue;
                if (IsWeeklyWindowActive(res, now)) return null;
                var nextStart = MostRecentOccurrence(res.DayOfWeek, res.StartHour, now).AddDays(7);
                int minsUntil = (int)Math.Ceiling((nextStart - now).TotalMinutes);
                if (minsUntil <= horizonHours * 60) return minsUntil;
            }
            return null;
        }

        // Returns the most recent past (or current) UTC DateTime at (dow, startHour:00).
        private static DateTime MostRecentOccurrence(DayOfWeek dow, int startHour, DateTime utcNow)
        {
            var candidate = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, startHour, 0, 0, DateTimeKind.Utc);
            int daysBack  = ((int)utcNow.DayOfWeek - (int)dow + 7) % 7;
            candidate     = candidate.AddDays(-daysBack);
            if (candidate > utcNow) candidate = candidate.AddDays(-7);
            return candidate;
        }

        // Returns true if the current lease holder's server has no non-lobby clients connected.
        private static async Task<bool> CurrentServerIsEmptyAsync()
        {
            string server = _currentState.JamulusServer;
            if (string.IsNullOrEmpty(server)) return true;
            string ip = server.Split(':')[0];
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                using var cts = new System.Threading.CancellationTokenSource(5000);
                await tcp.ConnectAsync(ip, 9999, cts.Token);
                using var stream = tcp.GetStream();
                using var writer = new System.IO.StreamWriter(stream, leaveOpen: true) { AutoFlush = true };
                using var reader = new System.IO.StreamReader(stream, leaveOpen: true);
                string secret = (await System.IO.File.ReadAllTextAsync("/secret.txt")).Trim();
                await writer.WriteLineAsync($"{{\"id\":1,\"jsonrpc\":\"2.0\",\"method\":\"jamulus/apiAuth\",\"params\":{{\"secret\":\"{secret}\"}}}}");
                await reader.ReadLineAsync();
                await writer.WriteLineAsync("{\"id\":2,\"jsonrpc\":\"2.0\",\"method\":\"jamulusserver/getClients\",\"params\":{}}");
                string? response = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(response)) return true;
                using var doc = System.Text.Json.JsonDocument.Parse(response);
                if (!doc.RootElement.TryGetProperty("result", out var result)) return true;
                int realClients = result.EnumerateArray()
                    .Count(c => !c.GetProperty("name").GetString()?.Contains("lobby", StringComparison.OrdinalIgnoreCase) ?? true);
                return realClients == 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StreamGate] Lease-break RPC failed for {ip}: {ex.Message}");
                return false;
            }
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
                    var match = servers.FirstOrDefault(s => s.ip == bareIp && s.port == 22224);
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

        public static bool IsEligibleServer(string bareIp)
        {
            var fleetIps = File.ReadLines("data/fleet-server-ips.txt")
                .Where(l => !l.StartsWith('#') && l.Contains(':'))
                .Select(l => l.Split(':')[0].Trim());
            return fleetIps.Contains(bareIp);
        }

        public static async Task<string> TryRequestStream(string requestIp, bool isWeekly, int knownPort = 0)
        {
            bool   allocated = false;
            string message;
            string previousServer = "";
            string bareIp        = BareIp(requestIp);
            int    port          = knownPort > 0 ? knownPort : FindPortInDirectory(bareIp);
            string jamulusServer = $"{bareIp}:{port}";

            // Only check history if the slot is free — if occupied, let them see the "in use" message
            // so a prohibited server thinks they just need to wait.
            bool slotFree;
            lock (_gateLock) { slotFree = DateTime.UtcNow >= _currentState.ExpiryUtc; }
            if (slotFree)
            {
                var (uniqueGuids, activeDays) = ReadServerHistory(bareIp);
                if (uniqueGuids < 16 || activeDays < 3)
                {
                    Console.WriteLine($"[StreamGate] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} Prohibited: {bareIp} (guids={uniqueGuids} days={activeDays})");
                    return "Prohibited.\n";
                }
            }

            // Check if the current lease can be broken: at least 1 hour elapsed and nobody with the lobby.
            bool leaseBreakable = false;
            lock (_gateLock)
            {
                if (DateTime.UtcNow < _currentState.ExpiryUtc &&
                    _currentState.ActiveIp != requestIp &&
                    (DateTime.UtcNow - _currentState.GrantedUtc).TotalHours >= 1.0)
                {
                    leaseBreakable = true;
                }
            }
            if (leaseBreakable)
                leaseBreakable = await CurrentServerIsEmptyAsync();

            lock (_gateLock)
            {
                if (DateTime.UtcNow < _currentState.ExpiryUtc)
                {
                    int minsLeft = (int)Math.Ceiling((_currentState.ExpiryUtc - DateTime.UtcNow).TotalMinutes);
                    if (_currentState.ActiveIp == requestIp)
                    {
                        Console.WriteLine($"[StreamGate] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} Already streaming: {bareIp} ({minsLeft} min left)");
                        if (isWeekly)
                        {
                            SaveWeeklyReservation(requestIp, jamulusServer);
                            return $"Already streaming. {minsLeft} minutes remaining. Repeats weekly.\n";
                        }
                        return $"Already streaming. {minsLeft} minutes remaining.\n";
                    }

                    if (!leaseBreakable)
                    {
                        Console.WriteLine($"[StreamGate] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} In use: {bareIp} denied ({minsLeft} min left, active={_currentState.ActiveIp})");
                        return $"In use by another server for {minsLeft} more minutes.\n";
                    }

                    Console.WriteLine($"[StreamGate] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} Lease broken: {BareIp(_currentState.ActiveIp)} had {minsLeft} min left, lobby empty — transferring to {bareIp}");
                }

                previousServer              = _currentState.JamulusServer;
                _currentState.ActiveIp      = requestIp;
                _currentState.JamulusServer = jamulusServer;
                _currentState.GrantedUtc    = DateTime.UtcNow;

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
                Console.WriteLine($"[StreamGate] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} Granted: {jamulusServer} for {minsAllocated} min{capNote}{(isWeekly ? " weekly" : "")}");
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
            Console.WriteLine($"[StreamGate] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} Weekly reservation saved for {ip}: every {now.DayOfWeek} {now.Hour:00}:00 UTC");
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

        public static async Task PostLeaseMonitorAsync()
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(20));
                RestoreFromWeeklyIfNeeded();
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
            int serverPort = int.TryParse(jamulusServer.Substring(colonIdx + 1), out var p) ? p : 22224;

            foreach (var json in JamulusCacheManager.LastReportedList.Values)
            {
                try
                {
                    var servers = JsonSerializer.Deserialize<List<JamulusServers>>(json);
                    if (servers == null) continue;
                    var match = servers.FirstOrDefault(s => s.ip == ip && s.port == serverPort);
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


