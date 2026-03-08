using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Linq;
using System.Text;

namespace JamFan22.Pages
{
    public class ClientModel : PageModel
    {
        public void OnGet()
        {
        }

        public string SystemStatus
        {
            get
            {
                if (IndexModel.ListServicesOffline.Count == 0)
                    return "";
                string ret = "<b>Oops!</b> Couldn't get updates for: ";
                foreach (var list in IndexModel.ListServicesOffline)
                    ret += list + ", ";
                return ret.Substring(0, ret.Length - 2); // chop comma
            }
            set { }
        }

        public string ShowServerByIPPortForView
        {
            get
            {
                if (IndexModel.m_allMyServers == null) return "";
                StringBuilder ret = new StringBuilder("<table><tr><th>Server<th>Server Address</tr>\n");
                foreach (var s in IndexModel.m_allMyServers.OrderBy(x => x.name).ToList())
                {
                    ret.Append("<tr><td>" + s.name + "<td>" +
                            s.serverIpAddress +
                            ":" +
                            s.serverPort +
                            "</tr>\n");
                }
                ret.Append("</table>\n");
                return ret.ToString();
            }
        }
    }
}
