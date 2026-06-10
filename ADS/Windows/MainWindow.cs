using System.Numerics;
using System.Text.Json;
using ADS.Models;
using ADS.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class MainWindow : PositionedWindow, IDisposable
{
    private readonly Plugin plugin;
    private readonly Dictionary<DutyCategory, bool> dutyCategoryFilters = DutyCategoryDisplayCatalog.Entries
        .ToDictionary(x => x.Category, _ => true);
    private string dutyCatalogSearch = string.Empty;
    private string? selectedDutyCatalogKey;

    public MainWindow(Plugin plugin)
        : base("AI Duty Solver###ADSMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(860f, 640f),
            MaximumSize = new Vector2(3200f, 2200f),
        };
        Size = new Vector2(1240f, 960f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
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
        ImGui.TextUnformatted($"{PluginInfo.DisplayName} v{PluginInfo.GetVersion()}");
        ImGui.SameLine();
        ImGui.TextDisabled("Operator console");
    }

    private void DrawCompactStateStrip()
    {
        var context = plugin.DutyContextService.Current;
        var execution = plugin.ExecutionService;
        if (!ImGui.BeginTable("ADSPrimaryState", 3, ImGuiTableFlags.SizingStretchSame))
            return;

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextDisabled("DUTY");
        ImGui.TextWrapped(GetCurrentDutyLabel(context));
        ImGui.TableSetColumnIndex(1);
        ImGui.TextDisabled("OWNERSHIP");
        ImGui.TextWrapped(execution.CurrentMode.ToString());
        ImGui.TableSetColumnIndex(2);
        ImGui.TextDisabled("EXECUTION PHASE");
        ImGui.TextWrapped(execution.CurrentPhase.ToString());
        ImGui.EndTable();
    }

    private void DrawTabs()
    {
        if (!ImGui.BeginTabBar("ADSMainTabs"))
            return;

        if (ImGui.BeginTabItem("Overview"))
        {
            DrawScrollableTabContent("ADSOverviewTabContent", DrawOverview);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Duties"))
        {
            DrawScrollableTabContent("ADSDutiesTabContent", DrawDutyCatalog);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Tools"))
        {
            DrawScrollableTabContent("ADSToolsTabContent", DrawTools);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Diagnostics"))
        {
            DrawScrollableTabContent("ADSDiagnosticsTabContent", DrawDiagnostics);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private static void DrawScrollableTabContent(string id, Action draw)
    {
        if (ImGui.BeginChild(id, Vector2.Zero, false))
            draw();

        ImGui.EndChild();
    }

    private void DrawOverview()
    {
        var context = plugin.DutyContextService.Current;
        var planner = plugin.ObjectivePlannerService.Current;
        var execution = plugin.ExecutionService;
        var currentDuty = context.CurrentDuty;
        var dutyDisplay = currentDuty is not null
            ? DutyCategoryDisplayCatalog.Get(currentDuty.Category)
            : null;
        var activeLayer = plugin.ObjectPriorityRuleService.GetActiveLayerName(context) ?? "Unknown";

        ImGui.TextUnformatted("Current Duty");
        if (ImGui.BeginTable("ADSOverviewDuty", 3, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableNextRow();
            DrawOverviewCell(0, "Duty", GetCurrentDutyLabel(context));
            DrawOverviewCell(1, "Family", dutyDisplay?.FilterLabel ?? "Uncatalogued");
            DrawOverviewCell(2, "Readiness", currentDuty is not null ? GetSupportLevelLabel(currentDuty.SupportLevel) : "No catalog row");
            ImGui.TableNextRow();
            DrawOverviewCell(0, "Maturity", currentDuty is not null ? GetClearanceLabel(currentDuty.ClearanceStatus) : "No catalog row");
            DrawOverviewCell(1, "Instanced / Catalog", $"{(context.InInstancedDuty ? "YES" : "NO")} / {(context.HasCatalogMetadata ? "YES" : "NO")}");
            DrawOverviewCell(2, "Unsafe Transition", context.IsUnsafeTransition ? "YES" : "NO");
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Planner And Execution");
        if (ImGui.BeginTable("ADSOverviewExecution", 3, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableNextRow();
            DrawOverviewCell(0, "Ownership", execution.CurrentMode.ToString());
            DrawOverviewCell(1, "Execution Phase", execution.CurrentPhase.ToString());
            DrawOverviewCell(2, "Planner Mode", planner.Mode.ToString());
            ImGui.EndTable();
        }

        ImGui.TextWrapped($"Objective kind: {planner.ObjectiveKind}");
        ImGui.TextWrapped($"Objective: {planner.Objective}");
        ImGui.TextWrapped($"Explanation: {planner.Explanation}");
        if (planner.TargetDistance.HasValue || planner.TargetVerticalDelta.HasValue)
            ImGui.TextWrapped($"Target distance / vertical: {planner.TargetDistance?.ToString("0.0") ?? "-"} / {planner.TargetVerticalDelta?.ToString("0.0") ?? "-"}");
        ImGui.TextWrapped($"Execution phase summary: {execution.LastStatus}");
        ImGui.TextWrapped($"Loot automation: {plugin.LootAutomationService.Status}");

        ImGui.Spacing();
        ImGui.TextUnformatted("Active Options");
        ImGui.TextWrapped(
            $"Treasure coffers: {(plugin.Configuration.ConsiderTreasureCoffers ? "ON" : "OFF")}  |  " +
            $"Loot: {plugin.Configuration.LootMode}  |  " +
            $"Object rules: {plugin.ObjectPriorityRuleService.ActiveRuleCount}  |  " +
            $"Dialog rules: {plugin.DialogYesNoRuleService.ActiveRuleCount}  |  " +
            $"Layer: {activeLayer}");

        ImGui.Spacing();
        ImGui.TextUnformatted("Warnings");
        if (context.InInstancedDuty && !execution.IsOwned)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.98f, 0.82f, 0.34f, 1f));
            ImGui.TextUnformatted("Observing only");
            ImGui.PopStyleColor();
        }

        if (context.InInstancedDuty && !context.HasCatalogMetadata)
        {
            ImGui.TextWrapped("This instanced duty has no ADS catalog row yet. Runtime still keys off live instanced-duty truth, but family/readiness metadata is uncatalogued.");
            ImGui.TextWrapped("Start/Resume stay enabled even without catalog metadata. ADS trusts instanced-duty truth and treats catalog rows as maturity metadata only.");
        }

        if (!context.InInstancedDuty || execution.IsOwned)
            ImGui.TextDisabled("No ownership warning.");
    }

    private static void DrawOverviewCell(int column, string label, string value)
    {
        ImGui.TableSetColumnIndex(column);
        ImGui.TextDisabled(label.ToUpperInvariant());
        ImGui.TextWrapped(value);
    }

    private void DrawTools()
    {
        ImGui.TextUnformatted("Authoring");
        DrawLauncherGrid(
            "ADSAuthoringTools",
            ("Object Explorer", plugin.ToggleObjectExplorerUi),
            ("Object Rules", plugin.ToggleRuleEditorUi),
            ("Dialog Rules", plugin.ToggleDialogRuleEditorUi),
            ("Frontier Labels", plugin.ToggleFrontierLabelUi));

        ImGui.Spacing();
        ImGui.TextUnformatted("Treasure And Operations");
        DrawLauncherGrid(
            "ADSTreasureTools",
            ("Loot Controls", plugin.ToggleLootUi),
            ("Higher / Lower", plugin.ToggleHigherLowerUi),
            ("Treasure Routes", plugin.OpenTreasureRouteEditorUi),
            ("Reflection", plugin.ToggleReflectionUi));

        ImGui.Spacing();
        ImGui.TextUnformatted("Desynthesis");
        DrawLauncherGrid(
            "ADSDesynthesisTools",
            ("Desynth Controls", plugin.OpenDesynthConfigUi),
            ("Inventory Only", () => plugin.StartDesynth("inventory-only")),
            ("Everywhere, Skip Gearsets", () => plugin.StartDesynth("everywhere-skip-gearsets")),
            ("Everywhere", () => plugin.StartDesynth("everywhere")),
            ("Stop Utility", () => plugin.CancelUtility()));

        ImGui.Spacing();
        ImGui.TextUnformatted("Diagnostics");
        DrawLauncherGrid(
            "ADSDiagnosticTools",
            ("Ghost Inspector", plugin.ToggleGhostListUi),
            ("Server Events", plugin.ToggleServerEventExplorerUi),
            ("VFX Explorer", plugin.ToggleVfxExplorerUi));

        ImGui.Spacing();
        ImGui.TextUnformatted("Windows And Settings");
        DrawLauncherGrid(
            "ADSWindowTools",
            ("Settings", plugin.OpenConfigUi),
            ("Compact Controls", plugin.ToggleQuickControlUi));

        ImGui.Spacing();
        ImGui.TextUnformatted("Data Update");
        using (new ImGuiDisabledBlock(plugin.RemoteJsonUpdateService.IsUpdateRunning))
        {
            if (ImGui.Button("Update Remote JSON Cache", new Vector2(-1f, 28f)))
                plugin.ForceRemoteJsonUpdate();
        }

        ImGui.TextWrapped(plugin.RemoteJsonUpdateService.LastUpdateStatus);
        ImGui.TextWrapped(TreasureDungeonData.LastLoadStatus);
        foreach (var statusLine in plugin.RemoteJsonUpdateService.GetCacheStatusLines())
            ImGui.TextDisabled(statusLine);

        ImGui.Spacing();
        ImGui.TextUnformatted("External Links");
        DrawLauncherGrid(
            "ADSExternalLinks",
            ("Ko-fi", () => plugin.OpenUrl(PluginInfo.SupportUrl)),
            ("Discord", () => plugin.OpenUrl(PluginInfo.DiscordUrl)),
            ("Repository", () => plugin.OpenUrl(PluginInfo.RepoUrl)));

        ImGui.Spacing();
        ImGui.TextWrapped(PluginInfo.Summary);
        ImGui.TextWrapped(PluginInfo.PilotDutySummary);
    }

    private void DrawLauncherGrid(string id, params (string Label, Action Action)[] launchers)
    {
        var columnCount = ImGui.GetContentRegionAvail().X >= 1000f ? 4 : 3;
        if (!ImGui.BeginTable(id, columnCount, ImGuiTableFlags.SizingStretchSame))
            return;

        for (var index = 0; index < launchers.Length; index++)
        {
            if (index % columnCount == 0)
                ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(index % columnCount);
            if (ImGui.Button($"{launchers[index].Label}##{id}{index}", new Vector2(-1f, 28f)))
                launchers[index].Action();
        }

        ImGui.EndTable();
    }

    private void DrawDiagnostics()
    {
        var context = plugin.DutyContextService.Current;
        ImGui.TextUnformatted("Duty Context");
        ImGui.TextWrapped($"Territory / Map / CFC: {context.TerritoryTypeId} / {context.MapId} / {context.ContentFinderConditionId}");

        ImGui.Spacing();
        ImGui.TextUnformatted("Frontier State");
        ImGui.TextWrapped(
            $"Mode: {plugin.DungeonFrontierService.CurrentMode}  |  " +
            $"Labels: {plugin.DungeonFrontierService.VisitedPoints} / {plugin.DungeonFrontierService.TotalPoints}  |  " +
            $"Map XZ: {plugin.DungeonFrontierService.VisitedManualMapXzDestinations} / {plugin.DungeonFrontierService.ManualMapXzDestinationCount}  |  " +
            $"XYZ: {plugin.DungeonFrontierService.VisitedManualXyzDestinations} / {plugin.DungeonFrontierService.ManualXyzDestinationCount}");
        if (plugin.DungeonFrontierService.CurrentTarget is { } frontierPoint)
        {
            var frontierTargetText = frontierPoint.IsManualXyzDestination
                ? $"Frontier target: {frontierPoint.Name} world {frontierPoint.Position.X:0.0}, {frontierPoint.Position.Y:0.0}, {frontierPoint.Position.Z:0.0}"
                : frontierPoint.MapCoordinates.HasValue
                    ? $"Frontier target: {frontierPoint.Name} map {frontierPoint.MapCoordinates.Value.X:0.0}, {frontierPoint.MapCoordinates.Value.Y:0.0}"
                    : $"Frontier target: {frontierPoint.Name}";
            ImGui.TextWrapped(frontierTargetText);
        }

        if (plugin.DungeonFrontierService.CurrentHeading is { } scoutHeading)
            ImGui.TextWrapped($"Frontier heading: {scoutHeading.X:0.00}, {scoutHeading.Z:0.00}");

        ImGui.Spacing();
        ImGui.TextUnformatted("Treasure Follow State");
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
        var postTransitSettle = plugin.ExecutionService.TreasureFollowerPostTransitSettleRemainingSeconds.ToString("0.0");
        ImGui.TextUnformatted($"Treasure role: {plugin.ExecutionService.TreasureDungeonRoleDisplayName} ({plugin.ExecutionService.TreasureDungeonRoleSource})");
        ImGui.SameLine();
        var openerLocal = opener is null ? "-" : opener.IsLocalOpener ? "local" : "remote";
        ImGui.TextUnformatted($"Portal opener: {opener?.Source ?? "None"} {opener?.OpenerName ?? string.Empty} {openerLocal} age {openerAge}s");
        ImGui.TextWrapped($"Interaction witness: {plugin.TreasurePortalOpenerTracker.LastInteractionWitnessSource} {plugin.TreasurePortalOpenerTracker.LastInteractionWitnessName} -> {plugin.TreasurePortalOpenerTracker.LastInteractionWitnessTarget} age {witnessAge}s | post-transit settle {postTransitSettle}s");
        ImGui.TextWrapped($"Relay: {plugin.TreasurePortalOpenerTracker.RelayStatus}");
        var commandAccepted = follow.BmraiFollowCommandAccepted is null
            ? "not sent"
            : follow.BmraiFollowCommandAccepted.Value ? "accepted" : "rejected";
        ImGui.TextWrapped($"BMRAI/VBM follow: {(follow.FollowApplied ? "applied" : "not applied")} method {follow.BmraiFollowCommandMethod} {commandAccepted} {follow.BmraiFollowCommandText}");
        ImGui.TextWrapped($"BMRAI/VBM reason: {follow.BmraiFollowCommandStatus}");
    }

    private void DrawActionRow()
    {
        var canStartInside = plugin.DutyContextService.Current.InInstancedDuty;
        if (ImGui.BeginTable("ADSPrimaryActions", 5, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            if (ImGui.Button("Start Outside", new Vector2(-1f, 32f)))
                plugin.StartDutyFromOutside();

            ImGui.TableSetColumnIndex(1);
            using (new ImGuiDisabledBlock(!canStartInside))
            {
                if (ImGui.Button("Start Inside", new Vector2(-1f, 32f)))
                    plugin.StartDutyFromInside();
            }

            ImGui.TableSetColumnIndex(2);
            using (new ImGuiDisabledBlock(!canStartInside))
            {
                if (ImGui.Button("Resume", new Vector2(-1f, 32f)))
                    plugin.ResumeDutyFromInside();
            }

            ImGui.TableSetColumnIndex(3);
            using (new ImGuiDisabledBlock(!plugin.ExecutionService.IsOwned))
            {
                if (ImGui.Button("Leave", new Vector2(-1f, 32f)))
                    plugin.LeaveDuty();
            }

            ImGui.TableSetColumnIndex(4);
            if (ImGui.Button("Stop", new Vector2(-1f, 32f)))
                plugin.StopOwnership();
            ImGui.EndTable();
        }
    }

    private void DrawDutyCatalog()
    {
        ImGui.TextUnformatted("Instanced Duty Catalog");
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint(
            "##ADSDutyCatalogSearch",
            "search duty, family, expansion, territory ID, or CFC ID",
            ref dutyCatalogSearch,
            160);
        DrawDutyCategoryFilters();
        DrawDutyCatalogStats();
        DrawRuleAtlas();

        var visibleEntries = plugin.DutyCatalogService.Entries
            .Where(entry => dutyCategoryFilters.GetValueOrDefault(entry.Category, true))
            .Where(DutyMatchesSearch)
            .ToList();
        var currentContext = plugin.DutyContextService.Current;
        var ruleCounts = BuildExplicitRuleCountsByDuty();
        var selectedEntry = visibleEntries.FirstOrDefault(entry => BuildDutyCatalogKey(entry) == selectedDutyCatalogKey);
        if (selectedEntry is null)
        {
            selectedEntry = visibleEntries.FirstOrDefault(entry => DutyMatchesCurrentContext(entry, currentContext))
                            ?? visibleEntries.FirstOrDefault();
            selectedDutyCatalogKey = selectedEntry is null ? null : BuildDutyCatalogKey(selectedEntry);
        }

        ImGui.TextDisabled($"Rows shown: {visibleEntries.Count} / {plugin.DutyCatalogService.Entries.Count}");
        if (visibleEntries.Count == 0)
        {
            ImGui.TextWrapped("No duties match the current search and family filters.");
            return;
        }

        var availableWidth = ImGui.GetContentRegionAvail().X;
        if (availableWidth >= 1120f)
        {
            if (!ImGui.BeginTable("ADSDutyDashboard", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
                return;

            ImGui.TableSetupColumn("Catalog", ImGuiTableColumnFlags.WidthStretch, 1.8f);
            ImGui.TableSetupColumn("Duty Details", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            DrawDutyCatalogTable(visibleEntries, currentContext, ruleCounts, 420f);
            ImGui.TableSetColumnIndex(1);
            DrawDutyDetail(selectedEntry!, ruleCounts.GetValueOrDefault(BuildDutyCatalogKey(selectedEntry!)), 420f);
            ImGui.EndTable();
            return;
        }

        DrawDutyCatalogTable(visibleEntries, currentContext, ruleCounts, 300f);
        ImGui.Spacing();
        DrawDutyDetail(selectedEntry!, ruleCounts.GetValueOrDefault(BuildDutyCatalogKey(selectedEntry!)), 260f);
    }

    private void DrawDutyCatalogTable(
        IReadOnlyList<DutyCatalogEntry> entries,
        DutyContextSnapshot currentContext,
        IReadOnlyDictionary<string, int> ruleCounts,
        float height)
    {
        if (!ImGui.BeginTable(
                "AdsDutyCatalog",
                5,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
                new Vector2(-1f, height)))
        {
            return;
        }

        ImGui.TableSetupColumn("Duty");
        ImGui.TableSetupColumn("Family", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("Maturity", ImGuiTableColumnFlags.WidthFixed, 175f);
        ImGui.TableSetupColumn("Readiness", ImGuiTableColumnFlags.WidthFixed, 130f);
        ImGui.TableSetupColumn("Rules", ImGuiTableColumnFlags.WidthFixed, 55f);
        ImGui.TableHeadersRow();

        foreach (var entry in entries)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);

            var highlight = GetClearanceColor(entry.ClearanceStatus);
            if (DutyMatchesCurrentContext(entry, currentContext))
                highlight = new Vector4(0.97f, 0.84f, 0.31f, 1f);

            ImGui.PushStyleColor(ImGuiCol.Text, highlight);
            var catalogKey = BuildDutyCatalogKey(entry);
            if (ImGui.Selectable($"{entry.EnglishName}##DutyCatalog{catalogKey}", catalogKey == selectedDutyCatalogKey))
                selectedDutyCatalogKey = catalogKey;
            ImGui.PopStyleColor();

            ImGui.TableSetColumnIndex(1);
            var family = DutyCategoryDisplayCatalog.Get(entry.Category);
            ImGui.PushStyleColor(ImGuiCol.Text, family.Accent);
            ImGui.TextUnformatted(family.FilterLabel);
            ImGui.PopStyleColor();

            ImGui.TableSetColumnIndex(2);
            ImGui.PushStyleColor(ImGuiCol.Text, GetClearanceColor(entry.ClearanceStatus));
            ImGui.TextUnformatted(GetClearanceLabel(entry.ClearanceStatus));
            ImGui.PopStyleColor();

            ImGui.TableSetColumnIndex(3);
            ImGui.PushStyleColor(ImGuiCol.Text, GetSupportLevelColor(entry.SupportLevel));
            ImGui.TextUnformatted(GetSupportLevelLabel(entry.SupportLevel));
            ImGui.PopStyleColor();

            ImGui.TableSetColumnIndex(4);
            var ruleCount = ruleCounts.GetValueOrDefault(catalogKey);
            if (entry.ClearanceStatus != DutyClearanceStatus.NotCleared && ruleCount == 0)
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.43f, 0.35f, 1f));
            else if (entry.ClearanceStatus != DutyClearanceStatus.NotCleared && ruleCount > 10)
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.86f, 0.24f, 1f));

            ImGui.TextUnformatted(ruleCount.ToString());
            if (entry.ClearanceStatus != DutyClearanceStatus.NotCleared && (ruleCount == 0 || ruleCount > 10))
                ImGui.PopStyleColor();
        }

        ImGui.EndTable();
    }

    private static void DrawDutyDetail(DutyCatalogEntry entry, int ruleCount, float height)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f);
        if (ImGui.BeginChild("ADSDutyDetail", new Vector2(-1f, height), true))
        {
            ImGui.TextColored(GetClearanceColor(entry.ClearanceStatus), entry.EnglishName);
            ImGui.TextColored(DutyCategoryDisplayCatalog.Get(entry.Category).Accent, DutyCategoryDisplayCatalog.Get(entry.Category).FilterLabel);
            ImGui.Spacing();

            if (ImGui.BeginTable("ADSDutyDetailFacts", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
            {
                ImGui.TableNextRow();
                DrawDutyDetailFact(0, "Level", entry.LevelRequired.ToString());
                DrawDutyDetailFact(1, "Expansion", entry.ExpansionName);
                ImGui.TableNextRow();
                DrawDutyDetailFact(0, "Party Size", entry.PartySize.ToString());
                DrawDutyDetailFact(1, "Content Type", entry.ContentTypeName);
                ImGui.TableNextRow();
                DrawDutyDetailFact(0, "Territory ID", entry.TerritoryTypeId.ToString());
                DrawDutyDetailFact(1, "CFC ID", entry.ContentFinderConditionId == 0 ? "-" : entry.ContentFinderConditionId.ToString());
                ImGui.TableNextRow();
                DrawDutyDetailFact(0, "Maturity", GetClearanceLabel(entry.ClearanceStatus));
                DrawDutyDetailFact(1, "Readiness", GetSupportLevelLabel(entry.SupportLevel));
                ImGui.TableNextRow();
                DrawDutyDetailFact(0, "Planned Test", entry.IsPlannedTest ? "YES" : "NO");
                DrawDutyDetailFact(1, "Explicit Rules", ruleCount.ToString());
                ImGui.EndTable();
            }

            ImGui.Spacing();
            ImGui.TextDisabled("SUPPORT NOTE");
            ImGui.TextWrapped(entry.SupportNote);
        }

        ImGui.EndChild();
        ImGui.PopStyleVar();
    }

    private static void DrawDutyDetailFact(int column, string label, string value)
    {
        ImGui.TableSetColumnIndex(column);
        ImGui.TextDisabled(label.ToUpperInvariant());
        ImGui.TextWrapped(value);
    }

    private void DrawDutyCategoryFilters()
    {
        ImGui.TextUnformatted("Families");
        if (ImGui.SmallButton("All"))
        {
            foreach (var entry in DutyCategoryDisplayCatalog.Entries)
                dutyCategoryFilters[entry.Category] = true;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("None"))
        {
            foreach (var entry in DutyCategoryDisplayCatalog.Entries)
                dutyCategoryFilters[entry.Category] = false;
        }

        var availableWidth = ImGui.GetContentRegionAvail().X;
        var columnCount = availableWidth >= 1040f ? 4 : availableWidth >= 620f ? 2 : 1;
        if (!ImGui.BeginTable("ADSDutyFamilyFilters", columnCount, ImGuiTableFlags.SizingStretchSame))
            return;

        for (var index = 0; index < DutyCategoryDisplayCatalog.Entries.Count; index++)
        {
            if (index % columnCount == 0)
                ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(index % columnCount);
            var entry = DutyCategoryDisplayCatalog.Entries[index];
            var enabled = dutyCategoryFilters.GetValueOrDefault(entry.Category, true);
            ImGui.PushStyleColor(ImGuiCol.Text, entry.Accent);
            if (ImGui.Checkbox($"{entry.FilterLabel}##DutyFamily{entry.Category}", ref enabled))
                dutyCategoryFilters[entry.Category] = enabled;
            ImGui.PopStyleColor();
        }

        ImGui.EndTable();
    }

    private void DrawDutyCatalogStats()
    {
        var entries = plugin.DutyCatalogService.Entries;
        var cards = new[]
        {
            (MaturityLevel: 0, Label: "Not Cleared", Count: CountStatus(DutyClearanceStatus.NotCleared), Accent: GetClearanceColor(DutyClearanceStatus.NotCleared)),
            (MaturityLevel: 1, Label: "1P Unsync Cleared", Count: CountStatus(DutyClearanceStatus.OnePlayerUnsyncCleared), Accent: GetClearanceColor(DutyClearanceStatus.OnePlayerUnsyncCleared)),
            (MaturityLevel: 2, Label: "1P Duty Support", Count: CountStatus(DutyClearanceStatus.OnePlayerDutySupport), Accent: GetClearanceColor(DutyClearanceStatus.OnePlayerDutySupport)),
            (MaturityLevel: 3, Label: "Synced Party Cleared", Count: CountStatus(DutyClearanceStatus.FourPlayerSyncCleared), Accent: GetClearanceColor(DutyClearanceStatus.FourPlayerSyncCleared)),
        };
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var columnCount = availableWidth >= 880f ? 4 : 2;
        if (ImGui.BeginTable("AdsDutyCatalogStats", columnCount, ImGuiTableFlags.SizingStretchSame))
        {
            for (var index = 0; index < cards.Length; index++)
            {
                if (index % columnCount == 0)
                    ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(index % columnCount);
                DrawDutyCatalogStatCard(cards[index].MaturityLevel, cards[index].Label, cards[index].Count, cards[index].Accent);
            }

            ImGui.EndTable();
        }

        var categorySummary = string.Join(
            "  |  ",
            DutyCategoryDisplayCatalog.Entries.Select(x => $"{x.FilterLabel} {entries.Count(y => y.Category == x.Category)}"));
        var plannedTests = entries.Count(x => x.IsPlannedTest);
        ImGui.TextDisabled(categorySummary);
        ImGui.TextDisabled($"{plannedTests} planned test{(plannedTests == 1 ? string.Empty : "s")} flagged in the catalog.");
        ImGui.TextDisabled(plugin.DutyCatalogService.LastMaturityLoadStatus);

        int CountStatus(DutyClearanceStatus status)
            => entries.Count(x => x.ClearanceStatus == status);
    }

    private void DrawRuleAtlas()
    {
        if (!ImGui.CollapsingHeader("Rule Atlas / Coverage"))
            return;

        var rules = plugin.ObjectPriorityRuleService.Current.Rules;
        var totalRules = rules.Count;
        var enabledRules = rules.Count(x => x.Enabled);
        var globalRules = rules.Count(IsGlobalRule);
        var explicitRuleCounts = BuildExplicitRuleCountsByDuty();
        var maturedEntries = plugin.DutyCatalogService.Entries.Where(x => x.ClearanceStatus != DutyClearanceStatus.NotCleared).ToList();
        var dutiesWithNoRules = maturedEntries.Count(x => explicitRuleCounts.GetValueOrDefault(BuildDutyCatalogKey(x)) == 0);
        var dutiesWithDenseRules = maturedEntries.Count(x => explicitRuleCounts.GetValueOrDefault(BuildDutyCatalogKey(x)) > 10);
        var classificationSummary = string.Join(
            "  |  ",
            rules.GroupBy(GetRuleCategoryLabel)
                .OrderByDescending(x => x.Count())
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => $"{x.Key} {x.Count()}"));

        ImGui.TextWrapped($"Rules: {totalRules} total | {enabledRules} enabled | {globalRules} global");
        ImGui.TextWrapped($"Coverage signals: {dutiesWithNoRules} matured duties with 0 rules | {dutiesWithDenseRules} matured duties with >10 rules");
        ImGui.TextWrapped($"By category: {(string.IsNullOrWhiteSpace(classificationSummary) ? "No rules authored yet." : classificationSummary)}");
    }

    private Dictionary<string, int> BuildExplicitRuleCountsByDuty()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in plugin.DutyCatalogService.Entries)
            counts[BuildDutyCatalogKey(entry)] = CountExplicitRulesForDuty(entry);

        return counts;
    }

    private int CountExplicitRulesForDuty(DutyCatalogEntry entry)
    {
        var context = new DutyContextSnapshot
        {
            PluginEnabled = true,
            IsLoggedIn = true,
            BoundByDuty = true,
            BoundByDuty56 = false,
            BetweenAreas = false,
            BetweenAreas51 = false,
            Jumping = false,
            Jumping61 = false,
            Occupied33 = false,
            OccupiedInCutSceneEvent = false,
            WatchingCutscene = false,
            InCombat = false,
            Mounted = false,
            TerritoryTypeId = entry.TerritoryTypeId,
            MapId = 0,
            ContentFinderConditionId = entry.ContentFinderConditionId,
            CurrentDuty = entry,
        };

        return plugin.ObjectPriorityRuleService.Current.Rules.Count(rule =>
            IsExplicitDutyRule(rule)
            && plugin.ObjectPriorityRuleService.MatchesCurrentDutyScopeForEditor(rule, context));
    }

    private static bool IsGlobalRule(ObjectPriorityRule rule)
        => !IsExplicitDutyRule(rule);

    private static bool IsExplicitDutyRule(ObjectPriorityRule rule)
        => rule.ContentFinderConditionId != 0
           || rule.TerritoryTypeId != 0
           || !string.IsNullOrWhiteSpace(rule.DutyEnglishName);

    private static string GetRuleCategoryLabel(ObjectPriorityRule rule)
        => string.IsNullOrWhiteSpace(rule.Classification) ? "(none)" : rule.Classification;

    private static void DrawDutyCatalogStatCard(int maturityLevel, string label, int count, Vector4 accent)
    {
        var background = new Vector4(
            MathF.Min(accent.X * 0.18f, 1f),
            MathF.Min(accent.Y * 0.18f, 1f),
            MathF.Min(accent.Z * 0.18f, 1f),
            0.32f);

        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, background);
        ImGui.PushStyleColor(ImGuiCol.Border, accent);
        if (ImGui.BeginChild($"##DutyCatalogStatCard{maturityLevel}", new Vector2(-1f, 76f), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.TextColored(accent, $"Maturity {maturityLevel}");
            ImGui.TextUnformatted($"{count} {(count == 1 ? "duty" : "duties")}");
            ImGui.TextWrapped(label);
        }

        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar();
    }

    private void DrawObservationSummary()
    {
        var observation = plugin.ObservationMemoryService.Current;
        ImGui.TextUnformatted("Observation Summary");
        ImGui.TextUnformatted($"Live monsters: {observation.LiveMonsters.Count}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Live follow: {observation.LiveFollowTargets.Count}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Monster ghosts: {observation.MonsterGhosts.Count}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Live interactables: {observation.LiveInteractables.Count}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Interactable ghosts: {observation.InteractableGhosts.Count}");
        ImGui.TextWrapped($"Execution phase summary: {plugin.ExecutionService.CurrentPhase} | {plugin.ExecutionService.LastStatus}");

        if (!plugin.Configuration.ShowDebugSections)
            return;

        ImGui.Spacing();
        ImGui.TextUnformatted("Debug Preview");
        DrawNameList("Live monster sample", observation.LiveMonsters.Select(x => x.Name));
        DrawNameList("Live follow sample", observation.LiveFollowTargets.Select(x => x.Name));
        DrawNameList("Monster ghost sample", observation.MonsterGhosts.Select(x => x.Name));
        DrawNameList("Live interactable sample", observation.LiveInteractables.Select(x => $"{x.Name} [{x.Classification}]"));
        DrawNameList("Interactable ghost sample", observation.InteractableGhosts.Select(x => $"{x.Name} [{x.Classification}]"));
    }

    private void DrawJsonButtons()
    {
        if (ImGui.SmallButton("Copy Status JSON"))
            ImGui.SetClipboardText(plugin.GetStatusJson());
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy Analysis JSON"))
            ImGui.SetClipboardText(plugin.GetCurrentAnalysisJson());

        if (!plugin.Configuration.ShowDebugSections)
            return;

        if (ImGui.CollapsingHeader("Live JSON Preview"))
            ImGui.TextWrapped(FormatJson(plugin.GetCurrentAnalysisJson()));
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
            return context.CurrentDuty.EnglishName;

        return context.InInstancedDuty
            ? $"territory {context.TerritoryTypeId}"
            : "None";
    }

    private static string BuildDutyCatalogKey(DutyCatalogEntry entry)
        => entry.ContentFinderConditionId != 0
            ? $"cfc:{entry.ContentFinderConditionId}"
            : $"terr:{entry.TerritoryTypeId}";

    private bool DutyMatchesSearch(DutyCatalogEntry entry)
    {
        var search = dutyCatalogSearch.Trim();
        if (search.Length == 0)
            return true;

        var family = DutyCategoryDisplayCatalog.Get(entry.Category).FilterLabel;
        return entry.EnglishName.Contains(search, StringComparison.OrdinalIgnoreCase)
               || entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
               || family.Contains(search, StringComparison.OrdinalIgnoreCase)
               || entry.ExpansionName.Contains(search, StringComparison.OrdinalIgnoreCase)
               || entry.TerritoryTypeId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)
               || entry.ContentFinderConditionId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private static bool DutyMatchesCurrentContext(DutyCatalogEntry entry, DutyContextSnapshot context)
    {
        if (entry.ContentFinderConditionId != 0 && entry.ContentFinderConditionId == context.ContentFinderConditionId)
            return true;

        return entry.ContentFinderConditionId == 0
               && entry.TerritoryTypeId != 0
               && entry.TerritoryTypeId == context.TerritoryTypeId;
    }

    private static string GetClearanceLabel(DutyClearanceStatus status)
        => status switch
        {
            DutyClearanceStatus.OnePlayerUnsyncCleared => "[1P Unsync Cleared]",
            DutyClearanceStatus.OnePlayerDutySupport => "[1P Duty Support]",
            DutyClearanceStatus.FourPlayerSyncCleared => "[Synced Party Cleared]",
            _ => "[Not Cleared]",
        };

    private static Vector4 GetClearanceColor(DutyClearanceStatus status)
        => status switch
        {
            DutyClearanceStatus.OnePlayerUnsyncCleared => new Vector4(0.35f, 0.62f, 1.0f, 1f),
            DutyClearanceStatus.OnePlayerDutySupport => new Vector4(1.0f, 0.86f, 0.24f, 1f),
            DutyClearanceStatus.FourPlayerSyncCleared => new Vector4(0.42f, 0.94f, 0.64f, 1f),
            _ => new Vector4(1.0f, 0.36f, 0.32f, 1f),
        };

    private static string GetSupportLevelLabel(DutySupportLevel supportLevel)
        => supportLevel switch
        {
            DutySupportLevel.ActiveSupported => "Pilot active",
            DutySupportLevel.PassiveOnly => "Catalog test lane",
            _ => "Metadata only",
        };

    private static Vector4 GetSupportLevelColor(DutySupportLevel supportLevel)
        => supportLevel switch
        {
            DutySupportLevel.ActiveSupported => new Vector4(0.42f, 0.94f, 0.64f, 1f),
            DutySupportLevel.PassiveOnly => new Vector4(1.0f, 0.86f, 0.24f, 1f),
            _ => new Vector4(0.86f, 0.86f, 0.86f, 1f),
        };

    private static void DrawNameList(string label, IEnumerable<string> names)
    {
        var value = string.Join(", ", names.Take(3));
        if (string.IsNullOrWhiteSpace(value))
            value = "none";
        ImGui.TextWrapped($"{label}: {value}");
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
