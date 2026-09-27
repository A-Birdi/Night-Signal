using NightSignal.Core.Profiles;
using NightSignal.Core.Rules;

namespace NightSignal.CoreTests;

public sealed class RecordTests
{
    static RecordKey StageKey(CampaignMode mode = CampaignMode.Normal, ProgressionDomain domain = ProgressionDomain.Local, MetricKind metric = MetricKind.ElapsedTime,
        RecordRuleset rules = null) =>
        RecordKey.ForCampaignStage(domain, TestContent.Catalogue.Stage("S05"), TestContent.Catalogue.Course("C05"), mode, metric, rules ?? TestContent.Rules);

    static RecordEntry Entry(RecordKey key, long value, string car = "V01", string evt = "le_a", int minute = 0,
        RecordVerification v = RecordVerification.LocalUnverified) => new()
    {
        Key = key, Value = value, CarModelId = car, Verification = v,
        Provenance = new RecordProvenance { EventInstanceId = evt, AchievedUtc = TestContent.T0.AddMinutes(minute), Origin = RecordEntry.LocalOrigin },
    };

    // ---------------- compatibility ----------------

    [Fact]
    public void IdenticalKeys_AreCompatible_AndEqual()
    {
        RecordKey a = StageKey(), b = StageKey();
        Assert.True(RecordCompatibility.Check(a, b).Compatible);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Empty(a.Validate());
        Assert.Equal(TestContent.Catalogue.Stage("S05").MaxPI, a.CarCapPi); // stage cap used when the ruleset leaves it open
    }

    [Fact]
    public void GhostedNonContact_NeverComparesWithContact()
    {
        RecordKey contact = RecordKey.ForFreeplay(ProgressionDomain.Local, "C01", "sprint", MetricKind.ElapsedTime, TestContent.Rules);
        var ghostRules = new RecordRuleset
        {
            CourseRevision = "r1", PhysicsVersion = "phys-3", ScoringVersion = "score-2", Conditions = "night-dry", Contact = ContactPolicy.NonContact,
        };
        RecordKey ghost = RecordKey.ForFreeplay(ProgressionDomain.Local, "C01", "sprint", MetricKind.ElapsedTime, ghostRules);
        RecordCompatibility c = RecordCompatibility.Check(contact, ghost);
        Assert.False(c.Compatible);
        Assert.Equal(RecordMismatch.ContactPolicy, c.Mismatches[0]);
        Assert.Contains("Non-contact", c.Explanation);
    }

    [Fact]
    public void NormalAndHard_KeepSeparateRecords()
    {
        RecordCompatibility c = RecordCompatibility.Check(StageKey(CampaignMode.Normal), StageKey(CampaignMode.Hard));
        Assert.False(c.Compatible);
        Assert.Equal(RecordMismatch.Difficulty, c.Mismatches[0]);
    }

    [Fact]
    public void LocalUnverified_NeverComparesWithOnlineVerified()
    {
        RecordKey local = StageKey(domain: ProgressionDomain.Local), online = StageKey(domain: ProgressionDomain.Online);
        RecordCompatibility c = RecordCompatibility.Check(local, online);
        Assert.False(c.Compatible);
        Assert.Equal(RecordMismatch.Domain, c.Mismatches[0]);
        // Even with a hand-built identical key, the labels are not rankable against each other.
        RecordEntry a = Entry(local, 200_000), b = Entry(local, 190_000, v: RecordVerification.OnlineVerified);
        Assert.Equal(RecordCompareOutcome.Incompatible, RecordComparer.Compare(a, b));
        Assert.Equal(RecordCompareOutcome.Incompatible, RecordComparer.Compare(Entry(online, 1, v: RecordVerification.OnlineVerified),
            Entry(online, 2, v: RecordVerification.PendingVerification)));
    }

