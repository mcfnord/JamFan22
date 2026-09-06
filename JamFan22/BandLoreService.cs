using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

public static class BandLoreService
{
    private const string CacheDir   = "data/band-lore-cache";
    private const string KeyPath    = "data/gemini-key.txt";
    private const string PromptPath = "data/band-lore-prompt.txt";
    private const string LogPath    = "data/band-lore.log";
    private const string BandsPath  = "data/bands.json";
    private const string TtPath     = "timeTogether.json";
    private const string LorePath   = "data/server-lore.json";
    private const string ModelBase  = "https://generativelanguage.googleapis.com/v1beta/models/";
    private const string Model      = "gemini-2.5-flash";
    private const int    DailyCap   = 10;

    private static readonly TimeSpan s_ttl = TimeSpan.FromDays(7);
    private static readonly HttpClient s_http = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_locks = new();
    private static string?  s_apiKey;

    private static readonly Dictionary<string, string> s_lang = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DE"]="German",  ["AT"]="German",  ["CH"]="German",
        ["FR"]="French",  ["IT"]="Italian",
        ["ES"]="Spanish", ["MX"]="Spanish", ["AR"]="Spanish", ["CL"]="Spanish",
        ["CO"]="Spanish", ["PE"]="Spanish", ["VE"]="Spanish",
        ["BR"]="Portuguese", ["PT"]="Portuguese",
        ["NL"]="Dutch",   ["PL"]="Polish",  ["SE"]="Swedish",  ["NO"]="Norwegian",
        ["DK"]="Danish",  ["FI"]="Finnish", ["RU"]="Russian",  ["TR"]="Turkish",
        ["TH"]="Thai",    ["JP"]="Japanese",["KR"]="Korean",
        ["CN"]="Simplified Chinese", ["TW"]="Traditional Chinese", ["HK"]="Traditional Chinese",
        ["ID"]="Indonesian",
    };
    private static int      s_generatedToday;
    private static DateTime s_resetAt = DateTime.UtcNow.Date.AddDays(1);

    public static void LoadApiKey()
    {
        try   { s_apiKey = File.ReadAllText(KeyPath).Trim(); Console.WriteLine("[BAND-LORE] key loaded."); }
        catch (Exception ex) { Console.WriteLine($"[BAND-LORE] key missing: {ex.Message}"); }
    }

    public static async Task<string?> GetLoreHtmlAsync(int bandId, string clientIp)
    {
        if (s_apiKey == null) return null;
        var language = await ResolveLanguageAsync(clientIp);
        var cached = TryReadCache(bandId, language);
        if (cached != null) return cached;

        var semKey = $"{bandId}:{language}";
        var sem = s_locks.GetOrAdd(semKey, _ => new SemaphoreSlim(1, 1));
        if (!await sem.WaitAsync(TimeSpan.FromSeconds(30))) return null;
        try
        {
            cached = TryReadCache(bandId, language);
            if (cached != null) return cached;

            if (DateTime.UtcNow >= s_resetAt) { s_generatedToday = 0; s_resetAt = DateTime.UtcNow.Date.AddDays(1); }
            if (s_generatedToday >= DailyCap)
            {
                Console.WriteLine($"[BAND-LORE] daily-cap-hit band_id={bandId} lang={language}");
                return null;
            }
            s_generatedToday++;

            var context = await BuildContextAsync(bandId, language);
            if (context == null) return null;

            var html = await CallLlmAsync(bandId, language, context);
            if (html != null)
            {
                Directory.CreateDirectory(CacheDir);
                WriteCache(bandId, language, html);
                Console.WriteLine($"[BAND-LORE] generated band_id={bandId} lang={language} {html.Length}ch");
            }
            return html;
        }
        finally { sem.Release(); }
    }

    private static async Task<string> ResolveLanguageAsync(string clientIp)
    {
        var geo = await JamFan22.Services.IpAnalyticsService.FetchIpApiAsync(clientIp);
        var cc  = geo?["countryCode"]?.ToString() ?? "";
        return s_lang.TryGetValue(cc, out var lang) ? lang : "English";
    }

    // ── Disk cache ────────────────────────────────────────────────────────────

    private static string? TryReadCache(int bandId, string language)
    {
        try
        {
            var path = Path.Combine(CacheDir, $"{bandId}-{language.ToLowerInvariant()}.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var generatedAt = doc.RootElement.GetProperty("generated_utc").GetDateTime();
            if (DateTime.UtcNow - generatedAt > s_ttl)
                return null;
            if (File.GetLastWriteTimeUtc(BandsPath) > generatedAt)
                return null;
            return doc.RootElement.GetProperty("html").GetString();
        }
        catch { return null; }
    }

    private static void WriteCache(int bandId, string language, string html)
    {
        var path = Path.Combine(CacheDir, $"{bandId}-{language.ToLowerInvariant()}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { generated_utc = DateTime.UtcNow, html }));
    }

    // ── Context assembly ──────────────────────────────────────────────────────

    private static async Task<string?> BuildContextAsync(int bandId, string language)
    {
        JsonElement band;
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(BandsPath));
            var match = doc.RootElement.GetProperty("bands").EnumerateArray()
                .FirstOrDefault(b => b.TryGetProperty("id", out var id) && id.GetInt32() == bandId);
            if (match.ValueKind == JsonValueKind.Undefined) return null;
            band = match.Clone();
        }
        catch { return null; }

        var tt = await LoadTimeTogetherAsync();
        var sb = new StringBuilder();
        sb.AppendLine($"Language: {language}");
        sb.AppendLine();

        var bandName = band.TryGetProperty("band_name", out var bn) ? bn.GetString() ?? "Unnamed" : "Unnamed";
        sb.AppendLine($"Band: {bandName}");
        if (band.TryGetProperty("session_count",      out var sc))  sb.AppendLine($"Sessions: {sc.GetInt32()}");
        if (band.TryGetProperty("span_weeks",         out var spw)) sb.AppendLine($"Span: {spw.GetDouble()} weeks");

        sb.AppendLine().AppendLine("Members:");
        var guidNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in band.GetProperty("members").EnumerateArray())
        {
            var name = m.GetProperty("name").GetString() ?? "";
            sb.AppendLine($"  {name}");
            if (m.GetProperty("guid").GetString() is { } g && name.Length > 0) guidNames[g] = name;
        }

        var guids = guidNames.Keys.ToList();
        var pairs = new List<(string N1, string N2, double Hours)>();
        for (int i = 0; i < guids.Count; i++)
        for (int j = i + 1; j < guids.Count; j++)
        {
            if (!tt.TryGetValue(guids[i] + guids[j], out var h)) tt.TryGetValue(guids[j] + guids[i], out h);
            if (h > 0) pairs.Add((guidNames[guids[i]], guidNames[guids[j]], h));
        }
        if (pairs.Count > 0)
        {
            sb.AppendLine().AppendLine("Time played together:");
            foreach (var (n1, n2, h) in pairs.OrderByDescending(p => p.Hours))
                sb.AppendLine($"  {n1} + {n2}: {h:F0} hours");
        }

        if (band.TryGetProperty("primary_server", out var ps) && ps.GetString() is { Length: > 0 } serverKey)
        {
            var serverLore = await LoadServerLoreAsync(serverKey);
            if (serverLore != null)
            {
                sb.AppendLine().AppendLine("Home server:");
                if (serverLore.Value.TryGetProperty("name", out var sn) && sn.GetString() is { } snv)
                    sb.AppendLine($"  Name: {snv}");
                if (serverLore.Value.TryGetProperty("tagline", out var stag) && stag.GetString() is { } stagv)
                    sb.AppendLine($"  Tagline: {stagv}");
                if (serverLore.Value.TryGetProperty("themes", out var sthemes) && sthemes.ValueKind == JsonValueKind.Array)
                    foreach (var theme in sthemes.EnumerateArray())
                        if (theme.GetString() is { } themeStr) sb.AppendLine($"  Theme: {themeStr}");
            }
        }

        if (band.TryGetProperty("url_samples", out var urls))
        {
            var list = urls.EnumerateArray().Select(u => u.GetString()).OfType<string>().ToList();
            if (list.Count > 0)
            {
                sb.AppendLine().AppendLine("URLs observed during their sessions (song links, chord charts, etc.):");
                foreach (var u in list) sb.AppendLine($"  {u}");
            }
        }

        return sb.ToString();
    }

    private static async Task<Dictionary<string, double>> LoadTimeTogetherAsync()
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(TtPath));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var key = item.GetProperty("Key").GetString();
                var val = item.GetProperty("Value").GetString();
                if (key?.Length == 64 && TimeSpan.TryParse(val, out var ts) && ts.TotalHours >= 10)
                    map[key] = ts.TotalHours;
            }
        }
        catch { }
        return map;
    }

    private static async Task<JsonElement?> LoadServerLoreAsync(string serverKey)
    {
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(LorePath));
            if (doc.RootElement.TryGetProperty(serverKey, out var entry)) return entry.Clone();
        }
        catch { }
        return null;
    }

    // ── LLM call ─────────────────────────────────────────────────────────────

    private static async Task<string?> CallLlmAsync(int bandId, string language, string context)
    {
        string sysPrompt;
        try { sysPrompt = await File.ReadAllTextAsync(PromptPath); }
        catch { sysPrompt = "Write a single prose paragraph (100-130 words) about this recurring Jamulus band. Use only the provided data — no speculation. Plain HTML: one <p> tag only."; }

        var body = new
        {
            system_instruction = new { parts = new[] { new { text = sysPrompt } } },
            contents = new[] { new { parts = new[] { new { text = context } } } },
            generationConfig = new { maxOutputTokens = 400, temperature = 1.0,
                                     thinkingConfig  = new { thinkingBudget = 0 } },
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? result = null; string err = "";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var payload = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var resp = await s_http.PostAsync($"{ModelBase}{Model}:generateContent?key={s_apiKey}", payload, cts.Token);
            var raw  = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("candidates", out var cands) && cands.GetArrayLength() > 0 &&
                    cands[0].TryGetProperty("content", out var cont) && cont.TryGetProperty("parts", out var parts))
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("thought", out var t) && t.GetBoolean()) continue;
                        if (part.TryGetProperty("text",    out var tp)) { result = tp.GetString()?.Trim(); break; }
                    }
                else err = $"bad shape: {raw[..Math.Min(200, raw.Length)]}";
                if (result?.StartsWith("```") == true)
                    result = string.Join('\n', result.Split('\n').Skip(1).TakeWhile(l => l != "```")).Trim();
            }
            else err = $"HTTP {(int)resp.StatusCode}: {raw[..Math.Min(200, raw.Length)]}";
        }
        catch (OperationCanceledException) { err = "timeout"; }
        catch (Exception ex)               { err = ex.Message; }
        sw.Stop();

        Console.WriteLine($"[BAND-LORE-LLM] band_id={bandId} lang={language} ms={sw.ElapsedMilliseconds}" +
                          (err.Length > 0 ? $" err={err}" : ""));
        try
        {
            await File.AppendAllTextAsync(LogPath,
                $"--- {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC  band_id={bandId}  ms={sw.ElapsedMilliseconds} ---\n" +
                $"CONTEXT:\n{context.TrimEnd()}\nLORE:\n{result ?? $"(null — {err})"}\n\n");
        }
        catch { }

        return result;
    }
}
