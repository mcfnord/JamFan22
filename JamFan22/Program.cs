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
        //string guid = encodedGuid;

        /////////////////////////////////////////////////////////////////
        /////////////////////////////////////////////////////////////////
        /////////////////////////////////////////////////////////////////
        /////////////////////////////////////////////////////////////////
        /////////////////////////////////////////////////////////////////
        // to fix bug, i match IP with guid
        /*
        string useripaddr = context.Request.HttpContext.Connection.RemoteIpAddress.ToString();
        if (useripaddr.Contains("127.0.0.1") || useripaddr.Contains("::1"))
        {
            useripaddr = context.Request.HttpContext.Request.Headers["X-Forwarded-For"];
            if (null != useripaddr)
                if (false == useripaddr.Contains("::ffff"))
                    useripaddr = "::ffff:" + useripaddr;
        }
        JamFan22.Pages.IndexModel.m_ipToGuid[useripaddr] = guid;
        */
        /////////////////////////////////////////////////////////////////
        /////////////////////////////////////////////////////////////////
        /////////////////////////////////////////////////////////////////
        /////////////////////////////////////////////////////////////////
        /////////////////////////////////////////////////////////////////


        // If the user is online...
        string theirName = "";
        string theirInstrument = "";
        bool result = JamFan22.Pages.IndexModel.DetailsFromHash(guid, ref theirName, ref theirInstrument);
        if (result == true)
            // if (guid != res)
            if (guid != "No Name")
            {
                // find the user's city-nation. If they don't have one, then we don't do nothin.
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
                                /*
                                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(guy.name + guy.country + guy.instrument);
                                var hashOfGuy = System.Security.Cryptography.MD5.HashData(bytes);
                                string stringHashOfGuy = System.Convert.ToBase64String(hashOfGuy);
                                */
                                if (guid == stringHashOfGuy)
                                {
                                    if ((guy.city != "") && (guy.country != ""))
                                    {
                                        // try to get a lat-long from the city-country
                                        string lat = "";
                                        string lon = "";
                                        if (true == JamFan22.Pages.IndexModel.CallOpenCageCached(guy.city + ", " + guy.country, ref lat, ref lon))
                                        {
                                            // assoc this lat-lon with this ip address
                                            string ipaddr = context.Request.HttpContext.Connection.RemoteIpAddress.ToString();

                                            // ::1 appears in local debugging, but also possibly in reverse-proxy :o
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
                    bool online = JamFan22.Pages.IndexModel.DetailsFromHash(otherGuysGuid, ref friendlyName, ref friendlyInstrument);
                    //if (otherGuysGuid != friendlyName) // if they have a name, they're online
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

            // now cut it in half.
            if (hotties.Count < 2) // if we don't even have 2, forget it. cuz we div by 2 later.
                return "[]";
            const string QUOT = "\"";
            string ret = "[";
            //foreach (var str in hotties)
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
Thread trd = new Thread(new ThreadStart(JamFan22.Pages.IndexModel.RefreshThreadTask));
trd.IsBackground = true;
trd.Start();

// This task is async. Task.Run is the correct way to start it on the
// ThreadPool. The original 'new Thread' wrapper was unnecessary.
_ = Task.Run(JamFan22.harvest.HarvestLoop2025);


// Use non-blocking delay for app startup instead of Thread.Sleep
await Task.Delay(6000); // let the thread get revved up first

app.Run();






