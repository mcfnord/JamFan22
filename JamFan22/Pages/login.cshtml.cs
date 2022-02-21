using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace JamFan21.Pages
{
    public class LoginModel : PageModel
    {

        public string LoginUserList
        {
            get
            {
                // I BELIEVE I MUST SYNCHRONIZE ANYWAY BECAUSE WHEN A USER CHANGES THE UNDERLYING DATA...
                // BUT THIS IS GONNA BE A RARE CLICK FOR USERS! SO I WON'T SYNCHRONIZE FOR NOW.
                // gonna use full server lists read-only to produce selection list for login.
                Dictionary<string, string> allEm = new Dictionary<string, string>();
                var globe = JamFan22.Pages.IndexModel.LastReportedList;
                foreach (var key in globe.Keys)
                {
                    var serversOnList = System.Text.Json.JsonSerializer.Deserialize<List<JamFan22.Pages.JamulusServers>>(globe[key]);
                    foreach (var server in serversOnList)
                    {
                        if (server.clients != null)
                        {
                            foreach (var guy in server.clients)
                            {
                                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(guy.name + guy.country + guy.instrument);
                                var hashOfGuy = System.Security.Cryptography.MD5.HashData(bytes);
                                string encodedHashOfGuy = System.Convert.ToBase64String(hashOfGuy);
                                allEm[encodedHashOfGuy] = "<td>" + guy.name
                                    + "<td>" + guy.instrument
                                    + "<td>" + guy.country;
                            }
                        }
                    }
                }
                // sort by values.
                var sortedDict = from entry in allEm orderby entry.Value ascending select entry;
                string output = "";
                foreach( var dude in sortedDict)
                {
                    // output += "<tr id=\"" + dude.Key + "\">" + dude.Value + "</tr>\n" ;
                    output += "<tr onmouseover=\"this.style.cursor='pointer'\" "
                        + "onmouseout=\"this.style.cursor='default'\" "
                        + "onclick=\"login('"
                        + dude.Key 
                        + "')\">"
                        + dude.Value + "</tr>\n";
                }

                return "<table class='table table-hover'>" + output + "</table>";
            }
        }

        public void OnGet()
        {
        }
    }
}
