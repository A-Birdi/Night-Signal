using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using Newtonsoft.Json;

namespace NightSignal.Core.Builds
{
    // Authored parts catalogue (Assets/Content/Data/authored/parts.json). Public fields keep the Newtonsoft model simple
    // and identical in Unity and the .NET services, like Core.Content.

    public sealed class PartEffect
    {
        /// <summary>A <see cref="SimParam"/> name.</summary>
        public string Param;
        /// <summary>scale | add | set | atLeast | chassisWeightReduction</summary>
        public string Op;
        public double Value;

        [JsonIgnore] public SimParam ParsedParam;
        [JsonIgnore] public ModifierOp ParsedOp;
    }

    /// <summary>
    /// One tuning value → simulation input mapping. The tuning integer v in [Min, Max] is mapped linearly to
    /// AtMin..AtMax and applied with <see cref="Op"/> (set | add | scale) after all part effects.
    /// </summary>
    public sealed class TuningTarget
    {
        public string Param;
        public string Op;
        public double AtMin;
        public double AtMax;

        [JsonIgnore] public SimParam ParsedParam;
        [JsonIgnore] public ModifierOp ParsedOp;
    }

    /// <summary>A tuning control a part makes adjustable, with its bounds and default for this part.</summary>
    public sealed class PartTuningControl
    {
        public string Key;
        public int Min;
        public int Max;
        public int Step = 1;
        public int Default;
        /// <summary>Default is the car's stock value of the (single) set-target parameter, in the control's units.</summary>
        public bool DefaultFromBase;
        public string Unit;
        public List<TuningTarget> Targets = new List<TuningTarget>();
    }

    /// <summary>Compatibility rule. Empty lists mean "any".</summary>
    public sealed class PartCompatibility
    {
        /// <summary>RWD | FWD | AWD (CarDef.Drive).</summary>
        public List<string> Drive = new List<string>();
        /// <summary>front | mid | front-mid (CarDef.EngineLayout).</summary>
        public List<string> EngineLayout = new List<string>();
        /// <summary>inline4 | six | triple | rotary (CarTuningDef.EngineFamily).</summary>
        public List<string> EngineFamily = new List<string>();
        /// <summary>Chassis body class from parts.json chassis table.</summary>
        public List<string> BodyClass = new List<string>();
        /// <summary>any | na | turbo — the car's FACTORY aspiration.</summary>
        public string Aspiration = "any";
        /// <summary>Allowed factory gear counts.</summary>
        public List<int> StockGears = new List<int>();
        public List<string> Cars = new List<string>();
        public List<string> ExcludeCars = new List<string>();
    }

    public sealed class UtilityDef
    {
        /// <summary>income | showcase</summary>
        public string Kind;
        public int Percent;
    }

    public sealed class PartDef
    {
        /// <summary>Stable id (never reused for a different part).</summary>
        public string Id;
        /// <summary>Slot id (see <see cref="PartSlots.Id"/>).</summary>
        public string Slot;
        public int Tier;
        public string Name;
        public long Price;
        /// <summary>Shop availability by act (1–4). Never unlocked by spending.</summary>
        public int UnlockAct = 1;
        /// <summary>No longer sold; owners keep using it.</summary>
        public bool Retired;
        public PartCompatibility Compat = new PartCompatibility();
        public List<PartEffect> Effects = new List<PartEffect>();
        public List<PartTuningControl> Tuning = new List<PartTuningControl>();
        /// <summary>Performance-relevant appearance component id (aero/body kit), or null.</summary>
        public string Appearance;
        public UtilityDef Utility;
        /// <summary>Plain-language tradeoff shown in the Garage.</summary>
        public string Tradeoff;

        [JsonIgnore] public PartSlot SlotValue;
    }

    public sealed class ChassisDef
    {
        public string Car;
        /// <summary>hatch | coupe | sedan | wagon | roadster | mid</summary>
        public string BodyClass;
        /// <summary>Upper bound on mass removable by weight-reduction/body parts combined.</summary>
        public double WeightReductionMaxKg;
        /// <summary>Explicit per-car exclusions (packaging), applied after the generic rules.</summary>
        public List<string> ExcludeParts = new List<string>();
        public string Note;
    }

