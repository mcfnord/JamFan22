// /chat-url-client SERVER-RESOLUTION FLOW
//   1. IsIpAllowedAsync(remoteIp) — silently reject blocked IPs
//   2. IdentityManager.GetGuidStrengths(remoteIp) -> Dictionary<string, int>
//      (all GUIDs seen for this IP at any strength, including 0)
//   3. Build activeGuidToServer map by iterating JamulusAnalyzer.m_allMyServers
//      -> whoObjectFromSourceData -> EncounterTracker.GetHash(name, country, instrument)
//   4. Intersect: find highest-strength GUID for this IP that is currently active on a server
//   5. Call harvest.IngestChatUrlAsync(url, serverAddr) with the winning server
// Log prefix [CHAT-URL-CLIENT] shows each step: BLOCKED, guid list,
// ACTIVE/NOT-ACTIVE per guid, and final STORING or "No active server found".

using JamFan22;
using JamFan22.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Newtonsoft.Json.Linq;
using System.Net.Mime;
using System.Text;
using System.Threading;

var hottiesSemaphore = new SemaphoreSlim(1, 1);

// Telemetry state
int _currentTelemetryMinute = JamFan22.Services.JamulusCacheManager.MinutesSince2023AsInt();
var _seenEvents = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
var _telemetryLock = new object();

// /chat-url-client rate limit: 3 requests per minute per IP
var _chatUrlClientRateLimit = new System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, DateTime Window)>();

// /api/nearby-essay rate limit: 2 requests per hour per IP
var _essayRateLimit = new System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, DateTime Window)>();

// Fleet WebSocket channel registry: "{callerIP}:{serverPort}" → open WebSocket
var _fleetWsRegistry = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Net.WebSockets.WebSocket>(StringComparer.Ordinal);
// Pending JSON-RPC responses: "{regKey}:{id}" → TaskCompletionSource (completed by receive loop)
var _fleetWsRpcPending = new System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<string>>(StringComparer.Ordinal);
// Per-connection RPC request ID counter
var _fleetWsRpcSeq = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);
// Per-socket send semaphore — WebSocket.SendAsync must not overlap
var _fleetWsSendLocks = new System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

// Fleet GUID-IP cache lives in FleetGuidCache (static class, accessible from IdentityManager)

Console.WriteLine($"[STARTUP] JamFan22 starting. PID={Environment.ProcessId} Time={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
FleetGuidCache.HydrateFromCsv();   // sync — loads fleet GUID cache from fleet-guid-ip.csv
StreamRequestManager.Load();        // sync — loads stream-requests.json
BandInviteTracker.Load();           // sync — loads band-invite-log.json
StreamGate.Load();                  // sync — loads stream-gate.json (current lease)
_ = StreamGate.PostLeaseMonitorAsync(); // background — watches for lease expiry
WelcomeMessageGenerator.LoadApiKey();   // sync — reads data/gemini-key.txt
DailyEssayService.LoadApiKey();         // sync — same key
BandLoreService.LoadApiKey();           // sync — same key
Task.Run(CensusIndex.EnsureBuilt);      // background — O(n) scan of census.csv; logs [CENSUS-INDEX] Built

// background — after 15s delay, pre-warms ip-api cache for all census server IPs;
// sets IpAnalyticsService.WarmupComplete = true when done (DailyEssayService gate)
_ = Task.Run(async () => {
    await Task.Delay(15000);
    try {
        var ips = new HashSet<string>();
        foreach (var line in await File.ReadAllLinesAsync("data/census.csv"))
        {
            var parts = line.Split(',');
            if (parts.Length >= 3 && parts[2].Contains(':'))
                ips.Add(parts[2].Split(':')[0]);
        }
        Console.WriteLine($"[STARTUP-WARM] Warming {ips.Count} server IPs from census");
        foreach (var ip in ips)
            await JamFan22.Services.IpAnalyticsService.FetchIpApiAsync(ip, waitIfThrottled: true);
        JamFan22.Services.IpAnalyticsService.WarmupComplete = true;
        Console.WriteLine($"[STARTUP-WARM] Done");
    } catch (Exception ex) { Console.WriteLine($"[STARTUP-WARM] Error: {ex.Message}"); }
});

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseKestrel(serverOptions =>
{
    var port = 443;
    var portStr = Environment.GetEnvironmentVariable("PORT");
    if (!string.IsNullOrEmpty(portStr) && int.TryParse(portStr, out int p))
        port = p;

    if (port == 443)
        serverOptions.ListenAnyIP(port, listenOptions => listenOptions.UseHttps("key-current.pfx", "jamfan"));
    else
        serverOptions.ListenAnyIP(port);
});

// Add services to the container.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});
builder.Services.AddRazorPages();
builder.Services.AddSignalR();

builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();

// Business logic singletons (order: dependencies before dependents)
builder.Services.AddSingleton<JamFan22.Services.EncounterTracker>();
builder.Services.AddSingleton<JamFan22.Services.JamulusCacheManager>();
builder.Services.AddSingleton<JamFan22.Services.IpAnalyticsService>();
builder.Services.AddSingleton<JamFan22.Services.GeolocationService>();
builder.Services.AddSingleton<JamFan22.Services.JamulusAnalyzer>();

builder.Services.AddHostedService<JamFan22.Services.JamulusListRefreshService>();
builder.Services.AddHostedService<JamFan22.Services.JammerHarvestService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseResponseCompression();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (ctx.Context.Request.Path.Value.EndsWith("asn-ip-client-blocks.txt", StringComparison.OrdinalIgnoreCase))
        {
            var logger = ctx.Context.RequestServices.GetRequiredService<ILogger<Program>>();
            logger.LogInformation("asn-ip-client-blocks.txt accessed by {IP}", ctx.Context.Connection.RemoteIpAddress);
        }
    }
});

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (!path.StartsWith("/api/track") && !path.StartsWith("/chathub") && path != "/favicon.ico")
    {
        string ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var xff = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(xff)) ip = xff.Split(',')[0].Trim();
        if (!ip.Contains("::ffff:")) ip = "::ffff:" + ip;

        var ua  = context.Request.Headers["User-Agent"].FirstOrDefault() ?? "";
        var ref_ = context.Request.Headers["Referer"].FirstOrDefault() ?? "";
        var fullPath = path + (context.Request.QueryString.Value ?? "");
        int nowMinute = JamFan22.Services.JamulusCacheManager.MinutesSince2023AsInt();

        var json = System.Text.Json.JsonSerializer.Serialize(new { a = "http_req", m = context.Request.Method, p = fullPath, ua, @ref = ref_ });
        lock (_telemetryLock)
            System.IO.File.AppendAllText("data/telemetry.log", $"{nowMinute},{ip},anon,0,0,{json}" + Environment.NewLine);
    }
    await next(context);
});

app.UseWebSockets();
app.UseRouting();
app.UseAuthorization();
app.MapRazorPages();
app.MapHub<JamFan22.ChatHub>("/chathub");

// Send a JSON-RPC request over the fleet WebSocket and return the raw response JSON.
// Returns null on timeout (5s), WS not found, or send error.
async Task<string?> FleetWsRpcCallAsync(string regKey, System.Net.WebSockets.WebSocket ws,
    string method, object @params, System.Text.Json.JsonSerializerOptions opts)
{
    int rpcId = _fleetWsRpcSeq.AddOrUpdate(regKey, 1, (_, v) => v + 1);
    string pendingKey = $"{regKey}:{rpcId}";
    var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    _fleetWsRpcPending[pendingKey] = tcs;
    try
    {
        var reqBytes = System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(
                new { id = rpcId, jsonrpc = "2.0", method, @params }, opts));
        if (!_fleetWsSendLocks.TryGetValue(regKey, out var sem))
        { _fleetWsRpcPending.TryRemove(pendingKey, out _); return null; }
        await sem.WaitAsync();
        try { await ws.SendAsync(new ArraySegment<byte>(reqBytes), System.Net.WebSockets.WebSocketMessageType.Text, true, default); }
        finally { sem.Release(); }
        using var cts = new System.Threading.CancellationTokenSource(5000);
        cts.Token.Register(() =>
        { if (_fleetWsRpcPending.TryRemove(pendingKey, out var t)) t.TrySetCanceled(); });
        return await tcs.Task;
    }
    catch { _fleetWsRpcPending.TryRemove(pendingKey, out _); return null; }
}

