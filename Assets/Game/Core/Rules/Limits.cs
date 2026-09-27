namespace NightSignal.Core.Rules
{
    /// <summary>
    /// Structural limits and timing constants from the specification. Structural values are binding;
    /// timing values marked "initial" are measured tuning targets (spec §4.4, §18) and may only change
    /// with a recorded reason in docs/DECISIONS.md.
    /// </summary>
    public static class Limits
    {
        // Capacities — binding (Addendum 01 D01/D02 supersedes the master's six-TOTAL entrant cap).
        // Separate named limits; never one overloaded "max players" value.
        public const int MaxConvoyHumans = 6;
        public const int MaxEventHumanEntrants = 6;
        /// <summary>Humans + friendly AI + opposing AI on track. Spectators and replays never count.</summary>
        public const int MaxRaceVehicles = 12;
        public const int MaxMeetHumans = 6;
        /// <summary>Authored meet bays (scenery); not an admission limit.</summary>
        public const int MeetBays = 12;
        public const int TeamTrialSideSize = 6;
        public const int MaxReplayOverlays = 3;
        public const int CampaignStages = 30;

        // Simulation and network rates (spec §6, §18) — initial targets.
        public const int SimulationHz = 60;
        public const int InputSendHz = 30;
        public const int SnapshotHz = 20;
        public const int RemoteInterpolationBufferMs = 100;
        public const int MaxRemoteExtrapolationMs = 150;

        // Connection lifecycle (spec §4.3, §4.4) — initial targets.
        public const int HeartbeatIntervalMs = 1000;
        public const int ConnectionWarningMs = 3000;
        public const int ConnectionLossMs = 8000;
        public const int InputStarvationCoastMs = 250;
        public const int LoadingTimeoutMs = 90_000;
        public const int LoadingExtensionMs = 30_000;
        public const int LeaderTransferMs = 15_000;
        public const int ReadyRequestCooldownMs = 15_000;
        public const int ProposalAwayAfterMs = 120_000;
        public const int FriendMeetReservationMs = 30_000;

        // Freeplay voting (Addendum D07).
        public const int BallotDefaultSeconds = 30;
        public static readonly int[] BallotSecondsChoices = { 15, 30, 45, 60 };

        // Team Trials (Addendum §3): a DNF/DQ contributes the hard timeout plus this penalty to the team mean.
        public const int TeamTrialFailurePenaltyMs = 30_000;

        // Race rules (spec §5.2, §6.1).
        public const int FirstFinishGraceMs = 90_000;
        public const int ResetPenaltyMs = 3000;
        public const int ResetGhostMaxMs = 2000;
        public const int WallImpactDebounceMs = 750;

        // Economy (spec §10) — binding caps.
        public const long WalletCap = 9_999_999;
        public const long MaxPerformancePartPrice = 200_000;
        public const long MaxCarPrice = 350_000;
        public const long MaxCosmeticPrice = 1_000_000;
        public const long StarterGrantCredits = 12_000;
        public const long TutorialCompletionCredits = 3_000;
    }
}
