using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Options;
using NightSignal.ControlPlane.Configuration;
using NightSignal.ControlPlane.Content;
using NightSignal.Core.Content;
using NightSignal.Core.Customization;

namespace NightSignal.ControlPlane.Garage;

/// <summary>
/// The trusted appearance catalogue the ONLINE Garage validates liveries with: <c>customization.json</c> (Core
/// <see cref="CustomizationCatalogue"/>), copied from <c>Assets/Content/Data/authored/</c> at build time into
/// <c>content/authored/</c> next to parts.json. Cross-checked against the race catalogue at startup (a chassis for every car,
/// every unlocking cosmetic exists and is equippable, every stock livery validates); startup fails otherwise.
///
/// Deliberately NOT in Core <c>ContentCatalogue.AuthoredFiles</c>: appearance never changes a simulation input, so the race
/// <c>ContentHash</c> does not cover it and a client with other customization data is not refused at connect.
/// <see cref="Hash"/> (SHA-256 of the LF-normalised document, <see cref="CustomizationCatalogue.Hash"/>) is published on
/// /healthz as <c>customizationContentHash</c> so a client can tell when its catalogue differs.
/// </summary>
public sealed class CustomizationContent
{
    public const string FileName = CustomizationCatalogue.FileName;

    readonly ConcurrentDictionary<string, string> stockHashes = new(StringComparer.Ordinal);

    CustomizationContent(CustomizationCatalogue catalogue) => Catalogue = catalogue;

    public CustomizationCatalogue Catalogue { get; }
    /// <summary>SHA-256 (lower-case hex) of the LF-normalised customization.json.</summary>
    public string Hash => Catalogue.Hash;
    public int Revision => Catalogue.Revision;

    /// <summary>
    /// The cosmetic hash of a car's STOCK appearance: <see cref="LiveryHash"/> of the chassis' stock livery, so a car with no
    /// applied livery still has a real, reproducible cosmetic identity (every runtime computes the same value).
    /// </summary>
    public string StockHash(string carId) => stockHashes.GetOrAdd(carId, id => LiveryHash.Of(Catalogue.StockLivery(id)));

    /// <summary>DI entry point: <c>{Content:Directory}/authored/customization.json</c>, resolved like the race catalogue.</summary>
    public static CustomizationContent FromContentDirectory(IOptions<ContentOptions> options, ContentService content, ILogger<CustomizationContent> log)
    {
        string folder = Path.Combine(PathResolver.Resolve(AppContext.BaseDirectory, options.Value.Directory), "authored");
        CustomizationContent c = Load(folder, content.Catalogue);
        log.LogInformation("Customization content loaded: revision {Revision}, {Chassis} chassis, {Rims} rims, {Shapes} decal shapes ({Hash})",
            c.Revision, c.Catalogue.Chassis.Count, c.Catalogue.RimDesigns.Count, c.Catalogue.DecalShapes.Count, c.Hash);
        return c;
    }

    public static CustomizationContent Load(string authoredFolder, ContentCatalogue catalogue)
    {
        string path = Path.Combine(authoredFolder, FileName);
        if (!File.Exists(path)) throw new InvalidOperationException($"Customization content missing: authored/{FileName} (the ONLINE Garage needs it).");
        return From(File.ReadAllText(path, Encoding.UTF8), catalogue);
    }

    /// <summary>Parses and cross-validates the document (also used by tests with modified copies).</summary>
    public static CustomizationContent From(string json, ContentCatalogue catalogue)
    {
        if (!CustomizationCatalogue.TryLoad(json, out CustomizationCatalogue? cat, out List<string> errors))
            throw new InvalidOperationException("Customization content failed validation: " + string.Join("; ", errors));
        var problems = new List<string>(cat!.ValidateAgainst(catalogue));
        foreach (CarDef car in catalogue.Cars)
        {
            if (!cat.TryChassis(car.Id, out _)) continue; // reported by ValidateAgainst
            LiveryValidation stock = LiveryValidator.Validate(cat.StockLivery(car.Id), cat, car.Id, null, LiveryValidationMode.Apply);
            if (!stock.IsValid) problems.Add($"Stock livery of {car.Id} is not valid: {string.Join("; ", stock.Errors)}");
        }
        if (problems.Count > 0)
            throw new InvalidOperationException("Customization content failed validation: " + string.Join("; ", problems));
        return new CustomizationContent(cat);
    }
}
