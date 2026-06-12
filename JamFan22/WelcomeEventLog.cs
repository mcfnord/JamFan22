public static class WelcomeEventLog
{
    private const string LogPath = "data/welcome-events.log";

    // Appends one compact line per welcome event — tail this file to monitor live output quality.
    // Format: timestamp | server=... nation=XX rich=1 llm=1|0|timeout|error|cached ms=NNN signals=... | message preview
    public static void Append(string serverKey, string nation, bool rich, string llmStatus,
        string signals, long elapsedMs, string message)
    {
        try
        {
            var (serverName, _) = WelcomeContext.LookupServer(serverKey);
            string serverLabel = serverName.Length > 0 ? serverName : serverKey;
            string preview = message;
            string line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} | server=\"{serverLabel}\" nation={nation}" +
                          $" rich={(rich ? 1 : 0)} llm={llmStatus} ms={elapsedMs}" +
                          $" signals={signals} | {preview}";
            File.AppendAllText(LogPath, line + "\n");
        }
        catch { }
    }
}
