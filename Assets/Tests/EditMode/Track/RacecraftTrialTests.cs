using System.Collections.Generic;
using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Rules;
using NightSignal.Front;
using NightSignal.Race;
using NightSignal.Track;
using NightSignal.Track.Generation;
using NightSignal.Vehicle;
using NUnit.Framework;

namespace NightSignal.Tests.Track
{
    /// <summary>
    /// Racecraft challenge trials (slice 3): the offline plan races the trial's fixed field — its own cars, roles and paces,
    /// not ones chosen by the cap — with contact on, and a trial that starts the player last puts the human behind every AI
    /// car on the grid while the local player stays entrant 0.
    /// </summary>
    public sealed class RacecraftTrialTests
    {
        static TrackData Track(string course)
        {
            string json = System.IO.File.ReadAllText(RouteIO.RoutePath(course));
            return CourseGenerator.BuildTrackData(RouteIO.Parse(json), RouteIO.SourceHash(json));
        }

        [Test]
        public void TheFixedField_IsPlaced_AndThePlayerStartsLast()
        {
            ContentLibrary lib = ContentLibrary.Load();
            ChallengeTrialDef trial = lib.Catalogue.ChallengeTrials.Find("TR-CH41");
            LocalEventPlan plan = LocalEvents.Trial(trial, lib.Catalogue.Course(trial.Course).Format);
            Assert.That(plan.Kind, Is.EqualTo(EventKind.FreeplayCircuit));
            Assert.That(plan.Rules.Contact, Is.EqualTo(ContactPolicy.LightContact));
            Assert.That(plan.OpposingAi.Count, Is.EqualTo(5));

            var humans = new List<HumanSlot> { new HumanSlot { EntrantId = "local", DisplayName = "You", CarId = trial.Loaner.Car } };
            RaceSimulation sim = RaceSimulation.Build(Track(trial.Course), lib, plan.Rules, humans, plan.OpposingAi, PlaneVehicleWorld.Flat);
            Assert.That(sim.Entrants.Count, Is.EqualTo(6));
            Assert.That(sim.Entrants[0].Human, Is.True, "the local player is still entrant 0");
            Assert.That(sim.Entrants[0].Roster.GridSlot, Is.EqualTo(5), "sixth on the grid");
            Assert.That(sim.Entrants.Skip(1).Select(e => e.Roster.GridSlot), Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));
            Assert.That(sim.Entrants.Skip(1).Select(e => e.Roster.CarId), Is.All.EqualTo("V07"), "the field's own cars");
            Assert.That(sim.Entrants.Skip(1).Select(e => e.Roster.Role), Is.All.EqualTo("field"));
            // Grid slots behind one another: the player's is the furthest back.
            float playerDistance = sim.Track.Grid[5].Distance;
            Assert.That(sim.Entrants.Skip(1).All(e => sim.Track.Grid[e.Roster.GridSlot].Distance > playerDistance), Is.True);
        }

        [Test]
        public void AFieldCarsPace_AndRole_AreItsOwn()
        {
            ContentLibrary lib = ContentLibrary.Load();
            ChallengeTrialDef trial = lib.Catalogue.ChallengeTrials.Find("TR-CH40");
            LocalEventPlan plan = LocalEvents.Trial(trial, lib.Catalogue.Course(trial.Course).Format);
            var humans = new List<HumanSlot> { new HumanSlot { EntrantId = "local", DisplayName = "You", CarId = trial.Loaner.Car } };
            RaceSimulation sim = RaceSimulation.Build(Track(trial.Course), lib, plan.Rules, humans, plan.OpposingAi, PlaneVehicleWorld.Flat);
            Assert.That(sim.Entrants.Skip(1).Select(e => e.Roster.CarId), Is.EqualTo(trial.Field.Select(c => c.Car)));
            Assert.That(sim.Entrants.Skip(1).Select(e => e.Ai.Profile.PaceScale), Is.All.EqualTo(0.95f));
            Assert.That(sim.Entrants[0].Roster.GridSlot, Is.EqualTo(3));

            // Without the trial's rules the same opponents are chosen by the cap and the human keeps pole.
            var free = new RaceEventRules { Kind = "freeplay", Contact = ContactPolicy.LightContact, StageNumber = 10, CarCapPi = 300 };
            RaceSimulation open = RaceSimulation.Build(Track(trial.Course), lib, free, humans, plan.OpposingAi, PlaneVehicleWorld.Flat);
            Assert.That(open.Entrants[0].Roster.GridSlot, Is.EqualTo(0));
            Assert.That(open.Entrants.Skip(1).All(e => lib.Catalogue.Car(e.Roster.CarId).BasePI <= 300), Is.True);
        }
    }
}
