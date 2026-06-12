using JamFan22.Models;
using JamFan22.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JamFan22
{
    public class SilenceStatus
    {
        public bool Quiet { get; init; }
        public List<int> Levels { get; init; } = new();
        public DateTime UpdatedAt { get; init; }
        public string? Error { get; init; }
    }

    public static class NonFleetSilencePoller
    {
        public static readonly ConcurrentDictionary<string, SilenceStatus> Status = new();

        private static readonly HashSet<string> _fleetIps = new(StringComparer.Ordinal)
        {
            "50.116.25.151", "24.199.127.71", "130.61.155.141",
            "172.239.129.32", "92.4.218.204", "132.226.27.144", "18.170.218.139"
        };

        // label → "anygenre1.jamulus.io:22124" extracted from the cache URL
        private static readonly Dictionary<string, string> _directoryHosts =
            JamulusCacheManager.JamulusListURLs.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Split('/')[4]);

        private static readonly ConcurrentDictionary<string, DateTime> _lastPolled = new();
        private static readonly ConcurrentDictionary<string, int> _pollInterval = new(); // seconds; doubles while quiet, resets on audio

        private static readonly SemaphoreSlim _concurrencyGate = new(5, 5);

        // M2: per-player audio levels keyed ipPort → playerName → audioLevel (0=silent, >0=audible)
        public static readonly ConcurrentDictionary<string, Dictionary<string, int>> ClientLevels = new();

        private static readonly byte[] s_clmReqFrame = BuildUdpFrame(0x04, 0x04);         // 1028
        private static readonly byte[] s_clmReqClientsFrame = BuildUdpFrame(0xF6, 0x03);  // 1014

        // Kill switch: touch this file to stop gjprobe probing (assumes all non-fleet servers audible)
        private static readonly string _killSwitchPath = "silence-poller-disabled";
        private static DateTime _lastDisabledLog = DateTime.MinValue;

        private static byte[] BuildUdpFrame(byte idLo, byte idHi)
        {
            byte[] f = new byte[9];
            f[0]=0x00; f[1]=0x00; f[2]=idLo; f[3]=idHi; f[4]=0x00; f[5]=0x00; f[6]=0x00;
            ushort crc = JamulusCrc(f, 7);
            f[7]=(byte)(crc & 0xFF); f[8]=(byte)(crc >> 8);
            return f;
        }

        private static ushort JamulusCrc(byte[] data, int len)
        {
            uint crc = 0xFFFF;
            for (int i = 0; i < len; i++)
            {
                crc ^= (uint)data[i] << 8;
                for (int b = 0; b < 8; b++)
                    crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021u) & 0xFFFF : (crc << 1) & 0xFFFF;
            }
            return (ushort)(~crc & 0xFFFF);
        }

        private static List<(int channelId, string name)> Parse1013Body(byte[] buf)
        {
            var result = new List<(int, string)>();
            if (buf.Length < 9 || !(buf[2] == 0xF5 && buf[3] == 0x03)) return result;
            int bodyLen = buf[5] | buf[6] << 8;
            int pos = 7, end = Math.Min(7 + bodyLen, buf.Length - 2);
            while (pos + 16 <= end)
            {
                int channelId = buf[pos]; pos += 12;
                if (pos + 2 > end) break;
                int nameLen = buf[pos] | buf[pos + 1] << 8; pos += 2;
                if (pos + nameLen > end) break;
                string name = System.Text.Encoding.UTF8.GetString(buf, pos, nameLen); pos += nameLen;
                if (pos + 2 > end) break;
                int cityLen = buf[pos] | buf[pos + 1] << 8; pos += 2 + cityLen;
                result.Add((channelId, name));
            }
            return result;
        }

        private static async Task<List<(int slot, string name)>?> TryUdp1014Async(string ip, int port)
        {
            try
            {
                using var udp = new UdpClient(); udp.Connect(ip, port);
                using var cts = new CancellationTokenSource(500);
                await udp.SendAsync(s_clmReqClientsFrame, s_clmReqClientsFrame.Length);
                var resp = await udp.ReceiveAsync(cts.Token);
                var result = Parse1013Body(resp.Buffer);
                return result.Count > 0 ? result : null;
            }
            catch { return null; }
        }

        public static async Task PollLoopAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RunOneCycleAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Console.WriteLine($"[NONFLEET-POLL] loop error: {ex.Message}"); }

                try { await Task.Delay(5000, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        private static async Task RunOneCycleAsync(CancellationToken stoppingToken)
        {
            if (File.Exists(_killSwitchPath))
            {
                if ((DateTime.UtcNow - _lastDisabledLog).TotalMinutes > 5)
                {
                    Console.WriteLine("[NONFLEET-POLL] disabled — assuming all servers audible");
                    _lastDisabledLog = DateTime.UtcNow;
                }
                Status.Clear();
                ClientLevels.Clear();
                return;
            }

            var candidates = new List<(string IpPort, string? DirectoryHost)>();
            var now = DateTime.UtcNow;

            foreach (var (label, json) in JamulusCacheManager.LastReportedList)
            {
                if (string.IsNullOrEmpty(json)) continue;
                _directoryHosts.TryGetValue(label, out string? dirHost);

                List<JamulusServers>? servers;
                try { servers = JsonSerializer.Deserialize<List<JamulusServers>>(json); }
                catch { continue; }
                if (servers == null) continue;

                foreach (var sv in servers)
                {
                    if (sv == null || sv.ip == null || _fleetIps.Contains(sv.ip)) continue;
                    if (sv.clients == null || sv.clients.Length == 0) continue;
                    if (sv.maxclients > 0 && sv.clients.Length >= sv.maxclients) continue;
                    string ipport = $"{sv.ip}:{sv.port}";
                    int pollSecs = _pollInterval.GetValueOrDefault(ipport, 10);
                    if (_lastPolled.TryGetValue(ipport, out DateTime last) &&
                        (now - last).TotalSeconds < pollSecs)
                        continue;
                    candidates.Add((ipport, dirHost));
                }
            }

            if (candidates.Count == 0) return;

            var tasks = candidates.Select(async c =>
            {
                await _concurrencyGate.WaitAsync(stoppingToken);
                try
                {
                    _lastPolled[c.IpPort] = DateTime.UtcNow;
                    await ProbeServerAsync(c.IpPort, c.DirectoryHost);
                }
                finally { _concurrencyGate.Release(); }
            });

            await Task.WhenAll(tasks);
        }

        private static async Task ProbeServerAsync(string ipPort, string? directoryHost)
        {
            var sep = ipPort.LastIndexOf(':');
            string probeIp = sep > 0 ? ipPort[..sep] : ipPort;
            int.TryParse(sep > 0 ? ipPort[(sep + 1)..] : "22124", out int probePort);
            string args = $"-server {ipPort} -timeout 6s";
            if (!string.IsNullOrEmpty(directoryHost))
                args += $" -directory {directoryHost}";

            try
            {
                using var cts = new CancellationTokenSource(7000);
                using var proc = new Process();
                proc.StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/local/bin/gjprobe",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                proc.Start();
                string output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token);
                var nameMap = await TryUdp1014Async(probeIp, probePort);

                var doc = JsonSerializer.Deserialize<JsonElement>(output);
                string? error = doc.TryGetProperty("error", out var errEl) ? errEl.GetString() : null;
                if (error != null && error.Contains("full", StringComparison.OrdinalIgnoreCase))
                {
                    _pollInterval[ipPort] = 120;
                    return;
                }
                bool quiet = error == null && doc.GetProperty("quiet").GetBoolean();
                var levels = new List<int>();
                foreach (var lv in doc.GetProperty("levels").EnumerateArray())
                    levels.Add(lv.GetInt32());

                if (nameMap != null)
                {
                    var nameLevel = new Dictionary<string, int>(nameMap.Count);
                    foreach (var (slot, name) in nameMap)
                        nameLevel[name] = slot < levels.Count ? levels[slot] : 0;
                    ClientLevels[ipPort] = nameLevel;
                }

                bool changed = !Status.TryGetValue(ipPort, out var prev) || prev.Quiet != quiet;
                Status[ipPort] = new SilenceStatus { Quiet = quiet, Levels = levels, UpdatedAt = DateTime.UtcNow, Error = error };
                int audibleCount = levels.Count(l => l > 0);
                int maxSecs = audibleCount >= 3 ? 400 : 300;
                int current = _pollInterval.GetValueOrDefault(ipPort, 300);
                int next;
                if (changed)
                    next = quiet ? 60 : 300;  // went quiet → check back sooner; went active → back off
                else if (quiet)
                    next = Math.Max(current / 2, 60);  // confirmed quiet → come down toward 60s
                else
                    next = Math.Min(current * 2, maxSecs);  // confirmed active → back off further
                _pollInterval[ipPort] = next;
                if (changed)
                    Console.WriteLine($"[NONFLEET-POLL] {ipPort}: quiet={quiet} levels=[{string.Join(",", levels)}]{(error != null ? $" err={error}" : "")}");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[NONFLEET-POLL] {ipPort}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
