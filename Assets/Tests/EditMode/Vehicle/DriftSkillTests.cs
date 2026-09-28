using System.IO;
using System.Linq;
using NightSignal.AI;
using NightSignal.Core.Content;
using NightSignal.Tests.Core;
using NUnit.Framework;

namespace NightSignal.Tests.Vehicle
{
    /// <summary>
    /// AI drift skill per driver (spec §13): unset keeps the tuned drift controller exactly; rivals' skill rises with the
    /// campaign and follows their tendency; generic opponents spread around the tuned point. (The PlayMode
    /// DriftAttackTests measure what the skill does to banked scores on a course.)
    /// </summary>
    public sealed class DriftSkillTests
    {
        static ContentCatalogue catalogue;
        static NightSignal.Track.TrackData track;
        static NightSignal.Track.TrackData Track => track != null ? track : (track = UnityEngine.ScriptableObject.CreateInstance<NightSignal.Track.TrackData>());
        static ContentCatalogue Catalogue => catalogue ?? (catalogue = ContentCatalogue.Load(AddendumRulesTests.LoadDocuments()));

        [Test]
        public void UnsetSkill_KeepsTheTunedController()
        {
            var unset = new RouteFollower(Track, null, DriverProfile.Validator);
            Assert.That(DriverProfile.Validator.DriftSkill, Is.EqualTo(0f));
            Assert.That(unset.DriftSlipDeg, Is.EqualTo(28f).Within(1e-4f));
            var baseline = new RouteFollower(Track, null, new DriverProfile { DriftSkill = DriverProfile.BaselineDriftSkill });
            Assert.That(baseline.DriftSlipDeg, Is.EqualTo(28f).Within(1e-4f));
            Assert.That(unset.DriftEntrySpeed, Is.EqualTo(19f).Within(1e-4f));
            Assert.That(unset.CountersteerGain, Is.EqualTo(0.4f).Within(1e-4f));
            var low = new RouteFollower(Track, null, new DriverProfile { DriftSkill = 0.05f });
            var high = new RouteFollower(Track, null, new DriverProfile { DriftSkill = 1f });
            Assert.That((unset.AttemptShare, unset.ReFlicks), Is.EqualTo((1f, 0)));
            Assert.That((baseline.AttemptShare, baseline.ReFlicks), Is.EqualTo((1f, 0)));
            Assert.That(low.AttemptShare, Is.InRange(0.4f, 0.5f), "a novice commits to under half the zones");
            Assert.That(high.ReFlicks, Is.EqualTo(2));
            Assert.That(high.AttemptShare, Is.EqualTo(1f));
            // The slide itself is the tuned controller for everyone.
            foreach (RouteFollower f in new[] { low, high })
                Assert.That((f.DriftSlipDeg, f.DriftEntrySpeed, f.CountersteerGain, f.EdgeMargin), Is.EqualTo((28f, 19f, 0.4f, 0.5f)));
        }

        [Test]
        public void Rivals_RiseWithTheCampaign_AndFollowTheirTendency()
        {
            RivalDef rotation = Catalogue.Rivals.First(r => r.Tendency == "rotation-specialist");
            RivalDef keeper = Catalogue.Rivals.First(r => r.Tendency == "margin-keeper");
            foreach (int stage in new[] { 1, 15, 30 })
                Assert.That(AiProfiles.DriftSkillFor(rotation, stage), Is.GreaterThan(AiProfiles.DriftSkillFor(keeper, stage)), $"stage {stage}");
            foreach (RivalDef r in Catalogue.Rivals)
            {
                float early = AiProfiles.For(r, 1).DriftSkill, late = AiProfiles.For(r, 30).DriftSkill;
                Assert.That(late, Is.GreaterThan(early), r.Id);
                Assert.That(early, Is.InRange(0.2f, 0.98f), r.Id);
                Assert.That(late, Is.InRange(0.2f, 0.98f), r.Id);
            }
            var generic = Enumerable.Range(0, 12).Select(i => AiProfiles.Generic(i).DriftSkill).ToList();
            Assert.That(generic.All(s => s >= 0.45f && s <= 0.8f), Is.True);
            Assert.That(generic.Distinct().Count(), Is.GreaterThan(3), "generic opponents differ");
        }
    }
}
