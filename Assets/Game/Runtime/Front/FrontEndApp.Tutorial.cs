using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Ghosts;
using NightSignal.Core.Profiles;
using NightSignal.Core.Rules;
using NightSignal.Core.Tutorial;
using NightSignal.Race;
using NightSignal.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        float pendingAutopilotDriftSkill;
        bool lessonRunning;

        /// <summary>Automation only (tours): the player's own lesson attempts are driven by the validator autopilot.</summary>
        public static bool LessonAutopilot;
        /// <summary>The last lesson run's verdict and feedback (tours read them).</summary>
        public LessonStatus LastLessonStatus { get; private set; }
        public string LastLessonFeedback { get; private set; } = "";

        public void StartLesson(TutorialLesson lesson, bool demonstration)
        {
            if (lesson == null || !lesson.IsDrive || lessonRunning) return;
            StartCoroutine(RunLesson(lesson, demonstration));
        }

        /// <summary>Keeps a passed lesson in the open Local profile (training progress only; no profile = practice).</summary>
        public void MarkLessonPassed(TutorialLesson lesson)
        {
            LocalSession s = LocalSession.Current;
            if (s?.Profile == null || lesson == null) return;
            LocalProgressionResult r = LocalProgression.MarkLessonPassed(s.Profile, ContentLibrary.Load()?.Tutorial, lesson.Id);
            if (r.Status == LocalOperationStatus.Applied) s.Commit(r, out _);
        }

        /// <summary>The instructor's demonstration lap of T00 (Resources/LessonGhosts/T00.json, recorded by RivalReferenceGhostTests).</summary>
        static GhostRecording InstructorGhost()
        {
            TextAsset t = Resources.Load<TextAsset>("LessonGhosts/T00");
            return t == null ? null : GhostRecording.Parse(t.text, out _);
        }

        /// <summary>
        /// One drive lesson on the T00 loop: alone, non-contact, in the profile's car (else the V01 loaner). The judge reads
        /// every tick (distance, speed, brake, slip, walls, resets, camera changes, drift) and the banner shows its live
        /// feedback; once judged the run ends and the Driving School shows the verdict, ready to retry. A demonstration is
        /// the autopilot driving the same lesson, visibly marked as a training aid; it never counts as a pass.
        /// </summary>
        IEnumerator RunLesson(TutorialLesson lesson, bool demonstration)
        {
            lessonRunning = true;
            TutorialLessons lessons = ContentLibrary.Load()?.Tutorial;
            string course = lessons?.Course ?? "T00";
            bool drifting = demonstration && (lesson.Demonstration?.DriftSkill ?? 0f) > 0f;
            var rules = new RaceEventRules
            {
                Kind = "freeplay", Contact = ContactPolicy.NonContact, StageNumber = 1, CarCapPi = PerformanceIndex.Max, DriftRanking = drifting,
            };
            pendingAutopilotDriftSkill = drifting ? lesson.Demonstration.DriftSkill : 0f;
            string car = LocalSession.Current?.Profile?.Cars.FirstOrDefault()?.ModelId ?? "V01";
            pendingGhostTemplate = new GhostHeader { Format = "lesson", CarModelId = car, Driver = DisplayName ?? "", Provenance = "local-simulation" };
            pendingGhosts.Clear();
            if (lesson.Ghost == "instructor" && InstructorGhost() is GhostRecording instructor) pendingGhosts.Add(instructor);
            var judge = new LessonJudge(lesson);
            bool raceDone = false;
            StartCoroutine(RunOfflineRace(course, car, rules, new List<string>(), false, (r, rev) => raceDone = true));
            float until = Time.realtimeSinceStartup + 60f;
            while ((activeRace == null || activeRace.Sim == null) && !raceDone && Time.realtimeSinceStartup < until) yield return null;
            OfflineRaceSession race = activeRace;
            GameObject overlay = null;
            if (race?.Sim != null)
            {
                race.Autopilot = demonstration || LessonAutopilot;
                string head = demonstration ? "DEMONSTRATION · the autopilot drives this lesson (training aid)"
                    : LessonAutopilot ? "LESSON · driven by automation (tour)" : "LESSON";
                TextMeshProUGUI banner = LessonBanner(out overlay);
                race.TickObserver = (sim, tick) =>
                {
                    RaceEntrant me = race.Player;
                    if (me == null) return;
                    Vector3 v = me.State.Velocity, f = me.State.Rotation * Vector3.forward;
                    v.y = 0f;
                    f.y = 0f;
                    float slip = v.magnitude > 4f ? Vector3.Angle(f, v) : 0f;
                    judge.Tick(new LessonTick
                    {
                        // Lessons are authored in route metres; race distance counts from the start line.
                        Seconds = sim.RaceMicros(tick) / 1e6f, Metres = me.Progress.RaceDistance + sim.Track.StartMetres, SpeedKmh = me.State.SpeedKmh,
                        Brake = race.LastPlayerInput.Brake, SlipDeg = slip, WallIncidents = me.Progress.WallIncidents, Resets = me.Progress.Resets,
                        CameraChanges = race.CameraChanges, DriftRaw = me.Drift.BankedRaw + me.Drift.UnbankedRaw, Finished = me.Progress.Finished,
                        Spun = slip > DriftScorer.SpinAngleDegrees,
                    });
                };
                while (!raceDone && judge.Status == LessonStatus.InProgress)
                {
                    banner.text = $"<b>{head}</b> · {lesson.Title}\n<size=75%>{lesson.Goal}</size>\n<color=#F2A541>{judge.Feedback}</color>";
                    yield return null;
                }
                banner.text = $"<b>{head}</b> · {lesson.Title}\n" + (judge.Status == LessonStatus.Passed ? "<color=#3EC6D8>PASSED</color>  " :
                    judge.Status == LessonStatus.NotYet ? "<color=#F2A541>NOT YET</color>  " : "") + judge.Feedback;
                if (!raceDone)
                {
                    yield return new WaitForSecondsRealtime(2.5f);
                    race.EndNow();
                }
            }
            while (!raceDone) yield return null;
            if (overlay != null) Destroy(overlay);
            LastLessonStatus = judge.Status;
            LastLessonFeedback = judge.Status == LessonStatus.InProgress ? "The run ended before the lesson was judged." : judge.Feedback;
            Debug.Log($"[NightSignal.Tutorial] {(demonstration ? "demonstration" : "attempt")} {lesson.Id}: {LastLessonStatus} — {LastLessonFeedback}");
            if (judge.Status == LessonStatus.Passed && !demonstration) MarkLessonPassed(lesson);
            string shown = (demonstration ? "<color=#9A968D>Demonstration:</color> " : "") +
                           (judge.Status == LessonStatus.Passed ? (demonstration ? "the autopilot passed. " : "<color=#3EC6D8>Passed.</color> ") :
                            judge.Status == LessonStatus.NotYet ? "<color=#F2A541>Not yet.</color> " : "") + LastLessonFeedback + (demonstration ? "" : "  Retry any time.");
            Lessons.ShowResult(lesson, shown);
            Canvas.gameObject.SetActive(true);
            yield return LoadBackdrop();
            Router.Show(Lessons, false);
            lessonRunning = false;
        }

        /// <summary>The lesson banner over the race (its own overlay canvas; destroyed with the lesson).</summary>
        static TextMeshProUGUI LessonBanner(out GameObject root)
        {
            root = new GameObject("LessonBanner", typeof(Canvas), typeof(CanvasScaler));
            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 50;
            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            // Below the race HUD's top row (timer, drift counter), above the car.
            Image panel = UIFactory.Panel("Panel", root.transform, new Vector2(0.28f, 0.68f), new Vector2(0.72f, 0.84f), Vector2.zero, Vector2.zero, new Color(0.04f, 0.045f, 0.055f, 0.82f));
            TextMeshProUGUI text = UIFactory.Label("Lesson", panel.transform, "", SignalTheme.Body, SignalTheme.Label, TextAlignmentOptions.Center);
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = new Vector2(24, 8);
            text.rectTransform.offsetMax = new Vector2(-24, -8);
            text.textWrappingMode = TextWrappingModes.Normal;
            text.richText = true;
            return text;
        }

        /// <summary>
        /// Driving School evidence (<c>-nsTutorialTour</c>), isolated profile folder: the help index is searched; drive
        /// lessons are attempted with the validator autopilot driving (automation standing in for a player — the lesson
        /// judge and the overlay are the real ones), with the camera cycled through the session's camera button path and
        /// a reset held as the button does; one demonstration is watched; a card is answered wrong, then right. Passes
        /// land in the profile. Automation, not a person.
        /// </summary>
        IEnumerator TutorialTour()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "tutorial"));
            System.IO.Directory.CreateDirectory(dir);
            string profiles = System.IO.Path.Combine(dir, "profiles");
            if (System.IO.Directory.Exists(profiles)) System.IO.Directory.Delete(profiles, true);
            LocalSession.UseFolder(profiles);
            var failures = new List<string>();
            void Note(string n) => Debug.Log("[NightSignal.TutorialTour] " + n);
            void Fail(string f) { failures.Add(f); Note("FAIL " + f); }
            bool Click(string name)
            {
                Button b = GameObject.Find(name)?.GetComponent<Button>();
                if (b == null || !b.interactable) { Fail($"button not available: {name} (screen {Router.Current?.ScreenName ?? "none"})"); return false; }
                b.onClick.Invoke();
                return true;
            }
            IEnumerator Until(Func<bool> condition, float seconds)
            {
                float t = Time.realtimeSinceStartup + seconds;
                while (!condition() && Time.realtimeSinceStartup < t) yield return null;
            }
            IEnumerator Snap(string name)
            {
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, name + ".png"));
                yield return new WaitForEndOfFrame();
                yield return null;
            }

            yield return Until(() => GameObject.Find("OfflinePlay") != null, 20f);
            yield return new WaitForSeconds(1f);
            Click("OfflinePlay");
            yield return new WaitForSeconds(1.2f);
            Click("NewProfile");
            yield return new WaitForSeconds(1.2f);
            GameObject.Find("ProfileName")?.GetComponent<TMP_InputField>()?.SetTextWithoutNotify("School Driver");
            Click("Create");
            yield return Until(() => LocalSession.Current?.Profile != null && Router.Current == OfflineHub, 10f);
            Click("DrivingSchool");
            yield return Until(() => Router.Current == Lessons, 10f);
            yield return new WaitForSeconds(0.8f);
            Note($"lessons listed: {Lessons.Shown.Count}");
            Lessons.Search("brake");
            yield return new WaitForSeconds(0.5f);
            Note("help index 'brake': " + string.Join(", ", Lessons.Shown.Select(l => l.Id)));
            if (!Lessons.Shown.Any(l => l.Id == "braking")) Fail("the help index does not find the braking lesson");
            yield return Snap("01-help-index");
            Lessons.Search("");
            yield return new WaitForSeconds(0.3f);

            LessonAutopilot = true;
            IEnumerator Attempt(string id, bool demonstration, Action<OfflineRaceSession> during = null)
            {
                if (!Lessons.SelectLesson(id)) { Fail("lesson not listed: " + id); yield break; }
                yield return new WaitForSeconds(0.4f);
                Click(demonstration ? "WatchDemonstration" : "TryLesson");
                yield return Until(() => activeRace != null && activeRace.Phase == MatchPhase.Racing, 60f);
                OfflineRaceSession race = activeRace;
                if (race != null && id != "controls-camera" && id != "resets") race.SimulationSpeed = 4;
                if (during != null && race != null) during(race);
                yield return new WaitForSeconds(4f);
                if (race != null) yield return Snap((demonstration ? "demo-" : "try-") + id);
                yield return Until(() => !lessonRunning && Router.Current == Lessons, 400f);
                yield return new WaitForSeconds(0.8f);
                Note($"{(demonstration ? "demonstration" : "attempt")} {id}: {LastLessonStatus} — {LastLessonFeedback}");
            }
            // Controls and camera: the camera cycled twice through the session's button path.
            yield return Attempt("controls-camera", false, race => StartCoroutine(CycleLater(race)));
            if (LastLessonStatus != LessonStatus.Passed) Fail("controls-camera was not passed");
            yield return Snap("02-after-controls");
            yield return Attempt("braking", false);
            yield return Attempt("turn-in-exit", false);
            yield return Attempt("exits-gearing", false);
            // Resets: hold reset once, as the button does, a few seconds in.
            yield return Attempt("resets", false, race => StartCoroutine(ResetLater(race)));
            if (LastLessonStatus != LessonStatus.Passed) Fail("resets was not passed");
            yield return Attempt("ghost-deltas", false);
            if (LastLessonStatus != LessonStatus.Passed) Fail("ghost-deltas was not passed");
            // A demonstration: never a pass in the profile.
            yield return Attempt("grip-drift", true);
            if (LastLessonStatus != LessonStatus.Passed) Fail("the drift demonstration did not drift the zone");
            yield return Attempt("countersteer", true);
            if (LastLessonStatus != LessonStatus.Passed) Fail("the countersteering demonstration did not catch a slide");
            LessonAutopilot = false;

            // A card: wrong first, then right.
            Lessons.SelectLesson("rewards-rank");
            yield return new WaitForSeconds(0.4f);
            Click("Choice0");
            yield return new WaitForSeconds(0.4f);
            Note("card, wrong answer: " + Lessons.Result);
            Click("Choice1");
            yield return new WaitForSeconds(0.4f);
            Note("card, right answer: " + Lessons.Result);
            yield return Snap("03-card");
            List<string> passed = LocalSession.Current.Profile.Tutorial.LessonsPassed;
            Note("passed in the profile: " + string.Join(", ", passed));
            if (!passed.Contains("rewards-rank")) Fail("the card was not recorded");
            if (passed.Contains("grip-drift")) Fail("a demonstration counted as a pass");
            yield return Snap("04-progress");
            Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
            Application.Quit(failures.Count == 0 ? 0 : 1);

            IEnumerator CycleLater(OfflineRaceSession race)
            {
                yield return new WaitForSeconds(3f);
                race?.CycleCamera();
                yield return new WaitForSeconds(2f);
                race?.CycleCamera();
            }
            IEnumerator ResetLater(OfflineRaceSession race)
            {
                yield return new WaitForSeconds(6f);
                race?.HoldReset(1.0f);
            }
        }
    }
}
