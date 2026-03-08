using Microsoft.AspNetCore.Mvc.RazorPages;

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
    }
}