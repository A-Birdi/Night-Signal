using NightSignal.Core.Builds;
using NightSignal.Core.Content;

namespace NightSignal.BuildsTests;

/// <summary>Loads the REAL project data (Assets/Content/Data) once: content catalogue, parts catalogue, recipes.</summary>
public static class TestData
{
    static readonly Lazy<string> root = new(FindDataRoot);
    static readonly Lazy<ContentCatalogue> content = new(LoadContent);
    static readonly Lazy<PartsCatalogue> parts = new(() => PartsCatalogue.Load(PartsJson));
    static readonly Lazy<RecipeBook> recipes = new(() => RecipeBook.Load(File.ReadAllText(Path.Combine(root.Value, "authored", "build-recipes.json"))));

    public static string DataRoot => root.Value;
    public static ContentCatalogue Content => content.Value;
    public static PartsCatalogue Parts => parts.Value;
    public static RecipeBook Recipes => recipes.Value;
    public static string PartsJson => File.ReadAllText(Path.Combine(root.Value, "authored", "parts.json"));

    public static readonly DateTime T0 = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    public static BuildContext Ctx(string model, IPartOwnership ownership = null, int shopAct = 4) =>
        BuildContext.Create(Content, Parts, model, ownership ?? new PartInventory(), shopAct);

    public static IEnumerable<object[]> AllCars() => Content.Cars.Select(c => new object[] { c.Id });

    static string FindDataRoot()
    {
        for (DirectoryInfo d = new(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "Assets", "Content", "Data");
            if (Directory.Exists(Path.Combine(candidate, "generated"))) return candidate;
        }
        throw new DirectoryNotFoundException("Assets/Content/Data not found above the test output folder.");
    }

    static ContentCatalogue LoadContent()
    {
        var docs = new Dictionary<string, string>();
        foreach (string f in ContentCatalogue.RequiredFiles) docs[f] = File.ReadAllText(Path.Combine(root.Value, "generated", f));
        foreach (string f in ContentCatalogue.AuthoredFiles)
        {
            string p = Path.Combine(root.Value, "authored", f);
            if (File.Exists(p)) docs[f] = File.ReadAllText(p);
        }
        return ContentCatalogue.Load(docs);
    }

    /// <summary>Snapshot from part ids (slots looked up in the catalogue).</summary>
    public static MechanicalSnapshot Build(params string[] partIds)
    {
        var s = new MechanicalSnapshot();
        foreach (string id in partIds)
        {
            PartDef p = Parts.Part(id);
            if (p.SlotValue == PartSlot.Utility) s.UtilityPartId = id;
            else s.Parts[PartSlots.Id(p.SlotValue)] = id;
        }
        return s;
    }

    public static ResolvedCarSpec Resolve(string model, MechanicalSnapshot build)
    {
        BuildContext ctx = Ctx(model);
        ResolveResult r = BuildResolver.Resolve(ctx.Car, ctx.Tuning, Parts, build);
        Assert.True(r.Ok, string.Join("; ", r.Issues));
        return r.Spec;
    }

    public static int Pi(string model, MechanicalSnapshot build)
    {
        BuildContext ctx = Ctx(model);
        return PerformanceIndexEstimator.Estimate(Resolve(model, build), ctx.Stock, ctx.Car.BasePI).Value;
    }

    /// <summary>Owns every part id for the instance (tests that need a fully owned build).</summary>
    public static PartInventory Owning(string instanceId, params string[] partIds)
    {
        var inv = new PartInventory();
        foreach (string id in partIds) inv.Grant(instanceId, id);
        return inv;
    }
}
