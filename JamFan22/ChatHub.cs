using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;
using System;
using System.Linq;
using System.Collections.Generic;

namespace JamFan22
{
    public class ChatHub : Hub
    {
        private static readonly List<ChatMessage> _history = new List<ChatMessage>();
        private static readonly object _historyLock = new object();

        public class ChatMessage
        {
            public DateTime Timestamp { get; set; }
            public string User { get; set; }
            public string Message { get; set; }
        }

        private (string Name, string Guid)? GetCallerPersona(out string ip)
        {
            var httpContext = Context.GetHttpContext();
            ip = httpContext?.Connection.RemoteIpAddress?.ToString();
            
            if (ip != null && (ip.Contains("127.0.0.1") || ip.Contains("::1")))
            {
                var xff = httpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
                if (xff != null)
                {
                    ip = xff.Split(',')[0].Trim();
                    if (!ip.Contains("::ffff")) ip = "::ffff:" + ip;
                }
            }

            bool chatTestMode = false;
            if (httpContext != null && httpContext.Request.Query.TryGetValue("chatTestMode", out var testModeValues))
            {
                 chatTestMode = testModeValues.Count > 0 && testModeValues.First() == "true";
            }
            if (httpContext != null && httpContext.Request.Query.ContainsKey("chatTestMode"))
            {
                chatTestMode = true;
            }

            return ChatPersonaManager.GetPersonaDetails(ip, chatTestMode);
        }

        public IEnumerable<ChatMessage> GetHistory()
        {
            if (!ChatPersonaManager.IsFeatureEnabled) return Enumerable.Empty<ChatMessage>();

            var details = GetCallerPersona(out _);
            if (details == null) return Enumerable.Empty<ChatMessage>();

            lock (_historyLock)
            {
                return _history.ToList();
            }
        }

        public async Task SendMessage(string user, string message)
        {
            if (!ChatPersonaManager.IsFeatureEnabled) return;

            var details = GetCallerPersona(out string ip);
            if (details == null)
            {
                var unauthorizedLog = $"{DateTime.UtcNow:O} | UNAUTHORIZED ATTEMPT | IP: {ip} | ClaimedUser: {user} | Message: {message}";
                System.IO.File.AppendAllText("chat_activity.log", unauthorizedLog + Environment.NewLine);
                return;
            }

            var guid = details.Value.Guid;
            var realName = details.Value.Name;

            var logLine = $"{DateTime.UtcNow:O} | IP: {ip} | GUID: {guid} | FriendlyName: {realName} | ClaimedUser: {user} | Message: {message}";
            System.IO.File.AppendAllText("chat_activity.log", logLine + Environment.NewLine);

            var chatMsg = new ChatMessage
            {
                Timestamp = DateTime.UtcNow,
                User = realName,
                Message = message
            };

            lock (_historyLock)
            {
                _history.Add(chatMsg);
                var cutoff = DateTime.UtcNow.AddHours(-1);
                _history.RemoveAll(m => m.Timestamp < cutoff);
            }

            await Clients.All.SendAsync("ReceiveMessage", realName, message, chatMsg.Timestamp);
        }
    }
}