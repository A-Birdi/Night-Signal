using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NightSignal.Content;
using NightSignal.Core.Content;
using NightSignal.Core.Ghosts;
using NightSignal.Core.Rules;
using NightSignal.Race;
using NightSignal.Track;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace NightSignal.Tests
{
    /// <summary>
    /// Records the authored rival reference ghosts (spec §8 "an authored rival reference"): for every course with a
    /// reference rival (<see cref="RivalReference"/>), that rival drives the course alone under Time Attack rules (dry,
    /// non-contact; the validator autopilot's car is ghosted and ignored), at its full profile for the stage it leads, in
    /// its own car where the class allows. The run is sampled like any ghost (10 Hz, checkpoint times) and written to
    /// Assets/Content/Resources/RivalGhosts/&lt;course&gt;.json with provenance <see cref="RivalReference.Provenance"/>; a summary
    /// goes to Evidence/ghosts/rival-references.txt. Rerun after a physics, scoring or route change (older references
    /// become incompatible and stay off the road). Automation: AI driving, not a human run.
    /// </summary>
    [Explicit("records the authored rival reference ghosts (spec §8)")]
    public sealed class RivalReferenceGhostTests
    {
        const string Folder = "Assets/Content/Resources/RivalGhosts";

        [UnityTest, Timeout(3600000)]
        public IEnumerator RecordRivalReferences()
        {
            ContentLibrary lib = ContentLibrary.Load();
            ContentCatalogue cat = lib.Catalogue;
            Directory.CreateDirectory(Folder);
            var report = new StringBuilder();
            report.AppendLine("# Authored rival reference ghosts (spec §8): each course's reference rival alone under Time Attack rules (dry, non-contact),");
            report.AppendLine("# full profile for the stage it leads, recorded at 10 Hz. Written by RivalReferenceGhostTests (PlayMode, explicit). AI driving.");
            report.AppendLine("course  rival  name                      car   result s   samples  resets  clean  bytes");
            var problems = new List<string>();
            foreach (CourseDef course in cat.Courses.OrderBy(c => c.Id, StringComparer.Ordinal))
            {
                RivalDef rival = RivalReference.For(cat, course.Id);
                string scene = $"Assets/Content/Courses/{course.Id}/{course.Id}.unity";
                if (rival == null || !File.Exists(scene)) continue;
#if UNITY_EDITOR
                yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(scene, new UnityEngine.SceneManagement.LoadSceneParameters(UnityEngine.SceneManagement.LoadSceneMode.Single));
#else
                Assert.Ignore("Editor-only scene loading");
#endif
                yield return null;
                StageDef stage = cat.Stages.Where(s => s.Course == course.Id).OrderBy(s => s.Number).FirstOrDefault();
                GhostRecording ghost = null;
                yield return Record(course, rival, stage, g => ghost = g);
                if (ghost == null || ghost.Header.ResultMicros <= 0)
                {
                    problems.Add($"{course.Id}: {rival.Id} did not finish");
                    report.AppendLine($"{course.Id,-6}  {rival.Id,-5}  {rival.Name,-24}  —     did not finish");
                    continue;
                }
                string json = ghost.ToJson();
                File.WriteAllText(Path.Combine(Folder, course.Id + ".json"), json);
                if (ghost.Problems().Count > 0) problems.Add($"{course.Id}: {string.Join("; ", ghost.Problems())}");
                bool clean = ghost.Header.Resets == 0 && !ghost.Header.CorridorCut; // a reset stays in the time (+3 s): stated, not hidden
                report.AppendLine($"{course.Id,-6}  {rival.Id,-5}  {rival.Name,-24}  {ghost.Header.CarModelId,-4}  {ghost.Header.ResultMicros / 1e6,8:F3}  {ghost.Count,7}  " +
                                  $"{ghost.Header.Resets,6}  {(clean ? "yes" : "NO"),5}  {json.Length,6}");
                Debug.Log($"[NightSignal.RivalGhost] {course.Id} {rival.Id} {ghost.Header.CarModelId} {ghost.Header.ResultMicros / 1e6:F3} s, {ghost.Count} samples, {json.Length} bytes");
            }
            report.AppendLine(problems.Count == 0 ? "# every reference finished and is a well-formed ghost; \"clean\" = no reset and no corridor cut" : "# problems: " + string.Join("; ", problems));
            Directory.CreateDirectory("Evidence/ghosts");
            File.WriteAllText("Evidence/ghosts/rival-references.txt", report.ToString());
            Assert.That(problems, Is.Empty, string.Join("; ", problems));
        }

        static IEnumerator Record(CourseDef course, RivalDef rival, StageDef stage, Action<GhostRecording> done)
        {
            var go = new GameObject("RivalReference");
            var session = go.AddComponent<OfflineRaceSession>();
            session.CarId = "V01";
            session.Autopilot = true;
            session.Headless = true;
            session.SimulationSpeed = 30;
            session.Rules = new RaceEventRules
            {
                Kind = "freeplay", Contact = ContactPolicy.NonContact, CalibrationGhosts = true, StageNumber = stage?.Number ?? 15,
                CarCapPi = stage?.MaxPI ?? PerformanceIndex.Max,
            };
            session.OpposingAi = new List<string> { rival.Id };
            GhostRecorder recorder = null;
            RaceEntrant driver = null;
            session.TickObserver = (sim, tick) =>
            {
                if (driver == null)
                {
                    driver = sim.Entrants.FirstOrDefault(e => !e.Human);
                    if (driver == null) return;
                    recorder = new GhostRecorder(new GhostHeader
                    {
                        CourseId = course.Id, CourseRevision = CourseRuntime.Active?.SourceHash ?? "", Direction = "forward",
                        Format = "time-attack", Surface = "dry", PhysicsVersion = RaceSimulation.PhysicsVersion, ScoringVersion = RaceSimulation.ScoringVersion,
                        GameVersion = Application.version, CarModelId = driver.Roster.CarId, Provenance = RivalReference.Provenance, Driver = rival.Name,
                        RecordedUtc = DateTime.UtcNow,
                    });
                }
                if (driver.Status == EntrantStatus.Racing || driver.Progress.Finished) recorder.Step(driver, sim.RaceMicros(tick), tick);
            };
            yield return null;
            float start = Time.realtimeSinceStartup;
            while (session.Results == null && Time.realtimeSinceStartup - start < 400f) yield return null;
            done(driver != null && recorder != null ? recorder.Finish(driver.Progress) : null);
            UnityEngine.Object.Destroy(go);
            yield return null;
        }
    }
}
