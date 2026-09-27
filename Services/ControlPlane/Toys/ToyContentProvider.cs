using Microsoft.Extensions.Options;
using NightSignal.ControlPlane.Configuration;
using NightSignal.Core.Toys;

namespace NightSignal.ControlPlane.Toys;

/// <summary>
/// The authored 'While We Wait' toy content (Addendum 02 D210): the same four documents the Unity game loads from
/// <c>Assets/Content/Data/authored/toys/</c>, copied at build time to <c>content/authored/toys/</c> next to the
/// catalogue that <c>ContentService</c> reads, and validated by Core's <see cref="ToyContent.Load"/>.
/// Toys are an optional activity service: a missing or invalid document disables them with an honest
/// <c>toys_unavailable</c> answer, but never stops the control plane or blocks a race.
/// </summary>
public sealed class ToyContentProvider
{
    public const string Folder = "toys";

    ToyContentProvider(ToyContent? content, string? error)
    {
        Content = content;
        Error = error;
    }

    /// <summary>Validated content, or null when the toys are unavailable (see <see cref="Error"/>).</summary>
    public ToyContent? Content { get; }
    public string? Error { get; }

    public static ToyContentProvider Of(ToyContent content) => new(content, null);

    /// <summary>DI entry point: <c>{Content:Directory}/authored/toys/</c>, resolved like the race catalogue.</summary>
    public static ToyContentProvider FromContentDirectory(IOptions<ContentOptions> options, ILogger<ToyContentProvider> log)
    {
        string folder = Path.Combine(PathResolver.Resolve(AppContext.BaseDirectory, options.Value.Directory), "authored", Folder);
        ToyContentProvider provider = Load(folder);
        if (provider.Content is null)
            log.LogError("'While We Wait' toys are unavailable: {Error}", provider.Error);
        else
            log.LogInformation("Toy content loaded: {ContentHash}", provider.Content.ContentHash);
        return provider;
    }

    public static ToyContentProvider Load(string folder)
    {
        var documents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in ToyContent.Files)
        {
            string path = Path.Combine(folder, file);
            if (!File.Exists(path)) return new ToyContentProvider(null, $"toy content document missing: authored/{Folder}/{file}");
            documents[file] = File.ReadAllText(path);
        }
        try
        {
            return new ToyContentProvider(ToyContent.Load(documents), null);
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException or Newtonsoft.Json.JsonException)
        {
            return new ToyContentProvider(null, "toy content failed validation: " + e.Message);
        }
    }
}
