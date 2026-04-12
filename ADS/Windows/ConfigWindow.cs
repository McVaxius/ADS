using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class ConfigWindow : PositionedWindow, IDisposable
{
    private readonly Plugin plugin;

    public ConfigWindow(Plugin plugin)
        : base("ADS Settings###ADSSettings")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(420f, 280f),
            MaximumSize = new System.Numerics.Vector2(2200f, 1600f),
        };
        Size = new System.Numerics.Vector2(720f, 620f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();

        var changed = false;
        var pluginEnabled = plugin.Configuration.PluginEnabled;
        if (ImGui.Checkbox("Plugin enabled", ref pluginEnabled))
        {
            plugin.Configuration.PluginEnabled = pluginEnabled;
            changed = true;
        }

        var openMainWindowOnLoad = plugin.Configuration.OpenMainWindowOnLoad;
        if (ImGui.Checkbox("Open main window on load", ref openMainWindowOnLoad))
        {
            plugin.Configuration.OpenMainWindowOnLoad = openMainWindowOnLoad;
            changed = true;
        }

        var dtrBarEnabled = plugin.Configuration.DtrBarEnabled;
        if (ImGui.Checkbox("Enable DTR bar", ref dtrBarEnabled))
        {
            plugin.Configuration.DtrBarEnabled = dtrBarEnabled;
            changed = true;
        }

        var showDebugSections = plugin.Configuration.ShowDebugSections;
        if (ImGui.Checkbox("Show debug sections in the main window", ref showDebugSections))
        {
            plugin.Configuration.ShowDebugSections = showDebugSections;
            changed = true;
        }

        var considerTreasureCoffers = plugin.Configuration.ConsiderTreasureCoffers;
        if (ImGui.Checkbox("Consider treasure coffers in planner", ref considerTreasureCoffers))
        {
            plugin.Configuration.ConsiderTreasureCoffers = considerTreasureCoffers;
            changed = true;
        }

        ImGui.TextWrapped("When treasure-coffer scan is enabled, ADS treats coffers as optional pickups: they must materially beat competing targets on XZ, ADS still tries to stand within about 1y on approach, and coffers more than 5y away in Y are skipped.");

        ImGui.Separator();
        ImGui.TextWrapped($"Duty object rules: {plugin.ObjectPriorityRuleService.ActiveRuleCount} active rule(s).");
        ImGui.TextWrapped(plugin.ObjectPriorityRuleService.ConfigPath);
        if (ImGui.Button("Open rules JSON"))
            plugin.OpenPath(plugin.ObjectPriorityRuleService.ConfigPath);
        ImGui.SameLine();
        if (ImGui.Button("Open frontier labels"))
            plugin.OpenFrontierLabelUi();
        ImGui.SameLine();
        if (ImGui.Button("Open rules table"))
            plugin.OpenRuleEditorUi();
        ImGui.SameLine();
        if (ImGui.Button("Reload rules JSON"))
            plugin.ObjectPriorityRuleService.Reload();
        ImGui.TextWrapped(plugin.ObjectPriorityRuleService.LastLoadStatus);
        ImGui.TextWrapped("Recommended fields: contentFinderConditionId or territoryTypeId, dutyEnglishName while scouting, objectKind, baseId if names collide, objectName, classification override or Ignored, lower-is-better priority, priorityVerticalRadius, optional maxDistance, waitAtDestinationSeconds for pre-interact arrival hold, waitAfterInteractSeconds for post-interact follow-through hold, BossFight for BattleNpc bosses that should beat nearby trash/objectives once in range, CombatFriendly on BattleNpc or EventNpc for direct-interact talk targets such as Goblin Pathfinder, and for manual waypoints classification MapXzDestination + mapCoordinates like 11.3,10.4 or XYZ + worldCoordinates like 154.1,101.9,-34.2. Layer now scopes any rule to the current live sub-area only: leave it blank for any layer, or set it to a live subarea name / map row id. Legacy DestinationType layer rows are auto-migrated on load.");
        ImGui.TextWrapped("On first run, ADS copies the bundled rules JSON here. After that, this config file is authoritative, so manual edits survive plugin enable/reload and are auto-reloaded while ADS is running.");

        var dtrModes = new[] { "Text only", "Icon + text", "Icon only" };
        var dtrMode = plugin.Configuration.DtrBarMode;
        if (ImGui.Combo("DTR mode", ref dtrMode, dtrModes, dtrModes.Length))
        {
            plugin.Configuration.DtrBarMode = dtrMode;
            changed = true;
        }

        var enabledGlyph = plugin.Configuration.DtrIconEnabled;
        if (ImGui.InputText("Enabled glyph", ref enabledGlyph, 8))
        {
            plugin.Configuration.DtrIconEnabled = enabledGlyph;
            changed = true;
        }

        var disabledGlyph = plugin.Configuration.DtrIconDisabled;
        if (ImGui.InputText("Disabled glyph", ref disabledGlyph, 8))
        {
            plugin.Configuration.DtrIconDisabled = disabledGlyph;
            changed = true;
        }

        ImGui.Separator();
        ImGui.TextWrapped("ADS v1 now includes staged execution phases, explicit planner objective kinds, immediate dead/opened ghosting for monsters and treasure coffers, a ghost inspector window, and a human-edited duty-object-rules.json override seam on top of the observer, planner explanation, duty catalog, ownership shell, and IPC.");

        if (changed)
            plugin.SaveConfiguration();
    }
}
