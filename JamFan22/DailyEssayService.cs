using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json.Linq;

public static class DailyEssayService
{
    private sealed record CenterInfo(double Lat, double Lon, string[] Nations, string Model, string Label);

    private static readonly Dictionary<string, CenterInfo> s_centers = new()
    {
        ["EU-W"] = new(51.5,   6.5, new[]{"DE","NL","FR","BE","SE","NO","TR","BG","AT","PL","CZ","DK","FI","HU","RO","CH","SK","RS","HR","GR","UA","LT","LV","EE"}, "gemini-2.5-pro",   "Western Europe"),
        ["IT"]   = new(44.0,  11.0, new[]{"IT"},                                                                                                                         "gemini-2.5-pro",  "Italy"),
        ["UK"]   = new(51.5,  -1.5, new[]{"GB","IE"},                                                                                                                    "gemini-2.5-pro",  "United Kingdom"),
        ["NA-E"] = new(42.0, -80.0, new[]{"US","CA","MX"},                                                                                                              "gemini-2.5-pro",   "Eastern North America"),
        ["NA-W"] = new(47.5,-122.0, new[]{"US","CA"},                                                                                                                    "gemini-2.5-pro",  "Western North America"),
        ["SA"]   = new(-15.0,-60.0, new[]{"BR","AR","CL","CO","PE","VE","EC","BO","PY","UY","GT","CU","DO","HN","SV","NI","CR","PA","PR","GY","SR"},                    "gemini-2.5-pro",  "South America"),
        ["SEA"]  = new(14.0, 108.0, new[]{"TH","PH","CN","SG","JP","KR","MY","ID","VN","HK","TW","AU","NZ"},                                                            "gemini-2.5-pro",  "Southeast Asia & Pacific"),
        ["WORLD"]= new(0.0,    0.0, Array.Empty<string>(),                                                                                                                "gemini-2.5-pro",  "the World"),
    };

