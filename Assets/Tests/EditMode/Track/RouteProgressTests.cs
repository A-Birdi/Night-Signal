using System.Linq;
using NightSignal.Race;
using NightSignal.Track;
using NightSignal.Track.Generation;
using NightSignal.Vehicle;
using NUnit.Framework;
using UnityEngine;

namespace NightSignal.Tests.Track
{
    /// <summary>
    /// Addendum 03 §6–7 (R01–R05 and recovery anchors) on the REAL generated routes: finite directional gates accepted once
    /// for a legal forward crossing (however fast), nothing for reversing, oscillating, dropping into a gate, falling onto a
    /// later lower road (C25's stacked descent) or passing under an overpass gate (C03's orchard bridge); circuits finish
    /// only on the final lap; recovery anchors never move a car forward. Synthetic motion over the course data — the
    /// driving/physics checks are in PlayMode.
    /// </summary>
    public sealed class RouteProgressTests
    {
        static TrackData Build(string course)
        {
            string json = System.IO.File.ReadAllText(RouteIO.RoutePath(course));
            return CourseGenerator.BuildTrackData(RouteIO.Parse(json), RouteIO.SourceHash(json));
        }

        /// <summary>A car on the route at <paramref name="d"/>, centre 0.5 m above the road, moving forward.</summary>
        static VehicleState At(TrackData t, float d, float lateral = 0f, float speed = 30f)
        {
            TrackSample s = t.SampleAt(d);
            VehicleState v = VehicleState.AtRest(s.Position + s.Right * lateral + s.Up * 0.5f, Quaternion.LookRotation(s.Tangent, s.Up));
            v.Velocity = s.Tangent * speed;
            return v;
        }

        sealed class Run
        {
            public TrackData Track;
            public RaceProgressTracker Tracker;
            public EntrantProgress Progress;
            public VehicleState State;
            public long Micros;

            public Run(TrackData t)
            {
                Track = t;
                Tracker = new RaceProgressTracker(t);
                Progress = new EntrantProgress(t);
                State = At(t, t.Grid[0].Distance);
                Tracker.Start(Progress, State.Position);
            }

            public void MoveTo(VehicleState next)
            {
                VehicleState prev = State;
                State = next;
                Micros += 16_667;
                Tracker.Step(Progress, prev, next, default, Micros, 1f / 60f);
            }

            /// <summary>Drives along the route (circuits keep counting past the seam) in steps of <paramref name="step"/> m.</summary>
            public void Drive(float from, float to, float step, float lateral = 0f)
            {
                for (float d = from; d <= to && !Progress.Finished; d += step) MoveTo(At(Track, d, lateral, step * 60f));
            }
        }

        [Test]
        public void R01_FastLegalCrossings_AcceptEveryGateOnce([Values(1.5f, 12f)] float metresPerTick)
        {
            TrackData t = Build("C01");
            var run = new Run(t);
            run.Drive(run.Progress.Location.Distance, t.LengthMetres, metresPerTick);
            Assert.That(run.Progress.Finished, Is.True, "a legal run finishes");
            Assert.That(run.Progress.CheckpointsPassed, Is.EqualTo(run.Tracker.TotalCheckpoints), "every gate accepted exactly once");
            Assert.That(run.Progress.CorridorCut, Is.False);
        }

        [Test]
        public void R02_ReverseOscillationAndVerticalDrop_NeverAdvance()
        {
            TrackData t = Build("C01");
            var run = new Run(t);
            float gate = t.CheckpointMetres[3];
            run.Drive(run.Progress.Location.Distance, gate - 3f, 1f);
            int before = run.Progress.CheckpointsPassed;
            for (int i = 0; i < 10; i++)
            {
                run.MoveTo(At(t, gate + 2f));
                run.MoveTo(At(t, gate - 2f, 0f, -20f));
            }
            Assert.That(run.Progress.CheckpointsPassed, Is.EqualTo(before + 1), "oscillating over one gate counts it once");

            // Backing over the NEXT gate from beyond it, then dropping vertically into its plane: no credit.
            float next = t.CheckpointMetres[4];
            run.Drive(gate + 2f, next - 20f, 1f);
            int held = run.Progress.CheckpointsPassed;
            TrackSample g = t.SampleAt(next);
            VehicleState above = At(t, next);
            above.Position += g.Up * 12f;
            run.MoveTo(above);
            for (int i = 0; i < 20; i++)
            {
                VehicleState fall = run.State;
                fall.Position -= g.Up * 0.6f;
                run.MoveTo(fall);
            }
            Assert.That(run.Progress.CheckpointsPassed, Is.EqualTo(held), "a vertical landing into a gate is not a crossing");
        }

