using System.Collections.Generic;

namespace NightSignal.Core.Content
{
    // Typed content definitions loaded from Assets/Content/Data (generated + authored JSON).
    // Public fields keep the model simple for Newtonsoft and identical across Unity and the .NET services.

    public sealed class CourseDef
    {
        public string Id;
        public string Name;
        public string Region;
        /// <summary>tutorial | regular | finale</summary>
        public string Kind;
        /// <summary>training | sprint | circuit</summary>
        public string Format;
        public int Laps;
        public string FormatDescription;
        public double TargetLengthKm;
        public double TargetNetElevationM;
        /// <summary>Server-owned economy input; clamped 120–420 by the payout formula.</summary>
        public int ExpectedSeconds;
        public string SectorsBrief;
        public List<string> Landmarks = new List<string>();
        public string DefaultConditions;
        public string DesignNote;
    }

    public sealed class CarDef
    {
        public string Id;
        public string Name;
        public string Maker;
        public string Model;
        public string Body;
        /// <summary>front | mid | front-mid</summary>
        public string EngineLayout;
        /// <summary>RWD | FWD | AWD</summary>
        public string Drive;
        public int BasePI;
        public double MassKg;
        public double PowerKw;
        public double TorqueNm;
        public long Price;
        public bool Starter;
        public string Silhouette;
        public string Handling;
    }

    /// <summary>
    /// Authored engineering data per car model (Assets/Content/Data/authored/cars.tuning.json). Values are
    /// initial tuning targets; the stat harness measures the resulting behaviour.
    /// </summary>
    public sealed class CarTuningDef
    {
        public string Id;
        /// <summary>inline4 | six | triple | rotary</summary>
        public string EngineFamily;
        public bool Turbo;
        public double TurboLagSeconds;
        public double RedlineRpm;
        public int Gears;
        public double LengthM;
        public double WidthM;
        public double HeightM;
        public double WheelbaseM;
        public double TrackM;
        public double FrontWeight;
        public double TyreGrip;
        public double RearGripBias = 1.0;
        public double InertiaScale = 1.0;
        public double DragAreaCdA;
        public double LiftAreaClA;
        public double AwdFrontShare;
        public string Notes;
    }

    public sealed class CarTuningFile { public string Schema; public List<CarTuningDef> Cars = new List<CarTuningDef>(); }

    public sealed class CrewDef
    {
        public string Id;
        public string Name;
        public string Region;
        public string Identity;
    }

    public sealed class TendencyDef
    {
        public string Id;
        public string Name;
    }

    public sealed class RivalDef
    {
        public string Id;
        public string Name;
        public int Age;
        public string Pronouns;
        public string Crew;
        /// <summary>crew | lieutenant | normal-final | hard-final</summary>
        public string Role;
        public string Appearance;
        public string Personality;
        public string PrimaryCar;
        public string Tendency;
        public string Strength;
        public string Weakness;
        public string IntroSample;
    }

    public sealed class StageSide
    {
        public string Lead;
        public List<string> Support = new List<string>();
        public string StoryBeat;
        public string Variation;
        public string SupportDerivation;
        /// <summary>
        /// Live opponents for this stage side, featured first (authored/stages.opposition.json, Addendum 01 §1.2).
        /// Humans never displace them; the support pool above is authoring reference only.
        /// </summary>
        public List<string> Opponents = new List<string>();
    }

    public sealed class StageDef
    {
        public string Id;
        public int Number;
        /// <summary>regular | lieutenant | penultimate | finale</summary>
        public string Type;
        public string Course;
        public int Act;
        public int MaxPI;
        public StageSide Normal = new StageSide();
        public StageSide Hard = new StageSide();
    }

    public sealed class ChallengeDef
    {
        public string Id;
        /// <summary>precision | drift | racecraft | workshop | touring</summary>
        public string Family;
        /// <summary>bronze | silver | gold</summary>
        public string Tier;
        public string Name;
        public string PredicateText;
        public string Reward;
        public int RankPoints;
        public long Cash;
    }

    public sealed class CosmeticDef
    {
        public string Id;
        public string Name;
        /// <summary>decal | paint | driver_clothing | card_customization | accessory_or_avatar</summary>
        public string Category;
        public string Source;
    }

    // File envelopes (header fields are informational; the loader checks the schema string).
    public sealed class StageOppositionEntry
    {
        public string Id;
        public List<string> Normal = new List<string>();
        public List<string> Hard = new List<string>();
    }

