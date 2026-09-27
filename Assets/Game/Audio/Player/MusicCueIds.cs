namespace NightSignal.AudioSynth
{
    /// <summary>
    /// Stable music cue IDs (Addendum 01 §11 inventory). Each ID is the "id" of one score in
    /// Assets/Content/Audio/Scores (file name = lower-case ID + ".json") and one entry in
    /// Assets/Content/Data/authored/music.cues.json, which also lists allowed contexts and loop points.
    /// </summary>
    public static class MusicCueIds
    {
        public const string Title = "MUS_TITLE";
        public const string MenuA = "MUS_MENU_A";
        public const string MenuB = "MUS_MENU_B";
        public const string Garage = "MUS_GARAGE";
        public const string Meet = "MUS_MEET";
        public const string Tutorial = "MUS_TUTORIAL";
        public const string ResultsWin = "MUS_RESULTS_WIN";
        public const string ResultsLoss = "MUS_RESULTS_LOSS";
        public const string RaceMizuhana = "MUS_RACE_MIZUHANA";
        public const string RaceKasumi = "MUS_RACE_KASUMI";
        public const string RaceKurogawa = "MUS_RACE_KUROGAWA";
        public const string RaceAkebono = "MUS_RACE_AKEBONO";
        public const string RaceHoshimi = "MUS_RACE_HOSHIMI";
        public const string RaceTsukishiro = "MUS_RACE_TSUKISHIRO";
        public const string LieutenantDaigo = "MUS_LT_DAIGO";
        public const string LieutenantEmi = "MUS_LT_EMI";
        public const string LieutenantJun = "MUS_LT_JUN";
        public const string LieutenantMako = "MUS_LT_MAKO";
        public const string Penultimate = "MUS_PENULTIMATE";
        public const string FinalReina = "MUS_FINAL_REINA";
        public const string FinalShiori = "MUS_FINAL_SHIORI";
        public const string TrialMean = "MUS_TT_MEAN";
        public const string TrialBest = "MUS_TT_BEST";
        public const string TrialDrift = "MUS_TT_DRIFT";

        public static readonly string[] All =
        {
            Title, MenuA, MenuB, Garage, Meet, Tutorial, ResultsWin, ResultsLoss,
            RaceMizuhana, RaceKasumi, RaceKurogawa, RaceAkebono, RaceHoshimi, RaceTsukishiro,
            LieutenantDaigo, LieutenantEmi, LieutenantJun, LieutenantMako,
            Penultimate, FinalReina, FinalShiori, TrialMean, TrialBest, TrialDrift,
        };
    }
}
