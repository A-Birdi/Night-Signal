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
    /// Marked-zone passes (Appendix E CH34 Let the Corner End) on the real C02 route: a pass inside the hairpin's marked exit
    /// zone (2584–2681 m) counts once the place is still held at the retain gate (2766 m); a pass on the approach, a place lost
    /// before the gate, a recovery or another course does not.
    /// </summary>
    public sealed class ZonePassTests
    {
        static TrackData track;
        static TrackData Track
        {
            get
            {
                if (track != null) return track;
                string json = System.IO.File.ReadAllText(RouteIO.RoutePath("C02"));
                return track = CourseGenerator.BuildTrackData(RouteIO.Parse(json), RouteIO.SourceHash(json));
            }
        }

        const float Dt = 1f / 60f;

        sealed class Field
        {
            public readonly List<RaceEntrant> Cars = new List<RaceEntrant>();
            public readonly RacecraftJudge Judge;
            int tick;

            public Field(params float[] startDistances)
            {
                foreach (float d in startDistances)
                {
                    var e = new RaceEntrant { Status = EntrantStatus.Racing, Progress = new EntrantProgress(Track) };
                    e.Progress.RaceDistance = d;
                    Cars.Add(e);
                }
                Judge = RacecraftJudge.ForEvent(new RaceEventRules(), Cars, Track);
            }

            long Micros => (long)tick * 1_000_000L / 60;

            /// <summary>Runs until car 0 reaches <paramref name="until"/> metres; <paramref name="speeds"/> gives each car's speed (m/s) at car 0's distance.</summary>
            public void RunTo(float until, System.Func<float, float[]> speeds, int resetCar = -1, float resetAt = -1f, (int, int)? touch = null, float touchAt = -1f)
            {
                while (Cars[0].Progress.RaceDistance < until)
                {
                    tick++;
                    float at = Cars[0].Progress.RaceDistance;
                    float[] v = speeds(at);
                    for (int i = 0; i < Cars.Count; i++)
                    {
                        RaceEntrant e = Cars[i];
                        bool reset = i == resetCar && resetAt >= 0f && at >= resetAt;
                        e.Progress.RaceDistance += v[i] * Dt - (reset ? 60f : 0f);
                        e.State.Velocity = Vector3.forward * v[i];
                        Judge.Step(e, reset, Micros);
                    }
                    if (resetCar >= 0 && at >= resetAt) resetAt = -1f;
                    if (touch.HasValue && touchAt >= 0f && at >= touchAt) { Judge.Touch(Cars[touch.Value.Item1], Cars[touch.Value.Item2], Micros); touchAt = -1f; }
                    Judge.Judge(Micros);
                }
            }
        }

        static IEnumerable<string> Challenges(string course, RaceEntrant e)
        {
            e.Progress.Finished = true;
            return ChallengePredicates.Evaluate(course, e.Progress, racecraft: e.Racecraft);
        }

        [Test]
        public void APassInTheExitZone_HeldToTheGate_EarnsCH34()
        {
            // Car 0 at 30 m/s catches car 1 (20 m/s) from 40 m back: 10 m/s closing → the pass 4 s later, at ~2660 m (in the zone).
            var f = new Field(2540f, 2580f);
            f.RunTo(2800f, _ => new[] { 30f, 20f });
            RacecraftRun r = f.Cars[0].Racecraft;
            Assert.That(r.ZonePasses.Select(p => p.Challenge), Is.EqualTo(new[] { "CH34" }), string.Join("\n", r.PassLog));
            Assert.That(r.ZonePasses[0].TouchFree, Is.True);
            Assert.That(Challenges("C02", f.Cars[0]), Does.Contain("CH34"));
            Assert.That(Challenges("C05", f.Cars[0]), Does.Not.Contain("CH34"), "C02 only");
        }

        [Test]
        public void APassOnTheApproach_DoesNotCount()
        {
            var f = new Field(2480f, 2500f); // closes 20 m at 10 m/s → the pass at ~2540 m, before the zone
            f.RunTo(2800f, _ => new[] { 30f, 20f });
            Assert.That(f.Cars[0].Racecraft.ZonePasses, Is.Empty);
            Assert.That(Challenges("C02", f.Cars[0]), Does.Not.Contain("CH34"));
        }

        [Test]
        public void ThePlaceLostBeforeTheGate_DoesNotCount()
        {
            var f = new Field(2540f, 2580f);
            // Once car 0 is past 2670 m (after the pass) car 1 speeds up and takes the place back before 2766 m.
            f.RunTo(2800f, d => d < 2670f ? new[] { 30f, 20f } : new[] { 20f, 45f });
            Assert.That(f.Cars[0].Racecraft.ZonePasses, Is.Empty, string.Join("\n", f.Cars[0].Racecraft.PassLog));
        }

        [Test]
        public void ARecovery_BreaksThePass_AndATouchIsRecorded()
        {
            var reset = new Field(2540f, 2580f);
            reset.RunTo(2800f, _ => new[] { 30f, 20f }, resetCar: 1, resetAt: 2700f); // the passed car recovers before the gate
            Assert.That(reset.Cars[0].Racecraft.ZonePasses, Is.Empty);

            var touched = new Field(2540f, 2580f);
            touched.RunTo(2800f, _ => new[] { 30f, 20f }, touch: (0, 1), touchAt: 2610f);
            Assert.That(touched.Cars[0].Racecraft.ZonePasses.Single().TouchFree, Is.False, "CH34 does not ask for a clean pass, but the fact is kept");
            Assert.That(Challenges("C02", touched.Cars[0]), Does.Contain("CH34"));
        }
    }
}
