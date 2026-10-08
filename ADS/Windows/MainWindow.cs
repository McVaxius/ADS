using System.Numerics;
using System.Text.Json;
using ADS.Localization;
using AethertekUI;
using Dalamud.Interface.Utility;
using ADS.Models;
using ADS.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using static ADS.Localization.Ui;

namespace ADS.Windows;

public sealed class MainWindow : PositionedWindow, IDisposable
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("AI Duty Solver###ADSMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(680f, 420f),
            MaximumSize = new Vector2(3200f, 2200f),
        };
        Size = new Vector2(1114f, 968f);
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Cog, Priority = 0, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) plugin.OpenConfigUi(); },
            ShowTooltip = () => MaterialText.SetTooltip(T("Settings")),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.SlidersH, Priority = -10, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) plugin.ToggleQuickControlUi(); },
            ShowTooltip = () => MaterialText.SetTooltip(T("ADS Controls")),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Play, Priority = -20, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left && plugin.GetDutyUiActionBlocker("Start Outside") == null) plugin.StartDutyFromOutside(); },
            ShowTooltip = () => ShowDutyTitleTooltip("Start Outside"),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.SignInAlt, Priority = -30, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left && plugin.GetDutyUiActionBlocker("Start Inside") == null) plugin.StartDutyFromInside(); },
            ShowTooltip = () => ShowDutyTitleTooltip("Start Inside"),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Redo, Priority = -40, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left && plugin.GetDutyUiActionBlocker("Resume") == null) plugin.ResumeDutyFromInside(); },
            ShowTooltip = () => ShowDutyTitleTooltip("Resume"),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Stop, Priority = -50, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) plugin.StopOwnership(); },
            ShowTooltip = () => ShowDutyTitleTooltip("Stop"),
        });
    }

    public void Dispose()
    {
    }

    public override void PreDraw()
    {
        PrepareWindowPlacement();
        WindowName = $"{T("AI Duty Solver")} {typeof(Plugin).Assembly.GetName().Version}###ADSMain";
        ReserveTitleSpace(680);
        windowMotion.Prepare(this, reducedMotion: false, roundedCorners: true);
    }

    public override void PostDraw()
    {
        base.PostDraw();
        WindowLayout.PaintTitleWithImage(this);
    }

    private void ShowDutyTitleTooltip(string action)
    {
        var blocker = plugin.GetDutyUiActionBlocker(action);
        MaterialText.SetTooltip(T(action) + (blocker == null ? string.Empty : "\n" + Ui.Display(blocker))
            + "\n" + Ui.Display(plugin.ExecutionService.LastStatus));
    }

    public override void Draw()
    {
        windowMotion.DrawChrome();
        FinalizePendingWindowPlacement();

        DrawHeader();
        ImGui.Spacing();
        DrawActionRow();
        ImGui.Spacing();
        DrawCompactStateStrip();
        ImGui.Spacing();
        DrawTabs();
    }

    private void DrawHeader()
    {
        var scale = ImGuiHelpers.GlobalScale;
        using var headerStyle = new MaterialStyleScope();
        headerStyle.Style(ImGuiStyleVar.CellPadding, Vector2.Zero);
        var compactWidth = plugin.Configuration.UiCompactVisibleOnMainWindow
            ? ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + MaterialText.Measure("C").X : 0;
        var languageWidth = plugin.Configuration.UiLanguageVisibleOnMainWindow
            ? Math.Max(130 * scale, plugin.Appearance.LanguageMenuLabels.Max(label => MaterialText.Measure(label).X) + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 2) : 0;
        var toolbarWidth = compactWidth + languageWidth + MaterialText.Measure(T("Support on Ko-fi")).X + 76 * scale
            + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + MaterialText.Measure(T("Transparency")).X
            + ImGui.GetStyle().ItemSpacing.X * 3;
        var stacked = ImGui.GetContentRegionAvail().X < toolbarWidth + 400 * scale;
        if (!ImGui.BeginTable("ADSHeader", stacked ? 1 : 2, ImGuiTableFlags.SizingStretchProp)) return;
        try
        {
            if (!stacked)
            {
                ImGui.TableSetupColumn("Title", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Access", ImGuiTableColumnFlags.WidthFixed, toolbarWidth);
            }
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            var origin = ImGui.GetCursorScreenPos();
            var side = 52 * scale;
            AdsPresentation.DrawPluginIcon(ImGui.GetWindowDrawList(), origin, origin + new Vector2(side));
            ImGui.Dummy(new Vector2(side, side));
            ImGui.SameLine(0, 26 * scale);
            ImGui.BeginGroup();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 12 * scale);
            using (plugin.Fonts.Push(AdsPresentation.Compact ? UiFontRole.CompactTitle : UiFontRole.Title))
                MaterialText.TextWrapped(T("AI Duty Solver"));
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, Math.Max(origin.Y + 30 * scale, ImGui.GetItemRectMax().Y)));
            MaterialText.TextColored(MaterialTheme.Current.Colors.OnSurfaceVariant, T("Automate your duties with confidence."));
            ImGui.EndGroup();
            if (stacked) ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(stacked ? 0 : 1);
            void Next(float nextWidth)
            {
                if (ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + nextWidth
                    <= ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X) ImGui.SameLine();
            }
            if (plugin.Configuration.UiCompactVisibleOnMainWindow)
            {
                plugin.Appearance.DrawCompactToggle();
                Next(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + MaterialText.Measure(T("Transparency")).X);
            }
            plugin.Appearance.DrawTransparencyToggle();
            Next(MaterialText.Measure(T("Support on Ko-fi")).X + 76 * scale);
            if (WindowLayout.Button(L("Support on Ko-fi"), icon: MaterialIcon.Heart)) plugin.OpenUrl(PluginInfo.SupportUrl);
            if (plugin.Configuration.UiLanguageVisibleOnMainWindow)
            {
                Next(languageWidth);
                plugin.Appearance.DrawLanguageSelector();
            }
        }
        finally { ImGui.EndTable(); }
    }

    private void DrawCompactStateStrip()
    {
        var context = plugin.DutyContextService.Current;
        var execution = plugin.ExecutionService;
        var planner = plugin.ObjectivePlannerService.Current;
        var columns = ImGui.GetContentRegionAvail().X >= 760f * ImGuiHelpers.GlobalScale ? 4 : 2;
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(7, 0) * ImGuiHelpers.GlobalScale);
        if (!ImGui.BeginTable("ADSPrimaryState", columns, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.PopStyleVar();
            return;
        }
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        AdsPresentation.Summary(plugin.Fonts.Push, MaterialIcon.Document, T("DUTY"), GetCurrentDutyLabel(context));
        ImGui.TableSetColumnIndex(1);
        AdsPresentation.Summary(plugin.Fonts.Push, MaterialIcon.Group, T("OWNERSHIP"), Display(execution.CurrentMode.ToString()));
        if (columns == 2)
            ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(columns == 2 ? 0 : 2);
        AdsPresentation.Summary(plugin.Fonts.Push, MaterialIcon.Settings, T("EXECUTION PHASE"), Display(execution.CurrentPhase.ToString()));
        ImGui.TableSetColumnIndex(columns == 2 ? 1 : 3);
        AdsPresentation.Summary(plugin.Fonts.Push, MaterialIcon.Cube, T("OBJECT NAME"), string.IsNullOrWhiteSpace(planner.TargetName) ? T("None") : planner.TargetName);
        ImGui.EndTable();
        ImGui.PopStyleVar();
    }

    private void DrawTabs()
    {
        var framePadding = ImGui.GetStyle().FramePadding;
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,
            new Vector2(framePadding.X, (AdsPresentation.Compact ? 10 : 12) * MaterialTheme.Metrics.Scale));
        bool visible;
        using(MaterialText.PushLineHeight(new[] { "Overview", "Duties", "Tools", "Diagnostics" }.Select(label => T(label)).ToArray()))
            visible = ImGui.BeginTabBar("ADSMainTabs");
        ImGui.PopStyleVar();
        if (!visible)
            return;

        if (WindowLayout.Tab(L("Overview"), MaterialIcon.Home))
        {
            DrawScrollableTabContent("ADSOverviewTabContent", DrawOverview);
            ImGui.EndTabItem();
        }

        if (WindowLayout.Tab(L("Duties"), MaterialIcon.List))
        {
            DrawScrollableTabContent("ADSDutiesTabContent", DrawDutyCatalog);
            ImGui.EndTabItem();
        }

        if (WindowLayout.Tab(L("Tools"), MaterialIcon.Wrench))
        {
            DrawScrollableTabContent("ADSToolsTabContent", DrawTools);
            ImGui.EndTabItem();
        }

        if (WindowLayout.Tab(L("Diagnostics"), MaterialIcon.Chart))
        {
            DrawScrollableTabContent("ADSDiagnosticsTabContent", DrawDiagnostics);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private static void DrawScrollableTabContent(string id, Action draw)
    {
        if (AdsPresentation.Compact) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 8 * MaterialTheme.Metrics.Scale);
        using var childStyle = new MaterialStyleScope();
        childStyle.Style(ImGuiStyleVar.WindowPadding, new Vector2(2) * MaterialTheme.Metrics.Scale);
        if (ImGui.BeginChild(id, Vector2.Zero, false, ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.AlwaysUseWindowPadding))
            draw();

        ImGui.EndChild();
    }

    private void DrawOverview()
    {
        using var overviewStyle = new MaterialStyleScope();
        overviewStyle.Style(ImGuiStyleVar.CellPadding, new Vector2(7, 0) * MaterialTheme.Metrics.Scale);
        var context = plugin.DutyContextService.Current;
        var planner = plugin.ObjectivePlannerService.Current;
        var execution = plugin.ExecutionService;
        var currentDuty = context.CurrentDuty;
        var dutyDisplay = currentDuty is not null
            ? DutyCategoryDisplayCatalog.Get(currentDuty.Category)
            : null;
        var activeLayer = plugin.ObjectPriorityRuleService.GetActiveLayerName(context) ?? T("Unknown");
        var columns = ImGui.GetContentRegionAvail().X >= 880f * ImGuiHelpers.GlobalScale ? 2 : 1;
        if (!ImGui.BeginTable("ADSOverviewCards", columns, ImGuiTableFlags.SizingStretchSame))
            return;
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        DrawCard("ADSCurrentDutyCard", MaterialIcon.Document, T("Current Duty"), T("Information about the current duty and plugin state."), () =>
        {
            string[] labels = [T("Duty"), T("Family"), T("Catalog"), T("Maturity"), T("Instanced / Catalog"), T("MSQ"), T("Unsafe Transition")];
            if (!BeginDetailTable("ADSOverviewDuty", AdsPresentation.Compact ? 0.42f : 0.39f, labels))
                return;
            DrawDetailRow(labels[0], GetCurrentDutyLabel(context));
            DrawDetailRow(labels[1], dutyDisplay is not null ? Display(dutyDisplay.FilterLabel) : T("Uncatalogued"));
            DrawDetailRow(labels[2], currentDuty is not null ? T("MATCHED") : T("NO ROW"));
            DrawDetailRow(labels[3], currentDuty is not null ? Display(DutyMaturityDisplayCatalog.GetClearanceLabel(currentDuty.ClearanceStatus)) : T("No catalog row"));
            DrawDetailRow(labels[4], T("{0} / {1}", T(context.InInstancedDuty ? "YES" : "NO"), T(context.HasCatalogMetadata ? "YES" : "NO")));
            DrawDetailRow(labels[5], T(currentDuty is not null && currentDuty.IsMainScenario ? "YES" : "NO"));
            DrawDetailRow(labels[6], T(context.IsUnsafeTransition ? "YES" : "NO"));
            ImGui.EndTable();
        });
        if (columns == 1)
            ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(columns == 1 ? 0 : 1);
        DrawCard("ADSPlannerCard", MaterialIcon.Settings, T("Planner And Execution"), T("Current objective and execution behaviour."), () =>
        {
            string[] labels = [T("Ownership"), T("Execution Phase"), T("Planner Mode"), DetailLabel("Objective kind: {0}"), DetailLabel("Objective: {0}"), DetailLabel("Explanation: {0}"), DetailLabel("Status: {0}"), DetailLabel("Loot automation: {0}")];
            if (BeginDetailTable("ADSOverviewExecution", AdsPresentation.Compact ? 0.33f : 0.38f, labels))
            {
                DrawDetailRow(labels[0], Display(execution.CurrentMode.ToString()));
                DrawDetailRow(labels[1], Display(execution.CurrentPhase.ToString()));
                DrawDetailRow(labels[2], Display(planner.Mode.ToString()));
                DrawDetailRow(labels[3], Display(planner.ObjectiveKind.ToString()));
                DrawDetailRow(labels[4], Display(planner.Objective));
                DrawDetailRow(labels[5], Display(planner.Explanation));
                DrawDetailRow(labels[6], Display(execution.LastStatus));
                DrawDetailRow(labels[7], Display(plugin.LootAutomationService.Status));
                ImGui.EndTable();
            }
            if (planner.TargetDistance.HasValue || planner.TargetVerticalDelta.HasValue)
                MaterialText.TextWrapped(T("Target distance / vertical: {0} / {1}", planner.TargetDistance?.ToString("0.0", Ui.Culture) ?? "-", planner.TargetVerticalDelta?.ToString("0.0", Ui.Culture) ?? "-"));
        });

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        DrawCard("ADSActiveOptionsCard", MaterialIcon.Sliders, T("Active Options"), T("Key options currently in effect."), () =>
        {
            string[] labels = [T("Treasure coffers"), T("Loot"), T("Object rules"), T("Dialog rules"), T("Layer")];
            if (!BeginDetailTable("ADSOverviewOptions", AdsPresentation.Compact ? 0.395f : 0.38f, labels))
                return;
            DrawDetailRow(labels[0], T(plugin.Configuration.ConsiderTreasureCoffers ? "ON" : "OFF"));
            DrawDetailRow(labels[1], Display(plugin.Configuration.LootMode.ToString()));
            DrawDetailRow(labels[2], plugin.ObjectPriorityRuleService.ActiveRuleCount.ToString());
            DrawDetailRow(labels[3], plugin.DialogYesNoRuleService.ActiveRuleCount.ToString());
            DrawDetailRow(labels[4], activeLayer);
            ImGui.EndTable();
        }, trailingGap: columns == 1);
        if (columns == 1)
            ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(columns == 1 ? 0 : 1);
        DrawCard("ADSWarningCard", MaterialIcon.Warning, T("Warnings"), T("Important information and potential issues."), () =>
        {
            if (context.InInstancedDuty && !execution.IsOwned)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.98f, 0.82f, 0.34f, 1f));
                MaterialText.TextWrapped(T("Observing only"));
                ImGui.PopStyleColor();
            }
            if (context.InInstancedDuty && !context.HasCatalogMetadata)
            {
                MaterialText.TextWrapped(T("This instanced duty has no ADS catalog row yet. Runtime still keys off live instanced-duty truth, but family/maturity metadata is uncatalogued."));
                MaterialText.TextWrapped(T("Start/Resume stay enabled even without catalog metadata. ADS trusts instanced-duty truth and treats catalog rows as maturity metadata only."));
            }
            if (!context.InInstancedDuty || execution.IsOwned)
                AdsPresentation.NoOwnershipWarning(plugin.Fonts.Push, T("No ownership warning."));
        }, trailingGap: false);
        ImGui.EndTable();
    }

    private void DrawCard(string id, MaterialIcon icon, string title, string description, Action draw, bool trailingGap = true)
    {
        using var cardStyle = new MaterialStyleScope();
        cardStyle.Style(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, Vector2.Zero);
        try
        {
            if (!ImGui.BeginTable(id, 1)) return;
            try
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                var height = id is "ADSCurrentDutyCard" or "ADSPlannerCard"
                    ? AdsPresentation.Compact ? 276 : 320
                    : AdsPresentation.Compact ? 212 : 252;
                AdsPresentation.Panel(() =>
                {
                    AdsPresentation.Heading(plugin.Fonts.Push, icon, title, description);
                    ImGui.Dummy(new Vector2(0, AdsPresentation.Compact ? 8 : 12) * MaterialTheme.Metrics.Scale);
                    ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(8f, AdsPresentation.Compact ? 2 : 4) * ImGuiHelpers.GlobalScale);
                    try { AdsPresentation.Inset(draw, ImGui.GetID(""), Math.Max(0, height - (AdsPresentation.Compact ? 80 : 92))); }
                    finally { ImGui.PopStyleVar(); }
                }, ImGui.GetID(""), height);
            }
            finally { ImGui.EndTable(); }
        }
        finally { ImGui.PopStyleVar(); }
        if (trailingGap) ImGui.Dummy(new Vector2(0, AdsPresentation.Gap * MaterialTheme.Metrics.Scale));
    }

    private static string DetailLabel(string template)
        => T(template, string.Empty).TrimEnd(' ', ':', '：');

    private static bool BeginDetailTable(string id, float labelWeight, string[] labels)
    {
        var available = Math.Max(1, ImGui.GetContentRegionAvail().X - ImGui.GetStyle().CellPadding.X * 4);
        var labelWidth = labels.Max(label => MaterialText.Measure(label).X) + 2 * MaterialTheme.Metrics.Scale;
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerH))
            return false;
        var measured = labelWidth > available * labelWeight;
        ImGui.TableSetupColumn("Label", measured ? ImGuiTableColumnFlags.WidthFixed : ImGuiTableColumnFlags.WidthStretch, measured ? labelWidth : labelWeight);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, measured ? 1 : 1 - labelWeight);
        return true;
    }

    private static void DrawDetailRow(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.PushTextWrapPos(-1);
        try
        {
            ImGui.TableSetColumnIndex(0);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MaterialTheme.Metrics.Scale);
            MaterialText.Text(label);
            ImGui.TableSetColumnIndex(1);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MaterialTheme.Metrics.Scale);
            ImGui.PushTextWrapPos(0);
            try { MaterialText.TextWrapped(value); }
            finally { ImGui.PopTextWrapPos(); }
        }
        finally { ImGui.PopTextWrapPos(); }
    }

    private static void DrawOverviewCell(int column, string label, string value)
    {
        ImGui.TableSetColumnIndex(column);
        MaterialText.TextDisabled(T(label));
        MaterialText.TextWrapped(value);
    }

    private void DrawTools()
    {
        MaterialText.Text(T("Authoring"));
        DrawLauncherGrid(
            "ADSAuthoringTools",
            ("Object Explorer", plugin.ToggleObjectExplorerUi),
            ("Object Rules", plugin.ToggleRuleEditorUi),
            ("Dialog Rules", plugin.ToggleDialogRuleEditorUi),
            ("Frontier Labels", plugin.ToggleFrontierLabelUi));

        ImGui.Spacing();
        MaterialText.Text(T("Treasure And Operations"));
        DrawLauncherGrid(
            "ADSTreasureTools",
            ("Loot Controls", plugin.ToggleLootUi),
            ("Higher / Lower", plugin.ToggleHigherLowerUi),
            ("Treasure Routes", plugin.OpenTreasureRouteEditorUi),
            ("Reflection", plugin.ToggleReflectionUi),
            ("Shop Lists", plugin.OpenShopListsUi),
            ("Desynth Controls", plugin.OpenDesynthConfigUi),
            ("Extract Materia", () => plugin.StartExtractMateria()));

        ImGui.Spacing();
        MaterialText.Text(T("Diagnostics"));
        DrawLauncherGrid(
            "ADSDiagnosticTools",
            ("Ghost Inspector", plugin.ToggleGhostListUi),
            ("Server Events", plugin.ToggleServerEventExplorerUi),
            ("VFX Explorer", plugin.ToggleVfxExplorerUi));

        if (plugin.Configuration.ShowDebugSections)
            DrawRelicPurchaseTest();

        ImGui.Spacing();
        MaterialText.Text(T("Windows And Settings"));
        DrawLauncherGrid(
            "ADSWindowTools",
            ("Settings", plugin.OpenConfigUi),
            ("Compact Controls", plugin.ToggleQuickControlUi));

        ImGui.Spacing();
        MaterialText.Text(T("Data Update"));
        using (new ImGuiDisabledBlock(plugin.RemoteJsonUpdateService.IsUpdateRunning))
        {
            if (WindowLayout.Button(L("Update Remote JSON Cache"), new Vector2(-1f, ImGui.GetFrameHeight())))
                plugin.ForceRemoteJsonUpdate();
        }

        MaterialText.TextWrapped(Display(plugin.RemoteJsonUpdateService.LastUpdateStatus));
        MaterialText.TextWrapped(Display(TreasureDungeonData.LastLoadStatus));
        foreach (var statusLine in plugin.RemoteJsonUpdateService.GetCacheStatusLines())
            MaterialText.TextDisabled(Display(statusLine));

        ImGui.Spacing();
        MaterialText.Text(T("External Links"));
        DrawLauncherGrid(
            "ADSExternalLinks",
            ("Ko-fi", () => plugin.OpenUrl(PluginInfo.SupportUrl)),
            ("Discord", () => plugin.OpenUrl(PluginInfo.DiscordUrl)),
            ("Repository", () => plugin.OpenUrl(PluginInfo.RepoUrl)));

        ImGui.Spacing();
        MaterialText.TextWrapped(T(PluginInfo.Summary));
    }

    private void DrawRelicPurchaseTest()
    {
        ImGui.Spacing();
        if (!MaterialText.CollapsingHeader(L("Relic purchase test")))
            return;
        var test = plugin.RelicPurchaseTestService;
        var selected = test.IsSelected;
        if (WindowLayout.Checkbox("Attempt remaining items after reload", ref selected))
            test.SetSelected(selected);
        MaterialText.TextWrapped(T("Buys one additional item of each of the 13 direct-shop ARR Zodiac and Heavensward Anima Poetics materials. Mysterious Map and its farming belong to Loot Goblin. Existing stock and refill thresholds are ignored; currency, unique-item limits, capacity, and unlocks still apply."));
        MaterialText.TextWrapped(T(plugin.Configuration.RelicPurchaseTest.CharacterId == 0 ? "Character: not bound."
            : test.IsBoundCharacter ? "Character: bound to this character." : "Character: bound to another character; purchases blocked."));
        using (new ImGuiDisabledBlock(!test.IsSelected || !test.IsBoundCharacter || test.IsRunning))
        {
            if (WindowLayout.Button(L("Run remaining now")))
                test.RunRemainingNow();
        }
        ImGui.SameLine();
        if (WindowLayout.Button(L("Stop##RelicPurchaseTest")))
            test.Stop();
        ImGui.SameLine();
        using (new ImGuiDisabledBlock(test.IsRunning))
        {
            if (WindowLayout.Button(L("Reset progress")))
                ImGui.OpenPopup(L("Reset relic purchase progress?"));
        }
        if (ImGui.BeginPopup(L("Reset relic purchase progress?")))
        {
            MaterialText.TextWrapped(T("Resetting allows another purchase of every completed item. Unresolved purchases cannot be discarded."));
            if (WindowLayout.Button(L("Reset confirmed")))
            {
                test.ResetProgress();
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        MaterialText.TextWrapped(T("Current action: {0}", Display(test.CurrentAction)));
        foreach (var item in RelicPurchaseTestCatalog.Items)
            MaterialText.TextWrapped(T("{0}: {1}", Ui.ItemName(item.ItemId, item.Name), Display(test.ItemStatus(item.ItemId))));
    }

    private void DrawLauncherGrid(string id, params (string Label, Action Action)[] launchers)
    {
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var widestLabel = launchers.Max(launcher => WindowLayout.ButtonMinimum(L(launcher.Label))) + ImGui.GetStyle().CellPadding.X * 2;
        var columnCount = Math.Clamp((int)(availableWidth / MathF.Max(180f * MaterialTheme.Metrics.Scale, widestLabel)), 1, 4);
        if (!ImGui.BeginTable(id, columnCount, ImGuiTableFlags.SizingStretchSame))
            return;

        for (var index = 0; index < launchers.Length; index++)
        {
            if (index % columnCount == 0)
                ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(index % columnCount);
            if (WindowLayout.Button(L($"{launchers[index].Label}##{id}{index}"), new Vector2(-1f, ImGui.GetFrameHeight() + 6f)))
                launchers[index].Action();
        }

        ImGui.EndTable();
    }

    private void DrawDiagnostics()
    {
        var context = plugin.DutyContextService.Current;
        MaterialText.Text(T("Duty Context"));
        MaterialText.TextWrapped(T("Territory / Map / CFC: {0} / {1} / {2}", context.TerritoryTypeId, context.MapId, context.ContentFinderConditionId));

        ImGui.Spacing();
        MaterialText.Text(T("Frontier State"));
        MaterialText.TextWrapped(T("Mode: {0}  |  Labels: {1} / {2}  |  Map XZ: {3} / {4}  |  XYZ: {5} / {6}",
            Display(plugin.DungeonFrontierService.CurrentMode.ToString()), plugin.DungeonFrontierService.VisitedPoints, plugin.DungeonFrontierService.TotalPoints,
            plugin.DungeonFrontierService.VisitedManualMapXzDestinations, plugin.DungeonFrontierService.ManualMapXzDestinationCount,
            plugin.DungeonFrontierService.VisitedManualXyzDestinations, plugin.DungeonFrontierService.ManualXyzDestinationCount));
        if (plugin.DungeonFrontierService.CurrentTarget is { } frontierPoint)
        {
            var frontierTargetText = frontierPoint.IsManualXyzDestination
                ? T("Frontier target: {0} world {1:0.0}, {2:0.0}, {3:0.0}", frontierPoint.Name, frontierPoint.Position.X, frontierPoint.Position.Y, frontierPoint.Position.Z)
                : frontierPoint.MapCoordinates.HasValue
                    ? T("Frontier target: {0} map {1:0.0}, {2:0.0}", frontierPoint.Name, frontierPoint.MapCoordinates.Value.X, frontierPoint.MapCoordinates.Value.Y)
                    : T("Frontier target: {0}", frontierPoint.Name);
            MaterialText.TextWrapped(frontierTargetText);
        }

        if (plugin.DungeonFrontierService.CurrentHeading is { } scoutHeading)
            MaterialText.TextWrapped(T("Frontier heading: {0:0.00}, {1:0.00}", scoutHeading.X, scoutHeading.Z));

        ImGui.Spacing();
        MaterialText.Text(T("Treasure Follow State"));
        DrawTreasurePortalFollowState();
        ImGui.Spacing();
        DrawObservationSummary();
        ImGui.Spacing();
        DrawJsonButtons();
    }

    private void DrawTreasurePortalFollowState()
    {
        var opener = plugin.TreasurePortalOpenerTracker.Current;
        var follow = plugin.BossModMultiboxFollowService;
        var openerAge = plugin.TreasurePortalOpenerTracker.CurrentAgeSeconds?.ToString("0") ?? "-";
        var witnessAge = plugin.TreasurePortalOpenerTracker.LastInteractionWitnessAgeSeconds?.ToString("0") ?? "-";
        var postTransitSettle = plugin.ExecutionService.TreasureFollowerPostTransitSettleRemainingSeconds.ToString("0.0", Ui.Culture);
        MaterialText.TextWrapped(T("Treasure role: {0} ({1})", Display(plugin.ExecutionService.TreasureDungeonRoleDisplayName), Display(plugin.ExecutionService.TreasureDungeonRoleSource)));
        var openerLocal = opener is null ? "-" : T(opener.IsLocalOpener ? "local" : "remote");
        MaterialText.TextWrapped(T("Portal opener: {0} {1} {2} age {3}s", Display(opener?.Source ?? "None"), opener?.OpenerName ?? string.Empty, openerLocal, openerAge));
        MaterialText.TextWrapped(T("Interaction witness: {0} {1} -> {2} age {3}s | post-transit settle {4}s", Display(plugin.TreasurePortalOpenerTracker.LastInteractionWitnessSource), plugin.TreasurePortalOpenerTracker.LastInteractionWitnessName, plugin.TreasurePortalOpenerTracker.LastInteractionWitnessTarget, witnessAge, postTransitSettle));
        MaterialText.TextWrapped(T("Relay: {0}", Display(plugin.TreasurePortalOpenerTracker.RelayStatus)));
        var commandAccepted = follow.BmraiFollowCommandAccepted is null
            ? T("not sent")
            : T(follow.BmraiFollowCommandAccepted.Value ? "accepted" : "rejected");
        MaterialText.TextWrapped(T("BMRAI/VBM follow: {0} method {1} {2} {3}", T(follow.FollowApplied ? "applied" : "not applied"), follow.BmraiFollowCommandMethod, commandAccepted, follow.BmraiFollowCommandText));
        MaterialText.TextWrapped(T("BMRAI/VBM reason: {0}", Display(follow.BmraiFollowCommandStatus)));
    }

    private void DrawActionRow()
    {
        var inInstancedDuty = plugin.DutyContextService.Current.InInstancedDuty;
        var blue = MaterialTheme.Current.Colors.Primary;
        var red = AdsPresentation.Rgb(0xBE172A);
        var labels = new[] { T("Start Outside"), T("Start Inside"), T("Resume"), T("Leave"), T("Stop"), T("Guided Setup") };
        var captions = new[] { T("Prepare and start"), T("Start in current duty"), T("Continue execution"),
            T("Leave current duty"), T("Halt execution"), T("Step-by-step help") };
        var scale = ImGuiHelpers.GlobalScale;
        var minima = labels.Select((label, index) => AdsPresentation.ActionMinimum(plugin.Fonts.Push, label, captions[index]) + 8 * scale).ToArray();
        var available = ImGui.GetContentRegionAvail().X;
        var columns = minima.Sum() <= available ? 6 : Math.Clamp((int)(available / minima.Max()), 1, 6);
        using var actionGridStyle = new MaterialStyleScope();
        actionGridStyle.Style(ImGuiStyleVar.CellPadding, new Vector2(4 * scale, 0));
        if (ImGui.BeginTable("ADSPrimaryActions", columns, ImGuiTableFlags.SizingStretchProp))
        {
            for (var column = 0; column < columns; column++)
                ImGui.TableSetupColumn("Action" + column, ImGuiTableColumnFlags.WidthStretch,
                    columns == 6 ? minima[column] : minima.Max());
            ImGui.TableNextColumn();
            if (DrawActionButton(T("Start Outside"), T("Prepare and start"), "Start Outside", blue))
                plugin.StartDutyFromOutside();

            ImGui.TableNextColumn();
            using (new ImGuiDisabledBlock(!inInstancedDuty))
            {
                if (DrawActionButton(T("Start Inside"), T("Start in current duty"), "Start Inside", blue))
                    plugin.StartDutyFromInside();
            }

            ImGui.TableNextColumn();
            using (new ImGuiDisabledBlock(!inInstancedDuty))
            {
                if (DrawActionButton(T("Resume"), T("Continue execution"), "Resume"))
                    plugin.ResumeDutyFromInside();
            }

            ImGui.TableNextColumn();
            using (new ImGuiDisabledBlock(!inInstancedDuty))
            {
                if (DrawActionButton(T("Leave"), T("Leave current duty"), "Leave"))
                    plugin.LeaveDuty();
            }

            ImGui.TableNextColumn();
            if (DrawActionButton(T("Stop"), T("Halt execution"), "Stop", red))
                plugin.StopOwnership();

            ImGui.TableNextColumn();
            if (DrawActionButton(T("Guided Setup"), T("Step-by-step help"), "Guided Setup"))
                plugin.OpenWizardUi();
            ImGui.EndTable();
        }
    }

    internal bool DrawActionButton(string label, string caption, string id, Vector4? color = null)
        => AdsPresentation.Action(plugin.Fonts, label, caption, id, color);

    private void DrawDutyCatalog()
    {
        var context = plugin.DutyContextService.Current;
        var currentDuty = context.CurrentDuty;
        var snapshot = DutyRuleCoverageHelper.BuildSnapshot(
            plugin.DutyCatalogService.Entries,
            plugin.ObjectPriorityRuleService.Current.Rules);
        var coverage = currentDuty is null ? default : snapshot.Get(currentDuty);

        MaterialText.Text(T("Duty Coverage"));
        if (ImGui.BeginTable("ADSDutyCoverageSummary", 3, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableNextRow();
            DrawOverviewCell(0, "Current Duty", GetCurrentDutyLabel(context));
            DrawOverviewCell(1, "Maturity", currentDuty is null ? "-" : Display(DutyMaturityDisplayCatalog.GetClearanceLabel(currentDuty.ClearanceStatus)));
            DrawOverviewCell(2, "Family", currentDuty is null ? "-" : Display(DutyCategoryDisplayCatalog.Get(currentDuty.Category).FilterLabel));
            ImGui.TableNextRow();
            DrawOverviewCell(0, "Enabled / Total Rules", currentDuty is null ? "-" : $"{coverage.EnabledRuleCount} / {coverage.AssociatedRuleCount}");
            DrawOverviewCell(1, "Valid Waypoints", currentDuty is null ? "-" : coverage.EnabledValidWaypointCount.ToString());
            DrawOverviewCell(2, "Scope Warnings", currentDuty is null ? "-" : coverage.RedundantScopeMismatchCount.ToString());
            ImGui.TableNextRow();
            DrawOverviewCell(0, "Global Rules", snapshot.GlobalRuleCount.ToString());
            DrawOverviewCell(1, "Unresolved Rules", snapshot.UnresolvedRuleCount.ToString());
            DrawOverviewCell(2, "Catalog Duties", plugin.DutyCatalogService.Entries.Count.ToString());
            ImGui.EndTable();
        }

        ImGui.Spacing();
        MaterialText.TextWrapped(T("Open the full manager for catalog work, jump directly to duties without authored rules, or inspect every rule diagnostically associated with the current duty."));
        if (WindowLayout.Button(L("Duty Manager")))
            plugin.OpenDutyMaturityEditorUi();
        ImGui.SameLine();
        if (WindowLayout.Button(L("Missing-duty Work")))
            plugin.OpenMissingDutyWorkUi();
        ImGui.SameLine();
        using (new ImGuiDisabledBlock(currentDuty is null))
        {
            if (WindowLayout.Button(L("Current-duty Rules")) && currentDuty is not null)
                plugin.OpenRuleEditorUi(currentDuty);
        }
    }
    private void DrawObservationSummary()
    {
        var observation = plugin.ObservationMemoryService.Current;
        MaterialText.Text(T("Observation Summary"));
        MaterialText.TextWrapped(T("Live monsters: {0}", observation.LiveMonsters.Count));
        MaterialText.TextWrapped(T("Live follow: {0}", observation.LiveFollowTargets.Count));
        MaterialText.TextWrapped(T("Monster ghosts: {0}", observation.MonsterGhosts.Count));
        MaterialText.TextWrapped(T("Live interactables: {0}", observation.LiveInteractables.Count));
        MaterialText.TextWrapped(T("Interactable ghosts: {0}", observation.InteractableGhosts.Count));
        MaterialText.TextWrapped(T("Execution phase summary: {0} | {1}", Display(plugin.ExecutionService.CurrentPhase.ToString()), Display(plugin.ExecutionService.LastStatus)));

        if (!plugin.Configuration.ShowDebugSections)
            return;

        ImGui.Spacing();
        MaterialText.Text(T("Debug Preview"));
        DrawNameList("Live monster sample", observation.LiveMonsters.Select(x => x.Name));
        DrawNameList("Live follow sample", observation.LiveFollowTargets.Select(x => x.Name));
        DrawNameList("Monster ghost sample", observation.MonsterGhosts.Select(x => x.Name));
        DrawNameList("Live interactable sample", observation.LiveInteractables.Select(x => $"{x.Name} [{x.Classification}]"));
        DrawNameList("Interactable ghost sample", observation.InteractableGhosts.Select(x => $"{x.Name} [{x.Classification}]"));
    }

    private void DrawJsonButtons()
    {
        if (WindowLayout.SmallButton(L("Copy Status JSON")))
            ImGui.SetClipboardText(plugin.GetStatusJson());
        ImGui.SameLine();
        if (WindowLayout.SmallButton(L("Copy Analysis JSON")))
            ImGui.SetClipboardText(plugin.GetCurrentAnalysisJson());

        if (!plugin.Configuration.ShowDebugSections)
            return;

        if (MaterialText.CollapsingHeader(L("Live JSON Preview")))
            MaterialText.TextWrapped(FormatJson(plugin.GetCurrentAnalysisJson()));
    }

    private static string FormatJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }

    private static string GetCurrentDutyLabel(DutyContextSnapshot context)
    {
        if (context.CurrentDuty is not null)
            return Ui.DutyName(context.CurrentDuty);

        return context.InInstancedDuty
            ? T("territory {0}", context.TerritoryTypeId)
            : T("None");
    }

    private static void DrawNameList(string label, IEnumerable<string> names)
    {
        var value = string.Join(", ", names.Take(3));
        if (string.IsNullOrWhiteSpace(value))
            value = T("none");
        MaterialText.TextWrapped(T("{0}: {1}", T(label), value));
    }

    private readonly ref struct ImGuiDisabledBlock
    {
        private readonly bool disabled;

        public ImGuiDisabledBlock(bool disabled)
        {
            this.disabled = disabled;
            if (disabled)
                ImGui.BeginDisabled();
        }

        public void Dispose()
        {
            if (disabled)
                ImGui.EndDisabled();
        }
    }
}
