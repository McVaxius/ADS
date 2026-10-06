using AethertekUI;
using ADS.Localization;
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
        windowMotion.DrawChrome();
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

        MaterialText.Text(Ui.T("Ghost Inspector"));
        MaterialText.TextWrapped(Ui.T("Shows the current recovery ghost memory ADS is carrying for this duty, plus the live, remembered, and last-ghosted manual destination state. Monster ghosts are stale battle targets; interactable ghosts include class and ghost-reason metadata."));
        MaterialText.Text(Ui.T("Duty: {0}", context.CurrentDuty is { } duty ? Ui.DutyName(duty) : Ui.T("None")));
        MaterialText.Text(Ui.T("Current live map id: {0}", context.MapId));
        MaterialText.Text(Ui.T("Monster ghosts: {0}", plugin.ObservationMemoryService.Current.MonsterGhosts.Count));
        ImGui.SameLine();
        MaterialText.Text(Ui.T("Interactable ghosts: {0}", plugin.ObservationMemoryService.Current.InteractableGhosts.Count));
        ImGui.SameLine();
        MaterialText.Text(Ui.T("Frontier/manual rows: {0}", frontierOrManualRowCount));

        ImGui.SetNextItemWidth(320f);
        WindowLayout.InputTextWithHint("##ADSGhostFilter", Ui.T("filter by name, type, class, or map"), ref filter, 128);
        ImGui.SameLine();
        if (WindowLayout.SmallButton(Ui.L("Clear")))
            filter = string.Empty;
        ImGui.SameLine();
        WindowLayout.Checkbox("Current Map Only", ref currentMapOnly);

        MaterialText.Text(Ui.T("Ghosts shown: {0}", rows.Count));
        if (!ImGui.BeginTable("ADSGhostTable", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(-1f, -1f)))
            return;

        ImGui.TableSetupColumn(Ui.L("Type"), ImGuiTableColumnFlags.WidthFixed, 110f);
        ImGui.TableSetupColumn(Ui.L("Name"), ImGuiTableColumnFlags.WidthStretch, 260f);
        ImGui.TableSetupColumn(Ui.L("Class"), ImGuiTableColumnFlags.WidthFixed, 140f);
        ImGui.TableSetupColumn(Ui.L("Reason"), ImGuiTableColumnFlags.WidthFixed, 130f);
        ImGui.TableSetupColumn(Ui.L("Map"), ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn(Ui.L("Age"), ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn(Ui.L("Pos"), ImGuiTableColumnFlags.WidthFixed, 250f);
        ImGui.TableSetupColumn(Ui.L("Flag"), ImGuiTableColumnFlags.WidthFixed, 70f);
        WindowLayout.TableHeadersRow();

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            MaterialText.Text(Ui.Display(row.Type));
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(1);
            MaterialText.Text(row.Name);
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(2);
            MaterialText.Text(Ui.Display(row.Classification));
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(3);
            MaterialText.Text(Ui.Display(row.GhostReason));
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(4);
            MaterialText.Text(row.MapId.ToString(Ui.Culture));
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(5);
            MaterialText.Text(Ui.T("{0:0.0}s", (DateTime.UtcNow - row.LastSeenUtc).TotalSeconds));
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(6);
            MaterialText.Text(Ui.T("{0:0.0}, {1:0.0}, {2:0.0}", row.Position.X, row.Position.Y, row.Position.Z));
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(7);
            if (WindowLayout.SmallButton(Ui.L("[FLAG]##ADSGhostFlag{0}", index)))
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
        try {
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35f);
        try {
        MaterialText.Text(row.Name);
        MaterialText.Text(Ui.T("Type: {0}", Ui.Display(row.Type)));
        MaterialText.Text(Ui.T("Class: {0}", Ui.Display(row.Classification)));
        MaterialText.Text(Ui.T("Reason: {0}", Ui.Display(row.GhostReason)));
        MaterialText.Text(Ui.T("MapId: {0}", row.MapId));
        MaterialText.Text(Ui.T("DataId: {0}", row.DataId));
        MaterialText.Text(Ui.T("GameObjectId: {0}", row.GameObjectId));
        MaterialText.Text(Ui.T("Last seen: {0:O}", row.LastSeenUtc));
        MaterialText.Text(Ui.T("Position: {0:0.00}, {1:0.00}, {2:0.00}", row.Position.X, row.Position.Y, row.Position.Z));

        } finally { ImGui.PopTextWrapPos(); }

        } finally { ImGui.EndTooltip(); }
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
