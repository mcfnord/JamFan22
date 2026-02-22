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
        public string category;
        public string serverIpAddress;
        public long serverPort;
        public string name;
        public string city;
        public string country;
        public int distanceAway;
        public char zone;
        public string who;
        public Client[] whoObjectFromSourceData; // just to get the hash to work later. the who string is decorated but this is just data.
        public int usercount;
        public int maxusercount;
    }
}
