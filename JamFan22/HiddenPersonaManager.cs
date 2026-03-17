using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace JamFan22
{
    public static class HiddenPersonaManager
    {
        private static ConcurrentDictionary<string, DateTime> _hiddenGuids = new ConcurrentDictionary<string, DateTime>();

        public static void SetHidden(IEnumerable<string> guids, bool hide)
        {
            if (guids == null) return;
            var expiry = DateTime.UtcNow.AddHours(24);
            foreach (var g in guids)
            {
                if (hide) _hiddenGuids[g] = expiry;
                else _hiddenGuids.TryRemove(g, out _);
            }
        }

        public static bool IsHidden(string guid)
        {
            return GetExpiry(guid) != null;
        }

        // Returns the Utc expiry if hidden, otherwise null
        public static DateTime? GetExpiry(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            if (_hiddenGuids.TryGetValue(guid, out var expiry))
            {
                if (DateTime.UtcNow < expiry) return expiry;
                _hiddenGuids.TryRemove(guid, out _);
            }
            return null;
        }
    }
}
