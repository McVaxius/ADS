using ADS.Models;

namespace ADS.Services;

public sealed class DesynthPolicyService
{
    private static readonly string[] InventoryOnlyCategories = ["InventoryEquipment"];
    private static readonly string[] InventoryAndArmouryCategories =
    [
        "InventoryEquipment",
        "ArmouryMainOff",
        "ArmouryHeadBodyHands",
        "ArmouryLegsFeet",
        "ArmouryNeckEars",
        "ArmouryWristsRings",
    ];

    private static readonly string[] LegacyCategoryNames =
    [
        "InventoryEquipment",
        "InventoryHousing",
        "ArmouryMainOff",
        "ArmouryHeadBodyHands",
        "ArmouryLegsFeet",
        "ArmouryNeckEars",
        "ArmouryWristsRings",
        "Equipped",
    ];

    public static IReadOnlyList<string> AllLegacyCategoryNames => LegacyCategoryNames;

    public static uint NormalizeBaseItemId(uint itemId)
        => itemId >= 1_000_000 ? itemId % 1_000_000 : itemId;

    public static IReadOnlyList<string> GetCategoryNames(DesynthInventoryScope scope)
        => scope == DesynthInventoryScope.InventoryOnly
            ? InventoryOnlyCategories
            : InventoryAndArmouryCategories;

    public static bool ScopeProtectsGearsets(DesynthInventoryScope scope)
        => scope != DesynthInventoryScope.InventoryAndArmoury;

    public static DesynthInventoryScope NormalizeScope(DesynthInventoryScope scope)
        => Enum.IsDefined(scope) ? scope : DesynthInventoryScope.InventoryOnly;

    public static DesynthInventoryScope NormalizeScopeFromLegacyCategories(IEnumerable<string>? categories, bool protectGearsets)
    {
        var hasArmoury = (categories ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Any(x => InventoryAndArmouryCategories.Contains(x, StringComparer.OrdinalIgnoreCase)
                      && !InventoryOnlyCategories.Contains(x, StringComparer.OrdinalIgnoreCase));

        if (!hasArmoury)
            return DesynthInventoryScope.InventoryOnly;

        return protectGearsets
            ? DesynthInventoryScope.InventoryAndArmourySkipGearsets
            : DesynthInventoryScope.InventoryAndArmoury;
    }

    public static bool TryNormalizeLegacyCategories(IEnumerable<string>? categories, bool protectGearsets, out DesynthInventoryScope scope, out string error)
    {
        var values = (categories ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (values.Count == 0)
        {
            scope = DesynthInventoryScope.InventoryOnly;
            error = "desynthCategories must contain at least one category.";
            return false;
        }

        var unknown = values.FirstOrDefault(x => !LegacyCategoryNames.Contains(x, StringComparer.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(unknown))
        {
            scope = DesynthInventoryScope.InventoryOnly;
            error = $"Unknown desynthesis category '{unknown}'.";
            return false;
        }

        scope = NormalizeScopeFromLegacyCategories(values, protectGearsets);
        error = string.Empty;
        return true;
    }

    public static bool ApplyScopeToConfiguration(Configuration configuration, DesynthInventoryScope scope)
    {
        var normalizedScope = NormalizeScope(scope);
        var categories = GetCategoryNames(normalizedScope).ToList();
        var protectGearsets = ScopeProtectsGearsets(normalizedScope);
        var existingCategories = configuration.DesynthCategories ?? [];
        var changed = configuration.DesynthInventoryScope != normalizedScope
                      || configuration.DesynthProtectGearsets != protectGearsets
                      || !existingCategories.SequenceEqual(categories, StringComparer.OrdinalIgnoreCase);

        configuration.DesynthInventoryScope = normalizedScope;
        configuration.DesynthProtectGearsets = protectGearsets;
        configuration.DesynthCategories = categories;
        return changed;
    }

    public DesynthPolicy Compose(
        DesynthRunMode mode,
        Configuration configuration,
        DesynthPresetStore presetStore,
        DesynthDutyLedgerStore ledgerStore)
    {
        var source = mode switch
        {
            DesynthRunMode.All => DesynthSource.AllInventory,
            DesynthRunMode.Whitelist => DesynthSource.ActiveWhitelist,
            DesynthRunMode.LastDuty => DesynthSource.LastDutyGains,
            DesynthRunMode.Skillups => DesynthSource.AllInventory,
            DesynthRunMode.InventoryOnly => DesynthSource.AllInventory,
            DesynthRunMode.EverywhereSkipGearsets => DesynthSource.AllInventory,
            DesynthRunMode.Everywhere => DesynthSource.AllInventory,
            _ => configuration.DesynthSource,
        };
        var scope = mode switch
        {
            DesynthRunMode.InventoryOnly => DesynthInventoryScope.InventoryOnly,
            DesynthRunMode.EverywhereSkipGearsets => DesynthInventoryScope.InventoryAndArmourySkipGearsets,
            DesynthRunMode.Everywhere => DesynthInventoryScope.InventoryAndArmoury,
            _ => NormalizeScope(configuration.DesynthInventoryScope),
        };
        var skillups = mode == DesynthRunMode.Skillups || configuration.DesynthSkillUpFilterEnabled;
        var protectGearsets = ScopeProtectsGearsets(scope);
        var categories = GetCategoryNames(scope).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preset = presetStore.Get(configuration.DesynthActivePreset);

        return new DesynthPolicy(
            mode,
            source,
            scope,
            preset.Name,
            skillups,
            Math.Clamp(configuration.DesynthSkillUpThreshold, 0, 1000),
            protectGearsets,
            categories,
            preset.ItemIds.Select(NormalizeBaseItemId).ToHashSet(),
            ledgerStore.GetRemainingCounts());
    }

    public static bool TryParseMode(string? value, out DesynthRunMode mode)
        => Enum.TryParse(NormalizeToken(value), true, out mode);

    public static bool TryParseScope(string? value, out DesynthInventoryScope scope)
        => Enum.TryParse(NormalizeToken(value), true, out scope)
           && Enum.IsDefined(scope);

    public static string GetModeName(DesynthRunMode mode)
        => mode switch
        {
            DesynthRunMode.LastDuty => "last-duty",
            DesynthRunMode.InventoryOnly => "inventory-only",
            DesynthRunMode.EverywhereSkipGearsets => "everywhere-skip-gearsets",
            DesynthRunMode.Everywhere => "everywhere",
            _ => mode.ToString().ToLowerInvariant(),
        };

    public static string GetScopeName(DesynthInventoryScope scope)
        => scope switch
        {
            DesynthInventoryScope.InventoryAndArmourySkipGearsets => "inventory-and-armoury-skip-gearsets",
            DesynthInventoryScope.InventoryAndArmoury => "inventory-and-armoury",
            _ => "inventory-only",
        };

    private static string NormalizeToken(string? value)
        => (value ?? string.Empty)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
}
