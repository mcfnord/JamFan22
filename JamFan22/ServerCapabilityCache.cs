using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace JamFan22
{
    public static class ServerCapabilityCache
    {
        private static readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(8) };
        private static readonly ConcurrentDictionary<string, (bool RawAudio, DateTime Expiry)> _cache = new();

        public static bool? GetRawAudio(string ip, int port)
        {
            var key = $"{ip}:{port}";
            if (_cache.TryGetValue(key, out var cached) && DateTime.UtcNow < cached.Expiry)
                return cached.RawAudio;
            _ = FetchAsync(ip, port);
            return null;
        }

        private static async Task FetchAsync(string ip, int port)
        {
            var key = $"{ip}:{port}";
            if (!_cache.TryAdd(key, (false, DateTime.UtcNow.AddSeconds(30))))
                return;
            try
            {
                var json = await _client.GetStringAsync(
                    $"https://explorer.jamulus.io/servers.php?query={ip}:{port}");
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                bool raw = root.ValueKind == JsonValueKind.Array
                    && root.GetArrayLength() > 0
                    && root[0].TryGetProperty("rawaudio", out var ra)
                    && ra.GetBoolean();
                _cache[key] = (raw, DateTime.UtcNow.AddHours(24));
            }
            catch
            {
                _cache[key] = (false, DateTime.UtcNow.AddMinutes(5));
            }
        }
    }
}
