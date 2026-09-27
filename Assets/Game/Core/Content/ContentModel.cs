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
    public sealed class CoursesFile { public string Schema; public string SourceSha256; public List<CourseDef> Courses = new List<CourseDef>(); }
    public sealed class CarsFile { public string Schema; public string SourceSha256; public List<CarDef> Cars = new List<CarDef>(); }
    public sealed class CrewsFile { public string Schema; public string SourceSha256; public List<CrewDef> Crews = new List<CrewDef>(); public List<TendencyDef> Tendencies = new List<TendencyDef>(); }
    public sealed class RivalsFile { public string Schema; public string SourceSha256; public List<RivalDef> Rivals = new List<RivalDef>(); }
    public sealed class StagesFile { public string Schema; public string SourceSha256; public List<StageDef> Stages = new List<StageDef>(); }
    public sealed class ChallengesFile { public string Schema; public string SourceSha256; public List<ChallengeDef> Challenges = new List<ChallengeDef>(); }
    public sealed class CosmeticsFile { public string Schema; public string SourceSha256; public List<CosmeticDef> Cosmetics = new List<CosmeticDef>(); }
}
