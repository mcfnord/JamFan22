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
        private const string RemoteCompose = "/opt/jamulus-lounge/docker-compose.yml";

        class GateState
        {
            public string ActiveIp      { get; set; } = "";
            public string JamulusServer { get; set; } = "";
            public DateTime ExpiryUtc   { get; set; } = DateTime.MinValue;
        }

        private static GateState _currentState = new();
        private static readonly object _gateLock = new object();
        private const string StateFile  = "data/stream-gate.json";
        private const string TargetFile = "wwwroot/stream-target.txt";

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
        }

        private static void SaveState()
        {
            var json = JsonSerializer.Serialize(_currentState, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StateFile, json);
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

        public static string TryRequestStream(string requestIp, bool isWeekly)
        {
            bool allocated = false;
            string message;
            string bareIp      = BareIp(requestIp);
            int    port        = FindPortInDirectory(bareIp);
            string jamulusServer = $"{bareIp}:{port}";

            lock (_gateLock)
            {
                if (DateTime.UtcNow < _currentState.ExpiryUtc)
                {
                    if (_currentState.ActiveIp == requestIp)
                        return $"You are already streaming. Your slot expires at {_currentState.ExpiryUtc.ToLocalTime():HH:mm} local time.\n";

                    return $"Sorry, the stream is currently in use by another server until {_currentState.ExpiryUtc.ToLocalTime():HH:mm} local time. Please try again later.\n";
                }

                _currentState.ActiveIp      = requestIp;
                _currentState.JamulusServer = jamulusServer;
                _currentState.ExpiryUtc     = DateTime.UtcNow.AddHours(4);
                SaveState();
                File.WriteAllText(TargetFile, jamulusServer);
                allocated = true;
                message = $"Stream allocated! Stream is yours until {_currentState.ExpiryUtc.ToLocalTime():HH:mm} local time.\n";
            }

            if (allocated)
                _ = ConnectGojamAsync(jamulusServer);

            return message;
        }

        private static async Task ConnectGojamAsync(string jamulusServer)
        {
            string envContent = $"JAMULUS_SERVER={jamulusServer}\n";
            await RunSshAsync($"printf '%s' {ShellEscape(envContent)} > {RemoteEnvFile} && docker compose -f {RemoteCompose} up -d gojam");
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

        private static async Task DisconnectGojamAsync()
        {
            // Point gojam at an unreachable address so it stops trying to connect.
            string envContent = "JAMULUS_SERVER=127.0.0.1:0\n";
            await RunSshAsync($"printf '%s' {ShellEscape(envContent)} > {RemoteEnvFile} && docker compose -f {RemoteCompose} up -d gojam");
        }

        private static async Task RunSshAsync(string remoteCommand)
        {
            try
            {
                var psi = new ProcessStartInfo("ssh")
                {
                    ArgumentList        = { "-o", "BatchMode=yes", "-o", "ConnectTimeout=15", RemoteHost, remoteCommand },
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
