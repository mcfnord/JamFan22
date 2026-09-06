public static class WelcomeEventLog
{
    private const string LogPath = "data/welcome-events.log";

    // Appends one compact line per welcome event — tail this file to monitor live output quality.
    // Format: timestamp | server=... nation=XX rich=1 llm=1|0|timeout|error|cached in=N cached=N out=N ms=NNN signals=... | message preview
    public static void Append(string serverKey, string nation, bool rich, string llmStatus,
        string signals, long elapsedMs, string message, int inputTokens = 0, int outputTokens = 0, int cachedTokens = 0)
    {
        try
        {
            // "preview:" marks /debug/welcome-preview entries; "group:" marks the
            // room-broadcast message so both stay distinguishable from individual sends.
            string prefix = "";
            foreach (var p in new[] { "preview:", "group:" })
            {
                if (serverKey.StartsWith(p))
                {
                    prefix = p;
                    serverKey = serverKey.Substring(prefix.Length);
                    break;
                }
            }
            var (serverName, _) = WelcomeContext.LookupServer(serverKey);
            string serverLabel = prefix + (serverName.Length > 0 ? serverName : serverKey);
            string tokPart = inputTokens > 0 ? $" in={inputTokens} cached={cachedTokens} out={outputTokens}" : "";
            string line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} | server=\"{serverLabel}\" nation={nation}" +
                          $" rich={(rich ? 1 : 0)} llm={llmStatus}{tokPart} ms={elapsedMs}" +
                          $" signals={signals} | {message}";
            File.AppendAllText(LogPath, line + "\n");
        }
        catch { }
    }
}
