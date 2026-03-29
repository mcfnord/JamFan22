using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace JamFan22
{
    public record StreamRequest(string IpAddress, bool IsWeekly, bool IsActive, DateTime CreatedAt);

    public static class StreamRequestManager
    {
        private static readonly ConcurrentDictionary<string, StreamRequest> _requests = new();
        private static readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);
        private const string FilePath = "data/stream-requests.json";

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var json = File.ReadAllText(FilePath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, StreamRequest>>(json);
                if (dict == null) return;
                foreach (var kvp in dict)
                    _requests[kvp.Key] = kvp.Value;
                Console.WriteLine($"[StreamRequestManager] Loaded {_requests.Count} stream request(s).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StreamRequestManager] Load failed, starting empty: {ex.Message}");
            }
        }

        private static void Save()
        {
            _fileLock.Wait();
            try
            {
                var snapshot = new Dictionary<string, StreamRequest>(_requests);
                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StreamRequestManager] Save failed: {ex.Message}");
            }
            finally
            {
                _fileLock.Release();
            }
        }

        public static void AddOrUpdateStream(string ip, bool isWeekly)
        {
            _requests.AddOrUpdate(
                ip,
                addValueFactory:    _   => new StreamRequest(ip, isWeekly, IsActive: true, DateTime.UtcNow),
                updateValueFactory: (_, existing) => existing with { IsWeekly = isWeekly, IsActive = true });
            Save();
        }

        public static void ResetStream(string ip)
        {
            if (_requests.TryGetValue(ip, out var existing))
            {
                _requests[ip] = existing with { IsActive = false };
                Save();
            }
        }

        public static IReadOnlyDictionary<string, StreamRequest> GetAll() => _requests;
    }
}
