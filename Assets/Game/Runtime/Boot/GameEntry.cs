using NightSignal.Net;
using NightSignal.Race;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NightSignal.Boot
{
    /// <summary>
    /// First scene of every build. Chooses the process role from the command line:
    /// <c>-nsServer</c> dedicated authoritative game server (use with -batchmode -nographics),
    /// <c>-nsClient -nsAuto</c> automated test client, otherwise interactive play.
    /// </summary>
    public sealed class GameEntry : MonoBehaviour
    {
        void Start()
        {
            // Networked clients and servers must keep simulating when unfocused (alt-tab must not stall a race).
            Application.runInBackground = true;
            NetConfig cfg = NetConfig.FromCommandLine();
            var host = new GameObject("ProcessRole");
            DontDestroyOnLoad(host);
            if (cfg.IsServer)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = 120;
                host.AddComponent<GameServerHost>();
                Debug.Log("[NightSignal.Boot] role: dedicated game server");
            }
            else if (cfg.IsClient && cfg.Auto)
            {
                host.AddComponent<AutoClient>();
                Debug.Log("[NightSignal.Boot] role: automated client");
            }
            else
            {
                // Interactive player: title screen, Online Login / Offline Play (Addendum 01 §8.1).
                Front.FrontEndApp.Create();
                Debug.Log("[NightSignal.Boot] role: interactive client");
            }
        }

    }
}
