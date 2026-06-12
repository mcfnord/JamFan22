using System.Diagnostics;
using System.Text;
using System.Text.Json;

public static class WelcomeMessageGenerator
{
    private static readonly HttpClient _http = new();
    private static string? _apiKey;
    private const string PromptPath = "data/welcome-system-prompt.txt";
    private const string GroupPromptPath = "data/welcome-group-announce-prompt.txt";
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
    public static async Task<(string? Message, string Status)> GetAsync(string contextText, string nationCode, Dictionary<string, string>? nameColors = null)
    {
        if (_apiKey == null) return (null, "error");

        string systemPrompt;
        try { systemPrompt = await File.ReadAllTextAsync(PromptPath); }
        catch { systemPrompt = "Generate a short warm 1-2 sentence HTML welcome for a musician. Output HTML only."; }

        var (model, temperature, _, timeoutMs) = ReadConfig();
        string modelEndpoint = $"{ModelEndpointBase}{model}:generateContent";

        var requestBody = new
        {
            system_instruction = new { parts = new[] { new { text = systemPrompt } } },
            contents = new[] { new { parts = new[] { new { text = contextText } } } },
            generationConfig = new { maxOutputTokens = 500, temperature,
                thinkingConfig = new { thinkingBudget = 0 } }
        };

        var sw = Stopwatch.StartNew();
        string? message = null;
        int inputTokens = 0, outputTokens = 0;
        string errorNote = "";
        string status = "";

        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var json = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
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
            if (nameColors != null && nameColors.Count > 0)
                message = ApplyNameColors(message, nameColors);
            else
                message = System.Text.RegularExpressions.Regex.Replace(
                    message, @"<font color=""([^""]+)"">([^<]+)</font>",
                    m => $"<font color=\"{m.Groups[1].Value}\"><b>{m.Groups[2].Value}</b></font>");
        }

        sw.Stop();
        Console.WriteLine($"[WELCOME-LLM] nation={nationCode} in={inputTokens} out={outputTokens} ms={sw.ElapsedMilliseconds}" +
                          (errorNote.Length > 0 ? $" error={errorNote}" : ""));

        try
        {
            var log = new StringBuilder();
            log.AppendLine($"--- {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC  nation={nationCode}  ms={sw.ElapsedMilliseconds} ---");
            log.AppendLine("CONTEXT:");
            log.AppendLine(contextText.TrimEnd());
            log.AppendLine("MESSAGE:");
            log.AppendLine(message ?? $"(fallback — {errorNote})");
            log.AppendLine();
            await File.AppendAllTextAsync(LogPath, log.ToString());
        }
        catch { /* log failure is non-fatal */ }

        return (message, status);
    }

    public static async Task<string?> GetGroupAsync(string contextText, Dictionary<string, string>? nameColors = null, bool usePro = false)
    {
        if (_apiKey == null) return null;

        string systemPrompt;
        try { systemPrompt = await File.ReadAllTextAsync(GroupPromptPath); }
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
        var requestBody = new
        {
            system_instruction = new { parts = new[] { new { text = systemPrompt } } },
            contents = new[] { new { parts = new[] { new { text = contextText } } } },
            generationConfig = new { maxOutputTokens = 200, temperature,
                thinkingConfig = new { thinkingBudget = 0 } }
        };

        var sw = Stopwatch.StartNew();
        string? message = null;
        string errorNote = "";

        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var json = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
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
            if (nameColors != null && nameColors.Count > 0) message = ApplyNameColors(message, nameColors);
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

    private static string ApplyNameColors(string message, Dictionary<string, string> nameColors)
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
            // Re-apply with correct color, respecting word boundaries
            message = System.Text.RegularExpressions.Regex.Replace(message,
                $@"(?<![A-Za-z0-9])(?<!<b>){escaped}(?![A-Za-z0-9])",
                $@"<font color=""{color}""><b>{name}</b></font>");
        }
        return message;
    }
}
