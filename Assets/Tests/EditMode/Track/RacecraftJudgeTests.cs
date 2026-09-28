using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Rules;
using NightSignal.Net;
using NightSignal.Race;
using NightSignal.Track;
using NightSignal.Track.Generation;
using NUnit.Framework;
using UnityEngine;

namespace NightSignal.Tests.Track
{
    /// <summary>
    /// Racecraft judging (Appendix E CH31 Clean Pass, CH32 Patient Mirror) on scripted progress: a pass counts only
    /// against a live, moving car, with no touch 2 s either side and the place kept 3 s; a recovery never makes a pass;
    /// a follow is the same moving car ahead inside the 1–2 s interval, broken by any touch.
    /// </summary>
    public sealed class RacecraftJudgeTests
    {
        static TrackData track;
        static TrackData Track
        {
            get
            {
                if (track != null) return track;
                string json = System.IO.File.ReadAllText(RouteIO.RoutePath("C05"));
                return track = CourseGenerator.BuildTrackData(RouteIO.Parse(json), RouteIO.SourceHash(json));
            }
        }

        const float Dt = 1f / 60f;

        /// <summary>A scripted field: each car's distance and speed per tick; touches and resets by tick.</summary>
        sealed class Field
        {
            public readonly List<RaceEntrant> Cars = new List<RaceEntrant>();
            public readonly RacecraftJudge Judge;
            public int Tick;

            public Field(params float[] startDistances)
            {
                foreach (float d in startDistances)
                {
                    var e = new RaceEntrant { Status = EntrantStatus.Racing, Progress = new EntrantProgress(Track) };
                    e.Progress.RaceDistance = d;
                    Cars.Add(e);
                }
                Judge = RacecraftJudge.ForEvent(new RaceEventRules(), Cars);
            }

            public long Micros => (long)Tick * 1_000_000L / 60;

            /// <summary>One tick: every car moves at its speed (m/s); <paramref name="touch"/> pairs touch; <paramref name="reset"/> cars jump back 50 m.</summary>
            public void Step(float[] speeds, (int, int)? touch = null, int reset = -1)
            {
                Tick++;
                for (int i = 0; i < Cars.Count; i++)
                {
                    RaceEntrant e = Cars[i];
                    if (e.Status != EntrantStatus.Racing) continue;
                    e.Progress.RaceDistance += speeds[i] * Dt;
                    if (i == reset) e.Progress.RaceDistance -= 50f;
                    e.State.Velocity = Vector3.forward * speeds[i];
                    Judge.Step(e, i == reset, Micros);
                }
                if (touch.HasValue) Judge.Touch(Cars[touch.Value.Item1], Cars[touch.Value.Item2], Micros);
                Judge.Judge(Micros);
            }

            public void Run(float seconds, float[] speeds, System.Func<int, (int, int)?> touchAt = null, System.Func<int, int> resetAt = null)
            {
                int n = Mathf.RoundToInt(seconds * 60f);
                for (int k = 0; k < n; k++) Step(speeds, touchAt?.Invoke(Tick + 1), resetAt?.Invoke(Tick + 1) ?? -1);
            }
        }

        static IEnumerable<string> Challenges(string course, RaceEntrant e)
        {
            e.Progress.Finished = true;
            return ChallengePredicates.Evaluate(course, e.Progress, racecraft: e.Racecraft);
        }

        [Test]
        public void History_IntervalIsTheTimeSinceTheCarAheadWasHere()
        {
            var h = new DistanceHistory();
            for (int k = 0; k <= 240; k++) h.Add(k / 60.0, 100f + 20f * k / 60f); // 20 m/s for 4 s
            Assert.That(h.IntervalBehind(150f, 4.0, out float gap), Is.True);
            Assert.That(gap, Is.EqualTo(1.5f).Within(0.02f)); // it was at 150 m at 2.5 s
            Assert.That(h.IntervalBehind(190f, 4.0, out _), Is.False, "not ahead of this car");
            Assert.That(h.IntervalBehind(90f, 4.0, out _), Is.False, "passed there before the history begins");
        }

        [Test]
        public void ForEvent_OnlyRacesWithLiveOpponents()
        {
            var cars = new List<RaceEntrant>();
            Assert.That(RacecraftJudge.ForEvent(new RaceEventRules(), cars), Is.Not.Null);
            Assert.That(RacecraftJudge.ForEvent(new RaceEventRules { Contact = ContactPolicy.NonContact }, cars), Is.Null, "Time Attack");
            Assert.That(RacecraftJudge.ForEvent(new RaceEventRules { DriftRanking = true }, cars), Is.Null, "Drift Attack");
        }

