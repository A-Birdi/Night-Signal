using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using NightSignal.ControlPlane.Configuration;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Garage;
using NightSignal.ControlPlane.Persistence;
using NightSignal.Core.Builds;

namespace NightSignal.Services.Tests.Infrastructure;

/// <summary>
/// A real SQLite store (all migrations applied), a real ConvoyDirectory and the real Garage service over the authored
/// parts/recipes copied into the test output (content/authored). No HTTP.
/// </summary>
public sealed class GarageTestKit : IAsyncDisposable
{
    static readonly Lazy<GarageContent> SharedContent = new(() =>
        GarageContent.FromContentDirectory(Options.Create(new ContentOptions()), TestData.Content, NullLogger<GarageContent>.Instance));

    /// <summary>parts.json + build-recipes.json as the control plane loads them (content/authored).</summary>
    public static GarageContent Content => SharedContent.Value;

    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    readonly TempDir dir = new();
    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));
    public RecordingNotifier Notifier { get; } = new();
    public SqliteGameStore Store { get; private set; } = null!;
    public ConvoyDirectory Convoys { get; private set; } = null!;
    public GarageService Garage { get; private set; } = null!;
    public string DbPath => dir.File("garage.db");

    public static async Task<GarageTestKit> CreateAsync()
    {
        var kit = new GarageTestKit();
        kit.Store = new SqliteGameStore(kit.DbPath);
        await kit.Store.InitializeAsync();
        kit.Convoys = new ConvoyDirectory(kit.Clock, TestData.Content, kit.Notifier);
        kit.Garage = kit.ServiceWith(Content);
        return kit;
    }

    public GarageService ServiceWith(GarageContent content) =>
        new(Store, Store, TestData.Content, content, Convoys, Clock, NullLogger<GarageService>.Instance);

    /// <summary>An account owning <paramref name="car"/> with a wallet balance and <paramref name="normalCleared"/> Normal clears.</summary>
    public async Task<string> PlayerAsync(int n, string car = "V01", long balance = 0, int normalCleared = 0)
    {
        string account = $"00000000-0000-4000-8000-0000000000{n:00}";
        await Store.EnsureAccountAsync(account);
        await Store.ClaimStarterAsync(account, car, 0);
        Exec("UPDATE wallets SET balance = $b WHERE account_id = $a", ("$b", balance), ("$a", account));
        for (int s = 1; s <= normalCleared; s++)
            Exec("INSERT INTO stage_clears (account_id, mode, stage, match_id) VALUES ($a, 'normal', $s, 'm_seed')", ("$a", account), ("$s", s));
        return account;
    }

    /// <summary>The account's (first) instance of <paramref name="car"/>, creating instances/workspaces like GET /v1/me/garage/cars.</summary>
    public async Task<string> InstanceAsync(string account, string car = "V01")
    {
        Assert.Equal(200, (await Garage.ListCarsAsync(account, CancellationToken.None)).Status);
        return (await Store.EnsureCarInstancesAsync(account)).First(i => i.CarId == car).InstanceId;
    }

    /// <summary>Grants parts directly (test setup for owned parts; production grants only through a settled quote).</summary>
    public void Grant(string account, string instanceId, params string[] parts)
    {
        foreach (string p in parts)
            Exec("INSERT INTO car_part_ownership (instance_id, part_id, account_id, source, quote_id, price) VALUES ($i, $p, $a, 'purchase', 'q-seed', 0)",
                ("$i", instanceId), ("$p", p), ("$a", account));
    }

    public long Scalar(string sql, params (string, object)[] args)
    {
        using var c = new SqliteConnection($"Data Source={DbPath}");
        c.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string n, object v) in args) cmd.Parameters.AddWithValue(n, v);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Exec(string sql, params (string, object)[] args)
    {
        using var c = new SqliteConnection($"Data Source={DbPath}");
        c.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string n, object v) in args) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    public long Balance(string account) => Scalar("SELECT balance FROM wallets WHERE account_id = $a", ("$a", account));
    public long WorkspaceRevision(string instanceId) => Scalar("SELECT revision FROM car_workspaces WHERE instance_id = $i", ("$i", instanceId));
    public long OwnedCount(string instanceId) => Scalar("SELECT COUNT(*) FROM car_part_ownership WHERE instance_id = $i", ("$i", instanceId));

    public static JsonElement Body(GarageReply r) => JsonSerializer.SerializeToElement(r.Body, Web);

    public static string ErrorOf(GarageReply r) => Body(r).GetProperty("error").GetString()!;

    public static BuildInput Build(params string[] parts) => Build(null, parts);

    public static BuildInput Build(Dictionary<string, int>? tuning, params string[] parts)
    {
        var slots = new Dictionary<string, string?>();
        string? utility = null;
        foreach (string id in parts)
        {
            PartDef p = Content.Parts.Part(id);
            if (p.SlotValue == PartSlot.Utility) utility = id;
            else slots[PartSlots.Id(p.SlotValue)] = id;
        }
        return new BuildInput(slots, utility, tuning is null ? null : new TuningInput(TuningModel.CurrentVersion, tuning));
    }

    public static BuildInput FromRecipe(RecipeStep step)
    {
        MechanicalSnapshot s = RecipeBook.ToSnapshot(step, Content.Parts);
        return new BuildInput(s.Parts.ToDictionary(kv => kv.Key, kv => (string?)kv.Value), s.UtilityPartId,
            new TuningInput(s.Tuning.Version, new Dictionary<string, int>(s.Tuning.Values)));
    }

    /// <summary>parts.json with one price changed and the price revision bumped (a real catalogue price edit).</summary>
    public static GarageContent WithPriceChange(string partId, long newPrice)
    {
        string authored = Path.Combine(AppContext.BaseDirectory, "content", "authored");
        JObject parts = JObject.Parse(File.ReadAllText(Path.Combine(authored, GarageContent.PartsFile)));
        parts["priceRevision"] = (int)parts["priceRevision"]! + 1;
        foreach (JObject p in parts["parts"]!.OfType<JObject>())
            if ((string?)p["id"] == partId) p["price"] = newPrice;
        return GarageContent.From(parts.ToString(), File.ReadAllText(Path.Combine(authored, GarageContent.RecipesFile)), TestData.Content.Catalogue);
    }

    public ValueTask DisposeAsync()
    {
        dir.Dispose();
        return ValueTask.CompletedTask;
    }
}
