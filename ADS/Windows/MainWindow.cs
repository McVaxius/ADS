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
        var planner = plugin.ObjectivePlannerService.Current;
        if (!ImGui.BeginTable("ADSPrimaryState", 4, ImGuiTableFlags.SizingStretchSame))
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
        ImGui.TableSetColumnIndex(3);
        ImGui.TextDisabled("OBJECT NAME");
        ImGui.TextWrapped(string.IsNullOrWhiteSpace(planner.TargetName) ? "None" : planner.TargetName);
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
            DrawOverviewCell(2, "Catalog", currentDuty is not null ? "MATCHED" : "NO ROW");
            ImGui.TableNextRow();
            DrawOverviewCell(0, "Maturity", currentDuty is not null ? DutyMaturityDisplayCatalog.GetClearanceLabel(currentDuty.ClearanceStatus) : "No catalog row");
            DrawOverviewCell(1, "Instanced / Catalog", $"{(context.InInstancedDuty ? "YES" : "NO")} / {(context.HasCatalogMetadata ? "YES" : "NO")}");
            DrawOverviewCell(2, "MSQ", currentDuty is not null && currentDuty.IsMainScenario ? "YES" : "NO");
            ImGui.TableNextRow();
            DrawOverviewCell(0, "Unsafe Transition", context.IsUnsafeTransition ? "YES" : "NO");
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
            ImGui.TextWrapped("This instanced duty has no ADS catalog row yet. Runtime still keys off live instanced-duty truth, but family/maturity metadata is uncatalogued.");
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
            ("Reflection", plugin.ToggleReflectionUi),
            ("Shop Lists", plugin.OpenShopListsUi),
            ("Desynth Controls", plugin.OpenDesynthConfigUi),
            ("Extract Materia", () => plugin.StartExtractMateria()));

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
        var inInstancedDuty = plugin.DutyContextService.Current.InInstancedDuty;
        if (ImGui.BeginTable("ADSPrimaryActions", 6, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            if (ImGui.Button("Start Outside", new Vector2(-1f, 32f)))
                plugin.StartDutyFromOutside();

            ImGui.TableSetColumnIndex(1);
            using (new ImGuiDisabledBlock(!inInstancedDuty))
            {
                if (ImGui.Button("Start Inside", new Vector2(-1f, 32f)))
                    plugin.StartDutyFromInside();
            }

            ImGui.TableSetColumnIndex(2);
            using (new ImGuiDisabledBlock(!inInstancedDuty))
            {
                if (ImGui.Button("Resume", new Vector2(-1f, 32f)))
                    plugin.ResumeDutyFromInside();
            }

            ImGui.TableSetColumnIndex(3);
            using (new ImGuiDisabledBlock(!inInstancedDuty))
            {
                if (ImGui.Button("Leave", new Vector2(-1f, 32f)))
                    plugin.LeaveDuty();
            }

            ImGui.TableSetColumnIndex(4);
            if (ImGui.Button("Stop", new Vector2(-1f, 32f)))
                plugin.StopOwnership();

            ImGui.TableSetColumnIndex(5);
            if (ImGui.Button("Guided Setup", new Vector2(-1f, 32f)))
                plugin.OpenWizardUi();
            ImGui.EndTable();
        }
    }

    private void DrawDutyCatalog()
    {
        var context = plugin.DutyContextService.Current;
        var currentDuty = context.CurrentDuty;
        var snapshot = DutyRuleCoverageHelper.BuildSnapshot(
            plugin.DutyCatalogService.Entries,
            plugin.ObjectPriorityRuleService.Current.Rules);
        var coverage = currentDuty is null ? default : snapshot.Get(currentDuty);

        ImGui.TextUnformatted("Duty Coverage");
        if (ImGui.BeginTable("ADSDutyCoverageSummary", 3, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableNextRow();
            DrawOverviewCell(0, "Current Duty", GetCurrentDutyLabel(context));
            DrawOverviewCell(1, "Maturity", currentDuty is null ? "-" : DutyMaturityDisplayCatalog.GetClearanceLabel(currentDuty.ClearanceStatus));
            DrawOverviewCell(2, "Family", currentDuty is null ? "-" : DutyCategoryDisplayCatalog.Get(currentDuty.Category).FilterLabel);
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
        ImGui.TextWrapped("Open the full manager for catalog work, jump directly to duties without authored rules, or inspect every rule diagnostically associated with the current duty.");
        if (ImGui.Button("Duty Manager"))
            plugin.OpenDutyMaturityEditorUi();
        ImGui.SameLine();
        if (ImGui.Button("Missing-duty Work"))
            plugin.OpenMissingDutyWorkUi();
        ImGui.SameLine();
        using (new ImGuiDisabledBlock(currentDuty is null))
        {
            if (ImGui.Button("Current-duty Rules") && currentDuty is not null)
                plugin.OpenRuleEditorUi(currentDuty);
        }
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
