using AethertekUI;
using ADS.Localization;
using System.Numerics;
using ADS.Models;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class FrontierLabelWindow : PositionedWindow, IDisposable
{
    private readonly Plugin plugin;
    private string filter = string.Empty;

    public FrontierLabelWindow(Plugin plugin)
        : base("ADS Frontier Labels###ADSFrontierLabels")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720f, 420f),
            MaximumSize = new Vector2(3200f, 2200f),
        };
        Size = plugin.Configuration.UiCompact ? new Vector2(860f, 620f) : new Vector2(1100f, 820f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        windowMotion.DrawChrome();
        FinalizePendingWindowPlacement();

        var context = plugin.DutyContextService.Current;
        var markers = plugin.DungeonFrontierService.CurrentLabelMarkers
            .Where(MatchesFilter)
            .ToList();

        MaterialText.Text(Ui.T("Map Label Marker Inspector"));
        MaterialText.TextWrapped(Ui.T("These entries come from the Lumina MapMarker range for the current live map row only, converted from marker texture-space back to world X/Z so you can inspect named labels and the DataType 1 area-boundary indicators used by frontier seeking."));
        MaterialText.Text(Ui.T("Duty: {0}", context.CurrentDuty is { } duty ? Ui.DutyName(duty) : Ui.T("None")));
        MaterialText.Text(Ui.T("Territory / Map / CFC: {0} / {1} / {2}", context.TerritoryTypeId, plugin.DungeonFrontierService.ActiveMapId, context.ContentFinderConditionId));
        MaterialText.Text(Ui.T("Active map: {0}", plugin.DungeonFrontierService.ActiveMapName));
        MaterialText.TextWrapped(Ui.T("Status: {0}", Ui.Display(plugin.DungeonFrontierService.CurrentLabelStatus)));
        MaterialText.TextWrapped(Ui.T("Flag status: {0}", Ui.Display(plugin.ObjectExplorerMapFlagStatus)));

        ImGui.SetNextItemWidth(320f);
        WindowLayout.InputTextWithHint("##ADSFrontierLabelFilter", Ui.T("filter by label or map"), ref filter, 128);
        ImGui.SameLine();
        if (WindowLayout.SmallButton(Ui.L("Clear")))
            filter = string.Empty;

        MaterialText.Text(Ui.T("Labels shown: {0}", markers.Count));
        if (!ImGui.BeginTable("ADSFrontierLabelTable", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(-1f, -1f)))
            return;

        ImGui.TableSetupColumn(Ui.L("Label"));
        ImGui.TableSetupColumn(Ui.L("Map"), ImGuiTableColumnFlags.WidthFixed, 140f);
        ImGui.TableSetupColumn(Ui.L("Map XY"), ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn(Ui.L("World X"), ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn(Ui.L("World Z"), ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn(Ui.L("Data"), ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn(Ui.L("Flag"), ImGuiTableColumnFlags.WidthFixed, 70f);
        WindowLayout.TableHeadersRow();

        for (var index = 0; index < markers.Count; index++)
        {
            var marker = markers[index];
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            MaterialText.Text(marker.Name);
            DrawRowTooltip(marker);

            ImGui.TableSetColumnIndex(1);
            MaterialText.Text(marker.MapName);
            DrawRowTooltip(marker);

            ImGui.TableSetColumnIndex(2);
            MaterialText.Text(Ui.T("{0:0.0}, {1:0.0}", marker.MapCoordinates.X, marker.MapCoordinates.Y));
            DrawRowTooltip(marker);

            ImGui.TableSetColumnIndex(3);
            MaterialText.Text(marker.WorldPosition.X.ToString("0.00"));
            DrawRowTooltip(marker);

            ImGui.TableSetColumnIndex(4);
            MaterialText.Text(marker.WorldPosition.Z.ToString("0.00"));
            DrawRowTooltip(marker);

            ImGui.TableSetColumnIndex(5);
            MaterialText.Text(marker.DataType.ToString());
            DrawRowTooltip(marker);

            ImGui.TableSetColumnIndex(6);
            if (WindowLayout.SmallButton(Ui.L("[FLAG]##ADSFrontierLabelFlag{0}", index)))
                plugin.TryPlaceObjectFlag(marker.Name, marker.WorldPosition);
        }

        ImGui.EndTable();
    }

    private bool MatchesFilter(MapLabelMarker marker)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        return marker.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || marker.MapName.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static void DrawRowTooltip(MapLabelMarker marker)
    {
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        try {
        MaterialText.Text(marker.Name);
        MaterialText.Text(Ui.T("Map: {0} ({1})", marker.MapName, marker.MapId));
        MaterialText.Text(Ui.T("Marker range: {0}", marker.MarkerRangeId));
        MaterialText.Text(Ui.T("Map XY: {0:0.0}, {1:0.0}", marker.MapCoordinates.X, marker.MapCoordinates.Y));
        MaterialText.Text(Ui.T("World X/Z: {0:0.00}, {1:0.00}", marker.WorldPosition.X, marker.WorldPosition.Z));
        MaterialText.Text(Ui.T("Texture XY: {0}, {1}", marker.TextureX, marker.TextureY));
        MaterialText.Text(Ui.T("DataType/DataKey/Icon: {0} / {1} / {2}", marker.DataType, marker.DataKeyRowId, marker.Icon));
        MaterialText.Text(Ui.T("Subrow: {0}", marker.SubrowId));

        } finally { ImGui.EndTooltip(); }
    }
}
