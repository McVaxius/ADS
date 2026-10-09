using ADS.Localization;
using AethertekUI;
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
        Flags |= ImGuiWindowFlags.HorizontalScrollbar;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        windowMotion.DrawChrome();
        FinalizePendingWindowPlacement();

        var changed = false;
        MaterialText.Text(Ui.T("{0} Settings", PluginInfo.DisplayName));
        MaterialText.TextDisabled(Ui.T("Configuration saves immediately."));
        ImGui.Spacing();

        bool tabsVisible;
        using(MaterialText.PushLineHeight(new[] { "General", "Automation", "Data & Rules", "Advanced", "Window appearance", "About" }.Select(label => Ui.T(label)).ToArray()))
            tabsVisible = ImGui.BeginTabBar("ADSSettingsTabs", ImGuiTabBarFlags.FittingPolicyScroll);
        if (tabsVisible)
        {
            if (WindowLayout.BeginTabItem(Ui.L("General")))
            {
                DrawGeneral(ref changed);
                ImGui.EndTabItem();
            }

            if (WindowLayout.BeginTabItem(Ui.L("Automation")))
            {
                DrawAutomation(ref changed);
                ImGui.EndTabItem();
            }

            if (WindowLayout.BeginTabItem(Ui.L("Data & Rules")))
            {
                DrawDataAndRules();
                ImGui.EndTabItem();
            }

            if (WindowLayout.BeginTabItem(Ui.L("Advanced")))
            {
                DrawAdvanced(ref changed);
                ImGui.EndTabItem();
            }

            if (WindowLayout.BeginTabItem(Ui.L("Window appearance"), ImGuiTabItemFlags.NoPushId))
            {
                ImGui.PushID(Ui.L("General"));
                try { plugin.Appearance.DrawWindowAppearanceSettings(); }
                finally { ImGui.PopID(); ImGui.EndTabItem(); }
            }
            if (WindowLayout.BeginTabItem(Ui.L("About")))
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
        MaterialText.Text(Ui.T("Startup"));
        var pluginEnabled = plugin.Configuration.PluginEnabled;
        if (WindowLayout.Checkbox("Plugin enabled", ref pluginEnabled))
        {
            plugin.Configuration.PluginEnabled = pluginEnabled;
            changed = true;
        }
        MaterialText.TextDisabled(Ui.T("ADS stays enabled. Start/Resume and Stop control duty ownership."));

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
        MaterialText.Text(Ui.T("DTR Bar"));
        var dtrBarEnabled = plugin.Configuration.DtrBarEnabled;
        if (WindowLayout.Checkbox("Enable DTR bar", ref dtrBarEnabled))
        {
            plugin.Configuration.DtrBarEnabled = dtrBarEnabled;
            changed = true;
        }

        var dtrModes = new[] { "Text only", "Icon + text", "Icon only" };
        var dtrMode = plugin.Configuration.DtrBarMode;
        var dtrLabels = dtrModes.Select(Ui.Display).ToArray();
        if (WindowLayout.Combo(WindowLayout.InputLabel("DTR mode", WindowLayout.ComboMinimum(dtrLabels)), ref dtrMode,dtrLabels, dtrModes.Length))
        {
            plugin.Configuration.DtrBarMode = dtrMode;
            changed = true;
        }

        var enabledGlyph = plugin.Configuration.DtrIconEnabled;
        if (WindowLayout.InputText(WindowLayout.InputLabel("Enabled glyph", WindowLayout.TextMinimum(8)), ref enabledGlyph, 8))
        {
            plugin.Configuration.DtrIconEnabled = enabledGlyph;
            changed = true;
        }

        var disabledGlyph = plugin.Configuration.DtrIconDisabled;
        if (WindowLayout.InputText(WindowLayout.InputLabel("Disabled glyph", WindowLayout.TextMinimum(8)), ref disabledGlyph, 8))
        {
            plugin.Configuration.DtrIconDisabled = disabledGlyph;
            changed = true;
        }

        MaterialText.TextDisabled(Ui.T("Click the DTR entry to open the Main window."));
    }

    private void DrawAutomation(ref bool changed)
    {
        MaterialText.Text(Ui.T("Equipment cleanup"));
        var gearSaleConfirmation = plugin.Configuration.GearSaleConfirmationEnabled;
        if (WindowLayout.Checkbox("Preview equipment before selling", ref gearSaleConfirmation))
        {
            plugin.Configuration.GearSaleConfirmationEnabled = gearSaleConfirmation;
            changed = true;
        }
        ImGui.Spacing();
        ImGui.Separator();
        MaterialText.Text(Ui.T("Regular Duties"));
        var enableBmraiVbmInRegularDuties = plugin.Configuration.EnableBmraiVbmInRegularDuties;
        if (WindowLayout.Checkbox("Enable BMRAI/VBM in regular duties", ref enableBmraiVbmInRegularDuties))
        {
            plugin.Configuration.EnableBmraiVbmInRegularDuties = enableBmraiVbmInRegularDuties;
            changed = true;
        }

        MaterialText.TextWrapped(Ui.T("When enabled, entering a regular duty resets BMRAI and VBM follow targets to Slot1. Changes take effect on the next regular-duty entry."));

        ImGui.Spacing();
        ImGui.Separator();
        MaterialText.Text(Ui.T("Treasure"));
        var considerTreasureCoffers = plugin.Configuration.ConsiderTreasureCoffers;
        if (WindowLayout.Checkbox("Consider treasure coffers in planner", ref considerTreasureCoffers))
        {
            plugin.Configuration.ConsiderTreasureCoffers = considerTreasureCoffers;
            changed = true;
        }

        MaterialText.TextWrapped(Ui.T("Treat nearby eligible coffers as optional pickups. ADS keeps vertical and route-value guards."));

        var treasureDoorJiggleRecoveryEnabled = plugin.Configuration.TreasureDoorJiggleRecoveryEnabled;
        if (WindowLayout.Checkbox("Treasure door frame recovery", ref treasureDoorJiggleRecoveryEnabled))
        {
            plugin.Configuration.TreasureDoorJiggleRecoveryEnabled = treasureDoorJiggleRecoveryEnabled;
            changed = true;
        }

        MaterialText.TextWrapped(Ui.T("Briefly strafe when treasure-door follow-through appears stuck while vnav continues toward the route."));

        ImGui.Spacing();
        ImGui.Separator();
        MaterialText.Text(Ui.T("Dialog Rules"));
        var processDialogRulesOutsideOwnedDuty = plugin.Configuration.ProcessDialogRulesOutsideOwnedDuty;
        if (WindowLayout.Checkbox("Process dialog rules outside owned duties", ref processDialogRulesOutsideOwnedDuty))
        {
            plugin.Configuration.ProcessDialogRulesOutsideOwnedDuty = processDialogRulesOutsideOwnedDuty;
            changed = true;
        }

        MaterialText.TextWrapped(Ui.T("When enabled, dialog rules can run while ADS is enabled, logged in, and not zoning. Disable to require ADS-owned or leaving duty execution."));
    }

    private void DrawDataAndRules()
    {
        MaterialText.Text(Ui.T("Remote JSON Cache"));
        ImGui.BeginDisabled(plugin.RemoteJsonUpdateService.IsUpdateRunning);
        if (WindowLayout.Button(Ui.L("Update rules cache"), new Vector2(-1f, 30f)))
            plugin.ForceRemoteJsonUpdate();
        ImGui.EndDisabled();
        MaterialText.TextWrapped(Ui.Display(plugin.RemoteJsonUpdateService.LastUpdateStatus));
        MaterialText.TextWrapped(Ui.Display(TreasureDungeonData.LastLoadStatus));
        foreach (var statusLine in plugin.RemoteJsonUpdateService.GetCacheStatusLines())
            MaterialText.TextDisabled(Ui.Display(statusLine));

        ImGui.Spacing();
        ImGui.Separator();
        MaterialText.Text(Ui.T("Duty Object Rules: {0} active", plugin.ObjectPriorityRuleService.ActiveRuleCount));
        MaterialText.TextWrapped(Ui.T("Active preset: {0}", plugin.ObjectPriorityRuleService.ActivePresetName));
        MaterialText.TextWrapped(Ui.T("Territory shard index: {0}", plugin.ObjectPriorityRuleService.ConfigPath));
        DrawActionGrid(
            "ADSObjectRuleActions",
            ("Open territory shards", () => plugin.OpenPath(plugin.ObjectPriorityRuleService.TerritoriesPath)),
            ("Open frontier labels", plugin.OpenFrontierLabelUi),
            ("Open rules table", plugin.OpenRuleEditorUi),
            ("Reload active object rules", () => plugin.ObjectPriorityRuleService.Reload()));
        if (WindowLayout.Button(Ui.L("Rules walkthrough"), new Vector2(-1f, 28f)))
            plugin.OpenRulesWalkthroughUi();
        MaterialText.TextWrapped(Ui.Display(plugin.ObjectPriorityRuleService.LastSyncStatus));
        MaterialText.TextWrapped(Ui.Display(plugin.ObjectPriorityRuleService.LastLoadStatus));
        MaterialText.TextDisabled(Ui.T("Custom presets execute immediately and inherit missing contexts from DEFAULT. DEFAULT saves require debug mode."));

        ImGui.Spacing();
        MaterialText.Text(Ui.T("PR-ready Checkout"));
        DrawCheckoutConfiguration();

        ImGui.Spacing();
        ImGui.Separator();
        MaterialText.Text(Ui.T("Dialog Yes/No Rules: {0} active", plugin.DialogYesNoRuleService.ActiveRuleCount));
        MaterialText.TextWrapped(plugin.DialogYesNoRuleService.ConfigPath);
        DrawActionGrid(
            "ADSDialogRuleActions",
            ("Open dialog rules JSON", () => plugin.OpenPath(plugin.DialogYesNoRuleService.ConfigPath)),
            ("Open dialog rules table", plugin.OpenDialogRuleEditorUi),
            ("Reload dialog rules JSON", () => plugin.DialogYesNoRuleService.Reload()));
        MaterialText.TextWrapped(Ui.Display(plugin.DialogYesNoRuleService.LastSyncStatus));
        MaterialText.TextWrapped(Ui.Display(plugin.DialogYesNoRuleService.LastLoadStatus));
        MaterialText.TextDisabled(Ui.T("Only saving or importing into DEFAULT changes runtime dialog behavior."));

        ImGui.Spacing();
        ImGui.Separator();
        MaterialText.Text(Ui.T("Duty Manager"));
        MaterialText.TextWrapped(plugin.DutyCatalogService.MaturityConfigPath);
        DrawActionGrid(
            "ADSDutyMaturityActions",
            ("Open duty metadata JSON", () => plugin.OpenPath(plugin.DutyCatalogService.MaturityConfigPath)),
            ("Open Duty Manager", plugin.OpenDutyMaturityEditorUi),
            ("Reload duty metadata JSON", () => plugin.DutyCatalogService.ReloadMaturity()));
        MaterialText.TextWrapped(Ui.Display(plugin.DutyCatalogService.LastMaturityLoadStatus));
    }

    private void DrawCheckoutConfiguration()
    {
        checkoutState.RefreshFromConfiguration();

        MaterialText.TextWrapped(Ui.T("ADS only prepares local shard and index files. Reviewing and submitting them to GitHub remains manual."));
        ImGui.SetNextItemWidth(MaterialLayout.FitNextItemWidth(-1f, WindowLayout.TextMinimum(512)));
        var candidatePath = checkoutState.CandidatePath;
        var submitted = WindowLayout.InputTextWithHint(
            "##ADSSettingsCheckoutPath",
            Ui.T("repository root or ads\\territories folder"),
            ref candidatePath,
            512,
            ImGuiInputTextFlags.EnterReturnsTrue);
        checkoutState.SetCandidatePath(candidatePath);
        if (WindowLayout.Button(Ui.L("Use checkout")) || submitted)
            checkoutState.TryUseCheckout();
        ImGui.SameLine();
        if (WindowLayout.Button(Ui.L("Clear")))
            checkoutState.Clear();
        ImGui.SameLine();
        var cannotOpen = string.IsNullOrWhiteSpace(checkoutState.ConfiguredRoot)
                         || !Directory.Exists(checkoutState.ConfiguredRoot);
        ImGui.BeginDisabled(cannotOpen);
        if (WindowLayout.Button(Ui.L("Open checkout")))
            plugin.OpenPath(checkoutState.ConfiguredRoot);
        ImGui.EndDisabled();
        MaterialText.TextWrapped(Ui.Display(checkoutState.Status));
    }

    private void DrawAdvanced(ref bool changed)
    {
        MaterialText.Text(Ui.T("Display"));
        var showDebugSections = plugin.Configuration.ShowDebugSections;
        if (WindowLayout.Checkbox("Show debug sections in the Main window", ref showDebugSections))
        {
            plugin.Configuration.ShowDebugSections = showDebugSections;
            changed = true;
        }

        MaterialText.TextWrapped(Ui.T("Enables live JSON preview and short observation samples in Main > Diagnostics."));

        ImGui.Spacing();
        ImGui.Separator();
        MaterialText.Text(Ui.T("Framework Hitch Profiler"));
        var frameworkHitchProfilerEnabled = plugin.Configuration.FrameworkHitchProfilerEnabled;
        if (WindowLayout.Checkbox("Enable framework hitch profiler", ref frameworkHitchProfilerEnabled))
        {
            plugin.Configuration.FrameworkHitchProfilerEnabled = frameworkHitchProfilerEnabled;
            changed = true;
        }

        MaterialText.TextWrapped(Ui.T("Debugging only. Enables per-framework-update timing to identify slow ADS sections; leave off during normal play."));
    }

    private void DrawAbout()
    {
        MaterialText.Text(Ui.T("{0} v{1}", PluginInfo.DisplayName, PluginInfo.GetVersion()));
        MaterialText.TextWrapped(Ui.Display(PluginInfo.Summary));
        ImGui.Spacing();
        DrawActionGrid(
            "ADSAboutLinks",
            ("Open Setup Wizards", plugin.OpenWizardUi),
            ("Ko-fi", () => plugin.OpenUrl(PluginInfo.SupportUrl)),
            ("Discord", () => plugin.OpenUrl(PluginInfo.DiscordUrl)),
            ("Repository", () => plugin.OpenUrl(PluginInfo.RepoUrl)));
        MaterialText.TextDisabled(Ui.T(PluginInfo.DiscordFeedbackNote));
        ImGui.Spacing();
        MaterialText.TextWrapped(Ui.T("ADS includes staged execution phases, explicit planner objectives, immediate dead/opened ghosting, specialist inspectors, human-edited rule overrides, duty catalog, ownership controls, and IPC."));
    }

    private void DrawActionGrid(string id, params (string Label, Action Action)[] actions)
    {
        var minimum = actions.Max(action => WindowLayout.ButtonMinimum(Ui.L(action.Label))) + ImGui.GetStyle().CellPadding.X * 2;
        var columnCount = Math.Clamp((int)(ImGui.GetContentRegionAvail().X / minimum), 1, 4);
        if (!ImGui.BeginTable(id, columnCount, ImGuiTableFlags.SizingStretchSame))
            return;

        for (var index = 0; index < actions.Length; index++)
        {
            if (index % columnCount == 0)
                ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(index % columnCount);
            if (WindowLayout.Button(Ui.L(actions[index].Label + "##" + id + index), new Vector2(-1f, 28f)))
                actions[index].Action();
        }

        ImGui.EndTable();
    }
}
