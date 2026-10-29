using System.Text.Json;
using ADS.Models;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class DutyBmrPolicyService
{
    public const string FileName = "duty-bmr-policy.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };
    private readonly IPluginLog log;
    private readonly IReadOnlyList<DutyCatalogEntry> catalog;
    private readonly Configuration configuration;
    private readonly Action saveConfiguration;
    private DutyBmrPolicyManifest current = new();

    public string ConfigPath { get; }
    public string LastLoadStatus { get; private set; } = string.Empty;

    public DutyBmrPolicyService(IPluginLog log, string configDirectory,
        IReadOnlyList<DutyCatalogEntry> catalog, Configuration configuration, Action? saveConfiguration = null)
    {
        this.log = log;
        this.catalog = catalog;
        this.configuration = configuration;
        this.saveConfiguration = saveConfiguration ?? configuration.Save;
        configuration.DutyBmrPolicyOverrides ??= [];
        ConfigPath = Path.Combine(configDirectory, FileName);
        if (!File.Exists(ConfigPath))
            ObjectRuleShardStore.WriteJsonAtomic(ConfigPath, Serialize(new()));
        Reload();
    }

    public bool Reload()
    {
        try
        {
            current = ValidateJson(File.ReadAllText(ConfigPath), catalog);
            LastLoadStatus = $"Loaded BMR duty policy: {current.Duties.Count} shared row(s).";
            return true;
        }
        catch (Exception ex)
        {
            LastLoadStatus = $"BMR duty policy reload failed; retained the last valid policy: {ex.Message}";
            log.Warning(ex, "[ADS] {Status}", LastLoadStatus);
            return false;
        }
    }

    internal static string Key(uint cfc, uint territory) => $"{cfc}:{territory}";
    private static string Key(DutyCatalogEntry duty) => Key(duty.ContentFinderConditionId, duty.TerritoryTypeId);

    public bool HasOverride(DutyCatalogEntry duty)
        => configuration.DutyBmrPolicyOverrides.ContainsKey(Key(duty));

    public bool DisablesBmr(DutyCatalogEntry? duty)
    {
        if (duty is null || duty.Category != DutyCategory.Solo) return false;
        if (configuration.DutyBmrPolicyOverrides.TryGetValue(Key(duty), out var disabled)) return disabled;
        return SharedDisablesBmr(duty);
    }

    public bool SharedDisablesBmr(DutyCatalogEntry duty)
        => duty.Category == DutyCategory.Solo && current.Duties.FirstOrDefault(row => row.ContentFinderConditionId == duty.ContentFinderConditionId
            && row.TerritoryTypeId == duty.TerritoryTypeId)?.DisableBmr == true;

    public void ApplyOverrides(IReadOnlyDictionary<string, bool?> changes)
    {
        var eligible = catalog.Where(duty => duty.Category == DutyCategory.Solo).Select(Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, disabled) in changes)
        {
            if (!eligible.Contains(key)) continue;
            if (disabled.HasValue) configuration.DutyBmrPolicyOverrides[key] = disabled.Value;
            else configuration.DutyBmrPolicyOverrides.Remove(key);
        }
        saveConfiguration();
    }

    public string BuildContributionJson()
    {
        var rows = current.Duties.ToDictionary(row => Key(row.ContentFinderConditionId, row.TerritoryTypeId));
        foreach (var duty in catalog.Where(duty => duty.Category == DutyCategory.Solo))
        {
            if (!configuration.DutyBmrPolicyOverrides.TryGetValue(Key(duty), out var disabled)) continue;
            rows[Key(duty)] = new()
            {
                ContentFinderConditionId = duty.ContentFinderConditionId,
                TerritoryTypeId = duty.TerritoryTypeId,
                DutyEnglishName = duty.EnglishName,
                DisableBmr = disabled,
            };
        }
        return Serialize(new()
        {
            Description = "Explicit ADS solo-duty BMR encounter-module choices. Missing duties preserve BMR.",
            Duties = rows.Values.OrderBy(row => row.ContentFinderConditionId).ThenBy(row => row.TerritoryTypeId).ToList(),
        });
    }

    internal static string Serialize(DutyBmrPolicyManifest manifest)
        => JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine;

    internal static DutyBmrPolicyManifest ValidateJson(string json, IReadOnlyList<DutyCatalogEntry> catalog)
    {
        using var document = JsonDocument.Parse(json, new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.EnumerateObject().Any(property => property.Name.Equals("SchemaVersion", StringComparison.OrdinalIgnoreCase))
            || !document.RootElement.EnumerateObject().Any(property => property.Name.Equals("Duties", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Array))
            throw new InvalidDataException("BMR duty-policy requires SchemaVersion and a Duties array.");
        var duties = document.RootElement.EnumerateObject()
            .Last(property => property.Name.Equals("Duties", StringComparison.OrdinalIgnoreCase)).Value;
        foreach (var row in duties.EnumerateArray())
            if (row.ValueKind != JsonValueKind.Object || !row.EnumerateObject().Any(property =>
                property.Name.Equals("DisableBmr", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Every BMR duty-policy row requires an explicit boolean DisableBmr.");
        var manifest = JsonSerializer.Deserialize<DutyBmrPolicyManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("Expected a BMR duty-policy manifest.");
        if (manifest.SchemaVersion != 1 || manifest.Duties is null)
            throw new InvalidDataException("BMR duty-policy SchemaVersion must be 1 and Duties must be an array.");
        var seen = new HashSet<uint>();
        foreach (var row in manifest.Duties)
        {
            if (row is null || row.ContentFinderConditionId == 0 || row.TerritoryTypeId == 0
                || !seen.Add(row.ContentFinderConditionId))
                throw new InvalidDataException("BMR duty-policy rows require unique nonzero CFC/territory pairs.");
            var duty = catalog.FirstOrDefault(duty => duty.ContentFinderConditionId == row.ContentFinderConditionId);
            duty ??= catalog.FirstOrDefault(duty => duty.TerritoryTypeId == row.TerritoryTypeId);
            if (duty is not null && (duty.ContentFinderConditionId != row.ContentFinderConditionId
                || duty.TerritoryTypeId != row.TerritoryTypeId || duty.Category != DutyCategory.Solo))
                throw new InvalidDataException($"BMR duty-policy identity is not a matching solo duty: {row.ContentFinderConditionId}/{row.TerritoryTypeId}.");
        }
        return manifest;
    }
}
