using Microsoft.AspNetCore.Http;
using System.Net.Mime;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

///* removed for localhost usage.
builder.WebHost.UseKestrel(serverOptions =>
{
//    serverOptions.ListenAnyIP(80);
    serverOptions.ListenAnyIP(443, listenOptions => listenOptions.UseHttps("keyMar23.pfx", "jamfan"));
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

System.Threading.Mutex m_serializeDocks = new System.Threading.Mutex(false, "DOCK_MUTEX");


app.MapGet("/dock/{destination}", (string destination, HttpContext context) =>
{
    m_serializeDocks.WaitOne();
    try
    {
        using (var httpClient = new HttpClient())
        {
            // only set destination if there's a free instance
            string freeInstance = "";

            var response = httpClient.GetAsync("http://lounge.jamulus.live/free.txt").Result;
            var content = response.Content.ReadAsStringAsync().Result;
            if (content.Contains("True"))
                freeInstance = "lounge";
            else
            {
                response = httpClient.GetAsync("http://radio.jamulus.live/free.txt").Result;
                content = response.Content.ReadAsStringAsync().Result;
                if (content.Contains("True"))
                    freeInstance = "radio";
                else
                {
                    Console.WriteLine("Dock request forbidden; neither lounge nor radio are free.");
                    context.Response.StatusCode = 403;
                    return context.Response.WriteAsync("Forbidden");
                }
            }

            // is this destination blocklisted?
            response = httpClient.GetAsync("https://jamulus.live/cannot-dock.txt").Result;
            content = response.Content.ReadAsStringAsync().Result;
            if (content.Contains(destination))
            {
                Console.WriteLine("Dock request forbidden; destination is blocklisted.");
                context.Response.StatusCode = 403;
                return context.Response.WriteAsync("Forbidden");
            }

            // Is this destination allowlisted?
            response = httpClient.GetAsync("https://jamulus.live/can-dock.txt").Result;
            content = response.Content.ReadAsStringAsync().Result;
            if (false == content.Contains(destination))
            {
                Console.WriteLine("Dock request forbidden; destination is not allowlisted.");
                context.Response.StatusCode = 403;
                return context.Response.WriteAsync("Forbidden");
            }

            //      string DIR = "C:\\Users\\User\\JamFan22\\JamFan22\\wwwroot\\"; // for WINDOWS debug
            string DIR = "/root/JamFan22/JamFan22/wwwroot/";

            // for any line that contains this string, remove the line from the file.
            JamFan22.Pages.IndexModel.m_connectedLounges[$"https://{freeInstance}.jamulus.live"] = destination;

            File.WriteAllText(DIR + "requested_on_" + freeInstance + ".txt", destination);

            string html = "<!DOCTYPE html><html><head><meta charset=\"UTF-8\"><meta http-equiv=\"refresh\" content=\"10;url=https://"
                + freeInstance
                + ".jamulus.live\"></head><body><font size='+4'><br><br><b>WAIT 10 SECONDS...</b></font></body></html>";
            context.Response.ContentType = MediaTypeNames.Text.Html;
            context.Response.ContentLength = Encoding.UTF8.GetByteCount(html);
            return context.Response.WriteAsync(html);
        }
    }
    finally
    {
        m_serializeDocks.ReleaseMutex();
    }
});

app.MapGet("/servers/{target}", (string target, HttpContext context) =>
{
    using var client = new HttpClient();
    System.Threading.Tasks.Task<string> task = client.GetStringAsync("http://147.182.226.170/servers.php?central=" + target);
    task.Wait();
    return task.Result;
});

app.MapGet("/hotties/{encodedGuid}", (string encodedGuid, HttpContext context) =>
    {
        JamFan22.Pages.IndexModel.m_serializerMutex.WaitOne();
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
            string res = JamFan22.Pages.IndexModel.NameFromHash(guid);
            if (guid != res)
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
                                                    Console.Write(res + " / ");
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

            /////////////////////////////////////////////////////////////////
            /////////////////////////////////////////////////////////////////
            /////////////////////////////////////////////////////////////////
            /////////////////////////////////////////////////////////////////
            /////////////////////////////////////////////////////////////////

            /*
             * THIS VERSION OF HOTTIES USES RAM-BASED DATA. BUT I STORE IN THE FILE SYSTEM NOW.

            if (JamFan22.Pages.IndexModel.m_userConnectDurationPerUser.ContainsKey(guid))
            {
                // I just wanna see who, if possile.
                if (guid != JamFan22.Pages.IndexModel.NameFromHash(guid))
                    Console.WriteLine(">>> Hinting for " + JamFan22.Pages.IndexModel.NameFromHash(guid) + " (just first half):");
                else
                    Console.WriteLine(">>> Hinting for offline user (just first half):");

                var cookedDurations = new Dictionary<string, TimeSpan>();
                foreach (var someoneElse in JamFan22.Pages.IndexModel.m_userConnectDurationPerUser[guid])
                {
                    string us = guid + someoneElse.Key;
                    var timesIJoinedThem = 0.5F;
                    if (JamFan22.Pages.IndexModel.m_everywhereIveJoinedYou.ContainsKey(us))
                    {
                        timesIJoinedThem = JamFan22.Pages.IndexModel.m_everywhereIveJoinedYou[us].Count;
                        if (timesIJoinedThem == 0)
                            timesIJoinedThem = 0.5F; // If I've never joined them, then they just joined me. .5 is multiplier.
                    }
                    cookedDurations[someoneElse.Key] = someoneElse.Value * timesIJoinedThem;
                }

                List<string> hotties = new List<string>();
                var orderedCookedDurations = cookedDurations.OrderByDescending(dude => dude.Value);
                foreach (var guy in orderedCookedDurations)
                {
                    // if guy's online...
                    if (guy.Key != JamFan22.Pages.IndexModel.NameFromHash(guy.Key))
                    {
                        // if has a name
                        if (JamFan22.Pages.IndexModel.NameFromHash(guy.Key) != "No Name")
                        {
                            Console.Write(JamFan22.Pages.IndexModel.NameFromHash(guy.Key) + " " + guy.Value + " ");
                            string us = guid + guy.Key;
                            if (JamFan22.Pages.IndexModel.m_everywhereIveJoinedYou.ContainsKey(us))
                            {
                                Console.Write(JamFan22.Pages.IndexModel.m_everywhereIveJoinedYou[us].Count);
                            }
                            Console.WriteLine();
                            hotties.Add(guy.Key);
                        }
                    }
                }
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


            */

            //////////////////////////////////////////////////////////
            //////////////////////////////////////////////////////////
            //////////////////////////////////////////////////////////
            //////////////////////////////////////////////////////////
            ///
            /// FIND ALL KEYS THIS GUY'S IN
            /// 

            if (null != JamFan22.Pages.IndexModel.m_timeTogether)
            {
                List<string> hotties = new List<string>();

                var timeTogetherDescending =  JamFan22.Pages.IndexModel.m_timeTogether.OrderByDescending(dude => dude.Value);

                foreach (var pair in timeTogetherDescending)
                {
                    if (pair.Key.Contains(guid))
                    {
                        var otherGuysGuid = pair.Key.Replace(guid, "");
                        var friendlyName = JamFan22.Pages.IndexModel.NameFromHash(otherGuysGuid);
                        if (otherGuysGuid != friendlyName) // if they have a name, they're online
                            if ("No Name" != friendlyName)
                                if ("" != friendlyName)
                                    if ("Studio Bridge" != friendlyName)
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
        finally { JamFan22.Pages.IndexModel.m_serializerMutex.ReleaseMutex(); }
    });


Thread trd = new Thread(new ThreadStart(JamFan22.Pages.IndexModel.RefreshThreadTask));
trd.IsBackground = true;
trd.Start();
Thread.Sleep(6000); // let the thread get revved up first

app.Run();
