using ADS.Models;
using Dalamud.Configuration;

namespace ADS;

public sealed class Configuration : IPluginConfiguration
{
    public const string DefaultDtrIconEnabled = "\uE044";
    public const string DefaultDtrIconDisabled = "\uE04C";

    public int Version { get; set; } = 17;
    public bool PluginEnabled { get; set; } = true;
    public bool OpenMainWindowOnLoad { get; set; } = false;
    public bool OpenQuickControlsOnLoad { get; set; } = false;
    public bool DtrBarEnabled { get; set; } = true;
    public int DtrBarMode { get; set; } = 1;
    public string DtrIconEnabled { get; set; } = DefaultDtrIconEnabled;
    public string DtrIconDisabled { get; set; } = DefaultDtrIconDisabled;
    public bool ShowDebugSections { get; set; }
    public bool ConsiderTreasureCoffers { get; set; } = true;
    public bool TreasureDoorJiggleRecoveryEnabled { get; set; } = true;
    public bool ResetCameraBeforeInteractEnabled { get; set; } = true;
    public bool ProcessDialogRulesOutsideOwnedDuty { get; set; } = true;
    public bool HigherLowerDiagnosticsEnabled { get; set; } = true;
    public bool HigherLowerAutomationEnabled { get; set; } = true;
    public bool HigherLowerVfxDataminingEnabled { get; set; } = false;
    public bool ReflectionToolsEnabled { get; set; } = true;
    public bool ReflectionQueenLunatenderDisabled { get; set; } = false;
    public bool ReflectionHuntsDisabled { get; set; } = false;
    public bool ReflectionMaxLoadDistanceMinimized { get; set; } = false;
    public float ReflectionMinimizedMaxLoadDistance { get; set; } = 100f;
    public bool ReflectionHasOriginalMaxLoadDistance { get; set; } = false;
    public float ReflectionOriginalMaxLoadDistance { get; set; } = 500f;
    public LootRollMode LootMode { get; set; } = LootRollMode.Off;
    public bool LootRegistrableNeedingEnabled { get; set; } = false;
    public bool LootRegistrableMountsEnabled { get; set; } = true;
    public bool LootRegistrableMinionsEnabled { get; set; } = true;
    public bool LootRegistrableFashionAccessoriesEnabled { get; set; } = true;
    public bool LootRegistrableFacewearEnabled { get; set; } = true;
    public bool LootRegistrableOrchestrionRollsEnabled { get; set; } = true;
    public bool LootRegistrableFadedOrchestrionCopiesEnabled { get; set; } = true;
    public bool LootRegistrableEmotesHairstylesEnabled { get; set; } = true;
    public bool LootRegistrableBardingsEnabled { get; set; } = true;
    public bool LootRegistrableTripleTriadCardsEnabled { get; set; } = true;
    public string TreasureDutyRecoveryKey { get; set; } = string.Empty;
    public DateTime TreasureDutyRecoveryUtc { get; set; } = DateTime.MinValue;
    public string TreasureDutyRecoveryRole { get; set; } = string.Empty;
    public bool BmraiTreasureFollowCleanupPending { get; set; } = false;

    public void Save()
        => Plugin.PluginInterface.SavePluginConfig(this);
}
