using Dalamud.Configuration;

namespace ADS;

public sealed class Configuration : IPluginConfiguration
{
    public const string DefaultDtrIconEnabled = "\uE044";
    public const string DefaultDtrIconDisabled = "\uE04C";

    public int Version { get; set; } = 5;
    public bool PluginEnabled { get; set; } = true;
    public bool OpenMainWindowOnLoad { get; set; } = false;
    public bool OpenQuickControlsOnLoad { get; set; } = true;
    public bool DtrBarEnabled { get; set; } = true;
    public int DtrBarMode { get; set; } = 1;
    public string DtrIconEnabled { get; set; } = DefaultDtrIconEnabled;
    public string DtrIconDisabled { get; set; } = DefaultDtrIconDisabled;
    public bool ShowDebugSections { get; set; }
    public bool ConsiderTreasureCoffers { get; set; } = true;
    public bool TreasureDoorJiggleRecoveryEnabled { get; set; } = true;
    public string TreasureDoorJiggleLeftKey { get; set; } = "A";
    public string TreasureDoorJiggleRightKey { get; set; } = "D";
    public string InstalledRuleSyncVersion { get; set; } = string.Empty;

    public void Save()
        => Plugin.PluginInterface.SavePluginConfig(this);
}
