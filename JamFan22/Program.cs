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

// Fleet GUID-IP cache lives in FleetGuidCache (static class, accessible from IdentityManager)

Console.WriteLine($"[STARTUP] JamFan22 starting. PID={Environment.ProcessId} Time={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
FleetGuidCache.HydrateFromCsv();
StreamRequestManager.Load();
StreamGate.Load();
_ = StreamGate.PostLeaseMonitorAsync();

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

    var guid = context.Request.Query["guid"].FirstOrDefault();

    bool blocked = await IpAnalyticsService.IsIpBlockedAsync(ip);
    if (!string.IsNullOrEmpty(guid))
        FleetGuidCache.UpsertGuid(guid, ip, callerIP, blocked);
    string verdict = blocked ? "BLOCKED" : "ALLOWED";
    Console.WriteLine($"[IP-ALLOWED] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} caller={callerIP} query={ip} guid={guid ?? "-"} => {verdict}");
    return Results.Text(blocked ? "false" : "true", "text/plain");
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
    string htmlResult = await finder.FindMusiciansHtmlAsync(clientIP);
    return Results.Content(htmlResult, "text/html");
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

                    if (online && friendlyInstrument != "Listener" && friendlyName != "No Name" &&
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

app.MapGet("/stream", (HttpContext context) =>
{
    string clientIP = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var xff = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    if (!string.IsNullOrEmpty(xff)) { clientIP = xff.Split(',')[0].Trim(); }
    if (!clientIP.Contains("::ffff")) clientIP = "::ffff:" + clientIP;

    bool isWeekly = context.Request.Query.ContainsKey("weekly");
    string message = StreamGate.TryRequestStream(clientIP, isWeekly);

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

    Console.WriteLine($"[CHAT-CMD] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} server={remoteIp} port={req.port} command={req.command}");
    string message = StreamGate.TryRequestStream(remoteIp, false);
    return Results.Text(message, "text/plain");
});

app.Run();

public class ChatUrlRequest
{
    public string url { get; set; }
    public int port { get; set; }
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

