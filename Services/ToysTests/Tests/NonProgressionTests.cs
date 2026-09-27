using System.Reflection;
using System.Text.RegularExpressions;
using NightSignal.Core.Toys;

namespace NightSignal.Toys.Tests;

/// <summary>
/// C09 / D209 / §11: nothing in the toy domain can return Credits, RP, challenge progress, soundtrack unlocks or official
/// course records. Checked at compile level (the toy assembly has no reference to the official rules/economy types) and
/// by reflection over every public member.
/// </summary>
public sealed class NonProgressionTests
{
    static readonly Assembly Toys = typeof(DowntimeSession).Assembly;

    static IEnumerable<string> Words(string pascal) => Regex.Matches(pascal, "[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+").Select(m => m.Value);

    static readonly string[] Forbidden =
        { "credit", "credits", "wallet", "rp", "rankpoint", "rankpoints", "reward", "rewards", "mastery", "challenge", "unlock", "unlocks", "entitlement", "settlement", "soundtrack", "ost", "purchase", "currency" };

    [Fact]
    public void Toy_assembly_is_compiled_without_the_official_rules_and_economy()
    {
        Assert.DoesNotContain(Toys.GetTypes(), t => t.Namespace != null && t.Namespace.StartsWith("NightSignal.Core.Rules"));
        Assert.DoesNotContain(Toys.GetReferencedAssemblies(), a => a.Name == "NightSignal.Core" || a.Name.Contains("ControlPlane"));
        Assert.All(Toys.GetTypes().Where(t => t.IsPublic), t => Assert.StartsWith("NightSignal.Core.Toys", t.Namespace));
    }

    [Fact]
    public void Toy_sources_never_mention_official_progression_types()
    {
        string root = Path.Combine(ToyData.RepoRoot(), "Services", "ToysCore", "src");
        if (!Directory.Exists(root)) root = Path.Combine(ToyData.RepoRoot(), "Assets", "Game", "Core", "Toys");
        string[] files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (string f in files)
        {
            string src = File.ReadAllText(f);
            Assert.DoesNotContain("NightSignal.Core.Rules", src);
            Assert.DoesNotContain("Economy.", src);
            Assert.DoesNotContain("RankPoints", src);
            Assert.DoesNotContain("SettlementService", src);
            Assert.DoesNotContain("UnityEngine", src);
        }
    }

    [Fact]
    public void No_public_member_of_any_toy_type_carries_progression_or_currency()
    {
        var offenders = new List<string>();
        foreach (Type t in Toys.GetTypes().Where(t => t.IsPublic || t.IsNestedPublic))
        {
            var names = new List<string> { t.Name };
            names.AddRange(t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Select(f => f.Name));
            names.AddRange(t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Select(p => p.Name));
            names.AddRange(t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Select(m => m.Name));
            foreach (string n in names)
                if (Words(n).Any(w => Forbidden.Contains(w.ToLowerInvariant()))) offenders.Add(t.Name + "." + n);
        }
        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_toy_record_declares_the_non_progression_domain()
    {
        var recordTypes = Toys.GetTypes().Where(t => typeof(INonProgressionRecord).IsAssignableFrom(t) && !t.IsInterface).ToList();
        Assert.True(recordTypes.Count >= 8);
        foreach (Type t in recordTypes)
        {
            var r = (INonProgressionRecord)Activator.CreateInstance(t);
            Assert.Equal(NonProgression.Domain, r.Domain);
        }
        Assert.True(NonProgression.IsToyDomain(NonProgression.Domain));
        Assert.True(NonProgression.IsToyDomain("toy.capclash.best")); // a spoofed toy-ish kind is still recognisably toy
        Assert.False(NonProgression.IsToyDomain("campaign-stage"));
    }

    [Fact]
    public void Toy_results_expose_no_path_to_a_wallet_or_record()
    {
        // Every command result is a ToyResult: a verdict, a reason, a revision and an opaque id. Nothing else.
        FieldInfo[] fields = typeof(ToyResult).GetFields();
        Assert.Equal(new[] { "Detail", "Duplicate", "Reason", "Revision", "Value", "Verdict" }, fields.Select(f => f.Name).OrderBy(n => n));
        Assert.Equal(typeof(string), typeof(ToyResult).GetField("Value").FieldType);
    }
}
