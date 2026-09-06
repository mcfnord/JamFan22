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

    // Representative IANA timezone for each center — used to convert UTC session times
    // to approximate local server time for that region.
    private static readonly Dictionary<string, string> s_centerTz = new()
    {
        ["EU-W"]  = "Europe/Berlin",
        ["IT"]    = "Europe/Rome",
        ["UK"]    = "Europe/London",
        ["NA-E"]  = "America/New_York",
        ["NA-W"]  = "America/Los_Angeles",
        ["SA"]    = "America/Sao_Paulo",
        ["SEA"]   = "Asia/Bangkok",
        ["WORLD"] = "UTC",
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

    private static readonly Dictionary<string, int> s_essayDelay = new()
    {
        ["EU-W"]  = 60,
        ["IT"]    = 120,
        ["UK"]    = 120,
        ["NA-W"]  = 120,
        ["NA-E"]  = 180,
        ["SEA"]   = 240,
        ["SA"]    = 240,
        ["WORLD"] = 240,
    };

    private static readonly ConcurrentDictionary<string, (string Html, DateTime At, HashSet<string> ServerKeys, Dictionary<string, string> MentionGuids)> s_cache = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_locks = new();
    private static readonly ConcurrentDictionary<string, int> s_hitCount = new();
    private static readonly ConcurrentDictionary<string, int> s_reqCount = new();
    private static readonly ConcurrentDictionary<string, DateTime> s_reqFirstAt = new();
    private static readonly ConcurrentDictionary<string, string> s_reqFirstIp = new();
    private static readonly TimeSpan s_ttl = TimeSpan.FromHours(8);
    private static readonly DateTime s_startedAt = DateTime.UtcNow;
    private static int s_onDemandToday = 0;
    private static DateTime s_onDemandResetAt = DateTime.UtcNow.Date.AddDays(1);
    // ESSAY_NO_GRACE=1 (test builds only) skips the 4 h post-start grace so a port-5000
    // instance can generate an essay immediately; production never sets it.
    private static readonly TimeSpan s_startupGrace =
        Environment.GetEnvironmentVariable("ESSAY_NO_GRACE") == "1" ? TimeSpan.Zero : TimeSpan.FromHours(4);
    private static readonly HttpClient s_http = new();
    private static string? s_apiKey;

    private const string KeyPath    = "data/gemini-key.txt";
    private const string PromptPath = "data/essay-system-prompt.txt";
    private const string LogPath    = "data/essay-llm.log";
    private const string YouLogPath = "data/essay-you.log";
    private const string ModelBase  = "https://generativelanguage.googleapis.com/v1beta/models/";
    private static readonly DateTime s_epoch = new(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string BluesRockBotKey = "77.163.83.31:22124";

    // Soft narrative bias: musicians who can actually read the essay (resolved via join-events
    // IP pairing, last 96h of telemetry.log) get a small tiebreaker bump — not a hard override.
    private const int WebUserTickBonus = 60;

    // A host named in server-lore.json is force-included in their session's player list once they
    // have been in the room this long. Presence, not a cameo — see the pri -1 tier below.
    private const int HostMinSpanMinutes = 30;

    public static void LoadApiKey()
    {
        try   { s_apiKey = File.ReadAllText(KeyPath).Trim(); Console.WriteLine("[ESSAY] key loaded."); }
        catch (Exception ex) { Console.WriteLine($"[ESSAY] key missing: {ex.Message}"); }
    }

    // Read-only peek — does not touch s_reqCount/s_hitCount, so it cannot influence the
    // gate or trigger generation. Lets the client skip the pacing delay when an essay for
    // this region:language is already sitting in cache.
    public static async Task<(int DelaySeconds, bool Ready)> GetEssayStatusAsync(string clientIp)
    {
        clientIp = clientIp.Replace("::ffff:", "");
        var geo = await JamFan22.Services.IpAnalyticsService.FetchIpApiAsync(clientIp);
        double lat = (double?)geo?["lat"] ?? 0;
        double lon = (double?)geo?["lon"] ?? 0;
        string cc  = geo?["countryCode"]?.ToString() ?? "";
        string centerId = NearestCenter(lat, lon);
        string language = s_lang.TryGetValue(cc, out var l) ? l : "English";
        string cacheKey = $"{centerId}:{language}";
        int delay = s_essayDelay.TryGetValue(centerId, out var d) ? d : 240;
        bool ready = s_cache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.At < s_ttl;
        return (delay, ready);
    }

    // Region:language cache key for a visitor IP — the same key GetEssayHtmlAsync serves under.
    // Factored out so the endpoint can locate the exact cached essay this visitor received in
    // order to personalize it. ip-api results are cached, so the extra lookup is a cache hit.
    public static async Task<string> ResolveCacheKeyAsync(string clientIp)
    {
        clientIp = clientIp.Replace("::ffff:", "");
        var geo = await JamFan22.Services.IpAnalyticsService.FetchIpApiAsync(clientIp);
        double lat = (double?)geo?["lat"] ?? 0;
        double lon = (double?)geo?["lon"] ?? 0;
        string cc  = geo?["countryCode"]?.ToString() ?? "";
        string language = s_lang.TryGetValue(cc, out var l) ? l : "English";
        return $"{NearestCenter(lat, lon)}:{language}";
    }

    // "You're in this one" — resolves the visitor's IP to a GUID via join-events (authoritative
    // strength ≥16), checks whether that GUID was a featured candidate in the essay they're being
    // served, and confirms the LLM actually wrote the name. Returns a ready-to-prepend banner or
    // null. Silent unless every gate passes — provenance + strength + name-in-prose. Never mutates
    // the shared cache entry; the banner is prepended to the returned copy only.
    public static string? TryGetVisitorBanner(string clientIp, string cacheKey)
    {
        var ip = clientIp.Replace("::ffff:", "");

        // Full decision funnel is logged with a result= token at every exit so the probable
        // success rate is greppable: denominator = all "[ESSAY-YOU] eval" lines; numerator =
        // result=shown. Every non-shown outcome names the stage it died at.
        if (!s_cache.TryGetValue(cacheKey, out var entry) || DateTime.UtcNow - entry.At >= s_ttl)
        {
            Console.WriteLine($"[ESSAY-YOU] eval ip={ip} key={cacheKey} result=no-essay");
            return null;
        }
        int featured = entry.MentionGuids?.Count ?? 0;

        var strengths = JamFan22.IdentityManager.GetGuidStrengths(clientIp);
        if (strengths.Count == 0)
        {
            Console.WriteLine($"[ESSAY-YOU] eval ip={ip} key={cacheKey} guids=0 featured={featured} result=no-guids");
            return null;
        }
        int maxStrength = strengths.Values.Max();
        var qualifying = strengths.Where(kv => kv.Value >= 16).ToList(); // 16 = join-events FLAG_HISTORY floor

        string? bestGuid = null, bestName = null;
        int bestStrength = 15;
        foreach (var kv in qualifying)
        {
            if (kv.Value <= bestStrength) continue;
            if (entry.MentionGuids == null || !entry.MentionGuids.TryGetValue(kv.Key, out var name)) continue;
            bestStrength = kv.Value; bestName = name; bestGuid = kv.Key;
        }
        if (bestName == null || bestGuid == null)
        {
            var reason = qualifying.Count == 0 ? "below-floor" : "no-mention-match";
            Console.WriteLine($"[ESSAY-YOU] eval ip={ip} key={cacheKey} guids={strengths.Count} maxStrength={maxStrength} qualifying={qualifying.Count} featured={featured} result={reason}");
            return null;
        }

        // Confirm the name survived into the LLM's prose — a featured candidate the essay never
        // named would produce a banner pointing at nothing.
        var rx = new System.Text.RegularExpressions.Regex(
            $@"\b{System.Text.RegularExpressions.Regex.Escape(bestName)}\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!rx.IsMatch(entry.Html))
        {
            Console.WriteLine($"[ESSAY-YOU] eval ip={ip} key={cacheKey} guid={bestGuid} name=\"{bestName}\" strength={bestStrength} featured={featured} result=name-not-in-prose");
            return null;
        }

        // "Golden" = the authoritative join-events-only strength (≥16 = residential IP history).
        // If it's below 16, the qualifying strength came from fleet synth, not join-events —
        // a weaker match worth watching. guidIPs>1 means the GUID is shared across IPs.
        int golden  = JamFan22.IdentityManager.GetStrengthForIpGuid(clientIp, bestGuid);
        int guidIPs = JamFan22.IdentityManager.GetIpCountForGuid(bestGuid);
        var flags = (golden >= 16 ? "" : " SYNTH-ONLY") + (guidIPs > 1 ? " MULTI-IP" : "") + (qualifying.Count > 1 ? " MULTI-QUALIFYING" : "");
        Console.WriteLine($"[ESSAY-YOU] eval ip={ip} key={cacheKey} guid={bestGuid} name=\"{bestName}\" strength={bestStrength} golden={golden} guidIPs={guidIPs} qualifying={qualifying.Count} featured={featured} result=shown{flags}");
        try
        {
            File.AppendAllText(YouLogPath,
                $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} ip={ip} key={cacheKey} guid={bestGuid} name=\"{bestName}\" strength={bestStrength} golden={golden} guidIPs={guidIPs} qualifying={qualifying.Count} featured={featured}{flags}\n");
        }
        catch { }

        var safe = WebUtility.HtmlEncode(bestName);
        return $"<div class=\"essay-you-banner\" style=\"border-left:3px solid #e8a840;padding:0.35em 0 0.35em 0.75em;margin-bottom:1em;color:#e8a840;font-size:0.95em\">You're in this one, <span style=\"font-family:'Brush Script MT','Segoe Script',cursive;font-weight:bold;font-size:1.3em\">{safe}</span>!</div>";
    }

    // ── Public entry point ─────────────────────────────────────────────────────

    public static async Task<string?> GetEssayHtmlAsync(string clientIp)
    {
        if (s_apiKey == null) return null;
        if (!JamFan22.Services.IpAnalyticsService.WarmupComplete) return null;
        if (DateTime.UtcNow - s_startedAt < s_startupGrace)
        {
            Console.WriteLine($"[ESSAY] startup grace — skipping generation (uptime {(DateTime.UtcNow - s_startedAt).TotalMinutes:F0}min)");
            return null;
        }
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
            Console.WriteLine($"[ESSAY-REQ] {DateTime.UtcNow:HH:mm:ss} ip={clientIp} key={cacheKey} cache=hit");
            s_hitCount.AddOrUpdate(cacheKey, 1, (_, n) => n + 1);
            return hit.Html;
        }

        int reqCount = s_reqCount.AddOrUpdate(cacheKey, 1, (_, n) => n + 1);
        if (reqCount == 1)
        {
            s_reqFirstAt[cacheKey] = DateTime.UtcNow;
            s_reqFirstIp[cacheKey] = clientIp;
            Console.WriteLine($"[ESSAY-GATE] key={cacheKey} ip={clientIp} reqCount=1 — first request, waiting for 2nd");
            return null;
        }
        if (s_reqFirstIp.TryGetValue(cacheKey, out var firstIp) && firstIp == clientIp)
        {
            Console.WriteLine($"[ESSAY-GATE] key={cacheKey} ip={clientIp} — same IP as first, not triggering");
            return null;
        }
        if (reqCount == 2)
        {
            double waited = s_reqFirstAt.TryGetValue(cacheKey, out var first) ? (DateTime.UtcNow - first).TotalSeconds : -1;
            Console.WriteLine($"[ESSAY-GATE] key={cacheKey} ip={clientIp} reqCount=2 wait={waited:F0}s — 2nd request, generating");
        }

        var sem = s_locks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        if (!await sem.WaitAsync(TimeSpan.FromSeconds(70)))
        {
            Console.WriteLine($"[ESSAY] sem timeout {cacheKey} ip={clientIp}");
            return null;
        }
        try
        {
            if (s_cache.TryGetValue(cacheKey, out hit) && DateTime.UtcNow - hit.At < s_ttl)
            {
                Console.WriteLine($"[ESSAY-REQ] {DateTime.UtcNow:HH:mm:ss} ip={clientIp} key={cacheKey} cache=hit2");
                s_hitCount.AddOrUpdate(cacheKey, 1, (_, n) => n + 1);
                return hit.Html;
            }

            if (DateTime.UtcNow >= s_onDemandResetAt)
            {
                s_onDemandToday = 0;
                s_onDemandResetAt = DateTime.UtcNow.Date.AddDays(1);
            }
            if (s_onDemandToday >= 40)
            {
                Console.WriteLine($"[ESSAY] daily-cap-hit ip={clientIp} key={cacheKey}");
                return null;
            }
            s_onDemandToday++;

            var prevReaders = s_hitCount.TryGetValue(cacheKey, out var hr) ? hr : 0;
            s_hitCount[cacheKey] = 0;
            string? prevSummary = s_cache.TryGetValue(cacheKey, out var expiring)
                ? ExtractSummary(expiring.Html) : null;
            Console.WriteLine($"[ESSAY-REQ] {DateTime.UtcNow:HH:mm:ss} ip={clientIp} key={cacheKey} cache=gen prev_readers={prevReaders} ondemand_today={s_onDemandToday}/40");
            Console.WriteLine($"[ESSAY] generating {cacheKey} prev_readers={prevReaders}" + (prevSummary != null ? " prev=yes" : ""));
            if (prevSummary != null && prevReaders < 5)
                Console.WriteLine($"[ESSAY-WARN] low-yield key={cacheKey} prev_readers={prevReaders} — consider whether this language variant is worth caching separately");
            var (html, serverKeys, mentionGuids) = await GenerateAsync(centerId, language, prevSummary);
            if (html != null)
            {
                s_cache[cacheKey] = (html, DateTime.UtcNow, serverKeys, mentionGuids);
                s_reqCount[cacheKey] = 0;
                s_reqFirstAt.TryRemove(cacheKey, out _);
                s_reqFirstIp.TryRemove(cacheKey, out _);
            }
            Console.WriteLine($"[ESSAY] {(html != null ? $"done {html.Length}ch" : "null")} {cacheKey}");
            return html;
        }
        finally { sem.Release(); }
    }

    // Returns cached-essay paragraphs that name this player, or an empty list.
    // Deliberately self-mention only — surfacing a bandmate's or a server's cross-network
    // activity to an arriving stranger read as surveillance, not delight (see essay-welcome review).
    public static List<(string Snippet, string Reason)> GetRelevantEssayContext(string playerName)
    {
        bool wantPlayer = playerName.Length >= 3;
        if (!wantPlayer) return new();
        var nameRegex = new System.Text.RegularExpressions.Regex(
            $@"\b{System.Text.RegularExpressions.Regex.Escape(playerName)}\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var results = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, entry) in s_cache)
        {
            if (DateTime.UtcNow - entry.At >= s_ttl) continue;
            var paras = System.Text.RegularExpressions.Regex
                .Split(entry.Html, @"</?p[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                .Select(p => System.Text.RegularExpressions.Regex.Replace(
                    System.Text.RegularExpressions.Regex.Replace(p,
                        @"<time\s[^>]*datetime=""([^""]+)""[^>]*>([^<]*)</time>",
                        "$2 [$1]"),
                    "<[^>]+>", "").Trim())
                .Where(p => p.Length > 10);
            foreach (var para in paras)
            {
                if (results.Count >= 2) break;
                if (!nameRegex.IsMatch(para)) continue;
                string snippet = para.Length > 280 ? para[..280] + "…" : para;
                if (seen.Add(snippet))
                    results.Add((snippet, "you were mentioned"));
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
        public Dictionary<string, (int T, long F, long L, int Silent, int Audible)> GuidTicks = new(StringComparer.Ordinal);
        public int TotalTicks;
        public long FirstMin, LastMin;
        public List<(string Name, string Instr, int Ticks)> Players = new();
    }

    private static async Task<(string? Html, HashSet<string> ServerKeys, Dictionary<string, string> MentionGuids)> GenerateAsync(string centerId, string language, string? prevSummary = null)
    {
        CensusIndex.EnsureBuilt();
        var center = s_centers[centerId];
        long nowMin = JamFan22.Services.JamulusCacheManager.MinutesSince2023AsInt();
        long cutoff = nowMin - 1440;

        var geoTask    = LoadGeoMapAsync();
        var metaTask   = LoadServerMetaAsync();
        var censusTask = ScanCensusAsync(cutoff);
        var songsTask  = LoadSongsAsync(cutoff);
        var ttTask     = LoadTimeTogetherAsync();
        var loreTask   = LoadServerLoreAsync();
        var webTask    = GetWebAppGuids96hAsync();
        await Task.WhenAll(geoTask, metaTask, censusTask, songsTask, ttTask, loreTask, webTask);

        var geoMap   = geoTask.Result;
        var meta     = metaTask.Result;
        var sessions = censusTask.Result;
        var songs    = songsTask.Result;
        var ttMap    = ttTask.Result;
        var lore     = loreTask.Result;
        var webGuids = webTask.Result;

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
            // The room's host, when server-lore.json names one. A host who was really in the room
            // outranks the audibility ranking below (pri -1): hosts of scheduled sessions are often
            // singers or MCs, and a voice is audible only while it sings, so their audible fraction
            // sits under IsActivePlayer's 50% bar and they lose their slot to whoever played
            // continuously. Studio D, 2026-09-02: Marsha K held the room 217 min — longer than
            // anyone — and was cut at rank 7 for a guitarist present 10 min.
            string hostName = lore.TryGetValue(sess.Key, out var sessLore)
                            ? (sessLore["host"]?.ToString() ?? "").Trim()
                            : "";

            sess.Players    = sess.GuidTicks
                .Where(kv => kv.Value.L - kv.Value.F >= 5)
                .Select(kv =>
                {
                    geoMap.TryGetValue(kv.Key, out var g);
                    bool isLis    = CensusIndex.IsListener(kv.Key);
                    int totalPolled = kv.Value.Silent + kv.Value.Audible;
                    bool isParked = !isLis && totalPolled >= 5 && kv.Value.Audible * 10 <= kv.Value.Silent;
                    bool isAct    = !isLis && !isParked && CensusIndex.IsActivePlayer(kv.Key);
                    bool isHost   = hostName.Length > 0
                                 && (g.N ?? "").Trim().Equals(hostName, StringComparison.OrdinalIgnoreCase)
                                 && kv.Value.L - kv.Value.F >= HostMinSpanMinutes;
                    // Priority: -1=host, present and past HostMinSpanMinutes; 0=confirmed active, 1=unknown, 2=listener, 3=confirmed silent (parked — connected but no audio)
                    string instr = isLis ? "listener" : isParked ? "parked" : g.I is { Length: > 0 } i ? i : "musician";
                    int pri = isHost ? -1 : isAct ? 0 : isParked ? 3 : isLis ? 2 : 1;
                    int score = kv.Value.T + (webGuids.Contains(kv.Key) ? WebUserTickBonus : 0);
                    return (Name: g.N ?? "", Instr: instr, Ticks: kv.Value.T, Pri: pri, Score: score);
                })
                .Where(p => !string.IsNullOrEmpty(p.Name) && !p.Name.StartsWith("lobby", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Pri).ThenByDescending(p => p.Score).Take(6)
                .Select(p => (p.Name, p.Instr, p.Ticks)).ToList();
        }

        bool IsValidSession(SessionEntry s) =>
            !s.Name.Contains("lobby", StringComparison.OrdinalIgnoreCase);

        int SessionScore(SessionEntry s) =>
            s.TotalTicks + WebUserTickBonus * s.GuidTicks.Keys.Count(webGuids.Contains);

        var nationSet = center.Nations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var local  = sessions.Where(s => IsValidSession(s) && (nationSet.Contains(s.Nation) || centerId == "WORLD"))
                              .OrderByDescending(SessionScore).Take(10).ToList();
        var global = sessions.Where(IsValidSession).OrderByDescending(SessionScore).Take(5).ToList();

        var allSessions = local.Concat(global).ToList();
        var featuredServerKeys = allSessions.Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pairs = FindNotablePairs(allSessions, geoMap, ttMap);

        // Provenance for the "You're in this one" banner: map each featured player's GUID to the
        // name that was actually fed to the LLM as a candidate. Serve-time IP→GUID resolution
        // intersects with this set, so only genuine featured candidates can ever match — a name
        // appearing in prose by coincidence is not enough. Names < 4 chars pruned (false-positive risk).
        var mentionGuids = new Dictionary<string, string>(StringComparer.Ordinal);
        void AddMention(string guid, string name)
        {
            if (guid?.Length == 32 && name is { Length: >= 4 } &&
                !name.StartsWith("lobby", StringComparison.OrdinalIgnoreCase))
                mentionGuids[guid] = name;
        }
        foreach (var s in allSessions)
        {
            var featured = s.Players.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in s.GuidTicks)
                if (geoMap.TryGetValue(kv.Key, out var g) && featured.Contains(g.N))
                    AddMention(kv.Key, g.N);
        }
        foreach (var (n1, g1, n2, g2, _) in pairs) { AddMention(g1, n1); AddMention(g2, n2); }

        var jazzKeys = GetGenreServerKeys("Genre Jazz");
        var rockKeys = GetGenreServerKeys("Genre Rock");
        string ctx = BuildContext(centerId, center.Label, language, local, global, songs, meta, pairs, lore, jazzKeys, rockKeys, prevSummary);
        var html = await CallLlmAsync(ctx, language, center.Model);
        if (html != null)
        {
            html = StripUnauthorizedLinks(html);
            var genTs = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            html += $"\n<p class=\"essay-footer\" data-essay-generated=\"{genTs}\" style=\"font-size:0.8em;color:#aaa;margin-top:1.5em;text-align:right\">Written <time class=\"essay-age\"></time></p>";
        }
        return (html, featuredServerKeys, mentionGuids);
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

    private static HashSet<string> GetGenreServerKeys(string directoryKey)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (JamFan22.Services.JamulusCacheManager.LastReportedList.TryGetValue(directoryKey, out var json))
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

    // GUIDs of musicians who also visited the web app in the last 96h, resolved via
    // join-events.csv IP->GUID pairing (same lookup chat-url-client uses). Used only to
    // nudge essay selection toward people who can actually see the result — see WebUserTickBonus.
    private static HashSet<string> _webGuids96h = new(StringComparer.Ordinal);
    private static DateTime _webGuids96hRefreshedAt = DateTime.MinValue;
    private static readonly SemaphoreSlim _webGuids96hLock = new(1, 1);

    private static async Task<HashSet<string>> GetWebAppGuids96hAsync()
    {
        var now = DateTime.UtcNow;
        if ((now - _webGuids96hRefreshedAt).TotalMinutes < 20.0) return _webGuids96h;
        await _webGuids96hLock.WaitAsync();
        try
        {
            if ((now - _webGuids96hRefreshedAt).TotalMinutes < 20.0) return _webGuids96h;
            long cutoff = JamFan22.Services.JamulusCacheManager.MinutesSince2023AsInt() - 5760;
            var ips = new HashSet<string>();
            try
            {
                foreach (var line in File.ReadLines("data/telemetry.log"))
                {
                    int i1 = line.IndexOf(',');
                    if (i1 < 0 || !long.TryParse(line.AsSpan(0, i1), out long min) || min < cutoff) continue;
                    int i2 = line.IndexOf(',', i1 + 1);
                    var ip = i2 < 0 ? line[(i1 + 1)..] : line[(i1 + 1)..i2];
                    if (!string.IsNullOrEmpty(ip)) ips.Add(ip);
                }
            }
            catch { }

            var guids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ip in ips)
                guids.UnionWith(JamFan22.IdentityManager.GetAllAssociatedGuids(ip));

            _webGuids96h = guids;
            _webGuids96hRefreshedAt = now;
            Console.WriteLine($"[ESSAY] WebAppGuids96h refreshed: {ips.Count} IPs -> {guids.Count} GUIDs");
            return guids;
        }
        finally { _webGuids96hLock.Release(); }
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

            int tickSilent = 0, tickAudible = 0;
            if (i3 >= 0) { var a = line.Substring(i3 + 1).Trim(); if (a == "0") tickSilent = 1; else if (a.Length > 0) tickAudible = 1; }

            if (!byServer.TryGetValue(server, out var sess))
                byServer[server] = sess = new SessionEntry { Key = server };
            if (sess.GuidTicks.TryGetValue(guid, out var cur))
                sess.GuidTicks[guid] = (cur.T + 1, Math.Min(cur.F, min), Math.Max(cur.L, min), cur.Silent + tickSilent, cur.Audible + tickAudible);
            else
                sess.GuidTicks[guid] = (1, min, min, tickSilent, tickAudible);
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

    private static void AppendLore(StringBuilder sb, string serverKey, Dictionary<string, JObject> lore,
        List<(string Name, string Instr, int Ticks)>? players = null)
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
        // Host: a server entry may name its `host` (a display name), and optionally a short list of
        // `essay_facts` about them. Either line reaches the essay ONLY when the host is among the
        // session's named players — the welcome path has its own mode machine in `notes` and
        // is deliberately not reused here. Whole-name, case-insensitive match; never substring.
        // The facts are optional: a host with none still gets named, which is the point of the
        // line. Requiring facts is why Veronica's own room could be written up without her.
        if (players != null
            && entry["host"]?.ToString() is { Length: > 0 } host
            && players.Any(p => p.Name.Trim().Equals(host, StringComparison.OrdinalIgnoreCase)))
        {
            if (entry["essay_facts"] is JArray facts && facts.Count > 0)
                sb.AppendLine($"  Host lore — {host} is in this session's player list. Approved facts about {host}'s band; "
                            + "weave two or three of them into this session's paragraph as plain asides (never all of them, "
                            + "never anything not listed here, no links): "
                            + string.Join("; ", facts.Select(f => f.ToString())));
            else
                sb.AppendLine($"  Host — {host} hosts this room and is in this session's player list. "
                            + $"Name {host} in this session's paragraph. Say nothing else about them.");
        }
    }

    private static string BuildContext(string centerId, string centerLabel, string language,
        List<SessionEntry> local, List<SessionEntry> global,
        List<(string ServerKey, string Title, string Artist)> songs,
        Dictionary<string, (string N, string C, string Na)> meta,
        List<(string Name1, string Guid1, string Name2, string Guid2, string Tier)> pairs,
        Dictionary<string, JObject> lore,
        HashSet<string> jazzKeys,
        HashSet<string> rockKeys,
        string? prevSummary = null)
    {
        var tzId = s_centerTz.TryGetValue(centerId, out var tz) ? tz : "UTC";
        TimeZoneInfo tzInfo;
        try { tzInfo = TimeZoneInfo.FindSystemTimeZoneById(tzId); }
        catch { tzInfo = TimeZoneInfo.Utc; }
        var localNow   = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tzInfo);
        var tzOffset   = tzInfo.GetUtcOffset(DateTime.UtcNow);
        var tzLabel    = $"UTC{(tzOffset >= TimeSpan.Zero ? "+" : "")}{tzOffset:hh\\:mm}";

        var sb = new StringBuilder();
        sb.AppendLine($"Language: {language}");
        sb.AppendLine($"Reader's region: {centerLabel}");
        sb.AppendLine($"Current date/time (server-local): {localNow:dddd, yyyy-MM-dd HH:mm} ({tzLabel})");
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
                var tStartUtc = s_epoch.AddMinutes(s.FirstMin);
                var tEndUtc   = s_epoch.AddMinutes(s.LastMin);
                var tStart = TimeZoneInfo.ConvertTimeFromUtc(tStartUtc, tzInfo).ToString("yyyy-MM-ddTHH:mm");
                var tEnd   = TimeZoneInfo.ConvertTimeFromUtc(tEndUtc,   tzInfo).ToString("yyyy-MM-ddTHH:mm");
                var tStartZ = tStartUtc.ToString("yyyy-MM-ddTHH:mmZ");
                var tEndZ   = tEndUtc.ToString("yyyy-MM-ddTHH:mmZ");
                sb.AppendLine($"  {s.GuidTicks.Count} players · {tStart}–{tEnd} ({tzLabel}) · UTC: {tStartZ}–{tEndZ} · ~{ApproxDuration(s.LastMin - s.FirstMin)}");
                if (s.Players.Count > 0)
                    sb.AppendLine("  " + string.Join(", ", s.Players.Select(p => $"{p.Name} ({p.Instr})")));
                AppendLore(sb, s.Key, lore, s.Players);
                if (jazzKeys.Contains(s.Key))
                    sb.AppendLine("  Genre: Jazz");
                if (rockKeys.Contains(s.Key))
                    sb.AppendLine("  Genre: Rock");
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
            if (rockKeys.Contains(s.Key))
                sb.AppendLine("   Genre: Rock");
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



        if (prevSummary != null)
        {
            sb.AppendLine();
            sb.AppendLine("PREVIOUS ESSAY SUMMARY (covers the prior 8-hour window — same data range largely overlaps):");
            sb.AppendLine("Use this only for continuity context. Do NOT open with a pronoun or phrase that refers to the summary (e.g. 'He wasn't', 'She continued', 'They were back'). Your first sentence must be self-contained. Where the same players appear again, you may use 'back again', 'still going', or 'quieter than earlier' — but only after you have introduced the scene fresh. Do not rehash the summary.");
            sb.AppendLine(prevSummary);
        }

        return sb.ToString();
    }

    private static string ExtractSummary(string html)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ")
            .Replace("  ", " ").Trim();
        if (text.Length <= 500) return text;
        int cut = text.LastIndexOfAny(new[] { '.', '!', '?' }, 500);
        return cut > 150 ? text[..(cut + 1)] : text[..500];
    }

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

    private static string StripUnauthorizedLinks(string html)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            html,
            @"<a\s[^>]*href=""([^""]+)""[^>]*>(.*?)</a>",
            m => m.Groups[2].Value,
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
            : new { maxOutputTokens = 4096, temperature = cfgTemp, thinkingConfig = new { thinkingBudget = 1024 } };
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