app.Map("/fleet-rpc-channel", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    { context.Response.StatusCode = 400; return; }
    string wsClientIP = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    wsClientIP = wsClientIP.Replace("::ffff:", "");
    string wsPort = context.Request.Query["port"].FirstOrDefault() ?? "";
    string wsBuild = context.Request.Query["build"].FirstOrDefault() ?? "";
    if (wsPort.Length == 0) { context.Response.StatusCode = 400; return; }
    string regKey = $"{wsClientIP}:{wsPort}";
    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    _fleetWsRegistry[regKey] = ws;
    _fleetWsSendLocks[regKey] = new SemaphoreSlim(1, 1);
    Console.WriteLine($"[fleet-rpc-channel] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} connected key={regKey}"
                      + (wsBuild.Length > 0 ? $" build={wsBuild}" : ""));
    var buf = new byte[8192];
    try
    {
        while (ws.State == System.Net.WebSockets.WebSocketState.Open)
        {
            using var ms = new System.IO.MemoryStream();
            System.Net.WebSockets.WebSocketReceiveResult rcvResult;
            do
            {
                rcvResult = await ws.ReceiveAsync(new ArraySegment<byte>(buf), context.RequestAborted);
                if (rcvResult.Count > 0) ms.Write(buf, 0, rcvResult.Count);
            } while (!rcvResult.EndOfMessage);

            if (rcvResult.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "bye", context.RequestAborted);
            else if (rcvResult.MessageType == System.Net.WebSockets.WebSocketMessageType.Text && ms.Length > 0)
            {
                string json = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("id", out var idEl)
                        && idEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        string pendingKey = $"{regKey}:{idEl.GetInt32()}";
                        if (_fleetWsRpcPending.TryRemove(pendingKey, out var tcs))
                            tcs.TrySetResult(json);
                    }
                }
                catch { }
            }
        }
    }
    catch { }
    finally
    {
        _fleetWsRegistry.TryRemove(regKey, out _);
        _fleetWsSendLocks.TryRemove(regKey, out _);
        _fleetWsRpcSeq.TryRemove(regKey, out _);
        string prefix = regKey + ":";
        foreach (var k in _fleetWsRpcPending.Keys.ToList())
            if (k.StartsWith(prefix) && _fleetWsRpcPending.TryRemove(k, out var t))
                t.TrySetCanceled();
        Console.WriteLine($"[fleet-rpc-channel] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} disconnected key={regKey}");
    }
});

app.MapGet("/countries", (HttpContext context) =>
{
    var analyzer = context.RequestServices.GetRequiredService<JamulusAnalyzer>();

    var stats = JamulusAnalyzer.m_bucketUniqueIPsByCountry
        .Select(kvp => new {
            CountryCode = string.IsNullOrEmpty(kvp.Key) ? "Unknown" : kvp.Key,
            UniqueIPs   = kvp.Value.Count,
            Refreshes   = JamulusAnalyzer.m_countryRefreshCounts.GetValueOrDefault(kvp.Key, 0)
        })
        .OrderByDescending(x => x.UniqueIPs)
        .ToList();

    var sb = new StringBuilder();
    sb.AppendLine("<!DOCTYPE html><html><head><title>Country Diagnostics</title>");
    sb.AppendLine("<style>body{font-family:sans-serif; margin: 2rem;} table{border-collapse:collapse; width: 100%; max-width: 600px;} th,td{padding:8px;border:1px solid #ccc; text-align: left;} th{background-color: #f4f4f4;}</style>");
    sb.AppendLine("</head><body>");
    sb.AppendLine("<h2>Visitor Distribution by Country</h2>");
    sb.AppendLine("<p><em>Data resets on application restart.</em></p>");
    sb.AppendLine("<table><tr><th>Country Code</th><th>Unique IPs</th><th>Total API Requests</th></tr>");
    foreach (var stat in stats)
        sb.AppendLine($"<tr><td>{stat.CountryCode}</td><td>{stat.UniqueIPs}</td><td>{stat.Refreshes}</td></tr>");
    sb.AppendLine("</table></body></html>");

    return Results.Content(sb.ToString(), "text/html");
});

app.MapGet("/ip-allowed/{ip}", async (string ip, HttpContext context) =>
{
    string callerIP = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var xff = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    if (!string.IsNullOrEmpty(xff)) callerIP = xff.Split(',')[0].Trim();
    callerIP = callerIP.Replace("::ffff:", "");

    if (!FleetIpAllowlist.Contains(callerIP))
    {
        Console.WriteLine($"[IP-ALLOWED] rejected non-fleet caller ip={callerIP}");
        return Results.StatusCode(403);
    }

    var guid = context.Request.Query["guid"].FirstOrDefault();
    var serverPortStr = context.Request.Query["serverport"].FirstOrDefault();
    string serverKey = !string.IsNullOrEmpty(serverPortStr) ? $"{callerIP}:{serverPortStr}" : callerIP;

    bool blocked = await IpAnalyticsService.IsIpBlockedAsync(ip);
    if (!string.IsNullOrEmpty(guid))
        FleetGuidCache.UpsertGuid(guid, ip, serverKey, blocked);
    string verdict = blocked ? "BLOCKED" : "ALLOWED";
    Console.WriteLine($"[IP-ALLOWED] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} caller={callerIP} query={ip} guid={guid ?? "-"} => {verdict}");

    return Results.Text(blocked ? "false" : "true", "text/plain");
});

app.MapGet("/debug/welcome-preview", async (HttpContext context) =>
{
    var remoteIp = context.Connection.RemoteIpAddress;
    if (remoteIp == null || !System.Net.IPAddress.IsLoopback(remoteIp))
        return Results.StatusCode(403);

    var guid = context.Request.Query["guid"].FirstOrDefault() ?? "";
    var nation = context.Request.Query["nation"].FirstOrDefault() ?? "US";
    var serverIp = context.Request.Query["serverIp"].FirstOrDefault() ?? "";
    var serverPort = context.Request.Query["serverport"].FirstOrDefault() ?? "";
    string serverKey = serverPort.Length > 0 ? $"{serverIp}:{serverPort}" : serverIp;
    if (!int.TryParse(context.Request.Query["rpcport"].FirstOrDefault(), out int rpcPort)) rpcPort = 9999;

    var debugPlayerIp = context.Request.Query["playerIp"].FirstOrDefault();
    var gatherDebug = await WelcomeContext.GatherAsync(guid, serverKey, rpcPort, nation, -1, debugPlayerIp);
    var ctx = gatherDebug.Context;
    var nameColors = gatherDebug.NameColors;
    string signals = WelcomeContext.TakeSignals(guid);
    bool rich = WelcomeContext.IsRich(ctx);
    bool usePro = WelcomeContext.IsLoreGuest(serverKey, gatherDebug.ArrivingName, guid);
    int fleetMinutes = WelcomeContext.FleetMinutes(guid);
    string? llmMessage = rich ? (await WelcomeMessageGenerator.GetAsync(ctx, nation, nameColors, usePro: usePro)).Message : null;
    string? englishMessage = null;
    if (rich && nation != "US")
    {
        var ctxEn = System.Text.RegularExpressions.Regex.Replace(ctx,
            @"Language to use for message: \S+", "Language to use for message: English");
        englishMessage = (await WelcomeMessageGenerator.GetAsync(ctxEn, "US", nameColors, usePro: usePro)).Message;
    }
    WelcomeEventLog.Append($"preview:{serverKey}", nation, rich, llmMessage != null ? "1" : "0", signals, 0, llmMessage ?? WelcomeMessages.Get(nation));

    return Results.Json(new
    {
        context = ctx,
        signals,
        rich,
        llmMessage,
        english = englishMessage,
        fallback = WelcomeMessages.Get(nation),
        usingLlm = llmMessage != null,
        usingPro = usePro,
        fleetMinutes
    });
});

app.MapGet("/debug/fleet-levels", (HttpContext context) =>
{
    var remoteIp = context.Connection.RemoteIpAddress;
    if (remoteIp == null || !System.Net.IPAddress.IsLoopback(remoteIp))
        return Results.StatusCode(403);

    var result = harvest.m_fleetClientLevels
        .OrderBy(kv => kv.Key)
        .ToDictionary(
            kv => kv.Key,
            kv => new {
                quiet    = harvest.m_fleetSilenceStatus.TryGetValue(kv.Key, out bool q) ? q : (bool?)null,
                clients  = kv.Value
                    .OrderBy(p => p.Key)
                    .Select(p => new { name = p.Key, level = p.Value, audible = p.Value > 0 })
                    .ToList()
            });

    return Results.Json(result);
});

