using System.Numerics;
using ADS.Models;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace ADS.Windows;

public sealed class ObjectExplorerWindow : PositionedWindow, IDisposable
{
    private readonly Plugin plugin;
    private readonly string[] objectKindFilters = ["All", .. Enum.GetNames<ObjectKind>().OrderBy(x => x, StringComparer.OrdinalIgnoreCase)];
    private readonly string[] ruleClassificationOptions = ["Auto", .. Enum.GetNames<InteractableClass>()];
    private string textFilter = string.Empty;
    private int objectKindFilterIndex;
    private bool levelFilterEnabled;
    private int levelFilter;
    private int levelFilterMode;
    private bool targetableOnly;
    private bool sameMapOnly;
    private bool compact;

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
        DrawExportControls();

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer is null)
        {
            if (!compact)
            {
                ImGui.TextWrapped($"Action status: {plugin.ObjectExplorerStatus}");
                ImGui.TextWrapped($"Flag status: {plugin.ObjectExplorerMapFlagStatus}");
                ImGui.TextUnformatted("No local player is available.");
            }
            return;
        }

        var context = plugin.DutyContextService.Current;
        var activeLayer = plugin.ObjectPriorityRuleService.GetActiveLayerName(context) ?? "Unknown";
        var nearestFrontierLabel = plugin.DungeonFrontierService.CurrentLabelMarkers
            .OrderBy(x => Vector3.Distance(localPlayer.Position, x.WorldPosition))
            .FirstOrDefault();

        if (!compact)
        {
            ImGui.TextUnformatted("Live Loaded Objects");
            ImGui.TextWrapped("Operator-first object table. Rules column shows all rule hits before live layer filtering. Same-map-only is best-effort: ADS hides rows that only match off-layer scoped rules and keeps rows with no map-layer evidence.");
            ImGui.TextUnformatted($"Territory / Map / CFC: {context.TerritoryTypeId} / {context.MapId} / {context.ContentFinderConditionId}");
            ImGui.TextUnformatted($"Layer / Sub-area: {activeLayer}");
            ImGui.TextUnformatted($"Nearest frontier label: {(nearestFrontierLabel is null ? "None" : $"{nearestFrontierLabel.Name} ({Vector3.Distance(localPlayer.Position, nearestFrontierLabel.WorldPosition):0.0}y)")}");
            ImGui.TextWrapped($"Frontier target: {plugin.DungeonFrontierService.CurrentTarget?.Name ?? "None"}");
            ImGui.TextWrapped($"Action status: {plugin.ObjectExplorerStatus}");
            ImGui.TextWrapped($"Flag status: {plugin.ObjectExplorerMapFlagStatus}");
        }

        ImGui.TextUnformatted("Search");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##ADSObjectTextFilter", "name / base id / object kind; | for OR", ref textFilter, 128);
        ImGui.TextUnformatted("Kind");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180f);
        ImGui.Combo("##ADSObjectKindFilter", ref objectKindFilterIndex, objectKindFilters, objectKindFilters.Length);
        ImGui.SameLine();
        if (ImGui.Button("Clear filters"))
        {
            textFilter = string.Empty;
            objectKindFilterIndex = 0;
            levelFilterEnabled = false;
            levelFilter = 0;
            levelFilterMode = 0;
            targetableOnly = false;
            sameMapOnly = false;
        }

        if (ImGui.Checkbox("Filter by Lv.", ref levelFilterEnabled) && levelFilterEnabled && levelFilter <= 0)
            levelFilter = localPlayer.Level;

        ImGui.SameLine();
        ImGui.BeginDisabled(!levelFilterEnabled);
        ImGui.SetNextItemWidth(72f);
        ImGui.InputInt("Lv.##ADSObjectLevelFilter", ref levelFilter, 0, 0);
        if (levelFilterEnabled)
            levelFilter = Math.Max(1, levelFilter);
        ImGui.SameLine();
        ImGui.RadioButton("Exact##ADSObjectLevelFilter", ref levelFilterMode, 0);
        ImGui.SameLine();
        ImGui.RadioButton("<=##ADSObjectLevelFilter", ref levelFilterMode, 1);
        ImGui.SameLine();
        ImGui.RadioButton(">=##ADSObjectLevelFilter", ref levelFilterMode, 2);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.Checkbox("Targetable only", ref targetableOnly);
        ImGui.SameLine();
        ImGui.Checkbox("Same-map-only", ref sameMapOnly);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Best-effort layer filter. Rows with only off-layer scoped rule hits are hidden; rows with no layer evidence stay visible.");
        }

        ImGui.SameLine();
        ImGui.TextUnformatted("|");
        ImGui.SameLine();
        var seedObjectPosition = plugin.Configuration.RuleEditorSeedObjectPosition;
        if (ImGui.Checkbox("Pin rule to XYZ", ref seedObjectPosition))
        {
            plugin.Configuration.RuleEditorSeedObjectPosition = seedObjectPosition;
            plugin.SaveConfiguration();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When enabled, RULE seeds object XYZ coordinates plus a 6y radius. BaseId remains 0; observed BaseId is kept in Notes.");

        var rows = BuildRows(context, localPlayer)
            .Where(MatchesFilter)
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!compact)
            ImGui.TextUnformatted($"Objects shown: {rows.Count}");
        if (!ImGui.BeginTable("ADSObjectExplorerTable", 13, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(-1f, -1f)))
            return;

        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 110f);
        ImGui.TableSetupColumn("Lv.", ImGuiTableColumnFlags.WidthFixed, 45f);
        ImGui.TableSetupColumn("f.Lv", ImGuiTableColumnFlags.WidthFixed, 45f);
        ImGui.TableSetupColumn("Element", ImGuiTableColumnFlags.WidthFixed, 72f);
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
            ImGui.TextUnformatted(row.Level?.ToString() ?? "—");
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(row.ForayLevel?.ToString() ?? "—");
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(FormatForayElement(row.ForayElement));
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted(row.Distance.ToString("0.00"));
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(6);
            ImGui.TextUnformatted(row.VerticalDelta.ToString("0.00"));
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(7);
            ImGui.TextUnformatted(row.MatchingRules.Count.ToString());
            DrawRuleTooltip(row);

            ImGui.TableSetColumnIndex(8);
            if (ImGui.SmallButton($"moveto##ADSObjectMove{index}"))
                plugin.TryExplorerNavigation(row.Position, useFly: false);

            ImGui.TableSetColumnIndex(9);
            if (ImGui.SmallButton($"flyto##ADSObjectFly{index}"))
                plugin.TryExplorerNavigation(row.Position, useFly: true);

            ImGui.TableSetColumnIndex(10);
            if (ImGui.SmallButton($"FLAG##ADSObjectFlag{index}"))
                plugin.TryPlaceObjectFlag(row.Name, row.Position);

            ImGui.TableSetColumnIndex(11);
            if (ImGui.SmallButton($"RULE##ADSObjectRule{index}"))
                ImGui.OpenPopup($"ADSObjectRulePopup##{index}");
            DrawRulePopup(index, row);

            ImGui.TableSetColumnIndex(12);
            if (ImGui.SmallButton($"XYZ##ADSObjectCopy{index}"))
                ImGui.SetClipboardText($"{row.Position.X:0.00}, {row.Position.Y:0.00}, {row.Position.Z:0.00}");
        }

        ImGui.EndTable();
    }

    private void DrawExportControls()
    {
        if (ImGui.SmallButton("Export All JSON"))
            plugin.ExportExplorerSnapshot();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Exports all buffered server events and every loaded object-table entry, independent of viewer filters.");

        ImGui.SameLine();
        if (ImGui.SmallButton("Open Export Folder"))
        {
            Directory.CreateDirectory(plugin.ExplorerSnapshotExportService.ExportDirectory);
            plugin.OpenPath(plugin.ExplorerSnapshotExportService.ExportDirectory);
        }

        ImGui.SameLine();
        ImGui.Checkbox("Compact", ref compact);

        if (!compact)
            ImGui.TextWrapped($"Export status: {plugin.ExplorerSnapshotExportService.Status}");
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
            var forayInfo = TryGetForayInfo(gameObject);

            yield return new ObjectExplorerRow(
                Name: name,
                ObjectKind: gameObject.ObjectKind.ToString(),
                Level: gameObject is ICharacter character ? character.Level : null,
                ForayLevel: forayInfo.Level,
                ForayElement: forayInfo.Element,
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
        if (levelFilterEnabled)
        {
            if (row.Level is not { } level)
                return false;

            if (levelFilterMode == 1 && level > levelFilter)
                return false;

            if (levelFilterMode == 2 && level < levelFilter)
                return false;

            if (levelFilterMode == 0 && level != levelFilter)
                return false;
        }

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

        var terms = textFilter.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.Length == 0 || terms.Any(term =>
            row.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || row.ObjectKind.Contains(term, StringComparison.OrdinalIgnoreCase)
            || row.BaseId.ToString().Contains(term, StringComparison.OrdinalIgnoreCase)
            || row.GameObjectId.ToString().Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static unsafe (byte? Level, byte? Element) TryGetForayInfo(IGameObject gameObject)
    {
        if (gameObject is not IBattleChara || gameObject.Address == nint.Zero)
            return (null, null);

        var character = (NativeCharacter*)gameObject.Address;
        if (character == null
            || character->VirtualTable == null
            || character->VirtualTable->GetForayInfo == null)
        {
            return (null, null);
        }

        var forayInfo = character->GetForayInfo();
        return forayInfo == null || forayInfo->Level == 0
            ? (null, null)
            : (forayInfo->Level, forayInfo->Element);
    }

    private static string FormatForayElement(byte? element)
        => element switch
        {
            null => "—",
            1 => "Fire",
            2 => "Ice",
            3 => "Wind",
            4 => "Earth",
            5 => "Lightning",
            6 => "Water",
            _ => element.Value.ToString(),
        };

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
        ImGui.TextUnformatted($"Level: {row.Level?.ToString() ?? "—"}");
        ImGui.TextUnformatted($"Foray level: {row.ForayLevel?.ToString() ?? "—"}");
        ImGui.TextUnformatted($"Foray element: {FormatForayElement(row.ForayElement)}");
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
        byte? Level,
        byte? ForayLevel,
        byte? ForayElement,
        float Distance,
        float VerticalDelta,
        uint BaseId,
        ulong GameObjectId,
        bool IsTargetable,
        bool MatchesCurrentLayer,
        Vector3 Position,
        IReadOnlyList<ObjectPriorityRule> MatchingRules);
}