    public sealed class PartsFile
    {
        public string Schema;
        public int Revision;
        public int PriceRevision;
        public string Note;
        public List<ChassisDef> Chassis = new List<ChassisDef>();
        public List<PartDef> Parts = new List<PartDef>();
    }

    public sealed class CompatibilityResult
    {
        public bool Compatible;
        public string Reason = "";

        public static readonly CompatibilityResult Ok = new CompatibilityResult { Compatible = true };

        public static CompatibilityResult No(string reason) => new CompatibilityResult { Compatible = false, Reason = reason };
    }

    /// <summary>The trusted, validated parts catalogue shared by the Unity client, the Unity server and the control plane.</summary>
    public sealed class PartsCatalogue
    {
        public const string SchemaId = "night-signal/parts@1";
        public static readonly string[] BodyClasses = { "hatch", "coupe", "sedan", "wagon", "roadster", "mid" };

        public int Revision { get; private set; }
        /// <summary>Quotes pin this; any price edit must bump it.</summary>
        public int PriceRevision { get; private set; }
        /// <summary>SHA-256 of the LF-normalised document text.</summary>
        public string Hash { get; private set; }
        public IReadOnlyList<PartDef> Parts { get; private set; }
        public IReadOnlyList<ChassisDef> Chassis { get; private set; }

        Dictionary<string, PartDef> partById;
        Dictionary<string, ChassisDef> chassisByCar;

        public PartDef Part(string id)
        {
            if (!TryPart(id, out PartDef p)) throw new KeyNotFoundException($"Unknown part {id}");
            return p;
        }

        public bool TryPart(string id, out PartDef part) => partById.TryGetValue(id ?? "", out part);

        public bool TryChassis(string carId, out ChassisDef chassis) => chassisByCar.TryGetValue(carId ?? "", out chassis);

        public static (long Min, long Max) TierPriceRange(int tier)
        {
            // Spec §10 initial mechanical price ranges.
            switch (tier)
            {
                case 1: return (6_000, 18_000);
                case 2: return (24_000, 55_000);
                case 3: return (70_000, 120_000);
                case 4: return (150_000, 200_000);
                default: throw new ArgumentOutOfRangeException(nameof(tier));
            }
        }

        public static PartsCatalogue Load(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new BuildDataException("parts.json is empty");
            var settings = new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error };
            PartsFile file;
            try
            {
                file = JsonConvert.DeserializeObject<PartsFile>(json, settings);
            }
            catch (JsonException e)
            {
                throw new BuildDataException("parts.json: " + e.Message);
            }
            if (file == null) throw new BuildDataException("parts.json is empty");
            if (file.Schema != SchemaId) throw new BuildDataException($"parts.json: expected schema {SchemaId}, found {file.Schema ?? "none"}");
            if (file.Revision < 1 || file.PriceRevision < 1) throw new BuildDataException("parts.json: revision and priceRevision must be ≥ 1");

            var errors = new List<string>();
            var byId = new Dictionary<string, PartDef>(StringComparer.Ordinal);
            foreach (PartDef p in file.Parts ?? new List<PartDef>())
            {
                if (p == null || string.IsNullOrEmpty(p.Id)) { errors.Add("A part has no id"); continue; }
                if (byId.ContainsKey(p.Id)) { errors.Add($"Duplicate part id {p.Id}"); continue; }
                byId.Add(p.Id, p);
                ValidatePart(p, errors);
            }