app.MapGet("/api/nearby", async (HttpContext context) =>
{
    string clientIP = context.Connection.RemoteIpAddress?.ToString() ?? "24.18.55.230";
    if (clientIP.Length < 5 || clientIP.Contains("127.0.0.1") || clientIP.Contains("::1"))
    {
        var xff = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(xff))
        {
            clientIP = xff.Split(',')[0].Trim();
            if (!clientIP.Contains("::ffff")) clientIP = "::ffff:" + clientIP;
        }
        else { clientIP = "24.18.55.230"; }
    }

    Console.WriteLine($"[VISIT] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} {clientIP}");

    var finder = new JamFan22.MusicianFinder();
    var htmlTask   = finder.FindMusiciansHtmlAsync(clientIP);
    var statusTask = DailyEssayService.GetEssayStatusAsync(clientIP);
    await Task.WhenAll(htmlTask, statusTask);
    context.Response.Headers["X-Essay-Delay"] = statusTask.Result.DelaySeconds.ToString();
    context.Response.Headers["X-Essay-Ready"] = statusTask.Result.Ready ? "1" : "0";
    return Results.Content(htmlTask.Result, "text/html");
});

app.MapGet("/api/nearby-essay", async (HttpContext context) =>
{
    string clientIP = context.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";
    if (clientIP.Length < 5 || clientIP.Contains("127.0.0.1") || clientIP.Contains("::1"))
    {
        var xff = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(xff))
            clientIP = xff.Split(',')[0].Trim();
        else
            clientIP = "0.0.0.0";
    }
    var now = DateTime.UtcNow;
    // Only successful deliveries count against the limit — polling while the essay
    // isn't ready yet must not burn the quota (see TODO.md "Daily Essay").
    if (_essayRateLimit.TryGetValue(clientIP, out var existing) &&
        now - existing.Window <= TimeSpan.FromHours(1) &&
        existing.Count > 2)
    {
        Console.WriteLine($"[ESSAY] rate-limit ip={clientIP} count={existing.Count}");
        return Results.NoContent();
    }
    var html = await DailyEssayService.GetEssayHtmlAsync(clientIP);
    if (html == null) return Results.NoContent();
    // Personalize: if this visitor's IP resolves to a GUID featured in the essay they're being
    // served, prepend a "You're in this one" banner. Silent no-op for everyone else.
    try
    {
        var banner = DailyEssayService.TryGetVisitorBanner(clientIP, await DailyEssayService.ResolveCacheKeyAsync(clientIP));
        if (banner != null) html = banner + html;
    }
    catch (Exception ex) { Console.WriteLine($"[ESSAY-YOU] banner error: {ex.Message}"); }
    _essayRateLimit.AddOrUpdate(clientIP,
        _ => (1, now),
        (_, old) => now - old.Window > TimeSpan.FromHours(1) ? (1, now) : (old.Count + 1, old.Window));
    return Results.Content(html, "text/html");
});

app.MapGet("/api/band-lore", async (HttpContext context) =>
{
    if (!int.TryParse(context.Request.Query["id"], out int bandId))
        return Results.NoContent();

    string clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";
    clientIp = clientIp.Replace("::ffff:", "");
    if (clientIp is "127.0.0.1" or "::1" or "0.0.0.0")
    {
        var xff = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(xff)) clientIp = xff.Split(',')[0].Trim();
    }

    var html = await BandLoreService.GetLoreHtmlAsync(bandId, clientIp);
    if (html == null) return Results.NoContent();
    return Results.Json(new { html });
});

app.MapGet("/api/geo-diag", async (HttpContext context) =>
{
    var finder = new JamFan22.MusicianFinder();
    string html = await finder.GeoDiagAsync();
    return Results.Content(html, "text/html");
});

app.MapGet("/hotties/{encodedGuid}", async (string encodedGuid, HttpContext context) =>
{
    await hottiesSemaphore.WaitAsync();
    try
    {
        string guid = System.Web.HttpUtility.UrlDecode(encodedGuid);

        string theirName = "", theirInstrument = "";
        var cacheManager = context.RequestServices.GetRequiredService<JamulusCacheManager>();

        bool result = EncounterTracker.DetailsFromHash(
            guid, ref theirName, ref theirInstrument,
            JamulusCacheManager.JamulusListURLs,
            JamulusCacheManager.LastReportedList);


        if (EncounterTracker.m_timeTogether != null)
        {
            var hotties = new List<string>();
            var timeTogetherDescending = EncounterTracker.m_timeTogether.OrderByDescending(dude => dude.Value);

            foreach (var pair in timeTogetherDescending)
            {
                if (pair.Key.Contains(guid))
                {
                    var otherGuysGuid = pair.Key.Replace(guid, "");
                    string friendlyName = "", friendlyInstrument = "";
                    bool online = EncounterTracker.DetailsFromHash(
                        otherGuysGuid, ref friendlyName, ref friendlyInstrument,
                        JamulusCacheManager.JamulusListURLs,
                        JamulusCacheManager.LastReportedList);

                    if (online && pair.Value.TotalMinutes >= 10 && friendlyInstrument != "Listener" && friendlyName != "No Name" &&
                        friendlyName != "" && friendlyName != "Studio Bridge" &&
                        friendlyName != "Ear" && !friendlyName.Contains("obby"))
                        hotties.Add(otherGuysGuid);
                }
            }

            if (hotties.Count < 2) return "[]";
            const string QUOT = "\"";
            string ret = "[";
            for (int i = 0; i < hotties.Count / 2; i++)
                ret += QUOT + hotties[i] + QUOT + ", ";
            ret = ret.Substring(0, ret.Length - 2);
            ret += "]";
            return ret;
        }
        return "[]";
    }
    finally
    {
        hottiesSemaphore.Release();
    }
});

app.MapGet("/halos/", async (HttpContext context) =>
{
    string url = "https://jamulus.live/halo-streaming.txt";
    List<string> halostreaming = await JamulusCacheManager.LoadLinesFromHttpTextFile(url);

    if (halostreaming.Count == 0)
    {
        Console.WriteLine("halostreaming.txt is empty, maybe things are offline.");
        return "[]";
    }

    url = "https://jamulus.live/halo-snippeting.txt";
    List<string> halosnippeting = await JamulusCacheManager.LoadLinesFromHttpTextFile(url);

    string ret = "[";
    const string QUOT = "\"";
    for (int i = 0; i < halostreaming.Count; i++)  ret += QUOT + halostreaming[i]  + QUOT + ", ";
    for (int i = 0; i < halosnippeting.Count; i++) ret += QUOT + halosnippeting[i] + QUOT + ", ";
    ret = ret.Substring(0, ret.Length - 2);
    ret += "]";
    return ret;
});

app.MapGet("/stream", async (HttpContext context) =>
{
    string clientIP = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var xff = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    if (!string.IsNullOrEmpty(xff)) { clientIP = xff.Split(',')[0].Trim(); }
    if (!clientIP.Contains("::ffff")) clientIP = "::ffff:" + clientIP;

    bool isWeekly = context.Request.Query.ContainsKey("weekly");
    string message = await StreamGate.TryRequestStream(clientIP, isWeekly);

    Console.WriteLine($"[STREAM] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} {clientIP} weekly={isWeekly} => {message}");
    return Results.Text(message, "text/plain");
});

app.MapGet("/reset", (HttpContext context) =>
{
    string clientIP = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var xff = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    if (!string.IsNullOrEmpty(xff)) { clientIP = xff.Split(',')[0].Trim(); }
    if (!clientIP.Contains("::ffff")) clientIP = "::ffff:" + clientIP;

    string message = StreamGate.ResetStream(clientIP);

    Console.WriteLine($"[STREAM-RESET] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} {clientIP} => {message}");
    return Results.Text(message, "text/plain");
});