        [Test]
        public void CleanPass_KeptThreeSeconds_CompletesCH31()
        {
            var f = new Field(100f, 120f);
            f.Run(2.2f, new[] { 25f, 15f }); // closes 20 m in 2 s: passes at ~2.0 s
            Assert.That(f.Cars[0].Racecraft.Pending.Count, Is.EqualTo(1), "the pass is seen, not yet confirmed");
            Assert.That(f.Cars[0].Racecraft.CleanPasses, Is.Empty);
            f.Run(3.1f, new[] { 25f, 15f });
            Assert.That(f.Cars[0].Racecraft.CleanPasses.Select(p => p.Passed), Is.EqualTo(new[] { 1 }));
            Assert.That(Challenges("C01", f.Cars[0]), Has.Member("CH31"));
            Assert.That(f.Cars[1].Racecraft.CleanPasses, Is.Empty, "being passed is not a pass");
        }

        [Test]
        public void Pass_TouchInsideEitherWindow_IsNotClean()
        {
            // Touching 1 s before the pass (at ~1.0 s).
            var before = new Field(100f, 120f);
            before.Run(5.5f, new[] { 25f, 15f }, t => t == 60 ? (0, 1) : ((int, int)?)null);
            Assert.That(before.Cars[0].Racecraft.CleanPasses, Is.Empty);
            // Touching 1.5 s after the pass (at ~3.5 s).
            var after = new Field(100f, 120f);
            after.Run(5.5f, new[] { 25f, 15f }, t => t == 210 ? (0, 1) : ((int, int)?)null);
            Assert.That(after.Cars[0].Racecraft.CleanPasses, Is.Empty);
            // Touching 2.5 s after: the clean window has closed; the place is still kept.
            var late = new Field(100f, 120f);
            late.Run(5.5f, new[] { 25f, 15f }, t => t == 270 ? (0, 1) : ((int, int)?)null);
            Assert.That(late.Cars[0].Racecraft.CleanPasses.Count, Is.EqualTo(1));
        }

        [Test]
        public void Pass_PlaceLostWithinThreeSeconds_DoesNotCount()
        {
            var f = new Field(100f, 120f);
            f.Run(2.2f, new[] { 25f, 15f });
            f.Run(3f, new[] { 10f, 30f }); // the other car comes straight back past
            Assert.That(f.Cars[0].Racecraft.CleanPasses, Is.Empty);
        }

        [Test]
        public void Recovery_NeverMakesAPass()
        {
            // The car ahead recovers 50 m back behind this one: no pass for this car.
            var f = new Field(100f, 110f);
            f.Run(5f, new[] { 15f, 15f }, resetAt: t => t == 60 ? 1 : -1);
            Assert.That(f.Cars[0].Racecraft.CleanPasses, Is.Empty);
            Assert.That(f.Cars[0].Racecraft.Pending, Is.Empty);
        }

        [Test]
        public void Pass_OfAStoppedCar_DoesNotCount()
        {
            var f = new Field(100f, 120f);
            f.Run(5f, new[] { 20f, 0f });
            Assert.That(f.Cars[0].Racecraft.CleanPasses, Is.Empty);
        }

        [Test]
        public void Follow_EightSecondsInsideTheWindow_CompletesCH32OnC05Only()
        {
            var f = new Field(100f, 130f); // 30 m at 20 m/s = 1.5 s (measurable once the car ahead's history reaches back 1.5 s)
            f.Run(10.5f, new[] { 20f, 20f });
            RacecraftRun r = f.Cars[0].Racecraft;
            Assert.That(r.Ahead, Is.EqualTo(1));
            Assert.That(r.Interval, Is.EqualTo(1.5f).Within(0.05f));
            Assert.That(r.FollowLongest, Is.GreaterThanOrEqualTo(8f));
            Assert.That(Challenges("C05", f.Cars[0]), Has.Member("CH32"));
            Assert.That(Challenges("C01", f.Cars[0]), Has.No.Member("CH32"));
        }

        [Test]
        public void Follow_BrokenByATouch_OrOutsideTheWindow()
        {
            var touched = new Field(100f, 130f);
            touched.Run(10.5f, new[] { 20f, 20f }, t => t == 360 ? (0, 1) : ((int, int)?)null); // a touch at 6 s: 4.5 s, then 4.5 s
            Assert.That(touched.Cars[0].Racecraft.FollowLongest, Is.LessThan(8f));
            var far = new Field(100f, 150f); // 2.5 s
            far.Run(10.5f, new[] { 20f, 20f });
            Assert.That(far.Cars[0].Racecraft.FollowLongest, Is.EqualTo(0f));
            var close = new Field(100f, 115f); // 0.75 s
            close.Run(10.5f, new[] { 20f, 20f });
            Assert.That(close.Cars[0].Racecraft.FollowLongest, Is.EqualTo(0f));
        }

        [Test]
        public void NoRacecraftFacts_NoRacecraftChallenges()
        {
            var e = new RaceEntrant { Status = EntrantStatus.Finished, Progress = new EntrantProgress(Track) };
            Assert.That(Challenges("C05", e).Where(c => c == "CH31" || c == "CH32"), Is.Empty);
        }
    }
}
