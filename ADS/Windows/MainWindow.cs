using System.Numerics;
using System.Text.Json;
using ADS.Models;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class MainWindow : PositionedWindow, IDisposable
{
    private readonly Plugin plugin;
    private readonly Dictionary<DutyCategory, bool> dutyCategoryFilters = DutyCategoryDisplayCatalog.Entries
        .ToDictionary(x => x.Category, _ => true);

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
        if (ImGui.SmallButton("Ghosts"))
            plugin.ToggleGhostListUi();
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
        var currentDuty = context.CurrentDuty;
        var dutyDisplay = currentDuty is not null
            ? DutyCategoryDisplayCatalog.Get(currentDuty.Category)
            : null;
        var activeLayer = plugin.ObjectPriorityRuleService.GetActiveLayerName(context) ?? "Unknown";

        ImGui.TextUnformatted($"Ownership: {execution.CurrentMode}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Exec phase: {execution.CurrentPhase}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Planner: {planner.Mode}");

        ImGui.TextUnformatted($"Duty: {GetCurrentDutyLabel(context)}");
        ImGui.TextUnformatted($"Instanced duty: {(context.InInstancedDuty ? "YES" : "NO")}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Catalog metadata: {(context.HasCatalogMetadata ? "YES" : "NO")}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Unsafe transition: {(context.IsUnsafeTransition ? "YES" : "NO")}");

        ImGui.TextUnformatted($"Family: {dutyDisplay?.FilterLabel ?? "Uncatalogued"}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Readiness: {(currentDuty is not null ? GetSupportLevelLabel(currentDuty.SupportLevel) : "No catalog row")}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Maturity: {(currentDuty is not null ? GetClearanceLabel(currentDuty.ClearanceStatus) : "No catalog row")}");

        ImGui.TextUnformatted($"Treasure coffers: {(plugin.Configuration.ConsiderTreasureCoffers ? "ON" : "OFF")}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Object rules: {plugin.ObjectPriorityRuleService.ActiveRuleCount}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Layer: {activeLayer}");
        ImGui.TextUnformatted($"Territory / Map / CFC: {context.TerritoryTypeId} / {context.MapId} / {context.ContentFinderConditionId}");
        ImGui.TextUnformatted($"Objective kind: {planner.ObjectiveKind}");
        ImGui.TextUnformatted($"Frontier mode: {plugin.DungeonFrontierService.CurrentMode}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Labels: {plugin.DungeonFrontierService.VisitedPoints} / {plugin.DungeonFrontierService.TotalPoints}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Map XZ: {plugin.DungeonFrontierService.VisitedManualMapXzDestinations} / {plugin.DungeonFrontierService.ManualMapXzDestinationCount}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"XYZ: {plugin.DungeonFrontierService.VisitedManualXyzDestinations} / {plugin.DungeonFrontierService.ManualXyzDestinationCount}");
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
        ImGui.TextWrapped($"Objective: {planner.Objective}");
        ImGui.TextWrapped($"Explanation: {planner.Explanation}");
        if (planner.TargetDistance.HasValue || planner.TargetVerticalDelta.HasValue)
            ImGui.TextWrapped($"Target distance / vertical: {planner.TargetDistance?.ToString("0.0") ?? "-"} / {planner.TargetVerticalDelta?.ToString("0.0") ?? "-"}");
        ImGui.TextWrapped($"Execution phase summary: {execution.LastStatus}");

        if (context.InInstancedDuty && !execution.IsOwned)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.98f, 0.82f, 0.34f, 1f));
            ImGui.TextUnformatted("Observing only");
            ImGui.PopStyleColor();
        }

        if (context.InInstancedDuty && !context.HasCatalogMetadata)
            ImGui.TextWrapped("This instanced duty has no ADS catalog row yet. Runtime still keys off live instanced-duty truth, but family/readiness metadata is uncatalogued.");
    }

    private void DrawActionRow()
    {
        if (ImGui.Button("Start Outside"))
            plugin.StartDutyFromOutside();
        ImGui.SameLine();

        var canStartInside = plugin.DutyContextService.Current.InInstancedDuty;
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

        var context = plugin.DutyContextService.Current;
        if (context.InInstancedDuty && !context.HasCatalogMetadata)
            ImGui.TextWrapped("Start/Resume stay enabled even without catalog metadata. ADS now trusts instanced-duty truth and treats catalog rows as maturity metadata only.");
    }

    private void DrawDutyCatalog()
    {
        ImGui.TextUnformatted("Instanced Duty Catalog");
        DrawDutyCategoryFilters();
        DrawRuleAtlas();
        DrawDutyCatalogStats();

        var visibleEntries = plugin.DutyCatalogService.Entries
            .Where(entry => dutyCategoryFilters.GetValueOrDefault(entry.Category, true))
            .ToList();
        var currentContext = plugin.DutyContextService.Current;
        var ruleCounts = BuildExplicitRuleCountsByDuty();

        ImGui.TextDisabled($"Rows shown: {visibleEntries.Count} / {plugin.DutyCatalogService.Entries.Count}");
        if (!ImGui.BeginTable("AdsDutyCatalog", 10, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(-1f, 320f)))
            return;

        ImGui.TableSetupColumn("Duty");
        ImGui.TableSetupColumn("Family", ImGuiTableColumnFlags.WidthFixed, 110f);
        ImGui.TableSetupColumn("Rules", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Terr", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("CFC", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Maturity", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableSetupColumn("Readiness", ImGuiTableColumnFlags.WidthFixed, 130f);
        ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("Expansion", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Note");
        ImGui.TableHeadersRow();

        foreach (var entry in visibleEntries)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);

            var highlight = GetClearanceColor(entry.ClearanceStatus);
            if (DutyMatchesCurrentContext(entry, currentContext))
                highlight = new Vector4(0.97f, 0.84f, 0.31f, 1f);

            ImGui.PushStyleColor(ImGuiCol.Text, highlight);
            ImGui.TextUnformatted(entry.EnglishName);
            ImGui.PopStyleColor();

            ImGui.TableSetColumnIndex(1);
            var family = DutyCategoryDisplayCatalog.Get(entry.Category);
            ImGui.PushStyleColor(ImGuiCol.Text, family.Accent);
            ImGui.TextUnformatted(family.FilterLabel);
            ImGui.PopStyleColor();

            ImGui.TableSetColumnIndex(2);
            var ruleCount = ruleCounts.GetValueOrDefault(BuildDutyCatalogKey(entry));
            if (entry.ClearanceStatus != DutyClearanceStatus.NotCleared && ruleCount == 0)
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.43f, 0.35f, 1f));
            else if (entry.ClearanceStatus != DutyClearanceStatus.NotCleared && ruleCount > 10)
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.86f, 0.24f, 1f));

            ImGui.TextUnformatted(ruleCount.ToString());
            if (entry.ClearanceStatus != DutyClearanceStatus.NotCleared && (ruleCount == 0 || ruleCount > 10))
                ImGui.PopStyleColor();

            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(entry.TerritoryTypeId.ToString());

            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(entry.ContentFinderConditionId == 0 ? "-" : entry.ContentFinderConditionId.ToString());

            ImGui.TableSetColumnIndex(5);
            ImGui.PushStyleColor(ImGuiCol.Text, GetClearanceColor(entry.ClearanceStatus));
            ImGui.TextUnformatted(GetClearanceLabel(entry.ClearanceStatus));
            ImGui.PopStyleColor();

            ImGui.TableSetColumnIndex(6);
            ImGui.PushStyleColor(ImGuiCol.Text, GetSupportLevelColor(entry.SupportLevel));
            ImGui.TextUnformatted(GetSupportLevelLabel(entry.SupportLevel));
            ImGui.PopStyleColor();

            ImGui.TableSetColumnIndex(7);
            ImGui.TextUnformatted(entry.LevelRequired.ToString());

            ImGui.TableSetColumnIndex(8);
            ImGui.TextUnformatted(entry.ExpansionName);

            ImGui.TableSetColumnIndex(9);
            ImGui.TextWrapped(entry.SupportNote);
        }

        ImGui.EndTable();
    }

    private void DrawDutyCategoryFilters()
    {
        ImGui.TextUnformatted("Families");
        for (var index = 0; index < DutyCategoryDisplayCatalog.Entries.Count; index++)
        {
            var entry = DutyCategoryDisplayCatalog.Entries[index];
            var enabled = dutyCategoryFilters.GetValueOrDefault(entry.Category, true);
            if (ImGui.Checkbox($"{entry.FilterLabel}##DutyFamily{entry.Category}", ref enabled))
                dutyCategoryFilters[entry.Category] = enabled;

            if (index < DutyCategoryDisplayCatalog.Entries.Count - 1)
                ImGui.SameLine();
        }

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

        var categorySummary = string.Join(
            "  |  ",
            DutyCategoryDisplayCatalog.Entries.Select(x => $"{x.FilterLabel} {entries.Count(y => y.Category == x.Category)}"));
        var plannedTests = entries.Count(x => x.IsPlannedTest);
        ImGui.TextDisabled(categorySummary);
        ImGui.TextDisabled($"{plannedTests} planned test{(plannedTests == 1 ? string.Empty : "s")} flagged in the catalog.");

        int CountStatus(DutyClearanceStatus status)
            => entries.Count(x => x.ClearanceStatus == status);
    }

    private void DrawRuleAtlas()
    {
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

        if (!ImGui.BeginTable("ADSRuleAtlas", 3, ImGuiTableFlags.SizingStretchSame))
            return;

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        DrawInfoCard(
            "Rule Atlas",
            new Vector4(0.33f, 0.73f, 0.96f, 1f),
            $"Grand total: {totalRules}\nEnabled: {enabledRules}\nGlobal: {globalRules}");

        ImGui.TableSetColumnIndex(1);
        DrawInfoCard(
            "Coverage Signals",
            new Vector4(1.0f, 0.82f, 0.29f, 1f),
            $"Maturity > 0 with 0 rules: {dutiesWithNoRules}\nMaturity > 0 with >10 rules: {dutiesWithDenseRules}");

        ImGui.TableSetColumnIndex(2);
        DrawInfoCard(
            "By Category",
            new Vector4(0.47f, 0.90f, 0.64f, 1f),
            string.IsNullOrWhiteSpace(classificationSummary) ? "No rules authored yet." : classificationSummary);

        ImGui.EndTable();
    }

    private void DrawInfoCard(string title, Vector4 accent, string body)
    {
        var background = new Vector4(
            MathF.Min(accent.X * 0.15f, 1f),
            MathF.Min(accent.Y * 0.15f, 1f),
            MathF.Min(accent.Z * 0.15f, 1f),
            0.34f);

        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, background);
        ImGui.PushStyleColor(ImGuiCol.Border, accent);
        if (ImGui.BeginChild($"##{title}", new Vector2(-1f, 88f), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.TextColored(accent, title);
            ImGui.TextWrapped(body);
        }

        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar();
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
