using System.IO;
using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NightSignal.Race;
using NightSignal.Track;
using NightSignal.Track.Generation;
using NUnit.Framework;
using UnityEngine;

namespace NightSignal.Tests.Track
{
    /// <summary>
    /// S29 "Four Signals" judging on C24's authored contract sectors with the certified published targets: a run that meets
    /// everything passes all four; each contract fails on its own condition and says why; Hard has its own, tighter
    /// certified targets; courses without the four sectors have no judge.
    /// </summary>
    public sealed class FourSignalsTests
    {
        TrackData track;
        ContractJudge judge;
        FourSignalsTargets t;

        [SetUp]
        public void SetUp()
        {
            ContentCatalogue cat = ContentFiles.LoadProjectCatalogue();
            string json = File.ReadAllText("Assets/Content/Courses/C24/route.json");
            track = CourseGenerator.BuildTrackData(RouteIO.Parse(json), RouteIO.SourceHash(json));
            judge = ContractJudge.ForEvent(track, cat, "campaign", "S29", CampaignMode.Normal);
            t = judge?.Targets;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(track);

        ContractRun Passing()
        {
            var r = new ContractRun(judge.ApexCount, judge.ExitCount)
            {
                EntryMs = t.EntrySectorMs - 500, ArcRaw = t.ArcDriftRaw + 50, DescentWalls = 0, DescentResets = 0,
                BrakedNearZone = true, ReleasePointSeen = true, BrakeOffAtReleasePoint = true, BrakeExitKmh = (t.BrakeExitMinKmh + t.BrakeExitMaxKmh) / 2f,
            };
            for (int i = 0; i < r.ApexHit.Length; i++) r.ApexHit[i] = true;
            for (int i = 0; i < r.ExitKmh.Length; i++) r.ExitKmh[i] = t.HorizonExitKmh[i] + 5f;
            return r;
        }

        [Test]
        public void AllFour_Pass_ThenEachFailsOnItsOwnCondition()
        {
            Assert.That(judge, Is.Not.Null, "C24 has the four contract sectors");
            Assert.That(t, Is.Not.Null, "S29 has certified contract targets");
            Assert.That(judge.Provisional, Is.False);
            Assert.That(judge.ApexCount, Is.EqualTo(2));
            Assert.That(judge.ExitCount, Is.EqualTo(3));
            long limit = 280_000;

            ContractVerdict all = judge.Evaluate(Passing(), true, limit - 1000, limit);
            Assert.That(all.Passed, Is.EqualTo(4), all.Detail);

            ContractRun r = Passing(); r.ApexHit[1] = false;
            ContractVerdict v = judge.Evaluate(r, true, limit - 1000, limit);
            Assert.That(v.Entry, Is.False); StringAssert.Contains("apex gates 1/2", v.Detail); Assert.That(v.Passed, Is.EqualTo(3));

            r = Passing(); r.EntryMs = t.EntrySectorMs + 1;
            Assert.That(judge.Evaluate(r, true, limit - 1000, limit).Entry, Is.False, "sector time");

            r = Passing(); r.ArcRaw = t.ArcDriftRaw - 1;
            v = judge.Evaluate(r, true, limit - 1000, limit);
            Assert.That(v.Arc, Is.False); StringAssert.Contains("Arc: drift", v.Detail);

            r = Passing(); r.DescentWalls = 1;
            v = judge.Evaluate(r, true, limit - 1000, limit);
            Assert.That(v.Descent, Is.False); StringAssert.Contains("wall impact", v.Detail);

            r = Passing(); r.DescentResets = 1;
            Assert.That(judge.Evaluate(r, true, limit - 1000, limit).Descent, Is.False, "a reset in the Descent");

            r = Passing(); r.BrakeOffAtReleasePoint = false;
            v = judge.Evaluate(r, true, limit - 1000, limit);
            Assert.That(v.Descent, Is.False); StringAssert.Contains("still on at the release point", v.Detail);

            r = Passing(); r.BrakeExitKmh = t.BrakeExitMaxKmh + 1f;
            Assert.That(judge.Evaluate(r, true, limit - 1000, limit).Descent, Is.False, "too fast out of the zone");

            r = Passing(); r.ExitKmh[2] = t.HorizonExitKmh[2] - 1f;
            Assert.That(judge.Evaluate(r, true, limit - 1000, limit).Horizon, Is.False, "exit speed");

            v = judge.Evaluate(Passing(), true, limit + 1, limit);
            Assert.That(v.Horizon, Is.False); StringAssert.Contains("time limit", v.Detail);
            Assert.That(judge.Evaluate(Passing(), false, 0, limit).Horizon, Is.False, "a run that did not finish");
        }

        [Test]
        public void Hard_HasItsOwnTighterTargets_AndOtherCoursesHaveNoJudge()
        {
            ContentCatalogue cat = ContentFiles.LoadProjectCatalogue();
            ContractJudge hard = ContractJudge.ForEvent(track, cat, "campaign", "S29", CampaignMode.Hard);
            Assert.That(hard.Provisional, Is.False, "Hard S29 was certified from its own reference runs");
            Assert.That(hard.Targets.EntrySectorMs, Is.LessThan(t.EntrySectorMs), "tighter legal targets (spec S29 Hard)");
            Assert.That(ContractJudge.ForEvent(track, cat, "freeplay", null, CampaignMode.Normal), Is.Null, "only a campaign event is judged");

            string c01 = File.ReadAllText("Assets/Content/Courses/C01/route.json");
            TrackData other = CourseGenerator.BuildTrackData(RouteIO.Parse(c01), RouteIO.SourceHash(c01));
            try { Assert.That(ContractJudge.ForEvent(other, cat, "campaign", "S01", CampaignMode.Normal), Is.Null); }
            finally { Object.DestroyImmediate(other); }
        }
    }
}
