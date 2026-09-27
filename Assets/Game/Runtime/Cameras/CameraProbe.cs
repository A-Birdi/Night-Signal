using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NightSignal.UI;
using Newtonsoft.Json;
using UnityEngine;

namespace NightSignal.Cameras
{
    /// <summary>
    /// Launch-argument plumbing for every role, and an evidence probe for the driving camera (Addendum 03 C09/C11/C12):
    /// <list type="bullet">
    /// <item><c>-nsPrefsFolder &lt;dir&gt;</c> — this process's own driving preferences (views, units, style, bindings).</item>
    /// <item><c>-nsTargetFps &lt;n&gt;</c> — vsync off and a frame-rate cap, to exercise 30/60/120 fps.</item>
    /// <item><c>-nsCameraProbe &lt;file&gt;</c> — record, per view: frames, the frame rate actually achieved, on-screen
    /// jitter of the car (second difference of its viewport position, in 1080p pixels — smooth following scores near 0,
    /// frame-to-frame shake or correction snaps score high), frames where geometry blocks the line from the car to an
    /// exterior camera or a mounted camera sits inside a collider, the largest impact offset; the view timeline; and
    /// the style/units in use. <c>-nsProbeCycleAt &lt;s&gt;</c> cycles this client's view once, s seconds after the car
    /// first moves (the others must not follow).</item>
    /// </list>
    /// Presentation-side measurement only; it never feeds the simulation.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public sealed class CameraProbe : MonoBehaviour
    {
        sealed class ViewStats
        {
            public int Frames, JitterSamples, Occluded, Inside;
            public double JitterSum;
            public readonly List<float> Jitter = new List<float>();
            public float MaxImpact, Seconds;
        }

        string path;
        float cycleAt = -1f;
        int targetFps;
        bool cycled;
        float movingSince = -1f, startedAt, lastWrite;
        DrivingCamera cam;
        readonly Dictionary<DrivingView, ViewStats> stats = new Dictionary<DrivingView, ViewStats>();
        readonly List<string> timeline = new List<string>();
        DrivingView? lastView;
        Vector2 vp1, vp2;
        int history;
        Vector3 lastCarPos;
        readonly List<float> frameMs = new List<float>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot()
        {
            string[] args = Environment.GetCommandLineArgs();
            string prefs = Arg(args, "-nsPrefsFolder");
            if (prefs != null)
            {
                DrivingPreferences.FolderOverride = Path.GetFullPath(prefs);
                DrivingPreferences.ResetCache();
            }
            int.TryParse(Arg(args, "-nsTargetFps"), out int fps);
            if (fps > 0)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = fps;
            }
            string probe = Arg(args, "-nsCameraProbe");
            if (probe == null) return;
            var go = new GameObject("CameraProbe");
            DontDestroyOnLoad(go);
            CameraProbe p = go.AddComponent<CameraProbe>();
            p.path = Path.GetFullPath(probe);
            p.targetFps = fps;
            if (float.TryParse(Arg(args, "-nsProbeCycleAt"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float at)) p.cycleAt = at;
            p.startedAt = Time.realtimeSinceStartup;
        }

        static string Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        void LateUpdate()
        {
            if (cam == null) cam = FindAnyObjectByType<DrivingCamera>();
            if (cam == null || cam.Target == null || cam.Camera == null) return;
            float now = Time.realtimeSinceStartup;
            frameMs.Add(Time.unscaledDeltaTime * 1000f);
            Transform car = cam.Target.transform;
            if (movingSince < 0f && cam.LastSpeedMps > 5f) movingSince = now;
            if (cycleAt >= 0f && !cycled && movingSince >= 0f && now - movingSince >= cycleAt)
            {
                cycled = true;
                cam.Cycle();
                timeline.Add($"{now - startedAt:F1}s scripted cycle");
            }
            DrivingView view = cam.View;
            if (lastView != view)
            {
                timeline.Add($"{now - startedAt:F1}s {view}");
                lastView = view;
                history = 0;
            }
            if (!stats.TryGetValue(view, out ViewStats v)) stats[view] = v = new ViewStats();
            v.Frames++;
            v.Seconds += Time.unscaledDeltaTime;
            v.MaxImpact = Mathf.Max(v.MaxImpact, cam.ImpactOffset);

            bool exterior = view == DrivingView.ChaseClose || view == DrivingView.ChaseFar || cam.LookBack;
            Vector3 camPos = cam.transform.position;
            if (exterior)
            {
                Vector3 pivot = car.position + car.up * (cam.Target.Params.HeightM * 0.55f);
                if (Physics.Linecast(pivot, camPos, cam.CollisionMask, QueryTriggerInteraction.Ignore)) v.Occluded++;
            }
            else if (Physics.CheckSphere(camPos, cam.Camera.nearClipPlane * 1.5f, cam.CollisionMask, QueryTriggerInteraction.Ignore)) v.Inside++;

            // On-screen jitter of the followed car (exterior views; a discontinuity restarts the history).
            if ((car.position - lastCarPos).sqrMagnitude > 64f) history = 0;
            lastCarPos = car.position;
            if (exterior)
            {
                Vector3 vp3 = cam.Camera.WorldToViewportPoint(car.position + car.up * 0.5f);
                var vp = new Vector2(vp3.x, vp3.y);
                if (history >= 2 && vp3.z > 0f)
                {
                    float px = (vp - 2f * vp1 + vp2).magnitude * 1080f;
                    v.JitterSum += px;
                    v.JitterSamples++;
                    if (v.Jitter.Count < 20000) v.Jitter.Add(px);
                }
                vp2 = vp1;
                vp1 = vp;
                history++;
            }
            if (now - lastWrite > 10f) { lastWrite = now; Write(); }
        }

        void OnApplicationQuit() => Write();
        void OnDestroy() => Write();

        void Write()
        {
            if (path == null || stats.Count == 0) return;
            try
            {
                DrivingPreferences prefs = DrivingPreferences.Current;
                var ordered = frameMs.OrderBy(x => x).ToList();
                var doc = new Dictionary<string, object>
                {
                    ["targetFps"] = targetFps,
                    ["measuredFps"] = frameMs.Count > 0 ? Math.Round(1000.0 / frameMs.Average(), 1) : 0,
                    ["frameMsP95"] = ordered.Count > 0 ? Math.Round(ordered[(int)(ordered.Count * 0.95f)], 2) : 0,
                    ["frames"] = frameMs.Count,
                    ["speedStyle"] = prefs.SpeedStyle,
                    ["units"] = prefs.Units,
                    ["savedView"] = prefs.View,
                    ["motionPreset"] = prefs.MotionPreset,
                    ["scriptedCycle"] = cycleAt >= 0f ? (cycled ? "done" : "not reached") : "none",
                    ["timeline"] = timeline,
                    ["views"] = stats.ToDictionary(kv => kv.Key.ToString(), kv =>
                    {
                        var j = kv.Value.Jitter.OrderBy(x => x).ToList();
                        return (object)new Dictionary<string, object>
                        {
                            ["frames"] = kv.Value.Frames,
                            ["seconds"] = Math.Round(kv.Value.Seconds, 1),
                            ["jitterMeanPx"] = kv.Value.JitterSamples > 0 ? Math.Round(kv.Value.JitterSum / kv.Value.JitterSamples, 3) : (object)null,
                            ["jitterP99Px"] = j.Count > 0 ? Math.Round(j[(int)(j.Count * 0.99f)], 3) : (object)null,
                            ["occludedFrames"] = kv.Value.Occluded,
                            ["insideColliderFrames"] = kv.Value.Inside,
                            ["maxImpactOffsetM"] = Math.Round(kv.Value.MaxImpact, 3),
                        };
                    }),
                };
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(doc, Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NightSignal.CameraProbe] write failed: " + e.Message);
            }
        }
    }
}