app.MapPost("/chat-url-server", async (HttpContext context) =>
{
    var req = await context.Request.ReadFromJsonAsync<ChatUrlRequest>();
    if (req == null || string.IsNullOrEmpty(req.url))
        return Results.BadRequest("missing url");

    var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var xff = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    if (!string.IsNullOrEmpty(xff)) remoteIp = xff.Split(',')[0].Trim();
    remoteIp = remoteIp.Replace("::ffff:", "");

    if (!FleetIpAllowlist.Contains(remoteIp))
    {
        Console.WriteLine($"[CHAT-URL-SERVER] rejected non-fleet caller ip={remoteIp}");
        return Results.StatusCode(403);
    }

    var server = req.port > 0 ? $"{remoteIp}:{req.port}" : remoteIp;

    if (!await JamFan22.harvest.UrlMatchesChatPatternsAsync(req.url))
    {
        Console.WriteLine($"[CHAT-URL] rejected url={req.url}");
        JamFan22.harvest.AppendRejectedLog(req.url, "server", server);
        return Results.Text("rejected", "text/plain");
    }

    Console.WriteLine($"[CHAT-URL] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} server={server} url={req.url}");
    await JamFan22.harvest.IngestChatUrlAsync(req.url, server, "server");
    return Results.Ok();
});
app.MapPost("/chat-url-client", async (HttpContext context) =>
{
    var req = await context.Request.ReadFromJsonAsync<ChatUrlRequest>();
    if (req == null || string.IsNullOrEmpty(req.url))
        return Results.BadRequest("missing url");

    var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var xff = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    if (!string.IsNullOrEmpty(xff)) remoteIp = xff.Split(',')[0].Trim();
    remoteIp = remoteIp.Replace("::ffff:", "");

    Console.WriteLine($"[CHAT-URL-CLIENT] {DateTime.UtcNow:HH:mm:ss} client={remoteIp} url={req.url}");

    var now = DateTime.UtcNow;
    _chatUrlClientRateLimit.AddOrUpdate(remoteIp,
        _ => (1, now),
        (_, prev) => now - prev.Window > TimeSpan.FromMinutes(1) ? (1, now) : (prev.Count + 1, prev.Window));
    if (_chatUrlClientRateLimit.TryGetValue(remoteIp, out var rl) && rl.Count > 3)
        return Results.Ok();

    bool allowed = await IpAnalyticsService.IsIpAllowedAsync(remoteIp);
    if (!allowed)
    {
        Console.WriteLine($"[CHAT-URL-CLIENT] BLOCKED ip={remoteIp} — ip-allowed gate rejected");
        return Results.Ok();
    }

    if (!await JamFan22.harvest.UrlMatchesChatPatternsAsync(req.url))
    {
        Console.WriteLine($"[CHAT-URL-CLIENT] rejected url={req.url}");
        JamFan22.harvest.AppendRejectedLog(req.url, "client", remoteIp);
        return Results.Ok();
    }

    // Resolve remoteIp -> guid -> server
    var guidStrengths = IdentityManager.GetGuidStrengths(remoteIp);
    Console.WriteLine($"[CHAT-URL-CLIENT] ip={remoteIp} has {guidStrengths.Count} known GUID(s) in join-events");
    foreach (var gs in guidStrengths.OrderByDescending(x => x.Value))
        Console.WriteLine($"[CHAT-URL-CLIENT]   guid={gs.Key} strength={gs.Value}");

    // Build map of currently-active guid -> serverAddress from live server data
    var activeGuidToServer = new Dictionary<string, string>();
    foreach (var svr in JamulusAnalyzer.m_allMyServers)
    {
        if (svr.whoObjectFromSourceData == null) continue;
        string serverAddr = svr.serverIpAddress + ":" + svr.serverPort;
        foreach (var c in svr.whoObjectFromSourceData)
        {
            string guid = EncounterTracker.GetHash(c.name, c.country, c.instrument);
            activeGuidToServer[guid] = serverAddr;
        }
    }
    Console.WriteLine($"[CHAT-URL-CLIENT] {activeGuidToServer.Count} active client slot(s) across all servers");

    // Find highest-strength GUID for this IP that is currently on a server
    string bestGuid = null;
    string bestServer = null;
    int bestStrength = -1;
    foreach (var gs in guidStrengths)
    {
        if (activeGuidToServer.TryGetValue(gs.Key, out var svrAddr))
        {
            Console.WriteLine($"[CHAT-URL-CLIENT]   ACTIVE guid={gs.Key} strength={gs.Value} server={svrAddr}");
            if (gs.Value > bestStrength)
            {
                bestStrength = gs.Value;
                bestGuid = gs.Key;
                bestServer = svrAddr;
            }
        }
        else
        {
            Console.WriteLine($"[CHAT-URL-CLIENT]   NOT-ACTIVE guid={gs.Key} strength={gs.Value}");
        }
    }

    if (!string.IsNullOrEmpty(req.serverAddr))
    {
        if (!req.serverAddr.Contains(':'))
        {
            Console.WriteLine($"[CHAT-URL-CLIENT] WARN client-supplied serverAddr={req.serverAddr} missing port — ignoring");
        }
        else
        {
            if (bestServer != null && bestServer != req.serverAddr)
                Console.WriteLine($"[CHAT-URL-CLIENT] client-supplied serverAddr={req.serverAddr} overrides inferred {bestServer} via guid={bestGuid}");
            else if (bestServer == null)
                Console.WriteLine($"[CHAT-URL-CLIENT] using client-supplied serverAddr={req.serverAddr} (no guid resolution)");
            bestServer = req.serverAddr;
        }
    }

    if (guidStrengths.Count == 0 && FleetGuidCache.GetGuidsByIp(remoteIp).Count == 0)
    {
        Console.WriteLine($"[CHAT-URL-CLIENT] REJECTED ip={remoteIp} — no identity (0 join-events, 0 fleet GUIDs)");
        return Results.Ok();
    }

    if (bestServer == null)
    {
        Console.WriteLine($"[CHAT-URL-CLIENT] No active server found for ip={remoteIp} — URL not stored");
        return Results.Ok();
    }

    Console.WriteLine($"[CHAT-URL-CLIENT] STORING url={req.url} for server={bestServer} via guid={bestGuid} strength={bestStrength}");
    await JamFan22.harvest.IngestChatUrlAsync(req.url, bestServer, "client");
    return Results.Ok();
});

app.MapPost("/api/hide", async (HttpContext context) =>
{
    var req = await context.Request.ReadFromJsonAsync<HideRequest>();
    string clientIP = context.Connection.RemoteIpAddress?.ToString() ?? "24.18.55.230";
    var xff = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    if (!string.IsNullOrEmpty(xff)) { clientIP = xff.Split(',')[0].Trim(); }
    if (!clientIP.Contains("::ffff")) clientIP = "::ffff:" + clientIP;

    var guids = JamFan22.IdentityManager.GetAllAssociatedGuids(clientIP);
    bool shouldHide = req?.hide ?? false;

    string actionText = shouldHide ? "HIDING" : "UNHIDING";
    Console.WriteLine($"\n[HIDE DIAGNOSTICS] {actionText} {guids.Count} GUID(s) for IP: {clientIP}");
    foreach(var g in guids)
    {
        Console.WriteLine($" -> {g}");
    }
    Console.WriteLine("--------------------------------------------------\n");

    JamFan22.HiddenPersonaManager.SetHidden(guids, shouldHide);
    return Results.Ok();
});

app.MapPost("/api/track", async (HttpContext context) =>
{
    string clientIP = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var xff = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    if (!string.IsNullOrEmpty(xff)) { clientIP = xff.Split(',')[0].Trim(); }
    if (!clientIP.Contains("::ffff")) clientIP = "::ffff:" + clientIP;

    using var reader = new System.IO.StreamReader(context.Request.Body);
    var rawBody = await reader.ReadToEndAsync();
    int nowMinute = JamFan22.Services.JamulusCacheManager.MinutesSince2023AsInt();

    if (nowMinute != _currentTelemetryMinute)
    {
        _seenEvents.Clear();
        _currentTelemetryMinute = nowMinute;
    }

    try
    {
        var payload = System.Text.Json.JsonSerializer.Deserialize<TelemetryPayload>(rawBody);
        if (payload != null && payload.events.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            lock (_telemetryLock)
            {
                foreach (var evt in payload.events.EnumerateArray())
                {
                    string rawEvent = evt.GetRawText();
                    string key = $"{clientIP}|{payload.h}|{rawEvent}";
                    if (_seenEvents.TryAdd(key, 1))
                    {
                        var line = $"{nowMinute},{clientIP},{payload.h},{payload.d},{payload.n},{rawEvent.Replace("\n", "")}";
                        System.IO.File.AppendAllText("data/telemetry.log", line + Environment.NewLine);
                    }
                }
            }
        }
    }
    catch (System.Text.Json.JsonException) { }

    return Results.Ok();
});

