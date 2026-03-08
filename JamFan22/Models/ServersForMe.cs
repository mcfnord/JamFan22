namespace JamFan22.Models
{
    public class ServersForMe
    {
        public ServersForMe(string cat, string ip, long port, string na, string ci, string cou, int distance, char earthZone, string w, Client[] originallyWho, int peoplenow, int maxpeople)
        {
            category = cat;
            serverIpAddress = ip;
            serverPort = port;
            name = na;
            city = ci;
            country = cou;
            distanceAway = distance;
            zone = earthZone;
            who = w;
            whoObjectFromSourceData = originallyWho;
            usercount = peoplenow;
            maxusercount = maxpeople;
        }
        public string category { get; set; }
        public string serverIpAddress { get; set; }
        public long serverPort { get; set; }
        public string name { get; set; }
        public string city { get; set; }
        public string country { get; set; }
        public int distanceAway { get; set; }
        public char zone { get; set; }
        public string who { get; set; }
        public Client[] whoObjectFromSourceData { get; set; } 
        public int usercount { get; set; }
        public int maxusercount { get; set; }
    }
}
