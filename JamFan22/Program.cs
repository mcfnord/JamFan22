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

// Fleet GUID-IP cache lives in FleetGuidCache (static class, accessible from IdentityManager)

Console.WriteLine($"[STARTUP] JamFan22 starting. PID={Environment.ProcessId} Time={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
FleetGuidCache.HydrateFromCsv();   // sync — loads fleet GUID cache from fleet-guid-ip.csv
StreamRequestManager.Load();        // sync — loads stream-requests.json
StreamGate.Load();                  // sync — loads stream-gate.json (current lease)
_ = StreamGate.PostLeaseMonitorAsync(); // background — watches for lease expiry
WelcomeMessageGenerator.LoadApiKey();   // sync — reads data/gemini-key.txt
DailyEssayService.LoadApiKey();         // sync — same key
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
        serverOptions.ListenAnyIP(port, listenOptions => listenOptions.UseHttps("keyApr26.pfx", "jamfan"));
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

app.UseRouting();
app.UseAuthorization();
app.MapRazorPages();
app.MapHub<JamFan22.ChatHub>("/chathub");

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
    string? llmMessage = rich ? (await WelcomeMessageGenerator.GetAsync(ctx, nation, nameColors)).Message : null;
    string? englishMessage = null;
    if (rich && nation != "US")
    {
        var ctxEn = System.Text.RegularExpressions.Regex.Replace(ctx,
            @"Language to use for message: \S+", "Language to use for message: English");
        englishMessage = (await WelcomeMessageGenerator.GetAsync(ctxEn, "US", nameColors)).Message;
    }
    WelcomeEventLog.Append(serverKey, nation, rich, llmMessage != null ? "1" : "0", signals, 0, llmMessage ?? WelcomeMessages.Get(nation));

    return Results.Json(new
    {
        context = ctx,
        signals,
        rich,
        llmMessage,
        english = englishMessage,
        fallback = WelcomeMessages.Get(nation),
        usingLlm = llmMessage != null
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
    var htmlTask  = finder.FindMusiciansHtmlAsync(clientIP);
    var delayTask = DailyEssayService.GetEssayDelaySecondsAsync(clientIP);
    await Task.WhenAll(htmlTask, delayTask);
    context.Response.Headers["X-Essay-Delay"] = delayTask.Result.ToString();
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
    var html = await DailyEssayService.GetEssayHtmlAsync(clientIP);
    if (html == null) return Results.NoContent();
    return Results.Content(html, "text/html");
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
        else if (bestServer == null)
        {
            bestServer = req.serverAddr;
            Console.WriteLine($"[CHAT-URL-CLIENT] using client-supplied serverAddr={bestServer} (no guid resolution)");
        }
        else if (bestServer != req.serverAddr)
        {
            Console.WriteLine($"[CHAT-URL-CLIENT] WARN serverAddr mismatch ignored: client says {req.serverAddr}, keeping inferred {bestServer} via guid={bestGuid}");
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
    int rpcPort = FleetRpcPorts.GetPort(callerIP);

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
        List<int> roomChannelIds = new();
        bool isGroupNoteworthy = false;
        bool hasWebUser = false;
        string? groupContextText = null;
        System.Collections.Generic.Dictionary<string, string>? groupNameColors = null;
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
                var gatherResult = await WelcomeContext.GatherAsync(capturedGuid, serverKey, rpcPort, capturedNation, channelId, ip);
                var contextText = gatherResult.Context;
                var nameColors = gatherResult.NameColors;
                roomChannelIds = gatherResult.RoomChannelIds;
                isGroupNoteworthy = gatherResult.IsGroupNoteworthy;
                hasWebUser = gatherResult.HasWebUser;
                groupContextText = System.Text.RegularExpressions.Regex.Replace(contextText,
                    @"Language to use for message: \S+",
                    $"Language to use for message: {gatherResult.RoomLanguage}");
                groupNameColors = nameColors;
                signals = WelcomeContext.TakeSignals(capturedGuid);
                rich = WelcomeContext.IsRich(contextText);
                if (rich)
                {
                    var (llm, llmErr) = await WelcomeMessageGenerator.GetAsync(contextText, capturedNation, nameColors);
                    if (llm != null) { msg = llm; llmStatus = "1"; }
                    else llmStatus = llmErr.Length > 0 ? llmErr : "error";
                }
                WelcomeCache.Set(cacheKey, msg);
            }
            var (svcName, _) = WelcomeContext.LookupServer(serverKey);
            string headerLabel = svcName.Length > 0 ? svcName : serverKey;
            msg = $"<br><big>{WelcomeMessages.YouveJoined(capturedNation)} {headerLabel}</big>{msg}";
            // Fleet JSON-RPC: plain TCP, newline-delimited JSON, two messages per session.
            // 1. apiAuth with /secret.txt  2. sendClientChatMessage  — read response after each send.
            // Port 9999 default; see data/fleet-rpc-ports.txt for overrides. Secret same on all fleet servers.
            string secret = (await System.IO.File.ReadAllTextAsync("/secret.txt")).Trim();
            var rpcOpts = new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            using var tcp = new System.Net.Sockets.TcpClient();
            using var tcpCts = new System.Threading.CancellationTokenSource(5000);
            await tcp.ConnectAsync(callerIP, rpcPort, tcpCts.Token);
            await using var stream = tcp.GetStream();
            await using var writer = new System.IO.StreamWriter(stream, leaveOpen: true) { AutoFlush = true };
            using var reader = new System.IO.StreamReader(stream, leaveOpen: true);
            await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new { id = 1, jsonrpc = "2.0", method = "jamulus/apiAuth", @params = new { secret } }, rpcOpts).AsMemory(), tcpCts.Token);
            await reader.ReadLineAsync(tcpCts.Token);
            await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new { id = 2, jsonrpc = "2.0", method = "jamulusserver/getClients", @params = new { } }, rpcOpts).AsMemory(), tcpCts.Token);
            string? clientsJson = await reader.ReadLineAsync(tcpCts.Token);
            bool stillPresent = false;
            if (clientsJson != null)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(clientsJson);
                if (doc.RootElement.TryGetProperty("result", out var res) &&
                    res.TryGetProperty("clients", out var clients) &&
                    clients.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var c in clients.EnumerateArray())
                    {
                        if (c.TryGetProperty("id", out var cid) && cid.GetInt32() == channelId)
                        { stillPresent = true; break; }
                    }
                }
            }
            if (!stillPresent)
            {
                Console.WriteLine($"[PLAYER-IDENTIFIED-WELCOME] skipped — departed before send caller={callerIP} channelId={channelId}");
                return;
            }
            await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new { id = 3, jsonrpc = "2.0", method = "jamulusserver/sendClientChatMessage", @params = new { channelId, message = msg } }, rpcOpts).AsMemory(), tcpCts.Token);
            string? result = await reader.ReadLineAsync(tcpCts.Token);
            WelcomeCache.Set(capturedChannelKey, "", 1);
            Console.WriteLine($"[PLAYER-IDENTIFIED-WELCOME] caller={callerIP} channelId={channelId} rpcport={rpcPort} nation={capturedNation} response={result}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PLAYER-IDENTIFIED-WELCOME] caller={callerIP} channelId={channelId} rpcport={rpcPort} error={ex.ToString()}");
        }
        finally
        {
            sw.Stop();
            WelcomeEventLog.Append(serverKey, capturedNation, rich, llmStatus, signals, sw.ElapsedMilliseconds, msg);
        }

        string groupCacheKey = $"group:{capturedGuid}:{serverKey}";
        if (isGroupNoteworthy && roomChannelIds.Count > 0 && groupContextText != null
            && !WelcomeCache.TryGet(groupCacheKey, out _))
        {
            WelcomeCache.Set(groupCacheKey, "", 10);
            Console.WriteLine($"[PLAYER-IDENTIFIED-GROUP] caller={callerIP} noteworthy=1 roomIds={roomChannelIds.Count} — sending group message");
            await Task.Delay(5000);
            string? groupMsg = null;
            try { groupMsg = await WelcomeMessageGenerator.GetGroupAsync(groupContextText, groupNameColors, hasWebUser); }
            catch { }
            if (groupMsg != null)
            {
                var (svcName2, _) = WelcomeContext.LookupServer(serverKey);
                string speaker = svcName2.Length > 0 ? svcName2 : serverKey;
                string encodedSpeaker = System.Web.HttpUtility.HtmlEncode(speaker);
                string groupBody = groupMsg.Replace("<p>", "").Replace("</p>", "").Trim();
                string groupFull = $"<br><b><font color=\"#4FC3F7\">{encodedSpeaker}:</font></b> {groupBody}";
                var allIds = roomChannelIds.Concat(new[] { channelId }).Distinct().ToList();
                try
                {
                    string secret2 = (await System.IO.File.ReadAllTextAsync("/secret.txt")).Trim();
                    var rpcOpts2 = new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                    using var tcp2 = new System.Net.Sockets.TcpClient();
                    using var tcpCts2 = new System.Threading.CancellationTokenSource(5000);
                    await tcp2.ConnectAsync(callerIP, rpcPort, tcpCts2.Token);
                    await using var stream2 = tcp2.GetStream();
                    await using var writer2 = new System.IO.StreamWriter(stream2, leaveOpen: true) { AutoFlush = true };
                    using var reader2 = new System.IO.StreamReader(stream2, leaveOpen: true);
                    await writer2.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new { id = 1, jsonrpc = "2.0", method = "jamulus/apiAuth", @params = new { secret = secret2 } }, rpcOpts2).AsMemory(), tcpCts2.Token);
                    await reader2.ReadLineAsync(tcpCts2.Token);
                    int msgId = 2;
                    foreach (var rcId in allIds)
                    {
                        await writer2.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new { id = msgId++, jsonrpc = "2.0", method = "jamulusserver/sendClientChatMessage", @params = new { channelId = rcId, message = groupFull } }, rpcOpts2).AsMemory(), tcpCts2.Token);
                        await reader2.ReadLineAsync(tcpCts2.Token);
                    }
                    Console.WriteLine($"[PLAYER-IDENTIFIED-GROUP] caller={callerIP} speaker={speaker} recipients={allIds.Count} msg={groupMsg}");
                }
                catch (Exception ex) { Console.WriteLine($"[PLAYER-IDENTIFIED-GROUP] error={ex.Message}"); }
            }
            else if (groupMsg == null)
            {
                Console.WriteLine($"[PLAYER-IDENTIFIED-GROUP] caller={callerIP} groupMsg=null (LLM returned nothing)");
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

// Fleet server RPC port map — data/fleet-rpc-ports.txt, format: ip=port, # = comment.
// Default 9999. 5-minute TTL.
public static class FleetRpcPorts
{
    private static (Dictionary<string, int> Map, DateTime Expiry) _cache = (new(), DateTime.MinValue);
    private static readonly object _lock = new object();

    public static int GetPort(string serverIp)
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


