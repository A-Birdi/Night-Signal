using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace NightSignal.Diagnostics
{
    /// <summary>
    /// Drives the setup verification scene. In the editor it only spins its target.
    /// In a player launched with <c>-nsSmokeTest</c> it renders a fixed number of frames,
    /// captures the back buffer, writes a JSON report and quits, so a build can be proven
    /// to launch and render rather than merely to exist on disk.
    /// </summary>
    public sealed class SmokeTestRunner : MonoBehaviour
    {
        public const string SmokeFlag = "-nsSmokeTest";
        public const string OutputDirFlag = "-nsSmokeOut";
        public const string FramesFlag = "-nsSmokeFrames";

        [SerializeField] Transform spinTarget;
        [SerializeField] float spinDegreesPerSecond = 45f;
        [SerializeField] int framesBeforeCapture = 120;

        public Transform SpinTarget => spinTarget;
        public int FramesRendered { get; private set; }

        [Serializable]
        sealed class SmokeReport
        {
            public string status;
            public string unityVersion;
            public string platform;
            public bool developmentBuild;
            public string renderPipelineType;
            public string renderPipelineAsset;
            public string graphicsDevice;
            public string colorSpace;
            public int screenWidth;
            public int screenHeight;
            public int framesRendered;
            public float elapsedSeconds;
            public float averageFps;
            public float nonBlackPixelFraction;
            public float spinDegrees;
            public string screenshotFile;
            public string utcFinished;
        }

        void Update()
        {
            if (spinTarget != null)
                spinTarget.Rotate(0f, spinDegreesPerSecond * Time.deltaTime, 0f, Space.World);
        }

        IEnumerator Start()
        {
            if (!HasArg(SmokeFlag))
                yield break;

            string outDir = GetArg(OutputDirFlag) ?? Path.Combine(Application.persistentDataPath, "smoke");
            if (int.TryParse(GetArg(FramesFlag), out int frames) && frames > 0)
                framesBeforeCapture = frames;
            Directory.CreateDirectory(outDir);

            float startTime = Time.realtimeSinceStartup;
            float startYaw = spinTarget != null ? spinTarget.eulerAngles.y : 0f;
            for (int i = 0; i < framesBeforeCapture; i++)
            {
                yield return new WaitForEndOfFrame();
                FramesRendered++;
            }
            float elapsed = Time.realtimeSinceStartup - startTime;

            var shot = ScreenCapture.CaptureScreenshotAsTexture();
            const string shotName = "player-smoke.png";
            File.WriteAllBytes(Path.Combine(outDir, shotName), shot.EncodeToPNG());
            float nonBlack = NonBlackFraction(shot);
            Destroy(shot);

            var pipeline = GraphicsSettings.currentRenderPipeline;
            var report = new SmokeReport
            {
                status = nonBlack > 0.05f ? "rendered" : "suspect-blank-frame",
                unityVersion = Application.unityVersion,
                platform = Application.platform.ToString(),
                developmentBuild = Debug.isDebugBuild,
                renderPipelineType = pipeline != null ? pipeline.GetType().FullName : "BuiltIn",
                renderPipelineAsset = pipeline != null ? pipeline.name : "",
                graphicsDevice = SystemInfo.graphicsDeviceType.ToString(),
                colorSpace = QualitySettings.activeColorSpace.ToString(),
                screenWidth = Screen.width,
                screenHeight = Screen.height,
                framesRendered = FramesRendered,
                elapsedSeconds = elapsed,
                averageFps = elapsed > 0f ? FramesRendered / elapsed : 0f,
                nonBlackPixelFraction = nonBlack,
                spinDegrees = spinTarget != null ? Mathf.DeltaAngle(startYaw, spinTarget.eulerAngles.y) : 0f,
                screenshotFile = shotName,
                utcFinished = DateTime.UtcNow.ToString("o"),
            };
            File.WriteAllText(Path.Combine(outDir, "player-smoke.json"), JsonUtility.ToJson(report, true));
            Debug.Log($"[NightSignal.Smoke] {report.status} frames={FramesRendered} fps={report.averageFps:F1} pipeline={report.renderPipelineType}");
            Application.Quit(report.status == "rendered" ? 0 : 2);
        }

        static float NonBlackFraction(Texture2D tex)
        {
            Color32[] pixels = tex.GetPixels32();
            if (pixels.Length == 0)
                return 0f;
            int lit = 0;
            foreach (Color32 p in pixels)
                if (p.r + p.g + p.b > 24)
                    lit++;
            return (float)lit / pixels.Length;
        }

        static bool HasArg(string flag) => Array.IndexOf(Environment.GetCommandLineArgs(), flag) >= 0;

        static string GetArg(string flag)
        {
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
