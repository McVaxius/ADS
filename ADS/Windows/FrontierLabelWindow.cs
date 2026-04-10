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

        ImGui.TextUnformatted("Map Label Marker Inspector");
        ImGui.TextWrapped("These entries come from the Lumina MapMarker range referenced by each current territory map, converted from marker texture-space back to world X/Z so you can flag and inspect where the game thinks named labels live.");
        ImGui.TextUnformatted($"Duty: {context.CurrentDuty?.EnglishName ?? "None"}");
        ImGui.TextUnformatted($"Territory / CFC: {context.TerritoryTypeId} / {context.ContentFinderConditionId}");
        ImGui.TextWrapped($"Status: {plugin.DungeonFrontierService.CurrentLabelStatus}");
        ImGui.TextWrapped($"Flag status: {plugin.ObjectExplorerStatus}");

        ImGui.SetNextItemWidth(320f);
        ImGui.InputTextWithHint("##ADSFrontierLabelFilter", "filter by label or map", ref filter, 128);
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear"))
            filter = string.Empty;

        ImGui.TextUnformatted($"Labels shown: {markers.Count}");
        if (!ImGui.BeginTable("ADSFrontierLabelTable", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(-1f, -1f)))
            return;

        ImGui.TableSetupColumn("Label");
        ImGui.TableSetupColumn("Map", ImGuiTableColumnFlags.WidthFixed, 140f);
        ImGui.TableSetupColumn("Map XY", ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn("World X", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("World Z", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("Data", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("Flag", ImGuiTableColumnFlags.WidthFixed, 70f);
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
            ImGui.TextUnformatted($"{marker.MapCoordinates.X:0.0}, {marker.MapCoordinates.Y:0.0}");
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
            if (ImGui.SmallButton($"[FLAG]##ADSFrontierLabelFlag{index}"))
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
        ImGui.TextUnformatted($"Map: {marker.MapName} ({marker.MapId})");
        ImGui.TextUnformatted($"Marker range: {marker.MarkerRangeId}");
        ImGui.TextUnformatted($"Map XY: {marker.MapCoordinates.X:0.0}, {marker.MapCoordinates.Y:0.0}");
        ImGui.TextUnformatted($"World X/Z: {marker.WorldPosition.X:0.00}, {marker.WorldPosition.Z:0.00}");
        ImGui.TextUnformatted($"Texture XY: {marker.TextureX}, {marker.TextureY}");
        ImGui.TextUnformatted($"DataType/Icon: {marker.DataType} / {marker.Icon}");
        ImGui.TextUnformatted($"Subrow: {marker.SubrowId}");
        ImGui.EndTooltip();
    }
}
