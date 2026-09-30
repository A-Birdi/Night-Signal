using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Rules;
using NightSignal.Race;
using NightSignal.Track;
using NightSignal.Track.Generation;
using NUnit.Framework;
using UnityEngine;

namespace NightSignal.Tests.Track
{
    /// <summary>
    /// The merge judge (Appendix E CH37 Keep the Door Open) on the real T00 route: its two marked MERGE lanes (76–240 m, ±2.8 m)
    /// driven side by side — the player in the lane it entered, the "merge" pace car beside it in the other, no touch — keep
    /// the merge; a car out of its lane, the pace car left behind, or a touch do not.
    /// </summary>
    public sealed class MergeTests
    {
        static TrackData track;
        static TrackData Track
        {
            get
            {
                if (track != null) return track;
                string json = System.IO.File.ReadAllText(RouteIO.RoutePath("T00"));
                return track = CourseGenerator.BuildTrackData(RouteIO.Parse(json), RouteIO.SourceHash(json));
            }
        }

        const float Dt = 1f / 60f;

        /// <summary>Car 0 the player, car 1 the pace car; both start at <paramref name="start"/> route metres and drive at 20 m/s.</summary>
        static RacecraftRun Drive(float paceBehind, System.Func<float, float> playerLateral, System.Func<float, float> paceLateral, float touchAt = -1f)
        {
            var cars = new List<RaceEntrant>();
            for (int i = 0; i < 2; i++)
            {
                var e = new RaceEntrant { Status = EntrantStatus.Racing, Progress = new EntrantProgress(Track),
                    Roster = new RosterEntry { Index = i, Role = i == 1 ? "merge" : "driver", Human = i == 0 } };
                cars.Add(e);
            }
            RacecraftJudge judge = RacecraftJudge.ForEvent(new RaceEventRules(), cars, Track);
            // T00 is a loop: race distance counts from the start line (StartMetres), so route = race + StartMetres.
            float race0 = 60f - Track.StartMetres;
            cars[0].Progress.RaceDistance = race0;
            cars[1].Progress.RaceDistance = race0 - paceBehind;
            int tick = 0;
            while (cars[0].Progress.RaceDistance + Track.StartMetres < 260f)
            {
                tick++;
                long micros = (long)tick * 1_000_000L / 60;
                foreach (RaceEntrant e in cars)
                {
                    e.Progress.RaceDistance += 20f * Dt;
                    float route = e.Progress.RaceDistance + Track.StartMetres;
                    e.Progress.Location = new TrackLocation { Distance = route, Lateral = e == cars[0] ? playerLateral(route) : paceLateral(route), InCorridor = true };
                    e.State.Velocity = Vector3.forward * 20f;
                    judge.Step(e, false, micros);
                }
                if (touchAt >= 0f && cars[0].Progress.RaceDistance + Track.StartMetres >= touchAt) { judge.Touch(cars[0], cars[1], micros); touchAt = -1f; }
                judge.Judge(micros);
            }
            return cars[0].Racecraft;
        }

        [Test]
        public void SideBySide_EachInItsLane_KeepsTheMerge()
        {
            RacecraftRun r = Drive(3f, _ => -2.6f, _ => 2.9f);
            Assert.That(r.Merges, Is.EqualTo(new[] { ("CH37", true) }), string.Join("\n", r.PassLog));
        }

        [Test]
        public void OutOfLane_LeftBehind_OrATouch_BreakTheMerge()
        {
            Assert.That(Drive(3f, d => d > 150f ? -0.5f : -2.6f, _ => 2.9f).Merges.Single().Kept, Is.False, "the player drifts out of its lane");
            Assert.That(Drive(3f, _ => -2.6f, d => d > 150f ? 0.5f : 2.9f).Merges.Single().Kept, Is.False, "the pace car drifts out of its lane");
            Assert.That(Drive(40f, _ => -2.6f, _ => 2.9f).Merges.Single().Kept, Is.False, "the pace car 40 m behind is not beside it");
            Assert.That(Drive(3f, _ => -2.6f, _ => 2.9f, touchAt: 120f).Merges.Single().Kept, Is.False, "a touch");
        }
    }
}