app.MapPost("/chat-command-server", async (HttpContext context) =>
{
    var req = await context.Request.ReadFromJsonAsync<ChatCommandRequest>();
    if (req == null || string.IsNullOrEmpty(req.command))
        return Results.BadRequest("missing command");

    var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var xff = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    if (!string.IsNullOrEmpty(xff)) remoteIp = xff.Split(',')[0].Trim();
    if (!remoteIp.Contains("::ffff:")) remoteIp = "::ffff:" + remoteIp;

    var bareIp = remoteIp.Replace("::ffff:", "");
    if (!FleetIpAllowlist.Contains(bareIp))
    {
        Console.WriteLine($"[CHAT-CMD] rejected non-fleet caller ip={bareIp}");
        return Results.StatusCode(403);
    }

    Console.WriteLine($"[CHAT-CMD] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} server={remoteIp} port={req.port} command={req.command}");
    string message = await StreamGate.TryRequestStream(remoteIp, false, req.port);
    return Results.Text(message, "text/plain");
});

// Fleet servers call this after CHANNEL_INFO arrives (player fully identified).
// Runs the welcome pipeline without coupling it to the allow/block decision.
// Query params: serverport=N&guid=G&channelId=C&nation=N
app.MapGet("/player-identified/{ip}", async (string ip, HttpContext context) =>
{
    string callerIP = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var xff = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    if (!string.IsNullOrEmpty(xff)) callerIP = xff.Split(',')[0].Trim();
    callerIP = callerIP.Replace("::ffff:", "");

    if (!FleetIpAllowlist.Contains(callerIP))
    {
        Console.WriteLine($"[PLAYER-IDENTIFIED] rejected non-fleet caller ip={callerIP}");
        return Results.StatusCode(403);
    }

    var guid = context.Request.Query["guid"].FirstOrDefault() ?? "";
    var nationCode = context.Request.Query["nation"].FirstOrDefault() ?? "";
    var serverPortStr = context.Request.Query["serverport"].FirstOrDefault();
    string serverKey = !string.IsNullOrEmpty(serverPortStr) ? $"{callerIP}:{serverPortStr}" : callerIP;
    var channelIdStr = context.Request.Query["channelId"].FirstOrDefault();
    if (!int.TryParse(channelIdStr, out int channelId)) channelId = -1;
    int rpcPort = FleetRpcPorts.GetPort(callerIP, serverPortStr ?? "");

    if (!string.IsNullOrEmpty(guid) && guid != "-")
        FleetGuidCache.UpsertGuid(guid, ip, serverKey, blocked: false);

    var capturedGuid = guid;
    var capturedNation = nationCode;
    var capturedChannelKey = $"channel:{callerIP}:{channelId}:{capturedGuid}";
    _ = Task.Run(async () =>
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string signals = "";
        string msg = WelcomeMessages.Get(capturedNation);
        bool rich = false;
        string llmStatus = "0";
        int inTok = 0, outTok = 0, cachedTok = 0;
        List<int> roomChannelIds = new();
        List<int> lobbyChannelIds = new();
        List<string> roomGuids = new();
        bool isGroupNoteworthy = false;
        bool hasWebUser = false;
        bool essayProduced = false;
        string essayLang = "";
        string? groupContextText = null;
        System.Collections.Generic.Dictionary<string, string>? groupNameColors = null;
        string wsKey = !string.IsNullOrEmpty(serverPortStr) ? $"{callerIP}:{serverPortStr}" : "";
        var rpcOpts = new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        Func<Task<string?>>? wsGC = wsKey.Length > 0 && _fleetWsRegistry.TryGetValue(wsKey, out var earlyFleetWs)
            && earlyFleetWs.State == System.Net.WebSockets.WebSocketState.Open
            ? () => FleetWsRpcCallAsync(wsKey, earlyFleetWs, "jamulusserver/getClients", new { }, rpcOpts)
            : null;
        try
        {
            if (WelcomeCache.TryGet(capturedChannelKey, out _))
            {
                Console.WriteLine($"[PLAYER-IDENTIFIED-WELCOME] suppressed flag-change re-welcome caller={callerIP} channelId={channelId}");
                return;
            }

            string cacheKey = $"{capturedGuid}:{serverKey}";
            if (WelcomeCache.TryGet(cacheKey, out _))
            {
                Console.WriteLine($"[PLAYER-IDENTIFIED-WELCOME] suppressed rapid re-join caller={callerIP} channelId={channelId}");
                return;
            }
            {
                var gatherResult = await WelcomeContext.GatherAsync(capturedGuid, serverKey, rpcPort, capturedNation, channelId, ip, wsGC);
                var contextText = gatherResult.Context;
                var nameColors = gatherResult.NameColors;
                roomChannelIds = gatherResult.RoomChannelIds;
                lobbyChannelIds = gatherResult.LobbyChannelIds ?? new();
                roomGuids = gatherResult.RoomGuids ?? new List<string>();
                isGroupNoteworthy = gatherResult.IsGroupNoteworthy;
                hasWebUser = gatherResult.HasWebUser;
                if (isGroupNoteworthy && gatherResult.ArrivingName.Length > 0)
                    GroupJoinerAccumulator.Add(serverKey, gatherResult.ArrivingName, capturedNation, gatherResult.ArrivingInstrument, gatherResult.ArrivingDistKm);
                groupContextText = System.Text.RegularExpressions.Regex.Replace(contextText,
                    @"Language to use for message: \S+",
                    $"Language to use for message: {gatherResult.RoomLanguage}");
                groupNameColors = nameColors;
                signals = WelcomeContext.TakeSignals(capturedGuid);
                rich = WelcomeContext.IsRich(contextText);

                // Limited-time joiner-centered "See your destiny" essay for TH/HK servers.
                // Fires whether or not others are present — the essay leans on the joiner's
                // shared history with whoever is already in the room.
                if (DormantEssayFeature.IsActive
                    && DormantEssayFeature.IsEligibleCountry(gatherResult.ServerCountryCode)
                    && DormantEssayFeature.TryReserve())
                {
                    essayLang = WelcomeContext.EssayLanguage(capturedNation, gatherResult.ServerCountryCode);
                    var (em, es, ei, eo, ec) = await WelcomeMessageGenerator.GetEssayAsync(
                        contextText, essayLang, capturedNation, nameColors, gatherResult.NameEmojis, gatherResult.EventNames);
                    inTok = ei; outTok = eo; cachedTok = ec;
                    if (em != null && em.Length >= 40)
                    {
                        msg = em; rich = true; essayProduced = true; llmStatus = "essay";
                        signals = signals is "" or "none" ? "essay-dormant" : signals + "|essay-dormant";
                        Console.WriteLine($"[DORMANT-ESSAY] produced #{DormantEssayFeature.Produced}/29 lang={essayLang} country={gatherResult.ServerCountryCode} server={serverKey} caller={callerIP}");
                    }
                    else
                    {
                        DormantEssayFeature.Release();
                        Console.WriteLine($"[DORMANT-ESSAY] release — status={(em == null ? es : "short")} server={serverKey} caller={callerIP}");
                    }
                }

                if (!essayProduced && rich)
                {
                    var (llm, llmErr, i, o, c) = await WelcomeMessageGenerator.GetAsync(contextText, capturedNation, nameColors, gatherResult.NameEmojis, gatherResult.EventNames,
                        usePro: WelcomeContext.IsLoreGuest(serverKey, gatherResult.ArrivingName, capturedGuid));
                    if (llm != null && o > 0 && o < 15)
                    {
                        // Dud: rich context but trivially short output — static fallback beats it
                        Console.WriteLine($"[PLAYER-IDENTIFIED-WELCOME] llm-short out={o} — static fallback caller={callerIP} channelId={channelId}");
                        llmStatus = "short";
                    }
                    else if (llm != null) { msg = llm; llmStatus = "1"; }
                    else llmStatus = llmErr.Length > 0 ? llmErr : "error";
                    inTok = i; outTok = o; cachedTok = c;
                }
                WelcomeCache.Set(cacheKey, msg);
            }
            if (essayProduced)
            {
                // "See your destiny" banner in the essay's language — TH/HK only.
                msg = $"<br><big>{WelcomeMessageGenerator.DestinyHeader(essayLang)}</big><br>{msg}";
            }
            else
            {
                var (svcName, _) = WelcomeContext.LookupServer(serverKey);
                string headerLabel = svcName.Length > 0 ? svcName : serverKey;
                msg = $"<br><big>{WelcomeMessages.YouveJoined(capturedNation)} {headerLabel}</big><br>{msg}";
            }
            if (wsKey.Length > 0 && _fleetWsRegistry.TryGetValue(wsKey, out var fleetWs)
                && fleetWs.State == System.Net.WebSockets.WebSocketState.Open)
            {
                try
                {
                    bool stillPresent = false;
                    string? clientsResp = await FleetWsRpcCallAsync(wsKey, fleetWs, "jamulusserver/getClients", new { }, rpcOpts);
                    if (clientsResp != null)
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(clientsResp);
                        if (doc.RootElement.TryGetProperty("result", out var res)
                            && res.TryGetProperty("clients", out var clients)
                            && clients.ValueKind == System.Text.Json.JsonValueKind.Array)
                            foreach (var c in clients.EnumerateArray())
                                if (c.TryGetProperty("id", out var cid) && cid.GetInt32() == channelId)
                                { stillPresent = true; break; }
                    }
                    if (clientsResp == null)
                    {
                        Console.WriteLine($"[PLAYER-IDENTIFIED-WELCOME] ws-rpc-failed — sending anyway caller={callerIP} channelId={channelId}");
                    }
                    else if (!stillPresent)
                    {
                        Console.WriteLine($"[PLAYER-IDENTIFIED-WELCOME] skipped — departed before send caller={callerIP} channelId={channelId}");
                        return;
                    }
                    var sendResp = await FleetWsRpcCallAsync(wsKey, fleetWs, "jamulusserver/sendClientChatMessage", new { channelId, message = msg }, rpcOpts);
                    WelcomeCache.Set(capturedChannelKey, "", 1);
                    if (sendResp == null)
                        Console.WriteLine($"[PLAYER-IDENTIFIED-WELCOME] ws-send-failed caller={callerIP} channelId={channelId} nation={capturedNation}");
                    else
                        Console.WriteLine($"[PLAYER-IDENTIFIED-WELCOME] ws caller={callerIP} channelId={channelId} nation={capturedNation}");
                }
                catch (Exception wsEx)
                { Console.WriteLine($"[PLAYER-IDENTIFIED-WELCOME] ws-error caller={callerIP} channelId={channelId}: {wsEx.Message}"); }
            }
            else
            {
                Console.WriteLine($"[PLAYER-IDENTIFIED-WELCOME] no-ws caller={callerIP} channelId={channelId} rpcport={rpcPort} — skipped (no WebSocket)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PLAYER-IDENTIFIED-WELCOME] caller={callerIP} channelId={channelId} rpcport={rpcPort} error={ex.ToString()}");
        }
        finally
        {
            sw.Stop();
            WelcomeEventLog.Append(serverKey, capturedNation, rich, llmStatus, signals, sw.ElapsedMilliseconds, msg, inTok, outTok, cachedTok);
        }

        string groupCacheKey = $"group:{serverKey}";
        if (isGroupNoteworthy && roomChannelIds.Count > 0 && groupContextText != null
            && !WelcomeCache.TryGet(groupCacheKey, out _))
        {
            WelcomeCache.Set(groupCacheKey, "", 5);
            Console.WriteLine($"[PLAYER-IDENTIFIED-GROUP] t={JamFan22.Services.JamulusCacheManager.MinutesSince2023AsInt()} caller={callerIP} noteworthy=1 roomIds={roomChannelIds.Count} — sending group message");
            await Task.Delay(60000);
            var batchJoiners = GroupJoinerAccumulator.Drain(serverKey);
            if (batchJoiners.Count > 1 && groupContextText != null)
            {
                var arrivals = string.Join("\n", batchJoiners.Select(j =>
                    $"- {j.Name}{(j.Instrument.Length > 0 ? $" ({j.Instrument})" : "")}, {j.Nation}{(j.DistKm > 0 ? $", ~{j.DistKm}km away" : "")}"));
                groupContextText += $"\n\nRECENT ARRIVALS (all joined in the last 60 seconds):\n{arrivals}";
            }
            // One and done: if the share/listen link already went out to this server in a private
            // welcome (or a prior group) inside the 20-min quiet window, strip every stream/link
            // instruction from this room broadcast so nobody sees it twice. Otherwise this group
            // opens the window itself.
            if (WelcomeCache.TryGet($"ear-link:{serverKey}", out _))
            {
                groupContextText = System.Text.RegularExpressions.Regex.Replace(groupContextText,
                    @"(?im)^.*(?:Share/record|Include this line|streaming live right now|Stream starts in|/stream|Stream slot|Lobby client connected).*$\n?", "");
                groupContextText += "\n[NO-URLS: the share/listen link already went out privately — omit streaming links and /stream. Just announce the arrival(s).]";
            }
            else
                WelcomeCache.Set($"ear-link:{serverKey}", "", 20);
            var groupSw = System.Diagnostics.Stopwatch.StartNew();
            string? groupMsg = null;
            try { groupMsg = await WelcomeMessageGenerator.GetGroupAsync(groupContextText, groupNameColors, hasWebUser); }
            catch { }
            if (groupMsg != null)
            {
                var (svcName2, _) = WelcomeContext.LookupServer(serverKey);
                string speaker = svcName2.Length > 0 ? svcName2 : serverKey;
                string encodedSpeaker = System.Web.HttpUtility.HtmlEncode(speaker);
                string groupBody = groupMsg.Replace("<p>", "").Replace("</p>", "").Trim();
                string dormantNote = "";
                string dormantCacheKey = $"dormant-note:{serverKey}";
                if (FleetDormantServers.Contains(serverKey) && !WelcomeCache.TryGet(dormantCacheKey, out _))
                {
                    dormantNote = " This server appears during peak hours.";
                    WelcomeCache.Set(dormantCacheKey, "", 20);
                }
                string groupFull = $"<br><b><font color=\"#4FC3F7\">{encodedSpeaker}:</font></b> {groupBody}{dormantNote}";
                var allIds = roomChannelIds.Concat(new[] { channelId }).Concat(lobbyChannelIds).Distinct().ToList();
                if (wsKey.Length > 0 && _fleetWsRegistry.TryGetValue(wsKey, out var groupWs)
                    && groupWs.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    try
                    {
                        bool groupStillPresent = false;
                        string? gcResp = await FleetWsRpcCallAsync(wsKey, groupWs, "jamulusserver/getClients", new { }, rpcOpts);
                        if (gcResp != null)
                        {
                            using var gDoc = System.Text.Json.JsonDocument.Parse(gcResp);
                            if (gDoc.RootElement.TryGetProperty("result", out var gRes)
                                && gRes.TryGetProperty("clients", out var gClients)
                                && gClients.ValueKind == System.Text.Json.JsonValueKind.Array)
                                foreach (var gc in gClients.EnumerateArray())
                                    if (gc.TryGetProperty("id", out var gcid) && gcid.GetInt32() == channelId)
                                    { groupStillPresent = true; break; }
                        }
                        if (!groupStillPresent)
                        {
                            Console.WriteLine($"[PLAYER-IDENTIFIED-GROUP] skipped — departed before group send caller={callerIP} channelId={channelId}");
                            WelcomeEventLog.Append($"group:{serverKey}", capturedNation, true, "skipped", "group", groupSw.ElapsedMilliseconds, "(skipped: departed before send)");
                        }
                        else
                        {
                            foreach (var rcId in allIds)
                                await FleetWsRpcCallAsync(wsKey, groupWs, "jamulusserver/sendClientChatMessage", new { channelId = rcId, message = groupFull }, rpcOpts);
                            Console.WriteLine($"[PLAYER-IDENTIFIED-GROUP] ws t={JamFan22.Services.JamulusCacheManager.MinutesSince2023AsInt()} caller={callerIP} speaker={speaker} recipients={allIds.Count} msg={groupMsg}");
                            WelcomeEventLog.Append($"group:{serverKey}", capturedNation, true, "1", $"recipients:{allIds.Count}", groupSw.ElapsedMilliseconds, groupFull);
                        }
                    }
                    catch (Exception wsEx)
                    {
                        Console.WriteLine($"[PLAYER-IDENTIFIED-GROUP] ws-error caller={callerIP}: {wsEx.Message}");
                        WelcomeEventLog.Append($"group:{serverKey}", capturedNation, true, "error", "group", groupSw.ElapsedMilliseconds, $"(ws-error: {wsEx.Message})");
                    }
                }
                else
                {
                    Console.WriteLine($"[PLAYER-IDENTIFIED-GROUP] t={JamFan22.Services.JamulusCacheManager.MinutesSince2023AsInt()} no-ws caller={callerIP} — skipped");
                    WelcomeEventLog.Append($"group:{serverKey}", capturedNation, true, "no-ws", "group", groupSw.ElapsedMilliseconds, "(no websocket)");
                }
            }
            else if (groupMsg == null)
            {
                Console.WriteLine($"[PLAYER-IDENTIFIED-GROUP] t={JamFan22.Services.JamulusCacheManager.MinutesSince2023AsInt()} caller={callerIP} groupMsg=null (LLM returned nothing)");
                WelcomeEventLog.Append($"group:{serverKey}", capturedNation, false, "0", "group", groupSw.ElapsedMilliseconds, "(LLM returned nothing)");
            }
        }
    });

    return Results.Ok();
});