            var chassis = new Dictionary<string, ChassisDef>(StringComparer.Ordinal);
            foreach (ChassisDef c in file.Chassis ?? new List<ChassisDef>())
            {
                if (c == null || string.IsNullOrEmpty(c.Car)) { errors.Add("A chassis entry has no car id"); continue; }
                if (chassis.ContainsKey(c.Car)) { errors.Add($"Duplicate chassis entry {c.Car}"); continue; }
                if (!BodyClasses.Contains(c.BodyClass)) errors.Add($"Chassis {c.Car}: unknown body class {c.BodyClass}");
                if (c.WeightReductionMaxKg < 0 || c.WeightReductionMaxKg > 200) errors.Add($"Chassis {c.Car}: weight-reduction allowance out of range");
                c.ExcludeParts = c.ExcludeParts ?? new List<string>();
                foreach (string ex in c.ExcludeParts)
                    if (!byId.ContainsKey(ex ?? "")) errors.Add($"Chassis {c.Car}: excludes unknown part {ex}");
                chassis.Add(c.Car, c);
            }

            if (errors.Count > 0) throw new BuildDataException("parts.json invalid:\n - " + string.Join("\n - ", errors));

            return new PartsCatalogue
            {
                Revision = file.Revision,
                PriceRevision = file.PriceRevision,
                Hash = Sha256(json.Replace("\r\n", "\n")),
                Parts = file.Parts,
                Chassis = file.Chassis,
                partById = byId,
                chassisByCar = chassis,
            };
        }

        static void ValidatePart(PartDef p, List<string> errors)
        {
            string where = "Part " + p.Id;
            if (!PartSlots.TryParse(p.Slot, out PartSlot slot)) { errors.Add($"{where}: unknown slot {p.Slot}"); return; }
            p.SlotValue = slot;
            if (string.IsNullOrWhiteSpace(p.Name)) errors.Add($"{where}: no name");
            if (string.IsNullOrWhiteSpace(p.Tradeoff)) errors.Add($"{where}: every part states its tradeoff");
            if (p.Tier < 1 || p.Tier > 4) errors.Add($"{where}: tier must be 1–4");
            else
            {
                var (min, max) = TierPriceRange(p.Tier);
                if (p.Price < min || p.Price > max) errors.Add($"{where}: price {p.Price} outside T{p.Tier} range {min}–{max}");
            }
            if (p.Price > Limits.MaxPerformancePartPrice) errors.Add($"{where}: price above the {Limits.MaxPerformancePartPrice} part cap");
            if (p.UnlockAct < 1 || p.UnlockAct > 4) errors.Add($"{where}: unlockAct must be 1–4");
            p.Compat = p.Compat ?? new PartCompatibility();
            p.Effects = p.Effects ?? new List<PartEffect>();
            p.Tuning = p.Tuning ?? new List<PartTuningControl>();
            ValidateCompat(p.Compat, where, errors);

            if (slot == PartSlot.Utility)
            {
                if (p.Utility == null) errors.Add($"{where}: utility parts declare a utility effect");
                else if (!(p.Utility.Kind == "income" && (p.Utility.Percent == 4 || p.Utility.Percent == 8)) &&
                         !(p.Utility.Kind == "showcase" && (p.Utility.Percent == 5 || p.Utility.Percent == 10)))
                    errors.Add($"{where}: utility must be income 4/8 or showcase 5/10 (spec §9)");
                if (p.Effects.Count > 0 || p.Tuning.Count > 0) errors.Add($"{where}: utility parts never change simulation inputs");
                return;
            }
            if (p.Utility != null) errors.Add($"{where}: only the utility slot carries a utility effect");
            if (p.Effects.Count == 0 && p.Tuning.Count == 0) errors.Add($"{where}: a mechanical part must change an implemented parameter");

            foreach (PartEffect e in p.Effects)
            {
                if (e == null || !SimParams.TryParse(e.Param, out SimParam sp)) { errors.Add($"{where}: unknown parameter {e?.Param}"); continue; }
                if (!ModifierOps.TryParse(e.Op, out ModifierOp op)) { errors.Add($"{where}: unknown op {e.Op}"); continue; }
                e.ParsedParam = sp;
                e.ParsedOp = op;
                if (op == ModifierOp.Scale && (e.Value <= 0.25 || e.Value >= 2.0)) errors.Add($"{where}: scale {e.Value} on {sp} is implausible");
                if (op == ModifierOp.ChassisWeightReduction && (sp != SimParam.MassKg || e.Value <= 0 || e.Value > 1))
                    errors.Add($"{where}: chassisWeightReduction applies to MassKg with a fraction in (0,1]");
            }

            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (PartTuningControl t in p.Tuning)
            {
                if (t == null || !TuningKeys.TrySlotOf(t.Key, out PartSlot owner)) { errors.Add($"{where}: unknown tuning key {t?.Key}"); continue; }
                if (owner != slot) errors.Add($"{where}: tuning key {t.Key} belongs to the {PartSlots.Id(owner)} slot");
                if (!keys.Add(t.Key)) errors.Add($"{where}: duplicate tuning key {t.Key}");
                if (t.Min >= t.Max) errors.Add($"{where}/{t.Key}: min must be below max");
                if (t.Step < 1 || (t.Max - t.Min) % t.Step != 0) errors.Add($"{where}/{t.Key}: step must divide the range");
                if (!t.DefaultFromBase && (t.Default < t.Min || t.Default > t.Max || (t.Default - t.Min) % t.Step != 0))
                    errors.Add($"{where}/{t.Key}: default must be an on-step value in range");
                t.Targets = t.Targets ?? new List<TuningTarget>();
                if (t.Targets.Count == 0) errors.Add($"{where}/{t.Key}: a tuning control must drive at least one parameter");
                if (t.DefaultFromBase && t.Targets.Count != 1) errors.Add($"{where}/{t.Key}: defaultFromBase needs exactly one target");
                foreach (TuningTarget tt in t.Targets)
                {
                    if (tt == null || !SimParams.TryParse(tt.Param, out SimParam sp)) { errors.Add($"{where}/{t.Key}: unknown parameter {tt?.Param}"); continue; }
                    if (!ModifierOps.TryParse(tt.Op, out ModifierOp op) || (op != ModifierOp.Set && op != ModifierOp.Add && op != ModifierOp.Scale))
                    {
                        errors.Add($"{where}/{t.Key}: tuning op must be set, add or scale");
                        continue;
                    }
                    if (t.DefaultFromBase && op != ModifierOp.Set) errors.Add($"{where}/{t.Key}: defaultFromBase needs a set target");
                    tt.ParsedParam = sp;
                    tt.ParsedOp = op;
                }
            }
        }

