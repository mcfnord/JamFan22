var builder = WebApplication.CreateBuilder(args);

/* removed for localhost usage.
builder.WebHost.UseKestrel(serverOptions =>
{
    serverOptions.ListenAnyIP(80);
    serverOptions.ListenAnyIP(443, listenOptions => listenOptions.UseHttps("jamfan.pfx", "jamfan"));
});
*/


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

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();

app.MapGet("/hotties/{encodedGuid}", (string encodedGuid) =>
    {
        JamFan22.Pages.IndexModel.m_serializerMutex.WaitOne();
        try
        {
            string guid = System.Web.HttpUtility.UrlDecode(encodedGuid);
            if (JamFan22.Pages.IndexModel.m_userConnectDurationPerUser.ContainsKey(guid))
            {
                // I just wanna see who, if possile.
                if (guid != JamFan22.Pages.IndexModel.NameFromHash(guid))
                    Console.WriteLine(">>> Hinting for " + JamFan22.Pages.IndexModel.NameFromHash(guid) + " (just first half):");

                /*

                // Now on to the cooking...
                var allMyDurations = JamFan22.Pages.IndexModel.m_userConnectDurationPerUser[guid];

                var sortedByLongestTime = allMyDurations.OrderBy(dude => dude.Value);

                // Create a new duration that is actually the old one multiplied by # of servers where i joined you
                var cookedDurations = new Dictionary<string, TimeSpan>();
                foreach (var someoneElse in JamFan22.Pages.IndexModel.m_userConnectDurationPerUser[guid])
                {

                    // Just start duration with total time togetther,
                    // regardless of who joined who.
                    cookedDurations[someoneElse.Key] = someoneElse.Value;
                    // and this shoots up for people I've joined, but even true north accrues here.
                    // even if he just joined me.

                    // make a key with me as actor, you as target
                    string us = guid + someoneElse.Key;
                    if (JamFan22.Pages.IndexModel.m_everywhereIveJoinedYou.ContainsKey(us))
                    {
                        var newCookedDuration = JamFan22.Pages.IndexModel.m_everywhereIveJoinedYou[us].Count * someoneElse.Value;
                        cookedDurations[someoneElse.Key] += newCookedDuration;
                    }
                }
                */
                
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
            return "";
        }
        finally { JamFan22.Pages.IndexModel.m_serializerMutex.ReleaseMutex(); }
    });




app.Run();
