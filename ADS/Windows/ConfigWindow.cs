using ADS.Localization;
using System.Numerics;
using ADS.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class ConfigWindow : PositionedWindow, IDisposable
{
    private readonly Plugin plugin;
    private readonly ObjectRuleCheckoutState checkoutState;

    public ConfigWindow(Plugin plugin)
        : base("ADS Settings###ADSSettings")
    {
        this.plugin = plugin;
        checkoutState = plugin.ObjectRuleCheckoutState;
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
        ImGui.TextUnformatted(Ui.T("{0} Settings", PluginInfo.DisplayName));
        ImGui.TextDisabled(Ui.T("Configuration saves immediately."));
        ImGui.Spacing();

        if (ImGui.BeginTabBar("ADSSettingsTabs"))
        {
            if (ImGui.BeginTabItem(Ui.L("General")))
            {
                DrawGeneral(ref changed);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(Ui.L("Automation")))
            {
                DrawAutomation(ref changed);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(Ui.L("Data & Rules")))
            {
                DrawDataAndRules();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(Ui.L("Advanced")))
            {
                DrawAdvanced(ref changed);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(Ui.L("About")))
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
        ImGui.TextUnformatted(Ui.T("Startup"));
        var pluginEnabled = plugin.Configuration.PluginEnabled;
        if (WindowLayout.Checkbox("Plugin enabled", ref pluginEnabled))
        {
            plugin.Configuration.PluginEnabled = pluginEnabled;
            changed = true;
        }
        ImGui.TextDisabled(Ui.T("ADS stays enabled. Start/Resume and Stop control duty ownership."));

        var openMainWindowOnLoad = plugin.Configuration.OpenMainWindowOnLoad;
        if (WindowLayout.Checkbox("Open main window on load", ref openMainWindowOnLoad))
        {
            plugin.Configuration.OpenMainWindowOnLoad = openMainWindowOnLoad;
            changed = true;
        }

        var openQuickControlsOnLoad = plugin.Configuration.OpenQuickControlsOnLoad;
        if (WindowLayout.Checkbox("Open compact controls on load", ref openQuickControlsOnLoad))
        {
            plugin.Configuration.OpenQuickControlsOnLoad = openQuickControlsOnLoad;
            changed = true;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted(Ui.T("DTR Bar"));
        var dtrBarEnabled = plugin.Configuration.DtrBarEnabled;
        if (WindowLayout.Checkbox("Enable DTR bar", ref dtrBarEnabled))
        {
            plugin.Configuration.DtrBarEnabled = dtrBarEnabled;
            changed = true;
        }

        var dtrModes = new[] { "Text only", "Icon + text", "Icon only" };
        var dtrMode = plugin.Configuration.DtrBarMode;
        if (ImGui.Combo(WindowLayout.InputLabel("DTR mode"), ref dtrMode,dtrModes.Select(Ui.Display).ToArray(), dtrModes.Length))
        {
            plugin.Configuration.DtrBarMode = dtrMode;
            changed = true;
        }

        var enabledGlyph = plugin.Configuration.DtrIconEnabled;
        if (ImGui.InputText(WindowLayout.InputLabel("Enabled glyph"), ref enabledGlyph, 8))
        {
            plugin.Configuration.DtrIconEnabled = enabledGlyph;
            changed = true;
        }

        var disabledGlyph = plugin.Configuration.DtrIconDisabled;
        if (ImGui.InputText(WindowLayout.InputLabel("Disabled glyph"), ref disabledGlyph, 8))
        {
            plugin.Configuration.DtrIconDisabled = disabledGlyph;
            changed = true;
        }

        ImGui.TextDisabled(Ui.T("Click the DTR entry to open the Main window."));
    }

    private void DrawAutomation(ref bool changed)
    {
        ImGui.TextUnformatted(Ui.T("Regular Duties"));
        var enableBmraiVbmInRegularDuties = plugin.Configuration.EnableBmraiVbmInRegularDuties;
        if (WindowLayout.Checkbox("Enable BMRAI/VBM in regular duties", ref enableBmraiVbmInRegularDuties))
        {
            plugin.Configuration.EnableBmraiVbmInRegularDuties = enableBmraiVbmInRegularDuties;
            changed = true;
        }

        ImGui.TextWrapped(Ui.T("When enabled, entering a regular duty resets BMRAI and VBM follow targets to Slot1. Changes take effect on the next regular-duty entry."));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted(Ui.T("Treasure"));
        var considerTreasureCoffers = plugin.Configuration.ConsiderTreasureCoffers;
        if (WindowLayout.Checkbox("Consider treasure coffers in planner", ref considerTreasureCoffers))
        {
            plugin.Configuration.ConsiderTreasureCoffers = considerTreasureCoffers;
            changed = true;
        }

        ImGui.TextWrapped(Ui.T("Treat nearby eligible coffers as optional pickups. ADS keeps vertical and route-value guards."));

        var treasureDoorJiggleRecoveryEnabled = plugin.Configuration.TreasureDoorJiggleRecoveryEnabled;
        if (WindowLayout.Checkbox("Treasure door frame recovery", ref treasureDoorJiggleRecoveryEnabled))
        {
            plugin.Configuration.TreasureDoorJiggleRecoveryEnabled = treasureDoorJiggleRecoveryEnabled;
            changed = true;
        }

        ImGui.TextWrapped(Ui.T("Briefly strafe when treasure-door follow-through appears stuck while vnav continues toward the route."));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted(Ui.T("Dialog Rules"));
        var processDialogRulesOutsideOwnedDuty = plugin.Configuration.ProcessDialogRulesOutsideOwnedDuty;
        if (WindowLayout.Checkbox("Process dialog rules outside owned duties", ref processDialogRulesOutsideOwnedDuty))
        {
            plugin.Configuration.ProcessDialogRulesOutsideOwnedDuty = processDialogRulesOutsideOwnedDuty;
            changed = true;
        }

        ImGui.TextWrapped(Ui.T("When enabled, dialog rules can run while ADS is enabled, logged in, and not zoning. Disable to require ADS-owned or leaving duty execution."));
    }

    private void DrawDataAndRules()
    {
        ImGui.TextUnformatted(Ui.T("Remote JSON Cache"));
        ImGui.BeginDisabled(plugin.RemoteJsonUpdateService.IsUpdateRunning);
        if (ImGui.Button(Ui.L("Update rules cache"), new Vector2(-1f, 30f)))
            plugin.ForceRemoteJsonUpdate();
        ImGui.EndDisabled();
        ImGui.TextWrapped(Ui.Display(plugin.RemoteJsonUpdateService.LastUpdateStatus));
        ImGui.TextWrapped(Ui.Display(TreasureDungeonData.LastLoadStatus));
        foreach (var statusLine in plugin.RemoteJsonUpdateService.GetCacheStatusLines())
            ImGui.TextDisabled(Ui.Display(statusLine));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted(Ui.T("Duty Object Rules: {0} active", plugin.ObjectPriorityRuleService.ActiveRuleCount));
        ImGui.TextWrapped(Ui.T("Active preset: {0}", plugin.ObjectPriorityRuleService.ActivePresetName));
        ImGui.TextWrapped(Ui.T("Territory shard index: {0}", plugin.ObjectPriorityRuleService.ConfigPath));
        DrawActionGrid(
            "ADSObjectRuleActions",
            ("Open territory shards", () => plugin.OpenPath(plugin.ObjectPriorityRuleService.TerritoriesPath)),
            ("Open frontier labels", plugin.OpenFrontierLabelUi),
            ("Open rules table", plugin.OpenRuleEditorUi),
            ("Reload active object rules", () => plugin.ObjectPriorityRuleService.Reload()));
        if (ImGui.Button(Ui.L("Rules walkthrough"), new Vector2(-1f, 28f)))
            plugin.OpenRulesWalkthroughUi();
        ImGui.TextWrapped(Ui.Display(plugin.ObjectPriorityRuleService.LastSyncStatus));
        ImGui.TextWrapped(Ui.Display(plugin.ObjectPriorityRuleService.LastLoadStatus));
        ImGui.TextDisabled(Ui.T("Custom presets execute immediately and inherit missing contexts from DEFAULT. DEFAULT saves require debug mode."));

        ImGui.Spacing();
        ImGui.TextUnformatted(Ui.T("PR-ready Checkout"));
        DrawCheckoutConfiguration();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted(Ui.T("Dialog Yes/No Rules: {0} active", plugin.DialogYesNoRuleService.ActiveRuleCount));
        ImGui.TextWrapped(plugin.DialogYesNoRuleService.ConfigPath);
        DrawActionGrid(
            "ADSDialogRuleActions",
            ("Open dialog rules JSON", () => plugin.OpenPath(plugin.DialogYesNoRuleService.ConfigPath)),
            ("Open dialog rules table", plugin.OpenDialogRuleEditorUi),
            ("Reload dialog rules JSON", () => plugin.DialogYesNoRuleService.Reload()));
        ImGui.TextWrapped(Ui.Display(plugin.DialogYesNoRuleService.LastSyncStatus));
        ImGui.TextWrapped(Ui.Display(plugin.DialogYesNoRuleService.LastLoadStatus));
        ImGui.TextDisabled(Ui.T("Only saving or importing into DEFAULT changes runtime dialog behavior."));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted(Ui.T("Duty Manager"));
        ImGui.TextWrapped(plugin.DutyCatalogService.MaturityConfigPath);
        DrawActionGrid(
            "ADSDutyMaturityActions",
            ("Open duty metadata JSON", () => plugin.OpenPath(plugin.DutyCatalogService.MaturityConfigPath)),
            ("Open Duty Manager", plugin.OpenDutyMaturityEditorUi),
            ("Reload duty metadata JSON", () => plugin.DutyCatalogService.ReloadMaturity()));
        ImGui.TextWrapped(Ui.Display(plugin.DutyCatalogService.LastMaturityLoadStatus));
    }

    private void DrawCheckoutConfiguration()
    {
        checkoutState.RefreshFromConfiguration();

        ImGui.TextWrapped(Ui.T("ADS only prepares local shard and index files. Reviewing and submitting them to GitHub remains manual."));
        ImGui.SetNextItemWidth(-1f);
        var candidatePath = checkoutState.CandidatePath;
        var submitted = ImGui.InputTextWithHint(
            "##ADSSettingsCheckoutPath",
            Ui.T("repository root or ads\\territories folder"),
            ref candidatePath,
            512,
            ImGuiInputTextFlags.EnterReturnsTrue);
        checkoutState.SetCandidatePath(candidatePath);
        if (ImGui.Button(Ui.L("Use checkout")) || submitted)
            checkoutState.TryUseCheckout();
        ImGui.SameLine();
        if (ImGui.Button(Ui.L("Clear")))
            checkoutState.Clear();
        ImGui.SameLine();
        var cannotOpen = string.IsNullOrWhiteSpace(checkoutState.ConfiguredRoot)
                         || !Directory.Exists(checkoutState.ConfiguredRoot);
        ImGui.BeginDisabled(cannotOpen);
        if (ImGui.Button(Ui.L("Open checkout")))
            plugin.OpenPath(checkoutState.ConfiguredRoot);
        ImGui.EndDisabled();
        ImGui.TextWrapped(Ui.Display(checkoutState.Status));
    }

    private void DrawAdvanced(ref bool changed)
    {
        ImGui.TextUnformatted(Ui.T("Display"));
        var showDebugSections = plugin.Configuration.ShowDebugSections;
        if (WindowLayout.Checkbox("Show debug sections in the Main window", ref showDebugSections))
        {
            plugin.Configuration.ShowDebugSections = showDebugSections;
            changed = true;
        }

        ImGui.TextWrapped(Ui.T("Enables live JSON preview and short observation samples in Main > Diagnostics."));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted(Ui.T("Framework Hitch Profiler"));
        var frameworkHitchProfilerEnabled = plugin.Configuration.FrameworkHitchProfilerEnabled;
        if (WindowLayout.Checkbox("Enable framework hitch profiler", ref frameworkHitchProfilerEnabled))
        {
            plugin.Configuration.FrameworkHitchProfilerEnabled = frameworkHitchProfilerEnabled;
            changed = true;
        }

        ImGui.TextWrapped(Ui.T("Debugging only. Enables per-framework-update timing to identify slow ADS sections; leave off during normal play."));
    }

    private void DrawAbout()
    {
        ImGui.TextUnformatted(Ui.T("{0} v{1}", PluginInfo.DisplayName, PluginInfo.GetVersion()));
        ImGui.TextWrapped(Ui.Display(PluginInfo.Summary));
        ImGui.Spacing();
        DrawActionGrid(
            "ADSAboutLinks",
            ("Open Setup Wizards", plugin.OpenWizardUi),
            ("Ko-fi", () => plugin.OpenUrl(PluginInfo.SupportUrl)),
            ("Discord", () => plugin.OpenUrl(PluginInfo.DiscordUrl)),
            ("Repository", () => plugin.OpenUrl(PluginInfo.RepoUrl)));
        ImGui.TextDisabled(Ui.T(PluginInfo.DiscordFeedbackNote));
        ImGui.Spacing();
        ImGui.TextWrapped(Ui.T("ADS includes staged execution phases, explicit planner objectives, immediate dead/opened ghosting, specialist inspectors, human-edited rule overrides, duty catalog, ownership controls, and IPC."));
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
            if (ImGui.Button(Ui.L(actions[index].Label + "##" + id + index), new Vector2(-1f, 28f)))
                actions[index].Action();
        }

        ImGui.EndTable();
    }
}
