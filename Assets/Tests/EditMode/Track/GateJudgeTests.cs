using System.Linq;
using NightSignal.Content;
using NightSignal.Race;
using NightSignal.Track;
using NightSignal.Track.Generation;
using NightSignal.Vehicle;
using NUnit.Framework;
using UnityEngine;

namespace NightSignal.Tests.Track
{
    /// <summary>
    /// Challenge gates on the real routes (Appendix E CH03, CH06, CH09): the body crossing a marked apex / precision gate's
    /// band counts, a line outside it does not; a guardrail touch spoils CH06; a lane zone crossed with a barrier inside the
    /// 0.5 m margin spoils CH09. Cars are driven along the route as in RouteProgressTests.
    /// </summary>
    public sealed class GateJudgeTests
    {
        static TrackData Build(string course)
        {
            string json = System.IO.File.ReadAllText(RouteIO.RoutePath(course));
            return CourseGenerator.BuildTrackData(RouteIO.Parse(json), RouteIO.SourceHash(json));
        }

        static VehicleState At(TrackData t, float d, float lateral, float speed)
        {
            TrackSample s = t.SampleAt(d);
            VehicleState v = VehicleState.AtRest(s.Position + s.Right * lateral + s.Up * 0.5f, Quaternion.LookRotation(s.Tangent, s.Up));
            v.Velocity = s.Tangent * speed;
            return v;
        }

        /// <summary>A world whose only barrier is within reach of one point (none when <see cref="Point"/> is null).</summary>
        sealed class BarrierBand : IVehicleWorld
        {
            public Vector3? Point;
            public bool CastWheel(Vector3 origin, Vector3 direction, float maxDistance, out GroundHit hit) { hit = default; return false; }
            public int ResolveBody(Vector3 center, Quaternion rotation, Vector3 halfExtents, BarrierContact[] buffer) => 0;
            public bool SweepBody(Vector3 from, Vector3 to, Quaternion rotation, Vector3 halfExtents, out float fraction, out Vector3 normal)
            {
                fraction = 1f;
                normal = Vector3.zero;
                return false;
            }
            public bool NearBarrier(Vector3 center, Quaternion rotation, Vector3 halfExtents) =>
                Point.HasValue && Vector3.Distance(center, Point.Value) < 6f;
        }

        /// <summary>Drives a whole route with <paramref name="lateral"/>(distance) as the line; returns the entrant.</summary>
        static RaceEntrant Drive(TrackData t, GateJudge judge, System.Func<float, float> lateral, IVehicleWorld world = null)
        {
            VehicleParams p = ContentLibrary.Load().Params("V01", AssistSettings.Default);
            var e = new RaceEntrant
            {
                Params = p,
                Sim = new VehicleSimulation(p, PlaneVehicleWorld.Flat),
                Progress = new EntrantProgress(t),
                State = At(t, t.Grid[0].Distance, 0f, 30f),
            };
            var tracker = new RaceProgressTracker(t);
            tracker.Start(e.Progress, e.State.Position);
            long micros = 0;
            // Every lap of a circuit, and on past the line until the race is finished.
            for (float d = e.Progress.Location.Distance; d <= t.LengthMetres * t.Laps + 300f && !e.Progress.Finished; d += 3f)
            {
                VehicleState prev = e.State;
                e.State = At(t, d, lateral(d % t.LengthMetres), 180f);
                micros += 16_667;
                tracker.Step(e.Progress, prev, e.State, default, micros, 1f / 60f);
                judge.Step(e, false, world);
            }
            return e;
        }

        /// <summary>The line through each gate of <paramref name="challenge"/> (its marked offset within 20 m of it), else the centreline.</summary>
        static System.Func<float, float> Through(GateJudge judge, string challenge, string skip = null) => d =>
        {
            RouteGateDef g = judge.TouchGates.FirstOrDefault(x => x.Challenge == challenge && x.Id != skip && Mathf.Abs(x.StartMetres - d) < 20f);
            return g != null ? g.LineOffset : 0f;
        };

