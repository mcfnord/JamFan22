using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

public static class WelcomeMessageGenerator
{
    private static readonly HttpClient _http = new();
    private static string? _apiKey;
    private static int _inFlight;
    private const int MaxConcurrentLlm = 1;

    // Gemini cachedContent: keyed by "model:promptHash", stores name + expiry.
    private static readonly ConcurrentDictionary<string, (string Name, DateTime ExpiresAt)> _caches = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _cacheSems = new();

    private static async Task<string?> EnsureCacheAsync(string model, string systemPrompt)
    {
        if (_apiKey == null) return null;
        var key = $"{model}:{systemPrompt.GetHashCode():x8}";
        if (_caches.TryGetValue(key, out var hit) && DateTime.UtcNow < hit.ExpiresAt.AddMinutes(-10))
            return hit.Name;

        var sem = _cacheSems.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();
        try
        {
            if (_caches.TryGetValue(key, out hit) && DateTime.UtcNow < hit.ExpiresAt.AddMinutes(-10))
                return hit.Name;
            var body = new { model = $"models/{model}",
                system_instruction = new { parts = new[] { new { text = systemPrompt } } },
                ttl = "7200s" };
            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var cts = new CancellationTokenSource(10_000);
            var resp = await _http.PostAsync(
                $"https://generativelanguage.googleapis.com/v1beta/cachedContents?key={_apiKey}", content, cts.Token);
            var raw = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[WELCOME-CACHE] create failed model={model}: {raw[..Math.Min(200, raw.Length)]}");
                return null;
            }
            using var doc = JsonDocument.Parse(raw);
            var name = doc.RootElement.GetProperty("name").GetString();
            if (name == null) return null;
            DateTime expires = DateTime.UtcNow.AddHours(2);
            if (doc.RootElement.TryGetProperty("expireTime", out var et) &&
                DateTime.TryParse(et.GetString(), out var parsed)) expires = parsed.ToUniversalTime();
            _caches[key] = (name, expires);
            int cachedTok = doc.RootElement.TryGetProperty("usageMetadata", out var um) &&
                um.TryGetProperty("totalTokenCount", out var tc) ? tc.GetInt32() : 0;
            Console.WriteLine($"[WELCOME-CACHE] created model={model} tokens={cachedTok} name={name}");
            return name;
        }
        catch (Exception ex) { Console.WriteLine($"[WELCOME-CACHE] error: {ex.Message}"); return null; }
        finally { sem.Release(); }
    }

    private const string PromptPath = "data/welcome-system-prompt.txt";
    private const string GroupPromptPath = "data/welcome-group-announce-prompt.txt";
    private const string EssayPromptPath = "data/welcome-essay-prompt.txt";
    private const string KeyPath = "data/gemini-key.txt";
    private const string LogPath = "data/welcome-llm.log";
    private const string ModelEndpointBase =
        "https://generativelanguage.googleapis.com/v1beta/models/";
    private const string DefaultModel = "gemini-2.5-flash";
    private const string ConfigPath = "data/welcome-config.txt";

    public static void LoadApiKey()
    {
        try
        {
            _apiKey = File.ReadAllText(KeyPath).Trim();
            Console.WriteLine("[WELCOME-LLM] Gemini API key loaded.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WELCOME-LLM] Could not load {KeyPath}: {ex.Message}");
        }
    }

    private const string ProModel = "gemini-2.5-pro";

    // One `key=value` from welcome-config.txt (re-read per call, so every knob here is hot), or null.
    public static string? ReadConfigValue(string key)
    {
        try
        {
            foreach (var raw in File.ReadLines(ConfigPath))
            {
                var line = raw.Trim();
                if (line.StartsWith('#') || !line.Contains('=')) continue;
                var eq = line.IndexOf('=');
                if (line[..eq].Trim() == key) { var v = line[(eq + 1)..].Trim(); return v.Length > 0 ? v : null; }
            }
        }
        catch { }
        return null;
    }

    // `pro_model=` overrides; used only when GetAsync is called with usePro.
    private static string ReadProModel() => ReadConfigValue("pro_model") ?? ProModel;

    // `pro_thinking_budget=` (default 128). gemini-2.5-pro rejects 0 — HTTP 400 "only works in thinking mode".
    private static int ReadProThinkingBudget() =>
        int.TryParse(ReadConfigValue("pro_thinking_budget"), out var b) && b > 0 ? b : 128;

    private static (string model, double temperature, double proTemperature, int timeoutMs) ReadConfig()
    {
        string model = DefaultModel;
        double temp = 0.9;
        double proTemp = 1.3;
        int timeoutMs = 2000;
        try
        {
            foreach (var raw in File.ReadLines(ConfigPath))
            {
                var line = raw.Trim();
                if (line.StartsWith('#') || !line.Contains('=')) continue;
                var eq = line.IndexOf('=');
                var k = line[..eq].Trim();
                var v = line[(eq + 1)..].Trim();
                switch (k)
                {
                    case "model":           model = v; break;
                    case "temperature":     double.TryParse(v, System.Globalization.NumberStyles.Float,
                                                System.Globalization.CultureInfo.InvariantCulture, out temp); break;
                    case "pro_temperature": double.TryParse(v, System.Globalization.NumberStyles.Float,
                                                System.Globalization.CultureInfo.InvariantCulture, out proTemp); break;
                    case "llm_timeout_ms":  int.TryParse(v, out timeoutMs); break;
                }
            }
        }
        catch { }
        return (model, temp, proTemp, timeoutMs);
    }

    // Status values: "" = success, "timeout" = timed out, "error" = other failure
    public static async Task<(string? Message, string Status, int InputTokens, int OutputTokens, int CachedTokens)> GetAsync(string contextText, string nationCode, Dictionary<string, string>? nameColors = null, Dictionary<string, string>? nameEmojis = null, List<string>? eventNames = null, bool usePro = false)
    {
        if (_apiKey == null) return (null, "error", 0, 0, 0);

        if (Interlocked.Increment(ref _inFlight) > MaxConcurrentLlm)
        {
            Interlocked.Decrement(ref _inFlight);
            Console.WriteLine($"[WELCOME-LLM] shed nation={nationCode} inflight={_inFlight}");
            return (null, "shed", 0, 0, 0);
        }
        try
        {

        string sharedRules = "";
        try { sharedRules = await File.ReadAllTextAsync("data/welcome-shared-rules.txt") + "\n\n"; } catch { }
        string systemPrompt;
        try { systemPrompt = sharedRules + await File.ReadAllTextAsync(PromptPath); }
        catch { systemPrompt = "Generate a short warm 1-2 sentence HTML welcome for a musician. Output HTML only."; }

        var (model, temperature, proTemperature, timeoutMs) = ReadConfig();
        // Pro tier: only for arrivals the server's own lore already knows (WelcomeContext.IsLoreGuest).
        // gemini-2.5-pro rejects thinkingBudget=0 (HTTP 400 "only works in thinking mode"), so 128.
        // Measured 2026-08-30 with the cached prompt: flash 0.7-0.9 s, Pro 2.2-2.4 s.
        int thinkingBudget = 0;
        if (usePro) { model = ReadProModel(); temperature = proTemperature; thinkingBudget = ReadProThinkingBudget(); }
        string modelEndpoint = $"{ModelEndpointBase}{model}:generateContent";

        var cachedName = await EnsureCacheAsync(model, systemPrompt);
        string reqJson = cachedName != null
            ? JsonSerializer.Serialize(new {
                cachedContent = cachedName,
                contents = new[] { new { parts = new[] { new { text = contextText } } } },
                generationConfig = new { maxOutputTokens = 500, temperature, thinkingConfig = new { thinkingBudget } } })
            : JsonSerializer.Serialize(new {
                system_instruction = new { parts = new[] { new { text = systemPrompt } } },
                contents = new[] { new { parts = new[] { new { text = contextText } } } },
                generationConfig = new { maxOutputTokens = 500, temperature, thinkingConfig = new { thinkingBudget } } });

        var sw = Stopwatch.StartNew();
        string? message = null;
        int inputTokens = 0, outputTokens = 0, cachedTokens = 0;
        string errorNote = "";
        string status = "";

        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var content = new StringContent(reqJson, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync($"{modelEndpoint}?key={_apiKey}", content, cts.Token);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                errorNote = $"HTTP {(int)response.StatusCode}: {responseJson[..Math.Min(200, responseJson.Length)]}";
                status = "error";
            }
            else
            {
                using var doc = JsonDocument.Parse(responseJson);
                message = doc.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString()?.Trim();
                // Strip markdown code fences if the model wraps output
                if (message != null && message.StartsWith("```"))
                {
                    var lines = message.Split('\n');
                    message = string.Join('\n', lines.Skip(1).TakeWhile(l => l != "```")).Trim();
                }

                if (doc.RootElement.TryGetProperty("usageMetadata", out var usage))
                {
                    if (usage.TryGetProperty("promptTokenCount", out var pt) && pt.ValueKind == JsonValueKind.Number)
                        inputTokens = pt.GetInt32();
                    if (usage.TryGetProperty("candidatesTokenCount", out var ct) && ct.ValueKind == JsonValueKind.Number)
                        outputTokens = ct.GetInt32();
                    if (usage.TryGetProperty("cachedContentTokenCount", out var cc) && cc.ValueKind == JsonValueKind.Number)
                        cachedTokens = cc.GetInt32();
                }
            }
        }
        catch (OperationCanceledException)
        {
            errorNote = $"timeout>{timeoutMs}ms";
            status = "timeout";
        }
        catch (Exception ex)
        {
            errorNote = ex.Message;
            status = "error";
        }

        if (message != null)
        {
            if (!message.Contains("<p>"))
                message = $"<p>{message}</p>";
            message = CollapseRepeatedSentences(message);
            if (nameColors != null && nameColors.Count > 0)
                message = ApplyNameColors(message, nameColors, nameEmojis);
            else
                message = System.Text.RegularExpressions.Regex.Replace(
                    message, @"<font color=""([^""]+)"">([^<]+)</font>",
                    m => $"<font color=\"{m.Groups[1].Value}\"><b>{m.Groups[2].Value}</b></font>");
            if (eventNames != null && eventNames.Count > 0)
                message = ApplyEventUnderlines(message, eventNames);
            message = AnchorBareUrls(message);
        }

        sw.Stop();
        Console.WriteLine($"[WELCOME-LLM] nation={nationCode} model={model} in={inputTokens} cached={cachedTokens} out={outputTokens} ms={sw.ElapsedMilliseconds}" +
                          (errorNote.Length > 0 ? $" error={errorNote}" : ""));

        try
        {
            var log = new StringBuilder();
            log.AppendLine($"--- {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC  nation={nationCode}  ms={sw.ElapsedMilliseconds}  model={model} ---");
            log.AppendLine("CONTEXT:");
            log.AppendLine(contextText.TrimEnd());
            log.AppendLine("MESSAGE:");
            log.AppendLine(message ?? $"(fallback — {errorNote})");
            log.AppendLine();
            await File.AppendAllTextAsync(LogPath, log.ToString());
        }
        catch { /* log failure is non-fatal */ }

        return (message, status, inputTokens, outputTokens, cachedTokens);
        }
        finally { Interlocked.Decrement(ref _inFlight); }
    }

    // Themed banner for dormant-essay welcomes: "See your destiny" in the essay's language.
    public static string DestinyHeader(string languageName) => languageName switch
    {
        "Thai"    => "🔮 ดูโชคชะตาของคุณ",
        "Chinese" => "🔮 看見你的命運",
        _         => "🔮 See Your Destiny",
    };

    private static (string model, double temperature, int timeoutMs) ReadEssayConfig()
    {
        var (_, _, proTemp, _) = ReadConfig();
        string model = ProModel;
        int timeoutMs = 20000;
        try
        {
            foreach (var raw in File.ReadLines(ConfigPath))
            {
                var line = raw.Trim();
                if (line.StartsWith('#') || !line.Contains('=')) continue;
                var eq = line.IndexOf('=');
                var k = line[..eq].Trim();
                var v = line[(eq + 1)..].Trim();
                switch (k)
                {
                    case "essay_model":          model = v; break;
                    case "essay_llm_timeout_ms": int.TryParse(v, out timeoutMs); break;
                }
            }
        }
        catch { }
        return (model, proTemp, timeoutMs);
    }

    // Safety net for the ~1,799-char design cap. Trims at the last tag boundary under the
    // limit so we never cut an HTML tag in half; shorter messages pass through untouched.
    private static string TruncateHtml(string message, int maxLen)
    {
        if (message.Length <= maxLen) return message;
        int cut = message.LastIndexOf('>', maxLen - 1);
        return cut > 0 ? message[..(cut + 1)] : message[..maxLen];
    }

    // Dormant-server joiner-centered mini-essay. Uses the Pro model, a longer timeout, and
    // the essay system prompt. Language is forced (overrides the room-vote language in context).
    public static async Task<(string? Message, string Status, int InputTokens, int OutputTokens, int CachedTokens)> GetEssayAsync(
        string contextText, string languageName, string nationCode,
        Dictionary<string, string>? nameColors = null, Dictionary<string, string>? nameEmojis = null, List<string>? eventNames = null)
    {
        if (_apiKey == null) return (null, "error", 0, 0, 0);

        string sharedRules = "";
        try { sharedRules = await File.ReadAllTextAsync("data/welcome-shared-rules.txt") + "\n\n"; } catch { }
        string systemPrompt;
        try { systemPrompt = sharedRules + await File.ReadAllTextAsync(EssayPromptPath); }
        catch { systemPrompt = "Write a warm, personal mini-essay in HTML welcoming this musician to a quiet server, centered on them. Output HTML only."; }

        // Force the essay language regardless of the room-vote language baked into the context.
        contextText = System.Text.RegularExpressions.Regex.Replace(contextText,
            @"Language to use for message: \S+", $"Language to use for message: {languageName}");

        var (model, temperature, timeoutMs) = ReadEssayConfig();
        string modelEndpoint = $"{ModelEndpointBase}{model}:generateContent";

        var cachedName = await EnsureCacheAsync(model, systemPrompt);
        object genConfig = new { maxOutputTokens = 2048, temperature, thinkingConfig = new { thinkingBudget = 1024 } };
        string reqJson = cachedName != null
            ? JsonSerializer.Serialize(new {
                cachedContent = cachedName,
                contents = new[] { new { parts = new[] { new { text = contextText } } } }, generationConfig = genConfig })
            : JsonSerializer.Serialize(new {
                system_instruction = new { parts = new[] { new { text = systemPrompt } } },
                contents = new[] { new { parts = new[] { new { text = contextText } } } }, generationConfig = genConfig });

        var sw = Stopwatch.StartNew();
        string? message = null;
        int inputTokens = 0, outputTokens = 0, cachedTokens = 0;
        string errorNote = "";
        string status = "";

        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var content = new StringContent(reqJson, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync($"{modelEndpoint}?key={_apiKey}", content, cts.Token);
            var responseJson = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                errorNote = $"HTTP {(int)response.StatusCode}: {responseJson[..Math.Min(200, responseJson.Length)]}";
                status = "error";
            }
            else
            {
                using var doc = JsonDocument.Parse(responseJson);
                message = doc.RootElement.GetProperty("candidates")[0]
                    .GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString()?.Trim();
                if (message != null && message.StartsWith("```"))
                {
                    var lines = message.Split('\n');
                    message = string.Join('\n', lines.Skip(1).TakeWhile(l => l != "```")).Trim();
                }
                if (doc.RootElement.TryGetProperty("usageMetadata", out var usage))
                {
                    if (usage.TryGetProperty("promptTokenCount", out var pt) && pt.ValueKind == JsonValueKind.Number) inputTokens = pt.GetInt32();
                    if (usage.TryGetProperty("candidatesTokenCount", out var ct) && ct.ValueKind == JsonValueKind.Number) outputTokens = ct.GetInt32();
                    if (usage.TryGetProperty("cachedContentTokenCount", out var cc) && cc.ValueKind == JsonValueKind.Number) cachedTokens = cc.GetInt32();
                }
            }
        }
        catch (OperationCanceledException) { errorNote = $"timeout>{timeoutMs}ms"; status = "timeout"; }
        catch (Exception ex) { errorNote = ex.Message; status = "error"; }

        if (message != null)
        {
            if (!message.Contains("<p>")) message = $"<p>{message}</p>";
            message = CollapseRepeatedSentences(message);
            if (nameColors != null && nameColors.Count > 0) message = ApplyNameColors(message, nameColors, nameEmojis);
            if (eventNames != null && eventNames.Count > 0) message = ApplyEventUnderlines(message, eventNames);
            message = TruncateHtml(message, 1799);
            message = AnchorBareUrls(message);
        }

        sw.Stop();
        Console.WriteLine($"[WELCOME-ESSAY-LLM] nation={nationCode} lang={languageName} model={model} in={inputTokens} cached={cachedTokens} out={outputTokens} ms={sw.ElapsedMilliseconds}" +
                          (errorNote.Length > 0 ? $" error={errorNote}" : ""));

        try
        {
            var log = new StringBuilder();
            log.AppendLine($"--- [ESSAY] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC  nation={nationCode} lang={languageName}  ms={sw.ElapsedMilliseconds} ---");
            log.AppendLine("CONTEXT:");
            log.AppendLine(contextText.TrimEnd());
            log.AppendLine("MESSAGE:");
            log.AppendLine(message ?? $"(fallback — {errorNote})");
            log.AppendLine();
            await File.AppendAllTextAsync(LogPath, log.ToString());
        }
        catch { }

        return (message, status, inputTokens, outputTokens, cachedTokens);
    }

    public static async Task<string?> GetGroupAsync(string contextText, Dictionary<string, string>? nameColors = null, bool usePro = false)
    {
        if (_apiKey == null) return null;

        string sharedRules = "";
        try { sharedRules = await File.ReadAllTextAsync("data/welcome-shared-rules.txt") + "\n\n"; } catch { }
        string systemPrompt;
        try { systemPrompt = sharedRules + await File.ReadAllTextAsync(GroupPromptPath); }
        catch { systemPrompt = "Announce the arriving musician to the room in one warm English sentence. Output HTML only."; }

        var (model, temperature, proTemperature, _) = ReadConfig();
        if (usePro) { model = ProModel; temperature = proTemperature; }
        int timeoutMs = 4000;
        try
        {
            foreach (var raw in File.ReadLines(ConfigPath))
            {
                var line = raw.Trim();
                if (!line.Contains('=') || line.StartsWith('#')) continue;
                var eq = line.IndexOf('=');
                if (line[..eq].Trim() == "group_llm_timeout_ms")
                    int.TryParse(line[(eq + 1)..].Trim(), out timeoutMs);
            }
        }
        catch { }

        string modelEndpoint = $"{ModelEndpointBase}{model}:generateContent";
        var cachedNameGrp = await EnsureCacheAsync(model, systemPrompt);
        string grpJson = cachedNameGrp != null
            ? JsonSerializer.Serialize(new {
                cachedContent = cachedNameGrp,
                contents = new[] { new { parts = new[] { new { text = contextText } } } },
                generationConfig = new { maxOutputTokens = 200, temperature, thinkingConfig = new { thinkingBudget = 0 } } })
            : JsonSerializer.Serialize(new {
                system_instruction = new { parts = new[] { new { text = systemPrompt } } },
                contents = new[] { new { parts = new[] { new { text = contextText } } } },
                generationConfig = new { maxOutputTokens = 200, temperature, thinkingConfig = new { thinkingBudget = 0 } } });

        var sw = Stopwatch.StartNew();
        string? message = null;
        string errorNote = "";

        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var content = new StringContent(grpJson, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync($"{modelEndpoint}?key={_apiKey}", content, cts.Token);
            var responseJson = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(responseJson);
                message = doc.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString()?.Trim();
                if (message != null && message.StartsWith("```"))
                {
                    var lines = message.Split('\n');
                    message = string.Join('\n', lines.Skip(1).TakeWhile(l => l != "```")).Trim();
                }
            }
            else { errorNote = $"HTTP {(int)response.StatusCode}"; }
        }
        catch (OperationCanceledException) { errorNote = $"timeout>{timeoutMs}ms"; }
        catch (Exception ex) { errorNote = ex.Message; }

        if (message != null)
        {
            if (!message.Contains("<p>")) message = $"<p>{message}</p>";
            message = CollapseRepeatedSentences(message);
            if (nameColors != null && nameColors.Count > 0) message = ApplyNameColors(message, nameColors);
            message = AnchorBareUrls(message);
        }

        sw.Stop();
        Console.WriteLine($"[WELCOME-LLM-GROUP] ms={sw.ElapsedMilliseconds}" + (errorNote.Length > 0 ? $" error={errorNote}" : ""));

        try
        {
            var log = new StringBuilder();
            log.AppendLine($"--- [GROUP] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC  ms={sw.ElapsedMilliseconds} ---");
            log.AppendLine("CONTEXT:");
            log.AppendLine(contextText.TrimEnd());
            log.AppendLine("MESSAGE:");
            log.AppendLine(message ?? $"(null — {errorNote})");
            log.AppendLine();
            await File.AppendAllTextAsync(LogPath, log.ToString());
        }
        catch { }

        return message;
    }

    // The Jamulus client auto-links bare URLs itself, but ONLY when the chat text contains no
    // href= -- and its linkifier (chatdlg.cpp, https?://\S+) matches '<', so a bare URL that ends
    // a paragraph swallows the closing tag: "... at https://ear.jamulus.live!</p>" becomes
    // href="https://ear.jamulus.live!</p", which QUrl rejects as an invalid host. The link is then
    // dead, the client's open-link prompt shows an empty '' where the URL belongs, and the orphaned
    // '>' renders as a stray line. Emitting a real anchor here makes the client skip its own
    // linkifier entirely, so this fixes the rendering on EVERY client already in the field --
    // the client-side regex fix only helps once clients are rebuilt and redistributed.
    // Reproduced and verified against Qt 5.15 rendering, 2026-09-02.
    private const int MaxChatHtml = 1799;   // MAX_LEN_CHAT_TEXT_PLUS_HTML is 1800; the client's
                                            // protocol parser DROPS a chat message longer than that,
                                            // so never let anchoring push a message over it.
    private static string AnchorBareUrls(string message)
    {
        // Alternation order matters: a whole existing anchor, then any other tag, are matched first
        // and returned untouched -- so a URL inside href="..." or already inside <a>...</a> is never
        // wrapped a second time. Only the third branch, a bare URL in text, is rewritten.
        var anchored = System.Text.RegularExpressions.Regex.Replace(
            message,
            @"(?<anchor><a\b[^>]*>.*?</a>)|(?<tag><[^>]*>)|(?<url>https?://[^\s<>""']+)",
            m =>
            {
                if (!m.Groups["url"].Success) return m.Value;
                string url = m.Groups["url"].Value;
                // Mirror the client's rule: a URL never ends on this punctuation. Leave it outside
                // the anchor, so "Share/record at <url>!" keeps its '!' as plain text.
                string trail = "";
                while (url.Length > 0 && "!\"'()+,.:;<=>?[]{}".IndexOf(url[^1]) >= 0)
                {
                    trail = url[^1] + trail;
                    url = url[..^1];
                }
                if (url.Length == 0) return m.Value;
                return $"<a href='{url}'>{url}</a>{trail}";
            },
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return anchored.Length <= MaxChatHtml ? anchored : message;
    }

    private static string CollapseRepeatedSentences(string message)
    {
        // Collapse consecutive identical sentences (e.g. LLM echoing a verbatim instruction twice)
        return System.Text.RegularExpressions.Regex.Replace(
            message,
            @"([^.!?]{8,}[.!?])\s*\1",
            "$1",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string ApplyNameColors(string message, Dictionary<string, string> nameColors, Dictionary<string, string>? nameEmojis = null)
    {
        foreach (var (name, color) in nameColors.OrderByDescending(kv => kv.Key.Length))
        {
            if (name.Length < 2) continue;
            var escaped = System.Text.RegularExpressions.Regex.Escape(name);
            // Strip any existing font wrapping for this name (LLM-generated or prior pass)
            message = System.Text.RegularExpressions.Regex.Replace(message,
                $@"<font color=""[^""]*"">(?:<b>)?{escaped}(?:</b>)?</font>", name,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Strip plain bold
            message = System.Text.RegularExpressions.Regex.Replace(message,
                $@"<b>{escaped}</b>", name,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string emoji = nameEmojis != null && nameEmojis.TryGetValue(name, out var e) ? e + " " : "";
            // Strip any emoji already preceding the name (LLM echo or prior pass) so re-wrapping doesn't double it
            if (emoji.Length > 0)
                message = System.Text.RegularExpressions.Regex.Replace(message,
                    $@"(?:{System.Text.RegularExpressions.Regex.Escape(emoji.TrimEnd())}\s*)+(?={escaped})", "");
            string repl = color.Length > 0
                ? $@"{emoji}<font color=""{color}""><b>{name}</b></font>"
                : $@"{emoji}<b>{name}</b>";
            message = System.Text.RegularExpressions.Regex.Replace(message,
                $@"(?<![A-Za-z0-9])(?<!<b>){escaped}(?![A-Za-z0-9])",
                repl);
        }
        return message;
    }

    private static string ApplyEventUnderlines(string message, List<string> eventNames)
    {
        foreach (var name in eventNames.OrderByDescending(n => n.Length))
        {
            if (name.Length < 4) continue;
            var escaped = System.Text.RegularExpressions.Regex.Escape(name);
            message = System.Text.RegularExpressions.Regex.Replace(message, $@"<u>{escaped}</u>", name, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            message = System.Text.RegularExpressions.Regex.Replace(message, escaped, $"<u>{name}</u>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        return message;
    }
}
