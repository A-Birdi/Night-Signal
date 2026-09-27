using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Garage;
using NightSignal.ControlPlane.Matches;
using NightSignal.ControlPlane.Persistence;
using NightSignal.Core.Builds;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>
/// Performance truth between the ONLINE Garage and the convoy (Addendum 02 §9.2, §10.5): the server's applied build decides
/// the loadout hash and PI, only a PERFORMANCE change unreadies (and only that player), caps use the server PI, the start
/// freezes the server build, and Last Race Build records exactly that frozen build.
/// </summary>
public sealed class GarageConvoyTests : ConvoyTestBase, IAsyncLifetime
{
    readonly TempDir tmp = new();
    SqliteGameStore store = null!;
    GarageService garage = null!;
    static readonly CancellationToken None = CancellationToken.None;

    public async Task InitializeAsync()
    {
        store = new SqliteGameStore(tmp.File("garage-convoy.db"));
        await store.InitializeAsync();
        garage = Service(dir);
    }

    public Task DisposeAsync()
    {
        tmp.Dispose();
        return Task.CompletedTask;
    }

    GarageService Service(ConvoyDirectory convoys) =>
        new(store, store, TestData.Content, GarageTestKit.Content, convoys, clock, NullLogger<GarageService>.Instance);

    /// <summary>A Garage writer that the convoy directory does not hear from (another process / a missed refresh).</summary>
    GarageService Detached() => Service(new ConvoyDirectory(clock, TestData.Content, new RecordingNotifier()));

    async Task<string> OwnerAsync(int i, string car = "V01")
    {
        await store.EnsureAccountAsync(Id(i));
        await store.ClaimStarterAsync(Id(i), car, 0);
        Assert.Equal(200, (await garage.ListCarsAsync(Id(i), None)).Status);
        return (await store.EnsureCarInstancesAsync(Id(i))).Single(x => x.CarId == car).InstanceId;
    }

