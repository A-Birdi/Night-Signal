using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Rules;

namespace NightSignal.Core.Content
{
    public sealed class ValidationIssue
    {
        public ValidationIssue(string severity, string code, string message)
        {
            Severity = severity;
            Code = code;
            Message = message;
        }

        /// <summary>error | warning</summary>
        public string Severity { get; }
        public string Code { get; }
        public string Message { get; }
        public override string ToString() => $"[{Severity}] {Code}: {Message}";
    }

    public sealed class ValidationReport
    {
        public List<ValidationIssue> Issues { get; } = new List<ValidationIssue>();
        public Dictionary<string, int> Counts { get; } = new Dictionary<string, int>();
        public bool Passed => Issues.All(i => i.Severity != "error");
        public IEnumerable<ValidationIssue> Errors => Issues.Where(i => i.Severity == "error");
        internal void Error(string code, string msg) => Issues.Add(new ValidationIssue("error", code, msg));
        internal void Warn(string code, string msg) => Issues.Add(new ValidationIssue("warning", code, msg));
    }

    /// <summary>
    /// Data-level assertions from spec Appendix G. These check counts, references and arithmetic only; they cannot
    /// prove that a course is drivable, a car is a distinct mesh, or a rival is recognisable.
    /// </summary>
    public static class CatalogueValidator
    {
        static readonly string[] Regions = { "mizuhana", "kasumi", "kurogawa", "akebono", "hoshimi", "tsukishiro" };

        public static ValidationReport Validate(ContentCatalogue c)
        {
            if (c == null) throw new ArgumentNullException(nameof(c));
            var r = new ValidationReport();
            ValidateCourses(c, r);
            ValidateCars(c, r);
            ValidateRivals(c, r);
            ValidateStages(c, r);
            ValidateChallenges(c, r);
            ValidateOpposition(c, r);
            ValidateCourseAccess(c, r);
            return r;
        }

        static void ValidateCourses(ContentCatalogue c, ValidationReport r)
        {
            // Addendum 01 D03: 26 original courses + FP01–FP03 currency-only Freeplay courses = 29 distinct places.
            var expected = new List<string> { "T00", "FP01", "FP02", "FP03" };
            for (int i = 1; i <= 25; i++) expected.Add("C" + i.ToString("00"));
            r.Counts["courses"] = c.Courses.Count;
            if (!c.Courses.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(expected.OrderBy(x => x, StringComparer.Ordinal)))
                r.Error("COURSE_IDS", "Course IDs must be exactly T00, C01–C25 and FP01–FP03 (29 base courses).");
            int freeplayOnly = c.Courses.Count(x => x.Kind == "freeplay");
            r.Counts["freeplayOnlyCourses"] = freeplayOnly;
            if (freeplayOnly != 3) r.Error("COURSE_FREEPLAY", $"Expected 3 Freeplay-only courses FP01–FP03, found {freeplayOnly}.");
            int regular = c.Courses.Count(x => x.Kind == "regular");
            r.Counts["regularCourses"] = regular;
            if (regular != 24) r.Error("COURSE_REGULAR", $"Expected 24 regular courses C01–C24, found {regular}.");
            foreach (CourseDef course in c.Courses)
            {
                if (course.ExpectedSeconds < Economy.MinExpectedSeconds || course.ExpectedSeconds > Economy.MaxExpectedSeconds)
                    r.Warn("COURSE_EXPECTED", $"{course.Id} expected seconds {course.ExpectedSeconds} is outside 120–420 (payout clamps it).");
                if (course.Landmarks.Count < 3) r.Error("COURSE_LANDMARKS", $"{course.Id} needs at least three landmarks.");
                if (course.Format == "circuit" && course.Laps != 2) r.Error("COURSE_LAPS", $"{course.Id} circuits run two laps.");
                if (course.Format == "circuit" && Math.Abs(course.TargetNetElevationM) > 0.001)
                    r.Error("COURSE_LOOP_ELEVATION", $"{course.Id} is a closed loop but has net elevation {course.TargetNetElevationM} m.");
            }
            foreach (string region in Regions)
                if (!c.Courses.Any(x => x.Region == region && x.Kind == "regular"))
                    r.Error("COURSE_REGION", $"No regular course in region {region}.");
        }

