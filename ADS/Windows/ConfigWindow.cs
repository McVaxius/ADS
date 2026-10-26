using ADS.Localization;
using AethertekUI;
using System.Numerics;
using ADS.Services;
using ADS.Models;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class ConfigWindow : PositionedWindow, IDisposable
{
    private readonly Plugin plugin;
    private readonly ObjectRuleCheckoutState checkoutState;
    private readonly AethertekUI.Dalamud.MaterialSupportLog supportLog = new();
    private int settingsPage;

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
        Size = plugin.Configuration.UiCompact ? new Vector2(640f, 480f) : new Vector2(760f, 640f);
        Flags |= ImGuiWindowFlags.HorizontalScrollbar;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        DrawWindowChrome();
        FinalizePendingWindowPlacement();

        var changed = false;
        using var controls = MaterialControls.Push(MaterialControlContext.Toolbar);
        var labels = new[] { "General", "Automation", "Data & Rules", "Debug###Advanced", "Appearance###Window appearance", "About" };
        var barRoot = ImGui.GetID("ADSSettingsTabs");
        var navWidth = labels.Max(label => WindowLayout.ButtonMinimum(Ui.L(label))) + 24 * MaterialTheme.Metrics.Scale;
        var sidebar = ImGui.GetContentRegionAvail().X >= navWidth + 300 * MaterialTheme.Metrics.Scale;
        if (sidebar)
        {
            var navigationVisible = ImGui.BeginChild("ADSSettingsNavigation", new Vector2(navWidth, 0), false);
            try { if (navigationVisible) DrawNavigation(); }
            finally { ImGui.EndChild(); }
            ImGui.SameLine();
        }
        else DrawNavigation();
        var contentVisible = ImGui.BeginChild("ADSSettingsContent", Vector2.Zero, false, ImGuiWindowFlags.HorizontalScrollbar);
        try
        {
            if (contentVisible)
            {
                ImGuiP.PushOverrideID(barRoot);
                // Native tab bodies previously used these exact label-derived ID roots.
                ImGui.PushID(Ui.L(settingsPage == 4 ? "General" : labels[settingsPage]));
                try
                {
                    using (plugin.Fonts.Push(UiFontRole.PluginName))
                    {
                        if (MaterialTheme.Current.IsLcars || MaterialTheme.Current.IsCyberpunk)
                            MaterialSkin.Heading(MaterialTheme.Current.IsCyberpunk
                                ? Ui.T(labels[settingsPage].Split("##")[0]).ToUpper(Ui.Culture)
                                : Ui.T(labels[settingsPage].Split("##")[0]), 1);
                        else MaterialText.Text(Ui.T(labels[settingsPage].Split("##")[0]));
                    }
                    MaterialText.TextDisabled(Ui.T("Configuration saves immediately."));
                    switch (settingsPage)
                    {
                        case 0: DrawGeneral(ref changed); break;
                        case 1: DrawAutomation(ref changed); break;
                        case 2: DrawDataAndRules(); break;
                        case 3: DrawDebug(ref changed); break;
                        case 4: plugin.Appearance.DrawWindowAppearanceSettings(); break;
                        case 5: DrawAbout(); break;
                    }
                }
                finally { ImGui.PopID(); ImGui.PopID(); }
            }
        }
        finally { ImGui.EndChild(); }
        if (changed) plugin.SaveConfiguration();

        void DrawNavigation()
        {
            ImGuiP.PushOverrideID(barRoot);
            try
            {
                for (var index = 0; index < labels.Length; index++)
                {
                    if (!sidebar && index > 0) WindowLayout.SameLineIfFits(WindowLayout.ButtonMinimum(Ui.L(labels[index])));
                    if (sidebar)
                    {
                        using var selected = new MaterialStyleScope();
                        if (MaterialTheme.Current.IsLcars)
                        {
                            selected.Color(ImGuiCol.Header, MaterialLcars.Lilac);
                            selected.Color(ImGuiCol.HeaderHovered, MaterialLcars.Periwinkle);
                            selected.Color(ImGuiCol.HeaderActive, MaterialLcars.Orange);
                            if (settingsPage == index) selected.Color(ImGuiCol.Text, MaterialLcars.Background);
                        }
                        else if (AdsPresentation.IsMaterial)
                        {
                            var colors = MaterialTheme.Current.Colors;
                            selected.Color(ImGuiCol.Header, colors.Primary);
                            selected.Color(ImGuiCol.HeaderHovered, Vector4.Lerp(colors.Primary, colors.OnSurface, .12f));
                            selected.Color(ImGuiCol.HeaderActive, Vector4.Lerp(colors.Primary, colors.Background, .12f));
                            if (settingsPage == index) selected.Color(ImGuiCol.Text, colors.OnPrimary);
                        }
                        if (MaterialText.Selectable(Ui.L(labels[index]), settingsPage == index,
                            size: new Vector2(0, ImGui.GetFrameHeight()))) settingsPage = index;
                    }
                    else if (WindowLayout.Button(Ui.L(labels[index]))) settingsPage = index;
                }
            }
            finally { ImGui.PopID(); }
        }
    }

    private void DrawGeneral(ref bool changed)
    {
        MaterialSettings.Section(Ui.T("Startup"));
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

        MaterialSettings.Section(Ui.T("DTR Bar"));
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
        => DrawAutomationSettings(plugin.Configuration, ref changed);

    internal static void DrawAutomationSettings(Configuration configuration, ref bool changed)
    {
        MaterialSettings.Section(Ui.T("Integrations & regular duties"));
        var disableAutoDuty = configuration.DisableAutoDutyOnStart;
        if (WindowLayout.Checkbox("Disable AutoDuty on ADS start", ref disableAutoDuty))
        {
            configuration.DisableAutoDutyOnStart = disableAutoDuty;
            changed = true;
        }
        if (ImGui.IsItemHovered()) MaterialText.SetTooltip(Ui.T("Off by default. Leave AutoDuty loaded so you can choose which plugin runs each duty."));
        ImGui.Spacing();
        var enableBmraiVbmInRegularDuties = configuration.EnableBmraiVbmInRegularDuties;
        if (WindowLayout.Checkbox("Enable BMRAI/VBM in regular duties", ref enableBmraiVbmInRegularDuties))
        {
            configuration.EnableBmraiVbmInRegularDuties = enableBmraiVbmInRegularDuties;
            changed = true;
        }

        if (ImGui.IsItemHovered()) MaterialText.SetTooltip(Ui.T("When enabled, entering a regular duty resets BMRAI and VBM follow targets to Slot1. Changes take effect on the next regular-duty entry."));

        MaterialSettings.Section(Ui.T("Equipment cleanup"));
        var gearSaleConfirmation = configuration.GearSaleConfirmationEnabled;
        if (WindowLayout.Checkbox("Preview equipment before selling", ref gearSaleConfirmation))
        {
            configuration.GearSaleConfirmationEnabled = gearSaleConfirmation;
            changed = true;
        }

        MaterialSettings.Section(Ui.T("Treasure"));
        var considerTreasureCoffers = configuration.ConsiderTreasureCoffers;
        if (WindowLayout.Checkbox("Consider treasure coffers in planner", ref considerTreasureCoffers))
        {
            configuration.ConsiderTreasureCoffers = considerTreasureCoffers;
            changed = true;
        }

        if (ImGui.IsItemHovered()) MaterialText.SetTooltip(Ui.T("Treat nearby eligible coffers as optional pickups. ADS keeps vertical and route-value guards."));

        var treasureDoorJiggleRecoveryEnabled = configuration.TreasureDoorJiggleRecoveryEnabled;
        if (WindowLayout.Checkbox("Treasure door frame recovery", ref treasureDoorJiggleRecoveryEnabled))
        {
            configuration.TreasureDoorJiggleRecoveryEnabled = treasureDoorJiggleRecoveryEnabled;
            changed = true;
        }

        if (ImGui.IsItemHovered()) MaterialText.SetTooltip(Ui.T("Briefly strafe when treasure-door follow-through appears stuck while vnav continues toward the route."));

        MaterialSettings.Section(Ui.T("Dialog Rules"));
        var processDialogRulesOutsideOwnedDuty = configuration.ProcessDialogRulesOutsideOwnedDuty;
        if (WindowLayout.Checkbox("Process dialog rules outside owned duties", ref processDialogRulesOutsideOwnedDuty))
        {
            configuration.ProcessDialogRulesOutsideOwnedDuty = processDialogRulesOutsideOwnedDuty;
            changed = true;
        }

        if (ImGui.IsItemHovered()) MaterialText.SetTooltip(Ui.T("When enabled, dialog rules can run while ADS is enabled, logged in, and not zoning. Disable to require ADS-owned or leaving duty execution."));
    }

    private void DrawDataAndRules()
    {
        MaterialSettings.Section(Ui.T("Updates"));
        ImGui.BeginDisabled(plugin.RemoteJsonUpdateService.IsUpdateRunning);
        if (WindowLayout.Button(Ui.L("Update rules cache"), new Vector2(-1f, 30f)))
            plugin.ForceRemoteJsonUpdate();
        ImGui.EndDisabled();
        MaterialText.TextWrapped(Ui.Display(plugin.RemoteJsonUpdateService.LastUpdateStatus));
        MaterialText.TextWrapped(Ui.Display(TreasureDungeonData.LastLoadStatus));
        foreach (var statusLine in plugin.RemoteJsonUpdateService.GetCacheStatusLines())
            MaterialText.TextDisabled(Ui.Display(statusLine));
        MaterialText.TextWrapped(Ui.T("Published presets arrive through Update JSON or the normal 24-hour refresh after publication. Downloading keeps the active selection."));

        MaterialSettings.Section(Ui.T("Object rules & presets"));
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
        MaterialText.TextWrapped(Ui.T("Published presets are protected and updated automatically. Use New Preset to create a personal copy."));

        MaterialSettings.Section(Ui.T("Dialog rules & duty metadata"));
        MaterialText.Text(Ui.T("Dialog Yes/No Rules: {0} active", plugin.DialogYesNoRuleService.ActiveRuleCount));
        MaterialText.TextWrapped(Ui.T("Active preset: {0}", plugin.DialogYesNoRuleService.ActivePresetName));
        MaterialText.TextWrapped(plugin.DialogYesNoRuleService.ActivePresetPath);
        DrawActionGrid(
            "ADSDialogRuleActions",
            ("Open dialog rules JSON", () => plugin.OpenPath(plugin.DialogYesNoRuleService.ActivePresetPath)),
            ("Open dialog rules table", plugin.OpenDialogRuleEditorUi),
            ("Reload dialog rules JSON", () => plugin.DialogYesNoRuleService.Reload()));
        MaterialText.TextWrapped(Ui.Display(plugin.DialogYesNoRuleService.LastSyncStatus));
        MaterialText.TextWrapped(Ui.Display(plugin.DialogYesNoRuleService.LastLoadStatus));

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
        ImGui.Spacing();
        if (MaterialText.CollapsingHeader(Ui.L("PR-ready Checkout")))
            DrawCheckoutConfiguration();
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

    private void DrawDebug(ref bool changed)
        => DrawDebugSettings(plugin.Configuration, plugin.DebugStrafeService.Enabled,
            enabled => plugin.SetDebugModeEnabled(enabled, "settings"), ref changed);

    internal static void DrawDebugSettings(Configuration configuration, bool debugEnabled, Action<bool> setDebugEnabled, ref bool changed)
    {
        var localChanged = false;
        DrawSettingsCard("ADSDebugModeCard", "Debug mode", () =>
        {
            if (WindowLayout.Checkbox("Enable debug mode", ref debugEnabled))
                setDebugEnabled(debugEnabled);
            MaterialText.TextWrapped(Ui.T("Debug tools and rule authoring."));
        });

        ImGui.Spacing();
        DrawSettingsCard("ADSDeathReturnCard", "Death & Return", () =>
        {
            ImGui.BeginDisabled(!debugEnabled);
            try
            {
                string[] labels = [Ui.T("Restart from entrance"), Ui.T("Resume")];
                MaterialText.Text(Ui.T("After death + Return"));
                ImGui.SameLine();
                var minimum = WindowLayout.ComboMinimum(labels);
                if (ImGui.GetContentRegionAvail().X < minimum) ImGui.NewLine();
                ImGui.SetNextItemWidth(-1f);
                var index = (int)configuration.DebugReturnPolicy;
                if (WindowLayout.Combo("##ADSAfterReturnPolicy", ref index, labels, labels.Length))
                {
                    configuration.DebugReturnPolicy = (DebugReturnPolicy)index;
                    localChanged = true;
                }
                MaterialText.TextWrapped(Ui.T(configuration.DebugReturnPolicy == DebugReturnPolicy.RestartFromEntrance
                    ? "Restart ADS progression after Return. Raise keeps current progress."
                    : "Resume existing progression after Return. Raise keeps current progress."));
            }
            finally { ImGui.EndDisabled(); }
            if (!debugEnabled) MaterialText.TextDisabled(Ui.T("Available while debug mode is on."));
        });

        ImGui.Spacing();
        DrawSettingsCard("ADSDebugDiagnosticsCard", "Diagnostics", () =>
        {
            var showDebugSections = configuration.ShowDebugSections;
            if (WindowLayout.Checkbox("Show debug sections in the Main window", ref showDebugSections))
            {
                configuration.ShowDebugSections = showDebugSections;
                localChanged = true;
            }
            MaterialText.TextWrapped(Ui.T("Enables live JSON preview and short observation samples in Main > Diagnostics."));
            var profiler = configuration.FrameworkHitchProfilerEnabled;
            if (WindowLayout.Checkbox("Enable framework hitch profiler", ref profiler))
            {
                configuration.FrameworkHitchProfilerEnabled = profiler;
                localChanged = true;
            }
            MaterialText.TextWrapped(Ui.T("Debugging only. Enables per-framework-update timing to identify slow ADS sections; leave off during normal play."));
        });
        changed |= localChanged;
    }

    private static void DrawSettingsCard(string id, string heading, Action draw)
    {
        var root = ImGui.GetID(string.Empty);
        var scale = MaterialTheme.Metrics.Scale;
        ImGui.PushID(id);
        try
        {
            // Reuse ADS's content-sized panels and retained native control ID root.
            AdsPresentation.Panel(() =>
            {
                var start = ImGui.GetCursorScreenPos();
                ImGui.GetWindowDrawList().AddRectFilled(start, start + new Vector2(3 * scale, ImGui.GetTextLineHeight()),
                    ImGui.ColorConvertFloat4ToU32(MaterialTheme.Current.Colors.Primary));
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 11 * scale);
                MaterialText.TextWrapped(Ui.T(heading));
                ImGui.Spacing();
                draw();
            }, root);
        }
        finally { ImGui.PopID(); }
    }

    private void DrawAbout()
    {
        supportLog.Draw(Plugin.PluginInterface, text => Ui.T(text), plugin.OpenPath,
            ex => Plugin.Log.Warning(ex, "[ADS] Manual support log export failed."), Plugin.CommandManager);
        ImGui.Spacing();
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
