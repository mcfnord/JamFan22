using JamFan22.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Linq;
using System.Text;

namespace JamFan22.Pages
{
    public class ClientModel : PageModel
    {
        private readonly JamulusCacheManager _cacheManager;

        public bool HasResolvedPersona { get; set; }

        public ClientModel(JamulusCacheManager cacheManager)
        {
            _cacheManager = cacheManager;
        }

        public void OnGet()
        {
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            var xff = HttpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (xff != null) ip = xff.Split(',')[0].Trim();
            HasResolvedPersona = IdentityManager.GetPersonaDetails(ip) != null;
        }

        public string SystemStatus
        {
            get
            {
                if (JamulusCacheManager.ListServicesOffline.Count == 0) return "";
                string ret = "<b>Oops!</b> Couldn't get updates for: ";
                foreach (var list in JamulusCacheManager.ListServicesOffline)
                    ret += list + ", ";
                return ret.Substring(0, ret.Length - 2);
            }
        }

        public string ShowServerByIPPortForView
        {
            get
            {
                if (JamulusAnalyzer.m_safeServerSnapshot == null) return "";
                var ret = new StringBuilder("<table><tr><th>Server<th>Server Address</tr>\n");
                foreach (var s in JamulusAnalyzer.m_safeServerSnapshot.OrderBy(x => x.name).ToList())
                {
                    ret.Append("<tr><td>" + s.name + "<td>" + s.serverIpAddress + ":" + s.serverPort + "</tr>\n");
                }
                ret.Append("</table>\n");
                return ret.ToString();
            }
        }
    }
}