        static void ValidateCars(ContentCatalogue c, ValidationReport r)
        {
            r.Counts["cars"] = c.Cars.Count;
            if (c.Cars.Count != 18) r.Error("CAR_COUNT", $"Expected 18 car models, found {c.Cars.Count}.");
            int starters = c.Cars.Count(x => x.Starter);
            if (starters != 3) r.Error("CAR_STARTERS", $"Expected 3 starter choices, found {starters}.");
            foreach (CarDef car in c.Cars)
            {
                if (car.Price <= 0 || car.Price > Limits.MaxCarPrice)
                    r.Error("CAR_PRICE", $"{car.Id} price {car.Price} must be 1–{Limits.MaxCarPrice}.");
                if (car.BasePI < PerformanceIndex.Min || car.BasePI > PerformanceIndex.Max)
                    r.Error("CAR_PI", $"{car.Id} base PI {car.BasePI} outside {PerformanceIndex.Min}–{PerformanceIndex.Max}.");
                if (car.Drive != "RWD" && car.Drive != "FWD" && car.Drive != "AWD")
                    r.Error("CAR_DRIVE", $"{car.Id} has unknown drive layout {car.Drive}.");
            }
            foreach (var dup in c.Cars.GroupBy(x => x.Silhouette).Where(g => g.Count() > 1))
                r.Error("CAR_SILHOUETTE", $"Cars share a silhouette description: {string.Join(", ", dup.Select(x => x.Id))}.");
        }

        static void ValidateRivals(ContentCatalogue c, ValidationReport r)
        {
            r.Counts["rivals"] = c.Rivals.Count;
            r.Counts["crews"] = c.Crews.Count;
            r.Counts["tendencies"] = c.Tendencies.Count;
            if (c.Rivals.Count != 48) r.Error("RIVAL_COUNT", $"Expected 48 rivals, found {c.Rivals.Count}.");
            if (c.Crews.Count != 6) r.Error("CREW_COUNT", $"Expected 6 crews, found {c.Crews.Count}.");
            if (c.Tendencies.Count != 19) r.Error("TENDENCY_COUNT", $"Expected 19 driving tendencies, found {c.Tendencies.Count}.");
            if (!c.TryRival("R40", out RivalDef reina) || reina.Role != "normal-final")
                r.Error("FINAL_NORMAL", "R40 must be the fixed Normal final rival.");
            if (!c.TryRival("R48", out RivalDef shiori) || shiori.Role != "hard-final")
                r.Error("FINAL_HARD", "R48 must be the fixed Hard final rival.");
            int lieutenants = c.Rivals.Count(x => x.Role == "lieutenant");
            if (lieutenants != 4) r.Error("LIEUTENANTS", $"Expected 4 lieutenants, found {lieutenants}.");
            var crewIds = new HashSet<string>(c.Crews.Select(x => x.Id));
            var tendencyIds = new HashSet<string>(c.Tendencies.Select(x => x.Id));
            foreach (RivalDef rival in c.Rivals)
            {
                if (!crewIds.Contains(rival.Crew)) r.Error("RIVAL_CREW", $"{rival.Id} references unknown crew {rival.Crew}.");
                if (!tendencyIds.Contains(rival.Tendency)) r.Error("RIVAL_TENDENCY", $"{rival.Id} references unknown tendency {rival.Tendency}.");
                if (!c.TryCar(rival.PrimaryCar, out _)) r.Error("RIVAL_CAR", $"{rival.Id} references unknown car {rival.PrimaryCar}.");
            }
            foreach (var dup in c.Rivals.GroupBy(x => x.Appearance).Where(g => g.Count() > 1))
                r.Error("RIVAL_APPEARANCE", $"Rivals share an appearance description: {string.Join(", ", dup.Select(x => x.Id))}.");
            foreach (CrewDef crew in c.Crews)
            {
                int members = c.Rivals.Count(x => x.Crew == crew.Id);
                if (members != 8) r.Error("CREW_SIZE", $"Crew {crew.Id} has {members} members; the roster defines eight per crew.");
            }
        }

