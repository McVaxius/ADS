namespace ADS.Services;

public static class AdsIpcValidation
{
    public static readonly IReadOnlySet<string> KnownActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "duty.start-outside", "duty.start-inside", "duty.resume-inside", "duty.leave",
        "window.open-loot", "window.toggle-loot", "window.open-desynth",
        "utility.start-repair", "utility.start-extract-materia", "utility.start-desynth", "utility.cancel",
        "preset.create", "preset.rename", "preset.delete", "preset.select", "preset.add-item",
        "preset.remove-item", "preset.import-raw", "preset.import-base64", "preset.export-raw",
        "preset.export-base64", "ledger.clear", "configuration.patch",
    };

    public static readonly IReadOnlySet<string> KnownConfigurationSettings = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "pluginEnabled",
        "lootMode",
        "lootRegistrableNeedingEnabled",
        "lootRegistrableMountsEnabled",
        "lootRegistrableMinionsEnabled",
        "lootRegistrableFashionAccessoriesEnabled",
        "lootRegistrableFacewearEnabled",
        "lootRegistrableOrchestrionRollsEnabled",
        "lootRegistrableFadedOrchestrionCopiesEnabled",
        "lootRegistrableEmotesHairstylesEnabled",
        "lootRegistrableBardingsEnabled",
        "lootRegistrableTripleTriadCardsEnabled",
        "processDialogRulesOutsideOwnedDuty",
        "higherLowerAutomationEnabled",
        "desynthSource",
        "desynthInventoryScope",
        "desynthActivePreset",
        "desynthSkillUpFilterEnabled",
        "desynthSkillUpThreshold",
        "desynthProtectGearsets",
        "desynthCategories",
        "desynthContextMenuEnabled",
    };

    public static bool IsKnownAction(string? action)
        => !string.IsNullOrWhiteSpace(action) && KnownActions.Contains(action.Trim());

    public static bool IsKnownConfigurationSetting(string? setting)
        => !string.IsNullOrWhiteSpace(setting) && KnownConfigurationSettings.Contains(setting.Trim());
}