    private static readonly Dictionary<string, string> s_lang = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DE"]="German",  ["AT"]="German",
        ["FR"]="French",
        ["IT"]="Italian",
        ["ES"]="Spanish", ["MX"]="Spanish",
        ["AR"]="Spanish", ["CL"]="Spanish", ["CO"]="Spanish", ["PE"]="Spanish",
        ["VE"]="Spanish", ["EC"]="Spanish", ["BO"]="Spanish", ["PY"]="Spanish",
        ["UY"]="Spanish", ["GT"]="Spanish", ["CU"]="Spanish", ["DO"]="Spanish",
        ["HN"]="Spanish", ["SV"]="Spanish", ["NI"]="Spanish", ["CR"]="Spanish",
        ["PA"]="Spanish", ["PR"]="Spanish",
        ["BR"]="Portuguese", ["PT"]="Portuguese",
        ["NL"]="Dutch",   ["PL"]="Polish",
        ["SE"]="Swedish", ["NO"]="Norwegian", ["DK"]="Danish", ["FI"]="Finnish",
        ["TH"]="Thai",
        ["CN"]="Chinese", ["TW"]="Chinese",   ["HK"]="Chinese",
        ["JP"]="Japanese",["KR"]="Korean",
        ["RU"]="Russian", ["TR"]="Turkish",   ["ID"]="Indonesian",
    };

    private static readonly ConcurrentDictionary<string, (string Html, DateTime At)> s_cache = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_locks = new();
    private static readonly ConcurrentDictionary<string, int> s_hitCount = new();
    private static readonly TimeSpan s_ttl = TimeSpan.FromHours(8);
    private static readonly HttpClient s_http = new();
    private static string? s_apiKey;

    private const string KeyPath    = "data/gemini-key.txt";
    private const string PromptPath = "data/essay-system-prompt.txt";
    private const string LogPath    = "data/essay-llm.log";
    private const string ModelBase  = "https://generativelanguage.googleapis.com/v1beta/models/";
    private static readonly DateTime s_epoch = new(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string BluesRockBotKey = "77.163.83.31:22124";

    public static void LoadApiKey()
    {
        try   { s_apiKey = File.ReadAllText(KeyPath).Trim(); Console.WriteLine("[ESSAY] key loaded."); }
        catch (Exception ex) { Console.WriteLine($"[ESSAY] key missing: {ex.Message}"); }
    }

    // ── Public entry point ─────────────────────────────────────────────────────

    public static async Task<string?> GetEssayHtmlAsync(string clientIp)
    {
        if (s_apiKey == null) return null;
        if (!JamFan22.Services.IpAnalyticsService.WarmupComplete) return null;
        clientIp = clientIp.Replace("::ffff:", "");

        var geo = await JamFan22.Services.IpAnalyticsService.FetchIpApiAsync(clientIp);
        double lat = (double?)geo?["lat"] ?? 0;
        double lon = (double?)geo?["lon"] ?? 0;
        string cc  = geo?["countryCode"]?.ToString() ?? "";

        string centerId = NearestCenter(lat, lon);
        string language = s_lang.TryGetValue(cc, out var l) ? l : "English";
        string cacheKey = $"{centerId}:{language}";

        if (s_cache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.At < s_ttl)
        {
            s_hitCount.AddOrUpdate(cacheKey, 1, (_, n) => n + 1);
            return hit.Html;
        }

        var sem = s_locks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        if (!await sem.WaitAsync(TimeSpan.FromSeconds(70)))
        {
            Console.WriteLine($"[ESSAY] sem timeout {cacheKey}");
            return null;
        }
        try
        {
            if (s_cache.TryGetValue(cacheKey, out hit) && DateTime.UtcNow - hit.At < s_ttl)
            {
                s_hitCount.AddOrUpdate(cacheKey, 1, (_, n) => n + 1);
                return hit.Html;
            }

            var prevReaders = s_hitCount.TryGetValue(cacheKey, out var hr) ? hr : 0;
            s_hitCount[cacheKey] = 0;
            Console.WriteLine($"[ESSAY] generating {cacheKey} prev_readers={prevReaders}");
            var html = await GenerateAsync(centerId, language);
            if (html != null) s_cache[cacheKey] = (html, DateTime.UtcNow);
            Console.WriteLine($"[ESSAY] {(html != null ? $"done {html.Length}ch" : "null")} {cacheKey}");
            return html;
        }
        finally { sem.Release(); }
    }

    // Returns the first cached-essay paragraph that names this player, or null.
    public static List<(string Snippet, string Reason)> GetRelevantEssayContext(
        string playerName, string serverName,
        IEnumerable<string> memberNames, string countryName, string instrument)
    {
        string normServer = System.Text.RegularExpressions.Regex
            .Replace(serverName, @"^[^\p{L}\p{N}]+", "").Trim().ToLowerInvariant();
        var members = memberNames.Where(n => n.Length >= 3).ToList();
        bool wantPlayer = playerName.Length >= 3;
        string normPlayer = wantPlayer ? playerName.ToLowerInvariant() : "";
        var results = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, entry) in s_cache)
        {
            if (DateTime.UtcNow - entry.At >= s_ttl) continue;
            var paras = System.Text.RegularExpressions.Regex
                .Split(entry.Html, @"</?p[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                .Select(p => System.Text.RegularExpressions.Regex.Replace(p, "<[^>]+>", "").Trim())
                .Where(p => p.Length > 10);
            foreach (var para in paras)
            {
                if (results.Count >= 2) break;
                string normPara = para.ToLowerInvariant();
                string? reason = null;
                if (wantPlayer && normPara.Contains(normPlayer))
                    reason = "you were mentioned";
                else if (normServer.Length >= 3 && normPara.Contains(normServer))
                    reason = "this server was featured";
                else
                {
                    var hit = members.FirstOrDefault(m => normPara.Contains(m.ToLowerInvariant()));
                    if (hit != null) reason = $"your bandmate {hit} was mentioned";
                }
                if (reason == null) continue;
                string snippet = para.Length > 280 ? para[..280] + "…" : para;
                if (seen.Add(snippet))
                    results.Add((snippet, reason));
            }
            if (results.Count >= 2) break;
        }
        return results;
    }

    // ── Center selection ───────────────────────────────────────────────────────

    private static string NearestCenter(double lat, double lon)
    {
        if (lat == 0 && lon == 0) return "WORLD";
        string best = "WORLD";
        double bestD = double.MaxValue;
        foreach (var (id, c) in s_centers)
        {
            if (id == "WORLD") continue;
            double d = (lat - c.Lat) * (lat - c.Lat) + (lon - c.Lon) * (lon - c.Lon);
            if (d < bestD) { bestD = d; best = id; }
        }
        return best;
    }

    // ── Data assembly ──────────────────────────────────────────────────────────

    private class SessionEntry
    {
        public string Key = "", Name = "", City = "", Nation = "", NationDisplay = "";
        public Dictionary<string, (int T, long F, long L)> GuidTicks = new(StringComparer.Ordinal);
        public int TotalTicks;
        public long FirstMin, LastMin;
        public List<(string Name, string Instr, int Ticks)> Players = new();
    }

    private static async Task<string?> GenerateAsync(string centerId, string language)
    {
        var center = s_centers[centerId];
        long nowMin = JamFan22.Services.JamulusCacheManager.MinutesSince2023AsInt();
        long cutoff = nowMin - 1440;

        var geoTask    = LoadGeoMapAsync();
        var metaTask   = LoadServerMetaAsync();
        var censusTask = ScanCensusAsync(cutoff);
        var songsTask  = LoadSongsAsync(cutoff);
        var ttTask     = LoadTimeTogetherAsync();
        var loreTask   = LoadServerLoreAsync();
        var jmTask     = LoadJammerMapIdsAsync();
        await Task.WhenAll(geoTask, metaTask, censusTask, songsTask, ttTask, loreTask, jmTask);

        var geoMap   = geoTask.Result;
        var meta     = metaTask.Result;
        var sessions = censusTask.Result;
        var songs    = songsTask.Result;
        var ttMap    = ttTask.Result;
        var lore     = loreTask.Result;

        var serverIpNations = await GeolocateServerIpsAsync(sessions);

        foreach (var sess in sessions)
        {
            if (meta.TryGetValue(sess.Key, out var m)) { sess.Name = m.N; sess.City = m.C; sess.Nation = m.Na; }
            var serverIp = sess.Key.Split(':')[0];
            if (serverIpNations.TryGetValue(serverIp, out var geoInfo) && !string.IsNullOrEmpty(geoInfo.Code))
            {
                sess.Nation = geoInfo.Code;
                sess.NationDisplay = geoInfo.FullName;
            }
            sess.TotalTicks = sess.GuidTicks.Values.Sum(v => v.T);
            sess.FirstMin   = sess.GuidTicks.Values.Min(v => v.F);
            sess.LastMin    = sess.GuidTicks.Values.Max(v => v.L);
            sess.Players    = sess.GuidTicks
                .Select(kv => { geoMap.TryGetValue(kv.Key, out var g); return (Name: g.N ?? "", Instr: g.I ?? "", Ticks: kv.Value.T); })
                .Where(p => !string.IsNullOrEmpty(p.Name) && !p.Name.StartsWith("lobby", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Ticks).Take(6).ToList();
        }

        bool IsValidSession(SessionEntry s) =>
            !s.Name.Contains("lobby", StringComparison.OrdinalIgnoreCase);

        var nationSet = center.Nations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var local  = sessions.Where(s => IsValidSession(s) && (nationSet.Contains(s.Nation) || centerId == "WORLD"))
                              .OrderByDescending(s => s.TotalTicks).Take(10).ToList();
        var global = sessions.Where(IsValidSession).OrderByDescending(s => s.TotalTicks).Take(5).ToList();

        var allSessions = local.Concat(global).ToList();
        var pairs = FindNotablePairs(allSessions, geoMap, ttMap);
        var jammerLinks = new List<(string Label, string Url)>(); // disabled — see TODO
        var jazzKeys = GetJazzServerKeys();
        string ctx = BuildContext(center.Label, language, local, global, songs, meta, pairs, lore, jammerLinks, jazzKeys);
        var html = await CallLlmAsync(ctx, language, center.Model);
        if (html != null)
        {
            html = StripUnauthorizedLinks(html, jammerLinks);
            var genTs = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            html += $"\n<p class=\"essay-footer\" data-essay-generated=\"{genTs}\" style=\"font-size:0.8em;color:#aaa;margin-top:1.5em;text-align:right\">Written <time class=\"essay-age\"></time></p>";
        }
        return html;
    }

    // timeTogether.json: [{Key: guid1+guid2 (64 chars), Value: "d.hh:mm:ss"}]
    private static async Task<Dictionary<string, double>> LoadTimeTogetherAsync()
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        try
        {
            var json = await File.ReadAllTextAsync("timeTogether.json");
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var key = item.GetProperty("Key").GetString();
                var val = item.GetProperty("Value").GetString();
                if (key?.Length == 64 && TimeSpan.TryParse(val, out var ts))
                {
                    double h = ts.TotalHours;
                    if (h >= 10 && h <= 500) map[key] = h;
                }
            }
        }
        catch { }
        return map;
    }

    private static async Task<HashSet<string>> LoadJammerMapIdsAsync()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var json = await File.ReadAllTextAsync("wwwroot/jammer-map.json");
            using var doc = JsonDocument.Parse(json);
            foreach (var elem in doc.RootElement.EnumerateArray())
                if (elem.TryGetProperty("id", out var id) && id.GetString() is { Length: 32 } g)
                    ids.Add(g);
        }
        catch { }
        return ids;
    }

    private static HashSet<string> GetJazzServerKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (JamFan22.Services.JamulusCacheManager.LastReportedList.TryGetValue("Genre Jazz", out var json))
            {
                var servers = JsonSerializer.Deserialize<List<JamFan22.Models.JamulusServers>>(json);
                if (servers != null)
                    foreach (var s in servers)
                        if (!string.IsNullOrEmpty(s.ip)) keys.Add($"{s.ip}:{s.port}");
            }
        }
        catch { }
        return keys;
    }

    private static List<(string Label, string Url)> BuildJammerMapLinks(
        List<SessionEntry> sessions,
        Dictionary<string, (string N, string I)> geoMap,
        Dictionary<string, double> ttMap,
        HashSet<string> jammerMapIds)
    {
        double bestScore = 0;
        string? bestLabel = null, bestUrl = null;

        foreach (var sess in sessions)
        {
            var eligible = sess.GuidTicks.Keys
                .Where(g => jammerMapIds.Contains(g) &&
                            geoMap.TryGetValue(g, out var gv) &&
                            !string.IsNullOrEmpty(gv.N) &&
                            !gv.N.StartsWith("lobby", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(g => sess.GuidTicks.TryGetValue(g, out var t) ? t.T : 0)
                .Take(5).ToList();

            if (eligible.Count < 3) continue;

            double totalHours = 0; int pairsFound = 0;
            for (int i = 0; i < eligible.Count; i++)
            for (int j = i + 1; j < eligible.Count; j++)
            {
                var g1 = eligible[i]; var g2 = eligible[j];
                if (ttMap.TryGetValue(g1 + g2, out var h) || ttMap.TryGetValue(g2 + g1, out h))
                { totalHours += h; pairsFound++; }
            }
            if (pairsFound < 2 || totalHours <= bestScore) continue;

            var group = eligible.Take(4).ToList();
            var names = group.Select(g => geoMap[g].N).ToList();
            bestScore   = totalHours;
            bestLabel   = names.Count == 3 ? $"{names[0]}, {names[1]}, and {names[2]}"
                                           : $"{names[0]}, {names[1]}, {names[2]}, and {names[3]}";
            bestUrl = $"https://jamfan.app/jammer-map#guids={string.Join(",", group)}";
        }

        return bestLabel != null ? [(bestLabel, bestUrl!)] : [];
    }

    private static List<(string Name1, string Guid1, string Name2, string Guid2, string Tier)> FindNotablePairs(
        List<SessionEntry> sessions,
        Dictionary<string, (string N, string I)> geoMap,
        Dictionary<string, double> ttMap)
    {
        var result = new List<(string, string, string, string, string)>();
        var seen   = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sess in sessions)
        {
            var guids = sess.GuidTicks.Keys
                .Where(g => geoMap.ContainsKey(g) && !geoMap[g].N.StartsWith("lobby", StringComparison.OrdinalIgnoreCase))
                .ToList();
            for (int i = 0; i < guids.Count; i++)
            for (int j = i + 1; j < guids.Count; j++)
            {
                var g1 = guids[i]; var g2 = guids[j];
                double hours = 0;
                if (!ttMap.TryGetValue(g1 + g2, out hours) && !ttMap.TryGetValue(g2 + g1, out hours)) continue;
                var pairKey = string.CompareOrdinal(g1, g2) < 0 ? g1 + g2 : g2 + g1;
                if (!seen.Add(pairKey)) continue;
                var tier = hours >= 100 ? "long-running duo" : "recurring collaborators";
                result.Add((geoMap[g1].N, g1, geoMap[g2].N, g2, tier));
            }
        }
        return result.Take(6).ToList();
    }

    // censusgeo.csv: hash,name,instrument,city,nation — last entry per guid wins
    private static async Task<Dictionary<string, (string N, string I)>> LoadGeoMapAsync()
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        await foreach (var line in ReadTailAsync("data/censusgeo.csv", 10_000_000))
        {
            var f = line.Split(',');
            if (f.Length < 3 || f[0].Length != 32) continue;
            var name  = WebUtility.UrlDecode(f[1]).Trim();
            var instr = WebUtility.UrlDecode(f[2]).Trim();
            if (!string.IsNullOrWhiteSpace(name) && name != "No Name" && name != "-")
                map[f[0]] = (name, string.IsNullOrWhiteSpace(instr) || instr == "-" ? "musician" : instr);
        }
        return map;
    }

    // server.csv: ip:port,name,city,nation — last entry per key wins
    private static async Task<Dictionary<string, (string N, string C, string Na)>> LoadServerMetaAsync()
    {
        var map = new Dictionary<string, (string, string, string)>(StringComparer.Ordinal);
        await foreach (var line in ReadTailAsync("data/server.csv", 8_000_000))
        {
            var f = line.Split(',');
            if (f.Length < 2 || !f[0].Contains(':')) continue;
            var name = WebUtility.UrlDecode(f[1]).Trim();
            var city = f.Length > 2 ? WebUtility.UrlDecode(f[2]).Trim() : "";
            var nat  = f.Length > 3 ? f[3].Trim() : "";
            if (!string.IsNullOrWhiteSpace(name)) map[f[0]] = (name, city, nat);
        }
        return map;
    }

    // census.csv: minutes,guid,ip:port
    private static async Task<List<SessionEntry>> ScanCensusAsync(long cutoff)
    {
        var byServer = new Dictionary<string, SessionEntry>(StringComparer.Ordinal);
        await foreach (var line in ReadTailAsync("data/census.csv", 30_000_000))
        {
            int i1 = line.IndexOf(','), i2 = i1 > 0 ? line.IndexOf(',', i1 + 1) : -1;
            if (i1 < 0 || i2 < 0) continue;
            if (!long.TryParse(line.AsSpan(0, i1), out long min) || min < cutoff) continue;
            var guid   = line.Substring(i1 + 1, i2 - i1 - 1);
            var i3 = line.IndexOf(',', i2 + 1);
            var server = i3 >= 0 ? line.Substring(i2 + 1, i3 - i2 - 1) : line.Substring(i2 + 1).Trim();
            if (guid.Length != 32) continue;
            if (server == BluesRockBotKey) continue;

            if (!byServer.TryGetValue(server, out var sess))
                byServer[server] = sess = new SessionEntry { Key = server };
            if (sess.GuidTicks.TryGetValue(guid, out var cur))
                sess.GuidTicks[guid] = (cur.T + 1, Math.Min(cur.F, min), Math.Max(cur.L, min));
            else
                sess.GuidTicks[guid] = (1, min, min);
        }
        return byServer.Values.ToList();
    }

    // urls.csv: minutes,source,ip:port,encoded_url,encoded_title,encoded_artist
    private static async Task<List<(string ServerKey, string Title, string Artist)>> LoadSongsAsync(long cutoff)
    {
        var list = new List<(string, string, string)>();
        await foreach (var line in ReadTailAsync("data/urls.csv", 500_000))
        {
            var f = line.Split(',');
            if (f.Length < 5) continue;
            if (!long.TryParse(f[0], out long min) || min < cutoff) continue;
            var title  = WebUtility.UrlDecode(f[4].Trim());
            var artist = f.Length > 5 ? WebUtility.UrlDecode(f[5].Trim()) : "";
            if (!string.IsNullOrWhiteSpace(title)) list.Add((f[2].Trim(), title, artist));
        }
        return list;
    }

    private static async Task<Dictionary<string, JObject>> LoadServerLoreAsync()
    {
        try
        {
            var json = await File.ReadAllTextAsync("data/server-lore.json");
            var root = JObject.Parse(json);
            return root.Properties()
                .ToDictionary(p => p.Name, p => (JObject)p.Value, StringComparer.Ordinal);
        }
        catch { return new Dictionary<string, JObject>(StringComparer.Ordinal); }
    }

    private static async Task<Dictionary<string, (string Code, string FullName)>> GeolocateServerIpsAsync(List<SessionEntry> sessions)
    {
        var ips = sessions.Select(s => s.Key.Split(':')[0]).Distinct().ToList();
        var tasks = ips.Select(async ip => {
            var geo = await JamFan22.Services.IpAnalyticsService.FetchIpApiAsync(ip, waitIfThrottled: true);
            return (ip, code: geo?["countryCode"]?.ToString() ?? "", full: geo?["country"]?.ToString() ?? "");
        }).ToList();
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var t in tasks)
        {
            var (ip, code, full) = await t;
            if (!string.IsNullOrEmpty(code)) result[ip] = (code, full);
        }
        return result;
    }

    // ── Context building ───────────────────────────────────────────────────────

    private static void AppendLore(StringBuilder sb, string serverKey, Dictionary<string, JObject> lore)
    {
        if (!lore.TryGetValue(serverKey, out var entry)) return;
        if (entry["tagline"]?.ToString() is { Length: > 0 } tagline)
            sb.AppendLine($"  Server identity: {tagline}");
        if (entry["themes"] is JArray themes && themes.Count > 0)
            sb.AppendLine($"  Server themes: {string.Join(", ", themes.Select(t => t.ToString()))}");
        if (entry["events"] is JArray events)
            foreach (var ev in events)
            {
                var name = ev["name"]?.ToString() ?? "";
                var sched = ev["schedule"]?.ToString() ?? "";
                var desc = ev["description"]?.ToString() ?? "";
                var url = ev["listen_url"]?.ToString() ?? "";
                var line = $"  Event — {name}: {sched}; {desc}";
                if (!string.IsNullOrWhiteSpace(url)) line += $"; listen: {url}";
                sb.AppendLine(line);
            }
    }

    private static string BuildContext(string centerLabel, string language,
        List<SessionEntry> local, List<SessionEntry> global,
        List<(string ServerKey, string Title, string Artist)> songs,
        Dictionary<string, (string N, string C, string Na)> meta,
        List<(string Name1, string Guid1, string Name2, string Guid2, string Tier)> pairs,
        Dictionary<string, JObject> lore,
        List<(string Label, string Url)> jammerLinks,
        HashSet<string> jazzKeys)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Language: {language}");
        sb.AppendLine($"Reader's region: {centerLabel}");
        sb.AppendLine($"Current date/time (UTC): {DateTime.UtcNow:dddd, yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        sb.AppendLine("LOCAL SERVERS — active in the reader's region, last 24 hours:");
        if (local.Count == 0)
        {
            sb.AppendLine("  (no activity in this region in the past 24 hours)");
        }
        else
        {
            foreach (var s in local)
            {
                var nationLabel = !string.IsNullOrWhiteSpace(s.NationDisplay) ? s.NationDisplay : s.Nation;
                var where = string.IsNullOrWhiteSpace(s.City) ? nationLabel : $"{s.City}, {nationLabel}";
                var sname = string.IsNullOrWhiteSpace(s.Name) ? s.Key : s.Name;
                sb.AppendLine();
                sb.AppendLine($"{sname} ({where})");
                var tStart = s_epoch.AddMinutes(s.FirstMin).ToString("yyyy-MM-ddTHH:mmZ");
                var tEnd   = s_epoch.AddMinutes(s.LastMin).ToString("yyyy-MM-ddTHH:mmZ");
                sb.AppendLine($"  {s.GuidTicks.Count} players · {tStart}–{tEnd} · ~{ApproxDuration(s.LastMin - s.FirstMin)}");
                if (s.Players.Count > 0)
                    sb.AppendLine("  " + string.Join(", ", s.Players.Select(p => $"{p.Name} ({p.Instr})")));
                AppendLore(sb, s.Key, lore);
                if (jazzKeys.Contains(s.Key))
                    sb.AppendLine("  Genre: Jazz");
            }
        }

        sb.AppendLine();
        sb.AppendLine("WORLDWIDE TOP SESSIONS, same 24 hours:");
        int rank = 1;
        foreach (var s in global)
        {
            var nationLabel2 = !string.IsNullOrWhiteSpace(s.NationDisplay) ? s.NationDisplay : s.Nation;
            var where = string.IsNullOrWhiteSpace(s.City) ? nationLabel2 : $"{s.City}, {nationLabel2}";
            var sname = string.IsNullOrWhiteSpace(s.Name) ? s.Key : s.Name;
            sb.AppendLine($"{rank++}. {sname} ({where}) — {s.GuidTicks.Count} players, ~{ApproxDuration(s.LastMin - s.FirstMin)}");
            if (s.Players.Count > 0)
                sb.AppendLine("   " + string.Join(", ", s.Players.Take(4).Select(p => $"{p.Name} ({p.Instr})")));
            if (lore.TryGetValue(s.Key, out var gLore) && gLore["tagline"]?.ToString() is { Length: > 0 } gtag)
                sb.AppendLine($"   Server identity: {gtag}");
            if (jazzKeys.Contains(s.Key))
                sb.AppendLine("   Genre: Jazz");
        }

        if (songs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("SONG TITLES FROM CHAT LINKS:");
            foreach (var (key, title, artist) in songs.Take(10))
            {
                var sname      = meta.TryGetValue(key, out var m) && !string.IsNullOrWhiteSpace(m.N) ? m.N : key;
                var artistPart = !string.IsNullOrWhiteSpace(artist) ? $" (by {artist})" : "";
                sb.AppendLine($"  \"{title}\"{artistPart} — {sname}");
            }
        }

        if (pairs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("NOTABLE PAIRS (musicians with significant shared history on this network):");
            sb.AppendLine("(Use this to color how you describe them — not to state hours or statistics)");
            foreach (var (n1, _, n2, _, tier) in pairs)
                sb.AppendLine($"  {n1} + {n2} — {tier}");
        }

        if (jammerLinks.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("JAMMER MAP LINKS (optional easter egg — use at most one):");
            sb.AppendLine("You may wrap the group's list of names already in the essay with <a href=\"URL\" target=\"_blank\">name</a>.");
            sb.AppendLine("Only for a group of 3 or more who clearly play together regularly. Never a solo player, never a pair. Omit if nothing earns it.");
            foreach (var (label, url) in jammerLinks)
                sb.AppendLine($"  {label} → {url}");
        }

        return sb.ToString();
    }

    private static string MinToTime(long min) => s_epoch.AddMinutes(min).ToString("HH:mm");

    private static string ApproxDuration(long minutes) => minutes switch
    {
        < 10  => "a few minutes",
        < 25  => "about 20 minutes",
        < 45  => "a half hour or so",
        < 75  => "about an hour",
        < 105 => "an hour and a half",
        < 150 => "a couple of hours",
        < 240 => "a few hours",
        _     => "most of the day",
    };

    private static string StripUnauthorizedLinks(string html, List<(string Label, string Url)> authorized)
    {
        if (authorized.Count == 0) return html;
        var allowed = new HashSet<string>(authorized.Select(x => x.Url), StringComparer.Ordinal);
        return System.Text.RegularExpressions.Regex.Replace(
            html,
            @"<a\s[^>]*href=""([^""]+)""[^>]*>(.*?)</a>",
            m => allowed.Contains(m.Groups[1].Value) ? m.Value : m.Groups[2].Value,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
    }

    // ── LLM call ──────────────────────────────────────────────────────────────

    private static (string model, double temperature) ReadEssayConfig(string defaultModel)
    {
        string model = defaultModel;
        double temp  = 1.4;
        try
        {
            foreach (var raw in File.ReadLines("data/essay-config.txt"))
            {
                var line = raw.Trim();
                if (line.StartsWith('#') || !line.Contains('=')) continue;
                var eq = line.IndexOf('=');
                var k  = line[..eq].Trim();
                var v  = line[(eq + 1)..].Trim();
                switch (k)
                {
                    case "model":       model = v;                              break;
                    case "temperature": double.TryParse(v,
                                           System.Globalization.NumberStyles.Float,
                                           System.Globalization.CultureInfo.InvariantCulture,
                                           out temp);                           break;
                }
            }
        }
        catch { }
        return (model, temp);
    }

    private static async Task<string?> CallLlmAsync(string contextText, string language, string model)
    {
        string sysPrompt;
        try { sysPrompt = await File.ReadAllTextAsync(PromptPath); }
        catch { sysPrompt = "Write a prose essay in HTML <p> tags only about Jamulus music activity in the past 24 hours. Use specific names, server names, and times. No bullet points. No generic phrases about 'community' or 'passion'."; }

        var (cfgModel, cfgTemp) = ReadEssayConfig(model);
        model = cfgModel;
        bool isFlash = model.Contains("flash", StringComparison.OrdinalIgnoreCase);
        object genConfig = isFlash
            ? new { maxOutputTokens = 2000, temperature = cfgTemp, thinkingConfig = new { thinkingBudget = 0 } }
            : new { maxOutputTokens = 8192, temperature = cfgTemp };
        var body = new
        {
            system_instruction = new { parts = new[] { new { text = sysPrompt } } },
            contents = new[] { new { parts = new[] { new { text = contextText } } } },
            generationConfig = genConfig,
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? result = null;
        string err = "";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var json = JsonSerializer.Serialize(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await s_http.PostAsync($"{ModelBase}{model}:generateContent?key={s_apiKey}", content, cts.Token);
            var raw  = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("candidates", out var cands) &&
                    cands.GetArrayLength() > 0 &&
                    cands[0].TryGetProperty("content", out var cont) &&
                    cont.TryGetProperty("parts", out var parts))
                {
                    // Pro thinking mode returns a thinking part (thought:true) before the text part
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("thought", out var thought) && thought.GetBoolean()) continue;
                        if (part.TryGetProperty("text", out var textProp))
                        {
                            result = textProp.GetString()?.Trim();
                            break;
                        }
                    }
                }
                else { err = $"unexpected shape: {raw[..Math.Min(300, raw.Length)]}"; }
                if (result != null && result.StartsWith("```"))
                    result = string.Join('\n', result.Split('\n').Skip(1).TakeWhile(l => l != "```")).Trim();
            }
            else { err = $"HTTP {(int)resp.StatusCode}: {raw[..Math.Min(200, raw.Length)]}"; }
        }
        catch (OperationCanceledException) { err = "timeout>60s"; }
        catch (Exception ex) { err = ex.Message; }

        sw.Stop();
        Console.WriteLine($"[ESSAY-LLM] model={model} lang={language} ms={sw.ElapsedMilliseconds}" + (err.Length > 0 ? $" err={err}" : ""));

        try
        {
            var log = new StringBuilder();
            log.AppendLine($"--- {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC  lang={language}  model={model}  ms={sw.ElapsedMilliseconds} ---");
            log.AppendLine("CONTEXT:"); log.AppendLine(contextText.TrimEnd());
            log.AppendLine("ESSAY:");   log.AppendLine(result ?? $"(null — {err})");
            log.AppendLine();
            await File.AppendAllTextAsync(LogPath, log.ToString());
        }
        catch { }

        return result;
    }

    // ── File tail helper ───────────────────────────────────────────────────────

    private static async IAsyncEnumerable<string> ReadTailAsync(string path, long tailBytes)
    {
        await Task.Yield();
        FileInfo fi;
        try { fi = new FileInfo(path); } catch { yield break; }
        if (!fi.Exists || fi.Length == 0) yield break;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long start = Math.Max(0, fs.Length - tailBytes);
        fs.Seek(start, SeekOrigin.Begin);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        if (start > 0) await sr.ReadLineAsync(); // skip partial first line
        string? line;
        while ((line = await sr.ReadLineAsync()) != null)
            if (line.Length > 0) yield return line;
    }
}