    [Fact]
    public void TeamSum_NeverComparesWithAPersonalScore()
    {
        RecordKey team = RecordKey.ForTeamTrial(ProgressionDomain.Local, "TT_DRIFT", "C03", "drift-attack", "standard", TeamTrialKind.Drift, 1, TestContent.Rules);
        RecordKey personal = team.WithMetric(MetricKind.RawDriftScore);
        personal.TeamSize = 0;
        personal.TeamHumans = 0;
        Assert.Empty(team.Validate());
        Assert.Empty(personal.Validate());
        RecordCompatibility c = RecordCompatibility.Check(team, personal);
        Assert.False(c.Compatible);
        Assert.Equal(RecordMismatch.TeamVersusPersonal, c.Mismatches[0]);
        Assert.Contains("team result", c.Explanation);
        // Team composition is part of the key (H = 1 vs H = 3 are different records).
        RecordKey threeHumans = RecordKey.ForTeamTrial(ProgressionDomain.Local, "TT_DRIFT", "C03", "drift-attack", "standard", TeamTrialKind.Drift, 3, TestContent.Rules);
        Assert.Contains(RecordMismatch.TeamComposition, RecordCompatibility.Check(team, threeHumans).Mismatches);
    }

    [Fact]
    public void RulesVersionChanges_AreIncompatible()
    {
        var newPhysics = new RecordRuleset
        {
            CourseRevision = "r1", PhysicsVersion = "phys-4", ScoringVersion = "score-2", Conditions = "night-dry", Contact = ContactPolicy.LightContact,
        };
        RecordCompatibility c = RecordCompatibility.Check(StageKey(), StageKey(rules: newPhysics));
        Assert.Equal(new[] { RecordMismatch.PhysicsVersion }, c.Mismatches);
    }

    [Fact]
    public void InvalidKeys_AreReported()
    {
        RecordKey k = StageKey();
        k.Difficulty = "casual";
        Assert.NotEmpty(k.Validate());
        RecordKey personalWithTeam = StageKey();
        personalWithTeam.TeamSize = 6;
        Assert.NotEmpty(personalWithTeam.Validate());
    }

    // ---------------- personal bests ----------------

    [Fact]
    public void PersonalBest_ImprovesOnlyWhenStrictlyBetter_TiesKeepTheEarlierRecord()
    {
        var pbs = new PersonalBests();
        RecordKey key = StageKey();
        Assert.Equal(RecordUpdateOutcome.NewPersonalBest, pbs.Offer(Entry(key, 200_000, evt: "le_1")).Outcome);
        Assert.Equal(RecordUpdateOutcome.NotImproved, pbs.Offer(Entry(key, 200_001, evt: "le_2", minute: 1)).Outcome);
        Assert.Equal(RecordUpdateOutcome.NewPersonalBest, pbs.Offer(Entry(key, 199_999, evt: "le_3", minute: 2)).Outcome);
        RecordUpdateResult tie = pbs.Offer(Entry(key, 199_999, evt: "le_4", minute: 3));
        Assert.Equal(RecordUpdateOutcome.Tie, tie.Outcome);
        Assert.Equal("le_3", pbs.Best(key).Provenance.EventInstanceId);
        Assert.Single(pbs.Entries);
    }

    [Fact]
    public void HigherIsBetterMetrics_ImproveUpwards()
    {
        var pbs = new PersonalBests();
        RecordKey key = StageKey(metric: MetricKind.RawDriftScore);
        pbs.Offer(Entry(key, 5_000));
        Assert.Equal(RecordUpdateOutcome.NotImproved, pbs.Offer(Entry(key, 4_999)).Outcome);
        Assert.Equal(RecordUpdateOutcome.NewPersonalBest, pbs.Offer(Entry(key, 5_001)).Outcome);
        Assert.Equal(5_001, pbs.Best(key).Value);
    }

    [Fact]
    public void SameCarFilter_KeepsABestPerCarModel()
    {
        var pbs = new PersonalBests();
        RecordKey key = StageKey();
        pbs.Offer(Entry(key, 200_000, car: "V01"));
        RecordUpdateResult v02 = pbs.Offer(Entry(key, 210_000, car: "V02"));
        Assert.Equal(RecordUpdateOutcome.NewCarBest, v02.Outcome);
        Assert.Equal(200_000, pbs.Best(key).Value);
        Assert.Equal(210_000, pbs.Best(key, RecordFilter.SameCar("V02")).Value);
        Assert.Null(pbs.Best(key, RecordFilter.SameCar("V03")));
        Assert.Equal(RecordCompareOutcome.Incompatible,
            RecordComparer.Compare(Entry(key, 1, car: "V01"), Entry(key, 2, car: "V02"), RecordFilter.SameCar("V01")));
        Assert.Equal(RecordCompareOutcome.Better, RecordComparer.Compare(Entry(key, 1, car: "V01"), Entry(key, 2, car: "V02")));
        Assert.Equal(RecordCompareOutcome.Tie, RecordComparer.Compare(Entry(key, 2), Entry(key, 2)));
    }