        static void ValidateStages(ContentCatalogue c, ValidationReport r)
        {
            r.Counts["stages"] = c.Stages.Count;
            if (c.Stages.Count != Limits.CampaignStages)
                r.Error("STAGE_COUNT", $"Expected {Limits.CampaignStages} stages, found {c.Stages.Count}.");
            var types = c.Stages.GroupBy(s => s.Type).ToDictionary(g => g.Key, g => g.Count());
            Expect(r, types, "regular", 24);
            Expect(r, types, "lieutenant", 4);
            Expect(r, types, "penultimate", 1);
            Expect(r, types, "finale", 1);

            for (int i = 0; i < c.Stages.Count; i++)
                if (c.Stages[i].Number != i + 1 || c.Stages[i].Id != "S" + (i + 1).ToString("00"))
                    r.Error("STAGE_ORDER", $"Stage at position {i + 1} is {c.Stages[i].Id}/{c.Stages[i].Number}.");

            var regularCourses = c.Courses.Where(x => x.Kind == "regular").Select(x => x.Id).ToList();
            var usedByRegular = c.Stages.Where(s => s.Type == "regular").Select(s => s.Course).ToList();
            foreach (string course in regularCourses)
                if (!usedByRegular.Contains(course))
                    r.Error("STAGE_COURSE_UNUSED", $"Regular course {course} is not used by any regular stage.");
            if (usedByRegular.Distinct().Count() != usedByRegular.Count)
                r.Error("STAGE_COURSE_REUSE", "Regular stages must each use a different regular course.");

            StageDef finale = c.Stages.LastOrDefault();
            if (finale != null)
            {
                if (finale.Type != "finale" || finale.Course != "C25") r.Error("FINALE", "S30 must be the finale on C25.");
                if (finale.Normal.Lead != "R40") r.Error("FINALE_NORMAL", "Normal S30 must always feature R40.");
                if (finale.Hard.Lead != "R48") r.Error("FINALE_HARD", "Hard S30 must always feature R48.");
            }

            // A lieutenant/final rival must not appear as support before their own featured stage in that mode.
            foreach (bool hard in new[] { false, true })
            {
                var introduced = new HashSet<string>();
                foreach (StageDef s in c.Stages)
                {
                    StageSide side = hard ? s.Hard : s.Normal;
                    string mode = hard ? "Hard" : "Normal";
                    if (!c.TryCourse(s.Course, out _)) r.Error("STAGE_COURSE", $"{s.Id} references unknown course {s.Course}.");
                    if (!c.TryRival(side.Lead, out _)) r.Error("STAGE_LEAD", $"{mode} {s.Id} lead {side.Lead} is unknown.");
                    if (side.Support.Contains(side.Lead)) r.Error("STAGE_SUPPORT_LEAD", $"{mode} {s.Id} lists its lead in the support pool.");
                    foreach (string support in side.Support)
                    {
                        if (!c.TryRival(support, out RivalDef sr)) { r.Error("STAGE_SUPPORT", $"{mode} {s.Id} support {support} unknown."); continue; }
                        if (sr.Role != "crew" && !introduced.Contains(support))
                            r.Error("STAGE_SUPPORT_EARLY", $"{mode} {s.Id} uses {sr.Role} {support} as support before their featured stage.");
                        if (FinalRivals.IsFinaleOnly(sr.Id))
                            r.Error("STAGE_FINAL_SUPPORT", $"{sr.Id} is campaign-finale-only and may not be a support draw ({mode} {s.Id}).");
                    }
                    introduced.Add(side.Lead);
                    if (c.TryRival(side.Lead, out RivalDef lead) && c.TryCar(lead.PrimaryCar, out CarDef leadCar) && leadCar.BasePI > s.MaxPI)
                        r.Warn("STAGE_LEAD_CAR_CAP", $"{mode} {s.Id}: {lead.Id}'s primary {leadCar.Id} (PI {leadCar.BasePI}) exceeds cap {s.MaxPI}; needs a declared legal alternate.");
                }
            }
            foreach (StageDef s in c.Stages)
                if (s.MaxPI < PerformanceIndex.Min || s.MaxPI > PerformanceIndex.Max)
                    r.Error("STAGE_CAP", $"{s.Id} car cap {s.MaxPI} outside PI range.");
        }

