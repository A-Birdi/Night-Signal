using NightSignal.Track;
using NightSignal.Track.Generation;
using NightSignal.Core.Rules;
using NUnit.Framework;
using UnityEngine;

namespace NightSignal.Tests.Track
{
    public sealed class TrackTests
    {
        static TrackData Build(string course)
        {
            string json = System.IO.File.ReadAllText(RouteIO.RoutePath(course));
            return CourseGenerator.BuildTrackData(RouteIO.Parse(json), RouteIO.SourceHash(json));
        }

        [Test]
        public void C01_MatchesItsBrief()
        {
            TrackData t = Build("C01");
            RouteStats s = RouteIO.Measure(t.Samples);
            Assert.That(s.LengthMetres, Is.InRange(3100f * 0.95f, 3100f * 1.05f), "target length 3.1 km");
            Assert.That(s.NetElevationMetres, Is.EqualTo(-80f).Within(1f));
            Assert.That(s.MinWidthMetres, Is.GreaterThanOrEqualTo(6f));
            Assert.That(s.MaxGradePercent, Is.LessThan(12f));
            Assert.That(s.MinRadiusMetres, Is.GreaterThan(15f), "the hairpin is clearly sighted, not a blind switchback");
            Assert.That(t.Sectors.Count, Is.GreaterThanOrEqualTo(3));
        }

        [Test]
        public void Grid_HasTwelveSeparatedSlotsOnTheRoadBehindTheStartLine()
        {
            // Addendum 01 D01: every counted course supports a safe twelve-car start (replaces the six-slot test).
            TrackData t = Build("C01");
            Assert.That(t.Grid.Length, Is.EqualTo(Limits.MaxRaceVehicles));
            for (int i = 0; i < t.Grid.Length; i++)
            {
                Assert.That(t.Grid[i].Distance, Is.LessThan(t.StartMetres));
                Assert.That(t.Grid[i].Distance, Is.GreaterThan(2f), $"slot {i} falls off the start of the route");
                TrackSample s = t.SampleAt(t.Grid[i].Distance);
                float lateral = Mathf.Abs(Vector3.Dot(t.Grid[i].Position - s.Position, s.Right));
                Assert.That(lateral + 1.0f, Is.LessThan(s.Width * 0.5f), $"slot {i} is too close to the road edge");
                for (int j = i + 1; j < t.Grid.Length; j++)
                    Assert.That(Vector3.Distance(t.Grid[i].Position, t.Grid[j].Position), Is.GreaterThan(4f), $"slots {i}/{j} overlap");
            }
        }

        [Test]
        public void Checkpoints_AreOrderedAndEndAtTheFinish()
        {
            TrackData t = Build("C01");
            for (int i = 1; i < t.CheckpointMetres.Length; i++)
                Assert.That(t.CheckpointMetres[i], Is.GreaterThan(t.CheckpointMetres[i - 1]));
            Assert.That(t.CheckpointMetres[t.CheckpointMetres.Length - 1], Is.EqualTo(CourseGenerator.FinishMetres(t)));
        }

        [Test]
        public void CircuitCheckpoints_GoAllTheWayRound_AndEndAtTheLapLine([Values("C03", "C11", "C14", "C18", "FP01", "FP03", "T00")] string course)
        {
            // Regression (V-026): a closed loop had ONE checkpoint — the start line — so GO counted a lap.
            TrackData t = Build(course);
            Assert.That(t.ClosedLoop, Is.True);
            Assert.That(t.CheckpointMetres.Length, Is.GreaterThan(t.LengthMetres / 150f), "gates all the way round the lap");
            float Forward(float d) => Mathf.Repeat(d - t.StartMetres, t.LengthMetres);
            for (int i = 1; i < t.CheckpointMetres.Length - 1; i++)
                Assert.That(Forward(t.CheckpointMetres[i]), Is.GreaterThan(Forward(t.CheckpointMetres[i - 1])));
            Assert.That(t.CheckpointMetres[t.CheckpointMetres.Length - 1], Is.EqualTo(CourseGenerator.FinishMetres(t)));
        }

