using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Content;
using NightSignal.Core.Profiles;
using NightSignal.Core.Rules;
using NightSignal.Front;
using NUnit.Framework;

namespace NightSignal.Tests.Vehicle
{
    /// <summary>
    /// The offline Freeplay field (spec §13, CH38/CH73): authored rivals as online — the named lead first, then a shuffled
    /// pool that never holds the finale-only rivals; Time Attack has none.
    /// </summary>
    public sealed class LocalFreeplayFieldTests
    {
        static ContentCatalogue Cat => ContentFiles.LoadProjectCatalogue();
        static readonly LocalCarChoice Car = new LocalCarChoice { ModelId = "V01" };

        [Test]
        public void NamedRivalLeads_TheRestAreAuthored_NoFinaleRivals()
        {
            ContentCatalogue cat = Cat;
            CourseDef course = cat.Course("C01");
            for (int seed = 0; seed < 50; seed++)
            {
                LocalEventPlan plan = LocalEvents.Freeplay(cat, course, false, 5, 999, Car, "R08", new System.Random(seed));
                Assert.AreEqual(5, plan.OpposingAi.Count);
                Assert.AreEqual("R08", plan.OpposingAi[0]);
                Assert.AreEqual(5, plan.OpposingAi.Distinct().Count());
                foreach (string id in plan.OpposingAi)
                {
                    Assert.IsTrue(cat.TryRival(id, out _), id);
                    Assert.IsFalse(FinalRivals.IsFinaleOnly(id), id);
                }
            }
        }

        [Test]
        public void FinaleRivalCannotBeNamed_TimeAttackHasNoField()
        {
            ContentCatalogue cat = Cat;
            CourseDef course = cat.Course("C01");
            LocalEventPlan named = LocalEvents.Freeplay(cat, course, false, 3, 999, Car, FinalRivals.NormalFinal, new System.Random(1));
            Assert.AreEqual(3, named.OpposingAi.Count);
            Assert.IsFalse(named.OpposingAi.Any(FinalRivals.IsFinaleOnly));
            Assert.IsEmpty(LocalEvents.Freeplay(cat, course, true, 5, 999, Car, "R08").OpposingAi);
        }

        [Test]
        public void Freeplay_RacesTheCoursesOwnConditions_AsOnline()
        {
            // The surface is left to the course (resolved when it loads: C08 wet, C11/C15/C20 damp), as an online Freeplay
            // race has it — never a fixed "dry" (the RaceEventRules default).
            ContentCatalogue cat = Cat;
            foreach (string id in new[] { "C15", "C08", "C01" })
            {
                Assert.IsNull(LocalEvents.Freeplay(cat, cat.Course(id), false, 3, 999, Car).Rules.Surface, id + " race");
                Assert.IsNull(LocalEvents.Freeplay(cat, cat.Course(id), true, 0, 999, Car).Rules.Surface, id + " time attack");
                Assert.IsNull(LocalEvents.Freeplay(cat, cat.Course(id), true, 0, 999, Car).Rules.Lighting, id + " lighting");
            }
        }

        [Test]
        public void Freeplay_DriftAttack_IsRankedByDrift_WithLightContactAndAnAuthoredField()
        {
            // As online (V-047): light contact, opponents allowed, finishers ranked by banked raw drift score; the format names
            // the record and the challenge predicates (CH21, CH24, CH26 read "drift-attack").
            ContentCatalogue cat = Cat;
            LocalEventPlan plan = LocalEvents.Freeplay(cat, cat.Course("C12"), false, 3, 999, Car, conditions: "wet-night", driftAttack: true);
            Assert.AreEqual(EventKind.FreeplayDriftAttack, plan.Kind);
            Assert.AreEqual("drift-attack", plan.FreeplayFormat);
            Assert.IsTrue(plan.Rules.DriftRanking);
            Assert.AreEqual(ContactPolicy.LightContact, plan.Rules.Contact);
            Assert.AreEqual(3, plan.OpposingAi.Count);
            Assert.AreEqual("wet", plan.Rules.Surface);
            Assert.IsFalse(LocalEvents.Freeplay(cat, cat.Course("C12"), false, 3, 999, Car).Rules.DriftRanking);
            Assert.Throws<System.ArgumentException>(() => LocalEvents.Freeplay(cat, cat.Course("C12"), true, 0, 999, Car, driftAttack: true));
        }

        [Test]
        public void Freeplay_APreset_FixesSurfaceAndLighting_AsTheGameServerDoes()
        {
            // The Offline hub's Conditions row and the Convoy page offer the same Core table; the game server resolves a preset
            // with ConditionPresets.Surface/Lighting, so a Local plan carries exactly the preset's surface and lighting.
            ContentCatalogue cat = Cat;
            foreach (ConditionPreset p in ConditionPresets.All)
            {
                LocalEventPlan plan = LocalEvents.Freeplay(cat, cat.Course("C15"), false, 3, 999, Car, conditions: p.Id);
                Assert.AreEqual(p.Surface, plan.Rules.Surface, p.Id + " surface");
                Assert.AreEqual(p.Lighting, plan.Rules.Lighting, p.Id + " lighting");
                if (p.Lighting != null)
                    Assert.AreEqual(p.Lighting, NightSignal.Atmosphere.LightingPresets.For(p.Lighting).Id, p.Id + " has a lighting preset");
            }
            Assert.AreEqual("wet", LocalEvents.Freeplay(cat, cat.Course("C12"), true, 0, 999, Car, conditions: "wet-night").Rules.Surface);
            Assert.IsTrue(NightSignal.Atmosphere.LightingPresets.For("fog").PracticalLights, "headlights on in fog");
        }
    }
}
