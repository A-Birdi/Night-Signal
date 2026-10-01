using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Tokens;
using NightSignal.ControlPlane.Content;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Persistence;
using NightSignal.ControlPlane.Security;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;

namespace NightSignal.ControlPlane.Matches;

/// <summary>
/// Game-server result body: server-observed FACTS only. Money, Rank Points, stage clears and payout multipliers are
/// never accepted; unknown members (e.g. "credits") make the whole body invalid.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ResultSubmission
{
    public string MatchId { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public bool Aborted { get; set; }
    public string? AbortReason { get; set; }
    public List<EntrantFacts> Entrants { get; set; } = new();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EntrantFacts
{
    /// <summary>Account ID for humans; the assignment's AI entrant ID otherwise.</summary>
    public string EntrantId { get; set; } = "";
    public bool Human { get; set; }
    public RunOutcome Outcome { get; set; }
    /// <summary>Server race-clock finish time, integer microseconds (Finished only), including ordinary penalties.</summary>
    public long FinishTimeMicros { get; set; }
    /// <summary>Placing as the server classified it; cross-checked against Core RaceClassification.</summary>
    public int Placement { get; set; }
    public bool Clean { get; set; }
    public double CheckpointFraction { get; set; }
    public bool ActiveProgressVerified { get; set; }
    public bool ActivelyDroveLegalCourse { get; set; }
    public double LegalProgressMetres { get; set; }
    public long RawDriftScore { get; set; }
    public int ContractsPassed { get; set; }
    /// <summary>
    /// AI only (optional, default true): false when the AI never spawned/initialised. A live opponent that never started
    /// makes the event broken — it is aborted (no rewards), never a free win (Addendum 01 §1.3).
    /// </summary>
    public bool? Started { get; set; }
    /// <summary>Challenge predicates the game server evaluated as met in this event (personal performance only).</summary>
    public List<string> ChallengesCompleted { get; set; } = new();
    /// <summary>Challenge trials (humans only): the trial judged, whether Core TrialJudge passed this run, and its reasons.</summary>
    public string? TrialId { get; set; }
    public bool? TrialPassed { get; set; }
    public string? TrialSummary { get; set; }
}

public sealed record SubmissionResult(int StatusCode, object Body);

/// <summary>Verifies, recomputes (with NightSignal.Core) and settles a match result idempotently.</summary>
public sealed class SettlementService(IResultLedger ledger, IPlayerStore players, ContentService content, ConvoyDirectory convoys,
    GameServerRegistry registry, ILogger<SettlementService> log, MusicUnlockManifest? music = null)
{
    public const string SignatureHeader = "X-NightSignal-Signature";
    public const int MaxBodyBytes = 64 * 1024;

    static readonly JsonSerializerOptions ParseOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    MusicUnlockManifest Music => music ?? MusicUnlockManifest.Empty;

    /// <summary>signature = "sha256=" + lowercase hex HMAC-SHA256(base64url-decoded per-match secret, raw body bytes).</summary>
    public static string Sign(string resultsSecret, byte[] body) =>
        "sha256=" + Hashing.HmacSha256Hex(Base64UrlEncoder.DecodeBytes(resultsSecret), body);

    /// <summary>Largest ghost document accepted (a 15-minute event at 10 Hz fits).</summary>
    public const int MaxGhostBytes = 400 * 1024;

    /// <summary>
    /// A ghost the game server recorded for one human entrant (spec §8: server-generated): signed like the results with the
    /// match secret, well-formed, for this match's course; held until the match settles, when it is kept only if it matches
    /// the settled finish.
    /// </summary>
    public async Task<SubmissionResult> SubmitGhostAsync(string serverId, string matchId, string accountId, byte[] body, string? signature, CancellationToken ct)
    {
        MatchRecord? match = await ledger.GetMatchAsync(matchId, ct);
        if (match is null) return Error(404, "unknown_match", "No such match.");
        if (match.ServerId != serverId) return Error(403, "wrong_server", "This match was allocated to a different server.");
        if (signature is null || !Hashing.FixedTimeEquals(signature, Sign(match.ResultsSecret, body)))
            return Error(401, "bad_signature", "Ghost body signature is missing or invalid.");
        if (match.State != "allocated") return Error(409, "already_closed", "This match was already settled or aborted.");
        MatchAssignment config = JsonSerializer.Deserialize<MatchAssignment>(match.ConfigJson, MatchAllocator.Json)!;
        if (config.Entrants.All(e => e.AccountId != accountId)) return Error(422, "not_an_entrant", "That account is not a human entrant of this match.");
        string json = System.Text.Encoding.UTF8.GetString(body);
        NightSignal.Core.Ghosts.GhostRecording? ghost = NightSignal.Core.Ghosts.GhostRecording.Parse(json, out string? error);
        if (ghost is null) return Error(400, "malformed", error ?? "Not a ghost.");
        List<string> problems = ghost.Problems();
        if (problems.Count > 0) return Error(422, "invalid_ghost", string.Join("; ", problems));
        if (ghost.Header.CourseId != config.CourseId) return Error(422, "mismatch", "The ghost is for another course.");
        await ledger.StoreMatchGhostAsync(matchId, accountId, json, ct);
        return new SubmissionResult(200, new { status = "held", samples = ghost.Count });
    }

    public async Task<SubmissionResult> SubmitAsync(string serverId, string matchId, byte[] body, string? signature, CancellationToken ct)
    {
        MatchRecord? match = await ledger.GetMatchAsync(matchId, ct);
        if (match is null) return Error(404, "unknown_match", "No such match.");
        if (match.ServerId != serverId) return Error(403, "wrong_server", "This match was allocated to a different server.");
        if (signature is null || !Hashing.FixedTimeEquals(signature, Sign(match.ResultsSecret, body)))
            return Error(401, "bad_signature", "Result body signature is missing or invalid.");

        ResultSubmission? submission;
        try
        {
            submission = JsonSerializer.Deserialize<ResultSubmission>(body, ParseOptions);
        }
        catch (JsonException e)
        {
            return Error(400, "malformed", "Result body is not valid: " + e.Message);
        }
        MatchAssignment config = JsonSerializer.Deserialize<MatchAssignment>(match.ConfigJson, MatchAllocator.Json)!;
        if (submission is null || submission.MatchId != matchId || submission.ContentHash != config.ContentHash)
            return Error(422, "mismatch", "Match ID or content hash does not match the allocation.");
        // Addendum 02 D209: a toy (non-progression) kind never reaches the wallet/RP pipeline — nothing is settled or aborted.
        if (ProgressionDomain.ToyViolation(config) is { } toy)
        {
            log.LogWarning("Refused a toy-domain result for match {MatchId}", matchId);
            return Error(422, ProgressionDomain.ErrorCode, toy);
        }

        string? broken = submission.Aborted ? null : BrokenEventReason(config, submission);
        if (submission.Aborted || broken is not null)
        {
            if (!await ledger.MarkAbortedAsync(matchId, ct))
                return (await ledger.GetMatchAsync(matchId, ct))?.State == "aborted"
                    ? new SubmissionResult(200, new { status = "aborted", replayed = true })
                    : Error(409, "conflict", "This match was already settled; it cannot be aborted.");
            log.LogWarning("Match {MatchId} aborted ({Cause}) by {ServerId}; no results or progression issued", matchId,
                broken is null ? "reported by the server" : "live opponent never started", serverId);
            // No results were recorded, so no post-event decision opens; the convoy returns to selection with the reason.
            registry.MatchFinished(config.ServerId);
            convoys.MatchAborted(config.ConvoyId, config.MatchId, broken ??
                "The race server aborted the event: no results, rank or progression were issued; retry any time.");
            return new SubmissionResult(200, new { status = "aborted", reason = broken });
        }

        // Cumulative challenges read every earlier settled finish of each finishing human.
        var finished = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
        // Ghosts (spec §8): each finishing human's recording, valid only for this settled finish; CH68 against the kept one.
        var ghosts = new Dictionary<string, NightSignal.Core.Ghosts.GhostRecording>(StringComparer.Ordinal);
        var ghostBeats = new HashSet<string>(StringComparer.Ordinal);
        foreach (EntrantFacts e in submission.Entrants.Where(x => x.Human && x.Outcome == RunOutcome.Finished))
        {
            string? json = await ledger.MatchGhostAsync(matchId, e.EntrantId, ct);
            NightSignal.Core.Ghosts.GhostRecording? g = json is null ? null : NightSignal.Core.Ghosts.GhostRecording.Parse(json, out _);
            if (g is null || !g.ValidPersonal || g.Header.ResultMicros != e.FinishTimeMicros) continue;
            ghosts[e.EntrantId] = g;
            foreach (StoredGhost kept in await ledger.GhostsAsync(e.EntrantId, g.Header.CourseId, g.Header.Format, ct))
                if (NightSignal.Core.Ghosts.GhostChallenges.BeatsYesterday(NightSignal.Core.Ghosts.GhostRecording.Parse(kept.Json, out _), g))
                    ghostBeats.Add(e.EntrantId);
        }
        // CH38 / CH73 read each finishing human's settled Freeplay races (the archetypes raced, the wins since a quit).
        var freeplayBefore = new Dictionary<string, IReadOnlyList<ArchetypeRace>>(StringComparer.Ordinal);
        if (config.Kind == "freeplay")
            foreach (EntrantFacts e in submission.Entrants.Where(x => x.Human && x.Outcome == RunOutcome.Finished))
                freeplayBefore[e.EntrantId] = await ledger.FreeplayRacesAsync(e.EntrantId, ct);
        // CH70 reads each finishing human's race-diary marks (all six crew introductions read before this race).
        var diaryComplete = new HashSet<string>(StringComparer.Ordinal);
        foreach (EntrantFacts e in submission.Entrants.Where(x => x.Human && x.Outcome == RunOutcome.Finished))
        {
            finished[e.EntrantId] = await ledger.FinishedCoursesAsync(e.EntrantId, ct);
            if (NightSignal.Core.Story.DiaryChallenges.AllCrewsRead(await players.DiaryReadsAsync(e.EntrantId, ct), content.Crews))
                diaryComplete.Add(e.EntrantId);
        }
        // Challenge trials read each finishing human's settled trial passes (a grouped challenge needs every trial of its group).
        var trialPassesBefore = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
        if (config.ChallengeTrialId is not null)
            foreach (EntrantFacts e in submission.Entrants.Where(x => x.Human && x.Outcome == RunOutcome.Finished))
                trialPassesBefore[e.EntrantId] = await ledger.TrialPassesAsync(e.EntrantId, ct);
        (MatchSettlement? settlement, string? invalid) = Compute(config, submission, Hashing.Sha256Hex(body), finished, diaryComplete, ghostBeats, freeplayBefore,
            trialPassesBefore);
        if (settlement is null) return Error(422, "invalid_results", invalid!);

        SettlementOutcome outcome = await ledger.SettleAsync(settlement, ct);
        switch (outcome.Status)
        {
            case SettlementStatus.Settled:
                log.LogInformation("Match {MatchId} settled: {Count} receipts", matchId, outcome.Receipts.Count);
                foreach ((string account, NightSignal.Core.Ghosts.GhostRecording g) in ghosts)
                {
                    bool kept = await ledger.OfferGhostAsync(account, g.Header.CourseId, g.Header.Format, NightSignal.Core.Ghosts.GhostRecording.RulesKey(g.Header),
                        g.Header.ResultMicros, matchId, g.ToJson(), ct);
                    log.LogInformation("Ghost {Course} {Format} {Result:F3} s for {Account}: {Kept}", g.Header.CourseId, g.Header.Format,
                        g.Header.ResultMicros / 1e6, account, kept ? "kept" : "slower than the kept one");
                }
                await EndMatchAsync(config, ct, CupLeg(config, submission));
                return new SubmissionResult(200, new { status = "settled", receipts = outcome.Receipts });
            case SettlementStatus.AlreadySettled:
                return new SubmissionResult(200, new { status = "settled", replayed = true, receipts = outcome.Receipts });
            case SettlementStatus.Conflict:
                return Error(409, "conflict", "This match was already settled with a different result body.");
            case SettlementStatus.Aborted:
                return Error(409, "aborted", "This match was aborted; it cannot be settled.");
            default:
                return Error(404, "unknown_match", "No such match.");
        }
    }

    /// <summary>A live AI entrant that never spawned/initialised makes the event broken (abort, retry without fees).</summary>
    internal static string? BrokenEventReason(MatchAssignment config, ResultSubmission s)
    {
        EntrantFacts? missing = s.Entrants.FirstOrDefault(e => !e.Human && e.Started == false && config.AiEntrants.Contains(e.EntrantId));
        if (missing is null) return null;
        return missing.EntrantId == config.FeaturedRival
            ? "The featured rival failed to start, so the event was aborted: no results, rank or progression; retry any time."
            : "A live opponent failed to start, so the event was aborted: no results, rank or progression; retry any time.";
    }

    /// <summary>
    /// The opposing AI's drivers (authored rival ids) in roster order: Freeplay AI race under slot ids ("ai-1"…) with the
    /// rival as the roster's driver, so rival rules (archetypes, crews) must read the roster, not the entrant ids.
    /// </summary>
    internal static IReadOnlyList<string> OpposingAiDrivers(MatchAssignment config) =>
        config.Roster.Count > 0
            ? config.Roster.Where(r => r.Kind == "ai" && r.Team == "opposing").Select(r => r.DriverId).ToList()
            : config.AiEntrants;

    static string DriverOf(MatchAssignment config, string entrantId) =>
        config.Roster.FirstOrDefault(r => r.EntrantId == entrantId)?.DriverId ?? entrantId;

    /// <summary>A Custom Cup leg's placings for the cup table (null outside a cup): finishers by place, everyone else none.</summary>
    IReadOnlyList<CupLegResult>? CupLeg(MatchAssignment config, ResultSubmission s)
    {
        if (config.Kind != "freeplay" || config.FreeplayMode != "cup") return null;
        return s.Entrants.Select(e => new CupLegResult
        {
            Id = e.EntrantId, Human = e.Human, Place = e.Outcome == RunOutcome.Finished && e.Placement > 0 ? e.Placement : null,
            Name = e.Human ? config.Entrants.FirstOrDefault(x => x.AccountId == e.EntrantId)?.DisplayName ?? e.EntrantId
                : content.Catalogue.TryRival(DriverOf(config, e.EntrantId), out RivalDef rival) ? rival.Name : e.EntrantId,
        }).ToList();
    }

    async Task EndMatchAsync(MatchAssignment config, CancellationToken ct, IReadOnlyList<CupLegResult>? cupLeg = null)
    {
        registry.MatchFinished(config.ServerId);
        List<string> ids = config.Entrants.Select(e => e.AccountId).ToList();
        IReadOnlyDictionary<string, MemberProgress> progress = await players.GetProgressAsync(ids, ct);
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> courses = await players.GetOwnedCoursesAsync(ids, content.Catalogue, ct);
        convoys.MatchEnded(config.ConvoyId, config.MatchId, progress, courses, cupLeg);
    }

    static SubmissionResult Error(int status, string code, string message) => new(status, new { error = code, message });

    /// <summary>Validates the facts against the frozen allocation and computes every reward input with Core.</summary>
    internal (MatchSettlement? Settlement, string? Error) Compute(MatchAssignment config, ResultSubmission s, string bodySha256,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? finishedBefore = null, IReadOnlySet<string>? diaryComplete = null,
        IReadOnlySet<string>? ghostBeats = null, IReadOnlyDictionary<string, IReadOnlyList<ArchetypeRace>>? freeplayBefore = null,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? trialPassesBefore = null)
    {
        if (ProgressionDomain.ToyViolation(config) is { } toy) return (null, toy);
        var humans = config.Entrants.Select(e => e.AccountId).ToHashSet(StringComparer.Ordinal);
        var ai = config.AiEntrants.ToHashSet(StringComparer.Ordinal);
        if (s.Entrants.Select(e => e.EntrantId).Distinct().Count() != s.Entrants.Count)
            return (null, "Duplicate entrant.");
        if (!s.Entrants.Select(e => e.EntrantId).ToHashSet().SetEquals(humans.Concat(ai)))
            return (null, "Entrants must be exactly the allocated humans and AI (DQs included).");
        foreach (EntrantFacts e in s.Entrants)
        {
            if (e.Human != humans.Contains(e.EntrantId)) return (null, $"{e.EntrantId}: human flag does not match the allocation.");
            if (!Enum.IsDefined(e.Outcome)) return (null, $"{e.EntrantId}: unknown outcome.");
            if (e.Outcome == RunOutcome.Finished ? e.FinishTimeMicros <= 0 : e.FinishTimeMicros < 0)
                return (null, $"{e.EntrantId}: invalid finish time.");
            if (!double.IsFinite(e.CheckpointFraction) || e.CheckpointFraction is < 0 or > 1)
                return (null, $"{e.EntrantId}: checkpoint fraction must be 0..1.");
            if (!double.IsFinite(e.LegalProgressMetres) || e.LegalProgressMetres < 0)
                return (null, $"{e.EntrantId}: invalid legal progress.");
            if (e.RawDriftScore < 0 || e.ContractsPassed is < 0 or > 4)
                return (null, $"{e.EntrantId}: invalid drift score or contract count.");
            if (e.Human && e.Started is not null)
                return (null, $"{e.EntrantId}: 'started' is reported for AI entrants only.");
            if (!e.Human && e.ChallengesCompleted.Count > 0)
                return (null, $"{e.EntrantId}: AI entrants cannot complete challenges.");
            if (e.ChallengesCompleted.Distinct().Count() != e.ChallengesCompleted.Count ||
                e.ChallengesCompleted.Any(id => !content.Catalogue.Challenges.Any(c => c.Id == id)))
                return (null, $"{e.EntrantId}: unknown or duplicate challenge ID.");
            bool trialFacts = e.TrialId is not null || e.TrialPassed is not null || e.TrialSummary is not null;
            if (trialFacts && (config.ChallengeTrialId is null || !e.Human || e.TrialId != config.ChallengeTrialId))
                return (null, $"{e.EntrantId}: challenge-trial facts that do not belong to this match.");
            if (e.TrialSummary is { Length: > 600 }) return (null, $"{e.EntrantId}: trial summary too long.");
        }
        if (config.ChallengeTrialId is not null && content.Catalogue.ChallengeTrials.Find(config.ChallengeTrialId) is null)
            return (null, $"Unknown challenge trial {config.ChallengeTrialId}.");
        if (BrokenEventReason(config, s) is { } broken) return (null, broken);

        // Placement: recomputed with Core (drift formats rank by raw score) and cross-checked with the server's.
        Dictionary<string, Placing> placings = Classify(config, s.Entrants);
        foreach (EntrantFacts e in s.Entrants)
            if (placings[e.EntrantId].Place != e.Placement)
                return (null, $"{e.EntrantId}: placement {e.Placement} disagrees with the recomputed classification {placings[e.EntrantId].Place}.");

        CourseDef course = content.Catalogue.Course(config.CourseId);
        bool campaign = config.Kind == "campaign";
        bool trial = config.Kind == "trial";
        CampaignMode mode = config.Mode == "hard" ? CampaignMode.Hard : CampaignMode.Normal;
        EventKind kind = campaign ? EventKind.CampaignStage : FreeplayRules.Kind(config.FreeplayMode);
        List<EntrantFacts> humanFacts = s.Entrants.Where(e => e.Human).ToList();
        int humansFinished = humanFacts.Count(e => e.Outcome == RunOutcome.Finished);

        // ---- campaign stage outcome, including the live featured-rival condition on encounter stages
        StageResolution? resolution = null;
        StageBenchmark? benchmark = null;
        StageDef? stage = null;
        string? featured = null;
        var beatRival = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (campaign)
        {
            stage = content.Catalogue.Stage(config.StageId!);
            // The benchmark locked at allocation (stored in the frozen config) is the one that is judged.
            benchmark = new StageBenchmark
            {
                Kind = Enum.Parse<BenchmarkKind>(config.Benchmark!.Kind),
                TargetTimeMs = config.Benchmark.TargetTimeMs,
                RawDriftTarget = config.Benchmark.RawDriftTarget,
                HardTimeoutMs = config.Benchmark.HardTimeoutMs,
                RequiresBeatingFeaturedRival = config.Benchmark.RequiresBeatingFeaturedRival,
            };
            featured = config.FeaturedRival ?? config.AiEntrants.FirstOrDefault();
            EntrantFacts? rival = featured is null ? null : s.Entrants.FirstOrDefault(e => !e.Human && e.EntrantId == featured);
            if (benchmark.RequiresBeatingFeaturedRival && rival is null)
                return (null, "This encounter's featured live rival is missing from the results.");
            foreach (EntrantFacts h in humanFacts) beatRival[h.EntrantId] = BeatsFeaturedRival(h, rival, placings);
            resolution = StageOutcome.Resolve(mode, benchmark, humans.Count, humanFacts.Select(e => new HumanStageResult
            {
                PlayerId = e.EntrantId, Outcome = e.Outcome, ActivelyDroveLegalCourse = e.ActivelyDroveLegalCourse,
                FinishTimeMs = RaceClassification.ToReportedMillis(e.FinishTimeMicros), RawDriftScore = e.RawDriftScore,
                ContractsPassed = e.ContractsPassed, BeatFeaturedRival = beatRival[e.EntrantId],
            }).ToList());
        }

        // ---- Team Trial: six v six team scores with Core TeamTrials (every starting position counts)
        TrialAssignment? rules = config.Trial;
        TeamScore? playerScore = null, opposingScore = null;
        TeamTrialVerdict verdict = TeamTrialVerdict.Tie;
        List<TeamMemberResult> playerSide = new();
        TeamTrialKind trialKind = TeamTrialKind.Mean;
        Dictionary<string, string> teamOf = new(StringComparer.Ordinal);
        if (trial)
        {
            if (rules is null || config.Roster.Count == 0) return (null, "The allocation carries no Team Trial rules or roster.");
            trialKind = rules.Kind switch { "best" => TeamTrialKind.Best, "drift" => TeamTrialKind.Drift, _ => TeamTrialKind.Mean };
            teamOf = config.Roster.ToDictionary(r => r.EntrantId, r => r.Team, StringComparer.Ordinal);
            if (s.Entrants.Any(e => !teamOf.ContainsKey(e.EntrantId))) return (null, "An entrant is not on either team.");
            List<TeamMemberResult> Side(string team) => s.Entrants.Where(e => teamOf[e.EntrantId] == team).Select(e => new TeamMemberResult
            {
                EntrantId = e.EntrantId, Human = e.Human, Outcome = e.Outcome,
                AdjustedFinishMs = RaceClassification.ToReportedMillis(e.FinishTimeMicros), RawDriftScore = e.RawDriftScore,
            }).ToList();
            playerSide = Side("player");
            List<TeamMemberResult> opposingSide = Side("opposing");
            if (playerSide.Count != Limits.TeamTrialSideSize || opposingSide.Count != Limits.TeamTrialSideSize)
                return (null, $"A Team Trial has exactly {Limits.TeamTrialSideSize} positions per team.");
            playerScore = TeamTrials.Score(trialKind, playerSide, rules.HardTimeoutMs);
            opposingScore = TeamTrials.Score(trialKind, opposingSide, rules.HardTimeoutMs);
            verdict = TeamTrials.Compare(playerScore, opposingScore);
        }

        var entrants = new List<EntrantSettlement>();
        foreach (EntrantFacts e in humanFacts)
        {
            Placing placing = placings[e.EntrantId];
            long finishMs = RaceClassification.ToReportedMillis(e.FinishTimeMicros);
            bool pvpConfiguration = config.PurePvP && !campaign && !trial && kind != EventKind.FreeplayTimeTrial; // AI or Time Attack: never
            var facts = new PayoutFacts
            {
                AuthoredExpectedSeconds = course.ExpectedSeconds, // trusted catalogue value, never elapsed time
                Kind = kind,
                Mode = campaign ? mode : CampaignMode.Normal,
                Outcome = e.Outcome,
                Placement = trial
                    ? (verdict == TeamTrialVerdict.PlayerTeamWins ? rules!.VictoryPlacement : rules!.DefeatPlacement) // bounded team modifier
                    : placing.Place,
                ReferenceBeaten = kind == EventKind.FreeplayTimeTrial && e.Outcome == RunOutcome.Finished &&
                                  finishMs <= content.FreeplayReferenceMs(course.Id),
                Clean = e.Clean,
                // The utility item of the build FROZEN at allocation (server-resolved; +4 % or +8 % ordinary event pay only).
                UtilityIncomePercent = UtilityIncomePercent(config, e.EntrantId),
                PvPWinnerBonusEligible = e.Outcome == RunOutcome.Finished &&
                                         Economy.PvPWinnerBonusEligible(placing.Place, ai.Count, humansFinished, pvpConfiguration),
                CheckpointFraction = e.CheckpointFraction,
                ServerVerifiedActiveProgress = e.ActiveProgressVerified,
                TutorialRepeat = course.Kind == "tutorial",
            };

            var receipt = new Receipt
            {
                MatchId = config.MatchId, AccountId = e.EntrantId, EventKind = trial ? "TeamTrial" : kind.ToString(), Mode = config.Mode,
                StageId = config.StageId, CourseId = config.CourseId, Outcome = e.Outcome.ToString(), Placement = placing.Place,
                Tied = placing.Tied, FinishTimeMs = e.Outcome == RunOutcome.Finished ? finishMs : null,
                RawDriftScore = !trial && kind == EventKind.FreeplayDriftAttack && e.Outcome == RunOutcome.Finished ? e.RawDriftScore : null,
            };
            if (config.GuestPasses.FirstOrDefault(p => p.AccountId == e.EntrantId) is { } pass)
                receipt.GuestPass = new GuestPassInfo { CourseId = pass.CourseId, SponsorId = pass.SponsorId };

            StageClearCandidate? clear = null;
            IReadOnlyList<string> courseGrants = Array.Empty<string>();
            IReadOnlyList<MusicGrant> clearMusic = Array.Empty<MusicGrant>();
            var musicGrants = new List<MusicGrant>();
            string? withheld = null;
            TeamBestCandidate? teamBest = null;
            string? finaleChallenge = null;
            if (resolution is not null)
            {
                PlayerStageVerdict v = resolution.Players.First(p => p.PlayerId == e.EntrantId);
                // CH44 / CH45: the S30 finale cleared personally within the qualifying benchmark, not only the support envelope.
                if (stage!.Id == "S30" && v.Qualified && v.EarnedClear) finaleChallenge = mode == CampaignMode.Hard ? "CH45" : "CH44";
                receipt.Stage = new StageVerdictInfo
                {
                    Qualified = v.Qualified, WithinSupport = v.WithinSupport, EarnedClear = v.EarnedClear,
                    TeamSuccess = resolution.TeamSuccess, Qualifiers = resolution.Qualifiers,
                    RequiredQualifiers = resolution.RequiredQualifiers, FrozenHumanCount = resolution.FrozenHumanCount,
                    Reason = v.Reason, BenchmarkTargetMs = benchmark!.TargetTimeMs,
                    BenchmarkProvisional = config.Benchmark!.Provisional, BenchmarkSource = config.Benchmark.Source,
                    RequiresBeatingFeaturedRival = benchmark.RequiresBeatingFeaturedRival, FeaturedRival = featured,
                    BeatFeaturedRival = beatRival[e.EntrantId],
                };
                if (v.EarnedClear)
                {
                    clear = new StageClearCandidate(mode, stage!.Number, ContentService.ParseStageType(stage.Type));
                    if (mode == CampaignMode.Normal) courseGrants = CourseAccess.GrantedByNormalClear(content.Catalogue, stage.Id);
                    clearMusic = Music.ForStageClear(stage.Id, mode).Select(m => new MusicGrant(m.CueId, m.Source.Kind, m.SourceRef)).ToList();
                }
            }
            if (trial)
            {
                TeamMemberResult me = playerSide.First(m => m.EntrantId == e.EntrantId);
                bool payable = TeamTrials.HumanCompletionPayable(trialKind, me, playerSide, rules!.ParticipationEnvelopeMs);
                if (!payable)
                    withheld = e.Outcome != RunOutcome.Finished
                        ? "Team Trials pay completion money only to active eligible human finishers."
                        : "No human finished within the participation envelope, so no human completion pay (an AI win is not a human payout).";
                bool bestHasTime = trialKind != TeamTrialKind.Best || playerScore!.Value != long.MaxValue;
                receipt.TeamTrial = new TeamTrialReceipt
                {
                    TrialId = rules.TrialId, Kind = rules.Kind, Difficulty = rules.Difficulty, Humans = humans.Count,
                    FriendlyAi = Limits.TeamTrialSideSize - humans.Count,
                    PlayerTeamValue = bestHasTime ? playerScore!.Value : null,
                    OpposingTeamValue = trialKind != TeamTrialKind.Best || opposingScore!.Value != long.MaxValue ? opposingScore!.Value : null,
                    PlayerTeamMeanMs = trialKind == TeamTrialKind.Mean ? playerScore!.DisplayMeanMs : null,
                    Verdict = verdict switch { TeamTrialVerdict.PlayerTeamWins => "victory", TeamTrialVerdict.OpposingTeamWins => "defeat", _ => "tie" },
                    Contributions = playerScore!.Contributions.Concat(opposingScore!.Contributions).Select(kv => new TeamContribution
                    {
                        EntrantId = kv.Key, Human = humans.Contains(kv.Key), Team = teamOf[kv.Key],
                        Value = kv.Value == long.MaxValue ? null : kv.Value,
                    }).ToList(),
                    CompletionPayable = payable, Provisional = rules.Provisional,
                };
                if (payable && bestHasTime)
                    teamBest = new TeamBestCandidate(rules.TrialId, rules.Difficulty, humans.Count, rules.Kind, playerScore.Value, trialKind != TeamTrialKind.Drift);
                if (payable && verdict == TeamTrialVerdict.PlayerTeamWins)
                    musicGrants.AddRange(Music.ForTrialVictory(rules.TrialId).Select(m => new MusicGrant(m.CueId, m.Source.Kind, m.SourceRef)));
            }

            var grants = new List<ChallengeGrant>();
            if (e.Outcome == RunOutcome.Finished)
            {
                // The server's own predicates join the game server's: the finale verdict and the cumulative course set.
                var ids = new List<string>(e.ChallengesCompleted);
                if (e.Human && finaleChallenge is not null && !ids.Contains(finaleChallenge)) ids.Add(finaleChallenge);
                // CH70 The Other Side of the Card: every crew introduction read, then a legal race against a crew member.
                if (e.Human && diaryComplete is not null && diaryComplete.Contains(e.EntrantId) &&
                    NightSignal.Core.Story.DiaryChallenges.RacedCrewMember(OpposingAiDrivers(config), content.Catalogue, content.Crews) &&
                    !ids.Contains(NightSignal.Core.Story.DiaryChallenges.OtherSideOfTheCard))
                    ids.Add(NightSignal.Core.Story.DiaryChallenges.OtherSideOfTheCard);
                // CH68 Chasing Your Yesterday: the kept C07 ghost beaten by a second (the caller checked ghost and rules).
                if (e.Human && ghostBeats is not null && ghostBeats.Contains(e.EntrantId) && !ids.Contains(NightSignal.Core.Ghosts.GhostChallenges.ChasingYourYesterday))
                    ids.Add(NightSignal.Core.Ghosts.GhostChallenges.ChasingYourYesterday);
                // CH38 / CH73: Freeplay rival archetypes — the settled history, then this race (the field's AI in roster order).
                if (e.Human && config.Kind == "freeplay" && freeplayBefore is not null)
                {
                    var races = new List<ArchetypeRace>(freeplayBefore.TryGetValue(e.EntrantId, out var earlier) ? earlier : Array.Empty<ArchetypeRace>())
                    {
                        new() { AiRivals = OpposingAiDrivers(config), Outcome = e.Outcome, Placement = placing.Place, Tied = placing.Tied },
                    };
                    foreach (string id in ArchetypeChallenges.Satisfied(ArchetypeChallenges.Replay(content.Catalogue, races)))
                        if (!ids.Contains(id)) ids.Add(id);
                }
                // Challenge trials: the game server's verdict for this run, the settled passes before it, then the trial's group.
                if (e.Human && config.ChallengeTrialId is not null && content.Catalogue.ChallengeTrials.Find(config.ChallengeTrialId) is { } challengeTrial)
                {
                    var passed = new HashSet<string>(trialPassesBefore is not null && trialPassesBefore.TryGetValue(e.EntrantId, out var before)
                        ? before : Array.Empty<string>(), StringComparer.Ordinal);
                    if (e.TrialPassed == true) passed.Add(challengeTrial.Id);
                    bool earned = e.TrialPassed == true && TrialJudge.ChallengeEarned(content.Catalogue.ChallengeTrials, challengeTrial.Challenge, passed);
                    if (earned && !ids.Contains(challengeTrial.Challenge)) ids.Add(challengeTrial.Challenge);
                    receipt.ChallengeTrial = new ChallengeTrialReceipt
                    {
                        TrialId = challengeTrial.Id, Challenge = challengeTrial.Challenge, Passed = e.TrialPassed == true, Summary = e.TrialSummary ?? "",
                        GroupPassed = content.Catalogue.ChallengeTrials.ForChallenge(challengeTrial.Challenge).Count(t => passed.Contains(t.Id)),
                        GroupSize = content.Catalogue.ChallengeTrials.ForChallenge(challengeTrial.Challenge).Count,
                    };
                }
                if (e.Human)
                {
                    var courses = new HashSet<string>(finishedBefore is not null && finishedBefore.TryGetValue(e.EntrantId, out var past) ? past : Array.Empty<string>(),
                        StringComparer.Ordinal) { config.CourseId };
                    foreach (string id in CumulativeChallenges.Satisfied(content.Catalogue, courses))
                        if (!ids.Contains(id)) ids.Add(id);
                }
                foreach (string id in ids)
                {
                    ChallengeDef ch = content.Catalogue.Challenge(id);
                    ChallengeTier tier = ContentService.ParseTier(ch.Tier);
                    grants.Add(new ChallengeGrant(ch.Id, tier, RankPoints.ChallengeCash(tier), RankPoints.ForChallenge(tier), ch.Reward));
                }
            }
            else if (e.ChallengesCompleted.Count > 0)
                receipt.Notes.Add("Challenge claims ignored: challenges require a valid finish.");

            entrants.Add(new EntrantSettlement
            {
                AccountId = e.EntrantId, Facts = facts, Clear = clear, Challenges = grants, Receipt = receipt,
                ClearCourseGrants = courseGrants, ClearMusicGrants = clearMusic, MusicGrants = musicGrants, PayoutWithheld = withheld, TeamBest = teamBest,
            });
        }
        return (new MatchSettlement { MatchId = config.MatchId, ResultsSha256 = bodySha256, Entrants = entrants }, null);
    }

    /// <summary>
    /// Income utility of the entrant's frozen applied build (assignment <c>entrants[].vehicleBuild.utility</c>, resolved by the
    /// control plane from owned parts at start). Anything but the two authored values (4, 8) counts as none; allocations made
    /// before builds were frozen carry no build and pay 1.00.
    /// </summary>
    internal static int UtilityIncomePercent(MatchAssignment config, string accountId) =>
        config.Entrants.FirstOrDefault(x => x.AccountId == accountId)?.VehicleBuild?.Utility?.IncomePercent is int p && (p == 4 || p == 8) ? p : 0;

    /// <summary>
    /// Server-observed "beat the featured rival" (Addendum 01 §1.3): a legally finished human placed strictly ahead of the
    /// rival (a tie shares a placing and does not beat it), or the rival legally failed to finish while the human finished.
    /// </summary>
    internal static bool BeatsFeaturedRival(EntrantFacts human, EntrantFacts? rival, IReadOnlyDictionary<string, Placing> placings)
    {
        if (human.Outcome != RunOutcome.Finished || rival is null) return false;
        if (rival.Outcome != RunOutcome.Finished) return true;
        return placings[human.EntrantId].Place < placings[rival.EntrantId].Place;
    }

    /// <summary>Core RaceClassification by time; drift formats rank finishers by raw score (ties share a placing).</summary>
    static Dictionary<string, Placing> Classify(MatchAssignment config, IReadOnlyList<EntrantFacts> entrants)
    {
        if (config.FreeplayMode != "drift-attack")
            return RaceClassification.Classify(entrants.Select(e => new EntrantFinish
            {
                EntrantId = e.EntrantId, Outcome = e.Outcome, FinishTimeMicros = e.FinishTimeMicros, LegalProgressMetres = e.LegalProgressMetres,
            }).ToList()).ToDictionary(p => p.EntrantId);

        var result = new Dictionary<string, Placing>();
        var finishers = entrants.Where(e => e.Outcome == RunOutcome.Finished).OrderByDescending(e => e.RawDriftScore).ToList();
        for (int i = 0; i < finishers.Count; i++)
        {
            int place = 1 + finishers.Count(f => f.RawDriftScore > finishers[i].RawDriftScore);
            bool tied = finishers.Count(f => f.RawDriftScore == finishers[i].RawDriftScore) > 1;
            result[finishers[i].EntrantId] = new Placing { EntrantId = finishers[i].EntrantId, Place = place, Tied = tied, Outcome = RunOutcome.Finished };
        }
        int next = finishers.Count;
        foreach (EntrantFacts dnf in entrants.Where(e => e.Outcome == RunOutcome.DidNotFinish).OrderByDescending(e => e.LegalProgressMetres))
            result[dnf.EntrantId] = new Placing { EntrantId = dnf.EntrantId, Place = ++next, Outcome = RunOutcome.DidNotFinish };
        foreach (EntrantFacts other in entrants.Where(e => !result.ContainsKey(e.EntrantId)))
            result[other.EntrantId] = new Placing { EntrantId = other.EntrantId, Place = 0, Outcome = other.Outcome };
        return result;
    }
}
