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
                // Interactive until the menu/convoy UI lands: offline practice on the first course.
                SceneManager.sceneLoaded += OnCourseLoaded;
                SceneManager.LoadScene("C01");
                Debug.Log("[NightSignal.Boot] role: interactive (offline practice)");
            }
        }

        static void OnCourseLoaded(Scene scene, LoadSceneMode mode)
        {
            SceneManager.sceneLoaded -= OnCourseLoaded;
            var session = new GameObject("PracticeSession").AddComponent<LocalDriveSession>();
            session.CarId = "V01";
        }
    }
}
