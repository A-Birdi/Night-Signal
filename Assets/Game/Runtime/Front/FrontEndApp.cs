using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Rules;
using NightSignal.Race;
using NightSignal.Track;
using NightSignal.UI;
using TMPro;
using NightSignal.Net;
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
    public sealed partial class FrontEndApp : MonoBehaviour
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
        public readonly ControlsScreen Controls = new ControlsScreen();
        public readonly ResultsScreen Results = new ResultsScreen();
        public readonly ProfileSelectScreen ProfileSelect = new ProfileSelectScreen();
        public readonly NewProfileScreen NewProfile = new NewProfileScreen();
        public readonly CampaignMapScreen CampaignMap = new CampaignMapScreen();
        public readonly ConvoyScreen Convoy = new ConvoyScreen();
        public readonly PocketCircuitScreen PocketCircuit = new PocketCircuitScreen();
        public readonly GreenlightScreen Greenlight = new GreenlightScreen();
        public readonly CapClashScreen CapClash = new CapClashScreen();
        public readonly PitCrewScreen PitCrew = new PitCrewScreen();
        public readonly CanvasScreen ConvoyCanvas = new CanvasScreen();
        public readonly WhileWeWaitScreen WhileWeWait = new WhileWeWaitScreen();
        public readonly FriendsScreen Friends = new FriendsScreen();
        public readonly CourseAccessScreen Courses = new CourseAccessScreen();
        public readonly GarageScreen Garage = new GarageScreen();
        public readonly AppearanceScreen Appearance = new AppearanceScreen();
        /// <summary>Rich-text summary of the last online race (placing, time, settled receipt) for the convoy screen.</summary>
        public string LastOnlineResult { get; private set; }
        /// <summary>UI tours drive online races with the validator autopilot (automation, labelled as such).</summary>
        public bool OnlineAutopilot { get; set; }
        Net.RaceClient onlineRace;

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
            // Stored presentation preferences (text size, contrast, Reduced Motion) apply before any screen is built.
            // Evidence runs keep their own preferences folder so they never touch the player's settings.
            int prefsArg = Array.IndexOf(Environment.GetCommandLineArgs(), "-nsPrefsFolder");
            if (prefsArg >= 0 && prefsArg + 1 < Environment.GetCommandLineArgs().Length)
                DrivingPreferences.FolderOverride = System.IO.Path.GetFullPath(Environment.GetCommandLineArgs()[prefsArg + 1]);
            SettingsScreen.ApplyAccessibility(DrivingPreferences.Current);
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
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-nsUiTourOnline") >= 0)
                StartCoroutine(UiTourOnline());
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-nsYardTour") >= 0)
                StartCoroutine(YardTour());
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-nsAppearanceTour") >= 0)
                StartCoroutine(AppearanceTour());
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-nsInstrumentTour") >= 0)
                StartCoroutine(InstrumentTour());
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-nsCameraTour") >= 0)
                StartCoroutine(CameraTour());
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-nsInstrumentExtremes") >= 0)
                StartCoroutine(InstrumentExtremes());
            int soakArg = Array.IndexOf(Environment.GetCommandLineArgs(), "-nsSoakTour");
            if (soakArg >= 0)
            {
                string[] a = Environment.GetCommandLineArgs();
                int races = soakArg + 1 < a.Length && int.TryParse(a[soakArg + 1], out int n) ? n : 8;
                StartCoroutine(SoakTour(races));
            }
            int social = Array.IndexOf(Environment.GetCommandLineArgs(), "-nsUiTourSocial");
            if (social >= 0 && social + 1 < Environment.GetCommandLineArgs().Length)
                StartCoroutine(UiTourSocial(Environment.GetCommandLineArgs()[social + 1]));
        }

        /// <summary>
        /// Standalone evidence run (<c>-nsUiTour</c>): drives the REAL buttons through title → Offline Play → a new Local
        /// profile → the campaign map → the S01 panel → an autopilot race → results with Local progression → the map again,
        /// saving a screenshot of each page to Builds/Screenshots/tour. Saves go to an isolated folder under the tour
        /// directory (never a player's real profiles). Labelled automation, not a human playtest.
        /// </summary>
        IEnumerator UiTour()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "tour")); // players resolve relative capture paths against the Data folder
            System.IO.Directory.CreateDirectory(dir);
            string profiles = System.IO.Path.Combine(dir, "profiles");
            if (System.IO.Directory.Exists(profiles)) System.IO.Directory.Delete(profiles, true);
            LocalSession.UseFolder(profiles);
            var failures = new List<string>();
            void Shot(string name) => ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, name + ".png"));
            bool Click(string name)
            {
                Button b = GameObject.Find(name)?.GetComponent<Button>();
                if (b == null || !b.interactable) { failures.Add("button not available: " + name); return false; }
                b.onClick.Invoke();
                return true;
            }

            yield return new WaitForSeconds(3f);
            Shot("01-title");
            yield return new WaitForSeconds(1f);
            Click("OfflinePlay");
            yield return new WaitForSeconds(1.2f);
            Shot("02-profiles");
            Click("NewProfile");
            yield return new WaitForSeconds(1.2f);
            TMPro.TMP_InputField field = GameObject.Find("ProfileName")?.GetComponent<TMPro.TMP_InputField>();
            if (field != null) field.text = "Tour Driver"; else failures.Add("name field missing");
            yield return new WaitForSeconds(0.5f);
            Shot("03-new-profile");
            Click("Create");
            yield return new WaitForSeconds(1.2f);
            if (Router.Current != OfflineHub) failures.Add("profile creation did not reach the Offline hub");
            Shot("04-offline-hub");
            Click("Campaign");
            float until = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < until && (Router.Current != CampaignMap || GameObject.Find("Map")?.GetComponent<RawImage>()?.texture == null)) yield return null;
            yield return new WaitForSeconds(2f); // act banner and reveal fade
            Shot("05-campaign-map");
            Click("Node-S01");
            yield return new WaitForSeconds(0.8f);
            Shot("06-stage-panel");
            Click("Race");
            until = Time.realtimeSinceStartup + 30f;
            while (activeRace == null && Time.realtimeSinceStartup < until) yield return null;
            if (activeRace != null)
            {
                activeRace.Autopilot = true;
                yield return new WaitForSeconds(9f);
                Shot("07-race");
                yield return new WaitForSeconds(1f);
                if (activeRace != null) activeRace.SimulationSpeed = 12;
            }
            else failures.Add("the campaign race did not start");
            until = Time.realtimeSinceStartup + 180f;
            while (Router.Current != Results && Time.realtimeSinceStartup < until) yield return null;
            yield return new WaitForSeconds(1.5f);
            Shot("08-results");
            LocalSession s = LocalSession.Current;
            bool cleared = s?.Profile != null && s.Profile.Campaign.IsCleared(CampaignMode.Normal, 1);
            if (!cleared) failures.Add("S01 not cleared in the Local profile");
            Click("Continue");
            yield return new WaitForSeconds(2.5f);
            Shot("09-campaign-after");

            // While We Wait, offline: Pocket Circuit at the Local table (Addendum 02 §5).
            Click("Back");
            yield return new WaitForSeconds(1.2f);
            Click("WhileWeWait");
            yield return new WaitForSeconds(1.2f);
            Shot("10a-while-we-wait");
            Click("Toy-PocketCircuit");
            yield return new WaitForSeconds(2f);
            PocketCircuit.AutoThrottle = TourThrottle;
            Shot("10-pocket-circuit-table");
            yield return new WaitForSeconds(14f);
            Click("View");
            yield return new WaitForSeconds(3f);
            Shot("11-pocket-circuit-chase");
            Click("View");
            yield return new WaitForSeconds(30f);
            Shot("12-pocket-circuit-laps");
            int laps = LocalSession.Current?.Profile != null && Router.Current == PocketCircuit ? CountToyLaps() : 0;
            if (laps < 1) failures.Add("no Pocket Circuit lap completed");
            Debug.Log($"[NightSignal.UiTour] pocket circuit laps: {laps}");
            Click("Back");
            yield return new WaitForSeconds(1.5f);

            // Greenlight: three Lights Out attempts, a scripted press 230 ms after the lights go out (not a human).
            Click("Toy-Greenlight");
            yield return new WaitForSeconds(1.5f);
            Greenlight.AutoPress = (variant, cue, t) => variant == Core.Toys.Greenlight.GreenlightVariant.LightsOut
                ? t >= cue.HiddenDelayMs + 230 : t >= cue.Target * cue.SweepMs;
            for (int i = 0; i < 3; i++)
            {
                Click("GreenlightStart");
                yield return new WaitForSeconds(6.5f);
                if (i == 1) Shot("13-greenlight");
            }
            yield return new WaitForSeconds(1f);
            Shot("14-greenlight-results");
            int cleanReactions = Greenlight.CleanAttempts;
            if (cleanReactions < 3) failures.Add($"Greenlight: {cleanReactions} clean attempts of 3");
            Debug.Log($"[NightSignal.UiTour] greenlight clean attempts: {cleanReactions}");
            Greenlight.AutoPress = null;
            Click("Back");
            yield return new WaitForSeconds(1.2f);

            // Cap Clash: aim straight with the power the Core preview says settles closest, three queued shots.
            Click("Toy-CapClash");
            yield return new WaitForSeconds(2f);
            CapClash.AutoAim = TourCapAim;
            for (int i = 0; i < 3; i++)
            {
                yield return new WaitForSeconds(0.6f);
                Click("CapShoot");
                yield return new WaitForSeconds(4.5f);
                if (i == 0) Shot("15-cap-clash");
            }
            Shot("16-cap-clash-standings");
            Debug.Log($"[NightSignal.UiTour] cap clash shots: {CapClash.MyShots}, on the scoring area: {CapClash.MyOnBoardShots}");
            if (CapClash.MyShots < 3) failures.Add($"Cap Clash: {CapClash.MyShots} settled shots of 3");
            if (CapClash.MyOnBoardShots < 1) failures.Add("Cap Clash: no shot reached the scoring area");
            CapClash.AutoAim = null;
            Click("Back");
            yield return new WaitForSeconds(1.2f);

            // Pit-Crew: claim the first available task and lock each step near the centre, three operations.
            Click("Toy-PitCrew");
            yield return new WaitForSeconds(2f);
            PitCrew.AutoLock = rel => rel < 0.05;
            for (int k = 0; k < 3; k++)
            {
                int before = PitCrew.OperationsDoneByMe;
                Click("Task0");
                float taskUntil = Time.realtimeSinceStartup + 30f;
                while (PitCrew.OperationsDoneByMe == before && Time.realtimeSinceStartup < taskUntil) yield return null;
                if (k == 1) Shot("17-pit-crew");
                yield return new WaitForSeconds(0.8f);
            }
            Shot("18-pit-crew-model");
            Debug.Log($"[NightSignal.UiTour] pit-crew operations completed: {PitCrew.OperationsDoneByMe}");
            if (PitCrew.OperationsDoneByMe < 3) failures.Add($"Pit-Crew: {PitCrew.OperationsDoneByMe} operations of 3");
            PitCrew.AutoLock = null;
            Click("Back");
            yield return new WaitForSeconds(1.2f);

            // Convoy Canvas: three scripted strokes (a wave, a ring, a signal zigzag) through the real operations.
            Click("Toy-Canvas");
            yield return new WaitForSeconds(2f);
            ConvoyCanvas.AutoDraw = TourStrokes();
            yield return new WaitForSeconds(3f);
            Shot("19-convoy-canvas");
            Debug.Log($"[NightSignal.UiTour] canvas marks: {ConvoyCanvas.MyObjects}");
            if (ConvoyCanvas.MyObjects < 3) failures.Add($"Canvas: {ConvoyCanvas.MyObjects} marks of 3");
            if (ConvoyCanvas.MyStrokePoints < 94) failures.Add($"Canvas: {ConvoyCanvas.MyStrokePoints} stroke points of 94 reached the sheet");
            ConvoyCanvas.AutoDraw = null;
            Click("Back");
            yield return new WaitForSeconds(1.2f);
            Click("Back");
            yield return new WaitForSeconds(1.2f);

            // Garage: tyres into the draft → Buy & Apply (quote, then the confirming press) → save a loadout → restore the
            // protected "before last apply" build into the draft → revert; then re-open the profile FROM DISK and check it.
            Click("Garage");
            yield return new WaitForSeconds(1.5f);
            Shot("20-garage");
            Click("Slot-tyres");
            yield return new WaitForSeconds(0.6f);
            Click("Part1"); // the cheapest compatible tyre (stock is row 0)
            yield return new WaitForSeconds(0.8f);
            string tyre = Garage.Workspace?.Draft?.Build.PartIn(Core.Builds.PartSlot.Tyres);
            Shot("21-garage-draft");
            Click("BuyAndApply");
            yield return new WaitForSeconds(0.8f);
            Shot("22-garage-quote");
            yield return new WaitForSeconds(0.5f); // the capture happens at the end of the frame: let it land first
            long walletBefore = LocalSession.Current.Profile.WalletBalance;
            Click("BuyAndApply");
            yield return new WaitForSeconds(1f);
            Shot("23-garage-bought");
            string applied = Garage.Workspace?.Applied.Build.PartIn(Core.Builds.PartSlot.Tyres);
            long walletAfter = LocalSession.Current.Profile.WalletBalance;
            Debug.Log($"[NightSignal.UiTour] garage: {tyre} applied={applied} wallet {walletBefore} -> {walletAfter} ({Garage.Message})");
            if (tyre == null || applied != tyre || walletAfter >= walletBefore) failures.Add("Garage: buy-and-apply did not apply " + tyre);
            Click("SaveLoadout");
            yield return new WaitForSeconds(0.8f);
            Click("Ref-before-last-apply");
            yield return new WaitForSeconds(0.8f);
            Shot("24-garage-restore");
            bool restoredStock = Garage.Workspace?.Draft?.Build.PartIn(Core.Builds.PartSlot.Tyres) == null;
            if (!restoredStock) failures.Add("Garage: before-last-apply did not restore the stock tyres into the draft");

            // Test Yard: B = the restored stock draft, A = the race build with the bought tyres; the same scripted launch
            // to 100 km/h and full stop on the braking straight for each (identical inputs, same surface and station).
            Click("TestYardB");
            until = Time.realtimeSinceStartup + 40f;
            while ((ActiveYard == null || !ActiveYard.Ready) && Time.realtimeSinceStartup < until) yield return null;
            if (ActiveYard == null) failures.Add("the Test Yard did not open");
            else
            {
                bool stopPhase = false;
                ActiveYard.Script = (st, t) =>
                {
                    if (t < 0.05f) stopPhase = false;
                    if (st.SpeedKmh >= 100f) stopPhase = true;
                    if (stopPhase && st.SpeedKmh < 0.3f) return Vehicle.DriverInput.Neutral; // at rest: do not select reverse
                    return stopPhase ? Vehicle.DriverInput.Quantize(0f, 0f, 1f, Vehicle.InputButtons.None) : Vehicle.DriverInput.Quantize(0f, 1f, 0f, Vehicle.InputButtons.None);
                };
                foreach (bool side in new[] { true, false })
                {
                    if (!side) ActiveYard.ResetAndDrive(false, 0);
                    until = Time.realtimeSinceStartup + 40f;
                    while (ActiveYard.CurrentRun != null && ActiveYard.CurrentRun.StopMetres < 0f && Time.realtimeSinceStartup < until) yield return null;
                    yield return new WaitForSeconds(1f);
                    Shot(side ? "27-test-yard-b" : "28-test-yard-a");
                    yield return new WaitForSeconds(0.3f); // let the capture land before the next reset
                }
                ActiveYard.ResetAndDrive(true, 0); // closes A's run into the comparison
                yield return new WaitForSeconds(0.5f);
                ActiveYard.RequestExit();
                until = Time.realtimeSinceStartup + 40f;
                while ((ActiveYard != null || Router.Current != Garage) && Time.realtimeSinceStartup < until) yield return null;
                yield return new WaitForSeconds(1.5f);
                Shot("29-garage-after-yard");
                TestYardRun yardA = LastYardRuns.FirstOrDefault(r => !r.B), yardB = LastYardRuns.FirstOrDefault(r => r.B);
                Debug.Log($"[NightSignal.UiTour] test yard A (bought tyres): {yardA?.Summary()}");
                Debug.Log($"[NightSignal.UiTour] test yard B (stock draft): {yardB?.Summary()}");
                if (yardA == null || yardB == null || yardA.StopMetres <= 0f || yardB.StopMetres <= 0f || yardA.ZeroTo100 <= 0f || yardB.ZeroTo100 <= 0f)
                    failures.Add("Test Yard: A/B launch-and-stop runs were not both measured");
            }
            Click("DiscardDraft");
            yield return new WaitForSeconds(0.8f);
            Click("Back");
            yield return new WaitForSeconds(1.2f);
            string profileId = LocalSession.Current.Profile.ProfileId;
            string instance = LocalSession.Current.Profile.Cars[0].InstanceId;
            if (!LocalSession.Current.Open(profileId, out string reopenMessage)) failures.Add("re-open profile: " + reopenMessage);
            Core.Profiles.OwnedCar reCar = LocalSession.Current.Profile.Cars.FirstOrDefault(c => c.InstanceId == instance);
            var reload = Core.Profiles.LocalGarage.LoadWorkspace(LocalSession.Current.Profile, LocalSession.Current.Catalogue,
                NightSignal.Content.ContentLibrary.Load().Parts, instance, DateTime.UtcNow);
            bool persisted = reCar != null && tyre != null && reCar.OwnsPart(tyre) && reload.Ok && reload.Workspace.Applied.Build.PartIn(Core.Builds.PartSlot.Tyres) == tyre
                             && reload.Workspace.Loadouts.Count == 1 && reload.Workspace.LoadoutCapacity >= 8 && reload.Workspace.Reference(Core.Builds.BuildReferenceKind.BeforeLastApply) != null;
            Debug.Log($"[NightSignal.UiTour] garage persisted: {persisted} (loadouts {reload.Workspace?.Loadouts.Count}/{reload.Workspace?.LoadoutCapacity}, owns {tyre}: {reCar?.OwnsPart(tyre)})");
            if (!persisted) failures.Add("Garage: the bought part, applied build, loadout or reference was not persisted");

            // Race S02 with the upgraded car: the race must use the APPLIED build and record it as Last Race Build.
            Click("Campaign");
            until = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < until && Router.Current != CampaignMap) yield return null;
            yield return new WaitForSeconds(2f);
            Click("Node-S02");
            yield return new WaitForSeconds(0.8f);
            Shot("25-stage-upgraded-car");
            Click("Race");
            until = Time.realtimeSinceStartup + 30f;
            while (activeRace == null && Time.realtimeSinceStartup < until) yield return null;
            string racedHash = activeRace?.PlayerSpec?.BuildHash; // what the session builds the player's car from
            if (activeRace != null)
            {
                activeRace.Autopilot = true;
                activeRace.SimulationSpeed = 12;
            }
            else failures.Add("the upgraded-car race did not start");
            until = Time.realtimeSinceStartup + 180f;
            while (Router.Current != Results && Time.realtimeSinceStartup < until) yield return null;
            yield return new WaitForSeconds(1.5f);
            Shot("26-results-upgraded");
            var afterRace = Core.Profiles.LocalGarage.LoadWorkspace(LocalSession.Current.Profile, LocalSession.Current.Catalogue,
                NightSignal.Content.ContentLibrary.Load().Parts, instance, DateTime.UtcNow);
            Core.Builds.BuildReference lastRace = afterRace.Workspace?.Reference(Core.Builds.BuildReferenceKind.LastRaceBuild);
            bool recorded = lastRace != null && lastRace.Build.PartIn(Core.Builds.PartSlot.Tyres) == tyre;
            Debug.Log($"[NightSignal.UiTour] upgraded race: spec {(racedHash ?? "none").Substring(0, Math.Min(12, (racedHash ?? "none").Length))}, last race build recorded {recorded} ({lastRace?.Context})");
            if (racedHash == null) failures.Add("the upgraded car raced without its applied build");
            if (!recorded) failures.Add("Last Race Build was not recorded with the bought tyres");
            Click("Continue");
            yield return new WaitForSeconds(2.5f);
            Click("Back");
            yield return new WaitForSeconds(1.2f);

            string summary = failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures);
            bool toySaved = LocalSession.Current?.ToySnapshot(Toys.LocalToyHost.DocumentKey) != null;
            Debug.Log($"[NightSignal.UiTour] {summary} (profile wallet {s?.Profile?.WalletBalance}, S01 cleared {cleared}, toy table saved {toySaved})");
            yield return new WaitForSeconds(1f);
            Application.Quit(failures.Count == 0 ? 0 : 1);
        }

        /// <summary>Tour driver for the toy: brakes for the tightest bend ahead so laps stay clean (scripted, not a human).</summary>
        static float TourThrottle(Core.Toys.PocketCircuit.PocketCircuitTable t, string member)
        {
            Core.Toys.PocketCircuit.SlotCarState car = t.Car(member);
            if (car == null) return 0f;
            Core.Toys.PocketCircuit.SlotLane lane = t.Track.Lane(car.Lane);
            double kMax = 0;
            for (double ahead = 0; ahead <= 0.45; ahead += 0.03)
                kMax = System.Math.Max(kMax, System.Math.Abs(lane.Curvature[lane.IndexAt(lane.Wrap(car.S + ahead))]));
            double vSafe = kMax > 1e-6 ? System.Math.Sqrt(t.Physics.LateralGrip * 0.8 / kMax) : t.Physics.MotorTopSpeed;
            return (float)System.Math.Min(1.0, System.Math.Max(0.15, vSafe / t.Physics.MotorTopSpeed));
        }

        int CountToyLaps() => PocketCircuit.CompletedLaps;

        /// <summary>Scripted Canvas strokes for the tour (sheet units 4096 × 2048).</summary>
        static IEnumerator<Vector2Int[]> TourStrokes()
        {
            yield return Enumerable.Range(0, 40).Select(i => new Vector2Int(300 + i * 60, 1300 + (int)(Mathf.Sin(i * 0.45f) * 260))).ToArray();
            yield return null;
            yield return Enumerable.Range(0, 49).Select(i => new Vector2Int(2900 + (int)(Mathf.Cos(i / 48f * Mathf.PI * 2) * 420), 900 + (int)(Mathf.Sin(i / 48f * Mathf.PI * 2) * 420))).ToArray();
            yield return null;
            yield return new[] { new Vector2Int(500, 700), new Vector2Int(800, 300), new Vector2Int(1100, 700), new Vector2Int(1400, 300), new Vector2Int(1700, 700) };
            while (true) yield return null;
        }

        static readonly System.Collections.Generic.Dictionary<string, float> tourCapPower = new System.Collections.Generic.Dictionary<string, float>();

        /// <summary>Tour aim for Cap Clash: straight up-table, power chosen by the Core preview (scripted, not a human).</summary>
        static (float, float, float)? TourCapAim(Core.Toys.CapClash.CapArrangementDef a, Core.Toys.CapClash.CapTargetDef t)
        {
            if (t == null) return null;
            float x = Mathf.Clamp((float)t.X, (float)a.LaunchMinX, (float)a.LaunchMaxX);
            string key = a.Id + "/" + t.Id;
            if (!tourCapPower.TryGetValue(key, out float best))
            {
                Core.Toys.CapClash.CapPhysicsDef ph = Content.ContentLibrary.Load().Toys.CapClash.Physics;
                double bestD = double.MaxValue;
                for (float p = 0.2f; p <= 1f; p += 0.01f)
                {
                    Core.Toys.CapClash.ShotOutcome o = Core.Toys.CapClash.CapPhysics.Predict(a, ph, t, new System.Collections.Generic.List<Core.Toys.CapClash.CapBody>(), "tour", 0, p, x);
                    if (o.OnBoard && o.Distance.HasValue && o.Distance.Value < bestD) { bestD = o.Distance.Value; best = p; }
                }
                tourCapPower[key] = best;
            }
            return (0f, best, x);
        }

        // ------------------------------------------------------------------ online

        /// <summary>Takes ownership of a signed-in session: pumped every frame; a match allocation starts the race.</summary>
        public void AttachOnline(OnlineSession session)
        {
            session.Client.MatchAllocated += p => StartCoroutine(RunOnlineRace(p));
            Domain = SessionDomain.Online;
            DisplayName = session.DisplayName;
            RefreshStrip();
        }

        void Update() => OnlineSession.Current?.Tick();

        /// <summary>An online race (racing or spectating) is running.</summary>
        public bool InOnlineRace => onlineRace != null;

        /// <summary>Watch the convoy's running race with a spectator ticket (spec §4.4).</summary>
        public void StartSpectating(Newtonsoft.Json.Linq.JObject ticket) => StartCoroutine(RunOnlineRace(ticket, spectating: true));

        /// <summary>
        /// Joins the allocated match with the ticket the control plane issued (never a local shortcut), races with the
        /// player's controls, then returns to the convoy with the settled receipt. The race scene replaces the menus.
        /// </summary>
        IEnumerator RunOnlineRace(Newtonsoft.Json.Linq.JObject allocation, bool spectating = false)
        {
            if (onlineRace != null) yield break;
            OnlineSession session = OnlineSession.Current;
            string matchId = (string)allocation["matchId"];
            // The server has already paused the toys at the match commit; leave the table view so nothing renders under the race.
            if (Router.Current == PocketCircuit) PocketCircuit.CloseNow();
            if (Router.Current == Greenlight) Greenlight.OnHide();
            if (Router.Current == CapClash) CapClash.OnHide();
            if (Router.Current == PitCrew) PitCrew.OnHide();
            if (Router.Current == ConvoyCanvas) ConvoyCanvas.OnHide();
            _ = session?.Request("presence.set", new { presence = "LoadingRace" }, quiet: true);
            Canvas.gameObject.SetActive(false);
            if (backdropCamera != null) backdropCamera.SetActive(false);
            var go = new GameObject("RaceClient");
            DontDestroyOnLoad(go);
            onlineRace = go.AddComponent<Net.RaceClient>();
            onlineRace.Autopilot = OnlineAutopilot;
            onlineRace.Connect((string)allocation["server"]["host"], (ushort)(int)allocation["server"]["port"], (string)allocation["ticket"]);
            bool racing = false;
            while (onlineRace.Results == null && onlineRace.Phase != MatchPhase.Aborted && onlineRace.DisconnectReason == null)
            {
                if (!racing && (onlineRace.Phase == MatchPhase.Racing || (spectating && onlineRace.Spectating)))
                {
                    racing = true;
                    _ = session?.Request("presence.set", new { presence = spectating ? "Spectating" : "InRace" }, quiet: true);
                }
                yield return null;
            }
            Net.MatchResults results = onlineRace.Results;
            yield return new WaitForSeconds(results != null ? 4f : 1.5f); // let the finish banner read
            Destroy(go);
            onlineRace = null;

            var sb = new System.Text.StringBuilder("<color=#9A968D>LAST RACE</color>\n");
            Net.ResultEntrant mine = results?.Entrants.FirstOrDefault(e => e.EntrantId == session?.AccountId);
            if (results == null) sb.Append("The race ended without results (aborted or disconnected): nothing was settled.\n");
            else if (mine == null) sb.Append("You spectated this race.\n");
            else sb.Append((mine.Outcome == "Finished" ? $"P{mine.Placement} of {results.Entrants.Count}   {ResultsScreen.FormatRaceTime(mine.FinishTimeMicros)}" : mine.Outcome)
                           + (mine.RawDriftScore > 0 ? $"   <color=#3EC6D8>drift {mine.RawDriftScore:N0} pts</color>" : "") + "\n");
            LastOnlineResult = sb.ToString();
            Canvas.gameObject.SetActive(true);
            yield return LoadBackdrop();
            Router.Show(Convoy, false);
            _ = session?.Request("presence.set", new { presence = "InMenus" }, quiet: true);
            if (session != null && results != null) StartCoroutine(FetchReceipt(session, matchId));
        }

        /// <summary>The server-settled receipt (money, clears, unlocks) — shown as the server states it.</summary>
        IEnumerator FetchReceipt(OnlineSession session, string matchId)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                var t = session.Client.GetWithStatus($"/v1/matches/{matchId}/receipt");
                while (!t.IsCompleted) yield return null;
                if (!t.IsFaulted && t.Result.status == 200 && (string)t.Result.body["status"] != "pending")
                {
                    Newtonsoft.Json.Linq.JObject r = t.Result.body;
                    var sb = new System.Text.StringBuilder(LastOnlineResult);
                    Newtonsoft.Json.Linq.JToken stage = r["stage"];
                    if (stage != null && stage.Type == Newtonsoft.Json.Linq.JTokenType.Object)
                        sb.Append((bool?)stage["earnedClear"] == true ? "<color=#3EC6D8>Stage cleared</color>" + ((bool?)r["firstClearAwarded"] == true ? " — first clear" : "") + "\n"
                                                                     : $"<color=#F2A541>Stage not cleared</color>  <size=85%>{(string)stage["reason"]}</size>\n");
                    if (r["teamTrial"] is Newtonsoft.Json.Linq.JObject tt)
                    {
                        string verdict = (string)tt["verdict"];
                        string kind = (string)tt["kind"];
                        string Value(Newtonsoft.Json.Linq.JToken v) => v == null || v.Type == Newtonsoft.Json.Linq.JTokenType.Null ? "-"
                            : kind == "drift" ? $"{(long)v:N0} pts"
                            : kind == "mean" ? RaceHudTime((long)v / Core.Rules.Limits.TeamTrialSideSize) + " mean" // team values are totals
                            : RaceHudTime((long)v);
                        string mine = kind == "mean" && tt["playerTeamMeanMs"] != null && tt["playerTeamMeanMs"].Type != Newtonsoft.Json.Linq.JTokenType.Null
                            ? RaceHudTime((long)(double)tt["playerTeamMeanMs"]) + " mean" : Value(tt["playerTeamValue"]);
                        string colour = verdict == "victory" ? "#3EC6D8" : verdict == "defeat" ? "#F2A541" : "#D8D4CB";
                        sb.Append($"<color={colour}>Team Trial {verdict?.ToUpperInvariant()}</color>  <size=85%>your team {mine}, opponents {Value(tt["opposingTeamValue"])}" +
                                  ((bool?)tt["provisional"] == true ? ", provisional targets" : "") + "</size>\n");
                    }
                    sb.Append($"Credits +{(long?)r["payout"]?["total"] ?? 0:N0}   ·   balance {(long?)r["balanceAfter"] ?? 0:N0} cr\n");
                    foreach (Newtonsoft.Json.Linq.JToken cue in (r["musicUnlocked"] as Newtonsoft.Json.Linq.JArray) ?? new Newtonsoft.Json.Linq.JArray())
                        sb.Append($"<color=#3EC6D8>+</color> Soundtrack {(string)cue}\n");
                    LastOnlineResult = sb.ToString();
                    var me = session.RefreshMe();
                    while (!me.IsCompleted) yield return null;
                    yield break;
                }
                yield return new WaitForSeconds(1f);
            }
        }

        static string RaceHudTime(long ms) => UI.RaceHud.FormatTime(ms / 1000.0);

        /// <summary>
        /// Online evidence run (<c>-nsUiTourOnline</c>, needs the local control plane and a registered game server): the
        /// REAL buttons from Online Login (a development account from the project's seed file) → convoy → Mode Ready →
        /// Enter Mode → Propose Event → Event Ready → Start → a server-authoritative race (validator autopilot through
        /// normal inputs) → settled receipt → Continue → Advance. Screenshots in Builds/Screenshots/tour-online.
        /// Automation, not a human playtest.
        /// </summary>
        IEnumerator UiTourOnline()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "tour-online"));
            System.IO.Directory.CreateDirectory(dir);
            var failures = new List<string>();
            OnlineAutopilot = true;
            void Shot(string name) => ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, name + ".png"));
            void Note(string s) => Debug.Log("[NightSignal.UiTourOnline] " + s);
            bool Click(string name)
            {
                Button b = GameObject.Find(name)?.GetComponent<Button>();
                if (b == null || !b.interactable) { failures.Add("button not available: " + name); Note("button not available: " + name); return false; }
                b.onClick.Invoke();
                return true;
            }
            Newtonsoft.Json.Linq.JObject State() => OnlineSession.Current?.Convoy;
            IEnumerator Until(Func<bool> condition, float seconds, string what)
            {
                float until = Time.realtimeSinceStartup + seconds;
                while (!condition() && Time.realtimeSinceStartup < until) yield return null;
                if (!condition()) { failures.Add("timed out: " + what); Note("timed out: " + what + " (" + OnlineSession.Current?.LastError + ")"); }
            }

            NetConfig cfg = NetConfig.FromCommandLine();
            Newtonsoft.Json.Linq.JToken account = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(cfg.DevSeedFile))["accounts"][cfg.DevAccount];
            yield return new WaitForSeconds(3f);
            Shot("01-title");
            Click("OnlineLogin");
            yield return new WaitForSeconds(1.2f);
            GameObject.Find("Email").GetComponent<TMP_InputField>().text = (string)account["email"];
            GameObject.Find("Password").GetComponent<TMP_InputField>().text = (string)account["devOnlyPassword"];
            Shot("02-sign-in");
            Click("SignIn");
            yield return Until(() => Router.Current == Convoy && OnlineSession.Current != null, 20f, "signed in");
            yield return new WaitForSeconds(1.5f);
            if (OnlineSession.Current?.StarterCarId == null) { Click("ChooseStarter"); yield return Until(() => OnlineSession.Current.StarterCarId != null, 10f, "starter chosen"); }
            Shot("03-online");
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-nsUiTourGarage") >= 0)
            {
                // Online Garage through the real screen: switch the tyres (buy them if this instance does not own them) so
                // the race below runs a build the server must re-resolve and verify.
                Click("OpenGarage");
                yield return Until(() => Router.Current == Garage && Garage.Workspace != null && !Garage.Busy, 20f, "online garage loaded");
                yield return new WaitForSeconds(1f);
                Shot("03a-online-garage");
                string tyreBefore = Garage.Workspace?.Applied.Build.PartIn(Core.Builds.PartSlot.Tyres);
                Click("Slot-tyres");
                yield return new WaitForSeconds(0.5f);
                Click(tyreBefore == null ? "Part1" : "Part0"); // stock ↔ the cheapest tyre
                yield return Until(() => !Garage.Busy, 20f, "draft edited");
                yield return new WaitForSeconds(0.8f);
                string wanted = Garage.Workspace?.Draft?.Build.PartIn(Core.Builds.PartSlot.Tyres);
                Button buy = GameObject.Find("BuyAndApply")?.GetComponent<Button>();
                if (buy != null && buy.gameObject.activeInHierarchy)
                {
                    Click("BuyAndApply");
                    yield return Until(() => !Garage.Busy, 20f, "quote");
                    yield return new WaitForSeconds(0.8f);
                    Shot("03b-online-garage-quote");
                    yield return new WaitForSeconds(0.3f);
                    Click("BuyAndApply");
                }
                else Click("ApplyDraft");
                yield return Until(() => !Garage.Busy, 20f, "applied");
                yield return new WaitForSeconds(1f);
                Shot("03c-online-garage-applied");
                string tyreAfter = Garage.Workspace?.Applied.Build.PartIn(Core.Builds.PartSlot.Tyres);
                Note($"online garage: tyres {tyreBefore ?? "stock"} -> {tyreAfter ?? "stock"} (wanted {wanted ?? "stock"}); applied hash {Garage.Workspace?.Applied.BuildHash} ({Garage.Message})");
                if (tyreAfter != wanted) failures.Add("online garage: the tyre change was not applied");
                Click("Back");
                yield return Until(() => Router.Current == Convoy, 10f, "back at the convoy screen");
                yield return new WaitForSeconds(1f);
            }
            string onlineLivery = null, onlineLiveryHash = null;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-nsUiTourAppearance") >= 0)
            {
                // Online appearance through the real screens: the control plane validates the livery (ownership included),
                // stores its canonical form and hash, and freezes it into the race roster the game server relays.
                IEnumerator Press(string row, int times)
                {
                    for (int i = 0; i < times; i++)
                    {
                        Click(row + "/Next");
                        yield return new WaitForSeconds(0.25f);
                    }
                }
                Click("OpenGarage");
                yield return Until(() => Router.Current == Garage && Garage.Workspace != null && !Garage.Busy, 20f, "online garage loaded");
                yield return new WaitForSeconds(0.8f);
                Click("OpenAppearance");
                yield return Until(() => Router.Current == Appearance && Appearance.Editor != null, 10f, "appearance open");
                yield return new WaitForSeconds(0.8f);
                string liveryBefore = Appearance.Workspace.AppliedLiveryHash;
                yield return Press("Front", 1);
                yield return Press("Rear aero", 1);
                yield return Press("Section", 2); // paint
                yield return Press("Colour", 2 + (string.IsNullOrEmpty(liveryBefore) ? 0 : 1));
                yield return Press("Section", 1); // lights & plate
                var plateField = GameObject.Find("Appearance-PlateText")?.GetComponent<TMPro.TMP_InputField>();
                if (plateField != null) { plateField.text = "NS ONL"; plateField.onEndEdit.Invoke(plateField.text); }
                yield return Press("Section", 1); // decals
                Click("Appearance-AddDecal");
                yield return new WaitForSeconds(0.3f);
                for (int i = 0; i < 6; i++) { Click("Appearance-Larger"); yield return new WaitForSeconds(0.15f); }
                Click("Appearance-Mirror");
                yield return new WaitForSeconds(0.6f);
                Shot("03d-online-appearance-draft");
                yield return new WaitForSeconds(0.3f);
                Click("Appearance-Apply");
                yield return Until(() => !Appearance.Busy && !Appearance.Editor.IsDirty, 20f, "livery applied");
                yield return new WaitForSeconds(0.8f);
                Shot("03e-online-appearance-applied");
                yield return new WaitForSeconds(0.3f);
                onlineLivery = Appearance.Workspace.AppliedLivery;
                onlineLiveryHash = Appearance.Workspace.AppliedLiveryHash;
                Note($"online appearance: {Appearance.Message} hash {liveryBefore} -> {onlineLiveryHash} ({onlineLivery?.Length} chars)");
                if (string.IsNullOrEmpty(onlineLivery) || onlineLiveryHash == liveryBefore) failures.Add("online appearance: the livery was not applied: " + Appearance.Message);
                Click("Back");
                yield return Until(() => Router.Current == Garage, 10f, "back at the garage");
                yield return new WaitForSeconds(0.8f);
                Click("Back");
                yield return Until(() => Router.Current == Convoy, 10f, "back at the convoy screen");
                yield return new WaitForSeconds(1f);
            }
            Click("CreateConvoy");
            yield return Until(() => OnlineSession.Current.InConvoy && OnlineSession.Current.MyMember?["carId"]?.Type == Newtonsoft.Json.Linq.JTokenType.String, 10f, "convoy created with a loadout");
            yield return new WaitForSeconds(0.8f);
            bool freeplayTour = Array.IndexOf(Environment.GetCommandLineArgs(), "-nsUiTourFreeplay") >= 0;
            if (freeplayTour) Convoy.SelectIntent(2); // Freeplay · Sprint, decided by a course vote
            string[] tourArgs = Environment.GetCommandLineArgs();
            int intentArg = Array.IndexOf(tourArgs, "-nsUiTourIntent");
            if (intentArg >= 0 && intentArg + 1 < tourArgs.Length) Convoy.SelectIntent(int.Parse(tourArgs[intentArg + 1]));
            int trialArg = Array.IndexOf(tourArgs, "-nsUiTourTrial");
            string tourTrial = trialArg >= 0 && trialArg + 1 < tourArgs.Length ? tourArgs[trialArg + 1] : null;
            if (tourTrial != null) Convoy.SelectIntent(5); // Challenges · Team Trial
            Click("ProposeIntent");
            yield return Until(() => State()?["intent"]?.Type == Newtonsoft.Json.Linq.JTokenType.Object, 20f, "intent set");
            yield return new WaitForSeconds(0.8f);
            // The leader's own proposal already counts as their Mode Ready (Addendum 01 §7): only ready if not yet ready.
            if ((bool?)OnlineSession.Current.MyMember?["modeReady"] != true) Click("ModeReady");
            yield return Until(() => (bool?)OnlineSession.Current.MyMember?["modeReady"] == true, 10f, "mode ready");
            yield return new WaitForSeconds(0.8f);
            Shot("04-mode-ready");
            Click("EnterMode");
            yield return Until(() => (bool?)State()?["modeEntered"] == true, 10f, "mode entered");
            yield return new WaitForSeconds(0.8f);
            Shot("05-event-selection");
            if (freeplayTour)
            {
                // Course vote: voting on (15 s) → open → cast → server deadline → leader draws → proposal from the draw.
                Click("VotingToggle");
                yield return Until(() => (bool?)(State()?["voting"] as Newtonsoft.Json.Linq.JObject)?["enabled"] == true, 10f, "voting on");
                yield return Until(() => GameObject.Find("OpenVote")?.GetComponent<Button>()?.interactable == true, 20f, "vote can open");
                yield return Until(() => !Convoy.Busy, 10f, "request settled");
                Click("OpenVote");
                yield return Until(() => (string)(State()?["ballot"] as Newtonsoft.Json.Linq.JObject)?["state"] == "open", 10f, "ballot open");
                yield return new WaitForSeconds(1f);
                yield return Until(() => !Convoy.Busy, 10f, "request settled");
                Click("CastVote");
                yield return new WaitForSeconds(1.5f);
                Shot("05b-vote-open");
                yield return Until(() => (string)(State()?["ballot"] as Newtonsoft.Json.Linq.JObject)?["state"] == "frozen", 30f, "ballot frozen at the server deadline");
                yield return Until(() => !Convoy.Busy, 10f, "request settled");
                Click("DrawVote");
                yield return Until(() => (State()?["ballot"] as Newtonsoft.Json.Linq.JObject)?["result"]?.Type == Newtonsoft.Json.Linq.JTokenType.Object, 10f, "course drawn");
                yield return new WaitForSeconds(1f);
                Shot("05c-vote-drawn");
            }
            else
            {
                // Readiness requests are rate-limited (15 s): wait until the button says it is available, like a player would.
                yield return Until(() => GameObject.Find("ProposeEvent")?.GetComponent<Button>()?.interactable == true, 20f, "propose available");
                int courseArg = Array.IndexOf(tourArgs, "-nsUiTourCourse");
                if (courseArg >= 0 && courseArg + 1 < tourArgs.Length)
                {
                    string tourCourse = tourArgs[courseArg + 1];
                    yield return Until(() => Convoy.SelectCourse(tourCourse), 10f, "course " + tourCourse + " offered");
                    yield return new WaitForSeconds(0.5f);
                }
                if (tourTrial != null)
                {
                    yield return Until(() => Convoy.SelectTrial(tourTrial, "standard"), 10f, "team trial listed");
                    yield return new WaitForSeconds(0.8f);
                    Shot("05t-team-trial");
                }
                Click("ProposeEvent");
            }
            yield return Until(() => State()?["eventProposal"]?.Type == Newtonsoft.Json.Linq.JTokenType.Object, 25f, "event proposed");
            yield return new WaitForSeconds(0.8f);
            if ((bool?)OnlineSession.Current.MyMember?["eventReady"] != true) Click("EventReady");
            yield return Until(() => (bool?)OnlineSession.Current.MyMember?["eventReady"] == true, 10f, "event ready");
            yield return new WaitForSeconds(0.8f);
            Shot("06-event-ready");

            // While We Wait: sit at the convoy's shared Pocket Circuit table while Event Ready (Addendum 02 §1-2).
            Click("WhileWeWait");
            yield return new WaitForSeconds(1.2f);
            Click("Toy-PocketCircuit");
            yield return new WaitForSeconds(2.5f);
            PocketCircuit.AutoThrottle = TourThrottle;
            double before = PocketCircuit.MyCarProgress;
            yield return new WaitForSeconds(12f);
            double after = PocketCircuit.MyCarProgress;
            Shot("06b-table-while-ready");
            if (!PocketCircuit.OnlineTable) failures.Add("the table was not the convoy's shared table");
            if (after <= before + 0.5) failures.Add($"toy car did not move on the shared table ({before:F2} -> {after:F2})");
            if ((bool?)OnlineSession.Current.MyMember?["eventReady"] != true) failures.Add("using the diversion cleared Event Ready");
            Note($"shared table: progress {before:F2} -> {after:F2}, still event ready {(bool?)OnlineSession.Current.MyMember?["eventReady"]}");
            PocketCircuit.AutoThrottle = null;
            Click("Back");
            yield return new WaitForSeconds(1.5f);
            Click("Back");
            yield return new WaitForSeconds(1.5f);
            Click("StartEvent");
            yield return Until(() => onlineRace != null, 30f, "match allocated");
            yield return Until(() => onlineRace == null || onlineRace.Phase == MatchPhase.Racing, 60f, "race started");
            yield return new WaitForSeconds(12f);
            Shot("07-online-race");
            if (onlineLivery != null)
            {
                // The roster came from the game server's match message: it relays the livery the control plane froze.
                Newtonsoft.Json.Linq.JToken member = OnlineSession.Current.MyMember;
                RosterEntry mine = onlineRace?.Info?.Roster.FirstOrDefault(r => r.Index == onlineRace.Info.YourIndex);
                Art.CarAppearance shown = onlineRace?.MyView?.Appearance;
                Core.Customization.LiveryDocument doc = Core.Customization.LiveryJson.Parse(onlineLivery).Document;
                Note($"online race livery: roster {mine?.Livery?.Length ?? 0} bytes, convoy cosmetic revision {(long?)member?["cosmeticRevision"]}, car front {shown?.Front}, rear aero {shown?.RearAero}, plate '{shown?.PlateText}', {shown?.Decals.Count} decals");
                if (string.IsNullOrEmpty(mine?.Livery) || shown == null || doc == null || shown.Front != doc.Body.Front || shown.RearAero != doc.Body.RearAero
                    || shown.PlateText != doc.Plate.Text || shown.Decals.Count != doc.Decals.Count)
                    failures.Add("online race: the car does not show the applied livery");
            }
            yield return Until(() => onlineRace == null && Router.Current == Convoy, 400f, "race finished and back at the convoy");
            yield return Until(() => (LastOnlineResult ?? "").Contains("Credits"), 25f, "settled receipt");
            yield return Until(() => State()?["postEvent"]?.Type == Newtonsoft.Json.Linq.JTokenType.Object, 15f, "post-event decision");
            yield return new WaitForSeconds(1.5f);
            Shot("08-post-event");
            Click("Continue");
            yield return new WaitForSeconds(1.2f);
            Click("Advance");
            yield return Until(() => State()?["postEvent"]?.Type != Newtonsoft.Json.Linq.JTokenType.Object, 15f, "advanced");
            yield return new WaitForSeconds(1.5f);
            Shot("09-after-advance");
            string summary = failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures);
            Note($"{summary}; last result: {(LastOnlineResult ?? "").Replace("\n", " | ")}");
            yield return new WaitForSeconds(1f);
            Application.Quit(failures.Count == 0 ? 0 : 1);
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

        /// <summary>Tabletop diversions draw their own room; the course backdrop camera steps aside meanwhile.</summary>
        public void SetBackdropVisible(bool visible)
        {
            if (backdropCamera != null) backdropCamera.SetActive(visible);
        }

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

        /// <summary>
        /// Drives a Local event (campaign stage, Freeplay or tutorial) for the open Local profile, then applies the result
        /// with the Core progression rules, saves atomically, and shows Results (which return to <paramref name="returnTo"/>).
        /// </summary>
        public void StartLocalEvent(LocalEventPlan plan, UIScreen returnTo)
        {
            StartCoroutine(RunLocalEvent(plan, returnTo));
        }

        IEnumerator RunLocalEvent(LocalEventPlan plan, UIScreen returnTo)
        {
            string courseRevision = "";
            List<RaceEntrantResult> results = null;
            // An owned car races its frozen APPLIED build (Addendum 02 §10): bought parts change the physics; a loaner is stock.
            LocalSession local = LocalSession.Current;
            Core.Builds.AppliedVehicleBuild frozen = null;
            string buildProblem = null;
            Core.Builds.ResolvedCarSpec spec = plan.Car.Loaner ? null : local?.RaceSpec(plan.Car.InstanceId, out frozen, out buildProblem);
            if (buildProblem != null) Debug.LogWarning("[NightSignal.Local] " + buildProblem);
            string livery = plan.Car.Loaner ? "" : local?.RaceLivery(plan.Car.InstanceId) ?? "";
            Debug.Log($"[NightSignal.Local] {plan.EventId}: {plan.Car.ModelId} races build {(spec != null ? spec.BuildHash.Substring(0, 12) : "stock")} (PI {frozen?.Pi}), " +
                      $"livery {(livery.Length > 0 ? livery.Length + " bytes" : "stock")}");
            yield return RunOfflineRace(plan.CourseId, plan.Car.ModelId, plan.Rules, plan.OpposingAi, false,
                (r, rev) => { results = r; courseRevision = rev; }, spec, () =>
                {
                    // Driving began with this build: record Last Race Build (Test Yard and toys never do).
                    if (frozen == null || local?.Profile == null) return;
                    Core.Profiles.LocalProgressionResult rec = Core.Profiles.LocalGarage.RecordLocalRaceBuild(local.Profile, local.Catalogue,
                        NightSignal.Content.ContentLibrary.Load().Parts, plan.Car.InstanceId, frozen, plan.EventId, DateTime.UtcNow);
                    if (rec.Changed && !local.Commit(rec, out string note)) Debug.LogWarning("[NightSignal.Local] Last Race Build not saved: " + note);
                }, livery);
            LocalSession session = LocalSession.Current;
            Core.Profiles.LocalProgressionResult applied = null;
            string saveNote = "";
            if (session?.Profile != null)
            {
                Core.Profiles.LocalEventFacts facts = LocalEvents.Facts(session, plan, results, courseRevision);
                if (facts != null)
                {
                    applied = Core.Profiles.LocalProgression.ApplyEvent(session.Profile, session.Catalogue, session.Music, facts);
                    if (applied.Changed && !session.Commit(applied, out saveNote))
                        saveNote = "Not saved: " + saveNote;
                    else if (!applied.Changed && applied.Status != Core.Profiles.LocalOperationStatus.Aborted)
                        saveNote = applied.Reason;
                    Debug.Log($"[NightSignal.Local] {plan.Kind} {plan.Stage?.Id ?? plan.CourseId}: {applied.Status} {applied.Reason} " +
                              $"wallet {applied.BalanceBefore} -> {applied.BalanceAfter}; {applied.Changes.Count} change(s) {saveNote}");
                }
            }
            Results.Set(plan.CourseId, plan.Rules, results, applied, saveNote, returnTo);
            Canvas.gameObject.SetActive(true);
            yield return LoadBackdrop();
            Router.Show(Results, true);
        }

        /// <summary>Starts a Local-domain race on a course scene: the UI steps aside; results come back to <see cref="Results"/>.</summary>
        public void StartOfflineRace(string courseId, string carId, RaceEventRules rules, List<string> opposingAi)
        {
            StartCoroutine(RunOfflineRace(courseId, carId, rules, opposingAi, true, null));
        }

        /// <summary>The Test Yard session while one is open (tours drive it).</summary>
        public TestYardSession ActiveYard { get; private set; }
        /// <summary>Runs kept from the last yard visit (A then B), shown back in the Garage.</summary>
        public List<TestYardRun> LastYardRuns { get; } = new List<TestYardRun>();

        /// <summary>
        /// Opens the Garage Test Yard (Addendum 02 §10) on the T00 service campus with A (baseline) and B (candidate); returns to
        /// <paramref name="returnTo"/> when the player leaves. Nothing is recorded, rewarded or bought.
        /// </summary>
        public void StartTestYard(string carId, TestYardBuild a, TestYardBuild b, bool driveB, UIScreen returnTo) =>
            StartCoroutine(RunTestYard(carId, a, b, driveB, returnTo));

        IEnumerator RunTestYard(string carId, TestYardBuild a, TestYardBuild b, bool driveB, UIScreen returnTo)
        {
            Canvas.gameObject.SetActive(false);
            if (backdropCamera != null) backdropCamera.SetActive(false);
            AsyncOperation load = SceneManager.LoadSceneAsync("T00", LoadSceneMode.Single);
            while (!load.isDone) yield return null;
            yield return null;
            var go = new GameObject("TestYard");
            ActiveYard = go.AddComponent<TestYardSession>();
            ActiveYard.CarId = carId;
            ActiveYard.A = a;
            ActiveYard.B = b;
            ActiveYard.StartWithB = driveB;
            while (ActiveYard != null && !ActiveYard.ExitRequested) yield return null;
            LastYardRuns.Clear();
            if (ActiveYard != null)
            {
                LastYardRuns.AddRange(ActiveYard.RunsA);
                LastYardRuns.AddRange(ActiveYard.RunsB);
            }
            Destroy(go);
            ActiveYard = null;
            Canvas.gameObject.SetActive(true);
            yield return LoadBackdrop();
            // The Garage stayed the current screen while the menus were hidden: refresh it in place (keeps the stack,
            // and re-entering its workshop session is idempotent), otherwise push it.
            if (Router.Current == returnTo) returnTo.OnShow();
            else Router.Show(returnTo, true);
        }

        IEnumerator RunOfflineRace(string courseId, string carId, RaceEventRules rules, List<string> opposingAi, bool showResults,
            Action<List<RaceEntrantResult>, string> onResults, Core.Builds.ResolvedCarSpec playerSpec = null, Action onRacing = null, string playerLivery = null)
        {
            Canvas.gameObject.SetActive(false);
            if (backdropCamera != null) backdropCamera.SetActive(false);
            AsyncOperation load = SceneManager.LoadSceneAsync(courseId, LoadSceneMode.Single);
            while (!load.isDone) yield return null;
            yield return null;
            // Stage-default conditions: the course's authored surface, as the online server does (records key on it).
            if (rules.Kind != "freeplay") rules.Surface = CourseRuntime.Active?.Route?.Surface ?? "dry";
            var go = new GameObject("OfflineRace");
            activeRace = go.AddComponent<OfflineRaceSession>();
            activeRace.CarId = carId;
            activeRace.PlayerSpec = playerSpec;
            activeRace.PlayerLivery = playerLivery;
            activeRace.PlayerName = string.IsNullOrEmpty(DisplayName) ? "You" : DisplayName;
            activeRace.Rules = rules;
            activeRace.OpposingAi = opposingAi;
            bool began = false;
            while (activeRace != null && activeRace.Phase != MatchPhase.Results)
            {
                if (!began && activeRace.Phase == MatchPhase.Racing)
                {
                    began = true;
                    onRacing?.Invoke();
                }
                yield return null;
            }
            yield return new WaitForSeconds(2.5f); // let the finish banner read before the results page
            List<RaceEntrantResult> results = activeRace != null ? activeRace.Results : null;
            string revision = CourseRuntime.Active != null ? CourseRuntime.Active.SourceHash : "";
            if (activeRace != null) Destroy(activeRace.gameObject);
            activeRace = null;
            onResults?.Invoke(results, revision);
            if (!showResults) yield break;
            Results.Set(courseId, rules, results, null, "", null);
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