        [Test]
        public void ACarDrivenTwoLapsRoundACircuit_PassesEveryGateOnce_AndFinishes()
        {
            TrackData t = Build("C03");
            var tracker = new NightSignal.Race.RaceProgressTracker(t);
            var e = new NightSignal.Race.EntrantProgress(t);
            GridSlot grid = t.Grid[0];
            tracker.Start(e, grid.Position);
            var telemetry = new NightSignal.Vehicle.StepTelemetry();
            NightSignal.Vehicle.VehicleState prev = NightSignal.Vehicle.VehicleState.AtRest(grid.Position, grid.Rotation);
            float lastRaceDistance = e.RaceDistance;
            long micros = 0;
            // Advance 2 m per tick from the grid slot, two full laps plus a little, along the centre line.
            for (float d = grid.Distance; d < grid.Distance + 2f * t.LengthMetres + 40f && !e.Finished; d += 2f)
            {
                TrackSample s = t.SampleAt(d);
                var cur = NightSignal.Vehicle.VehicleState.AtRest(s.Position + s.Up * 0.5f, Quaternion.LookRotation(s.Tangent, s.Up));
                cur.Velocity = s.Tangent * 30f;
                micros += 16_667;
                tracker.Step(e, prev, cur, telemetry, micros, 1f / 60f);
                Assert.That(e.RaceDistance, Is.GreaterThanOrEqualTo(lastRaceDistance - 0.5f), $"race distance went backwards at {d:F0} m");
                lastRaceDistance = e.RaceDistance;
                prev = cur;
            }
            Assert.That(e.Finished, Is.True, $"finished (checkpoints {e.CheckpointsPassed}/{tracker.TotalCheckpoints}, lap {e.Lap})");
            Assert.That(e.CheckpointsPassed, Is.EqualTo(tracker.TotalCheckpoints));
            Assert.That(e.CorridorCut, Is.False);
            Assert.That(lastRaceDistance, Is.EqualTo(2f * t.LengthMetres).Within(10f), "two laps from the start line");
        }

        [Test]
        public void PositiveBank_RaisesTheLeftEdge()
        {
            var route = new RouteDefinition { Schema = RouteIO.Schema, Course = "TEST" };
            route.ControlPoints.Add(new RouteControlPoint { P = new[] { 0f, 0f, 0f }, Bank = 10f });
            route.ControlPoints.Add(new RouteControlPoint { P = new[] { 0f, 0f, 50f }, Bank = 10f });
            route.ControlPoints.Add(new RouteControlPoint { P = new[] { 0f, 0f, 100f }, Bank = 10f });
            TrackSample[] s = RouteSampler.Sample(route);
            TrackSample mid = s[s.Length / 2];
            Assert.That(mid.Right.y, Is.LessThan(0f), "right vector points down, so the left edge is higher");
            Assert.That(mid.Up.y, Is.GreaterThan(0.9f));
        }

        [Test]
        public void Locator_ReportsProgressLateralAndWrongWay()
        {
            TrackData t = Build("C01");
            var loc = new TrackLocator(t);
            TrackSample s = t.SampleAt(1500f);
            TrackLocation onRoad = loc.Locate(s.Position + s.Right * 2f, s.Tangent);
            Assert.That(onRoad.Distance, Is.EqualTo(1500f).Within(1.5f));
            Assert.That(onRoad.Lateral, Is.EqualTo(2f).Within(0.2f));
            Assert.That(onRoad.OnPaved && onRoad.InCorridor, Is.True);
            TrackLocation off = loc.Locate(s.Position + s.Right * 40f, -s.Tangent);
            Assert.That(off.InCorridor, Is.False);
            Assert.That(off.HeadingDot, Is.LessThan(-0.9f), "wrong way");
        }
    }
}
