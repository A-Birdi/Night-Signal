using System.Linq;
using NightSignal.Core.Rules;
using NightSignal.Net;
using NightSignal.Race;
using NightSignal.Track;
using NUnit.Framework;
using UnityEngine;

namespace NightSignal.Tests.Core
{
    /// <summary>
    /// Drift challenges from the scorer's banked chains (Appendix E CH18, CH20, CH21, CH24): each chain's raw points and
    /// its slowest scoring step are kept; the predicates read them for the right course, format and surface only.
    /// </summary>
    public sealed class DriftChallengeTests
    {
        static float progress;

        /// <summary>Drives one chain: <paramref name="metres"/> of valid 25° drifting at <paramref name="kmh"/>, then straightens until it banks.</summary>
        static void Chain(DriftScorer d, int metres, float kmh)
        {
            for (int i = 0; i < metres; i++)
                d.Step(new DriftSample { ProgressMetres = progress += 1f, SpeedKmh = kmh, SlipAngleDegrees = 25f, JudgedZone = 0, LineOffsetMetres = 0f,
                    LineToleranceMetres = 2f, MovingInLegalDirection = true, OnRoad = true, DeltaSeconds = 1f / 60f });
            for (int i = 0; i < 70; i++)
                d.Step(new DriftSample { ProgressMetres = progress += 1f, SpeedKmh = kmh, SlipAngleDegrees = 2f, JudgedZone = -1, LineOffsetMetres = 0f,
                    LineToleranceMetres = 2f, MovingInLegalDirection = true, OnRoad = true, DeltaSeconds = 1f / 60f });
        }

        static EntrantProgress Finished()
        {
            var track = ScriptableObject.CreateInstance<TrackData>();
            return new EntrantProgress(track) { Finished = true };
        }

        [Test]
        public void BankedChains_KeepRawAndSlowestStep()
        {
            progress = 0f;
            var d = new DriftScorer();
            Chain(d, 60, 70f);   // 60 m × 100 × 1.0 × 1.25 = 7,500
            Chain(d, 20, 40f);   // 2,500
            Assert.That(d.BankedChains.Select(c => (int)c.Raw), Is.EqualTo(new[] { 7_500, 2_500 }));
            Assert.That(d.BankedChains[0].MinSpeedKmh, Is.EqualTo(70f));
            Assert.That(d.BankedChains[1].MinSpeedKmh, Is.EqualTo(40f));
            Assert.That(d.ChainsBanked, Is.EqualTo(2));
        }

        [Test]
        public void DriftPredicates_ReadTheChains_OnTheirCourseOnly()
        {
            progress = 0f;
            var two = new DriftScorer();
            Chain(two, 60, 70f);
            Chain(two, 50, 70f); // 6,250
            Assert.That(ChallengePredicates.Evaluate("C04", Finished(), two), Does.Contain("CH18"));
            Assert.That(ChallengePredicates.Evaluate("C05", Finished(), two), Does.Not.Contain("CH18"), "C04 only");

            progress = 0f;
            var arc = new DriftScorer();
            Chain(arc, 65, 70f); // 65 m × 100 × 1.25 = 8,125 in one chain
            Assert.That(ChallengePredicates.Evaluate("T00", Finished(), arc), Does.Contain("CH16"));
            progress = 0f;
            var split = new DriftScorer();
            Chain(split, 40, 70f);
            Chain(split, 40, 70f); // 10,000 over two chains of 5,000: no single 8,000 arc
            Assert.That(ChallengePredicates.Evaluate("T00", Finished(), split), Does.Not.Contain("CH16"));
            Assert.That(ChallengePredicates.Evaluate("C01", Finished(), arc), Does.Not.Contain("CH16"), "T00 only");

            progress = 0f;
            var long1 = new DriftScorer();
            Chain(long1, 480, 60f); // 60,000 raw in one chain, never below 60 km/h
            Assert.That(ChallengePredicates.Evaluate("C15", Finished(), long1), Does.Contain("CH24"));
            progress = 0f;
            var slow = new DriftScorer();
            Chain(slow, 480, 40f); // the same points but slower than 45 km/h while scoring
            Assert.That(ChallengePredicates.Evaluate("C15", Finished(), slow), Does.Not.Contain("CH24"));

            progress = 0f;
            var big = new DriftScorer();
            Chain(big, 300, 70f); // 37,500
            Chain(big, 300, 70f); // 75,000 over two chains
            Assert.That(ChallengePredicates.Evaluate("C01", Finished(), big, "drift-attack"), Does.Contain("CH20"));
            Assert.That(ChallengePredicates.Evaluate("C01", Finished(), big, "sprint"), Does.Not.Contain("CH20"), "Drift Attack only");
            Assert.That(ChallengePredicates.Evaluate("C08", Finished(), big, "drift-attack", "wet"), Does.Contain("CH21"));
            Assert.That(ChallengePredicates.Evaluate("C08", Finished(), big, "drift-attack", "dry"), Does.Not.Contain("CH21"), "the wet event only");
            var unfinished = Finished();
            unfinished.Finished = false;
            Assert.That(ChallengePredicates.Evaluate("C08", unfinished, big, "drift-attack", "wet"), Is.Empty, "a finish is required");
        }
    }
}