        static void ValidateCompat(PartCompatibility c, string where, List<string> errors)
        {
            c.Drive = c.Drive ?? new List<string>();
            c.EngineLayout = c.EngineLayout ?? new List<string>();
            c.EngineFamily = c.EngineFamily ?? new List<string>();
            c.BodyClass = c.BodyClass ?? new List<string>();
            c.StockGears = c.StockGears ?? new List<int>();
            c.Cars = c.Cars ?? new List<string>();
            c.ExcludeCars = c.ExcludeCars ?? new List<string>();
            c.Aspiration = c.Aspiration ?? "any";
            foreach (string d in c.Drive) if (d != "RWD" && d != "FWD" && d != "AWD") errors.Add($"{where}: unknown drive {d}");
            foreach (string l in c.EngineLayout) if (l != "front" && l != "mid" && l != "front-mid") errors.Add($"{where}: unknown layout {l}");
            foreach (string f in c.EngineFamily) if (f != "inline4" && f != "six" && f != "triple" && f != "rotary") errors.Add($"{where}: unknown engine family {f}");
            foreach (string b in c.BodyClass) if (!BodyClasses.Contains(b)) errors.Add($"{where}: unknown body class {b}");
            if (c.Aspiration != "any" && c.Aspiration != "na" && c.Aspiration != "turbo") errors.Add($"{where}: aspiration must be any|na|turbo");
        }

