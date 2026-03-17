using JamFan22;
using JamFan22.Services;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using System.Net.Mime;
using System.Text;
using System.Threading;

var hottiesSemaphore = new SemaphoreSlim(1, 1);

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

        if (result && guid != "No Name")
        {
            foreach (var key in JamulusCacheManager.JamulusListURLs.Keys)
            {
                var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamFan22.Models.JamulusServers>>(JamulusCacheManager.LastReportedList[key]);
                foreach (var server in serversOnList)
                {
                    if (server.clients == null) continue;
                    foreach (var guy in server.clients)
                    {
                        string stringHashOfGuy = EncounterTracker.GetHash(guy.name, guy.country, guy.instrument);
                        if (guid == stringHashOfGuy && guy.city != "" && guy.country != "")
                        {
                            var geoService = context.RequestServices.GetRequiredService<JamFan22.Services.GeolocationService>();
                            var (success, lat, lon) = await geoService.CallOpenCageCachedAsync(guy.city + ", " + guy.country);

                            if (success)
                            {
                                string ipaddr = context.Request.HttpContext.Connection.RemoteIpAddress.ToString();
                                if (ipaddr.Contains("127.0.0.1") || ipaddr.Contains("::1"))
                                {
                                    ipaddr = context.Request.HttpContext.Request.Headers["X-Forwarded-For"];
                                    if (ipaddr != null && !ipaddr.Contains("::ffff"))
                                        ipaddr = "::ffff:" + ipaddr;
                                    Console.WriteLine("Due to localhost IP, switched to XFF IP: " + ipaddr);
                                }

                                if (ipaddr != null)
                                {
                                    JamFan22.Services.GeolocationService.m_ipAddrToLatLong[ipaddr] = new JamFan22.Models.LatLong(lat, lon);
                                    Console.Write("From " + ipaddr + " ");
                                    Console.Write(result + " / ");
                                    Console.Write(guy.city + ", " + guy.country + " ");
                                    Console.WriteLine(lat + ", " + lon);
                                }
                                else { Console.WriteLine("no ipaddr. could be bug."); }
                            }
                            else { Console.WriteLine("Failed to map " + guy.city + ", " + guy.country + " to a lat-long."); }
                        }
                    }
                }
            }
        }

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
