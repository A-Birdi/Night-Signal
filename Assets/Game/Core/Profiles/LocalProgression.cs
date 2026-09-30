using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;

namespace NightSignal.Core.Profiles
{
    /// <summary>Creates stable local ids ("lp_…" profiles, "ci_…" car instances). Tests inject a deterministic source.</summary>
    public interface ILocalIdSource
    {
        string NewId(string prefix);
    }

    public sealed class RandomLocalIdSource : ILocalIdSource
    {
        public static readonly RandomLocalIdSource Instance = new RandomLocalIdSource();

        public string NewId(string prefix)
        {
            var bytes = new byte[8];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            var sb = new StringBuilder(prefix.Length + 17);
            sb.Append(prefix).Append('_');
            foreach (byte b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    public sealed class NewLocalProfileRequest
    {
        public string DisplayName;
        /// <summary>One of the catalogue's starter models (V01–V03).</summary>
        public string StarterCarModelId;
        public CardAppearance Card;
        public DateTime Utc;
        /// <summary>Optional explicit id; generated when null.</summary>
        public string ProfileId;
    }

    /// <summary>A record value measured in a local event. The key's domain must be Local.</summary>
    public sealed class RecordCandidate
    {
        public RecordKey Key;
        /// <summary>The measured value at authoritative precision, or null when the run produced no eligible value.</summary>
        public long? Value;
    }

    /// <summary>Team Trial facts from the in-process local simulation (one local human; AI fill the other eleven positions).</summary>
    public sealed class LocalTeamTrialFacts
    {
        public string TrialId;
        public TeamTrialKind Kind;
        public string Difficulty = "";
        public long HardTimeoutMs;
        public long ParticipationEnvelopeMs;
        /// <summary>Declared bounded placement modifier for a team victory / defeat (Core Economy placement).</summary>
        public int VictoryPlacement = 1;
        public int DefeatPlacement = 4;
        /// <summary>Exactly six positions: the local human plus five friendly AI.</summary>
        public List<TeamMemberResult> PlayerSide = new List<TeamMemberResult>();
        /// <summary>Exactly six opposing AI.</summary>
        public List<TeamMemberResult> OpposingSide = new List<TeamMemberResult>();
        public string LocalEntrantId;
        /// <summary>Optional team-record key (metric must match the trial kind, composition H = 1); the value is computed here.</summary>
        public RecordKey TeamRecordKey;
    }

    /// <summary>
    /// Facts about one completed LOCAL event, produced by the in-process authoritative simulation (the same shared rules
    /// and vehicle model as online). Money, RP, clears and unlocks are never inputs: they are computed from these facts.
    /// </summary>
    public sealed class LocalEventFacts
    {
        /// <summary>Unique id of this completed local event (idempotency key; re-applying it changes nothing).</summary>
        public string EventId;
        /// <summary>CampaignStage, Tutorial or a Freeplay kind (Team Trials use Sprint/Circuit/DriftAttack plus <see cref="TeamTrial"/>).</summary>
        public EventKind Kind;
        public string CourseId;
        /// <summary>Campaign only.</summary>
        public string StageId;
        public CampaignMode Mode;
        public DateTime CompletedUtc;

        public RunOutcome Outcome;
        /// <summary>Finish time in race-clock microseconds including ordinary penalties (Finished only).</summary>
        public long FinishTimeMicros;
        /// <summary>1-based classified placing among up to 12 vehicles (ignored for Time Attack and Team Trials).</summary>
        public int Placement;
        public bool Clean;
        public double CheckpointFraction;
        public bool ActiveProgressVerified;
        public bool ActivelyDroveLegalCourse;
        public long RawDriftScore;
        public int ContractsPassed;
        /// <summary>0, 4 or 8 (single income utility item).</summary>
        public int UtilityIncomePercent;
        /// <summary>Time Attack reference; when null the course's expected time is used (as the control plane does today).</summary>
        public long? FreeplayReferenceMs;

        /// <summary>Car model driven; required.</summary>
        public string CarModelId;
        /// <summary>Owned instance driven, or null/"" with <see cref="Loaner"/> = true for a class-legal loaner.</summary>
        public string CarInstanceId;
        public bool Loaner;
        public string BuildId;

        /// <summary>Campaign: the benchmark frozen when the event started.</summary>
        public StageBenchmark Benchmark;
        /// <summary>Campaign encounter: the local human legally finished ahead of the featured live rival (or it legally failed to finish).</summary>
        public bool BeatFeaturedRival;
        /// <summary>False when the featured rival never spawned: the event is broken and aborted, never a free win.</summary>
        public bool FeaturedRivalStarted = true;
        /// <summary>The field's AI in roster order, the lead first (Freeplay: CH38/CH73 read the authored rivals' archetypes).</summary>
        public List<string> OpposingAi = new List<string>();
        /// <summary>The local human shares its placing with another entrant (a tie is not a win).</summary>
        public bool Tied;

        /// <summary>Challenge trial run (docs/CHALLENGE_TRIALS.md): the trial id, and whether the Core TrialJudge passed it.</summary>
        public string TrialId;
        public bool TrialPassed;

        /// <summary>Challenge predicates the local simulation judged met by THIS driver's personal performance.</summary>
        public List<string> ChallengesCompleted = new List<string>();
        public List<RecordCandidate> Records = new List<RecordCandidate>();
        public LocalTeamTrialFacts TeamTrial;
    }

    public enum LocalOperationStatus
    {
        Applied = 0,
        /// <summary>Idempotent repeat (same event/request id, already-owned course): nothing changed, nothing charged.</summary>
        AlreadyApplied = 1,
        Rejected = 2,
        /// <summary>Broken event (featured rival never started): no results, rewards or progression.</summary>
        Aborted = 3,
    }

    public enum ProgressionChangeKind
    {
        Credits = 0,
        CreditsClamped = 1,
        Debit = 2,
        StageCleared = 3,
        RankPoints = 4,
        CourseUnlocked = 5,
        CoursePurchased = 6,
        CourseAlreadyOwned = 7,
        CarGranted = 8,
        CarPurchased = 9,
        ChallengeCompleted = 10,
        CosmeticGranted = 11,
        MusicUnlocked = 12,
        PersonalBest = 13,
        CarBest = 14,
        RecordTie = 15,
        TutorialCompleted = 16,
        DisplayNameChanged = 17,
        /// <summary>Garage (LocalGarage): a part now owned by one car instance.</summary>
        PartGranted = 18,
        /// <summary>Garage: a new applied build revision (Buy and Apply).</summary>
        BuildApplied = 19,
        /// <summary>Garage: a car's build workspace was stored.</summary>
        WorkspaceSaved = 20,
        /// <summary>Garage: Last Race Build recorded when a Local race began.</summary>
        RaceBuildRecorded = 21,
        CardChanged = 22,
        /// <summary>A race-diary entry read (CH70 counts crew introductions).</summary>
        DiaryRead = 23,
        /// <summary>A T00 lesson passed (training progress only).</summary>
        LessonPassed = 24,
    }

    /// <summary>One itemised line for the results screen: what changed and why.</summary>
    public sealed class ProgressionChange
    {
        public ProgressionChangeKind Kind;
        public string Subject = "";
        public long Amount;
        public string Detail = "";

        public override string ToString() => $"{Kind} {Subject} {Amount}: {Detail}";
    }

    public sealed class StageVerdictSummary
    {
        public string StageId = "";
        public CampaignMode Mode;
        public bool Qualified;
        public bool WithinSupport;
        public bool EarnedClear;
        public bool FirstClear;
        public bool RequiresBeatingFeaturedRival;
        public bool BeatFeaturedRival;
        public string Reason = "";
    }

    public sealed class TeamTrialSummary
    {
        public string TrialId = "";
        public TeamTrialKind Kind;
        public TeamTrialVerdict Verdict;
        public long PlayerTeamValue;
        public long OpposingTeamValue;
        public bool CompletionPayable;
        public string Note = "";
    }

    /// <summary>
    /// Itemised outcome of a Local operation, for the results/wallet screens. It is deliberately NOT a receipt: it has no
    /// match id, signature or server identity and cannot be submitted anywhere. <see cref="Profile"/> is the new profile
    /// state (the input is never modified); persist it with <see cref="ProfileRepository.Save"/>.
    /// </summary>
    public sealed class LocalProgressionResult
    {
        public ProgressionDomain Domain => ProgressionDomain.Local;
        public LocalOperationStatus Status;
        public string Reason = "";
        public LocalProfile Profile;
        public List<ProgressionChange> Changes = new List<ProgressionChange>();
        public List<string> Notes = new List<string>();
        public PayoutBreakdown Payout;
        public CoursePurchaseOutcome? PurchaseOutcome;
        public StageVerdictSummary Stage;
        public TeamTrialSummary TeamTrial;
        public List<RecordUpdateResult> Records = new List<RecordUpdateResult>();
        /// <summary>Garage operations only (<see cref="LocalGarage"/>): the car, its stored workspace and the settlement.</summary>
        public LocalGarageOutcome Garage;
        public long BalanceBefore;
        public long BalanceAfter;
        public int RankPointsBefore;
        public int RankPointsAfter;
        public string RankBefore = "";
        public string RankAfter = "";

        public bool Changed => Status == LocalOperationStatus.Applied;
        public IEnumerable<ProgressionChange> Of(ProgressionChangeKind kind) => Changes.Where(c => c.Kind == kind);
    }

    /// <summary>
    /// LOCAL progression rules (Addendum 01 §8.2, D05). Pure functions over (profile, content, facts) that apply the SAME
    /// Core rules as the Online settlement wherever they exist: Core <see cref="Economy"/> payouts (placements 1–12, DNF
    /// allowance, no quitter pay), first-clear bonus once per stage and mode, <see cref="CampaignProgress"/> frontier and
    /// Hard access, <see cref="StageOutcome"/> clear rules (with H = 1), <see cref="CourseAccess"/> unlocks and atomic
    /// purchases, one-time challenge rewards, <see cref="RankPoints"/> (finite 15,000 per domain), <see cref="Wallet"/> cap
    /// clamping, <see cref="TeamTrials"/> scoring (no mastery RP) and soundtrack grants from their declared source only.
    /// <para>
    /// Every operation returns a new profile copy plus an itemised result; the input is never modified, so a failure
    /// leaves nothing half-applied. AI entrants never receive anything: only the single local human's profile exists here.
    /// Nothing produced here is, or can be converted into, Online state; there is intentionally no export, signature or
    /// "verify later" path. A future trusted offline-to-online scheme would need its own approved design.
    /// </para>
    /// </summary>
    public static class LocalProgression
    {
        static readonly Regex OperationIdPattern = new Regex("^[A-Za-z0-9][A-Za-z0-9_.:-]{2,95}$", RegexOptions.CultureInvariant);

        public static bool IsValidOperationId(string id) => id != null && OperationIdPattern.IsMatch(id);

        // ---------------- profile lifecycle ----------------

        /// <summary>A new Local profile: chosen starter car instance, the starter grant (12,000) and the baseline soundtrack.</summary>
        public static LocalProgressionResult NewProfile(ContentCatalogue catalogue, IMusicUnlockSource music, NewLocalProfileRequest request, ILocalIdSource ids = null)
        {
            if (catalogue == null) throw new ArgumentNullException(nameof(catalogue));
            if (request == null) throw new ArgumentNullException(nameof(request));
            ids = ids ?? RandomLocalIdSource.Instance;
            music = music ?? MusicUnlockTable.Empty;
            var result = new LocalProgressionResult();

            if (!LocalDisplayName.TryNormalize(request.DisplayName, out string name, out string error)) return Reject(result, error);
            if (!catalogue.TryCar(request.StarterCarModelId, out CarDef car) || !car.Starter)
            {
                string starters = string.Join(", ", catalogue.Cars.Where(c => c.Starter).Select(c => c.Id));
                return Reject(result, $"Choose one of the starter cars ({starters}).");
            }
            string profileId = request.ProfileId ?? ids.NewId("lp");
            if (!LocalProfile.IsValidId(profileId)) return Reject(result, "Malformed profile id.");
            DateTime utc = request.Utc;

            var p = new LocalProfile
            {
                ProfileId = profileId,
                DisplayName = name,
                Card = request.Card != null ? ProfileJson.Clone(request.Card) : new CardAppearance(),
                CreatedUtc = utc,
                UpdatedUtc = utc,
                StarterCarModelId = car.Id,
                ContentHash = catalogue.ContentHash ?? "",
            };
            string instanceId = ids.NewId("ci");
            p.Cars.Add(new OwnedCar { InstanceId = instanceId, ModelId = car.Id, Source = CarSource.Starter, SourceReference = "starter-choice", AcquiredUtc = utc });
            Add(result, ProgressionChangeKind.CarGranted, instanceId, 0, $"Starter car {car.Name} ({car.Id}).");
            Credit(p, result, "starter", car.Id, Limits.StarterGrantCredits, utc, "Starter grant.");
            GrantBaseline(p, result, music, utc);
            result.Status = LocalOperationStatus.Applied;
            return Finish(result, p);
        }

        public static LocalProgressionResult Rename(LocalProfile profile, string displayName)
        {
            LocalProgressionResult result = Begin(profile);
            if (!LocalDisplayName.TryNormalize(displayName, out string name, out string error)) return Reject(result, error);
            if (name == profile.DisplayName) return Already(result, "The name is unchanged.");
            LocalProfile p = ProfileJson.Clone(profile);
            p.DisplayName = name;
            Add(result, ProgressionChangeKind.DisplayNameChanged, name, 0, "Display name changed.");
            result.Status = LocalOperationStatus.Applied;
            return Finish(result, p);
        }

        /// <summary>
        /// The Local driver card: display name, driver look and pronouns — the look validated and stored canonically exactly as
        /// the online card's (<see cref="Characters.PlayerLooks"/>); "" = the default look from the name. Unchanged is a no-op.
        /// </summary>
        public static LocalProgressionResult SetCard(LocalProfile profile, string displayName, string lookJson, string pronouns,
            Customization.CardStyle style = null, Customization.CardStyleCatalogue card = null, IReadOnlyList<string> showcase = null,
            ContentCatalogue content = null)
        {
            LocalProgressionResult result = Begin(profile);
            if (!LocalDisplayName.TryNormalize(displayName, out string name, out string error)) return Reject(result, error);
            string look = "";
            if (!string.IsNullOrEmpty(lookJson))
            {
                Characters.CharacterLook parsed = Characters.PlayerLooks.Parse(lookJson);
                if (parsed == null) return Reject(result, "That look could not be read.");
                List<string> problems = Characters.PlayerLooks.Problems(parsed).ToList();
                if (problems.Count > 0) return Reject(result, problems[0]);
                look = Characters.PlayerLooks.Canonical(parsed);
            }
            string words = (pronouns ?? "").Trim();
            if (!PronounsOk(words)) return Reject(result, "Pronouns: up to 24 plain characters.");
            // The card's style (spec §11): reward items only once this profile owns them, the car one it owns.
            Customization.CardStyle styled = style != null && card != null ? style : StyleOf(profile.Card, null);
            if (style != null)
            {
                if (card == null) return Reject(result, "No card style catalogue.");
                var owned = new HashSet<string>((profile.Cosmetics ?? new List<OwnedCosmetic>()).Select(c => c.CosmeticId), StringComparer.Ordinal);
                List<string> bad = card.Problems(style, owned.Contains, car => (profile.Cars ?? new List<OwnedCar>()).Any(c => c.ModelId == car));
                if (bad.Count > 0) return Reject(result, bad[0]);
            }
            bool sameStyle = style == null || StyleOf(profile.Card, null).ContentEquals(styled);
            // The showcase: up to three of this profile's own records (null = keep).
            if (LocalShowcase.Problem(showcase, profile, content) is string showcaseProblem) return Reject(result, showcaseProblem);
            bool sameShowcase = showcase == null || (profile.Card?.Showcase ?? new List<string>()).SequenceEqual(showcase);
            if (name == profile.DisplayName && look == (profile.Card?.Look ?? "") && words == (profile.Card?.Pronouns ?? "") && sameStyle && sameShowcase)
                return Already(result, "The card is unchanged.");
            LocalProfile p = ProfileJson.Clone(profile);
            p.DisplayName = name;
            p.Card = p.Card ?? new CardAppearance();
            p.Card.Look = look;
            p.Card.Pronouns = words;
            if (style != null)
            {
                p.Card.BackgroundId = styled.Background;
                p.Card.FrameId = styled.Frame;
                p.Card.MotifId = styled.Motif;
                p.Card.TitleId = styled.Title;
                p.Card.LayoutId = styled.Layout;
                p.Card.Region = styled.Region;
                p.Card.PreferredCar = styled.PreferredCar;
            }
            if (showcase != null) p.Card.Showcase = showcase.ToList();
            Add(result, ProgressionChangeKind.CardChanged, name, 0, "Driver card changed.");
            result.Status = LocalOperationStatus.Applied;
            return Finish(result, p);
        }

        /// <summary>
        /// The Local card's style as a <see cref="Customization.CardStyle"/>; an empty field takes <paramref name="defaults"/>'s
        /// value ("" when no defaults are given).
        /// </summary>
        public static Customization.CardStyle StyleOf(CardAppearance card, Customization.CardStyle defaults)
        {
            string Or(string v, string d) => string.IsNullOrEmpty(v) ? d ?? "" : v;
            return new Customization.CardStyle
            {
                Background = Or(card?.BackgroundId, defaults?.Background),
                Frame = Or(card?.FrameId, defaults?.Frame),
                Motif = Or(card?.MotifId, defaults?.Motif),
                Title = Or(card?.TitleId, defaults?.Title),
                Layout = Or(card?.LayoutId, defaults?.Layout),
                Region = card?.Region ?? "",
                PreferredCar = card?.PreferredCar ?? "",
            };
        }

        /// <summary>The online card's pronoun rule: up to 24 characters, no control characters, markup or surrogates.</summary>
        public static bool PronounsOk(string pronouns) =>
            pronouns.Length <= 24 && !pronouns.Any(ch => char.IsControl(ch) || ch == '<' || ch == '>' || ch == '{' || ch == '}' || char.IsSurrogate(ch));

        /// <summary>Grants any baseline cues the profile is missing (e.g. after the manifest gained one). Idempotent.</summary>
        public static LocalProgressionResult SyncBaselineMusic(LocalProfile profile, IMusicUnlockSource music, DateTime utc)
        {
            LocalProgressionResult result = Begin(profile);
            music = music ?? MusicUnlockTable.Empty;
            if (music.Baseline().All(r => profile.HasCue(r.CueId))) return Already(result, "Baseline soundtrack already complete.");
            LocalProfile p = ProfileJson.Clone(profile);
            GrantBaseline(p, result, music, utc);
            result.Status = LocalOperationStatus.Applied;
            return Finish(result, p);
        }

        // ---------------- access ----------------

        /// <summary>Solo campaign access with Core <see cref="CampaignProgress"/> (frontier; Hard after the Normal finale).</summary>
        public static ConvoyStageAccess CampaignAccess(LocalProfile profile, CampaignMode mode)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            var member = new MemberProgress("local", profile.Campaign.Flags(CampaignMode.Normal), profile.Campaign.Flags(CampaignMode.Hard));
            ConvoyStageAccess access = CampaignProgress.Evaluate(mode, new[] { member });
            if (!access.ModeAllowed)
                access.Explanation = "Hard unlocks after you clear the Normal finale (S30) in this Local profile.";
            else
            {
                int frontier = CampaignProgress.Frontier(member.Cleared(mode));
                access.Explanation = frontier <= Limits.CampaignStages
                    ? $"Next stage: {CampaignProgress.StageLabel(frontier)}."
                    : "Campaign complete: every stage is open for replay.";
            }
            return access;
        }

        public static bool CanStartCampaignStage(LocalProfile profile, ContentCatalogue catalogue, string stageId, CampaignMode mode, out string reason)
        {
            StageDef stage = catalogue.Stages.FirstOrDefault(s => s.Id == stageId);
            if (stage == null)
            {
                reason = $"Unknown stage '{stageId}'.";
                return false;
            }
            ConvoyStageAccess access = CampaignAccess(profile, mode);
            if (!access.ModeAllowed)
            {
                reason = access.Explanation;
                return false;
            }
            if (!access.CanSelect(stage.Number))
            {
                reason = $"{stage.Id} is beyond your frontier. {access.Explanation} A course purchase never unlocks a campaign stage.";
                return false;
            }
            reason = "";
            return true;
        }

        /// <summary>Freeplay/Team Trial course access in the Local course ledger (starters + stored rights).</summary>
        public static bool CanStartFreeplay(LocalProfile profile, ContentCatalogue catalogue, string courseId, out string reason)
        {
            if (!catalogue.TryCourse(courseId, out CourseDef course))
            {
                reason = $"Unknown course '{courseId}'.";
                return false;
            }
            if (course.Kind == "tutorial")
            {
                reason = "The tutorial course is played from the tutorial, not Freeplay.";
                return false;
            }
            if (profile.OwnsCourse(catalogue, courseId))
            {
                reason = "";
                return true;
            }
            reason = AccessHint(catalogue, courseId);
            return false;
        }

        /// <summary>Course-card wording for a locked course ("Buy Freeplay access — 45,000" / "Unlock free: clear Normal S23").</summary>
        public static string AccessHint(ContentCatalogue catalogue, string courseId)
        {
            CourseAccessRule rule = CourseAccess.RuleFor(catalogue, courseId);
            string price = rule.Price.ToString("N0", CultureInfo.InvariantCulture);
            switch (rule.Kind)
            {
                case CourseAccessKind.PurchaseOrCampaignClear:
                    return $"Buy Freeplay access — {price}, or unlock free: clear Normal {rule.UnlockStage}.";
                case CourseAccessKind.PurchaseOnly:
                    return $"Freeplay-only course: buy access — {price}.";
                case CourseAccessKind.CampaignRewardOnly:
                    return $"Unlock free: clear Normal {rule.UnlockStage}. Not sold.";
                case CourseAccessKind.Starter:
                    return "Available from the start.";
                default:
                    return "Not available.";
            }
        }

        // ---------------- purchases ----------------

        /// <summary>
        /// Buys permanent Freeplay access with Core <see cref="CourseAccess.DecidePurchase"/>: the debit and the entitlement are
        /// committed together in the returned copy. AlreadyOwned (a free unlock or an earlier purchase came first) never
        /// debits. A purchase grants no campaign clear, Hard access, RP, rival identity or soundtrack cue.
        /// </summary>
        public static LocalProgressionResult PurchaseCourse(LocalProfile profile, ContentCatalogue catalogue, string courseId, DateTime utc)
        {
            LocalProgressionResult result = Begin(profile);
            if (catalogue == null) throw new ArgumentNullException(nameof(catalogue));
            if (!catalogue.TryCourse(courseId, out CourseDef course)) return Reject(result, $"Unknown course '{courseId}'.");
            CoursePurchaseOutcome outcome = CourseAccess.DecidePurchase(catalogue, courseId, profile.StoredCourseIds(), profile.WalletBalance, out long price);
            result.PurchaseOutcome = outcome;
            switch (outcome)
            {
                case CoursePurchaseOutcome.AlreadyOwned:
                    Add(result, ProgressionChangeKind.CourseAlreadyOwned, courseId, 0, "Already owned: nothing was charged.");
                    return Already(result, $"{course.Name} is already yours; nothing was charged.");
                case CoursePurchaseOutcome.NotPurchasable:
                    return Reject(result, $"{course.Name} cannot be bought. {AccessHint(catalogue, courseId)}");
                case CoursePurchaseOutcome.InsufficientFunds:
                    return Reject(result, $"Not enough credits: {price.ToString("N0", CultureInfo.InvariantCulture)} needed, " +
                                          $"{profile.WalletBalance.ToString("N0", CultureInfo.InvariantCulture)} available.");
            }

            LocalProfile p = ProfileJson.Clone(profile);
            if (!Wallet.TryDebit(p.WalletBalance, price, out long balance))
                return Reject(result, "Not enough credits.");
            p.WalletBalance = balance;
            p.Courses.Add(new CourseEntitlement { CourseId = courseId, Source = CourseEntitlementSource.Purchased, Reference = "purchase", PricePaid = price, AcquiredUtc = utc });
            History(p, utc, "course-purchase", courseId, -price, -price, 0);
            Add(result, ProgressionChangeKind.Debit, courseId, -price, $"Bought Freeplay access to {course.Name}.");
            Add(result, ProgressionChangeKind.CoursePurchased, courseId, price, "Permanent Freeplay access in this Local profile.");
            result.Notes.Add("Buying a course is Freeplay access only: it does not clear a campaign stage, unlock Hard, add RP, reveal a rival or unlock soundtrack.");
            p.ContentHash = catalogue.ContentHash ?? "";
            result.Status = LocalOperationStatus.Applied;
            return Finish(result, p);
        }

        /// <summary>
        /// Buys a new, independent instance of a car model. <paramref name="requestId"/> makes a double-submit harmless.
        /// Shop availability (act gating, compatibility) is decided by the caller's shop rules before this is offered.
        /// </summary>
        public static LocalProgressionResult PurchaseCar(LocalProfile profile, ContentCatalogue catalogue, string modelId, string requestId, DateTime utc, ILocalIdSource ids = null)
        {
            LocalProgressionResult result = Begin(profile);
            if (catalogue == null) throw new ArgumentNullException(nameof(catalogue));
            ids = ids ?? RandomLocalIdSource.Instance;
            if (!IsValidOperationId(requestId)) return Reject(result, "A purchase needs a request id.");
            if (profile.HasApplied(requestId)) return Already(result, "This purchase was already completed; nothing more was charged.");
            if (!catalogue.TryCar(modelId, out CarDef car)) return Reject(result, $"Unknown car '{modelId}'.");
            if (car.Price < 0 || car.Price > Limits.MaxCarPrice) return Reject(result, $"{car.Id} has an invalid price.");
            if (!Wallet.TryDebit(profile.WalletBalance, car.Price, out long balance))
                return Reject(result, $"Not enough credits: {car.Price.ToString("N0", CultureInfo.InvariantCulture)} needed, " +
                                      $"{profile.WalletBalance.ToString("N0", CultureInfo.InvariantCulture)} available.");

            LocalProfile p = ProfileJson.Clone(profile);
            p.WalletBalance = balance;
            string instanceId = ids.NewId("ci");
            if (!LocalProfile.IsValidId(instanceId) || p.FindCar(instanceId) != null) return Reject(result, "Could not allocate a car instance id.");
            p.Cars.Add(new OwnedCar
            {
                InstanceId = instanceId, ModelId = car.Id, Source = CarSource.Purchased, SourceReference = requestId, PricePaid = car.Price, AcquiredUtc = utc,
            });
            History(p, utc, "car-purchase", car.Id, -car.Price, -car.Price, 0);
            MarkApplied(p, requestId);
            Add(result, ProgressionChangeKind.Debit, car.Id, -car.Price, $"Bought {car.Name}.");
            Add(result, ProgressionChangeKind.CarPurchased, instanceId, car.Price, $"{car.Name} ({car.Id}) added to your garage.");
            result.Status = LocalOperationStatus.Applied;
            return Finish(result, p);
        }

        // ---------------- events ----------------

        /// <summary>
        /// Settles one completed local event: payout through Core Economy, stage clear (first-clear bonus and RP once per
        /// stage and mode), course unlocks from Normal clears, challenge rewards once, soundtrack cues from their declared
        /// source once, Team Trial scoring (no RP), and personal records (a legal losing run can set a PB).
        /// </summary>
        public static LocalProgressionResult ApplyEvent(LocalProfile profile, ContentCatalogue catalogue, IMusicUnlockSource music, LocalEventFacts facts)
        {
            LocalProgressionResult result = Begin(profile);
            if (catalogue == null) throw new ArgumentNullException(nameof(catalogue));
            if (facts == null) throw new ArgumentNullException(nameof(facts));
            music = music ?? MusicUnlockTable.Empty;

            if (!IsValidOperationId(facts.EventId)) return Reject(result, "A local event needs a unique event id.");
            if (profile.HasApplied(facts.EventId)) return Already(result, "This event's result was already applied.");
            string invalid = ValidateFacts(profile, catalogue, facts, out CourseDef course, out StageDef stage);
            if (invalid != null) return Reject(result, invalid);

            string denied;
            if (facts.Kind == EventKind.CampaignStage)
            {
                if (!CanStartCampaignStage(profile, catalogue, stage.Id, facts.Mode, out denied)) return Reject(result, denied);
            }
            // A challenge trial supplies its course as it supplies its loaner (spec §11: every challenge without a purchase);
            // the trial id was validated above against the trial's course and car.
            else if (facts.Kind != EventKind.Tutorial && string.IsNullOrEmpty(facts.TrialId) && !CanStartFreeplay(profile, catalogue, course.Id, out denied))
                return Reject(result, denied);

            LocalProfile p = ProfileJson.Clone(profile);
            DateTime utc = facts.CompletedUtc;
            p.ContentHash = catalogue.ContentHash ?? "";

            if (facts.Kind == EventKind.CampaignStage && !facts.FeaturedRivalStarted)
            {
                MarkApplied(p, facts.EventId);
                result.Status = LocalOperationStatus.Aborted;
                result.Reason = "The featured rival never started, so the event was aborted: no results, rewards or progression. Retry any time.";
                return Finish(result, p);
            }

            bool campaign = facts.Kind == EventKind.CampaignStage;
            var payoutFacts = new PayoutFacts
            {
                AuthoredExpectedSeconds = course.ExpectedSeconds, // trusted catalogue value, never elapsed time
                Kind = facts.Kind,
                Mode = campaign ? facts.Mode : CampaignMode.Normal,
                Outcome = facts.Outcome,
                Placement = facts.Placement,
                ReferenceBeaten = facts.Kind == EventKind.FreeplayTimeTrial && facts.Outcome == RunOutcome.Finished &&
                                  RaceClassification.ToReportedMillis(facts.FinishTimeMicros) <= (facts.FreeplayReferenceMs ?? course.ExpectedSeconds * 1000L),
                Clean = facts.Clean,
                UtilityIncomePercent = facts.UtilityIncomePercent,
                // One local human and no authenticated opponents: the pure-PvP bonus can never apply offline.
                PvPWinnerBonusEligible = false,
                CheckpointFraction = facts.CheckpointFraction,
                ServerVerifiedActiveProgress = facts.ActiveProgressVerified,
                TutorialRepeat = course.Kind == "tutorial",
            };
            string withheld = null;
            var newCosmetics = new List<string>();

            // ---- campaign stage: Core StageOutcome with H = 1, then first clear / course unlocks / soundtrack
            if (campaign)
            {
                var human = new HumanStageResult
                {
                    PlayerId = "local", Outcome = facts.Outcome, ActivelyDroveLegalCourse = facts.ActivelyDroveLegalCourse,
                    FinishTimeMs = RaceClassification.ToReportedMillis(facts.FinishTimeMicros), RawDriftScore = facts.RawDriftScore,
                    ContractsPassed = facts.ContractsPassed, BeatFeaturedRival = facts.BeatFeaturedRival,
                };
                StageResolution resolution = StageOutcome.Resolve(facts.Mode, facts.Benchmark, 1, new[] { human });
                PlayerStageVerdict v = resolution.Players[0];
                result.Stage = new StageVerdictSummary
                {
                    StageId = stage.Id, Mode = facts.Mode, Qualified = v.Qualified, WithinSupport = v.WithinSupport, EarnedClear = v.EarnedClear,
                    RequiresBeatingFeaturedRival = facts.Benchmark.RequiresBeatingFeaturedRival, BeatFeaturedRival = facts.BeatFeaturedRival, Reason = v.Reason,
                };
                if (v.EarnedClear)
                {
                    bool[] flags = p.Campaign.Flags(facts.Mode);
                    bool first = CampaignProgress.ApplyClear(flags, stage.Number); // access was checked: never beyond the frontier
                    result.Stage.FirstClear = first;
                    if (first)
                    {
                        List<int> list = p.Campaign.For(facts.Mode);
                        list.Add(stage.Number);
                        list.Sort();
                        payoutFacts.FirstClearBonus = Economy.FirstClearBonus(ParseStageType(stage.Type), facts.Mode);
                        int rp = facts.Mode == CampaignMode.Hard ? RankPoints.HardFirstClear : RankPoints.NormalFirstClear;
                        Add(result, ProgressionChangeKind.StageCleared, stage.Id, 0, $"First {ModeName(facts.Mode)} clear of {stage.Id}.");
                        Add(result, ProgressionChangeKind.RankPoints, stage.Id, rp, $"First {ModeName(facts.Mode)} clear.");
                    }
                    else
                    {
                        result.Notes.Add("Stage already cleared: ordinary race money only, no first-clear bonus or RP.");
                    }
                    if (facts.Mode == CampaignMode.Normal)
                        foreach (string courseId in CourseAccess.GrantedByNormalClear(catalogue, stage.Id))
                            GrantCourseFromClear(p, result, catalogue, courseId, stage.Id, first, utc);
                    foreach (MusicUnlockRule rule in music.ForStageClear(stage.Id, facts.Mode))
                        GrantCue(p, result, rule, facts.EventId, utc);
                }
                else if (facts.Outcome == RunOutcome.Finished)
                {
                    result.Notes.Add($"Stage not cleared: {v.Reason} Your time still counts for personal records.");
                }
            }

            // ---- Team Trial: Core TeamTrials scoring; completion pay for the eligible human only; never mastery RP
            if (facts.TeamTrial != null)
            {
                LocalTeamTrialFacts t = facts.TeamTrial;
                TeamScore mine = TeamTrials.Score(t.Kind, t.PlayerSide, t.HardTimeoutMs);
                TeamScore theirs = TeamTrials.Score(t.Kind, t.OpposingSide, t.HardTimeoutMs);
                TeamTrialVerdict verdict = TeamTrials.Compare(mine, theirs);
                TeamMemberResult me = t.PlayerSide.First(m => m.EntrantId == t.LocalEntrantId);
                bool payable = TeamTrials.HumanCompletionPayable(t.Kind, me, t.PlayerSide, t.ParticipationEnvelopeMs);
                payoutFacts.Placement = verdict == TeamTrialVerdict.PlayerTeamWins ? t.VictoryPlacement : t.DefeatPlacement;
                if (!payable)
                    withheld = me.Outcome != RunOutcome.Finished
                        ? "Team Trials pay completion money only to active eligible human finishers."
                        : "No human finished within the participation envelope, so no completion pay (an AI win is not a human payout).";
                result.TeamTrial = new TeamTrialSummary
                {
                    TrialId = t.TrialId, Kind = t.Kind, Verdict = verdict, PlayerTeamValue = mine.Value, OpposingTeamValue = theirs.Value,
                    CompletionPayable = payable, Note = "Team Trials award no mastery RP.",
                };
                if (payable && verdict == TeamTrialVerdict.PlayerTeamWins)
                    foreach (MusicUnlockRule rule in music.ForTrialVictory(t.TrialId))
                        GrantCue(p, result, rule, facts.EventId, utc);
                bool hasValue = t.Kind != TeamTrialKind.Best || mine.Value != long.MaxValue;
                if (t.TeamRecordKey != null)
                {
                    RecordKey teamKey = t.TeamRecordKey;
                    string problem = TeamKeyProblem(teamKey, t, course);
                    if (problem != null) result.Notes.Add("Team record skipped: " + problem);
                    else
                    {
                        bool valid = payable && hasValue && Metrics.Get(teamKey.Metric).IsValidValue(mine.Value);
                        p.Records.RegisterAttempt(teamKey, facts.Outcome, valid, utc);
                        if (valid) OfferRecord(p, result, teamKey, mine.Value, facts, catalogue, utc);
                    }
                }
            }

            // ---- Freeplay rival archetypes (CH38, CH73): the authored rivals raced, a quit clearing the lead archetypes beaten
            if (facts.Kind != EventKind.CampaignStage && facts.Kind != EventKind.Tutorial && facts.TeamTrial == null && facts.OpposingAi?.Count > 0)
            {
                var archetypes = new ArchetypeState();
                archetypes.Raced.UnionWith(p.ArchetypesRaced ?? new List<string>());
                archetypes.WonStreak.UnionWith(p.ArchetypeWinStreak ?? new List<string>());
                ArchetypeChallenges.Apply(archetypes, catalogue, new ArchetypeRace
                {
                    AiRivals = facts.OpposingAi, Outcome = facts.Outcome, Placement = facts.Placement, Tied = facts.Tied,
                });
                p.ArchetypesRaced = archetypes.Raced.OrderBy(x => x, StringComparer.Ordinal).ToList();
                p.ArchetypeWinStreak = archetypes.WonStreak.OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (facts.Outcome == RunOutcome.Finished)
                    foreach (string id in ArchetypeChallenges.Satisfied(archetypes))
                        if (!facts.ChallengesCompleted.Contains(id) && !p.HasCompletedChallenge(id)) facts.ChallengesCompleted.Add(id);
            }

            // ---- challenge trials: a passed trial is kept; its challenge once every trial of its group is passed
            if (!string.IsNullOrEmpty(facts.TrialId) && facts.TrialPassed && facts.Outcome == RunOutcome.Finished)
            {
                ChallengeTrialDef trial = catalogue.ChallengeTrials.Find(facts.TrialId);
                var passed = new HashSet<string>(p.TrialsPassed ?? new List<string>(), StringComparer.Ordinal) { trial.Id };
                p.TrialsPassed = passed.OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (TrialJudge.ChallengeEarned(catalogue.ChallengeTrials, trial.Challenge, passed) &&
                    !facts.ChallengesCompleted.Contains(trial.Challenge) && !p.HasCompletedChallenge(trial.Challenge))
                    facts.ChallengesCompleted.Add(trial.Challenge);
            }

            // ---- cumulative challenges (CH66, CH71) from every course this profile has legally finished, this one included
            if (facts.Outcome == RunOutcome.Finished)
            {
                var finished = new HashSet<string>(p.Records.Entries.Where(r => r?.Key != null).Select(r => r.Key.CourseId), StringComparer.Ordinal) { facts.CourseId };
                if (p.Tutorial.Completed || facts.Kind == EventKind.Tutorial) finished.Add("T00");
                foreach (string id in CumulativeChallenges.Satisfied(catalogue, finished))
                    if (!facts.ChallengesCompleted.Contains(id) && !p.HasCompletedChallenge(id)) facts.ChallengesCompleted.Add(id);
            }

            // ---- challenges: personal predicates only, valid finish only, each reward exactly once
            long challengeCash = 0;
            var challengeLines = new List<KeyValuePair<string, long>>();
            if (facts.ChallengesCompleted.Count > 0)
            {
                if (facts.Outcome != RunOutcome.Finished)
                    result.Notes.Add("Challenge claims ignored: challenges require a valid finish.");
                else
                    foreach (string id in facts.ChallengesCompleted)
                    {
                        if (p.HasCompletedChallenge(id))
                        {
                            result.Notes.Add($"{id} was already completed; no repeat reward.");
                            continue;
                        }
                        ChallengeDef ch = catalogue.Challenge(id);
                        ChallengeTier tier = ParseTier(ch.Tier);
                        long cash = RankPoints.ChallengeCash(tier);
                        int rp = RankPoints.ForChallenge(tier);
                        p.Challenges.Add(new CompletedChallenge { ChallengeId = id, Tier = tier, EventId = facts.EventId, CompletedUtc = utc });
                        challengeCash += cash;
                        challengeLines.Add(new KeyValuePair<string, long>(id, cash));
                        Add(result, ProgressionChangeKind.ChallengeCompleted, id, cash, $"{ch.Name} ({ch.Tier}).");
                        Add(result, ProgressionChangeKind.RankPoints, id, rp, $"{ch.Tier} challenge.");
                        if (!string.IsNullOrEmpty(ch.Reward) && !p.OwnsCosmetic(ch.Reward))
                        {
                            p.Cosmetics.Add(new OwnedCosmetic { CosmeticId = ch.Reward, Source = id, AcquiredUtc = utc });
                            newCosmetics.Add(ch.Reward);
                            string cosmeticName = catalogue.TryCosmetic(ch.Reward, out CosmeticDef cos) ? cos.Name : ch.Reward;
                            Add(result, ProgressionChangeKind.CosmeticGranted, ch.Reward, 0, $"{cosmeticName} (reward for {id}).");
                        }
                    }
            }
            payoutFacts.NewlyCompletedChallengeCash = challengeCash;

            // ---- payout: Core Economy (or withheld Team Trial completion pay), clamped per line at the wallet cap
            PayoutBreakdown payout = withheld != null
                ? new PayoutBreakdown { Note = withheld, ChallengeCash = challengeCash }
                : Economy.Compute(payoutFacts);
            result.Payout = payout;
            Credit(p, result, "event", facts.EventId, payout.EventCredits, utc, EventCreditDetail(payout, facts));
            if (payout.FirstClearBonus > 0)
                Credit(p, result, "first-clear", stage.Id, payout.FirstClearBonus, utc, $"First-clear bonus ({ModeName(facts.Mode)} {stage.Id}).");
            foreach (KeyValuePair<string, long> line in challengeLines)
                Credit(p, result, "challenge", line.Key, line.Value, utc, $"Challenge reward ({line.Key}).");

            // ---- tutorial: 3,000 once; repetitions never pay
            if (facts.Kind == EventKind.Tutorial && facts.Outcome == RunOutcome.Finished)
            {
                if (!p.Tutorial.Completed)
                {
                    p.Tutorial.Completed = true;
                    p.Tutorial.CompletedUtc = utc;
                    Add(result, ProgressionChangeKind.TutorialCompleted, course.Id, 0, "Tutorial completed.");
                    Credit(p, result, "tutorial", course.Id, Limits.TutorialCompletionCredits, utc, "Tutorial completion award (once).");
                    payout.Note = "Tutorial completion award (once).";
                }
                else result.Notes.Add("Tutorial repetitions do not pay.");
            }

            // ---- personal records: independent of placing and stage clear; Local / Unverified only
            foreach (RecordCandidate rc in facts.Records ?? new List<RecordCandidate>())
            {
                string problem = CandidateProblem(rc, facts, course, stage);
                if (problem != null)
                {
                    result.Notes.Add("Record skipped: " + problem);
                    continue;
                }
                bool valid = facts.Outcome == RunOutcome.Finished && rc.Value.HasValue && Metrics.Get(rc.Key.Metric).IsValidValue(rc.Value.Value);
                p.Records.RegisterAttempt(rc.Key, facts.Outcome, valid, utc);
                if (valid) OfferRecord(p, result, rc.Key, rc.Value.Value, facts, catalogue, utc);
            }

            MarkApplied(p, facts.EventId);
            result.Status = LocalOperationStatus.Applied;
            return Finish(result, p);
        }

        // ---------------- rank ----------------

        public static int RankPointsOf(LocalProfile profile) => profile.ComputeRankPoints();

        public static RankDefinition RankOf(LocalProfile profile) => profile.ComputeRank();

        // ---------------- helpers ----------------

        static string ValidateFacts(LocalProfile profile, ContentCatalogue catalogue, LocalEventFacts f, out CourseDef course, out StageDef stage)
        {
            course = null;
            stage = null;
            if (!Enum.IsDefined(typeof(EventKind), f.Kind)) return "Unknown event kind.";
            if (!Enum.IsDefined(typeof(RunOutcome), f.Outcome)) return "Unknown outcome.";
            if (!catalogue.TryCourse(f.CourseId, out course)) return $"Unknown course '{f.CourseId}'.";
            if (f.CompletedUtc == default(DateTime)) return "The event needs its completion time.";
            if (f.Outcome == RunOutcome.Finished ? f.FinishTimeMicros <= 0 : f.FinishTimeMicros < 0) return "Invalid finish time.";
            if (double.IsNaN(f.CheckpointFraction) || double.IsInfinity(f.CheckpointFraction) || f.CheckpointFraction < 0 || f.CheckpointFraction > 1)
                return "Checkpoint fraction must be 0..1.";
            if (f.RawDriftScore < 0 || f.ContractsPassed < 0 || f.ContractsPassed > 4) return "Invalid drift score or contract count.";
            if (f.UtilityIncomePercent != 0 && f.UtilityIncomePercent != 4 && f.UtilityIncomePercent != 8) return "Only +4% or +8% income utilities exist.";
            if (f.ChallengesCompleted == null) return "Challenge list missing.";
            if (f.OpposingAi != null && (f.OpposingAi.Count >= Limits.MaxRaceVehicles || f.OpposingAi.Any(string.IsNullOrEmpty) ||
                                         f.OpposingAi.Distinct(StringComparer.Ordinal).Count() != f.OpposingAi.Count))
                return "Invalid opponent list.";
            if (f.ChallengesCompleted.Distinct(StringComparer.Ordinal).Count() != f.ChallengesCompleted.Count) return "Duplicate challenge claim.";
            foreach (string id in f.ChallengesCompleted)
                if (!catalogue.Challenges.Any(c => c.Id == id)) return $"Unknown challenge '{id}'.";

            if (string.IsNullOrEmpty(f.CarModelId) || !catalogue.TryCar(f.CarModelId, out _)) return "The event needs the car model driven.";
            if (!string.IsNullOrEmpty(f.TrialId))
            {
                ChallengeTrialDef trial = catalogue.ChallengeTrials.Find(f.TrialId);
                if (trial == null) return $"Unknown challenge trial '{f.TrialId}'.";
                if ((trial.IsCup ? !trial.Legs.Any(l => l.Course == f.CourseId) : f.CourseId != trial.Course) || (trial.IsRace
                        ? f.Kind != EventKind.FreeplaySprint && f.Kind != EventKind.FreeplayCircuit
                        : f.Kind != EventKind.FreeplayTimeTrial))
                    return $"{trial.Id} runs as a {(trial.IsRace ? "race against its field" : "time trial")} on {trial.Course}.";
                if (!f.Loaner || f.CarModelId != trial.Loaner.Car) return $"{trial.Id} is driven in its supplied {trial.Loaner.Car}.";
            }
            else if (f.TrialPassed) return "A trial pass needs its trial id.";
            if (string.IsNullOrEmpty(f.CarInstanceId))
            {
                if (!f.Loaner) return "Name the owned car instance driven, or mark the run as a loaner.";
            }
            else
            {
                if (f.Loaner) return "A loaner run does not use an owned car instance.";
                OwnedCar car = profile.FindCar(f.CarInstanceId);
                if (car == null) return $"Car instance {f.CarInstanceId} is not in this Local garage.";
                if (car.ModelId != f.CarModelId) return $"Car instance {f.CarInstanceId} is a {car.ModelId}, not a {f.CarModelId}.";
            }

            bool placed = f.Outcome == RunOutcome.Finished && f.Kind != EventKind.FreeplayTimeTrial && f.Kind != EventKind.Tutorial && f.TeamTrial == null;
            if (placed && (f.Placement < 1 || f.Placement > Limits.MaxRaceVehicles)) return $"Placement must be 1..{Limits.MaxRaceVehicles}.";

            switch (f.Kind)
            {
                case EventKind.CampaignStage:
                    stage = catalogue.Stages.FirstOrDefault(s => s.Id == f.StageId);
                    if (stage == null) return $"Unknown stage '{f.StageId}'.";
                    if (stage.Course != course.Id) return $"{stage.Id} runs on {stage.Course}, not {course.Id}.";
                    if (!Enum.IsDefined(typeof(CampaignMode), f.Mode)) return "Unknown campaign mode.";
                    if (f.Benchmark == null) return "A campaign event needs its frozen benchmark.";
                    if (f.TeamTrial != null) return "A campaign stage is not a Team Trial.";
                    break;
                case EventKind.Tutorial:
                    if (course.Kind != "tutorial") return "Tutorial events run on the tutorial course.";
                    if (f.StageId != null || f.TeamTrial != null) return "A tutorial event has no stage or trial.";
                    break;
                default:
                    if (course.Kind == "tutorial") return "The tutorial course is not a Freeplay course.";
                    if (f.StageId != null) return "Only campaign events name a stage.";
                    if (f.TeamTrial != null)
                    {
                        string trialProblem = TrialProblem(f);
                        if (trialProblem != null) return trialProblem;
                    }
                    break;
            }
            return null;
        }

        static string TrialProblem(LocalEventFacts f)
        {
            LocalTeamTrialFacts t = f.TeamTrial;
            if (f.Kind != EventKind.FreeplaySprint && f.Kind != EventKind.FreeplayCircuit && f.Kind != EventKind.FreeplayDriftAttack)
                return "Team Trials run as Sprint, Circuit or Drift Attack events.";
            if (string.IsNullOrEmpty(t.TrialId)) return "A Team Trial needs its trial id.";
            if (!Enum.IsDefined(typeof(TeamTrialKind), t.Kind)) return "Unknown Team Trial kind.";
            if (t.PlayerSide == null || t.OpposingSide == null ||
                t.PlayerSide.Count != Limits.TeamTrialSideSize || t.OpposingSide.Count != Limits.TeamTrialSideSize)
                return $"A Team Trial has exactly {Limits.TeamTrialSideSize} positions per team.";
            if (t.PlayerSide.Concat(t.OpposingSide).Any(m => m == null || string.IsNullOrEmpty(m.EntrantId)))
                return "Every Team Trial position needs an entrant id.";
            if (t.PlayerSide.Concat(t.OpposingSide).Select(m => m.EntrantId).Distinct(StringComparer.Ordinal).Count() != 2 * Limits.TeamTrialSideSize)
                return "Duplicate Team Trial entrant.";
            List<TeamMemberResult> humans = t.PlayerSide.Where(m => m.Human).ToList();
            if (humans.Count != 1 || humans[0].EntrantId != t.LocalEntrantId) return "Offline Team Trials have exactly one human: the local player.";
            if (t.OpposingSide.Any(m => m.Human)) return "The opposing Team Trial side is all AI.";
            if (humans[0].Outcome != f.Outcome) return "The local player's Team Trial outcome disagrees with the event outcome.";
            if (t.HardTimeoutMs <= 0 || t.ParticipationEnvelopeMs < 0) return "Team Trial timeouts must be positive.";
            if (t.VictoryPlacement < 1 || t.VictoryPlacement > Limits.MaxRaceVehicles || t.DefeatPlacement < 1 || t.DefeatPlacement > Limits.MaxRaceVehicles)
                return $"Team Trial placement modifiers must be 1..{Limits.MaxRaceVehicles}.";
            return null;
        }

        static string CandidateProblem(RecordCandidate rc, LocalEventFacts f, CourseDef course, StageDef stage)
        {
            if (rc?.Key == null) return "no key.";
            RecordKey k = rc.Key;
            if (k.Domain != ProgressionDomain.Local) return RecordCompatibility.Explain(RecordMismatch.Domain);
            IReadOnlyList<string> errors = k.Validate();
            if (errors.Count > 0) return errors[0];
            if (Metrics.Get(k.Metric).IsTeamMetric) return "team results are recorded from the Team Trial score, not submitted.";
            if (k.CourseId != course.Id) return $"key names {k.CourseId}, but the event ran on {course.Id}.";
            switch (f.Kind)
            {
                case EventKind.CampaignStage:
                    if (k.EventType != RecordEventType.CampaignStage || k.EventId != stage.Id) return $"key is not for {stage.Id}.";
                    if (k.Difficulty != (f.Mode == CampaignMode.Hard ? RecordKey.HardDifficulty : RecordKey.NormalDifficulty))
                        return RecordCompatibility.Explain(RecordMismatch.Difficulty);
                    break;
                case EventKind.Tutorial:
                    if (k.EventType != RecordEventType.Tutorial) return "key is not a tutorial record.";
                    return null; // tutorial demonstrations may use either contact policy
                default:
                    if (f.TeamTrial != null)
                    {
                        if (k.EventType != RecordEventType.TeamTrial || k.EventId != f.TeamTrial.TrialId) return $"key is not for {f.TeamTrial.TrialId}.";
                    }
                    else if (k.EventType != RecordEventType.Freeplay || k.EventId != course.Id + "/" + k.Format)
                        return "key is not a Freeplay record for this course and format.";
                    break;
            }
            ContactPolicy expected = f.Kind == EventKind.FreeplayTimeTrial ? ContactPolicy.NonContact : ContactPolicy.LightContact;
            if (k.Contact != expected) return RecordCompatibility.Explain(RecordMismatch.ContactPolicy);
            return null;
        }

        static string TeamKeyProblem(RecordKey k, LocalTeamTrialFacts t, CourseDef course)
        {
            if (k.Domain != ProgressionDomain.Local) return RecordCompatibility.Explain(RecordMismatch.Domain);
            IReadOnlyList<string> errors = k.Validate();
            if (errors.Count > 0) return errors[0];
            if (k.Metric != Metrics.ForTeamTrial(t.Kind)) return "the team metric does not match the trial kind.";
            if (k.EventType != RecordEventType.TeamTrial || k.EventId != t.TrialId) return $"key is not for {t.TrialId}.";
            if (k.CourseId != course.Id) return $"key names {k.CourseId}, but the trial ran on {course.Id}.";
            if (k.TeamHumans != 1) return "offline Team Trials are recorded with one human.";
            if (k.Difficulty != (t.Difficulty ?? "")) return RecordCompatibility.Explain(RecordMismatch.Difficulty);
            if (k.Contact != ContactPolicy.LightContact) return RecordCompatibility.Explain(RecordMismatch.ContactPolicy);
            return null;
        }

        static void OfferRecord(LocalProfile p, LocalProgressionResult result, RecordKey key, long value, LocalEventFacts f, ContentCatalogue catalogue, DateTime utc)
        {
            var entry = new RecordEntry
            {
                Key = key.Clone(),
                Value = value,
                CarModelId = f.CarModelId,
                CarInstanceId = f.Loaner ? "" : f.CarInstanceId ?? "",
                BuildId = !string.IsNullOrEmpty(f.BuildId) ? f.BuildId : f.Loaner ? "loaner:" + f.CarModelId : "",
                Loaner = f.Loaner,
                Verification = RecordVerification.LocalUnverified,
                Provenance = new RecordProvenance
                {
                    EventInstanceId = f.EventId, AchievedUtc = utc, RulesVersion = catalogue.ContentHash ?? "", Origin = RecordEntry.LocalOrigin,
                },
            };
            RecordUpdateResult r = p.Records.Offer(entry);
            result.Records.Add(r);
            string text = RecordFormat.Value(key.Metric, value);
            string name = Metrics.Get(key.Metric).Name;
            switch (r.Outcome)
            {
                case RecordUpdateOutcome.NewPersonalBest:
                    Add(result, ProgressionChangeKind.PersonalBest, key.EventId, value, $"New personal best — {name}: {text} ({DomainNotices.LocalBadge}).");
                    break;
                case RecordUpdateOutcome.NewCarBest:
                    Add(result, ProgressionChangeKind.CarBest, key.EventId, value, $"Best with the {f.CarModelId} — {name}: {text}.");
                    break;
                case RecordUpdateOutcome.Tie:
                    Add(result, ProgressionChangeKind.RecordTie, key.EventId, value, $"Equalled your personal best — {name}: {text}.");
                    break;
                case RecordUpdateOutcome.Rejected:
                    result.Notes.Add("Record skipped: " + r.Reason);
                    break;
            }
        }

        static void GrantCourseFromClear(LocalProfile p, LocalProgressionResult result, ContentCatalogue catalogue, string courseId, string stageId, bool firstClear, DateTime utc)
        {
            CourseEntitlement existing = p.Courses.FirstOrDefault(c => c.CourseId == courseId);
            string name = catalogue.TryCourse(courseId, out CourseDef course) ? course.Name : courseId;
            if (existing == null)
            {
                p.Courses.Add(new CourseEntitlement { CourseId = courseId, Source = CourseEntitlementSource.CampaignClear, Reference = stageId, AcquiredUtc = utc });
                Add(result, ProgressionChangeKind.CourseUnlocked, courseId, 0, $"{name}: permanent access from your Normal {stageId} clear.");
            }
            else if (existing.Source == CourseEntitlementSource.Purchased && firstClear)
            {
                result.Notes.Add($"You already owned {name} (bought early); clearing keeps it — no refund or duplicate reward.");
            }
        }

        static void GrantCue(LocalProfile p, LocalProgressionResult result, MusicUnlockRule rule, string eventId, DateTime utc)
        {
            if (p.HasCue(rule.CueId)) return;
            p.Music.Add(new MusicUnlock { CueId = rule.CueId, SourceKind = rule.Kind, SourceRef = rule.SourceRef, EventId = eventId ?? "", UnlockedUtc = utc });
            Add(result, ProgressionChangeKind.MusicUnlocked, rule.CueId, 0,
                rule.Kind == MusicUnlockSourceKind.Baseline ? "Soundtrack: available from the start." : $"Soundtrack unlocked ({MusicUnlockRule.Token(rule.Kind)} {rule.SourceRef}).");
        }

        static void GrantBaseline(LocalProfile p, LocalProgressionResult result, IMusicUnlockSource music, DateTime utc)
        {
            foreach (MusicUnlockRule rule in music.Baseline())
                GrantCue(p, result, rule, "", utc);
        }

        static void Credit(LocalProfile p, LocalProgressionResult result, string kind, string reference, long amount, DateTime utc, string detail)
        {
            if (amount <= 0) return;
            WalletCredit credit = Wallet.Credit(p.WalletBalance, amount); // Core semantics: clamp at the cap, report the loss
            p.WalletBalance = credit.NewBalance;
            History(p, utc, kind, reference, amount, credit.Credited, credit.ClampedAway);
            Add(result, ProgressionChangeKind.Credits, reference, credit.Credited, detail);
            if (credit.ClampedAway > 0)
            {
                Add(result, ProgressionChangeKind.CreditsClamped, reference, credit.ClampedAway,
                    $"Wallet cap {Limits.WalletCap.ToString("N0", CultureInfo.InvariantCulture)} reached: {credit.ClampedAway.ToString("N0", CultureInfo.InvariantCulture)} credits could not be added.");
            }
        }

        internal static void History(LocalProfile p, DateTime utc, string kind, string reference, long amount, long applied, long clamped)
        {
            p.WalletHistory.Add(new WalletEntry
            {
                Utc = utc, Kind = kind, Reference = reference ?? "", Amount = amount, Applied = applied, ClampedAway = clamped, BalanceAfter = p.WalletBalance,
            });
            int excess = p.WalletHistory.Count - LocalProfile.MaxWalletHistory;
            if (excess > 0) p.WalletHistory.RemoveRange(0, excess);
        }

        static string EventCreditDetail(PayoutBreakdown b, LocalEventFacts f)
        {
            if (!string.IsNullOrEmpty(b.Note)) return b.Note;
            return f.Kind == EventKind.FreeplayTimeTrial
                ? FormattableString.Invariant($"Event pay: base {b.Base:N0} × time band {b.PlacementX100 / 100.0:0.00}.")
                : FormattableString.Invariant(
                    $"Event pay: base {b.Base:N0} × difficulty {b.DifficultyX100 / 100.0:0.00} × placement {b.PlacementX100 / 100.0:0.00} × clean {b.CleanlinessX100 / 100.0:0.00}.");
        }

        internal static void MarkApplied(LocalProfile p, string operationId)
        {
            p.AppliedOperations.Add(operationId);
            int excess = p.AppliedOperations.Count - LocalProfile.MaxAppliedOperations;
            if (excess > 0) p.AppliedOperations.RemoveRange(0, excess);
        }

        /// <summary>
        /// A meet touring challenge (CH61–CH65) completed at the offline meet: unlock, RP, cosmetic and cash exactly once, as
        /// the online meet room grants them. Only the touring family completes here — race challenges come from race facts.
        /// </summary>
        public static LocalProgressionResult CompleteMeetChallenge(LocalProfile profile, ContentCatalogue catalogue, string challengeId, DateTime utc) =>
            // The meet's touring challenges, and CH48 (a workshop challenge whose last step — the signed car seen parked — is at the meet).
            CompleteOutsideRace(profile, catalogue, challengeId, utc, "meet", ch => ch.Family == "touring" || ch.Id == Customization.LiveryChallenges.SignYourCar,
                "Only the meet's touring challenges complete at the meet.");

        /// <summary>
        /// A workshop challenge completed in the Local Garage (CH50: presets switched and the first restored exactly), granted
        /// exactly once like the online Garage grants it. Only those complete here.
        /// </summary>
        public static LocalProgressionResult CompleteGarageChallenge(LocalProfile profile, ContentCatalogue catalogue, string challengeId, DateTime utc) =>
            CompleteOutsideRace(profile, catalogue, challengeId, utc, "garage", ch => ch.Id == Customization.LiveryChallenges.ChangeWithoutLosing,
                "Only the Garage's workshop challenges complete in the Garage.");

        static LocalProgressionResult CompleteOutsideRace(LocalProfile profile, ContentCatalogue catalogue, string challengeId, DateTime utc, string where,
            Func<ChallengeDef, bool> allowed, string refusal)
        {
            LocalProgressionResult result = Begin(profile);
            if (catalogue == null) throw new ArgumentNullException(nameof(catalogue));
            if (!catalogue.TryChallenge(challengeId ?? "", out ChallengeDef ch) || !allowed(ch)) return Reject(result, refusal);
            if (profile.HasCompletedChallenge(challengeId)) return Already(result, $"{challengeId} was already completed; no repeat reward.");
            ChallengeTier tier = ParseTier(ch.Tier);
            long cash = RankPoints.ChallengeCash(tier);
            int rp = RankPoints.ForChallenge(tier);
            profile.Challenges.Add(new CompletedChallenge { ChallengeId = ch.Id, Tier = tier, EventId = where, CompletedUtc = utc });
            Add(result, ProgressionChangeKind.ChallengeCompleted, ch.Id, cash, $"{ch.Name} ({ch.Tier}).");
            Add(result, ProgressionChangeKind.RankPoints, ch.Id, rp, $"{ch.Tier} challenge.");
            if (!string.IsNullOrEmpty(ch.Reward) && !profile.OwnsCosmetic(ch.Reward))
            {
                profile.Cosmetics.Add(new OwnedCosmetic { CosmeticId = ch.Reward, Source = ch.Id, AcquiredUtc = utc });
                string cosmeticName = catalogue.TryCosmetic(ch.Reward, out CosmeticDef cos) ? cos.Name : ch.Reward;
                Add(result, ProgressionChangeKind.CosmeticGranted, ch.Reward, 0, $"{cosmeticName} (reward for {ch.Id}).");
            }
            Credit(profile, result, "challenge", ch.Id, cash, utc, $"{ch.Name} ({ch.Tier}).");
            result.Status = LocalOperationStatus.Applied;
            return Finish(result, profile);
        }

        /// <summary>
        /// Marks a race-diary entry read — only a crew introduction the profile has opened (its crew's stage cleared on Normal),
        /// as the control plane records it online. A repeat is a no-op.
        /// </summary>
        public static LocalProgressionResult MarkDiaryRead(LocalProfile profile, ContentCatalogue catalogue, IReadOnlyList<Story.CrewIntroduction> crews, string entry)
        {
            LocalProgressionResult result = Begin(profile);
            if (catalogue == null) throw new ArgumentNullException(nameof(catalogue));
            var number = catalogue.Stages.ToDictionary(s => s.Id, s => s.Number);
            bool Cleared(string stageId) => number.TryGetValue(stageId ?? "", out int n) && profile.Campaign.For(CampaignMode.Normal).Contains(n);
            if (!Story.DiaryChallenges.Unlocked(entry, crews, Cleared))
                return Reject(result, "That entry opens when you clear its crew's stage on Normal.");
            if (profile.DiaryRead == null) profile.DiaryRead = new List<string>();
            if (profile.DiaryRead.Contains(entry)) return Already(result, "Already read.");
            profile.DiaryRead.Add(entry);
            Add(result, ProgressionChangeKind.DiaryRead, entry, 0, "Read in the race diary.");
            result.Status = LocalOperationStatus.Applied;
            return Finish(result, profile);
        }

        /// <summary>
        /// Marks a T00 lesson passed (spec §16): training progress only — no money, rank or unlock, so a lesson is never a
        /// gate before a first race. Unknown lessons are refused; a repeat pass changes nothing.
        /// </summary>
        public static LocalProgressionResult MarkLessonPassed(LocalProfile profile, Tutorial.TutorialLessons lessons, string lessonId)
        {
            LocalProgressionResult result = Begin(profile);
            if (lessons?.Find(lessonId ?? "") == null) return Reject(result, "No such lesson.");
            if (profile.Tutorial == null) profile.Tutorial = new TutorialState();
            if (profile.Tutorial.LessonsPassed == null) profile.Tutorial.LessonsPassed = new List<string>();
            if (profile.Tutorial.LessonsPassed.Contains(lessonId)) return Already(result, "Already passed.");
            profile.Tutorial.LessonsPassed.Add(lessonId);
            Add(result, ProgressionChangeKind.LessonPassed, lessonId, 0, "Lesson passed: " + lessons.Find(lessonId).Title + ".");
            result.Status = LocalOperationStatus.Applied;
            return Finish(result, profile);
        }

        /// <summary>Whether the profile has completed an eligible event (the touring challenge CH65 reads its result slip).</summary>
        public static bool HasCompletedEvent(LocalProfile p) =>
            p.Campaign.Count(CampaignMode.Normal) + p.Campaign.Count(CampaignMode.Hard) > 0 || p.WalletHistory.Any(w => w.Kind == "event" || w.Kind == "tutorial");

        internal static void Add(LocalProgressionResult result, ProgressionChangeKind kind, string subject, long amount, string detail) =>
            result.Changes.Add(new ProgressionChange { Kind = kind, Subject = subject ?? "", Amount = amount, Detail = detail ?? "" });

        internal static LocalProgressionResult Begin(LocalProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            var result = new LocalProgressionResult { Profile = profile, BalanceBefore = profile.WalletBalance };
            result.RankPointsBefore = profile.ComputeRankPoints();
            result.RankBefore = profile.ComputeRank().Name;
            result.BalanceAfter = result.BalanceBefore;
            result.RankPointsAfter = result.RankPointsBefore;
            result.RankAfter = result.RankBefore;
            return result;
        }

        internal static LocalProgressionResult Finish(LocalProgressionResult result, LocalProfile p)
        {
            result.Profile = p;
            result.BalanceAfter = p.WalletBalance;
            result.RankPointsAfter = p.ComputeRankPoints();
            result.RankAfter = p.ComputeRank().Name;
            return result;
        }

        internal static LocalProgressionResult Reject(LocalProgressionResult result, string reason)
        {
            result.Status = LocalOperationStatus.Rejected;
            result.Reason = reason ?? "";
            return result;
        }

        internal static LocalProgressionResult Already(LocalProgressionResult result, string reason)
        {
            result.Status = LocalOperationStatus.AlreadyApplied;
            result.Reason = reason ?? "";
            return result;
        }

        static string ModeName(CampaignMode mode) => mode == CampaignMode.Hard ? "Hard" : "Normal";

        static StageType ParseStageType(string type)
        {
            switch (type)
            {
                case "regular": return StageType.Regular;
                case "lieutenant": return StageType.Lieutenant;
                case "penultimate": return StageType.Penultimate;
                case "finale": return StageType.Finale;
                default: throw new ContentLoadException($"Unknown stage type '{type}'");
            }
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
    }
}
