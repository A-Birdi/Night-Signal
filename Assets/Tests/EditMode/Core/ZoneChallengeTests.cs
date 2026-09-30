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
    /// Challenge-zone predicates (Appendix E CH17, CH19, CH22, CH27) read the entrant's <see cref="ZoneChainRun"/> for their
    /// own course only and only after a finish; the chain rules themselves are Core-tested (Services/CoreTests ZoneChainTests).
    /// </summary>
    public sealed class ZoneChallengeTests
    {
        static ChallengeZone Z(string challenge, string kind, float start, float end, float offset = 0f) =>
            new ChallengeZone { Id = challenge + start, Challenge = challenge, Kind = kind, StartMetres = start, EndMetres = end, LineOffset = offset, LineTolerance = 1.2f };

        /// <summary>One slide at <paramref name="slip"/>° and 72 km/h from 0 to <paramref name="metres"/>, crossing the bank gate at <paramref name="gate"/>, then straightening.</summary>
        static ZoneChainRun Slide(ZoneChainRun run, float metres, float slip, float lateral = 0f, float gate = -1f, string gateChallenge = "")
        {
            double at = 0;
            int ZoneAt(double d)
            {
                for (int i = 0; i < run.Zones.Count; i++)
                    if (d >= run.Zones[i].StartMetres && d <= run.Zones[i].EndMetres) return i;
                return -1;
            }
            void Step(float s)
            {
                double before = at;
                at += 20.0 / 60.0;
                run.Step(new ZoneChainSample { DeltaSeconds = 1f / 60f, SpeedKmh = 72f, SlipAngleDegrees = s, ProgressMetres = at, MovingInLegalDirection = true, OnRoad = true,
                    Zone = ZoneAt(at), LateralMetres = lateral, BankGate = gate >= 0f && before < gate && at >= gate ? gateChallenge : "" });
            }
            while (at < metres) Step(slip);
            for (int i = 0; i < 90; i++) Step(2f);
            return run;
        }

        static EntrantProgress Finished(bool finished = true)
        {
            var track = ScriptableObject.CreateInstance<TrackData>();
            return new EntrantProgress(track) { Finished = finished };
        }

        [Test]
        public void ZonePredicates_ReadTheChains_OnTheirCourseOnly()
        {
            ZoneChainRun link = Slide(new ZoneChainRun(new[]
            {
                Z("CH17", ChallengeZone.Transition, 10, 40), Z("CH17", ChallengeZone.Transition, 40, 80), Z("CH17", ChallengeZone.Transition, 78, 120),
            }), 130, 28f);
            Assert.That(ChallengePredicates.Evaluate("C03", Finished(), zones: link), Does.Contain("CH17"));
            Assert.That(ChallengePredicates.Evaluate("C05", Finished(), zones: link), Does.Not.Contain("CH17"), "C03 only");
            Assert.That(ChallengePredicates.Evaluate("C03", Finished(false), zones: link), Is.Empty, "then finish");

            ZoneChainRun demo = Slide(new ZoneChainRun(new[] { Z("CH19", ChallengeZone.Demo, 0, 200) }), 70, 27f); // 3.5 s in band
            Assert.That(ChallengePredicates.Evaluate("C05", Finished(), zones: demo), Does.Contain("CH19"));
            ZoneChainRun steep = Slide(new ZoneChainRun(new[] { Z("CH19", ChallengeZone.Demo, 0, 200) }), 70, 40f); // outside the band
            Assert.That(ChallengePredicates.Evaluate("C05", Finished(), zones: steep), Does.Not.Contain("CH19"));

            ZoneChainRun clips = Slide(new ZoneChainRun(new[] { Z("CH22", ChallengeZone.Clip, 10, 50, 3f), Z("CH22", ChallengeZone.Clip, 100, 140, 3f) }), 150, 25f, lateral: 3.2f);
            Assert.That(ChallengePredicates.Evaluate("C09", Finished(), zones: clips), Does.Contain("CH22"));
            ZoneChainRun offLine = Slide(new ZoneChainRun(clips.Zones), 150, 25f, lateral: 0f);
            Assert.That(ChallengePredicates.Evaluate("C09", Finished(), zones: offLine), Does.Not.Contain("CH22"));

            ChallengeZone[] six = Enumerable.Range(0, 6).Select(i => Z("CH27", ChallengeZone.Transition, 20 + 60 * i, 60 + 60 * i)).ToArray();
            ZoneChainRun atGate = Slide(new ZoneChainRun(six), 400, 20f, gate: 390f, gateChallenge: "CH27");
            Assert.That(ChallengePredicates.Evaluate("C19", Finished(), zones: atGate), Does.Contain("CH27"));
            ZoneChainRun released = Slide(new ZoneChainRun(six), 380, 20f, gate: 420f, gateChallenge: "CH27"); // straightened before the gate
            Assert.That(ChallengePredicates.Evaluate("C19", Finished(), zones: released), Does.Not.Contain("CH27"));
        }
    }
}
