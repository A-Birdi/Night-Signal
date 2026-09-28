using System;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace NightSignal.Net
{
    /// <summary>Command-line configuration for server, client and automation roles.</summary>
    public sealed class NetConfig
    {
        public bool IsServer;
        public bool IsClient;
        public bool Auto;
        public string ControlPlaneUrl = "http://127.0.0.1:5080";
        public string ServerKeyFile = "Services/ControlPlane/.devkeys/gameserver-dev.key";
        /// <summary>
        /// What the control plane tells clients to connect to (<c>-nsPublicHost</c>). Separate from <see cref="BindHost"/>
        /// (Addendum 04): advertising an address does not open the listener on it.
        /// </summary>
        public string PublicHost = "127.0.0.1";
        /// <summary>
        /// Where the game-server socket actually binds (<c>-nsBindHost</c>): the IPv4 loopback by default, so a local run
        /// never listens on LAN/public interfaces (Addendum 04). A numeric address only — no host names, no "best interface".
        /// A wildcard or LAN bind is accepted here only because it was given explicitly; the project harnesses additionally
        /// require their LAN opt-in before passing one.
        /// </summary>
        public string BindHost = "127.0.0.1";
        /// <summary>The harness's explicit LAN opt-in (<c>-nsAllowLan</c>), recorded in the evidence.</summary>
        public bool AllowLan;
        public ushort Port = 7777;
        public int DevAccount = -1;
        public string DevSeedFile = "Backend/seed/dev-accounts.example.json";
        public string EvidenceDir = "Evidence/net";
        public string AutoRole = "leader";   // leader | member
        public int AutoHumans = 2;
        public string AutoStage = "S01";
        public string AutoCar = "V01";
        /// <summary>When set, the leader runs a Freeplay event on this course instead of a campaign stage.</summary>
        public string AutoFreeplayCourse;
        public int AutoFreeplayAi;
        public string AutoFreeplayMode = "sprint";
        public int ExitAfterSeconds = 900;
        /// <summary>Automation (Addendum 03 R11): drop the connection right after the server completes a recovery, then try to come back.</summary>
        public bool AutoDropAfterReset;
        /// <summary>Automation: drop the connection this many seconds after the start (a mid-race departure; −1 = never).</summary>
        public int AutoDropAt = -1;
        /// <summary>Automation: while spectating, settle on this entrant index after a few switches (−1 = keep cycling).</summary>
        public int AutoSpectateWatch = -1;

        public static string Build => Application.version;

        public static NetConfig FromCommandLine() => Parse(Environment.GetCommandLineArgs());

        public static NetConfig Parse(string[] args)
        {
            var c = new NetConfig();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (a)
                {
                    case "-nsServer": c.IsServer = true; break;
                    case "-nsClient": c.IsClient = true; break;
                    case "-nsAuto": c.Auto = true; break;
                    case "-nsControlUrl": c.ControlPlaneUrl = next; i++; break;
                    case "-nsServerKeyFile": c.ServerKeyFile = next; i++; break;
                    case "-nsPublicHost": c.PublicHost = next; i++; break;
                    case "-nsBindHost": c.BindHost = next; i++; break;
                    case "-nsAllowLan": c.AllowLan = true; break;
                    case "-nsPort": c.Port = ushort.Parse(next); i++; break;
                    case "-nsDevAccount": c.DevAccount = int.Parse(next); i++; break;
                    case "-nsDevSeed": c.DevSeedFile = next; i++; break;
                    case "-nsEvidence": c.EvidenceDir = next; i++; break;
                    case "-nsAutoRole": c.AutoRole = next; i++; break;
                    case "-nsAutoHumans": c.AutoHumans = int.Parse(next); i++; break;
                    case "-nsAutoStage": c.AutoStage = next; i++; break;
                    case "-nsAutoCar": c.AutoCar = next; i++; break;
                    case "-nsAutoFreeplay": c.AutoFreeplayCourse = next; i++; break;
                    case "-nsAutoFreeplayAi": c.AutoFreeplayAi = int.Parse(next); i++; break;
                    case "-nsAutoFreeplayMode": c.AutoFreeplayMode = next; i++; break;
                    case "-nsExitAfter": c.ExitAfterSeconds = int.Parse(next); i++; break;
                    case "-nsAutoDropAfterReset": c.AutoDropAfterReset = true; break;
                    case "-nsAutoDropAt": c.AutoDropAt = int.Parse(next); i++; break;
                    case "-nsAutoSpectateWatch": c.AutoSpectateWatch = int.Parse(next); i++; break;
                }
            }
            return c;
        }

        /// <summary>
        /// Checks the listen/advertise pair before any socket starts: the bind address must be a numeric IP; a loopback bind
        /// must advertise a loopback address (nothing else could reach it). Null when the pair is usable, else why not.
        /// </summary>
        public string BindProblem()
        {
            if (string.IsNullOrWhiteSpace(BindHost)) return "no bind address (-nsBindHost)";
            if (!IPAddress.TryParse(BindHost.Trim(), out IPAddress bind) || (bind.AddressFamily != AddressFamily.InterNetwork && bind.AddressFamily != AddressFamily.InterNetworkV6))
                return $"bind address '{BindHost}' is not a numeric IP address (host names and interface discovery are not used)";
            if (string.IsNullOrWhiteSpace(PublicHost)) return "no advertised address (-nsPublicHost)";
            if (IPAddress.IsLoopback(bind) && !(IPAddress.TryParse(PublicHost.Trim(), out IPAddress pub) && IPAddress.IsLoopback(pub)))
                return $"the listener binds loopback {BindHost} but advertises {PublicHost}, which no other machine could reach through it";
            return null;
        }

        /// <summary>local (loopback bind), wildcard (all interfaces), lan (a private address) or wan (anything else).</summary>
        public string BindClass()
        {
            if (!IPAddress.TryParse((BindHost ?? "").Trim(), out IPAddress a)) return "invalid";
            if (IPAddress.IsLoopback(a)) return "local";
            if (a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) return "wildcard";
            byte[] b = a.GetAddressBytes();
            bool privateV4 = b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254));
            bool privateV6 = b.Length == 16 && (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC);
            return privateV4 || privateV6 ? "lan" : "wan";
        }
    }
}
