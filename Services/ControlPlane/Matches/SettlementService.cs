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
    /// <summary>Server race-clock finish time, integer microseconds (Finished only).</summary>
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
    /// <summary>Challenge predicates the game server evaluated as met in this event.</summary>
    public List<string> ChallengesCompleted { get; set; } = new();
}

public sealed record SubmissionResult(int StatusCode, object Body);

/// <summary>Verifies, recomputes (with NightSignal.Core) and settles a match result idempotently.</summary>
public sealed class SettlementService(IResultLedger ledger, IPlayerStore players, ContentService content, ConvoyDirectory convoys,
    GameServerRegistry registry, ILogger<SettlementService> log)
{
    public const string SignatureHeader = "X-NightSignal-Signature";
    public const int MaxBodyBytes = 64 * 1024;

    static readonly JsonSerializerOptions ParseOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    /// <summary>signature = "sha256=" + lowercase hex HMAC-SHA256(base64url-decoded per-match secret, raw body bytes).</summary>
    public static string Sign(string resultsSecret, byte[] body) =>
        "sha256=" + Hashing.HmacSha256Hex(Base64UrlEncoder.DecodeBytes(resultsSecret), body);

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

        if (submission.Aborted)
        {
            if (!await ledger.MarkAbortedAsync(matchId, ct))
                return (await ledger.GetMatchAsync(matchId, ct))?.State == "aborted"
                    ? new SubmissionResult(200, new { status = "aborted", replayed = true })
                    : Error(409, "conflict", "This match was already settled; it cannot be aborted.");
            log.LogWarning("Match {MatchId} reported aborted by {ServerId}; no results or progression issued", matchId, serverId);
            await EndMatchAsync(config, ct);
            return new SubmissionResult(200, new { status = "aborted" });
        }

        (MatchSettlement? settlement, string? invalid) = Compute(config, submission, Hashing.Sha256Hex(body));
        if (settlement is null) return Error(422, "invalid_results", invalid!);

        SettlementOutcome outcome = await ledger.SettleAsync(settlement, ct);
        switch (outcome.Status)
        {
            case SettlementStatus.Settled:
                log.LogInformation("Match {MatchId} settled: {Count} receipts", matchId, outcome.Receipts.Count);
                await EndMatchAsync(config, ct);
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

    async Task EndMatchAsync(MatchAssignment config, CancellationToken ct)
    {
        registry.MatchFinished(config.ServerId);
        IReadOnlyDictionary<string, MemberProgress> progress =
            await players.GetProgressAsync(config.Entrants.Select(e => e.AccountId).ToList(), ct);
        convoys.MatchEnded(config.ConvoyId, config.MatchId, progress);
    }

    static SubmissionResult Error(int status, string code, string message) => new(status, new { error = code, message });

    /// <summary>Validates the facts against the frozen allocation and computes every reward input with Core.</summary>
    internal (MatchSettlement? Settlement, string? Error) Compute(MatchAssignment config, ResultSubmission s, string bodySha256)
    {
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
            if (!e.Human && e.ChallengesCompleted.Count > 0)
                return (null, $"{e.EntrantId}: AI entrants cannot complete challenges.");
            if (e.ChallengesCompleted.Distinct().Count() != e.ChallengesCompleted.Count ||
                e.ChallengesCompleted.Any(id => !content.Catalogue.Challenges.Any(c => c.Id == id)))
                return (null, $"{e.EntrantId}: unknown or duplicate challenge ID.");
        }

        // Placement: recomputed with Core (drift events rank by raw score) and cross-checked with the server's.
        Dictionary<string, Placing> placings = Classify(config, s.Entrants);
        foreach (EntrantFacts e in s.Entrants)
            if (placings[e.EntrantId].Place != e.Placement)
                return (null, $"{e.EntrantId}: placement {e.Placement} disagrees with the recomputed classification {placings[e.EntrantId].Place}.");

        CourseDef course = content.Catalogue.Course(config.CourseId);
        bool campaign = config.Kind == "campaign";
        CampaignMode mode = config.Mode == "hard" ? CampaignMode.Hard : CampaignMode.Normal;
        EventKind kind = campaign ? EventKind.CampaignStage : config.FreeplayMode switch
        {
            "circuit" => EventKind.FreeplayCircuit,
            "drift-attack" => EventKind.FreeplayDriftAttack,
            "time-attack" => EventKind.FreeplayTimeTrial,
            _ => EventKind.FreeplaySprint,
        };
        List<EntrantFacts> humanFacts = s.Entrants.Where(e => e.Human).ToList();
        int humansFinished = humanFacts.Count(e => e.Outcome == RunOutcome.Finished);

        StageResolution? resolution = null;
        StageBenchmark? benchmark = null;
        StageDef? stage = null;
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
            };
            resolution = StageOutcome.Resolve(mode, benchmark, humans.Count, humanFacts.Select(e => new HumanStageResult
            {
                PlayerId = e.EntrantId, Outcome = e.Outcome, ActivelyDroveLegalCourse = e.ActivelyDroveLegalCourse,
                FinishTimeMs = RaceClassification.ToReportedMillis(e.FinishTimeMicros), RawDriftScore = e.RawDriftScore,
                ContractsPassed = e.ContractsPassed,
            }).ToList());
        }

        var entrants = new List<EntrantSettlement>();
        foreach (EntrantFacts e in humanFacts)
        {
            Placing placing = placings[e.EntrantId];
            long finishMs = RaceClassification.ToReportedMillis(e.FinishTimeMicros);
            var facts = new PayoutFacts
            {
                AuthoredExpectedSeconds = course.ExpectedSeconds, // trusted catalogue value, never elapsed time
                Kind = kind,
                Mode = campaign ? mode : CampaignMode.Normal,
                Outcome = e.Outcome,
                Placement = placing.Place,
                ReferenceBeaten = kind == EventKind.FreeplayTimeTrial && e.Outcome == RunOutcome.Finished &&
                                  finishMs <= content.FreeplayReferenceMs(course.Id),
                Clean = e.Clean,
                UtilityIncomePercent = 0, // no utility items are owned/equippable in this build
                PvPWinnerBonusEligible = e.Outcome == RunOutcome.Finished &&
                                         Economy.PvPWinnerBonusEligible(placing.Place, ai.Count, humansFinished, config.PurePvP && !campaign),
                CheckpointFraction = e.CheckpointFraction,
                ServerVerifiedActiveProgress = e.ActiveProgressVerified,
                TutorialRepeat = course.Kind == "tutorial",
            };

            var receipt = new Receipt
            {
                MatchId = config.MatchId, AccountId = e.EntrantId, EventKind = kind.ToString(), Mode = config.Mode,
                StageId = config.StageId, CourseId = config.CourseId, Outcome = e.Outcome.ToString(), Placement = placing.Place,
                Tied = placing.Tied, FinishTimeMs = e.Outcome == RunOutcome.Finished ? finishMs : null,
            };
            StageClearCandidate? clear = null;
            if (resolution is not null)
            {
                PlayerStageVerdict v = resolution.Players.First(p => p.PlayerId == e.EntrantId);
                receipt.Stage = new StageVerdictInfo
                {
                    Qualified = v.Qualified, WithinSupport = v.WithinSupport, EarnedClear = v.EarnedClear,
                    TeamSuccess = resolution.TeamSuccess, Qualifiers = resolution.Qualifiers,
                    RequiredQualifiers = resolution.RequiredQualifiers, FrozenHumanCount = resolution.FrozenHumanCount,
                    Reason = v.Reason, BenchmarkTargetMs = benchmark!.TargetTimeMs,
                    BenchmarkProvisional = config.Benchmark!.Provisional, BenchmarkSource = config.Benchmark.Source,
                };
                if (v.EarnedClear)
                    clear = new StageClearCandidate(mode, stage!.Number, ContentService.ParseStageType(stage.Type));
            }

            var grants = new List<ChallengeGrant>();
            if (e.Outcome == RunOutcome.Finished)
                foreach (string id in e.ChallengesCompleted)
                {
                    ChallengeDef ch = content.Catalogue.Challenge(id);
                    ChallengeTier tier = ContentService.ParseTier(ch.Tier);
                    grants.Add(new ChallengeGrant(ch.Id, tier, RankPoints.ChallengeCash(tier), RankPoints.ForChallenge(tier), ch.Reward));
                }
            else if (e.ChallengesCompleted.Count > 0)
                receipt.Notes.Add("Challenge claims ignored: challenges require a valid finish.");

            entrants.Add(new EntrantSettlement { AccountId = e.EntrantId, Facts = facts, Clear = clear, Challenges = grants, Receipt = receipt });
        }
        return (new MatchSettlement { MatchId = config.MatchId, ResultsSha256 = bodySha256, Entrants = entrants }, null);
    }

    /// <summary>Core RaceClassification by time; Drift Attack ranks finishers by raw score (ties share a placing).</summary>
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