        [Test]
        public void R03_FallOntoALaterLowerRoad_GivesNoProgressAndRecoversBehind()
        {
            TrackData t = Build("C25");
            // The stacked pair: an earlier upper stretch directly above a later, lower one.
            int upper = -1, lower = -1;
            float bestPlan = float.MaxValue;
            TrackSample[] s = t.Samples;
            for (int i = 6000; i < 7000; i += 2)
                for (int j = 7500; j < 8500; j += 2)
                {
                    Vector3 d = s[i].Position - s[j].Position;
                    float plan = new Vector2(d.x, d.z).magnitude;
                    if (d.y > 6f && plan < bestPlan) { bestPlan = plan; upper = i; lower = j; }
                }
            Assert.That(upper, Is.GreaterThan(0), "C25 has a stacked stretch");
            TestContext.WriteLine($"C25 stacked pair: upper {upper} m over lower {lower} m, {bestPlan:F1} m apart in plan, {s[upper].Position.y - s[lower].Position.y:F1} m above");

            var run = new Run(t);
            run.Drive(run.Progress.Location.Distance, upper, 4f);
            int gates = run.Progress.CheckpointsPassed;
            float raceDistance = run.Progress.RaceDistance, lastSafe = run.Progress.LastSafeDistance;

            // Fall off the upper road onto the lower one, then drive on down the lower road.
            Vector3 from = run.State.Position, to = s[lower].Position + s[lower].Up * 0.5f;
            for (int k = 1; k <= 90; k++)
            {
                VehicleState fall = run.State;
                fall.Position = Vector3.Lerp(from, to, k / 90f);
                fall.Velocity = (to - from) / 1.5f;
                run.MoveTo(fall);
            }
            run.Drive(lower, lower + 400f, 1f);
            Assert.That(run.Progress.CheckpointsPassed, Is.EqualTo(gates), "no gate from the lower road");
            Assert.That(run.Progress.Finished, Is.False);
            Assert.That(run.Progress.RaceDistance, Is.LessThanOrEqualTo(raceDistance + 1f), "no ranking gain from falling down the mountain");
            Assert.That(run.Progress.OffRouteSeconds, Is.GreaterThan(RaceSimulation.AutoRescueSeconds), "the car is off its route");

            var p = new VehicleParams();
            VehicleState back = run.Tracker.ResetPose(run.Progress, p, run.Micros, "off-route");
            RecoveryEvent ev = run.Progress.Recoveries.Last();
            Assert.That(ev.ToDistance, Is.LessThanOrEqualTo(lastSafe + 0.01f), "recovered at or behind the last accepted gate");
            Assert.That(Mathf.Abs(back.Position.y - t.SampleAt(ev.ToDistance).Position.y), Is.LessThan(2f), "on the upper road, not the lower one");
            Assert.That(run.Progress.Resets, Is.EqualTo(1));
            Assert.That(run.Progress.PenaltyMicros, Is.EqualTo(3_000_000L), "one +3 s penalty");
        }

