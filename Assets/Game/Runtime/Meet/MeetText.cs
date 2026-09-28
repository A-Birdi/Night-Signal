using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace NightSignal.Meet
{
    /// <summary>The meet's authored words (story/meet.text.json): the host's lines, placards, timing board and quick chat.</summary>
    public sealed class MeetText
    {
        public sealed class Placard
        {
            public string Id = "", Title = "", Text = "";
        }

        public string HostName = "Host", HostRole = "";
        public List<string> Greeting = new List<string>(), EmoteHelp = new List<string>(), AfterGreeting = new List<string>();
        public List<Placard> Viewpoints = new List<Placard>(), PhotoPoints = new List<Placard>();
        public string BoardHeader = "Timing board", BoardEmpty = "";
        public List<string> QuickChat = new List<string>();

        public static MeetText Parse(string json)
        {
            var t = new MeetText();
            if (string.IsNullOrEmpty(json)) return t;
            JObject o = JObject.Parse(json);
            List<string> Strings(JToken a)
            {
                var l = new List<string>();
                if (a is JArray arr) foreach (JToken x in arr) l.Add((string)x);
                return l;
            }
            List<Placard> Placards(JToken a)
            {
                var l = new List<Placard>();
                if (a is JArray arr)
                    foreach (JToken x in arr) l.Add(new Placard { Id = (string)x["id"] ?? "", Title = (string)x["title"] ?? "", Text = (string)x["text"] ?? "" });
                return l;
            }
            JToken host = o["host"];
            if (host != null)
            {
                t.HostName = (string)host["name"] ?? t.HostName;
                t.HostRole = (string)host["role"] ?? "";
                t.Greeting = Strings(host["greeting"]);
                t.EmoteHelp = Strings(host["emoteHelp"]);
                t.AfterGreeting = Strings(host["afterGreeting"]);
            }
            t.Viewpoints = Placards(o["placards"]?["viewpoints"]);
            t.PhotoPoints = Placards(o["placards"]?["photoPoints"]);
            t.BoardHeader = (string)o["timingBoard"]?["header"] ?? t.BoardHeader;
            t.BoardEmpty = (string)o["timingBoard"]?["emptyState"] ?? "";
            t.QuickChat = Strings(o["quickChat"]);
            return t;
        }

        public Placard Find(string id)
        {
            foreach (Placard p in Viewpoints) if (p.Id == id) return p;
            foreach (Placard p in PhotoPoints) if (p.Id == id) return p;
            return null;
        }
    }
}
