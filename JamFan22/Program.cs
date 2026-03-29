using JamFan22;
using JamFan22.Services;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using System.Net.Mime;
using System.Text;
using System.Threading;

var hottiesSemaphore = new SemaphoreSlim(1, 1);

Console.WriteLine($"[STARTUP] JamFan22 starting. PID={Environment.ProcessId} Time={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
StreamRequestManager.Load();
StreamGate.Load();

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseKestrel(serverOptions =>
{
    var port = 443;
    var portStr = Environment.GetEnvironmentVariable("PORT");
    if (!string.IsNullOrEmpty(portStr) && int.TryParse(portStr, out int p))
        port = p;

    if (port == 443)
        serverOptions.ListenAnyIP(port, listenOptions => listenOptions.UseHttps("keyJan26.pfx", "jamfan"));
    else
        serverOptions.ListenAnyIP(port);
});

// Add services to the container.
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

    var finder = new JamFan22.MusicianFinder();
    string htmlResult = await finder.FindMusiciansHtmlAsync(clientIP);
    return Results.Content(htmlResult, "text/html");
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

app.Run();

public class HideRequest
{
    public bool hide { get; set; }
}
