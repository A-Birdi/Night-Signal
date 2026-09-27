using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using Newtonsoft.Json;

namespace NightSignal.Core.Builds
{
    // Authored favorite-car upgrade paths (Assets/Content/Data/authored/build-recipes.json, Addendum 02 §8, D206).
    // INITIAL AUTHORING: demands and builds are targets to be calibrated by real driving in Unity; nothing here is measured.

    /// <summary>A preparation band: a run of stages with one class cap and an authored target PI (demand).</summary>
    public sealed class ProgressionBand
    {
        public string Id;
        /// <summary>normal | hard</summary>
        public string Mode;
        public string FirstStage;
        public string LastStage;
        /// <summary>Initial authored PI a representative player's build should reach for these stages (estimate scale).</summary>
        public int DemandPi;
        public string Note;
    }

    /// <summary>Parameters of the deterministic affordability model (ordinary income, first clears, losses, optional spending).</summary>
    public sealed class EconomyModelDef
    {
        public long StartingCredits;
        /// <summary>Placements of successful (qualifying) attempts, used cyclically.</summary>
        public List<int> PlacementCycle = new List<int>();
        /// <summary>Placement of a non-qualifying (lost) but legally finished attempt.</summary>
        public int LostAttemptPlacement = 6;
        /// <summary>Every Nth attempt is clean (1.05).</summary>
        public int CleanEvery = 3;
        /// <summary>Failed attempts before each stage's clear, cycled per stage.</summary>
        public List<int> NormalFailuresPerStage = new List<int>();
        public List<int> HardFailuresPerStage = new List<int>();
        /// <summary>Share of every payout reserved for optional cosmetics/course purchases (not available for parts).</summary>
        public int DiscretionaryPercent;
        /// <summary>Challenge cash is optional; the model counts none by default (conservative).</summary>
        public bool CountChallengeCash;
        public string Note;
    }

    public sealed class RecipeStep
    {
        public string Id;
        public string Label;
        /// <summary>main | incremental | longTerm</summary>
        public string Kind = "main";
        /// <summary>Band ids this build is the recommended loadout for.</summary>
        public List<string> For = new List<string>();
        /// <summary>Must be purchasable and applied before this stage, e.g. "N:S04" or "H:S08".</summary>
        public string By;
        /// <summary>Full list of installed part ids (slots derived from the catalogue).</summary>
        public List<string> Parts = new List<string>();
        public string Utility;
        public SortedDictionary<string, int> Tuning = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public string Note;
    }

    public sealed class CarRecipe
    {
        public string Car;
        /// <summary>starter | purchase</summary>
        public string Role;
        /// <summary>Stage by which the car itself is bought (non-starters), e.g. "N:S08".</summary>
        public string BuyBy;
        /// <summary>Bands the car cannot enter at any legal build (stock PI above the cap): use another owned car or a loaner.</summary>
        public List<string> CapExcludedBands = new List<string>();
        public List<RecipeStep> Path = new List<RecipeStep>();
        public List<RecipeStep> Alternatives = new List<RecipeStep>();
        public string Identity;
    }

    public sealed class RecipeFile
    {
        public string Schema;
        public int Revision;
        public string Note;
        public EconomyModelDef Economy = new EconomyModelDef();
        public List<ProgressionBand> Bands = new List<ProgressionBand>();
        public List<CarRecipe> Cars = new List<CarRecipe>();
    }

    /// <summary>Parsed "N:S04" / "H:S08".</summary>
    public struct StageRef : IComparable<StageRef>
    {
        public CampaignMode Mode;
        public int Stage;

        public static StageRef Parse(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 5 || text[1] != ':' || (text[0] != 'N' && text[0] != 'H') || text[2] != 'S' ||
                !int.TryParse(text.Substring(3), NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 1 || n > Limits.CampaignStages)
                throw new BuildDataException($"Bad stage reference {text} (expected N:Sxx or H:Sxx)");
            return new StageRef { Mode = text[0] == 'H' ? CampaignMode.Hard : CampaignMode.Normal, Stage = n };
        }

