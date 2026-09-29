using System.Data.Common;
using System.Text.Json;
using NightSignal.ControlPlane.Players;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;

namespace NightSignal.ControlPlane.Persistence;

/// <summary>
/// The shared SQL implementation of <see cref="IPlayerStore"/> and <see cref="IResultLedger"/>. Dialects supply
/// connections, write-transaction semantics, row locking and unique-violation detection only.
///
/// Correctness does not rely on in-process locks: every economic write runs in one database transaction that
/// (a) serializes writers per wallet (SQLite BEGIN IMMEDIATE / PostgreSQL SELECT ... FOR UPDATE) and
/// (b) is protected by UNIQUE constraints (ledger idempotency keys, stage/challenge "once" constraints, receipts).
/// </summary>
public abstract partial class SqlGameStore : IPlayerStore, IResultLedger, ISocialStore
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected abstract string Dialect { get; }
    /// <summary>Row-lock suffix for SELECTs on rows about to be updated (" FOR UPDATE" or "").</summary>
    protected abstract string ForUpdate { get; }
    protected abstract string MigrationsTableDdl { get; }
    protected abstract Task<DbConnection> OpenAsync(CancellationToken ct);
    protected abstract Task<DbTransaction> BeginWriteAsync(DbConnection c, CancellationToken ct);
    protected abstract Task<DbTransaction> BeginReadAsync(DbConnection c, CancellationToken ct);
    protected abstract bool IsUniqueViolation(Exception e);

    /// <summary>Applies embedded migrations not yet recorded in schema_migrations, each in its own transaction.</summary>
    public virtual async Task InitializeAsync(CancellationToken ct = default)
    {
        await using DbConnection c = await OpenAsync(ct);
        await c.ExecAsync(null, MigrationsTableDdl);
        var applied = (await c.QueryAsync(null, "SELECT version FROM schema_migrations", r => r.Str(0))).ToHashSet();
        foreach ((string version, string sql) in Db.Migrations(Dialect))
        {
            if (applied.Contains(version)) continue;
            await using DbTransaction tx = await c.BeginTransactionAsync(ct);
            await c.ExecAsync(tx, sql);
            await c.ExecAsync(tx, "INSERT INTO schema_migrations (version) VALUES (@v)", ("@v", version));
            await tx.CommitAsync(ct);
        }
    }

    /// <summary>Runs <paramref name="work"/> in a write transaction. If a concurrent writer won a UNIQUE race
    /// (possible under PostgreSQL READ COMMITTED), the work is re-run so it observes and replays that result.</summary>
    protected async Task<T> WriteAsync<T>(Func<DbConnection, DbTransaction, Task<T>> work, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            await using DbConnection c = await OpenAsync(ct);
            await using DbTransaction tx = await BeginWriteAsync(c, ct);
            try
            {
                T result = await work(c, tx);
                await tx.CommitAsync(ct);
                return result;
            }
            catch (Exception e) when (attempt < 3 && IsUniqueViolation(e))
            {
                // Rolled back by disposal; retry.
            }
        }
    }

    protected async Task<T> ReadAsync<T>(Func<DbConnection, DbTransaction, Task<T>> work, CancellationToken ct)
    {
        await using DbConnection c = await OpenAsync(ct);
        await using DbTransaction tx = await BeginReadAsync(c, ct);
        T result = await work(c, tx);
        await tx.CommitAsync(ct);
        return result;
    }

    // ---------------------------------------------------------------- players

    public Task EnsureAccountAsync(string accountId, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) => { await EnsureAccount(c, tx, accountId); return true; }, ct);

    static async Task EnsureAccount(DbConnection c, DbTransaction tx, string accountId)
    {
        await c.ExecAsync(tx, "INSERT INTO accounts (account_id) VALUES (@a) ON CONFLICT DO NOTHING", ("@a", accountId));
        await c.ExecAsync(tx, "INSERT INTO wallets (account_id, balance) VALUES (@a, 0) ON CONFLICT DO NOTHING", ("@a", accountId));
    }

    Task<long> LockWallet(DbConnection c, DbTransaction tx, string accountId) =>
        c.FirstOrDefaultAsync(tx, "SELECT balance FROM wallets WHERE account_id = @a" + ForUpdate, r => r.Long(0), ("@a", accountId));

    static Task SetBalance(DbConnection c, DbTransaction tx, string accountId, long balance) =>
        c.ExecAsync(tx, "UPDATE wallets SET balance = @b, revision = revision + 1 WHERE account_id = @a", ("@b", balance), ("@a", accountId));

    static Task InsertLedger(DbConnection c, DbTransaction tx, string accountId, string key, string type, string? matchId,
        string? itemRef, long requested, long applied, long clamped, long balanceAfter) =>
        c.ExecAsync(tx,
            "INSERT INTO ledger_entries (account_id, idempotency_key, reward_type, match_id, item_ref, requested_amount, applied_amount, clamped_amount, balance_after) " +
            "VALUES (@a, @k, @t, @m, @i, @req, @app, @cl, @bal)",
            ("@a", accountId), ("@k", key), ("@t", type), ("@m", matchId), ("@i", itemRef),
            ("@req", requested), ("@app", applied), ("@cl", clamped), ("@bal", balanceAfter));

    public Task<PlayerSnapshot> GetSnapshotAsync(string accountId, CancellationToken ct = default) =>
        ReadAsync(async (c, tx) =>
        {
            PlayerCard? card = await c.FirstOrDefaultAsync(tx, "SELECT display_name, revision, look_json, pronouns, style_json, showcase_json FROM player_cards WHERE account_id = @a",
                r => new PlayerCard(r.Str(0), r.Long(1), r.NStr(2), r.NStr(3), r.NStr(4), r.NStr(5)), ("@a", accountId));
            long balance = await c.FirstOrDefaultAsync(tx, "SELECT balance FROM wallets WHERE account_id = @a", r => r.Long(0), ("@a", accountId));
            var cars = await c.QueryAsync(tx, "SELECT car_id, source FROM owned_cars WHERE account_id = @a ORDER BY car_id",
                r => new OwnedCar(r.Str(0), r.Str(1)), ("@a", accountId));
            (bool[] normal, bool[] hard) = await LoadClears(c, tx, accountId);
            var challenges = await c.QueryAsync(tx, "SELECT challenge_id, tier FROM challenge_unlocks WHERE account_id = @a ORDER BY challenge_id",
                r => new ChallengeUnlock(r.Str(0), r.Str(1)), ("@a", accountId));
            var cosmetics = await c.QueryAsync(tx, "SELECT cosmetic_id FROM cosmetics_owned WHERE account_id = @a ORDER BY cosmetic_id",
                r => r.Str(0), ("@a", accountId));
            string? starter = await c.FirstOrDefaultAsync(tx, "SELECT item_ref FROM ledger_entries WHERE idempotency_key = @k",
                r => r.NStr(0), ("@k", StarterKey(accountId)));
            PlayerHandle? handle = await c.FirstOrDefaultAsync(tx,
                "SELECT handle_display, handle_canonical, revision FROM player_handles WHERE account_id = @a",
                r => new PlayerHandle(r.Str(0), r.Str(1), r.Long(2)), ("@a", accountId));
            var music = await c.QueryAsync(tx, "SELECT cue_id, source_kind, source_ref FROM ost_entitlements WHERE account_id = @a ORDER BY cue_id",
                r => new MusicEntitlement(r.Str(0), r.Str(1), r.Str(2)), ("@a", accountId));
            var bests = await c.QueryAsync(tx,
                "SELECT trial_id, difficulty, humans, kind, team_value, match_id FROM team_trial_bests WHERE account_id = @a ORDER BY trial_id, difficulty, humans",
                r => new TeamBestRecord(r.Str(0), r.Str(1), r.Int(2), r.Str(3), r.Long(4), r.Str(5)), ("@a", accountId));
            return new PlayerSnapshot
            {
                AccountId = accountId, Card = card, Handle = handle, Balance = balance, Cars = cars, NormalCleared = normal, HardCleared = hard,
                Challenges = challenges, Cosmetics = cosmetics, StarterCarId = starter, StoredCourses = await LoadStoredCourses(c, tx, accountId),
                Music = music, TeamBests = bests,
            };
        }, ct);

    static Task<List<CourseEntitlement>> LoadStoredCourses(DbConnection c, DbTransaction tx, string accountId) =>
        c.QueryAsync(tx, "SELECT course_id, source FROM course_entitlements WHERE account_id = @a ORDER BY course_id",
            r => new CourseEntitlement(r.Str(0), r.Str(1)), ("@a", accountId));

    public Task<IReadOnlyDictionary<string, IReadOnlyCollection<string>>> GetOwnedCoursesAsync(IReadOnlyCollection<string> accountIds,
        ContentCatalogue catalogue, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyDictionary<string, IReadOnlyCollection<string>>>(async (c, tx) =>
        {
            var result = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
            foreach (string id in accountIds.Distinct())
            {
                (bool[] normal, _) = await LoadClears(c, tx, id);
                result[id] = CourseOwnership.Owned(catalogue, await LoadStoredCourses(c, tx, id), normal).Keys.ToList();
            }
            return result;
        }, ct);

    /// <summary>
    /// Course purchase in ONE transaction: lock the wallet (serializing this account's purchases AND settlements), replay by
    /// idempotency key, decide with Core against the ownership visible inside the transaction, then debit + ledger +
    /// entitlement together. A campaign unlock that committed first makes this AlreadyOwned with no debit; a purchase that
    /// committed first makes the later clear keep the purchased entitlement (no refund, no duplicate).
    /// </summary>
    public Task<CoursePurchaseResult> PurchaseCourseAsync(string accountId, string idempotencyKey, string courseId, ContentCatalogue catalogue,
        CancellationToken ct = default)
    {
        string key = $"course/{accountId}/{idempotencyKey}";
        string itemRef = $"course:{courseId}";
        return WriteAsync(async (c, tx) =>
        {
            await EnsureAccount(c, tx, accountId);
            long balance = await LockWallet(c, tx, accountId);
            var prior = await c.FirstOrDefaultAsync(tx, "SELECT item_ref, requested_amount FROM ledger_entries WHERE idempotency_key = @k",
                r => (Item: r.NStr(0), Amount: r.Long(1)), ("@k", key));
            if (prior.Item is not null)
                return prior.Item == itemRef
                    ? new CoursePurchaseResult(CoursePurchaseStatus.Replayed, balance, -prior.Amount, -prior.Amount)
                    : new CoursePurchaseResult(CoursePurchaseStatus.Conflict, balance, 0, 0);

            (bool[] normal, _) = await LoadClears(c, tx, accountId);
            var owned = new HashSet<string>(CourseOwnership.Owned(catalogue, await LoadStoredCourses(c, tx, accountId), normal).Keys, StringComparer.Ordinal);
            CoursePurchaseOutcome outcome = CourseAccess.DecidePurchase(catalogue, courseId, owned, balance, out long price);
            switch (outcome)
            {
                case CoursePurchaseOutcome.AlreadyOwned:
                    return new CoursePurchaseResult(CoursePurchaseStatus.AlreadyOwned, balance, 0, 0);
                case CoursePurchaseOutcome.NotPurchasable:
                    return new CoursePurchaseResult(CoursePurchaseStatus.NotPurchasable, balance, 0, 0);
                case CoursePurchaseOutcome.InsufficientFunds:
                    return new CoursePurchaseResult(CoursePurchaseStatus.InsufficientFunds, balance, price, 0);
            }
            if (price <= 0 || price > Limits.WalletCap || !Wallet.TryDebit(balance, price, out long newBalance))
                return new CoursePurchaseResult(CoursePurchaseStatus.InsufficientFunds, balance, price, 0);
            await InsertLedger(c, tx, accountId, key, "course-purchase", null, itemRef, -price, -price, 0, newBalance);
            await SetBalance(c, tx, accountId, newBalance);
            await c.ExecAsync(tx, "INSERT INTO course_entitlements (account_id, course_id, source, ledger_key) VALUES (@a, @c, 'purchase', @k)",
                ("@a", accountId), ("@c", courseId), ("@k", key));
            return new CoursePurchaseResult(CoursePurchaseStatus.Purchased, newBalance, price, price);
        }, ct);
    }

    public Task<bool> GrantMusicCueAsync(string accountId, string cueId, string sourceKind, string sourceRef, string? matchId, CancellationToken ct = default) =>
        Matches.ProgressionDomain.ToyViolation(sourceKind, sourceRef) is not null
            ? Task.FromResult(false) // Addendum 02 D209: toys never unlock soundtrack cues
            : WriteAsync(async (c, tx) =>
        {
            await EnsureAccount(c, tx, accountId);
            return await GrantMusic(c, tx, accountId, new MusicGrant(cueId, sourceKind, sourceRef), matchId);
        }, ct);

    static async Task<bool> GrantMusic(DbConnection c, DbTransaction tx, string accountId, MusicGrant g, string? matchId) =>
        await c.ExecAsync(tx,
            "INSERT INTO ost_entitlements (account_id, cue_id, source_kind, source_ref, match_id) VALUES (@a, @c, @k, @r, @m) ON CONFLICT DO NOTHING",
            ("@a", accountId), ("@c", g.CueId), ("@k", g.SourceKind), ("@r", g.SourceRef), ("@m", matchId)) == 1;

    public Task<IReadOnlyDictionary<string, MemberProgress>> GetProgressAsync(IReadOnlyCollection<string> accountIds, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyDictionary<string, MemberProgress>>(async (c, tx) =>
        {
            var result = new Dictionary<string, MemberProgress>();
            foreach (string id in accountIds.Distinct())
            {
                (bool[] normal, bool[] hard) = await LoadClears(c, tx, id);
                result[id] = new MemberProgress(id, normal, hard);
            }
            return result;
        }, ct);

    static async Task<(bool[] Normal, bool[] Hard)> LoadClears(DbConnection c, DbTransaction tx, string accountId)
    {
        var normal = new bool[Limits.CampaignStages];
        var hard = new bool[Limits.CampaignStages];
        foreach ((string mode, int stage) in await c.QueryAsync(tx, "SELECT mode, stage FROM stage_clears WHERE account_id = @a",
                     r => (r.Str(0), r.Int(1)), ("@a", accountId)))
            (mode == "hard" ? hard : normal)[stage - 1] = true;
        return (normal, hard);
    }

    public Task<CardWriteResult> UpsertCardAsync(string accountId, string displayName, long? expectedRevision, CancellationToken ct = default) =>
        UpsertCardAsync(accountId, displayName, expectedRevision, null, ct);

    public Task<CardWriteResult> UpsertCardAsync(string accountId, string displayName, long? expectedRevision, CardExtras? extras, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) =>
        {
            await EnsureAccount(c, tx, accountId);
            (long current, string? look, string? pronouns, string? style, string? showcase) = await c.FirstOrDefaultAsync(tx,
                "SELECT revision, look_json, pronouns, style_json, showcase_json FROM player_cards WHERE account_id = @a" + ForUpdate,
                r => (r.Long(0), r.NStr(1), r.NStr(2), r.NStr(3), r.NStr(4)), ("@a", accountId));
            if (expectedRevision is { } expected && expected != current)
                return new CardWriteResult(WriteStatus.Conflict, null);
            // null = keep what is stored; "" = clear (back to the default look / no pronouns).
            if (extras?.LookJson is { } l) look = l.Length == 0 ? null : l;
            if (extras?.Pronouns is { } p) pronouns = p.Length == 0 ? null : p;
            if (extras?.StyleJson is { } st) style = st.Length == 0 ? null : st;
            if (extras?.ShowcaseJson is { } sc) showcase = sc.Length == 0 ? null : sc;
            if (current == 0)
                await c.ExecAsync(tx, "INSERT INTO player_cards (account_id, display_name, revision, look_json, pronouns, style_json, showcase_json) VALUES (@a, @n, 1, @l, @p, @s, @sc)",
                    ("@a", accountId), ("@n", displayName), ("@l", look), ("@p", pronouns), ("@s", style), ("@sc", showcase));
            else
                await c.ExecAsync(tx, "UPDATE player_cards SET display_name = @n, look_json = @l, pronouns = @p, style_json = @s, showcase_json = @sc, revision = revision + 1, updated_at = CURRENT_TIMESTAMP WHERE account_id = @a",
                    ("@a", accountId), ("@n", displayName), ("@l", look), ("@p", pronouns), ("@s", style), ("@sc", showcase));
            return new CardWriteResult(WriteStatus.Ok, new PlayerCard(displayName, current + 1, look, pronouns, style, showcase));
        }, ct);

    public Task<ChallengeGrantResult> GrantChallengeAsync(string accountId, ChallengeGrant grant, string source, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) =>
        {
            await EnsureAccount(c, tx, accountId);
            long balance = await LockWallet(c, tx, accountId);
            int inserted = await c.ExecAsync(tx,
                "INSERT INTO challenge_unlocks (account_id, challenge_id, tier, match_id) VALUES (@a, @ch, @t, @m) ON CONFLICT DO NOTHING",
                ("@a", accountId), ("@ch", grant.ChallengeId), ("@t", grant.Tier.ToString().ToLowerInvariant()), ("@m", source));
            if (inserted == 0) return new ChallengeGrantResult(false, 0, balance);
            if (!string.IsNullOrEmpty(grant.CosmeticId))
                await c.ExecAsync(tx, "INSERT INTO cosmetics_owned (account_id, cosmetic_id, source) VALUES (@a, @cos, @src) ON CONFLICT DO NOTHING",
                    ("@a", accountId), ("@cos", grant.CosmeticId), ("@src", grant.ChallengeId));
            WalletCredit credit = Wallet.Credit(balance, grant.Cash);
            await InsertLedger(c, tx, accountId, $"challenge/{accountId}/{grant.ChallengeId}", "challenge", null, grant.ChallengeId,
                grant.Cash, credit.Credited, credit.ClampedAway, credit.NewBalance);
            await SetBalance(c, tx, accountId, credit.NewBalance);
            return new ChallengeGrantResult(true, credit.Credited, credit.NewBalance);
        }, ct);

    public Task<IReadOnlyList<PersonalRecord>> PersonalRecordsAsync(string accountId, ContentCatalogue catalogue, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyList<PersonalRecord>>(async (c, tx) =>
        {
            static string Time(long ms) => $"{ms / 60000}:{ms / 1000 % 60:00}.{ms % 1000:000}";
            var best = new Dictionary<string, (long Ms, string Label)>(StringComparer.Ordinal);
            foreach (string json in await c.QueryAsync(tx, "SELECT receipt_json FROM match_results WHERE account_id = @a", r => r.Str(0), ("@a", accountId)))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                System.Text.Json.JsonElement root = doc.RootElement;
                string Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
                if (Get("outcome") != "Finished" || !root.TryGetProperty("finishTimeMs", out var t) || t.ValueKind != System.Text.Json.JsonValueKind.Number) continue;
                long ms = t.GetInt64();
                string kind = Get("eventKind"), course = Get("courseId"), stage = Get("stageId"), mode = Get("mode").ToLowerInvariant();
                string courseName = catalogue.TryCourse(course, out CourseDef cd) ? cd.Name : course;
                (string Key, string Label)? rec = kind switch
                {
                    "CampaignStage" when stage.Length > 0 && mode is "normal" or "hard" => ($"stage:{stage}:{mode}", $"{stage} {(mode == "hard" ? "Hard" : "Normal")} · {courseName}"),
                    "FreeplaySprint" => ($"course:{course}:sprint", $"{course} {courseName} · sprint"),
                    "FreeplayCircuit" => ($"course:{course}:circuit", $"{course} {courseName} · circuit"),
                    "FreeplayTimeTrial" => ($"course:{course}:time-trial", $"{course} {courseName} · time attack"),
                    _ => null,
                };
                if (rec is not { } r) continue;
                if (!best.TryGetValue(r.Key, out var b) || ms < b.Ms) best[r.Key] = (ms, r.Label);
            }
            var list = best.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new PersonalRecord(x.Key, x.Value.Label, Time(x.Value.Ms))).ToList();
            foreach (var tb in await c.QueryAsync(tx, "SELECT trial_id, difficulty, humans, kind, team_value FROM team_trial_bests WHERE account_id = @a ORDER BY trial_id, difficulty, humans",
                         r => (Trial: r.Str(0), Difficulty: r.Str(1), Humans: r.Long(2), Kind: r.Str(3), Value: r.Long(4)), ("@a", accountId)))
                list.Add(new PersonalRecord($"team:{tb.Trial}:{tb.Difficulty}:{tb.Humans}", $"{tb.Trial} {tb.Difficulty} · team of {tb.Humans}",
                    tb.Kind == "drift" ? $"{tb.Value:N0} raw" : Time(tb.Value)));
            return list;
        }, ct);

    public Task<IReadOnlyCollection<string>> FinishedCoursesAsync(string accountId, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyCollection<string>>(async (c, tx) =>
        {
            var courses = new HashSet<string>(StringComparer.Ordinal);
            foreach (string json in await c.QueryAsync(tx, "SELECT receipt_json FROM match_results WHERE account_id = @a", r => r.Str(0), ("@a", accountId)))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                System.Text.Json.JsonElement root = doc.RootElement;
                if (root.TryGetProperty("outcome", out var o) && o.GetString() == "Finished" && root.TryGetProperty("courseId", out var course) && course.GetString() is { } id)
                    courses.Add(id);
            }
            return courses;
        }, ct);

    public Task<IReadOnlyList<ArchetypeRace>> FreeplayRacesAsync(string accountId, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyList<ArchetypeRace>>(async (c, tx) =>
        {
            var races = new List<ArchetypeRace>();
            var rows = await c.QueryAsync(tx,
                "SELECT r.receipt_json, m.config_json FROM match_results r JOIN matches m ON m.match_id = r.match_id " +
                "WHERE r.account_id = @a AND m.state = 'settled' ORDER BY m.settled_at, m.created_at, m.match_id",
                r => (Receipt: r.Str(0), Config: r.Str(1)), ("@a", accountId));
            foreach (var row in rows)
            {
                using JsonDocument config = JsonDocument.Parse(row.Config);
                using JsonDocument receipt = JsonDocument.Parse(row.Receipt);
                JsonElement cfg = config.RootElement, rc = receipt.RootElement;
                if (!cfg.TryGetProperty("kind", out JsonElement kind) || kind.GetString() != "freeplay") continue;
                // The opposing AI's drivers from the frozen roster (Freeplay AI race under slot ids); older configs: the entrant ids.
                List<string> drivers = cfg.TryGetProperty("roster", out JsonElement roster) && roster.ValueKind == JsonValueKind.Array && roster.GetArrayLength() > 0
                    ? roster.EnumerateArray().Where(r => r.TryGetProperty("kind", out JsonElement k) && k.GetString() == "ai" &&
                                                         r.TryGetProperty("team", out JsonElement t) && t.GetString() == "opposing")
                        .Select(r => r.TryGetProperty("driverId", out JsonElement d) ? d.GetString() ?? "" : "").ToList()
                    : cfg.TryGetProperty("aiEntrants", out JsonElement ai) && ai.ValueKind == JsonValueKind.Array
                        ? ai.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string>();
                if (drivers.Count == 0) continue;
                if (!rc.TryGetProperty("outcome", out JsonElement o) || !Enum.TryParse(o.GetString(), out RunOutcome outcome)) continue;
                races.Add(new ArchetypeRace
                {
                    AiRivals = drivers, Outcome = outcome,
                    Placement = rc.TryGetProperty("placement", out JsonElement p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0,
                    Tied = rc.TryGetProperty("tied", out JsonElement t) && t.ValueKind == JsonValueKind.True,
                });
            }
            return races;
        }, ct);

    public Task<IReadOnlyCollection<string>> TrialPassesAsync(string accountId, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyCollection<string>>(async (c, tx) =>
        {
            var passed = new SortedSet<string>(StringComparer.Ordinal);
            var rows = await c.QueryAsync(tx,
                "SELECT r.receipt_json FROM match_results r JOIN matches m ON m.match_id = r.match_id " +
                "WHERE r.account_id = @a AND m.state = 'settled' AND r.receipt_json LIKE @t",
                r => r.Str(0), ("@a", accountId), ("@t", "%\"challengeTrial\"%"));
            foreach (string json in rows)
            {
                using JsonDocument receipt = JsonDocument.Parse(json);
                if (receipt.RootElement.TryGetProperty("challengeTrial", out JsonElement t) && t.ValueKind == JsonValueKind.Object &&
                    t.TryGetProperty("passed", out JsonElement p) && p.ValueKind == JsonValueKind.True &&
                    t.TryGetProperty("trialId", out JsonElement id) && id.GetString() is { Length: > 0 } trialId)
                    passed.Add(trialId);
            }
            return passed;
        }, ct);

    public Task<bool> HasFinishedEventAsync(string accountId, CancellationToken ct = default) =>
        ReadAsync(async (c, tx) => await c.FirstOrDefaultAsync(tx,
            "SELECT 1 FROM match_results WHERE account_id = @a AND receipt_json LIKE @f", r => true,
            ("@a", accountId), ("@f", "%\"outcome\":\"Finished\"%")), ct);

    static string StarterKey(string accountId) => $"starter/{accountId}";

    public Task<StarterResult> ClaimStarterAsync(string accountId, string carId, long credits, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) =>
        {
            await EnsureAccount(c, tx, accountId);
            long balance = await LockWallet(c, tx, accountId);
            string? claimed = await c.FirstOrDefaultAsync(tx, "SELECT item_ref FROM ledger_entries WHERE idempotency_key = @k",
                r => r.NStr(0), ("@k", StarterKey(accountId)));
            if (claimed is not null)
                return new StarterResult(claimed == carId ? WriteStatus.Replayed : WriteStatus.Conflict, claimed, balance, 0);

            await c.ExecAsync(tx, "INSERT INTO owned_cars (account_id, car_id, source) VALUES (@a, @c, 'starter') ON CONFLICT DO NOTHING",
                ("@a", accountId), ("@c", carId));
            WalletCredit credit = Wallet.Credit(balance, credits);
            await InsertLedger(c, tx, accountId, StarterKey(accountId), "starter", null, carId, credits, credit.Credited, credit.ClampedAway, credit.NewBalance);
            await SetBalance(c, tx, accountId, credit.NewBalance);
            return new StarterResult(WriteStatus.Ok, carId, credit.NewBalance, credit.Credited);
        }, ct);

    public Task<PurchaseResult> PurchaseAsync(PurchaseRequest request, CancellationToken ct = default)
    {
        if (!PriceRules.IsValid(request.ItemKind, request.Price))
            return Task.FromResult(new PurchaseResult(WriteStatus.Invalid, 0, "Invalid price."));
        string key = $"purchase/{request.AccountId}/{request.IdempotencyKey}";
        string itemRef = $"{request.ItemKind}:{request.ItemId}";
        return WriteAsync(async (c, tx) =>
        {
            await EnsureAccount(c, tx, request.AccountId);
            long balance = await LockWallet(c, tx, request.AccountId); // serializes this account's purchases
            var prior = await c.FirstOrDefaultAsync(tx, "SELECT item_ref, requested_amount FROM ledger_entries WHERE idempotency_key = @k",
                r => (Item: r.NStr(0), Amount: r.Long(1)), ("@k", key));
            if (prior.Item is not null)
                return prior.Item == itemRef && prior.Amount == -request.Price
                    ? new PurchaseResult(WriteStatus.Replayed, balance)
                    : new PurchaseResult(WriteStatus.Conflict, balance, "Idempotency key was used for a different purchase.");

            if (request.ItemKind != PriceRules.Car)
                return new PurchaseResult(WriteStatus.Invalid, balance, "Only cars are purchasable in this build.");
            bool owned = await c.FirstOrDefaultAsync(tx, "SELECT 1 FROM owned_cars WHERE account_id = @a AND car_id = @c",
                r => true, ("@a", request.AccountId), ("@c", request.ItemId));
            if (owned)
                return new PurchaseResult(WriteStatus.AlreadyOwned, balance);
            if (!Wallet.TryDebit(balance, request.Price, out long newBalance))
                return new PurchaseResult(WriteStatus.InsufficientFunds, balance);

            await InsertLedger(c, tx, request.AccountId, key, "purchase", null, itemRef, -request.Price, -request.Price, 0, newBalance);
            await SetBalance(c, tx, request.AccountId, newBalance);
            await c.ExecAsync(tx, "INSERT INTO owned_cars (account_id, car_id, source) VALUES (@a, @c, 'purchase')",
                ("@a", request.AccountId), ("@c", request.ItemId));
            return new PurchaseResult(WriteStatus.Ok, newBalance);
        }, ct);
    }

    // ---------------------------------------------------------------- matches and settlement

    public Task RecordAllocationAsync(MatchRecord match, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) => await c.ExecAsync(tx,
            "INSERT INTO matches (match_id, convoy_id, server_id, config_json, results_secret, state) VALUES (@m, @cv, @s, @cfg, @sec, 'allocated')",
            ("@m", match.MatchId), ("@cv", match.ConvoyId), ("@s", match.ServerId), ("@cfg", match.ConfigJson), ("@sec", match.ResultsSecret)), ct);

    public Task<MatchRecord?> GetMatchAsync(string matchId, CancellationToken ct = default) =>
        ReadAsync((c, tx) => c.FirstOrDefaultAsync(tx,
            "SELECT match_id, convoy_id, server_id, config_json, results_secret, state, results_sha256 FROM matches WHERE match_id = @m",
            r => new MatchRecord
            {
                MatchId = r.Str(0), ConvoyId = r.Str(1), ServerId = r.Str(2), ConfigJson = r.Str(3),
                ResultsSecret = r.Str(4), State = r.Str(5), ResultsSha256 = r.NStr(6),
            }, ("@m", matchId)), ct);

    public Task<bool> MarkAbortedAsync(string matchId, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) => await c.ExecAsync(tx,
            "UPDATE matches SET state = 'aborted', settled_at = CURRENT_TIMESTAMP WHERE match_id = @m AND state = 'allocated'",
            ("@m", matchId)) == 1, ct);

    public Task<string?> GetReceiptJsonAsync(string matchId, string accountId, CancellationToken ct = default) =>
        ReadAsync((c, tx) => c.FirstOrDefaultAsync(tx, "SELECT receipt_json FROM match_results WHERE match_id = @m AND account_id = @a",
            r => r.NStr(0), ("@m", matchId), ("@a", accountId)), ct);

    public Task<SettlementOutcome> SettleAsync(MatchSettlement s, CancellationToken ct = default) =>
        // Addendum 02 D209 (defence in depth behind SettlementService): a toy-domain kind never reaches the wallet/RP pipeline.
        Matches.ProgressionDomain.ToyViolation(s) is { } toy
            ? Task.FromException<SettlementOutcome>(new InvalidOperationException(toy))
            : WriteAsync(async (c, tx) =>
        {
            // Lock the match row first: concurrent retries of the same settlement serialize here.
            var match = await c.FirstOrDefaultAsync(tx, "SELECT state, results_sha256 FROM matches WHERE match_id = @m" + ForUpdate,
                r => (State: r.Str(0), Sha: r.NStr(1)), ("@m", s.MatchId));
            if (match.State is null)
                return new SettlementOutcome(SettlementStatus.NotFound, Array.Empty<Receipt>());
            if (match.State == "aborted")
                return new SettlementOutcome(SettlementStatus.Aborted, Array.Empty<Receipt>());
            if (match.State == "settled")
            {
                var stored = await c.QueryAsync(tx, "SELECT receipt_json FROM match_results WHERE match_id = @m ORDER BY account_id",
                    r => JsonSerializer.Deserialize<Receipt>(r.Str(0), Json)!, ("@m", s.MatchId));
                return new SettlementOutcome(match.Sha == s.ResultsSha256 ? SettlementStatus.AlreadySettled : SettlementStatus.Conflict, stored);
            }

            var receipts = new List<Receipt>();
            foreach (EntrantSettlement e in s.Entrants.OrderBy(x => x.AccountId, StringComparer.Ordinal)) // stable lock order
                receipts.Add(await SettleEntrant(c, tx, s.MatchId, e));

            await c.ExecAsync(tx, "UPDATE matches SET state = 'settled', results_sha256 = @h, settled_at = CURRENT_TIMESTAMP WHERE match_id = @m",
                ("@h", s.ResultsSha256), ("@m", s.MatchId));
            return new SettlementOutcome(SettlementStatus.Settled, receipts);
        }, ct);

    async Task<Receipt> SettleEntrant(DbConnection c, DbTransaction tx, string matchId, EntrantSettlement e)
    {
        string a = e.AccountId;
        // Work on a copy: WriteAsync may re-run this transaction body after a lost UNIQUE race.
        Receipt receipt = JsonSerializer.Deserialize<Receipt>(JsonSerializer.Serialize(e.Receipt, Json), Json)!;
        await EnsureAccount(c, tx, a);
        long balance = await LockWallet(c, tx, a);
        receipt.RankPointsBefore = (await LoadRank(c, tx, a)).RankPoints;

        // First clear: Core decides whether this stage may be marked (never beyond the frontier, never twice);
        // the UNIQUE constraint makes the award once-only even across concurrent matches.
        PayoutFacts facts = Copy(e.Facts); // never mutate the caller's input (the body may be re-run)
        bool clearValid = false;
        if (e.Clear is { } clear)
        {
            (bool[] normal, bool[] hard) = await LoadClears(c, tx, a);
            bool newClear, beyondFrontier = false;
            try
            {
                newClear = CampaignProgress.ApplyClear(clear.Mode == CampaignMode.Hard ? hard : normal, clear.StageNumber);
            }
            catch (InvalidOperationException ex)
            {
                newClear = false;
                beyondFrontier = true;
                receipt.Notes.Add(ex.Message);
            }
            if (newClear && await c.ExecAsync(tx,
                    "INSERT INTO stage_clears (account_id, mode, stage, match_id) VALUES (@a, @mode, @st, @m) ON CONFLICT DO NOTHING",
                    ("@a", a), ("@mode", clear.Mode == CampaignMode.Hard ? "hard" : "normal"), ("@st", clear.StageNumber), ("@m", matchId)) == 1)
            {
                facts.FirstClearBonus = Economy.FirstClearBonus(clear.Type, clear.Mode);
                receipt.FirstClearAwarded = true;
            }
            else if (!beyondFrontier)
                receipt.Notes.Add("Stage already cleared: ordinary race money only, no first-clear bonus or RP.");
            clearValid = !beyondFrontier;
        }

        // Free course unlocks from a valid Normal clear (Core CourseAccess.GrantedByNormalClear), idempotent via the
        // (account, course) key. A course bought earlier keeps its purchase row: no refund and no duplicate.
        if (clearValid && e.Clear!.Mode == CampaignMode.Normal)
            foreach (string course in e.ClearCourseGrants)
            {
                bool inserted = await c.ExecAsync(tx,
                    "INSERT INTO course_entitlements (account_id, course_id, source, match_id) VALUES (@a, @c, 'campaign-clear', @m) ON CONFLICT DO NOTHING",
                    ("@a", a), ("@c", course), ("@m", matchId)) == 1;
                if (inserted && receipt.FirstClearAwarded) receipt.CoursesUnlocked.Add(course);
                else if (!inserted && receipt.FirstClearAwarded &&
                         await c.FirstOrDefaultAsync(tx, "SELECT source FROM course_entitlements WHERE account_id = @a AND course_id = @c",
                             r => r.Str(0), ("@a", a), ("@c", course)) == "purchase")
                    receipt.Notes.Add($"You already owned {course} (purchased early); clearing keeps it — no refund or duplicate reward.");
            }
        // Soundtrack cues: only from eligible settled results, once per (account, cue).
        if (clearValid)
            foreach (MusicGrant g in e.ClearMusicGrants)
                if (await GrantMusic(c, tx, a, g, matchId)) receipt.MusicUnlocked.Add(g.CueId);
        foreach (MusicGrant g in e.MusicGrants)
            if (await GrantMusic(c, tx, a, g, matchId)) receipt.MusicUnlocked.Add(g.CueId);

        if (e.TeamBest is { } best && receipt.TeamTrial is { } trial)
        {
            long? stored = await c.FirstOrDefaultAsync<long?>(tx,
                "SELECT team_value FROM team_trial_bests WHERE account_id = @a AND trial_id = @t AND difficulty = @d AND humans = @h" + ForUpdate,
                r => r.Long(0), ("@a", a), ("@t", best.TrialId), ("@d", best.Difficulty), ("@h", best.Humans));
            bool better = stored is null || (best.LowerIsBetter ? best.Value < stored : best.Value > stored);
            if (better)
                await c.ExecAsync(tx,
                    "INSERT INTO team_trial_bests (account_id, trial_id, difficulty, humans, kind, team_value, match_id) VALUES (@a, @t, @d, @h, @k, @v, @m) " +
                    "ON CONFLICT (account_id, trial_id, difficulty, humans) DO UPDATE SET team_value = excluded.team_value, match_id = excluded.match_id, " +
                    "kind = excluded.kind, updated_at = CURRENT_TIMESTAMP",
                    ("@a", a), ("@t", best.TrialId), ("@d", best.Difficulty), ("@h", best.Humans), ("@k", best.Kind), ("@v", best.Value), ("@m", matchId));
            trial.NewTeamBest = better;
        }

        long challengeCash = 0;
        var challengeLines = new List<(string Id, long Cash)>();
        foreach (ChallengeGrant g in e.Challenges)
        {
            int inserted = await c.ExecAsync(tx,
                "INSERT INTO challenge_unlocks (account_id, challenge_id, tier, match_id) VALUES (@a, @ch, @t, @m) ON CONFLICT DO NOTHING",
                ("@a", a), ("@ch", g.ChallengeId), ("@t", g.Tier.ToString().ToLowerInvariant()), ("@m", matchId));
            if (inserted == 0)
            {
                receipt.Notes.Add($"{g.ChallengeId} was already completed; no repeat reward.");
                continue;
            }
            await c.ExecAsync(tx, "INSERT INTO cosmetics_owned (account_id, cosmetic_id, source) VALUES (@a, @cos, @src) ON CONFLICT DO NOTHING",
                ("@a", a), ("@cos", g.CosmeticId), ("@src", g.ChallengeId));
            challengeCash += g.Cash;
            challengeLines.Add((g.ChallengeId, g.Cash));
            receipt.ChallengesUnlocked.Add(g.ChallengeId);
            receipt.CosmeticsGranted.Add(g.CosmeticId);
        }
        facts.NewlyCompletedChallengeCash = challengeCash;

        PayoutBreakdown payout = e.PayoutWithheld is { } withheld
            ? new PayoutBreakdown { Note = withheld, ChallengeCash = challengeCash }
            : Economy.Compute(facts);
        receipt.Payout = PayoutInfo.From(payout);

        var lines = new List<(string Type, long Amount)> { ("event", payout.EventCredits) };
        if (payout.FirstClearBonus > 0) lines.Add(("first-clear", payout.FirstClearBonus));
        lines.AddRange(challengeLines.Select(l => ($"challenge:{l.Id}", l.Cash)));
        foreach ((string type, long amount) in lines.Where(l => l.Amount > 0))
        {
            WalletCredit credit = Wallet.Credit(balance, amount); // Core semantics: clamp at the cap, record the loss
            balance = credit.NewBalance;
            await InsertLedger(c, tx, a, $"{matchId}/{a}/{type}", type, matchId, null, amount, credit.Credited, credit.ClampedAway, balance);
            receipt.Credits.Add(new CreditLine(type, amount, credit.Credited, credit.ClampedAway));
            receipt.ClampedAwayTotal += credit.ClampedAway;
        }
        if (receipt.ClampedAwayTotal > 0)
            receipt.Notes.Add($"Wallet cap {Limits.WalletCap:N0} reached: {receipt.ClampedAwayTotal:N0} credits could not be added.");
        await SetBalance(c, tx, a, balance);
        receipt.BalanceAfter = balance;

        RankSummary rank = await LoadRank(c, tx, a);
        receipt.RankPointsAfter = rank.RankPoints;
        receipt.Rank = rank.Name;

        await c.ExecAsync(tx, "INSERT INTO match_results (match_id, account_id, receipt_json) VALUES (@m, @a, @r)",
            ("@m", matchId), ("@a", a), ("@r", JsonSerializer.Serialize(receipt, Json)));
        return receipt;
    }

    static PayoutFacts Copy(PayoutFacts f) => new()
    {
        AuthoredExpectedSeconds = f.AuthoredExpectedSeconds, Kind = f.Kind, Mode = f.Mode, Outcome = f.Outcome, Placement = f.Placement,
        ReferenceBeaten = f.ReferenceBeaten, Clean = f.Clean, UtilityIncomePercent = f.UtilityIncomePercent,
        PvPWinnerBonusEligible = f.PvPWinnerBonusEligible, CheckpointFraction = f.CheckpointFraction,
        ServerVerifiedActiveProgress = f.ServerVerifiedActiveProgress, TutorialRepeat = f.TutorialRepeat,
        FirstClearBonus = 0, NewlyCompletedChallengeCash = 0, // decided inside the transaction
    };

    static async Task<RankSummary> LoadRank(DbConnection c, DbTransaction tx, string accountId)
    {
        var clears = await c.QueryAsync(tx, "SELECT mode, COUNT(*) FROM stage_clears WHERE account_id = @a GROUP BY mode",
            r => (Mode: r.Str(0), Count: r.Int(1)), ("@a", accountId));
        var tiers = await c.QueryAsync(tx, "SELECT tier, COUNT(*) FROM challenge_unlocks WHERE account_id = @a GROUP BY tier",
            r => (Tier: r.Str(0), Count: r.Int(1)), ("@a", accountId));
        int Clears(string m) => clears.Where(x => x.Mode == m).Sum(x => x.Count);
        int Tier(string t) => tiers.Where(x => x.Tier == t).Sum(x => x.Count);
        return RankSummary.Compute(Clears("normal"), Clears("hard"), Tier("bronze"), Tier("silver"), Tier("gold"));
    }
}