    public sealed class StageOppositionFile { public string Schema; public string Rules; public List<StageOppositionEntry> Stages = new List<StageOppositionEntry>(); }

    /// <summary>Certified stage benchmarks (authored/stage-benchmarks.json), produced by the benchmark certification run.</summary>
    public sealed class StageBenchmarksFile
    {
        public string Schema;
        /// <summary>How the file was produced (driver, physics/scoring versions, date) — shown with the targets.</summary>
        public string Method;
        public List<CertifiedBenchmark> Stages = new List<CertifiedBenchmark>();
    }

    /// <summary>
    /// One stage side's certified benchmark (spec: reference run P in a freely available class-legal car; Normal targets
    /// ~1.18×P early → ~1.05×P in the final act; frozen milliseconds). The featured rival's driving pace is calibrated to
    /// finish near the target, so the encounter is beatable by a driver who meets it.
    /// </summary>
    public sealed class CertifiedBenchmark
    {
        public string Stage;
        /// <summary>"normal" or "hard".</summary>
        public string Mode;
        /// <summary>P: the reference time (ms) and the car/build that set it.</summary>
        public long ReferenceMs;
        public string ReferenceCar;
        public string ReferenceBuild;
        public double Factor;
        public long TargetMs;
        /// <summary>Speed-plan scale for the featured rival's driver (1 = its stage profile unchanged).</summary>
        public double FeaturedRivalPace = 1.0;
        /// <summary>The featured rival's calibrated solo time at that pace (ms).</summary>
        public long FeaturedRivalMs;
        /// <summary>S29 "Four Signals": the published contract targets (null on every other stage).</summary>
        public FourSignalsTargets Contracts;
    }

    /// <summary>Published S29 contract targets (spec "Four Signals judging contract"), frozen by the certification run.</summary>
    public sealed class FourSignalsTargets
    {
        /// <summary>Entry: the sector time from the start to the Arc (ms).</summary>
        public long EntrySectorMs;
        /// <summary>Arc: raw drift banked across the three marked corners.</summary>
        public long ArcDriftRaw;
        /// <summary>Descent: the speed window at the end of the brake-release zone (km/h).</summary>
        public float BrakeExitMinKmh, BrakeExitMaxKmh;
        /// <summary>Descent: the published release point — the brake must be off by this route distance (m).</summary>
        public float BrakeReleaseByMetres;
        /// <summary>Horizon: minimum speed at each exit-speed point, in route order (km/h).</summary>
        public List<float> HorizonExitKmh = new List<float>();
    }

    public sealed class RewardOnlyCourse { public string Course; public string Stage; public string Mode; }

    public sealed class PurchaseOnlyCourse { public string Course; public long Price; }

    /// <summary>Course-access table (Addendum 01 §5.1); the regular-stage unlock mapping is derived from the stages.</summary>
    public sealed class CourseAccessRules
    {
        public List<string> StarterCourses = new List<string>();
        public long CampaignCoursePrice;
        /// <summary>Inclusive first/last course IDs that may be bought early or unlocked by their regular Normal stage.</summary>
        public List<string> CampaignCourseRange = new List<string>();
        public List<RewardOnlyCourse> RewardOnly = new List<RewardOnlyCourse>();
        public List<PurchaseOnlyCourse> PurchaseOnly = new List<PurchaseOnlyCourse>();
        public string PriceBasis;
    }

    public sealed class CoursesAddendumFile { public string Schema; public List<CourseDef> Courses = new List<CourseDef>(); public CourseAccessRules Access = new CourseAccessRules(); }

    public sealed class CoursesFile { public string Schema; public string SourceSha256; public List<CourseDef> Courses = new List<CourseDef>(); }
    public sealed class CarsFile { public string Schema; public string SourceSha256; public List<CarDef> Cars = new List<CarDef>(); }
    public sealed class CrewsFile { public string Schema; public string SourceSha256; public List<CrewDef> Crews = new List<CrewDef>(); public List<TendencyDef> Tendencies = new List<TendencyDef>(); }
    public sealed class RivalsFile { public string Schema; public string SourceSha256; public List<RivalDef> Rivals = new List<RivalDef>(); }
    public sealed class StagesFile { public string Schema; public string SourceSha256; public List<StageDef> Stages = new List<StageDef>(); }
    public sealed class ChallengesFile { public string Schema; public string SourceSha256; public List<ChallengeDef> Challenges = new List<ChallengeDef>(); }
    public sealed class CosmeticsFile { public string Schema; public string SourceSha256; public List<CosmeticDef> Cosmetics = new List<CosmeticDef>(); }
}
