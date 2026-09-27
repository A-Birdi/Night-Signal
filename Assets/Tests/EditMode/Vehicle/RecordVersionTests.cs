using System.Linq;
using NightSignal.Core.Profiles;
using NightSignal.Front;
using NightSignal.Race;
using Newtonsoft.Json;
using NUnit.Framework;

namespace NightSignal.Tests.Vehicle
{
    /// <summary>
    /// Addendum 03 I02/I03 through the game's own record key builder: a personal best set under the previous
    /// classification (classify-1) is kept — never erased or overwritten — survives a save/load round trip, and is shown as
    /// a legacy result for the same event under the current rules (classify-2) rather than compared with it; a new result
    /// under the current rules is a separate best. Camera/HUD preferences are not part of any record key.
    /// </summary>
    public sealed class RecordVersionTests
    {
        static RecordKey Key(string scoringVersion)
        {
            var rules = new RaceEventRules { Kind = "freeplay", Surface = "dry" };
            RecordRuleset r = LocalEvents.Ruleset(rules, "route-hash-C01");
            r.ScoringVersion = scoringVersion;
            return RecordKey.ForFreeplay(ProgressionDomain.Local, "C01", "sprint", MetricKind.ElapsedTime, r);
        }

        static RecordEntry Entry(RecordKey key, long ms) => new RecordEntry
        {
            Key = key, Value = ms, CarModelId = "V01", CarInstanceId = "car-1", Verification = RecordVerification.LocalUnverified,
        };

        [Test]
        public void I02_OldClassificationBest_IsKept_ShownAsLegacy_AndNeverCompared()
        {
            Assert.That(RaceSimulation.ScoringVersion, Is.EqualTo("classify-2"), "the Addendum 03 classification is current");
            RecordKey oldKey = Key("classify-1"), current = Key(RaceSimulation.ScoringVersion);
            Assert.That(oldKey.Identity, Is.EqualTo(current.Identity), "same event identity");
            Assert.That(oldKey.Equals(current), Is.False, "different rules: a different record key");

            var bests = new PersonalBests { Domain = ProgressionDomain.Local };
            Assert.That(bests.Offer(Entry(oldKey, 95_000)).Outcome, Is.Not.EqualTo(RecordUpdateOutcome.Rejected));

            // Save/load round trip (the profile stores records as JSON).
            string json = JsonConvert.SerializeObject(bests);
            PersonalBests loaded = JsonConvert.DeserializeObject<PersonalBests>(json);
            Assert.That(loaded.Entries.Count, Is.EqualTo(1), "the old best survives a round trip");

            RecordView view = loaded.View(current);
            Assert.That(view.State, Is.EqualTo(RecordDisplayState.LegacyIncompatible), "shown as a legacy result, not a current best");
            Assert.That(view.Legacy.Single().Value, Is.EqualTo(95_000));
            Assert.That(RecordCompatibility.Check(oldKey, current).Mismatches, Contains.Item(RecordMismatch.ScoringVersion));

            // A slower result under the current rules is a new current best, and the old one is untouched.
            RecordUpdateResult r = loaded.Offer(Entry(current, 101_000));
            Assert.That(r.Outcome, Is.Not.EqualTo(RecordUpdateOutcome.Rejected));
            Assert.That(loaded.View(current).Best.Value, Is.EqualTo(101_000), "not compared with the legacy 95.000 s");
            Assert.That(loaded.Entries.Any(e => e.Key.Equals(oldKey) && e.Value == 95_000), Is.True, "the old best is not erased");
        }
    }
}