app.Run();

// Fleet server IP allowlist — data/fleet-server-ips.txt, one IP per line, # = comment.
// 5-minute TTL so adding a new fleet server takes effect without a restart.
public static class FleetIpAllowlist
{
    private static (HashSet<string> Ips, DateTime Expiry) _cache = (new HashSet<string>(), DateTime.MinValue);
    private static readonly object _lock = new object();

    public static bool Contains(string ip)
    {
        string bare = ip.Replace("::ffff:", "").Trim();
        lock (_lock)
        {
            if (DateTime.UtcNow >= _cache.Expiry)
            {
                var ips = new HashSet<string>(StringComparer.Ordinal);
                if (File.Exists("data/fleet-server-ips.txt"))
                    foreach (var line in File.ReadAllLines("data/fleet-server-ips.txt"))
                    { var t = line.Trim(); if (t.Length > 0 && !t.StartsWith('#')) { var colon = t.IndexOf(':'); ips.Add(colon >= 0 ? t[..colon] : t); } }
                _cache = (ips, DateTime.UtcNow.AddMinutes(5));
            }
            return _cache.Ips.Contains(bare);
        }
    }
}

// Dormant fleet servers — data/fleet-dormant-ips.txt, written by dormant-monitor.py.
// One ip:port per line. 1-minute TTL matches the monitor's write cadence.
public static class FleetDormantServers
{
    private static (HashSet<string> Keys, DateTime Expiry) _cache = (new(), DateTime.MinValue);
    private static readonly object _lock = new object();

