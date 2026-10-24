using System;

namespace JamFan22.Models
{
    public class JamulusServers
    {
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
        // CLM_SERVER_FEATURES bits, as the sampler emits them (None/absent when the server
        // never answered CLM_REQ_SERVER_FEATURES — every 3.12.x server, our JAMFAN rooms included).
        public Features? features { get; set; }
        // The sampler's own raw-audio verdict (FU861, 2026-10-24): true / false once the sampler has
        // opened an audio channel to the server from its hole-punched sweep socket and the server
        // did / did not send RAWAUDIO_SUPPORTED; absent (null) until asked. Every JAMFAN room
        // answers this, unlike `features`, which no 3.12.x server sends.
        public bool? rawaudio { get; set; }
    }

    public class Features
    {
        public bool raw_audio_available { get; set; }
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
