namespace NightSignal.Race
{
    /// <summary>Entrant lifecycle within one event (shared by the dedicated server, clients and offline races).</summary>
    public enum EntrantStatus : byte { Reserved = 0, Loading = 1, Loaded = 2, Racing = 3, Finished = 4, Dnf = 5, DqDisconnected = 6, DqQuit = 7, Spectator = 8 }

    /// <summary>Event phase (broadcast on the reliable channel online).</summary>
    public enum MatchPhase : byte { WaitingForEntrants = 0, Loading = 1, Countdown = 2, Racing = 3, Results = 4, Aborted = 5 }

    /// <summary>Public roster row for one car in an event: what every client needs to spawn and label it.</summary>
    public sealed class RosterEntry
    {
        public int Index;
        public string EntrantId;
        public string DisplayName;
        public bool Human;
        public string CarId;
        public int GridSlot;
        public float[] Paint;
        /// <summary>player | opposing (Team Trials use player-side friendly AI).</summary>
        public string Team = "player";
        /// <summary>driver | featured | support | friendly | opponent</summary>
        public string Role = "driver";
        /// <summary>A human's frozen applied build (null = the model's stock car) and its Core BuildHash.</summary>
        public Core.Builds.MechanicalSnapshot Build;
        public string BuildHash = "";
        /// <summary>A human's applied livery in the compact wire form ("" = the palette colour). Visual only.</summary>
        public string Livery = "";
    }
}