    public static bool Contains(string serverKey)
    {
        lock (_lock)
        {
            if (DateTime.UtcNow >= _cache.Expiry)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                if (File.Exists("data/fleet-dormant-ips.txt"))
                    foreach (var line in File.ReadAllLines("data/fleet-dormant-ips.txt"))
                    { var t = line.Trim(); if (t.Length > 0 && !t.StartsWith('#')) keys.Add(t); }
                _cache = (keys, DateTime.UtcNow.AddMinutes(1));
            }
            return _cache.Keys.Contains(serverKey);
        }
    }
}

// Limited-time "See your destiny" joiner-centered essay welcome for dormant Thailand (Thai)
// and Hong Kong (Chinese) servers. Self-expiring by two independent backstops, whichever comes
// first: a hardcoded conclusion instant, and a hard RAM cap of 29 essays produced (resets on
// reboot). After either fires, the feature is off forever — joiners get the normal welcome.
public static class DormantEssayFeature
{
    // Revived 2026-08-10 for a 7-day encore (originally concluded 2026-08-05). Automatic, no config needed.
    private static readonly DateTime Expiry = new(2026, 8, 17, 23, 59, 59, DateTimeKind.Utc);
    private const int MaxEssays = 29;
    private static int _produced;   // essays successfully generated since process start

    public static int Produced => Volatile.Read(ref _produced);
    public static bool IsActive => DateTime.UtcNow < Expiry && Volatile.Read(ref _produced) < MaxEssays;
    // 2026-08-10 encore: Thailand only (HK dropped for this revival).
    public static bool IsEligibleCountry(string countryCode) =>
        countryCode == "TH";

    // Atomically claim one of the 29 slots. Returns false if the window has closed or the cap
    // is reached. Release() must be called if generation then fails, to give the slot back.
    public static bool TryReserve()
    {
        if (DateTime.UtcNow >= Expiry) return false;
        if (Interlocked.Increment(ref _produced) <= MaxEssays) return true;
        Interlocked.Decrement(ref _produced);
        return false;
    }

    public static void Release() => Interlocked.Decrement(ref _produced);
}

// Fleet server RPC port map — reads from data/fleet-rpc-ports.txt (ip=port format).
// Default 9999. 5-minute TTL.
public static class FleetRpcPorts
{
    private static (Dictionary<string, int> Map, DateTime Expiry) _cache = (new(), DateTime.MinValue);
    private static readonly object _lock = new object();

    public static int GetPort(string serverIp, string serverPort = "")
    {
        string bare = serverIp.Replace("::ffff:", "").Trim();
        lock (_lock)
        {
            if (DateTime.UtcNow >= _cache.Expiry)
            {
                var map = new Dictionary<string, int>(StringComparer.Ordinal);
                if (File.Exists("data/fleet-rpc-ports.txt"))
                    foreach (var line in File.ReadAllLines("data/fleet-rpc-ports.txt"))
                    {
                        var t = line.Trim();
                        if (t.Length == 0 || t.StartsWith('#')) continue;
                        var parts = t.Split('=');
                        if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out int port))
                            map[parts[0].Trim()] = port;
                    }
                _cache = (map, DateTime.UtcNow.AddMinutes(5));
            }
            string portKey = serverPort.Length > 0 ? $"{bare}:{serverPort}" : "";
            if (portKey.Length > 0 && _cache.Map.TryGetValue(portKey, out int rpcPortByKey)) return rpcPortByKey;
            return _cache.Map.TryGetValue(bare, out int rpcPort) ? rpcPort : 9999;
        }
    }
}