    [Fact]
    public void LocalStore_RefusesOnlineVerifiedPendingAndWrongDomainValues()
    {
        var pbs = new PersonalBests { Domain = ProgressionDomain.Local };
        Assert.Equal(RecordUpdateOutcome.Rejected, pbs.Offer(Entry(StageKey(), 1, v: RecordVerification.OnlineVerified)).Outcome);
        Assert.Equal(RecordUpdateOutcome.Rejected, pbs.Offer(Entry(StageKey(), 1, v: RecordVerification.PendingVerification)).Outcome);
        Assert.Equal(RecordUpdateOutcome.Rejected, pbs.Offer(Entry(StageKey(domain: ProgressionDomain.Online), 1)).Outcome);
        Assert.Equal(RecordUpdateOutcome.Rejected, pbs.Offer(Entry(StageKey(), 0)).Outcome); // not a valid time
        Assert.Throws<InvalidOperationException>(() => pbs.SetPending(Entry(StageKey(), 1)));
        Assert.Empty(pbs.Entries);
    }

    [Fact]
    public void DisplayStates_DistinguishNotAttempted_DidNotFinish_NoResult_Legacy_Pending()
    {
        var pbs = new PersonalBests();
        RecordKey key = StageKey();
        Assert.Equal(RecordDisplayState.NotAttempted, pbs.View(key).State);
        Assert.Equal("--:--.---", pbs.View(key).ValueText);

        pbs.RegisterAttempt(key, RunOutcome.DidNotFinish, false, TestContent.T0);
        Assert.Equal(RecordDisplayState.DidNotFinish, pbs.View(key).State);

        pbs.RegisterAttempt(key, RunOutcome.Finished, false, TestContent.T0);
        Assert.Equal(RecordDisplayState.NoResult, pbs.View(key).State);

        pbs.Offer(Entry(key, 222_815));
        RecordView view = pbs.View(key);
        Assert.Equal(RecordDisplayState.HasValue, view.State);
        Assert.Equal("03:42.815", view.ValueText);
        Assert.Equal("Local / Unverified", view.VerificationText);

        // Rules change: archive, never delete; the new key shows the legacy result as incompatible.
        int archived = pbs.Archive(e => e.Key.PhysicsVersion == "phys-3", "Physics v4 changed contact handling", TestContent.T0);
        Assert.Equal(1, archived);
        Assert.Single(pbs.Archived);
        Assert.Equal(RecordVerification.Legacy, pbs.Archived[0].Verification);
        Assert.Equal(RecordVerification.LocalUnverified, pbs.Archived[0].OriginalVerification);
        var v4 = new RecordRuleset { CourseRevision = "r1", PhysicsVersion = "phys-4", ScoringVersion = "score-2", Conditions = "night-dry" };
        RecordKey newKey = StageKey(rules: v4);
        RecordView legacy = pbs.View(newKey);
        Assert.Equal(RecordDisplayState.LegacyIncompatible, legacy.State);
        Assert.Single(legacy.Legacy);
        // Normal and Hard are different identities: a Normal result is never "legacy" for Hard.
        Assert.Equal(RecordDisplayState.NotAttempted, pbs.View(StageKey(CampaignMode.Hard, rules: v4)).State);

        var online = new PersonalBests { Domain = ProgressionDomain.Online };
        RecordKey onlineKey = StageKey(domain: ProgressionDomain.Online);
        online.SetPending(Entry(onlineKey, 199_000, v: RecordVerification.OnlineVerified));
        Assert.Equal(RecordDisplayState.PendingVerification, online.View(onlineKey).State);
        Assert.Equal("Pending verification", online.View(onlineKey).VerificationText);
    }
}