        static void ValidateChallenges(ContentCatalogue c, ValidationReport r)
        {
            r.Counts["challenges"] = c.Challenges.Count;
            r.Counts["cosmetics"] = c.Cosmetics.Count;
            if (c.Challenges.Count != RankPoints.TotalChallenges)
                r.Error("CHALLENGE_COUNT", $"Expected 75 challenges, found {c.Challenges.Count}.");
            foreach (string tier in new[] { "bronze", "silver", "gold" })
            {
                int n = c.Challenges.Count(x => x.Tier == tier);
                r.Counts["challenges." + tier] = n;
                if (n != RankPoints.ChallengesPerTier) r.Error("CHALLENGE_TIER", $"Expected 25 {tier} challenges, found {n}.");
            }
            foreach (var family in c.Challenges.GroupBy(x => x.Family))
                foreach (string tier in new[] { "bronze", "silver", "gold" })
                    if (family.Count(x => x.Tier == tier) != 5)
                        r.Error("CHALLENGE_FAMILY_TIER", $"Family {family.Key} needs five {tier} challenges.");

            int challengeRp = 0;
            foreach (ChallengeDef ch in c.Challenges)
            {
                ChallengeTier tier = ParseTier(ch.Tier);
                if (ch.RankPoints != RankPoints.ForChallenge(tier)) r.Error("CHALLENGE_RP", $"{ch.Id} RP {ch.RankPoints} does not match {ch.Tier}.");
                if (ch.Cash != RankPoints.ChallengeCash(tier)) r.Error("CHALLENGE_CASH", $"{ch.Id} cash {ch.Cash} does not match {ch.Tier}.");
                if (!c.TryCosmetic(ch.Reward, out CosmeticDef cos)) r.Error("CHALLENGE_REWARD", $"{ch.Id} reward {ch.Reward} unknown.");
                else if (cos.Source != ch.Id) r.Error("CHALLENGE_REWARD_SOURCE", $"{ch.Reward} is not sourced from {ch.Id}.");
                challengeRp += ch.RankPoints;
            }
            if (c.Challenges.Select(x => x.Reward).Distinct().Count() != c.Challenges.Count)
                r.Error("REWARD_UNIQUE", "Every challenge needs its own unique cosmetic reward.");
            if (c.Cosmetics.Select(x => x.Name).Distinct().Count() != c.Cosmetics.Count)
                r.Error("REWARD_NAME_UNIQUE", "Cosmetic reward names must be unique.");

            r.Counts["rp.challenges"] = challengeRp;
            r.Counts["rp.normal"] = RankPoints.NormalBudget;
            r.Counts["rp.hard"] = RankPoints.HardBudget;
            r.Counts["rp.total"] = RankPoints.NormalBudget + RankPoints.HardBudget + challengeRp;
            if (challengeRp != RankPoints.ChallengeBudget) r.Error("RP_CHALLENGES", $"Challenge RP is {challengeRp}, expected 6,000.");
            if (r.Counts["rp.total"] != RankPoints.MaximumTotal) r.Error("RP_TOTAL", $"Total RP is {r.Counts["rp.total"]}, expected 15,000.");
        }

        /// <summary>Addendum 01 §1.2 and §12: authored live opposition, finale duels, finale-only rivals.</summary>
        static void ValidateOpposition(ContentCatalogue c, ValidationReport r)
        {
            int maxOpponents = Limits.MaxRaceVehicles - Limits.MaxEventHumanEntrants;
            foreach (StageDef s in c.Stages)
                foreach (bool hard in new[] { false, true })
                {
                    StageSide side = hard ? s.Hard : s.Normal;
                    string where = $"{(hard ? "Hard" : "Normal")} {s.Id}";
                    List<string> opp = side.Opponents ?? new List<string>();
                    if (opp.Count == 0) { r.Error("OPPOSITION_MISSING", $"{where} has no authored live opposition."); continue; }
                    if (opp[0] != side.Lead) r.Error("OPPOSITION_FEATURED", $"{where}: the featured rival {side.Lead} must be the first live opponent.");
                    if (opp.Distinct().Count() != opp.Count) r.Error("OPPOSITION_DUPLICATE", $"{where} lists an opponent twice.");
                    if (opp.Count > maxOpponents) r.Error("OPPOSITION_SIZE", $"{where}: {opp.Count} opponents cannot fit beside six humans (max {maxOpponents}).");
                    foreach (string id in opp)
                    {
                        if (!c.TryRival(id, out _)) r.Error("OPPOSITION_UNKNOWN", $"{where} references unknown rival {id}.");
                        bool ownFinale = s.Type == "finale" && id == side.Lead && id == (hard ? FinalRivals.HardFinal : FinalRivals.NormalFinal);
                        if (FinalRivals.IsFinaleOnly(id) && !ownFinale)
                            r.Error("OPPOSITION_FINAL_ONLY", $"{id} is campaign-finale-only; it cannot race in {where}.");
                    }
                    switch (s.Type)
                    {
                        case "finale":
                            if (opp.Count != 1) r.Warn("OPPOSITION_FINALE_DUEL", $"{where}: the finale is authored as a live duel with the final rival.");
                            break;
                        case "lieutenant":
                        case "penultimate":
                            if (opp.Count > 3) r.Error("OPPOSITION_ENCOUNTER", $"{where}: a featured encounter has at most two supports.");
                            break;
                        default:
                            if (s.Act == 1 && opp.Count > 2) r.Warn("OPPOSITION_EARLY", $"{where}: early races normally field one or two opponents.");
                            if (s.Act > 1 && (opp.Count < 2 || opp.Count > 3)) r.Warn("OPPOSITION_LATER", $"{where}: later regular races normally field two or three opponents.");
                            break;
                    }
                }
        }

