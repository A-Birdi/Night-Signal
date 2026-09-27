using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Content;

namespace NightSignal.Core.Rules
{
    public enum CourseAccessKind { None = 0, Starter = 1, PurchaseOrCampaignClear = 2, CampaignRewardOnly = 3, PurchaseOnly = 4 }

    /// <summary>How a progression domain holds a course. Guest passes are event-scoped and never stored here.</summary>
    public enum CourseEntitlementSource { Starter = 0, Purchased = 1, CampaignClear = 2 }

    public sealed class CourseAccessRule
    {
        public string CourseId;
        public CourseAccessKind Kind;
        /// <summary>Price for purchasable kinds; 0 otherwise.</summary>
        public long Price;
        /// <summary>Stage whose first Normal clear grants the course (regular stage for C05–C24, S30 for C25).</summary>
        public string UnlockStage;
        public bool Purchasable => Kind == CourseAccessKind.PurchaseOrCampaignClear || Kind == CourseAccessKind.PurchaseOnly;
    }

    /// <summary>Result of an idempotent purchase attempt (Addendum 01 §5.1).</summary>
    public enum CoursePurchaseOutcome { Purchased = 0, AlreadyOwned = 1, NotPurchasable = 2, InsufficientFunds = 3 }

    /// <summary>
    /// Course-access rules (Addendum 01 §5): starter courses, early-access purchases, campaign-clear unlocks derived from
    /// each course's REGULAR Normal stage (never a reused lieutenant/penultimate stage, never course-number arithmetic),
    /// the C25 finale-route reward, and currency-only Freeplay courses. Ownership never grants a clear, Hard access, RP,
    /// a rival identity or a soundtrack cue. A Freeplay event is convoy-accessible when at least one current member owns
    /// the course; that member sponsors event-scoped guest passes for everyone else.
    /// </summary>
    public static class CourseAccess
    {
        /// <summary>Course → regular stage whose first Normal clear grants permanent access (C05–C24).</summary>
        public static IReadOnlyDictionary<string, string> RegularStageUnlocks(ContentCatalogue c)
        {
            CourseAccessRules a = c.CourseAccess ?? throw new ContentLoadException("Course-access table missing");
            if (a.CampaignCourseRange == null || a.CampaignCourseRange.Count != 2)
                throw new ContentLoadException("campaignCourseRange must name the first and last course");
            string first = a.CampaignCourseRange[0], last = a.CampaignCourseRange[1];
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (CourseDef course in c.Courses.Where(x => x.Kind == "regular"))
            {
                if (string.CompareOrdinal(course.Id, first) < 0 || string.CompareOrdinal(course.Id, last) > 0) continue;
                List<StageDef> regular = c.Stages.Where(s => s.Type == "regular" && s.Course == course.Id).ToList();
                if (regular.Count != 1)
                    throw new ContentLoadException($"{course.Id} needs exactly one regular campaign stage for its free unlock; found {regular.Count}");
                map[course.Id] = regular[0].Id;
            }
            return map;
        }

        public static CourseAccessRule RuleFor(ContentCatalogue c, string courseId)
        {
            CourseAccessRules a = c.CourseAccess;
            var rule = new CourseAccessRule { CourseId = courseId, Kind = CourseAccessKind.None };
            if (a == null || !c.TryCourse(courseId, out _)) return rule;
            if (a.StarterCourses.Contains(courseId)) { rule.Kind = CourseAccessKind.Starter; return rule; }
            PurchaseOnlyCourse po = a.PurchaseOnly.FirstOrDefault(p => p.Course == courseId);
            if (po != null) { rule.Kind = CourseAccessKind.PurchaseOnly; rule.Price = po.Price; return rule; }
            RewardOnlyCourse ro = a.RewardOnly.FirstOrDefault(p => p.Course == courseId);
            if (ro != null) { rule.Kind = CourseAccessKind.CampaignRewardOnly; rule.UnlockStage = ro.Stage; return rule; }
            if (RegularStageUnlocks(c).TryGetValue(courseId, out string stage))
            {
                rule.Kind = CourseAccessKind.PurchaseOrCampaignClear;
                rule.Price = a.CampaignCoursePrice;
                rule.UnlockStage = stage;
            }
            return rule;
        }

        /// <summary>Courses a domain holds permanently: starters plus stored entitlements.</summary>
        public static bool Owns(ContentCatalogue c, string courseId, ICollection<string> storedEntitlements) =>
            RuleFor(c, courseId).Kind == CourseAccessKind.Starter || (storedEntitlements != null && storedEntitlements.Contains(courseId));

        /// <summary>Courses granted by the first Normal clear of <paramref name="stageId"/> (idempotent; may be empty).</summary>
        public static IReadOnlyList<string> GrantedByNormalClear(ContentCatalogue c, string stageId)
        {
            var granted = new List<string>();
            foreach (KeyValuePair<string, string> kv in RegularStageUnlocks(c))
                if (kv.Value == stageId) granted.Add(kv.Key);
            foreach (RewardOnlyCourse ro in c.CourseAccess.RewardOnly)
                if (ro.Stage == stageId && ro.Mode == "normal") granted.Add(ro.Course);
            return granted;
        }

        /// <summary>
        /// Decides a purchase against the current ledger state. The caller commits the debit and the entitlement in one
        /// transaction; if a free unlock committed first, the retry sees AlreadyOwned and charges nothing.
        /// </summary>
        public static CoursePurchaseOutcome DecidePurchase(ContentCatalogue c, string courseId, ICollection<string> storedEntitlements, long balance, out long price)
        {
            CourseAccessRule rule = RuleFor(c, courseId);
            price = 0;
            if (Owns(c, courseId, storedEntitlements)) return CoursePurchaseOutcome.AlreadyOwned;
            if (!rule.Purchasable) return CoursePurchaseOutcome.NotPurchasable;
            price = rule.Price;
            return balance >= price ? CoursePurchaseOutcome.Purchased : CoursePurchaseOutcome.InsufficientFunds;
        }

        /// <summary>
        /// Current convoy members who can sponsor <paramref name="courseId"/> for a Freeplay event (Addendum 01 D04).
        /// Keys are account IDs; values are that account's stored entitlements in its ONLINE domain.
        /// </summary>
        public static IReadOnlyList<string> Sponsors(ContentCatalogue c, string courseId, IReadOnlyDictionary<string, ICollection<string>> activeMembers)
        {
            if (!c.TryCourse(courseId, out CourseDef course) || course.Kind == "tutorial") return Array.Empty<string>();
            return activeMembers.Where(m => Owns(c, courseId, m.Value)).Select(m => m.Key).OrderBy(id => id, StringComparer.Ordinal).ToList();
        }
    }
}