        /// <summary>Campaign order: all Normal stages, then all Hard stages (Hard requires the Normal finale).</summary>
        public int Ordinal => (Mode == CampaignMode.Hard ? Limits.CampaignStages : 0) + Stage;

        public int CompareTo(StageRef other) => Ordinal.CompareTo(other.Ordinal);

        public override string ToString() => (Mode == CampaignMode.Hard ? "H:S" : "N:S") + Stage.ToString("00", CultureInfo.InvariantCulture);
    }

    public sealed class RecipeBook
    {
        public const string SchemaId = "night-signal/build-recipes@1";

        public RecipeFile File { get; private set; }

        public static RecipeBook Load(string json)
        {
            RecipeFile f;
            try
            {
                f = JsonConvert.DeserializeObject<RecipeFile>(json, new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error });
            }
            catch (JsonException e)
            {
                throw new BuildDataException("build-recipes.json: " + e.Message);
            }
            if (f == null || f.Schema != SchemaId) throw new BuildDataException($"build-recipes.json: expected schema {SchemaId}");
            foreach (CarRecipe c in f.Cars)
            {
                if (c.BuyBy != null) StageRef.Parse(c.BuyBy);
                foreach (RecipeStep s in c.Path.Concat(c.Alternatives)) StageRef.Parse(s.By);
            }
            return new RecipeBook { File = f };
        }

        public ProgressionBand Band(string id) => File.Bands.First(b => b.Id == id);

        public CarRecipe Car(string id) => File.Cars.FirstOrDefault(c => c.Car == id);

        /// <summary>Class cap of a band (the stages' MaxPI; all stages of a band share it — checked by tests).</summary>
        public static int CapOf(ProgressionBand band, ContentCatalogue content) => content.Stage(band.FirstStage).MaxPI;

        public static StageRef FirstStageOf(ProgressionBand band) =>
            StageRef.Parse((band.Mode == "hard" ? "H:" : "N:") + band.FirstStage);

        /// <summary>Recipe step → snapshot (slot of each part from the catalogue). Throws on unknown parts or two parts per slot.</summary>
        public static MechanicalSnapshot ToSnapshot(RecipeStep step, PartsCatalogue parts)
        {
            var s = new MechanicalSnapshot();
            foreach (string id in step.Parts)
            {
                PartDef p = parts.TryPart(id, out PartDef found) ? found : throw new BuildDataException($"Recipe {step.Id}: unknown part {id}");
                string slot = PartSlots.Id(p.SlotValue);
                if (p.SlotValue == PartSlot.Utility) throw new BuildDataException($"Recipe {step.Id}: utility {id} belongs in 'utility'");
                if (s.Parts.ContainsKey(slot)) throw new BuildDataException($"Recipe {step.Id}: two parts in {slot}");
                s.Parts[slot] = id;
            }
            s.UtilityPartId = string.IsNullOrEmpty(step.Utility) ? null : step.Utility;
            foreach (var kv in step.Tuning ?? new SortedDictionary<string, int>()) s.Tuning.Values[kv.Key] = kv.Value;
            return s;
        }
    }

    /// <summary>Shop availability by act (spec §10: "unlock shop availability primarily by act"), never by spending.</summary>
    public static class ShopAvailability
    {
        /// <summary>Act of the player's Normal frontier stage (1-based stage number; 31 = Normal complete → act 4).</summary>
        public static int ActForNormalFrontier(int frontierStage, IReadOnlyList<StageDef> stages)
        {
            if (frontierStage > Limits.CampaignStages) return 4;
            StageDef s = stages.FirstOrDefault(x => x.Number == Math.Max(1, frontierStage));
            return s?.Act ?? 1;
        }
    }
}
