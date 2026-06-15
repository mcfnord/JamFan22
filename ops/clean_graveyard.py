import sys

with open("JamFan22/JamFan22/Pages/Index.cshtml.cs", "r", encoding="utf-8") as f:
    content = f.read()

# Normalize line endings to make replacement robust
content = content.replace('\r\n', '\n')

blocks_to_remove = [
    # 1. LogVisitAsync
"""// --- PASTE THIS HELPER METHOD ANYWHERE ---
/*
private async Task LogVisitAsync(string ip, string countryCode)
{
    // FILTER: Only log if the country is Thailand ("TH")
    if (countryCode != "TH") return;

    // Format: "2025-12-31 14:00:05, 192.168.1.5, TH"
    string logLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}, {ip}, {countryCode}{Environment.NewLine}";

    // Thread-safe write
    await _logLock.WaitAsync();
    try
    {
        await System.IO.File.AppendAllTextAsync(LogFilePath, logLine);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Logging failed: {ex.Message}");
    }
    finally
    {
        _logLock.Release();
    }
}
*/""",

    # 2. MineLists()
"""        /*
        protected async Task MineLists()
        {
            if (LastReportedListGatheredAt != null)
            {
                if (DateTime.Now < LastReportedListGatheredAt.Value.AddSeconds(25))
                {
                    //                    Console.WriteLine("Data is less than 60 seconds old, and cached data is adequate.");
                    return; // data we have was gathered within the last minute.
                }
            }
        }
        */""",

    # 2.b File Writers
"""        /*
        // Each time we mine the list, save the ACTIVE ip:port set in a local file.
        List<string> svrIpPort = new List<string>();
        List<string> svrActivesIpPort = new List<string>();
        foreach (var key in JamulusListURLs.Keys)
        {
            var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamulusServers>>(LastReportedList[key]);
            foreach (var server in serversOnList)
            {
                svrIpPort.Add(server.ip + ":" + server.port);

                if (server.clients != null)
                    if (server.clients.GetLength(0) > 0)
                        svrActivesIpPort.Add(server.ip + ":" + server.port);
            }
        }
        await System.IO.File.WriteAllLinesAsync("allSvrIpPorts.txt", svrIpPort);
        await System.IO.File.WriteAllLinesAsync("activeSvrIpPorts.txt", svrActivesIpPort);
        */""",

    # 3. Giant Duration Tracker
"""        /*
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes("mcfnord" + "United States" + "-");
            var hashOfGuy = System.Security.Cryptography.MD5.HashData(bytes);
            string stringHashOfMe = System.Convert.ToBase64String(hashOfGuy);

            // 1) with me by duration
            if (m_userConnectDurationPerUser.ContainsKey(stringHashOfMe))
                {
                var allMyDurations = m_userConnectDurationPerUser[stringHashOfMe];

                var sortedByLongestTime = allMyDurations.OrderBy(dude => dude.Value);
                foreach (var them in sortedByLongestTime)
                {
                    //                    Console.WriteLine(them.Key + " " + them.Value.ToString());
                }

                /////////////////////////////////////////////////////////////////////////////////
                /////////////////////////////////////////////////////////////////////////////////
                /////////////////////////////////////////////////////////////////////////////////
                /////////////////////////////////////////////////////////////////////////////////
                // Create a new duration that is actually the old one multiplied by # of servers where i joined you
                {
                    var cookedDurations = new Dictionary<string, TimeSpan>();
                    foreach (var someoneElse in m_userConnectDurationPerUser[stringHashOfMe])
                    {

                        // Just start duration with total time togetther,
                        // regardless of who joined who.
                        cookedDurations[someoneElse.Key] = someoneElse.Value;
                        // and this shoots up for people I've joined, but even true north accrues here.
                        // even if he just joined me.

                        // make a key with me as actor, you as target
                        string us = stringHashOfMe + someoneElse.Key;
                        if (m_everywhereIveJoinedYou.ContainsKey(us))
                        {
                            var newCookedDuration = m_everywhereIveJoinedYou[us].Count * someoneElse.Value;
                            cookedDurations[someoneElse.Key] += newCookedDuration;
                        }
                    }

                    var orderedCookedDurations = cookedDurations.OrderByDescending(dude => dude.Value);
                    foreach (var guy in orderedCookedDurations)
                    {
                        Console.Write(NameFromHash(guy.Key) + " " + guy.Value + " ");
                        string us = stringHashOfMe + guy.Key;
                        if (m_everywhereIveJoinedYou.ContainsKey(us))
                        {
                            Console.Write(m_everywhereIveJoinedYou[us].Count);
                        }
                        Console.WriteLine();
                    }
                }
            }
        }
        }
*/""",

    # 4. Halos & Snippeting
"""        /* I'm not snippeting so who cares?

                static int m_lastRefreshSnippetingHalos = 0;
                static List<string> m_halos_snippeting = new List<string>();
        */""",

"""        /* For now, nobody's gonna block streaming
                static int m_lastRefreshStreamingHalos = 0;
                static List<string> m_halos_streaming = new List<string>();
                */""",

    # 5. DistanceFromMe
"""        /*
        protected int DistanceFromMe(string ipThem)
        {
            string clientIP = HttpContext.Connection.RemoteIpAddress.ToString();
            if (clientIP.Length < 5)
                clientIP = "104.215.148.63"; //microsoft as test 

            double clientLatitude = 0.0, clientLongitude = 0.0, serverLatitude = 0.0, serverLongitude = 0.0;
            SmartGeoLocate(clientIP, ref clientLatitude, ref clientLongitude);
            SmartGeoLocate(ipThem, ref serverLatitude, ref serverLongitude);

            const double EquatorialRadiusOfEarth = 6371D;
            const double DegreesToRadians = (Math.PI / 180D);
            var deltalat = (serverLatitude - clientLatitude) * DegreesToRadians;
            var deltalong = (serverLongitude - clientLongitude) * DegreesToRadians;
            var a = Math.Pow(
                Math.Sin(deltalat / 2D), 2D) +
                Math.Cos(clientLatitude * DegreesToRadians) *
                Math.Cos(serverLatitude * DegreesToRadians) *
                Math.Pow(Math.Sin(deltalong / 2D), 2D);
            var c = 2D * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1D - a));
            var d = EquatorialRadiusOfEarth * c;
            return Convert.ToInt32(d);
        }
        */""",

    # 6. RightNow Property
"""        /*
                public string RightNow
                {
                    get
                    {
                        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                        m_serializerMutex.WaitOne();
                        try
                        {
                            string ipAddress = GetClientIpAddress();
                            if (string.IsNullOrEmpty(ipAddress)) return string.Empty;

                            // var geoData = GetOrAddUserGeoData(ipAddress);
                            MyUserGeoCandy geoData = await GetOrAddUserGeoDataAsync(ipAddress);
                            if (geoData != null)
                            {
                                UpdateUserStatistics(ipAddress, geoData);
                                m_TwoLetterNationCode = geoData.countryCode2;
                            }


                            // Call the synchronous version or block the async version
                            var task = GetGutsRightNow(); // Assuming GetGutsRightNow returns a Task<string>
                            task.Wait();
                            return task.Result;
                        }
                        finally
                        {
                            stopwatch.Stop();
                            AdjustPerformanceDelta(stopwatch.Elapsed);
                            m_serializerMutex.ReleaseMutex();
                        }
                    }

                    set
                    {
                    }
                }
                */"""
]

for block in blocks_to_remove:
    block_normalized = block.replace('\r\n', '\n')
    if block_normalized in content:
        content = content.replace(block_normalized, "")
        print(f"Removed block starting with: {block_normalized[:40]}...")
    else:
        print(f"Could not find block starting with: {block_normalized[:40]}...")

with open("JamFan22/JamFan22/Pages/Index.cshtml.cs", "w", encoding="utf-8") as f:
    f.write(content)