        [Test]
        public void ApexGates_TouchedAllOrNot_CH03()
        {
            TrackData t = Build("C03");
            GateJudge judge = GateJudge.ForTrack(t);
            Assert.That(judge, Is.Not.Null);
            Assert.That(judge.TouchGates.Count(g => g.Challenge == "CH03"), Is.EqualTo(3), "C03's three designated apexes");
            RaceEntrant through = Drive(t, judge, Through(judge, "CH03"));
            Assert.That(through.Progress.Finished, Is.True);
            Assert.That(through.GateRun.AllTouched("CH03"), Is.True);
            Assert.That(Net.ChallengePredicates.Evaluate("C03", through.Progress, gates: through.GateRun), Does.Contain("CH03"));
            through.Progress.WallIncidents = 1;
            Assert.That(Net.ChallengePredicates.Evaluate("C03", through.Progress, gates: through.GateRun), Does.Not.Contain("CH03"), "no wall incident allowed");

            RaceEntrant missed = Drive(t, judge, Through(judge, "CH03", skip: "C03-APEX-2"));
            Assert.That(missed.GateRun.AllTouched("CH03"), Is.False, "the centreline misses an apex 2.7 m off it");
            Assert.That(Net.ChallengePredicates.Evaluate("C03", missed.Progress, gates: missed.GateRun), Does.Not.Contain("CH03"));
        }

        [Test]
        public void PrecisionGates_AndNoGuardrailTouch_CH06()
        {
            TrackData t = Build("C05");
            GateJudge judge = GateJudge.ForTrack(t);
            Assert.That(judge.TouchGates.Count(g => g.Challenge == "CH06"), Is.EqualTo(6));
            RaceEntrant e = Drive(t, judge, Through(judge, "CH06"));
            Assert.That(Net.ChallengePredicates.Evaluate("C05", e.Progress, gates: e.GateRun), Does.Contain("CH06"));
            e.GateRun.BarrierTouchSteps = 1; // one guardrail touch, below a meaningful wall incident, still spoils it
            Assert.That(Net.ChallengePredicates.Evaluate("C05", e.Progress, gates: e.GateRun), Does.Not.Contain("CH06"));
        }

        [Test]
        public void LaneZones_MarginKeptOrNot_CH09()
        {
            TrackData t = Build("C13");
            GateJudge judge = GateJudge.ForTrack(t);
            Assert.That(judge.LaneZones.Count(g => g.Challenge == "CH09"), Is.EqualTo(2));
            var clear = new BarrierBand();
            RaceEntrant kept = Drive(t, judge, _ => 0f, clear);
            Assert.That(kept.GateRun.LanesKept("CH09"), Is.True);
            Assert.That(Net.ChallengePredicates.Evaluate("C13", kept.Progress, gates: kept.GateRun), Does.Contain("CH09"));

            // A barrier within the margin at one point inside the second viaduct zone.
            RouteGateDef second = judge.LaneZones.Where(g => g.Challenge == "CH09").OrderBy(g => g.StartMetres).Last();
            var close = new BarrierBand { Point = t.SampleAt(second.StartMetres + 60f).Position + t.SampleAt(second.StartMetres + 60f).Up * 0.5f };
            RaceEntrant broken = Drive(t, judge, _ => 0f, close);
            Assert.That(broken.GateRun.LanesKept("CH09"), Is.False, "the margin broken inside a zone");
            Assert.That(Net.ChallengePredicates.Evaluate("C13", broken.Progress, gates: broken.GateRun), Does.Not.Contain("CH09"));
            // The same barrier outside any zone does not count.
            var outside = new BarrierBand { Point = t.SampleAt(second.EndMetres + 80f).Position + t.SampleAt(second.EndMetres + 80f).Up * 0.5f };
            Assert.That(Drive(t, judge, _ => 0f, outside).GateRun.LanesKept("CH09"), Is.True);
        }
    }
}
