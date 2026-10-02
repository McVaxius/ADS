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
        Size = new Vector2(1100f, 820f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();

        var context = plugin.DutyContextService.Current;
        var markers = plugin.DungeonFrontierService.CurrentLabelMarkers
            .Where(MatchesFilter)
            .ToList();

        ImGui.TextUnformatted(Ui.T("Map Label Marker Inspector"));
        ImGui.TextWrapped(Ui.T("These entries come from the Lumina MapMarker range for the current live map row only, converted from marker texture-space back to world X/Z so you can inspect named labels and the DataType 1 area-boundary indicators used by frontier seeking."));
        ImGui.TextUnformatted(Ui.T("Duty: {0}", context.CurrentDuty is { } duty ? Ui.DutyName(duty) : Ui.T("None")));
        ImGui.TextUnformatted(Ui.T("Territory / Map / CFC: {0} / {1} / {2}", context.TerritoryTypeId, plugin.DungeonFrontierService.ActiveMapId, context.ContentFinderConditionId));
        ImGui.TextUnformatted(Ui.T("Active map: {0}", plugin.DungeonFrontierService.ActiveMapName));
        ImGui.TextWrapped(Ui.T("Status: {0}", Ui.Display(plugin.DungeonFrontierService.CurrentLabelStatus)));
        ImGui.TextWrapped(Ui.T("Flag status: {0}", Ui.Display(plugin.ObjectExplorerMapFlagStatus)));

        ImGui.SetNextItemWidth(320f);
        ImGui.InputTextWithHint("##ADSFrontierLabelFilter", Ui.T("filter by label or map"), ref filter, 128);
        ImGui.SameLine();
        if (ImGui.SmallButton(Ui.L("Clear")))
            filter = string.Empty;

        ImGui.TextUnformatted(Ui.T("Labels shown: {0}", markers.Count));
        if (!ImGui.BeginTable("ADSFrontierLabelTable", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(-1f, -1f)))
            return;

        ImGui.TableSetupColumn(Ui.L("Label"));
        ImGui.TableSetupColumn(Ui.L("Map"), ImGuiTableColumnFlags.WidthFixed, 140f);
        ImGui.TableSetupColumn(Ui.L("Map XY"), ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn(Ui.L("World X"), ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn(Ui.L("World Z"), ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn(Ui.L("Data"), ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn(Ui.L("Flag"), ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableHeadersRow();

        for (var index = 0; index < markers.Count; index++)
        {
            var marker = markers[index];
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(marker.Name);
            DrawRowTooltip(marker);

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(marker.MapName);
            DrawRowTooltip(marker);

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(Ui.T("{0:0.0}, {1:0.0}", marker.MapCoordinates.X, marker.MapCoordinates.Y));
            DrawRowTooltip(marker);

            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(marker.WorldPosition.X.ToString("0.00"));
            DrawRowTooltip(marker);

            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(marker.WorldPosition.Z.ToString("0.00"));
            DrawRowTooltip(marker);

            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted(marker.DataType.ToString());
            DrawRowTooltip(marker);

            ImGui.TableSetColumnIndex(6);
            if (ImGui.SmallButton(Ui.L("[FLAG]##ADSFrontierLabelFlag{0}", index)))
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
        ImGui.TextUnformatted(marker.Name);
        ImGui.TextUnformatted(Ui.T("Map: {0} ({1})", marker.MapName, marker.MapId));
        ImGui.TextUnformatted(Ui.T("Marker range: {0}", marker.MarkerRangeId));
        ImGui.TextUnformatted(Ui.T("Map XY: {0:0.0}, {1:0.0}", marker.MapCoordinates.X, marker.MapCoordinates.Y));
        ImGui.TextUnformatted(Ui.T("World X/Z: {0:0.00}, {1:0.00}", marker.WorldPosition.X, marker.WorldPosition.Z));
        ImGui.TextUnformatted(Ui.T("Texture XY: {0}, {1}", marker.TextureX, marker.TextureY));
        ImGui.TextUnformatted(Ui.T("DataType/DataKey/Icon: {0} / {1} / {2}", marker.DataType, marker.DataKeyRowId, marker.Icon));
        ImGui.TextUnformatted(Ui.T("Subrow: {0}", marker.SubrowId));
        ImGui.EndTooltip();
    }
}
