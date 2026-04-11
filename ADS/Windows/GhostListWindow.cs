using System.Numerics;
using ADS.Models;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class GhostListWindow : PositionedWindow, IDisposable
{
    private readonly Plugin plugin;
    private string filter = string.Empty;
    private bool currentMapOnly;

    public GhostListWindow(Plugin plugin)
        : base("ADS Ghost List###ADSGhostList")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(860f, 420f),
            MaximumSize = new Vector2(3200f, 2200f),
        };
        Size = new Vector2(1280f, 840f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();

        var context = plugin.DutyContextService.Current;
        var rows = BuildRows()
            .Where(x => !currentMapOnly || context.MapId == 0 || x.MapId == context.MapId)
            .Where(MatchesFilter)
            .OrderByDescending(x => x.MapId == context.MapId)
            .ThenBy(x => x.Type)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(x => x.LastSeenUtc)
            .ToList();
        var frontierOrManualRowCount = rows.Count(x => x.Type is "Frontier" or "ManualMapXZ" or "ManualXYZ");

        ImGui.TextUnformatted("Ghost Inspector");
        ImGui.TextWrapped("Shows the current recovery ghost memory ADS is carrying for this duty, plus the live, remembered, and last-ghosted manual destination state. Monster ghosts are stale battle targets; interactable ghosts include class and ghost-reason metadata.");
        ImGui.TextUnformatted($"Duty: {context.CurrentDuty?.EnglishName ?? "None"}");
        ImGui.TextUnformatted($"Current live map id: {context.MapId}");
        ImGui.TextUnformatted($"Monster ghosts: {plugin.ObservationMemoryService.Current.MonsterGhosts.Count}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Interactable ghosts: {plugin.ObservationMemoryService.Current.InteractableGhosts.Count}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Frontier/manual rows: {frontierOrManualRowCount}");

        ImGui.SetNextItemWidth(320f);
        ImGui.InputTextWithHint("##ADSGhostFilter", "filter by name, type, class, or map", ref filter, 128);
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear"))
            filter = string.Empty;
        ImGui.SameLine();
        ImGui.Checkbox("Current Map Only", ref currentMapOnly);

        ImGui.TextUnformatted($"Ghosts shown: {rows.Count}");
        if (!ImGui.BeginTable("ADSGhostTable", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(-1f, -1f)))
            return;

        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 110f);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 260f);
        ImGui.TableSetupColumn("Class", ImGuiTableColumnFlags.WidthFixed, 140f);
        ImGui.TableSetupColumn("Reason", ImGuiTableColumnFlags.WidthFixed, 130f);
        ImGui.TableSetupColumn("Map", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Age", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("Pos", ImGuiTableColumnFlags.WidthFixed, 250f);
        ImGui.TableSetupColumn("Flag", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableHeadersRow();

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(row.Type);
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(row.Name);
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(row.Classification);
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(row.GhostReason);
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(row.MapId.ToString());
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted($"{(DateTime.UtcNow - row.LastSeenUtc).TotalSeconds:0.0}s");
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(6);
            ImGui.TextUnformatted($"{row.Position.X:0.0}, {row.Position.Y:0.0}, {row.Position.Z:0.0}");
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(7);
            if (ImGui.SmallButton($"[FLAG]##ADSGhostFlag{index}"))
                plugin.TryPlaceObjectFlag(row.Name, row.Position);
        }

        ImGui.EndTable();
    }

    private IEnumerable<GhostRow> BuildRows()
    {
        var localPlayerPosition = Plugin.ObjectTable.LocalPlayer?.Position;
        var currentFrontier = plugin.DungeonFrontierService.CurrentTarget;
        if (currentFrontier is not null)
        {
            yield return new GhostRow(
                GetFrontierRowType(currentFrontier),
                currentFrontier.Name,
                plugin.DungeonFrontierService.CurrentMode.ToString(),
                "CurrentTarget",
                currentFrontier.MapId,
                0,
                0,
                DateTime.UtcNow,
                currentFrontier.Position);
        }

        var rememberedManualDestination = plugin.DungeonFrontierService.GetCurrentOrRememberedManualDestination(localPlayerPosition);
        if (rememberedManualDestination is not null
            && (currentFrontier is null || !string.Equals(currentFrontier.Key, rememberedManualDestination.Key, StringComparison.Ordinal)))
        {
            yield return new GhostRow(
                GetFrontierRowType(rememberedManualDestination),
                rememberedManualDestination.Name,
                "Remembered",
                "FollowThrough",
                rememberedManualDestination.MapId,
                0,
                0,
                DateTime.UtcNow,
                rememberedManualDestination.Position);
        }

        if (plugin.DungeonFrontierService.LastGhostedManualDestination is { } ghostedManualDestination)
        {
            yield return new GhostRow(
                GetFrontierRowType(ghostedManualDestination),
                ghostedManualDestination.Name,
                "Ghosted",
                plugin.DungeonFrontierService.LastGhostedManualDestinationReason,
                ghostedManualDestination.MapId,
                0,
                0,
                plugin.DungeonFrontierService.LastGhostedManualDestinationUtc ?? DateTime.UtcNow,
                ghostedManualDestination.Position);
        }

        foreach (var monster in plugin.ObservationMemoryService.Current.MonsterGhosts)
        {
            yield return new GhostRow(
                "Monster",
                monster.Name,
                "Monster",
                "SeenPreviously",
                monster.MapId,
                monster.DataId,
                monster.GameObjectId,
                monster.LastSeenUtc,
                monster.Position);
        }

        foreach (var interactable in plugin.ObservationMemoryService.Current.InteractableGhosts)
        {
            yield return new GhostRow(
                "Interactable",
                interactable.Name,
                interactable.Classification.ToString(),
                interactable.GhostReason.ToString(),
                interactable.MapId,
                interactable.DataId,
                interactable.GameObjectId,
                interactable.LastSeenUtc,
                interactable.Position);
        }
    }

    private bool MatchesFilter(GhostRow row)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        return row.Type.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.Classification.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.GhostReason.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.MapId.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static void DrawRowTooltip(GhostRow row)
    {
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35f);
        ImGui.TextUnformatted(row.Name);
        ImGui.TextUnformatted($"Type: {row.Type}");
        ImGui.TextUnformatted($"Class: {row.Classification}");
        ImGui.TextUnformatted($"Reason: {row.GhostReason}");
        ImGui.TextUnformatted($"MapId: {row.MapId}");
        ImGui.TextUnformatted($"DataId: {row.DataId}");
        ImGui.TextUnformatted($"GameObjectId: {row.GameObjectId}");
        ImGui.TextUnformatted($"Last seen: {row.LastSeenUtc:O}");
        ImGui.TextUnformatted($"Position: {row.Position.X:0.00}, {row.Position.Y:0.00}, {row.Position.Z:0.00}");
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private static string GetFrontierRowType(DungeonFrontierPoint point)
        => point.IsManualXyzDestination
            ? "ManualXYZ"
            : point.IsManualMapXzDestination
                ? "ManualMapXZ"
                : "Frontier";

    private sealed record GhostRow(
        string Type,
        string Name,
        string Classification,
        string GhostReason,
        uint MapId,
        uint DataId,
        ulong GameObjectId,
        DateTime LastSeenUtc,
        Vector3 Position);
}