public static class WelcomeMessages
{
    private static readonly Dictionary<string, string> _nationToLang = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DE"] = "de", ["AT"] = "de", ["CH"] = "de",
        ["FR"] = "fr", ["BE"] = "fr", ["LU"] = "fr",
        ["ES"] = "es", ["MX"] = "es", ["AR"] = "es", ["CL"] = "es",
        ["CO"] = "es", ["PE"] = "es", ["VE"] = "es", ["EC"] = "es",
        ["BO"] = "es", ["UY"] = "es", ["PY"] = "es", ["PA"] = "es",
        ["CR"] = "es", ["GT"] = "es", ["HN"] = "es",
        ["SV"] = "es", ["NI"] = "es", ["DO"] = "es", ["CU"] = "es",
        ["BR"] = "pt", ["PT"] = "pt",
        ["IT"] = "it",
        ["NL"] = "nl",
        ["JP"] = "ja",
        ["KR"] = "ko",
        ["CN"] = "zh", ["TW"] = "zh", ["HK"] = "zh",
        ["TH"] = "th",
        ["SE"] = "sv",
        ["PL"] = "pl",
        ["RU"] = "ru",
        ["PH"] = "tl",
        ["TR"] = "tr",
        ["UA"] = "uk",
        ["FI"] = "fi",
        ["NO"] = "no",
        ["IN"] = "hi",
        ["GR"] = "el",
        ["RO"] = "ro",
        ["HU"] = "hu",
        ["DK"] = "da",
        ["SK"] = "sk", ["CZ"] = "cs",
        ["HR"] = "hr", ["SI"] = "sl", ["RS"] = "sr", ["BG"] = "bg",
        ["LT"] = "lt", ["LV"] = "lv", ["EE"] = "et", ["IS"] = "is",
        ["IL"] = "he", ["VN"] = "vi", ["ID"] = "id", ["MY"] = "ms", ["BN"] = "ms", ["BD"] = "bn",
        ["SA"] = "ar", ["EG"] = "ar", ["AE"] = "ar", ["MA"] = "ar", ["DZ"] = "ar", ["IQ"] = "ar",
        ["LI"] = "de", ["MC"] = "fr", ["SM"] = "it", ["VA"] = "it",
        ["AO"] = "pt", ["MZ"] = "pt", ["BY"] = "ru", ["KZ"] = "ru",
    };

    private static readonly Dictionary<string, string> _messages = new()
    {
        ["en"] = "<a href='https://jamulus.live'>https://jamulus.live</a> shows more.",
        ["de"] = "<a href='https://jamulus.live'>https://jamulus.live</a> zeigt mehr.",
        ["fr"] = "<a href='https://jamulus.live'>https://jamulus.live</a> en montre plus.",
        ["es"] = "<a href='https://jamulus.live'>https://jamulus.live</a> muestra más.",
        ["pt"] = "<a href='https://jamulus.live'>https://jamulus.live</a> mostra mais.",
        ["it"] = "<a href='https://jamulus.live'>https://jamulus.live</a> mostra di più.",
        ["nl"] = "<a href='https://jamulus.live'>https://jamulus.live</a> toont meer.",
        ["ja"] = "<a href='https://jamulus.live'>https://jamulus.live</a> で詳細を確認。",
        ["ko"] = "<a href='https://jamulus.live'>https://jamulus.live</a> 에서 더 보기.",
        ["zh"] = "<a href='https://jamulus.live'>https://jamulus.live</a> 显示更多。",
        ["th"] = "<a href='https://jamulus.live'>https://jamulus.live</a> แสดงเพิ่มเติม",
        ["sv"] = "<a href='https://jamulus.live'>https://jamulus.live</a> visar mer.",
        ["pl"] = "<a href='https://jamulus.live'>https://jamulus.live</a> pokazuje więcej.",
        ["ru"] = "<a href='https://jamulus.live'>https://jamulus.live</a> покажет больше.",
        ["tl"] = "<a href='https://jamulus.live'>https://jamulus.live</a> nagpapakita ng higit pa.",
        ["tr"] = "<a href='https://jamulus.live'>https://jamulus.live</a> daha fazlasını gösterir.",
        ["uk"] = "<a href='https://jamulus.live'>https://jamulus.live</a> показує більше.",
        ["fi"] = "<a href='https://jamulus.live'>https://jamulus.live</a> näyttää lisää.",
        ["no"] = "<a href='https://jamulus.live'>https://jamulus.live</a> viser mer.",
        ["hi"] = "<a href='https://jamulus.live'>https://jamulus.live</a> अधिक दिखाता है।",
        ["el"] = "<a href='https://jamulus.live'>https://jamulus.live</a> δείχνει περισσότερα.",
        ["ro"] = "<a href='https://jamulus.live'>https://jamulus.live</a> arată mai mult.",
        ["hu"] = "<a href='https://jamulus.live'>https://jamulus.live</a> többet mutat.",
        ["da"] = "<a href='https://jamulus.live'>https://jamulus.live</a> viser mere.",
        ["sk"] = "<a href='https://jamulus.live'>https://jamulus.live</a> ukazuje viac.",
        ["cs"] = "<a href='https://jamulus.live'>https://jamulus.live</a> ukazuje více.",
        ["hr"] = "<a href='https://jamulus.live'>https://jamulus.live</a> prikazuje više.",
        ["sl"] = "<a href='https://jamulus.live'>https://jamulus.live</a> prikazuje več.",
        ["sr"] = "<a href='https://jamulus.live'>https://jamulus.live</a> prikazuje više.",
        ["bg"] = "<a href='https://jamulus.live'>https://jamulus.live</a> показва повече.",
        ["lt"] = "<a href='https://jamulus.live'>https://jamulus.live</a> rodo daugiau.",
        ["lv"] = "<a href='https://jamulus.live'>https://jamulus.live</a> rāda vairāk.",
        ["et"] = "<a href='https://jamulus.live'>https://jamulus.live</a> näitab rohkem.",
        ["is"] = "<a href='https://jamulus.live'>https://jamulus.live</a> sýnir meira.",
        ["he"] = "<a href='https://jamulus.live'>https://jamulus.live</a> מציג עוד.",
        ["ar"] = "<a href='https://jamulus.live'>https://jamulus.live</a> يعرض المزيد.",
        ["vi"] = "<a href='https://jamulus.live'>https://jamulus.live</a> hiển thị thêm.",
        ["id"] = "<a href='https://jamulus.live'>https://jamulus.live</a> menampilkan lebih banyak.",
        ["ms"] = "<a href='https://jamulus.live'>https://jamulus.live</a> menunjukkan lebih banyak.",
        ["bn"] = "<a href='https://jamulus.live'>https://jamulus.live</a> আরও দেখায়।",
    };

    private static readonly Dictionary<string, string> _youveJoined = new()
    {
        ["en"] = "You've joined",
        ["de"] = "Du bist beigetreten:",
        ["fr"] = "Vous avez rejoint",
        ["es"] = "Te has unido a",
        ["pt"] = "Você entrou em",
        ["it"] = "Sei entrato in",
        ["nl"] = "Je bent toegetreden tot",
        ["ja"] = "参加しました：",
        ["ko"] = "입장했습니다:",
        ["zh"] = "你加入了",
        ["th"] = "คุณเข้าร่วม",
        ["sv"] = "Du har gått med i",
        ["pl"] = "Dołączyłeś do",
        ["ru"] = "Вы вошли в",
        ["tl"] = "Sumali ka sa",
        ["tr"] = "Katıldınız:",
        ["uk"] = "Ви приєдналися до",
        ["fi"] = "Liityit palveluun",
        ["no"] = "Du ble med i",
        ["hi"] = "आप शामिल हो गए",
        ["el"] = "Μπήκατε στο",
        ["ro"] = "Te-ai alăturat la",
        ["hu"] = "Csatlakoztál:",
        ["da"] = "Du er tilsluttet",
        ["sk"] = "Pripojili ste sa k",
        ["cs"] = "Připojili jste se k",
        ["hr"] = "Pridružili ste se",
        ["sl"] = "Pridružili ste se",
        ["sr"] = "Pridružili ste se",
        ["bg"] = "Присъединихте се към",
        ["lt"] = "Prisijungėte prie",
        ["lv"] = "Jūs pievienojāties",
        ["et"] = "Liitusite serveriga",
        ["is"] = "Þú hefur gengið í",
        ["he"] = "הצטרפת אל",
        ["ar"] = "لقد انضممت إلى",
        ["vi"] = "Bạn đã tham gia",
        ["id"] = "Anda telah bergabung dengan",
        ["ms"] = "Anda telah menyertai",
        ["bn"] = "আপনি যোগ দিয়েছেন",
    };

    public static string YouveJoined(string nation)
    {
        string lang = _nationToLang.TryGetValue(nation ?? "", out var l) ? l : "en";
        return _youveJoined.TryGetValue(lang, out var v) ? v : _youveJoined["en"];
    }

    public static string Get(string nation)
    {
        string lang = _nationToLang.TryGetValue(nation ?? "", out var l) ? l : "en";
        return _messages.TryGetValue(lang, out var v) ? v : _messages["en"];
    }
}

public static class GroupJoinerAccumulator
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentBag<(string Name, string Nation, string Instrument, int DistKm)>> _bags = new();

    public static void Add(string serverKey, string name, string nation, string instrument, int distKm)
        => _bags.GetOrAdd(serverKey, _ => new()).Add((name, nation, instrument, distKm));

    public static List<(string Name, string Nation, string Instrument, int DistKm)> Drain(string serverKey)
        => _bags.TryRemove(serverKey, out var bag) ? bag.ToList() : new();
}

public static class WelcomeCache
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Expiry, string Message)> _cache = new();
    private const int DefaultMinutes = 5;

    public static bool TryGet(string key, out string message)
    {
        if (_cache.TryGetValue(key, out var entry) && entry.Expiry > DateTime.UtcNow)
        { message = entry.Message; return true; }
        message = "";
        return false;
    }

    public static void Set(string key, string message, int minutes = DefaultMinutes) =>
        _cache[key] = (DateTime.UtcNow.AddMinutes(minutes), message);
}

public class ChatUrlRequest
{
    public string url { get; set; }
    public int port { get; set; }
    public string serverAddr { get; set; }
}

public class HideRequest
{
    public bool hide { get; set; }
}

public class ChatCommandRequest
{
    public string command { get; set; }
    public int port { get; set; }
    public bool weekly { get; set; }
}

public class TelemetryPayload
{
    public string h { get; set; }
    public int d { get; set; }
    public int n { get; set; }
    public System.Text.Json.JsonElement events { get; set; }
}