    void Grant(int i, string instance, params string[] parts)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={tmp.File("garage-convoy.db")}");
        c.Open();
        foreach (string p in parts)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO car_part_ownership (instance_id, part_id, account_id, source, price) VALUES ($i, $p, $a, 'purchase', 0)";
            cmd.Parameters.AddWithValue("$i", instance);
            cmd.Parameters.AddWithValue("$p", p);
            cmd.Parameters.AddWithValue("$a", Id(i));
            cmd.ExecuteNonQuery();
        }
    }

    async Task<LoadoutInfo> SelectAsync(int i, string cosmetic = "paint-1")
    {
        (LoadoutInfo? l, ConvoyError? e) = await garage.SelectionAsync(Id(i), "V01", null, cosmetic, None);
        Assert.Null(e);
        Assert.True(dir.UpdateLoadout(Id(i), l!).Ok);
        return l!;
    }

    async Task<long> RevisionAsync(GarageService g, int i, string instance) =>
        GarageTestKit.Body(await g.GetCarAsync(Id(i), instance, None)).GetProperty("workspace").GetProperty("revision").GetInt64();

    /// <summary>edit-draft + apply through <paramref name="g"/> (the whole-build Core Apply).</summary>
    async Task<JsonElement> ApplyAsync(GarageService g, int i, string instance, BuildInput build)
    {
        long r = await RevisionAsync(g, i, instance);
        Assert.True((await g.OperateAsync(Id(i), instance, new GarageOpRequest("edit-draft", r, Build: build), None)).Ok);
        GarageReply applied = await g.OperateAsync(Id(i), instance, new GarageOpRequest("apply", r + 1), None);
        Assert.True(applied.Ok, JsonSerializer.Serialize(applied.Body));
        return GarageTestKit.Body(applied);
    }

    async Task<(string I1, string I2)> ConvoyOfTwoAsync()
    {
        string i1 = await OwnerAsync(1), i2 = await OwnerAsync(2);
        dir.Connected(Id(1), V);
        dir.Connected(Id(2), V);
        Assert.True(dir.Create(Id(1), Info(1), ConvoyPrivacy.InviteOnly).Ok);
        Assert.True(dir.JoinByCode(Id(2), Info(2), Code(dir.CreateInvite(Id(1)))).Ok);
        await SelectAsync(1);
        await SelectAsync(2);
        return (i1, i2);
    }

    static string StockHash(string car = "V01") =>
        BuildContext.Create(TestData.Content.Catalogue, GarageTestKit.Content.Parts, car, new PartInventory(), 1).Stock.BuildHash;

    [Fact]
    public async Task Selection_CarriesTheServerHashPiAndInstance()
    {
        string i1 = await OwnerAsync(1);
        (LoadoutInfo? l, ConvoyError? e) = await garage.SelectionAsync(Id(1), "V01", null, "paint-1", None);
        Assert.Null(e);
        Assert.Equal(StockHash(), l!.PerformanceHash);
        Assert.Equal(220, l.CarPi);
        Assert.Equal(i1, l.InstanceId);
        Assert.Equal(1, l.AppliedRevision);
        Assert.Equal("not_owned", (await garage.SelectionAsync(Id(1), "V18", null, "c", None)).Error!.Code);
        Assert.Equal("not_owned", (await garage.SelectionAsync(Id(1), null, "ci_not_mine", "c", None)).Error!.Code);
    }

    [Fact]
    public async Task OnlyAPerformanceChange_UnreadiesAndOnlyThatPlayer_CosmeticAndUtilityOnlyStayReady()
    {
        (string _, string i2) = await ConvoyOfTwoAsync();
        long rev = OpenEvent("S01");
        ReadyAll(rev);
        long loadoutRev = MemberState(1, 2).GetProperty("loadoutRevision").GetInt64();

        // Cosmetic-only: a new livery keeps Event Ready.
        await SelectAsync(2, "paint-2");
        Assert.True(Ready(2));
        Assert.Equal(loadoutRev, MemberState(1, 2).GetProperty("loadoutRevision").GetInt64());

        // Utility-only apply: the physics BuildHash is unchanged, so readiness stays.
        Grant(2, i2, "UTL-INC-4", "TYR-T1-STREET");
        JsonElement utility = await ApplyAsync(garage, 2, i2, GarageTestKit.Build("UTL-INC-4"));
        Assert.False(utility.GetProperty("performanceChanged").GetBoolean());
        Assert.True(Ready(2));
        Assert.Equal(loadoutRev, MemberState(1, 2).GetProperty("loadoutRevision").GetInt64());

        // Performance apply of the SELECTED car: only member 2 unreadies; the leader stays ready.
        JsonElement tyres = await ApplyAsync(garage, 2, i2, GarageTestKit.Build("UTL-INC-4", "TYR-T1-STREET"));
        Assert.True(tyres.GetProperty("performanceChanged").GetBoolean());
        Assert.True(tyres.GetProperty("convoy").GetProperty("performanceChanged").GetBoolean());
        Assert.False(Ready(2));
        Assert.True(Ready(1));
        Assert.Equal(loadoutRev + 1, MemberState(1, 2).GetProperty("loadoutRevision").GetInt64());
        Assert.Equal("stale_revision", dir.SetReady(Id(2), rev, loadoutRev, true).Error?.Code);
        Assert.True(dir.SetReady(Id(2), rev, loadoutRev + 1, true).Ok);
        Assert.Equal(tyres.GetProperty("workspace").GetProperty("applied").GetProperty("buildHash").GetString(), dir.LoadoutOf(Id(2))!.PerformanceHash);
    }

    [Fact]
    public async Task EventReady_RereadsTheServerBuild_AndACapUsesTheServerPi()
    {
        (string _, string i2) = await ConvoyOfTwoAsync();
        long rev = OpenEvent("S01"); // D cap 299
        ReadyAll(rev);
        long loadoutRev = MemberState(1, 2).GetProperty("loadoutRevision").GetInt64();

        // Member 2's applied build changes where the directory did not hear it (another process): event.ready re-reads it.
        RecipeStep late = GarageTestKit.Content.Recipes.Car("V01")!.Path.Last();
        MechanicalSnapshot lateBuild = RecipeBook.ToSnapshot(late, GarageTestKit.Content.Parts);
        Grant(2, i2, lateBuild.AllPartIds().ToArray());
        JsonElement applied = await ApplyAsync(Detached(), 2, i2, GarageTestKit.FromRecipe(late));
        int serverPi = applied.GetProperty("workspace").GetProperty("applied").GetProperty("pi").GetInt32();
        Assert.True(serverPi > 299, $"expected the late V01 recipe above the D cap, got PI {serverPi}");
        Assert.True(Ready(2)); // the directory still holds the stale selection

        (LoadoutInfo? fresh, ConvoyError? error) = await garage.FreshSelectionAsync(Id(2), None);
        Assert.Null(error);
        Assert.Equal(serverPi, fresh!.CarPi);
        ConvoyResult stale = dir.SetReady(Id(2), rev, loadoutRev, true, fresh);
        Assert.Equal("stale_revision", stale.Error?.Code);
        Assert.False(Ready(2));
        Assert.True(Ready(1));

        // Readying with the new loadout revision is refused by the cap because the SERVER PI (not the catalogue 220) is over it.
        (fresh, _) = await garage.FreshSelectionAsync(Id(2), None);
        ConvoyResult illegal = dir.SetReady(Id(2), rev, loadoutRev + 1, true, fresh);
        Assert.Equal("loadout_illegal", illegal.Error?.Code);
        Assert.Contains(serverPi.ToString(), illegal.Error!.Message);
    }

    [Fact]
    public async Task Start_FreezesTheServerBuilds_AndRefusesABuildThatChangedAfterReady()
    {
        (string i1, string i2) = await ConvoyOfTwoAsync();
        long rev = OpenEvent("S01");
        ReadyAll(rev);

        // A change nobody told the directory about (other process): the start revalidates and unreadies only that entrant.
        Grant(2, i2, "TYR-T1-STREET");
        await ApplyAsync(Detached(), 2, i2, GarageTestKit.Build("TYR-T1-STREET"));
        IReadOnlyDictionary<string, EntrantBuild> builds = await garage.FreezeSelectionsAsync(new[] { Id(1), Id(2) }, None);
        (ConvoyError? refused, MatchPlan? none) = dir.BeginStart(Id(1), rev, Fresh(2), null, builds);
        Assert.Null(none);
        Assert.Equal("not_all_ready", refused!.Code);
        Assert.False(Ready(2));
        Assert.True(Ready(1));

        Assert.True(dir.SetReady(Id(2), rev, MemberState(1, 2).GetProperty("loadoutRevision").GetInt64(), true).Ok);
        builds = await garage.FreezeSelectionsAsync(new[] { Id(1), Id(2) }, None);
        (ConvoyError? error, MatchPlan? plan) = dir.BeginStart(Id(1), rev, Fresh(2), null, builds);
        Assert.Null(error);
        PlannedEntrant e1 = plan!.Entrants.Single(e => e.AccountId == Id(1));
        PlannedEntrant e2 = plan.Entrants.Single(e => e.AccountId == Id(2));
        Assert.Equal(i1, e1.Build!.InstanceId);
        Assert.Equal(StockHash(), e1.Build.BuildHash);
        Assert.Equal(220, e1.Build.Pi);
        Assert.Equal("TYR-T1-STREET", e2.Build!.Parts["tyres"]);
        Assert.Equal(2, e2.Build.AppliedRevision);
        Assert.Equal(e2.Loadout.PerformanceHash, e2.Build.BuildHash);
        Assert.Equal(SimParams.All.Length, e2.Build.ParamsMicro.Count);
        Assert.Equal(GarageTestKit.Content.Hash, e2.Build.PartsCatalogueHash);

        // While Allocating, the selected car's build is frozen in the Garage (the selector cannot bypass the event).
        long r2 = await RevisionAsync(garage, 2, i2);
        Assert.True((await garage.OperateAsync(Id(2), i2, new GarageOpRequest("edit-draft", r2, Build: GarageTestKit.Build()), None)).Ok);
        GarageReply frozen = await garage.OperateAsync(Id(2), i2, new GarageOpRequest("apply", r2 + 1), None);
        Assert.Equal(409, frozen.Status);
        Assert.Equal("build_locked", GarageTestKit.ErrorOf(frozen));
    }

    [Fact]
    public async Task RecordRaceBegan_StoresTheFrozenBuildAsLastRaceBuild_NotTheLaterApplied()
    {
        string i1 = await OwnerAsync(1);
        (EntrantBuild? frozen, _) = await garage.FreezeAsync(Id(1), i1, None);
        Grant(1, i1, "TYR-T1-STREET");
        await ApplyAsync(garage, 1, i1, GarageTestKit.Build("TYR-T1-STREET")); // applied revision 2 after the freeze

        Assert.True(await garage.RecordRaceBeganAsync(Id(1), frozen!, "m_frozen", None));
        JsonElement ws = GarageTestKit.Body(await garage.GetCarAsync(Id(1), i1, None)).GetProperty("workspace");
        JsonElement last = ws.GetProperty("references").GetProperty("last-race-build");
        Assert.Equal("m_frozen", last.GetProperty("context").GetString());
        Assert.Equal(1, last.GetProperty("sourceAppliedRevision").GetInt64());
        Assert.Equal(StockHash(), last.GetProperty("buildHash").GetString());
        Assert.Equal(2, ws.GetProperty("applied").GetProperty("revision").GetInt64()); // the applied build is untouched

        // A frozen build newer than the car's applied revision is refused by Core (never a fabricated reference).
        Assert.False(await garage.RecordRaceBeganAsync(Id(1), frozen! with { AppliedRevision = 99 }, "m_bad", None));
    }

    [Fact]
    public void SettlementUtilityIncome_ComesFromTheFrozenBuild()
    {
        static MatchAssignment Match(params AssignedEntrant[] entrants) => new()
        {
            MatchId = "m_u", ConvoyId = "cv", ServerId = "srv", Kind = "campaign", Mode = "normal", StageId = "S01", StageNumber = 1, StageType = "regular",
            CourseId = "C01", Weather = "stage-default", Collision = "light-contact", CarCapPi = 299, Entrants = entrants, AiEntrants = Array.Empty<string>(),
            Benchmark = new AssignedBenchmark("Time", 180_000, 0, 390_000, true, "test"), Build = "b", Protocol = 1, ContentHash = "c", Seed = 1,
            ResultsUrl = "", TicketIssuer = "i", TicketAudience = "a",
        };
        static AssignedEntrant Entrant(string id, int? income) => new(id, id, "racer", "V01", 220, "p", "c", 1,
            income is null ? null : new EntrantBuild { InstanceId = "ci_x", CarId = "V01", Utility = new BuildUtility("UTL", income.Value, 0) });
        MatchAssignment m = Match(Entrant(Id(1), 8), Entrant(Id(2), null), Entrant(Id(3), 5), Entrant(Id(4), 4));
        Assert.Equal(8, SettlementService.UtilityIncomePercent(m, Id(1)));
        Assert.Equal(0, SettlementService.UtilityIncomePercent(m, Id(2)));
        Assert.Equal(0, SettlementService.UtilityIncomePercent(m, Id(3))); // not an authored income value → none
        Assert.Equal(4, SettlementService.UtilityIncomePercent(m, Id(4)));

        var service = new SettlementService(null!, null!, TestData.Content, null!, null!, null!);
        EntrantFacts F(int i) => new()
        {
            EntrantId = Id(i), Human = true, Outcome = RunOutcome.Finished, FinishTimeMicros = (170 + i) * 1_000_000L, Placement = i, Clean = false,
            CheckpointFraction = 1, ActiveProgressVerified = true, ActivelyDroveLegalCourse = true, LegalProgressMetres = 3000,
        };
        (MatchSettlement? s, string? error) = service.Compute(m, new ResultSubmission { MatchId = "m_u", ContentHash = "c", Entrants = new() { F(1), F(2), F(3), F(4) } }, "h");
        Assert.Null(error);
        Assert.Equal(108, Economy.Compute(s!.Entrants.Single(e => e.AccountId == Id(1)).Facts).UtilityX100);
        Assert.Equal(100, Economy.Compute(s.Entrants.Single(e => e.AccountId == Id(2)).Facts).UtilityX100);
        Assert.Equal(104, Economy.Compute(s.Entrants.Single(e => e.AccountId == Id(4)).Facts).UtilityX100);
    }

    // The frozen record survives the JSON round trip the stored match config takes (settlement reads it back).
    [Fact]
    public async Task FrozenBuild_RoundTripsThroughTheStoredAssignmentJson()
    {
        string i1 = await OwnerAsync(1);
        Grant(1, i1, "UTL-INC-4");
        await ApplyAsync(garage, 1, i1, GarageTestKit.Build("UTL-INC-4"));
        (EntrantBuild? b, _) = await garage.FreezeAsync(Id(1), i1, None);
        var entrant = new AssignedEntrant(Id(1), "D1", "racer", "V01", b!.Pi, b.BuildHash, "c", 1, b);
        string json = JsonSerializer.Serialize(entrant, MatchAllocator.Json);
        Assert.Contains("\"vehicleBuild\"", json);
        AssignedEntrant back = JsonSerializer.Deserialize<AssignedEntrant>(json, MatchAllocator.Json)!;
        Assert.Equal(4, back.VehicleBuild!.Utility.IncomePercent);
        Assert.Equal(b.BuildHash, back.VehicleBuild.BuildHash);
        Assert.Equal(b.ParamsMicro["MassKg"], back.VehicleBuild.ParamsMicro["MassKg"]);
        Assert.Equal(b.Snapshot().SelectionHash(), back.VehicleBuild.Snapshot().SelectionHash());
        // Re-resolving the frozen selection with Core reproduces the frozen hash (what the game server can verify).
        BuildContext ctx = BuildContext.Create(TestData.Content.Catalogue, GarageTestKit.Content.Parts, "V01", new PartInventory(), 4);
        Assert.Equal(b.BuildHash, BuildResolver.Resolve(ctx.Car, ctx.Tuning, ctx.Parts, back.VehicleBuild.Snapshot()).Spec.BuildHash);
    }
}
