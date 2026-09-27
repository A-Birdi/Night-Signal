using System.Text;
using Microsoft.Extensions.Options;
using NightSignal.ControlPlane.Configuration;
using NightSignal.ControlPlane.Content;
using NightSignal.ControlPlane.Security;
using NightSignal.Core.Builds;
using NightSignal.Core.Content;

namespace NightSignal.ControlPlane.Garage;

/// <summary>
/// The trusted build data the ONLINE Garage evaluates with: <c>parts.json</c> (Core <see cref="PartsCatalogue"/>) and
/// <c>build-recipes.json</c> (Core <see cref="RecipeBook"/>), copied from <c>Assets/Content/Data/authored/</c> at build time
/// into <c>content/authored/</c> next to the race catalogue. Validated against the race catalogue at startup: every car has
/// a chassis entry and a resolvable stock build, every recipe names real parts. Startup fails otherwise — the server cannot
/// own performance truth without it.
///
/// NOTE: neither document is part of Core <c>ContentCatalogue.ContentHash</c> yet (they are not in
/// <c>ContentCatalogue.AuthoredFiles</c>), so the client/server content check does not cover them. <see cref="Hash"/> is
/// published separately (/healthz <c>garageContentHash</c>, assignments <c>vehicleBuild.partsCatalogueHash</c>) until the
/// Core file list and the Unity content hash are changed together.
/// </summary>
public sealed class GarageContent
{
    public const string PartsFile = "parts.json";
    public const string RecipesFile = "build-recipes.json";

    GarageContent(PartsCatalogue parts, RecipeBook recipes, string recipesJson)
    {
        Parts = parts;
        Recipes = recipes;
        Hash = Hashing.Sha256Hex("garage1|" + parts.Hash + "|" + Hashing.Sha256Hex(recipesJson.Replace("\r\n", "\n")));
    }

    public PartsCatalogue Parts { get; }
    public RecipeBook Recipes { get; }
    /// <summary>SHA-256 over the LF-normalised parts and recipe documents (published until they join ContentHash).</summary>
    public string Hash { get; }

    /// <summary>DI entry point: <c>{Content:Directory}/authored/</c>, resolved like the race catalogue.</summary>
    public static GarageContent FromContentDirectory(IOptions<ContentOptions> options, ContentService content, ILogger<GarageContent> log)
    {
        string folder = Path.Combine(PathResolver.Resolve(AppContext.BaseDirectory, options.Value.Directory), "authored");
        GarageContent garage = Load(folder, content.Catalogue);
        log.LogInformation("Garage content loaded: parts revision {Revision}, price revision {PriceRevision}, {Parts} parts, {Recipes} car recipes ({Hash})",
            garage.Parts.Revision, garage.Parts.PriceRevision, garage.Parts.Parts.Count, garage.Recipes.File.Cars.Count, garage.Hash);
        return garage;
    }

    public static GarageContent Load(string authoredFolder, ContentCatalogue catalogue)
    {
        string Read(string file)
        {
            string path = Path.Combine(authoredFolder, file);
            if (!File.Exists(path)) throw new InvalidOperationException($"Garage content missing: authored/{file} (the ONLINE Garage needs it).");
            return File.ReadAllText(path, Encoding.UTF8);
        }
        return From(Read(PartsFile), Read(RecipesFile), catalogue);
    }

    /// <summary>Parses and cross-validates both documents (also used by tests with modified copies).</summary>
    public static GarageContent From(string partsJson, string recipesJson, ContentCatalogue catalogue)
    {
        PartsCatalogue parts;
        RecipeBook recipes;
        try
        {
            parts = PartsCatalogue.Load(partsJson);
            recipes = RecipeBook.Load(recipesJson);
        }
        catch (BuildDataException e)
        {
            throw new InvalidOperationException("Garage content failed validation: " + e.Message, e);
        }
        var problems = new List<string>(parts.ValidateAgainst(catalogue));
        foreach (CarDef car in catalogue.Cars)
        {
            if (!catalogue.CarTunings.TryGetValue(car.Id, out CarTuningDef? tuning)) continue; // reported by ValidateAgainst
            ResolveResult stock = BuildResolver.Resolve(car, tuning, parts, MechanicalSnapshot.Stock());
            if (!stock.Ok) problems.Add($"Stock {car.Id} does not resolve: {string.Join("; ", stock.Issues)}");
        }
        foreach (CarRecipe recipe in recipes.File.Cars)
        {
            if (!catalogue.TryCar(recipe.Car, out _)) problems.Add($"Recipe for unknown car {recipe.Car}");
            foreach (RecipeStep step in recipe.Path.Concat(recipe.Alternatives))
            {
                try { RecipeBook.ToSnapshot(step, parts); }
                catch (BuildDataException e) { problems.Add(e.Message); }
            }
        }
        if (problems.Count > 0)
            throw new InvalidOperationException("Garage content failed validation: " + string.Join("; ", problems));
        return new GarageContent(parts, recipes, recipesJson);
    }
}
