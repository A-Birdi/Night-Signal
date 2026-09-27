namespace NightSignal.Core.Rules
{
    public enum CampaignMode { Normal = 0, Hard = 1 }

    public enum StageType { Regular = 0, Lieutenant = 1, Penultimate = 2, Finale = 3 }

    /// <summary>What an entrant's run counts as for settlement. Only the server assigns this.</summary>
    public enum RunOutcome
    {
        Finished = 0,
        /// <summary>Active, non-quitting driver still on course at the deadline.</summary>
        DidNotFinish = 1,
        Quit = 2,
        DisqualifiedDisconnect = 3,
        DisqualifiedAfk = 4,
        DisqualifiedInvalid = 5,
    }

    public enum EventKind
    {
        CampaignStage = 0,
        FreeplaySprint = 1,
        FreeplayCircuit = 2,
        FreeplayDriftAttack = 3,
        FreeplayTimeTrial = 4,
        FreeplayCustomCup = 5,
        Tutorial = 6,
    }

    public enum PerformanceClass { D = 0, C = 1, B = 2, A = 3, S = 4 }

    public enum ChallengeTier { Bronze = 0, Silver = 1, Gold = 2 }

    public enum ChallengeFamily { Precision = 0, Drift = 1, Racecraft = 2, Workshop = 3, Touring = 4 }
}
