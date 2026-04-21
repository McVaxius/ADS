using System.Numerics;
using ADS.Models;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class ObjectExplorerWindow : PositionedWindow, IDisposable
{
    private readonly Plugin plugin;
    private readonly string[] objectKindFilters = ["All", .. Enum.GetNames<ObjectKind>().OrderBy(x => x, StringComparer.OrdinalIgnoreCase)];
    private readonly string[] ruleClassificationOptions = ["Auto", .. Enum.GetNames<InteractableClass>()];
    private string textFilter = string.Empty;
    private int objectKindFilterIndex;
    private bool targetableOnly;
    private bool sameMapOnly;

    public ObjectExplorerWindow(Plugin plugin)
        : base("ADS Object Explorer###ADSObjectExplorer")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720f, 420f),
            MaximumSize = new Vector2(3200f, 2200f),
        };
        Size = new Vector2(1320f, 920f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer is null)
        {
            ImGui.TextUnformatted("No local player is available.");
            return;
        }

        var context = plugin.DutyContextService.Current;
        var activeLayer = plugin.ObjectPriorityRuleService.GetActiveLayerName(context) ?? "Unknown";
        var nearestFrontierLabel = plugin.DungeonFrontierService.CurrentLabelMarkers
            .OrderBy(x => Vector3.Distance(localPlayer.Position, x.WorldPosition))
            .FirstOrDefault();

        ImGui.TextUnformatted("Live Loaded Objects");
        ImGui.TextWrapped("Operator-first object table. Rules column shows all rule hits before live layer filtering. Same-map-only is best-effort: ADS hides rows that only match off-layer scoped rules and keeps rows with no map-layer evidence.");
        ImGui.TextUnformatted($"Territory / Map / CFC: {context.TerritoryTypeId} / {context.MapId} / {context.ContentFinderConditionId}");
        ImGui.TextUnformatted($"Layer / Sub-area: {activeLayer}");
        ImGui.TextUnformatted($"Nearest frontier label: {(nearestFrontierLabel is null ? "None" : $"{nearestFrontierLabel.Name} ({Vector3.Distance(localPlayer.Position, nearestFrontierLabel.WorldPosition):0.0}y)")}");
        ImGui.TextWrapped($"Frontier target: {plugin.DungeonFrontierService.CurrentTarget?.Name ?? "None"}");
        ImGui.TextWrapped($"Status: {plugin.ObjectExplorerStatus}");

        ImGui.SetNextItemWidth(260f);
        ImGui.InputTextWithHint("##ADSObjectTextFilter", "filter text / base id / object kind", ref textFilter, 128);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180f);
        ImGui.Combo("##ADSObjectKindFilter", ref objectKindFilterIndex, objectKindFilters, objectKindFilters.Length);
        ImGui.SameLine();
        ImGui.Checkbox("Targetable only", ref targetableOnly);
        ImGui.SameLine();
        ImGui.Checkbox("Same-map-only", ref sameMapOnly);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Best-effort layer filter. Rows with only off-layer scoped rule hits are hidden; rows with no layer evidence stay visible.");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear"))
        {
            textFilter = string.Empty;
            objectKindFilterIndex = 0;
            targetableOnly = false;
            sameMapOnly = false;
        }

        var rows = BuildRows(context, localPlayer)
            .Where(MatchesFilter)
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ImGui.TextUnformatted($"Objects shown: {rows.Count}");
        if (!ImGui.BeginTable("ADSObjectExplorerTable", 10, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(-1f, -1f)))
            return;

        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 110f);
        ImGui.TableSetupColumn("Dist", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Y", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("Rules", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("moveto", ImGuiTableColumnFlags.WidthFixed, 78f);
        ImGui.TableSetupColumn("flyto", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("FLAG", ImGuiTableColumnFlags.WidthFixed, 62f);
        ImGui.TableSetupColumn("RULE", ImGuiTableColumnFlags.WidthFixed, 62f);
        ImGui.TableSetupColumn("Copy XYZ", ImGuiTableColumnFlags.WidthFixed, 88f);
        ImGui.TableHeadersRow();

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(row.Name);
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(row.ObjectKind);
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(row.Distance.ToString("0.00"));
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(row.VerticalDelta.ToString("0.00"));
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(row.MatchingRules.Count.ToString());
            DrawRuleTooltip(row);

            ImGui.TableSetColumnIndex(5);
            if (ImGui.SmallButton($"moveto##ADSObjectMove{index}"))
                plugin.TryExplorerNavigation(row.Position, useFly: false);

            ImGui.TableSetColumnIndex(6);
            if (ImGui.SmallButton($"flyto##ADSObjectFly{index}"))
                plugin.TryExplorerNavigation(row.Position, useFly: true);

            ImGui.TableSetColumnIndex(7);
            if (ImGui.SmallButton($"FLAG##ADSObjectFlag{index}"))
                plugin.TryPlaceObjectFlag(row.Name, row.Position);

            ImGui.TableSetColumnIndex(8);
            if (ImGui.SmallButton($"RULE##ADSObjectRule{index}"))
                ImGui.OpenPopup($"ADSObjectRulePopup##{index}");
            DrawRulePopup(index, row);

            ImGui.TableSetColumnIndex(9);
            if (ImGui.SmallButton($"XYZ##ADSObjectCopy{index}"))
                ImGui.SetClipboardText($"{row.Position.X:0.00}, {row.Position.Y:0.00}, {row.Position.Z:0.00}");
        }

        ImGui.EndTable();
    }

    private IEnumerable<ObjectExplorerRow> BuildRows(DutyContextSnapshot context, IGameObject localPlayer)
    {
        foreach (var gameObject in Plugin.ObjectTable)
        {
            if (gameObject is null)
                continue;

            if (gameObject.GameObjectId == localPlayer.GameObjectId)
                continue;

            var name = gameObject.Name.TextValue.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var matchingRules = plugin.ObjectPriorityRuleService.GetExplorerMatches(
                context,
                gameObject.ObjectKind,
                gameObject.BaseId,
                name,
                gameObject.Position,
                context.MapId);
            var matchesCurrentLayer = plugin.ObjectPriorityRuleService.MatchesCurrentLayerForExplorer(
                context,
                gameObject.ObjectKind,
                gameObject.BaseId,
                name,
                gameObject.Position,
                context.MapId);

            yield return new ObjectExplorerRow(
                Name: name,
                ObjectKind: gameObject.ObjectKind.ToString(),
                Distance: Vector3.Distance(localPlayer.Position, gameObject.Position),
                VerticalDelta: MathF.Abs(gameObject.Position.Y - localPlayer.Position.Y),
                BaseId: gameObject.BaseId,
                GameObjectId: gameObject.GameObjectId,
                IsTargetable: gameObject.IsTargetable,
                MatchesCurrentLayer: matchesCurrentLayer,
                Position: gameObject.Position,
                MatchingRules: matchingRules);
        }
    }

    private bool MatchesFilter(ObjectExplorerRow row)
    {
        if (targetableOnly && !row.IsTargetable)
            return false;

        if (sameMapOnly && !row.MatchesCurrentLayer)
            return false;

        var selectedKind = objectKindFilters[Math.Clamp(objectKindFilterIndex, 0, objectKindFilters.Length - 1)];
        if (!string.Equals(selectedKind, "All", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(row.ObjectKind, selectedKind, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(textFilter))
            return true;

        return row.Name.Contains(textFilter, StringComparison.OrdinalIgnoreCase)
            || row.ObjectKind.Contains(textFilter, StringComparison.OrdinalIgnoreCase)
            || row.BaseId.ToString().Contains(textFilter, StringComparison.OrdinalIgnoreCase)
            || row.GameObjectId.ToString().Contains(textFilter, StringComparison.OrdinalIgnoreCase);
    }

    private void DrawRulePopup(int index, ObjectExplorerRow row)
    {
        if (!ImGui.BeginPopup($"ADSObjectRulePopup##{index}"))
            return;

        ImGui.TextUnformatted("Seed rule with");
        ImGui.Separator();
        for (var optionIndex = 0; optionIndex < ruleClassificationOptions.Length; optionIndex++)
        {
            var option = ruleClassificationOptions[optionIndex];
            if (ImGui.Selectable(option))
            {
                plugin.CreateRuleFromExplorer(
                    row.Name,
                    row.ObjectKind,
                    row.BaseId,
                    row.Position,
                    string.Equals(option, "Auto", StringComparison.OrdinalIgnoreCase) ? string.Empty : option);
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.EndPopup();
    }

    private void DrawRuleTooltip(ObjectExplorerRow row)
    {
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted(row.MatchingRules.Count == 0 ? "No matching rules." : "Matching rules");
        if (row.MatchingRules.Count > 0)
        {
            foreach (var rule in row.MatchingRules.Take(8))
            {
                var type = string.IsNullOrWhiteSpace(rule.Classification) ? "(blank)" : rule.Classification;
                var scope = plugin.ObjectPriorityRuleService.DescribeRuleScope(rule);
                ImGui.TextUnformatted($"{type} | pri {rule.Priority} | {scope}");
            }

            if (row.MatchingRules.Count > 8)
                ImGui.TextUnformatted($"... {row.MatchingRules.Count - 8} more");
        }

        ImGui.EndTooltip();
    }

    private static void DrawRowTooltip(ObjectExplorerRow row)
    {
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted(row.Name);
        ImGui.TextUnformatted($"ObjectKind: {row.ObjectKind}");
        ImGui.TextUnformatted($"Distance: {row.Distance:0.00}");
        ImGui.TextUnformatted($"Y delta: {row.VerticalDelta:0.00}");
        ImGui.TextUnformatted($"BaseId: {row.BaseId}");
        ImGui.TextUnformatted($"GameObjectId: {row.GameObjectId}");
        ImGui.TextUnformatted($"Targetable: {(row.IsTargetable ? "YES" : "NO")}");
        ImGui.TextUnformatted($"Matches current layer: {(row.MatchesCurrentLayer ? "YES" : "NO")}");
        ImGui.TextUnformatted($"Position: {row.Position.X:0.00}, {row.Position.Y:0.00}, {row.Position.Z:0.00}");
        ImGui.EndTooltip();
    }

    private sealed record ObjectExplorerRow(
        string Name,
        string ObjectKind,
        float Distance,
        float VerticalDelta,
        uint BaseId,
        ulong GameObjectId,
        bool IsTargetable,
        bool MatchesCurrentLayer,
        Vector3 Position,
        IReadOnlyList<ObjectPriorityRule> MatchingRules);
}
