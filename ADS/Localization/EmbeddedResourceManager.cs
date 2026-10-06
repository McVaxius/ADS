using System.Globalization;
using System.Resources;

namespace ADS.Localization;

// Keep all sixteen native translation resources in the owning plugin assembly.
internal sealed class EmbeddedResourceManager : ResourceManager
{
    private readonly Dictionary<string, ResourceSet> sets = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public override ResourceSet? GetResourceSet(CultureInfo culture, bool createIfNotExists, bool tryParents)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var name = culture.Name;
        if (name is not ("zh-Hans" or "zh-Hant" or "pt-BR"))
        {
            name = culture.TwoLetterISOLanguageName;
            if (name is not ("fr" or "de" or "ja" or "es" or "ko" or "it" or "ru" or "vi" or "id" or "pl" or "tr" or "hi")) name = "en";
        }
        lock (gate)
        {
            if (sets.TryGetValue(name, out var found)) return found;
            if (!createIfNotExists) return null;
            var resource = "ADS.Localization.Strings" + (name == "en" ? "" : "." + name) + ".resources";
            var stream = typeof(Ui).Assembly.GetManifestResourceStream(resource)
                ?? throw new MissingManifestResourceException(resource);
            var set = new ResourceSet(stream);
            sets.Add(name, set);
            return set;
        }
    }

    public override string? GetString(string name, CultureInfo? culture)
        => GetResourceSet(culture ?? CultureInfo.CurrentUICulture, true, true)?.GetString(name, IgnoreCase);

    public override string? GetString(string name) => GetString(name, CultureInfo.CurrentUICulture);

    public override void ReleaseAllResources()
    {
        lock (gate)
        {
            foreach (var set in sets.Values) set.Dispose();
            sets.Clear();
        }
    }
}
