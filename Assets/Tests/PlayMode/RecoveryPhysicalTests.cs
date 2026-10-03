using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Rules;
using NightSignal.Race;
using NightSignal.Track;
using NightSignal.Track.Generation;
using NightSignal.Vehicle;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace NightSignal.Tests
{
    /// <summary>
    /// Addendum 03 R03/R06/R10 with real physics on real course scenes, through the shared RaceSimulation (the code the
    /// dedicated server runs): a car released beyond the edge of C25's upper road physically falls toward the later,
    /// lower road and is recovered by the marshal within the 2–4 s target, on the upper road at/behind its last gate,
    /// with one +3 s and no progress gained; a car dropped on its roof on C01 is offered the reset, counted down and
    /// recovered upright; a human car merely stopped is offered the reset but never taken away. Scripted inputs in the
    /// human seat — not a human playtest.
    /// </summary>
    [ResetAutomationStatics]
    public sealed class RecoveryPhysicalTests
    {
        OfflineRaceSession session;

        [TearDown]
        public void TearDown()
        {
            if (session != null) Object.Destroy(session.gameObject);
        }

        IEnumerator Start(string course, int speed)
        {
            yield return LoadCourse(course);
            var go = new GameObject("OfflineRace");
            session = go.AddComponent<OfflineRaceSession>();
            session.CarId = "V01";
            session.Autopilot = true;
            session.Headless = true;
            session.SimulationSpeed = speed;
            session.Rules = new RaceEventRules { Kind = "freeplay", Contact = ContactPolicy.NonContact };
            session.OpposingAi = new List<string>();
            yield return null;
            Assert.That(session.Ready, Is.True);
            float until = Time.realtimeSinceStartup + 60f;
            while (session.Phase != MatchPhase.Racing && Time.realtimeSinceStartup < until) yield return null;
            Assert.That(session.Phase, Is.EqualTo(MatchPhase.Racing));
        }

        /// <summary>Runs the simulation (scripted neutral input) for a number of simulated seconds.</summary>
        IEnumerator Hold(float simSeconds, System.Func<bool> stop = null)
        {
            int target = session.CurrentTick + Mathf.RoundToInt(simSeconds * VehicleSimulation.TickRate);
            float until = Time.realtimeSinceStartup + 120f;
            while (session.CurrentTick < target && Time.realtimeSinceStartup < until && (stop == null || !stop())) yield return null;
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator R03_PhysicalFallFromTheUpperRoad_RecoveredPromptlyBehind_NoProgress()
        {
            yield return Start("C25", 20);
            TrackData t = session.Sim.Track;
            TrackSample[] s = t.Samples;
            // A point beside C25's upper road, clear of its edge, with the later lower road beneath it.
            int upper = -1, lower = -1;
            float best = float.MaxValue;
            for (int i = 6000; i < 7000 && i < s.Length; i += 2)
                for (int j = 7500; j < 8500 && j < s.Length; j += 2)
                {
                    Vector3 d = s[i].Position - s[j].Position;
                    float plan = new Vector2(d.x, d.z).magnitude;
                    float edge = s[i].Width * 0.5f + Mathf.Max(s[i].ShoulderLeft, s[i].ShoulderRight) + 3f;
                    if (d.y > 8f && plan > edge && plan < edge + 12f && plan < best) { best = plan; upper = i; lower = j; }
                }
            Assert.That(upper, Is.GreaterThan(0), "C25: a place beside the upper road above the lower road");

            // Drive there legally with the autopilot.
            float until = Time.realtimeSinceStartup + 240f;
            while (session.Player.Progress.Location.Distance < s[upper].Distance - 3f && Time.realtimeSinceStartup < until) yield return null;
            Assert.That(session.Player.Progress.Location.Distance, Is.GreaterThanOrEqualTo(s[upper].Distance - 3f), "reached the upper road");
            session.SimulationSpeed = 1;
            session.Sim.HumanInput = (e, tick) => DriverInput.Neutral;
            RaceEntrant car = session.Player;
            int gates = car.Progress.CheckpointsPassed;
            float raceDistance = car.Progress.RaceDistance, lastSafe = car.Progress.LastSafeDistance;
            int recoveries = car.Progress.Recoveries.Count;

            // Released at rest beyond the edge, level with the upper road: gravity does the rest.
            Vector3 drop = new Vector3(s[lower].Position.x, s[upper].Position.y + 1.2f, s[lower].Position.z);
            VehicleState st = VehicleState.AtRest(drop, Quaternion.LookRotation(s[upper].Tangent, Vector3.up));
            car.State = st;
            // Baseline once the release pose has been tracked (its projection onto the upper road is where the car is placed).
            yield return Hold(1f / VehicleSimulation.TickRate);
            raceDistance = car.Progress.RaceDistance;
            int releaseTick = session.CurrentTick;
            float minY = drop.y;
            RecoveryKind seenKind = RecoveryKind.None;
            float seenCountdown = -1f;
            yield return Hold(6f, () =>
            {
                minY = Mathf.Min(minY, car.State.Position.y);
                RecoveryStatus r = session.Sim.Recovery(car);
                if (r.Kind == RecoveryKind.OffRoute && r.SecondsToAuto >= 0f) { seenKind = r.Kind; seenCountdown = Mathf.Max(seenCountdown, r.SecondsToAuto); }
                return car.Progress.Recoveries.Count > recoveries;
            });
            float seconds = (session.CurrentTick - releaseTick) / (float)VehicleSimulation.TickRate;
            TestContext.WriteLine($"C25: released at {s[upper].Distance:F0} m beside the upper road ({best:F1} m in plan from the lower road at {s[lower].Distance:F0} m), fell {drop.y - minY:F1} m, recovered after {seconds:F2} s");
            Assert.That(drop.y - minY, Is.GreaterThan(2f), "the car physically fell");
            Assert.That(car.Progress.Recoveries.Count, Is.EqualTo(recoveries + 1), "one automatic recovery");
            RecoveryEvent ev = car.Progress.Recoveries.Last();
            Assert.That(ev.Reason, Is.EqualTo("off-route"));
            Assert.That(seconds, Is.InRange(1.5f, 4.5f), "within the 2–4 s target of losing the route (plus the fall)");
            Assert.That(seenKind, Is.EqualTo(RecoveryKind.OffRoute), "the HUD offer counted down before the recovery");
            Assert.That(ev.ToDistance, Is.LessThanOrEqualTo(lastSafe + 0.01f), "at or behind the last accepted gate");
            Assert.That(Mathf.Abs(car.State.Position.y - t.SampleAt(ev.ToDistance).Position.y), Is.LessThan(2.5f), "back on the upper road, not the lower one");
            Assert.That(car.Progress.CheckpointsPassed, Is.EqualTo(gates), "no gate from the fall");
            Assert.That(car.Progress.RaceDistance, Is.LessThanOrEqualTo(raceDistance + 0.5f), "no ranking gain from the fall");
            yield return Hold(0.5f);
            Assert.That(car.Progress.RaceDistance, Is.LessThanOrEqualTo(lastSafe + 1f), "ranked from the recovery point afterwards");
            Assert.That(ev.PenaltyMs, Is.EqualTo(3000));
            Assert.That((car.State.Rotation * Vector3.up).y, Is.GreaterThan(0.9f), "upright");
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator R10_OverturnedCar_IsOfferedTheReset_CountedDown_AndRecoveredUpright()
        {
            yield return Start("C01", 1);
            yield return Hold(5f);
            session.Sim.HumanInput = (e, tick) => DriverInput.Neutral;
            RaceEntrant car = session.Player;
            int recoveries = car.Progress.Recoveries.Count;
            VehicleState st = car.State;
            st.Position += st.Rotation * Vector3.up * 1.5f;
            st.Rotation = st.Rotation * Quaternion.Euler(0f, 0f, 180f); // on its roof
            st.Velocity = st.AngularVelocity = Vector3.zero;
            car.State = st;
            int start = session.CurrentTick;
            RecoveryKind prompt = RecoveryKind.None;
            float firstPromptAt = -1f, lastCountdown = 99f;
            yield return Hold(6f, () =>
            {
                RecoveryStatus r = session.Sim.Recovery(car);
                if (r.Kind == RecoveryKind.Overturned)
                {
                    prompt = r.Kind;
                    if (firstPromptAt < 0f) firstPromptAt = (session.CurrentTick - start) / (float)VehicleSimulation.TickRate;
                    Assert.That(r.SecondsToAuto, Is.LessThanOrEqualTo(lastCountdown + 1e-3f), "the countdown only counts down");
                    lastCountdown = r.SecondsToAuto;
                }
                return car.Progress.Recoveries.Count > recoveries;
            });
            float seconds = (session.CurrentTick - start) / (float)VehicleSimulation.TickRate;
            TestContext.WriteLine($"C01 roof: prompt after {firstPromptAt:F2} s, recovered after {seconds:F2} s");
            Assert.That(prompt, Is.EqualTo(RecoveryKind.Overturned), "the overturned offer was shown");
            Assert.That(firstPromptAt, Is.InRange(0.5f, 2f), "promptly");
            Assert.That(car.Progress.Recoveries.Count, Is.EqualTo(recoveries + 1), "one recovery");
            Assert.That(car.Progress.Recoveries.Last().Reason, Is.EqualTo("overturned"));
            Assert.That(seconds, Is.InRange(2.5f, 5f));
            Assert.That((car.State.Rotation * Vector3.up).y, Is.GreaterThan(0.9f), "upright again");
            // Holding still upright on the road afterwards does not recover it again.
            int after = car.Progress.Recoveries.Count;
            yield return Hold(3f);
            Assert.That(car.Progress.Recoveries.Count, Is.EqualTo(after), "no repeated recovery");
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator R10_AHumanMerelyStopped_IsOfferedTheReset_NeverRecoveredAutomatically()
        {
            yield return Start("C01", 1);
            yield return Hold(3f);
            session.Sim.HumanInput = (e, tick) => DriverInput.Quantize(0f, 0f, 1f, InputButtons.None);
            RaceEntrant car = session.Player;
            int recoveries = car.Progress.Recoveries.Count;
            yield return Hold(3f); // brake to a stop
            // Parked on the handbrake (holding the brake at rest would select reverse; neutral rolls on C01's grade).
            session.Sim.HumanInput = (e, tick) => DriverInput.Quantize(0f, 0f, 0f, InputButtons.Handbrake);
            yield return Hold(RaceSimulation.StuckPromptSeconds + 4f);
            TestContext.WriteLine($"stopped: speed {car.State.Velocity.magnitude:F2} m/s, stuck {car.StuckSeconds:F1} s");
            RecoveryStatus r = session.Sim.Recovery(car);
            Assert.That(r.Kind, Is.EqualTo(RecoveryKind.Stopped), "a stopped human is offered the reset");
            Assert.That(r.SecondsToAuto, Is.LessThan(0f), "with no automatic countdown");
            Assert.That(car.Progress.Recoveries.Count, Is.EqualTo(recoveries), "and the car is never taken away");
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator R08_SimultaneousRecoveries_GetSeparateNonForwardAnchors()
        {
            yield return LoadCourse("C01");
            var go = new GameObject("OfflineRace");
            session = go.AddComponent<OfflineRaceSession>();
            session.CarId = "V01";
            session.Autopilot = true;
            session.Headless = true;
            session.SimulationSpeed = 1;
            session.Rules = new RaceEventRules { Kind = "freeplay", Contact = ContactPolicy.LightContact };
            session.OpposingAi = new List<string> { "R01", "R02", "R03" };
            yield return null;
            float until = Time.realtimeSinceStartup + 60f;
            while (session.Phase != MatchPhase.Racing && Time.realtimeSinceStartup < until) yield return null;
            yield return Hold(9f);

            // Every car is thrown off the route at the same tick onto one spot beside the road.
            TrackData t = session.Sim.Track;
            RaceEntrant lead = session.Sim.Entrants.OrderByDescending(e => e.Progress.RaceDistance).First();
            TrackSample s = t.SampleAt(lead.Progress.Location.Distance);
            Vector3 beside = s.Position + s.Right * (RoadGeometry.BarrierLateral(s, 1) + 16f);
            if (Physics.Raycast(beside + Vector3.up * 80f, Vector3.down, out RaycastHit ground, 200f, ~0, QueryTriggerInteraction.Ignore)) beside = ground.point;
            var before = new Dictionary<RaceEntrant, (int Recoveries, float LastSafe, int Laps)>();
            foreach (RaceEntrant e in session.Sim.Entrants)
            {
                before[e] = (e.Progress.Recoveries.Count, e.Progress.LastSafeDistance, e.Progress.Lap);
                e.State = VehicleState.AtRest(beside + Vector3.up * (1.2f + 2.5f * session.Sim.Entrants.IndexOf(e)), Quaternion.LookRotation(s.Tangent, Vector3.up));
            }
            session.Sim.HumanInput = (e, tick) => DriverInput.Neutral;
            yield return Hold(5f, () => session.Sim.Entrants.All(e => e.Progress.Recoveries.Count > before[e].Recoveries));

            var placed = new List<(RaceEntrant E, Vector3 At, RecoveryEvent Ev)>();
            foreach (RaceEntrant e in session.Sim.Entrants)
            {
                Assert.That(e.Progress.Recoveries.Count, Is.EqualTo(before[e].Recoveries + 1), $"{e.Roster.EntrantId}: exactly one recovery");
                RecoveryEvent ev = e.Progress.Recoveries.Last();
                Assert.That(ev.ToDistance, Is.LessThanOrEqualTo(before[e].LastSafe + 0.01f), $"{e.Roster.EntrantId}: never forward of its last gate");
                Assert.That(e.Progress.Lap, Is.EqualTo(before[e].Laps), "completed laps kept");
                Assert.That(ev.PenaltyMs, Is.EqualTo(3000));
                placed.Add((e, e.State.Position, ev));
            }
            float closest = float.MaxValue;
            for (int i = 0; i < placed.Count; i++)
                for (int j = i + 1; j < placed.Count; j++)
                    closest = Mathf.Min(closest, Vector3.Distance(placed[i].At, placed[j].At));
            TestContext.WriteLine("C01 simultaneous recoveries: " + string.Join("; ", placed.Select(p => $"{p.E.Roster.EntrantId} {p.Ev.Reason} {p.Ev.FromDistance:F0}->{p.Ev.ToDistance:F0} m")) + $"; closest pair {closest:F1} m");
            Assert.That(closest, Is.GreaterThan(session.Player.Params.LengthM), "no two cars placed on top of each other");
            // After the ghost window every car is solid again and none still overlaps another.
            yield return Hold(2.5f);
            foreach (RaceEntrant e in session.Sim.Entrants)
                Assert.That(e.GhostUntilTick < 0 || e.GhostUntilTick <= session.CurrentTick, $"{e.Roster.EntrantId}: protection ended within its window");
        }

        static IEnumerator LoadCourse(string course)
        {
#if UNITY_EDITOR
            AsyncOperation load = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
                $"Assets/Content/Courses/{course}/{course}.unity", new LoadSceneParameters(LoadSceneMode.Single));
            yield return load;
#else
            Assert.Ignore("Editor-only scene loading");
            yield break;
#endif
            yield return null;
            Assert.That(CourseRuntime.Active, Is.Not.Null, "course generated");
        }
    }
}
