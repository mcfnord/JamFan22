using System;

namespace JamFan22.Models
{
    public class JamulusServers
    {
        public long numip { get; set; }
        public long port { get; set; }
        public string? country { get; set; }
        public long maxclients { get; set; }
        public long perm { get; set; }
        public string name { get; set; }
        public string ipaddrs { get; set; }
        public string city { get; set; }
        public string ip { get; set; }
        public long ping { get; set; }
        public Os ps { get; set; }
        public string version { get; set; }
        public string versionsort { get; set; }
        public long nclients { get; set; }
        public long index { get; set; }
        public Client[] clients { get; set; }
        public long port2 { get; set; }
    }

    public class Client
    {
        public long chanid { get; set; }
        public string country { get; set; }
        public string instrument { get; set; }
        public string skill { get; set; }
        public string name { get; set; }
        public string city { get; set; }
    }

    public enum Os { Linux, MacOs, Windows };
}
