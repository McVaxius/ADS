using System.Numerics;
using System.Text.Json;
using ADS.Models;
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

        DrawTopRow();
        ImGui.Spacing();
        DrawActionRow();
        ImGui.Spacing();
        DrawJsonButtons();
        ImGui.Separator();
        DrawCurrentState();
        ImGui.Spacing();
        DrawDutyCatalog();
        ImGui.Spacing();
        DrawObservationSummary();
    }

    private void DrawTopRow()
    {
        ImGui.TextUnformatted($"{PluginInfo.DisplayName} v{PluginInfo.GetVersion()}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Ko-fi"))
            plugin.OpenUrl(PluginInfo.SupportUrl);
        ImGui.SameLine();
        if (ImGui.SmallButton("Discord"))
            plugin.OpenUrl(PluginInfo.DiscordUrl);
        ImGui.SameLine();
        if (ImGui.SmallButton("Repo"))
            plugin.OpenUrl(PluginInfo.RepoUrl);
        ImGui.SameLine();
        if (ImGui.SmallButton("Settings"))
            plugin.OpenConfigUi();
        ImGui.SameLine();
        if (ImGui.SmallButton("Objects"))
            plugin.ToggleObjectExplorerUi();
        ImGui.SameLine();
        if (ImGui.SmallButton("Labels"))
            plugin.ToggleFrontierLabelUi();
        ImGui.SameLine();
        if (ImGui.SmallButton("Rules"))
            plugin.ToggleRuleEditorUi();
        ImGui.SameLine();
        if (ImGui.SmallButton("Dialogs"))
            plugin.ToggleDialogRuleEditorUi();

        ImGui.TextWrapped(PluginInfo.Summary);
        ImGui.TextWrapped(PluginInfo.PilotDutySummary);
    }

    private void DrawCurrentState()
    {
        var context = plugin.DutyContextService.Current;
        var planner = plugin.ObjectivePlannerService.Current;
        var execution = plugin.ExecutionService;

        ImGui.TextUnformatted($"Ownership: {execution.CurrentMode}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Exec phase: {execution.CurrentPhase}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Planner: {planner.Mode}");

        ImGui.TextUnformatted($"Duty: {context.CurrentDuty?.EnglishName ?? "None"}");
        ImGui.TextUnformatted($"Active execution eligible: {(context.AllowsActiveExecution ? "YES" : "NO")}");
        ImGui.TextUnformatted($"Treasure coffers: {(plugin.Configuration.ConsiderTreasureCoffers ? "ON" : "OFF")}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Object rules: {plugin.ObjectPriorityRuleService.ActiveRuleCount}");
        ImGui.TextUnformatted($"Territory / CFC: {context.TerritoryTypeId} / {context.ContentFinderConditionId}");
        ImGui.TextUnformatted($"Objective kind: {planner.ObjectiveKind}");
        ImGui.TextUnformatted($"Frontier mode: {plugin.DungeonFrontierService.CurrentMode}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Labels: {plugin.DungeonFrontierService.VisitedPoints} / {plugin.DungeonFrontierService.TotalPoints}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Map XZ: {plugin.DungeonFrontierService.VisitedManualMapXzDestinations} / {plugin.DungeonFrontierService.ManualMapXzDestinationCount}");
        if (plugin.DungeonFrontierService.CurrentTarget is { } frontierPoint)
            ImGui.TextWrapped(frontierPoint.MapCoordinates.HasValue
                ? $"Frontier target: {frontierPoint.Name} map {frontierPoint.MapCoordinates.Value.X:0.0}, {frontierPoint.MapCoordinates.Value.Y:0.0}"
                : $"Frontier target: {frontierPoint.Name}");
        if (plugin.DungeonFrontierService.CurrentHeading is { } scoutHeading)
            ImGui.TextWrapped($"Frontier heading: {scoutHeading.X:0.00}, {scoutHeading.Z:0.00}");
        ImGui.TextWrapped($"Objective: {planner.Objective}");
        ImGui.TextWrapped($"Explanation: {planner.Explanation}");
        if (planner.TargetDistance.HasValue || planner.TargetVerticalDelta.HasValue)
            ImGui.TextWrapped($"Target distance / vertical: {planner.TargetDistance?.ToString("0.0") ?? "-"} / {planner.TargetVerticalDelta?.ToString("0.0") ?? "-"}");
        ImGui.TextWrapped($"Execution phase summary: {execution.LastStatus}");

        if (context.InDuty && context.IsSupportedDuty && !execution.IsOwned)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.98f, 0.82f, 0.34f, 1f));
            ImGui.TextUnformatted("Observing only");
            ImGui.PopStyleColor();
        }
    }

    private void DrawActionRow()
    {
        if (ImGui.Button("Start Outside"))
            plugin.StartDutyFromOutside();
        ImGui.SameLine();

        var canStartInside = plugin.DutyContextService.Current.InDuty && plugin.DutyContextService.Current.IsSupportedDuty;
        using (new ImGuiDisabledBlock(!canStartInside))
        {
            if (ImGui.Button("Start Inside"))
                plugin.StartDutyFromInside();
        }

        ImGui.SameLine();
        using (new ImGuiDisabledBlock(!canStartInside))
        {
            if (ImGui.Button("Resume"))
                plugin.ResumeDutyFromInside();
        }

        ImGui.SameLine();
        using (new ImGuiDisabledBlock(!plugin.ExecutionService.IsOwned))
        {
            if (ImGui.Button("Leave"))
                plugin.LeaveDuty();
        }

        ImGui.SameLine();
        if (ImGui.Button("Stop"))
            plugin.StopOwnership();

        if (plugin.DutyContextService.Current.InDuty
            && plugin.DutyContextService.Current.IsSupportedDuty
            && !plugin.DutyContextService.Current.AllowsActiveExecution)
        {
            ImGui.TextWrapped("Pilot flag is still NO for this duty, but Start/Resume are allowed so you can exercise the ADS observer/FSM test surface.");
        }
    }

    private void DrawDutyCatalog()
    {
        ImGui.TextUnformatted("All 4-Man Dungeons");
        DrawDutyCatalogStats();
        if (!ImGui.BeginTable("AdsDutyCatalog", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(-1f, 320f)))
            return;

        ImGui.TableSetupColumn("Duty");
        ImGui.TableSetupColumn("Territory", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("Clearance", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("Expansion", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Passive", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Active", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Note");
        ImGui.TableHeadersRow();

        var currentDuty = plugin.DutyContextService.Current.CurrentDuty;
        foreach (var entry in plugin.DutyCatalogService.Entries)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);

            var highlight = GetClearanceColor(entry.ClearanceStatus);
            if (currentDuty?.ContentFinderConditionId == entry.ContentFinderConditionId)
                highlight = new Vector4(0.97f, 0.84f, 0.31f, 1f);

            ImGui.PushStyleColor(ImGuiCol.Text, highlight);
            ImGui.TextUnformatted(entry.EnglishName);
            ImGui.PopStyleColor();

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(entry.TerritoryTypeId.ToString());

            ImGui.TableSetColumnIndex(2);
            ImGui.PushStyleColor(ImGuiCol.Text, GetClearanceColor(entry.ClearanceStatus));
            ImGui.TextUnformatted(GetClearanceLabel(entry.ClearanceStatus));
            ImGui.PopStyleColor();

            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(entry.LevelRequired.ToString());

            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(entry.ExpansionName);

            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted(entry.SupportsPassiveObservation ? "YES" : "NO");

            ImGui.TableSetColumnIndex(6);
            ImGui.TextUnformatted(entry.SupportsActiveExecution ? "YES" : "NO");

            ImGui.TableSetColumnIndex(7);
            ImGui.TextWrapped(entry.SupportNote);
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
            (MaturityLevel: 3, Label: "4P Sync Cleared", Count: CountStatus(DutyClearanceStatus.FourPlayerSyncCleared), Accent: GetClearanceColor(DutyClearanceStatus.FourPlayerSyncCleared)),
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

        var plannedTests = entries.Count(x => x.IsPlannedTest);
        ImGui.TextDisabled($"{plannedTests} planned test{(plannedTests == 1 ? string.Empty : "s")} flagged in the catalog.");

        int CountStatus(DutyClearanceStatus status)
            => entries.Count(x => x.ClearanceStatus == status);
    }

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

    private static string GetClearanceLabel(DutyClearanceStatus status)
        => status switch
        {
            DutyClearanceStatus.OnePlayerUnsyncCleared => "[1P Unsync Cleared]",
            DutyClearanceStatus.OnePlayerDutySupport => "[1P Duty Support]",
            DutyClearanceStatus.FourPlayerSyncCleared => "[4P Sync Cleared]",
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