        [Test]
        public void R04_UnderAnOverpassGate_OnTheWrongLayer_IsNotACrossing()
        {
            TrackData t = Build("C03");
            TrackSample[] s = t.Samples;
            int upper = -1, lower = -1;
            float bestPlan = float.MaxValue;
            for (int i = 0; i < s.Length; i += 2)
                for (int j = i + 300; j < s.Length; j += 2)
                {
                    Vector3 d = s[i].Position - s[j].Position;
                    float plan = new Vector2(d.x, d.z).magnitude;
                    if (Mathf.Abs(d.y) > 8f && plan < bestPlan) { bestPlan = plan; upper = d.y > 0 ? i : j; lower = d.y > 0 ? j : i; }
                }
            Assert.That(bestPlan, Is.LessThan(12f), "C03 crosses itself on the orchard bridge");
            var tracker = new RaceProgressTracker(t);
            // A car tracked on the lower road passes under the upper road exactly where a gate would stand.
            var e = new EntrantProgress(t);
            e.Locator.Reset(lower);
            TrackSample g = s[upper];
            Vector3 under = s[lower].Position + s[lower].Up * 0.5f;
            Vector3 from = under - g.Tangent * 2f, to = under + g.Tangent * 2f;
            TrackLocation loc = e.Locator.Locate(to, s[lower].Tangent);
            Assert.That(tracker.GateCrossed(upper, from, to, loc, out float _), Is.False, "no credit for the upper gate from the road beneath it");
            // The same movement on the bridge deck, tracked there, is a legal crossing.
            var deck = new EntrantProgress(t);
            deck.Locator.Reset(upper);
            Vector3 on = g.Position + g.Up * 0.5f;
            TrackLocation onDeck = deck.Locator.Locate(on + g.Tangent * 2f, g.Tangent);
            Assert.That(tracker.GateCrossed(upper, on - g.Tangent * 2f, on + g.Tangent * 2f, onDeck, out float _), Is.True);
        }

        [Test]
        public void R05_Circuit_FinishesOnlyOnTheFinalLap()
        {
            TrackData t = Build("C03");
            Assert.That(t.ClosedLoop && t.Laps >= 2, Is.True);
            var run = new Run(t);
            float start = run.Progress.Location.Distance;
            // Lap one, then a backward excursion over the start/finish line and forward again: still lap two, not finished.
            run.Drive(start, start + t.LengthMetres + 40f, 2f);
            int lap = run.Progress.Lap;
            for (float back = 0f; back < 60f; back += 2f) run.MoveTo(At(t, run.Progress.Location.Distance - 2f, 0f, -20f));
            run.Drive(run.Progress.Location.Distance, run.Progress.Location.Distance + 80f, 2f);
            Assert.That(run.Progress.Lap, Is.EqualTo(lap), "backing over the line and driving on adds no lap");
            Assert.That(run.Progress.Finished, Is.False);
            // Drive on round every remaining lap: finished exactly at the final line.
            for (int k = 0; k < 4 && !run.Progress.Finished; k++)
                run.Drive(run.Progress.Location.Distance, run.Progress.Location.Distance + t.LengthMetres, 2f);
            Assert.That(run.Progress.Finished, Is.True);
            Assert.That(run.Progress.CheckpointsPassed, Is.EqualTo(run.Tracker.TotalCheckpoints));
        }

        [Test]
        public void RecoveryAnchors_NeverMoveACarForward()
        {
            TrackData t = Build("C12");
            var run = new Run(t);
            run.Drive(run.Progress.Location.Distance, t.CheckpointMetres[6] + 40f, 2f);
            float lastSafe = run.Progress.LastSafeDistance;
            var p = new VehicleParams();
            // Free anchor: the gate itself.
            run.Tracker.ResetPose(run.Progress, p, run.Micros, "manual");
            Assert.That(run.Progress.Recoveries.Last().ToDistance, Is.EqualTo(lastSafe).Within(0.01f));
            // Every lane of the gate occupied: stepped back, never forward, never before 60 m behind.
            TrackSample g = t.SampleAt(lastSafe);
            run.Tracker.ResetPose(run.Progress, p, run.Micros, "manual", pos => Vector3.Distance(pos, g.Position) < 12f);
            float to = run.Progress.Recoveries.Last().ToDistance;
            Assert.That(to, Is.LessThan(lastSafe));
            Assert.That(to, Is.GreaterThanOrEqualTo(lastSafe - 60f));
            Assert.That(run.Progress.Resets, Is.EqualTo(2));
            Assert.That(run.Progress.PenaltyMicros, Is.EqualTo(6_000_000L), "one penalty per completed recovery");
        }
    }
}
