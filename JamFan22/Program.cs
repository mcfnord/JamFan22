using JamFan22;
using JamFan22.Pages;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using System.Net.Mime;
using System.Text;
using System.Threading; // Added for SemaphoreSlim and Task.Delay

// Use SemaphoreSlim(1) for non-blocking asynchronous serialization
var hottiesSemaphore = new System.Threading.SemaphoreSlim(1, 1);

var builder = WebApplication.CreateBuilder(args);

///* removed for localhost usage.
builder.WebHost.UseKestrel(serverOptions =>
{
    //    serverOptions.ListenAnyIP(80);
    serverOptions.ListenAnyIP(443, listenOptions => listenOptions.UseHttps("keyOct25.pfx", "jamfan"));
});
//*/


// Add services to the container.
builder.Services.AddRazorPages();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();


app.MapGet("/hotties/{encodedGuid}", async (string encodedGuid, HttpContext context) =>
{
    // Use non-blocking wait for serialization
    await hottiesSemaphore.WaitAsync();
    try
    {
        string guid = System.Web.HttpUtility.UrlDecode(encodedGuid);

        // ... (your IP-GUID association comment block) ...

        // If the user is online...
        string theirName = "";
        string theirInstrument = "";
        // This is still a synchronous call, which is fine if it's just a fast dictionary lookup
        bool result = JamFan22.Pages.IndexModel.DetailsFromHash(guid, ref theirName, ref theirInstrument);
        
        if (result == true)
            if (guid != "No Name")
            {
                // find the user's city-nation.
                foreach (var key in JamFan22.Pages.IndexModel.JamulusListURLs.Keys)
                {
                    var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamFan22.Pages.JamulusServers>>(JamFan22.Pages.IndexModel.LastReportedList[key]);
                    foreach (var server in serversOnList)
                    {
                        if (server.clients != null)
                        {
                            foreach (var guy in server.clients)
                            {
                                string stringHashOfGuy = JamFan22.Pages.IndexModel.GetHash(guy.name, guy.country, guy.instrument);
                                if (guid == stringHashOfGuy)
                                {
                                    if ((guy.city != "") && (guy.country != ""))
                                    {
                                        // try to get a lat-long from the city-country
                                        
                                        // ***** THIS IS THE FIX *****
                                        // We now 'await' the new async method and check the 'success' boolean
                                        var (success, lat, lon) = await JamFan22.Pages.IndexModel.CallOpenCageCachedAsync(guy.city + ", " + guy.country);
                                        
                                        if (true == success)
                                        // ***** END OF FIX *****
                                        {
                                            // assoc this lat-lon with this ip address
                                            string ipaddr = context.Request.HttpContext.Connection.RemoteIpAddress.ToString();

                                            // ... (your X-Forwarded-For logic is unchanged) ...
                                            if (ipaddr.Contains("127.0.0.1") || ipaddr.Contains("::1"))
                                            {
                                                ipaddr = context.Request.HttpContext.Request.Headers["X-Forwarded-For"];
                                                if (null != ipaddr)
                                                {
                                                    if (false == ipaddr.Contains("::ffff"))
                                                        ipaddr = "::ffff:" + ipaddr;
                                                }
                                                Console.WriteLine("Due to localhost IP, switched to XFF IP: " + ipaddr);
                                            }

                                            if (null != ipaddr)
                                            {
                                                // We use the 'lat' and 'lon' variables returned from the async call
                                                JamFan22.Pages.IndexModel.m_ipAddrToLatLong[ipaddr] = new JamFan22.Pages.IndexModel.LatLong(lat, lon);

                                                Console.Write("From " + ipaddr + " ");
                                                Console.Write(result + " / ");
                                                Console.Write(guy.city + ", " + guy.country + " ");
                                                Console.WriteLine(lat + ", " + lon);
                                            }
                                            else
                                                Console.WriteLine("no ipaddr. could be bug.");
                                        }
                                        else
                                            Console.WriteLine("Failed to map " + guy.city + ", " + guy.country + " to a lat-long.");
                                    }
                                }
                            }
                        }
                    }
                }
            }

        ///
        /// FIND ALL KEYS THIS GUY'S IN
        ///

        if (null != JamFan22.Pages.IndexModel.m_timeTogether)
        {
            List<string> hotties = new List<string>();

            var timeTogetherDescending = JamFan22.Pages.IndexModel.m_timeTogether.OrderByDescending(dude => dude.Value);

            foreach (var pair in timeTogetherDescending)
            {
                if (pair.Key.Contains(guid))
                {
                    var otherGuysGuid = pair.Key.Replace(guid, "");
                    string friendlyName = "";
                    string friendlyInstrument = "";
                    // This is still a synchronous call
                    bool online = JamFan22.Pages.IndexModel.DetailsFromHash(otherGuysGuid, ref friendlyName, ref friendlyInstrument);
                    
                    if (online)
                        if ("Listener" != friendlyInstrument)
                            if ("No Name" != friendlyName)
                                if ("" != friendlyName)
                                    if ("Studio Bridge" != friendlyName)
                                        if ("Ear" != friendlyName)
                                            if (false == friendlyName.Contains("obby"))
                                                hotties.Add(otherGuysGuid); // the guid that isn't me is left!
                }
            }

            // ... (rest of your JSON return logic is unchanged) ...
            if (hotties.Count < 2) 
                return "[]";
            const string QUOT = "\"";
            string ret = "[";
            for (int i = 0; i < hotties.Count / 2; i++) // of the top half...
                ret += QUOT + hotties[i] + QUOT + ", ";
            ret = ret.Substring(0, ret.Length - 2);
            ret += "]";
            return ret;
        }
        return "[]";
    }
    finally
    {
        // Release the non-blocking semaphore
        hottiesSemaphore.Release();
    }
});

app.MapGet("/halos/", async (HttpContext context) =>
{
    string url = "https://jamulus.live/halo-streaming.txt";
    // Use await for non-blocking I/O instead of .Wait()/.Result
    List<string> halostreaming = await JamFan22.Pages.IndexModel.LoadLinesFromHttpTextFile(url);

    if (halostreaming.Count == 0)
    {
        Console.WriteLine("halostreaming.txt is empty, maybe things are offline.");
        return "[]";
    }

    url = "https://jamulus.live/halo-snippeting.txt";
    // Use await for non-blocking I/O instead of .Wait()/.Result
    List<string> halosnippeting = await JamFan22.Pages.IndexModel.LoadLinesFromHttpTextFile(url);

    string ret = "[";

    const string QUOT = "\"";

    for (int i = 0; i < halostreaming.Count; i++) // of the top half...
        ret += QUOT + halostreaming[i] + QUOT + ", ";

    for (int i = 0; i < halosnippeting.Count; i++) // of the top half...
        ret += QUOT + halosnippeting[i] + QUOT + ", ";

    ret = ret.Substring(0, ret.Length - 2);

    ret += "]";
    return ret;
});

// This task appears to be a long-running *synchronous* loop.
// Running it on a dedicated Thread is correct to avoid ThreadPool starvation.
// Thread trd = new Thread(new ThreadStart(JamFan22.Pages.IndexModel.RefreshThreadTask));
Task.Run(() => JamFan22.Pages.IndexModel.RefreshThreadTask());

// This task is async. Task.Run is the correct way to start it on the
// ThreadPool. The original 'new Thread' wrapper was unnecessary.
_ = Task.Run(JamFan22.harvest.HarvestLoop2025);


// Use non-blocking delay for app startup instead of Thread.Sleep
await Task.Delay(6000); // let the thread get revved up first

app.Run();