        /// <summary>
        /// Cross-checks the catalogue against the content catalogue: a chassis entry for every car and no rule naming an
        /// unknown car. Returns human-readable problems (empty = consistent).
        /// </summary>
        public IReadOnlyList<string> ValidateAgainst(ContentCatalogue content)
        {
            var e = new List<string>();
            foreach (CarDef car in content.Cars)
            {
                if (!chassisByCar.ContainsKey(car.Id)) e.Add($"No chassis entry for {car.Id}");
                if (!content.CarTunings.ContainsKey(car.Id)) e.Add($"No car tuning for {car.Id}");
            }
            foreach (ChassisDef c in Chassis)
                if (!content.TryCar(c.Car, out _)) e.Add($"Chassis entry for unknown car {c.Car}");
            foreach (PartDef p in Parts)
                foreach (string id in p.Compat.Cars.Concat(p.Compat.ExcludeCars))
                    if (!content.TryCar(id, out _)) e.Add($"Part {p.Id} names unknown car {id}");
            return e;
        }

        /// <summary>
        /// Explicit per-car compatibility resolution: generic rules (drive, layout, engine family, body class, factory
        /// aspiration, factory gear count, car lists) then the car's own chassis exclusions. The reason names the rule.
        /// </summary>
        public CompatibilityResult CheckCompatibility(PartDef part, CarDef car, CarTuningDef tuning)
        {
            if (part == null) throw new ArgumentNullException(nameof(part));
            if (car == null) throw new ArgumentNullException(nameof(car));
            if (tuning == null) throw new ArgumentNullException(nameof(tuning));
            if (!TryChassis(car.Id, out ChassisDef chassis)) return CompatibilityResult.No($"No chassis data for {car.Id}");
            PartCompatibility c = part.Compat;
            if (c.Cars.Count > 0 && !c.Cars.Contains(car.Id)) return CompatibilityResult.No($"Made for {string.Join(", ", c.Cars)} only");
            if (c.ExcludeCars.Contains(car.Id)) return CompatibilityResult.No($"Not made for {car.Id}");
            if (c.Drive.Count > 0 && !c.Drive.Contains(car.Drive)) return CompatibilityResult.No($"Requires {string.Join("/", c.Drive)} (car is {car.Drive})");
            if (c.EngineLayout.Count > 0 && !c.EngineLayout.Contains(car.EngineLayout))
                return CompatibilityResult.No($"Requires a {string.Join("/", c.EngineLayout)} engine layout (car is {car.EngineLayout})");
            if (c.EngineFamily.Count > 0 && !c.EngineFamily.Contains(tuning.EngineFamily))
                return CompatibilityResult.No($"Fits {string.Join("/", c.EngineFamily)} engines (car has {tuning.EngineFamily})");
            if (c.BodyClass.Count > 0 && !c.BodyClass.Contains(chassis.BodyClass))
                return CompatibilityResult.No($"Fits {string.Join("/", c.BodyClass)} bodies (car is a {chassis.BodyClass})");
            if (c.Aspiration == "na" && tuning.Turbo) return CompatibilityResult.No("For naturally aspirated engines only");
            if (c.Aspiration == "turbo" && !tuning.Turbo) return CompatibilityResult.No("For factory-turbo engines only");
            if (c.StockGears.Count > 0 && !c.StockGears.Contains(tuning.Gears))
                return CompatibilityResult.No($"Fits {string.Join("/", c.StockGears)}-speed gearboxes (car has {tuning.Gears})");
            if (chassis.ExcludeParts.Contains(part.Id)) return CompatibilityResult.No(chassis.Note ?? "Packaging does not allow this part on this chassis");
            return CompatibilityResult.Ok;
        }

        /// <summary>Every part compatible with the car (optionally one slot), in catalogue order.</summary>
        public IReadOnlyList<PartDef> CompatibleParts(CarDef car, CarTuningDef tuning, PartSlot? slot = null) =>
            Parts.Where(p => (slot == null || p.SlotValue == slot.Value) && CheckCompatibility(p, car, tuning).Compatible).ToList();

        static string Sha256(string text)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2")));
        }
    }
}