        /// <summary>Addendum 01 §5.1: course-access table and its derived stage mapping.</summary>
        static void ValidateCourseAccess(ContentCatalogue c, ValidationReport r)
        {
            CourseAccessRules a = c.CourseAccess;
            if (a == null) { r.Error("ACCESS_MISSING", "Course-access table missing."); return; }
            foreach (string id in a.StarterCourses)
                if (!c.TryCourse(id, out _)) r.Error("ACCESS_STARTER", $"Starter course {id} is unknown.");
            IReadOnlyDictionary<string, string> unlock;
            try { unlock = CourseAccess.RegularStageUnlocks(c); }
            catch (ContentLoadException e) { r.Error("ACCESS_UNLOCK_MAP", e.Message); return; }
            r.Counts["access.campaignPurchasable"] = unlock.Count;
            if (unlock.Count != 20) r.Error("ACCESS_UNLOCK_COUNT", $"Expected 20 courses (C05–C24) with a regular-stage unlock, found {unlock.Count}.");
            if (a.CampaignCoursePrice <= 0 || a.CampaignCoursePrice > Limits.MaxCosmeticPrice)
                r.Error("ACCESS_PRICE", $"Campaign course price {a.CampaignCoursePrice} is out of range.");
            foreach (RewardOnlyCourse ro in a.RewardOnly)
                if (!c.TryCourse(ro.Course, out _) || c.Stages.All(s => s.Id != ro.Stage))
                    r.Error("ACCESS_REWARD", $"Reward-only access {ro.Course} ← {ro.Stage} references unknown content.");
            foreach (PurchaseOnlyCourse po in a.PurchaseOnly)
            {
                if (!c.TryCourse(po.Course, out CourseDef course) || course.Kind != "freeplay")
                    r.Error("ACCESS_PURCHASE_ONLY", $"Purchase-only course {po.Course} must be a Freeplay-only course.");
                if (po.Price <= 0 || po.Price > Limits.MaxCosmeticPrice) r.Error("ACCESS_PRICE", $"{po.Course} price {po.Price} is out of range.");
            }
            foreach (CourseDef course in c.Courses)
                if (CourseAccess.RuleFor(c, course.Id).Kind == CourseAccessKind.None)
                    r.Error("ACCESS_UNDEFINED", $"Course {course.Id} has no access rule.");
        }

        static ChallengeTier ParseTier(string tier)
        {
            switch (tier)
            {
                case "bronze": return ChallengeTier.Bronze;
                case "silver": return ChallengeTier.Silver;
                case "gold": return ChallengeTier.Gold;
                default: throw new ContentLoadException($"Unknown challenge tier '{tier}'");
            }
        }

        static void Expect(ValidationReport r, Dictionary<string, int> types, string type, int expected)
        {
            types.TryGetValue(type, out int n);
            r.Counts["stages." + type] = n;
            if (n != expected) r.Error("STAGE_TYPES", $"Expected {expected} {type} stages, found {n}.");
        }
    }
}
