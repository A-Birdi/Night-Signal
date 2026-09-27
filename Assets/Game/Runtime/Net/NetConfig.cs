using System;
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
        public string PublicHost = "127.0.0.1";
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
                }
            }
            return c;
        }
    }
}
