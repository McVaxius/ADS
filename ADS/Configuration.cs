using Dalamud.Configuration;

namespace ADS;

public sealed class Configuration : IPluginConfiguration
{
    public const string DefaultDtrIconEnabled = "\uE044";
    public const string DefaultDtrIconDisabled = "\uE04C";

    public int Version { get; set; } = 3;
    public bool PluginEnabled { get; set; } = true;
    public bool OpenMainWindowOnLoad { get; set; } = false;
    public bool DtrBarEnabled { get; set; } = true;
    public int DtrBarMode { get; set; } = 1;
    public string DtrIconEnabled { get; set; } = DefaultDtrIconEnabled;
    public string DtrIconDisabled { get; set; } = DefaultDtrIconDisabled;
    public bool ShowDebugSections { get; set; }
    public bool ConsiderTreasureCoffers { get; set; } = true;
    public string AppliedBundledObjectRulesStamp { get; set; } = string.Empty;
    public string AppliedBundledDialogRulesStamp { get; set; } = string.Empty;

    public void Save()
        => Plugin.PluginInterface.SavePluginConfig(this);
}
