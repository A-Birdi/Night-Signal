using System;
using System.Collections;
using System.Collections.Generic;
using NightSignal.Core.Rules;
using NightSignal.Race;
using NightSignal.Track;
using NightSignal.UI;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>What the player is currently connected as (Addendum 01 §8: Online and Local never mix).</summary>
    public enum SessionDomain { None = 0, Online = 1, Local = 2 }

    /// <summary>
    /// Interactive client shell: persistent UI root, top status strip, screen router, the living course backdrop behind
    /// the menus, and launching/returning from races. Server and automated-client roles never create it.
    /// </summary>
    public sealed class FrontEndApp : MonoBehaviour
    {
        public static FrontEndApp Instance { get; private set; }
        public ScreenRouter Router { get; private set; }
        public SessionDomain Domain { get; set; }
        public string DisplayName { get; set; } = "";
        public Canvas Canvas { get; private set; }
        public event Action<UIScreen> ScreenChanged;

        public readonly MainMenuScreen MainMenu = new MainMenuScreen();
        public readonly SignInScreen SignIn = new SignInScreen();
        public readonly OfflineHubScreen OfflineHub = new OfflineHubScreen();
        public readonly SettingsScreen Settings = new SettingsScreen();
        public readonly ResultsScreen Results = new ResultsScreen();

        TextMeshProUGUI stripDomain, stripName, stripScreen;
        Image stripBar;
        GameObject backdropCamera;
        OfflineRaceSession activeRace;
        string backdropCourse = "C01";

        public static FrontEndApp Create()
        {
            var go = new GameObject("FrontEnd");
            DontDestroyOnLoad(go);
            return go.AddComponent<FrontEndApp>();
        }

        void Awake()
        {
            Instance = this;
            Canvas = UIFactory.Root("FrontEndCanvas", 10);
            Canvas.transform.SetParent(transform, false);
            var root = (RectTransform)Canvas.transform;
            Router = gameObject.AddComponent<ScreenRouter>();
            RectTransform body = UIFactory.Rect("Body", root, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(0, -64));
            Router.Init(this, body);
            BuildStrip(root);
        }

        IEnumerator Start()
        {
            yield return LoadBackdrop();
            Router.Show(MainMenu, false);
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-nsUiTour") >= 0)
                StartCoroutine(UiTour());
        }

        /// <summary>
        /// Standalone evidence run (<c>-nsUiTour</c>): drives the REAL buttons through title → Offline Play → a short
        /// autopilot race → results, saving a screenshot of each page to Builds/Screenshots/tour. Labelled automation,
        /// not a human playtest.
        /// </summary>
        IEnumerator UiTour()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "tour")); // players resolve relative capture paths against the Data folder
            System.IO.Directory.CreateDirectory(dir);
            yield return new WaitForSeconds(3f);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, "01-title.png"));
            yield return new WaitForSeconds(1f);
            GameObject.Find("OfflinePlay")?.GetComponent<Button>()?.onClick.Invoke();
            yield return new WaitForSeconds(1.5f);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, "02-offline.png"));
            yield return new WaitForSeconds(1f);
            GameObject.Find("Start")?.GetComponent<Button>()?.onClick.Invoke();
            float until = Time.realtimeSinceStartup + 20f;
            while (activeRace == null && Time.realtimeSinceStartup < until) yield return null;
            if (activeRace != null)
            {
                activeRace.Autopilot = true;
                yield return new WaitForSeconds(9f);
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, "03-race.png"));
                yield return new WaitForSeconds(1f);
                if (activeRace != null) activeRace.SimulationSpeed = 12;
            }
            until = Time.realtimeSinceStartup + 120f;
            while (Router.Current != Results && Time.realtimeSinceStartup < until) yield return null;
            yield return new WaitForSeconds(1.5f);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, "04-results.png"));
            Debug.Log("[NightSignal.UiTour] complete: " + (Router.Current == Results ? "results reached" : "results NOT reached"));
            yield return new WaitForSeconds(1f);
            Application.Quit(Router.Current == Results ? 0 : 1);
        }

        // ------------------------------------------------------------------ top strip

        /// <summary>
        /// Persistent status strip (spec §4 header; Addendum §7): domain, identity, current activity. Convoy slots, intent,
        /// ready prompts and ballot countdowns join it once the online convoy session is connected.
        /// </summary>
        void BuildStrip(RectTransform root)
        {
            Image strip = UIFactory.Panel("TopStrip", root, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -64), Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.94f));
            stripBar = UIFactory.Panel("DomainBar", strip.transform, new Vector2(0, 0), new Vector2(0, 1), Vector2.zero, new Vector2(8, 0), SignalTheme.LabelDim);
            stripDomain = UIFactory.Label("Domain", strip.transform, "", SignalTheme.Small, SignalTheme.LabelDim, TextAlignmentOptions.MidlineLeft, true);
            stripDomain.rectTransform.anchorMin = new Vector2(0, 0);
            stripDomain.rectTransform.anchorMax = new Vector2(0, 1);
            stripDomain.rectTransform.offsetMin = new Vector2(28, 0);
            stripDomain.rectTransform.offsetMax = new Vector2(360, 0);
            stripName = UIFactory.Label("Name", strip.transform, "", SignalTheme.Body, SignalTheme.Label, TextAlignmentOptions.MidlineLeft);
            stripName.richText = false; // display names render literally
            stripName.rectTransform.anchorMin = new Vector2(0, 0);
            stripName.rectTransform.anchorMax = new Vector2(0.5f, 1);
            stripName.rectTransform.offsetMin = new Vector2(370, 0);
            stripScreen = UIFactory.Label("Activity", strip.transform, "", SignalTheme.Small, SignalTheme.LabelDim, TextAlignmentOptions.MidlineRight, true);
            stripScreen.rectTransform.anchorMin = new Vector2(0.5f, 0);
            stripScreen.rectTransform.anchorMax = new Vector2(1, 1);
            stripScreen.rectTransform.offsetMax = new Vector2(-28, 0);
            RefreshStrip();
        }

        public void RefreshStrip()
        {
            switch (Domain)
            {
                case SessionDomain.Online:
                    stripDomain.text = "ONLINE";
                    stripBar.color = SignalTheme.Timing;
                    break;
                case SessionDomain.Local:
                    stripDomain.text = "LOCAL / OFFLINE";
                    stripBar.color = SignalTheme.Caution;
                    break;
                default:
                    stripDomain.text = "NOT SIGNED IN";
                    stripBar.color = SignalTheme.LabelDim;
                    break;
            }
            stripName.text = DisplayName;
        }

        internal void OnScreenChanged(UIScreen screen)
        {
            if (stripScreen != null) stripScreen.text = screen.ScreenName.ToUpperInvariant();
            RefreshStrip();
            ScreenChanged?.Invoke(screen);
        }

        // ------------------------------------------------------------------ backdrop

        /// <summary>Loads a real course behind the menus and glides a camera along it (no fake video, no static image).</summary>
        IEnumerator LoadBackdrop()
        {
            if (Application.CanStreamedLevelBeLoaded(backdropCourse))
            {
                AsyncOperation load = SceneManager.LoadSceneAsync(backdropCourse, LoadSceneMode.Single);
                while (!load.isDone) yield return null;
                yield return null;
            }
            if (backdropCamera == null)
            {
                backdropCamera = new GameObject("MenuCamera", typeof(Camera));
                DontDestroyOnLoad(backdropCamera);
                backdropCamera.tag = "MainCamera";
                Cameras.CameraRig.Configure(backdropCamera.GetComponent<Camera>());
                backdropCamera.AddComponent<BackdropDolly>();
            }
            backdropCamera.SetActive(true);
        }

        // ------------------------------------------------------------------ offline races

        /// <summary>Starts a Local-domain race on a course scene: the UI steps aside; results come back to <see cref="Results"/>.</summary>
        public void StartOfflineRace(string courseId, string carId, RaceEventRules rules, List<string> opposingAi)
        {
            StartCoroutine(RunOfflineRace(courseId, carId, rules, opposingAi));
        }

        IEnumerator RunOfflineRace(string courseId, string carId, RaceEventRules rules, List<string> opposingAi)
        {
            Canvas.gameObject.SetActive(false);
            if (backdropCamera != null) backdropCamera.SetActive(false);
            AsyncOperation load = SceneManager.LoadSceneAsync(courseId, LoadSceneMode.Single);
            while (!load.isDone) yield return null;
            yield return null;
            var go = new GameObject("OfflineRace");
            activeRace = go.AddComponent<OfflineRaceSession>();
            activeRace.CarId = carId;
            activeRace.PlayerName = string.IsNullOrEmpty(DisplayName) ? "You" : DisplayName;
            activeRace.Rules = rules;
            activeRace.OpposingAi = opposingAi;
            while (activeRace != null && activeRace.Phase != MatchPhase.Results) yield return null;
            yield return new WaitForSeconds(2.5f); // let the finish banner read before the results page
            List<RaceEntrantResult> results = activeRace != null ? activeRace.Results : null;
            string course = courseId;
            if (activeRace != null) Destroy(activeRace.gameObject);
            activeRace = null;
            Results.Set(course, rules, results);
            Canvas.gameObject.SetActive(true);
            yield return LoadBackdrop();
            Router.Show(Results, true);
        }
    }

    /// <summary>Slow cinematic glide along the loaded course's road for the menu backdrop.</summary>
    public sealed class BackdropDolly : MonoBehaviour
    {
        float distance = 40f;
        bool placed;

        void OnEnable() => placed = false;

        void Update()
        {
            CourseRuntime course = CourseRuntime.Active;
            if (course == null || course.Track == null) return;
            TrackData t = course.Track;
            distance += Time.unscaledDeltaTime * 9f;
            if (distance > t.LengthMetres - 60f) distance = 40f;
            TrackSample here = t.SampleAt(distance);
            TrackSample ahead = t.SampleAt(Mathf.Min(t.LengthMetres - 1f, distance + 45f));
            Vector3 side = here.Right * (Mathf.Sin(distance * 0.004f) * 9f + 6f);
            Vector3 target = here.Position + side + Vector3.up * 5.5f;
            // Snap on the first frame for a course (never ease in from the world origin under the terrain).
            transform.position = placed ? Vector3.Lerp(transform.position, target, 1f - Mathf.Exp(-2f * Time.unscaledDeltaTime)) : target;
            Quaternion look = Quaternion.LookRotation(ahead.Position + Vector3.up * 1.5f - transform.position, Vector3.up);
            transform.rotation = placed ? Quaternion.Slerp(transform.rotation, look, 1f - Mathf.Exp(-1.5f * Time.unscaledDeltaTime)) : look;
            placed = true;
        }
    }
}
