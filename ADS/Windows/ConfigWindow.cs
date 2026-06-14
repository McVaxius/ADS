using System.Numerics;
using ADS.Services;
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
            MinimumSize = new Vector2(520f, 420f),
            MaximumSize = new Vector2(2200f, 1600f),
        };
        Size = new Vector2(760f, 640f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();

        var changed = false;
        ImGui.TextUnformatted($"{PluginInfo.DisplayName} Settings");
        ImGui.TextDisabled("Configuration saves immediately.");
        ImGui.Spacing();

        if (ImGui.BeginTabBar("ADSSettingsTabs"))
        {
            if (ImGui.BeginTabItem("General"))
            {
                DrawGeneral(ref changed);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Automation"))
            {
                DrawAutomation(ref changed);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Data & Rules"))
            {
                DrawDataAndRules();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Advanced"))
            {
                DrawAdvanced(ref changed);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("About"))
            {
                DrawAbout();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        if (changed)
            plugin.SaveConfiguration();
    }

    private void DrawGeneral(ref bool changed)
    {
        ImGui.TextUnformatted("Startup");
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

        var openQuickControlsOnLoad = plugin.Configuration.OpenQuickControlsOnLoad;
        if (ImGui.Checkbox("Open compact controls on load", ref openQuickControlsOnLoad))
        {
            plugin.Configuration.OpenQuickControlsOnLoad = openQuickControlsOnLoad;
            changed = true;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("DTR Bar");
        var dtrBarEnabled = plugin.Configuration.DtrBarEnabled;
        if (ImGui.Checkbox("Enable DTR bar", ref dtrBarEnabled))
        {
            plugin.Configuration.DtrBarEnabled = dtrBarEnabled;
            changed = true;
        }

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

        ImGui.TextDisabled("Click the DTR entry to open the Main window.");
    }

    private void DrawAutomation(ref bool changed)
    {
        ImGui.TextUnformatted("Treasure");
        var considerTreasureCoffers = plugin.Configuration.ConsiderTreasureCoffers;
        if (ImGui.Checkbox("Consider treasure coffers in planner", ref considerTreasureCoffers))
        {
            plugin.Configuration.ConsiderTreasureCoffers = considerTreasureCoffers;
            changed = true;
        }

        ImGui.TextWrapped("Treat nearby eligible coffers as optional pickups. ADS keeps vertical and route-value guards.");

        var treasureDoorJiggleRecoveryEnabled = plugin.Configuration.TreasureDoorJiggleRecoveryEnabled;
        if (ImGui.Checkbox("Treasure door frame recovery", ref treasureDoorJiggleRecoveryEnabled))
        {
            plugin.Configuration.TreasureDoorJiggleRecoveryEnabled = treasureDoorJiggleRecoveryEnabled;
            changed = true;
        }

        ImGui.TextWrapped("Briefly strafe when treasure-door follow-through appears stuck while vnav continues toward the route.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Dialog Rules");
        var processDialogRulesOutsideOwnedDuty = plugin.Configuration.ProcessDialogRulesOutsideOwnedDuty;
        if (ImGui.Checkbox("Process dialog rules outside owned duties", ref processDialogRulesOutsideOwnedDuty))
        {
            plugin.Configuration.ProcessDialogRulesOutsideOwnedDuty = processDialogRulesOutsideOwnedDuty;
            changed = true;
        }

        ImGui.TextWrapped("When enabled, dialog rules can run while ADS is enabled, logged in, and not zoning. Disable to require ADS-owned or leaving duty execution.");
    }

    private void DrawDataAndRules()
    {
        ImGui.TextUnformatted("Remote JSON Cache");
        ImGui.BeginDisabled(plugin.RemoteJsonUpdateService.IsUpdateRunning);
        if (ImGui.Button("Update rules cache", new Vector2(-1f, 30f)))
            plugin.ForceRemoteJsonUpdate();
        ImGui.EndDisabled();
        ImGui.TextWrapped(plugin.RemoteJsonUpdateService.LastUpdateStatus);
        ImGui.TextWrapped(TreasureDungeonData.LastLoadStatus);
        foreach (var statusLine in plugin.RemoteJsonUpdateService.GetCacheStatusLines())
            ImGui.TextDisabled(statusLine);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted($"Duty Object Rules: {plugin.ObjectPriorityRuleService.ActiveRuleCount} active");
        ImGui.TextWrapped(plugin.ObjectPriorityRuleService.ConfigPath);
        DrawActionGrid(
            "ADSObjectRuleActions",
            ("Open rules JSON", () => plugin.OpenPath(plugin.ObjectPriorityRuleService.ConfigPath)),
            ("Open frontier labels", plugin.OpenFrontierLabelUi),
            ("Open rules table", plugin.OpenRuleEditorUi),
            ("Reload rules JSON", () => plugin.ObjectPriorityRuleService.Reload()));
        ImGui.TextWrapped(plugin.ObjectPriorityRuleService.LastSyncStatus);
        ImGui.TextWrapped(plugin.ObjectPriorityRuleService.LastLoadStatus);
        ImGui.TextDisabled("DEFAULT is live runtime data. Parked presets do not affect runtime until loaded into DEFAULT.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted($"Dialog Yes/No Rules: {plugin.DialogYesNoRuleService.ActiveRuleCount} active");
        ImGui.TextWrapped(plugin.DialogYesNoRuleService.ConfigPath);
        DrawActionGrid(
            "ADSDialogRuleActions",
            ("Open dialog rules JSON", () => plugin.OpenPath(plugin.DialogYesNoRuleService.ConfigPath)),
            ("Open dialog rules table", plugin.OpenDialogRuleEditorUi),
            ("Reload dialog rules JSON", () => plugin.DialogYesNoRuleService.Reload()));
        ImGui.TextWrapped(plugin.DialogYesNoRuleService.LastSyncStatus);
        ImGui.TextWrapped(plugin.DialogYesNoRuleService.LastLoadStatus);
        ImGui.TextDisabled("Only saving or importing into DEFAULT changes runtime dialog behavior.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Duty Maturity");
        ImGui.TextWrapped(plugin.DutyCatalogService.MaturityConfigPath);
        DrawActionGrid(
            "ADSDutyMaturityActions",
            ("Open duty maturity JSON", () => plugin.OpenPath(plugin.DutyCatalogService.MaturityConfigPath)),
            ("Open maturity editor", plugin.OpenDutyMaturityEditorUi),
            ("Reload duty maturity JSON", () => plugin.DutyCatalogService.ReloadMaturity()));
        ImGui.TextWrapped(plugin.DutyCatalogService.LastMaturityLoadStatus);
    }

    private void DrawAdvanced(ref bool changed)
    {
        ImGui.TextUnformatted("Display");
        var showDebugSections = plugin.Configuration.ShowDebugSections;
        if (ImGui.Checkbox("Show debug sections in the Main window", ref showDebugSections))
        {
            plugin.Configuration.ShowDebugSections = showDebugSections;
            changed = true;
        }

        ImGui.TextWrapped("Enables live JSON preview and short observation samples in Main > Diagnostics.");
    }

    private void DrawAbout()
    {
        ImGui.TextUnformatted($"{PluginInfo.DisplayName} v{PluginInfo.GetVersion()}");
        ImGui.TextWrapped(PluginInfo.Summary);
        ImGui.TextWrapped(PluginInfo.PilotDutySummary);
        ImGui.Spacing();
        DrawActionGrid(
            "ADSAboutLinks",
            ("Ko-fi", () => plugin.OpenUrl(PluginInfo.SupportUrl)),
            ("Discord", () => plugin.OpenUrl(PluginInfo.DiscordUrl)),
            ("Repository", () => plugin.OpenUrl(PluginInfo.RepoUrl)));
        ImGui.TextDisabled(PluginInfo.DiscordFeedbackNote);
        ImGui.Spacing();
        ImGui.TextWrapped("ADS includes staged execution phases, explicit planner objectives, immediate dead/opened ghosting, specialist inspectors, human-edited rule overrides, duty catalog, ownership controls, and IPC.");
    }

    private void DrawActionGrid(string id, params (string Label, Action Action)[] actions)
    {
        var columnCount = ImGui.GetContentRegionAvail().X >= 800f ? 4 : 2;
        if (!ImGui.BeginTable(id, columnCount, ImGuiTableFlags.SizingStretchSame))
            return;

        for (var index = 0; index < actions.Length; index++)
        {
            if (index % columnCount == 0)
                ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(index % columnCount);
            if (ImGui.Button($"{actions[index].Label}##{id}{index}", new Vector2(-1f, 28f)))
                actions[index].Action();
        }

        ImGui.EndTable();
    }
}
