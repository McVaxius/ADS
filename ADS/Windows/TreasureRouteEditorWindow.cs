using System.Numerics;
using ADS.Models;
using ADS.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class TreasureRouteEditorWindow : PositionedWindow, IDisposable
{
    private readonly Plugin plugin;
    private TreasureRouteManifest draft = new();
    private bool draftLoaded;
    private bool dirty;
    private bool filterCurrentTerritory = true;
    private uint selectedTerritoryId;
    private string editorStatus = "Treasure routes not loaded.";

    public TreasureRouteEditorWindow(Plugin plugin)
        : base("ADS Treasure Routes###ADSTreasureRoutes")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(980f, 520f),
            MaximumSize = new Vector2(3200f, 2200f),
        };
        Size = new Vector2(1500f, 860f);
    }

    public void Dispose()
    {
    }

    public void OpenForCurrentTerritory()
    {
        filterCurrentTerritory = true;
        selectedTerritoryId = 0;
        draftLoaded = false;
        IsOpen = true;
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();
        EnsureDraftLoaded();

        var context = plugin.DutyContextService.Current;
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var playerPosition = localPlayer?.Position;
        var visibleRoutes = BuildVisibleRoutes(context.TerritoryTypeId).ToList();
        EnsureSelectedRoute(visibleRoutes);
        var selectedRoute = draft.Routes.FirstOrDefault(x => x.TerritoryTypeId == selectedTerritoryId);

        ImGui.TextUnformatted($"Path: {TreasureDungeonData.ConfigPath}");
        ImGui.TextUnformatted($"Active routes: {TreasureDungeonData.ActiveRouteCount}");
        ImGui.TextWrapped(TreasureDungeonData.LastLoadStatus);
        foreach (var warning in TreasureDungeonData.FallbackWarnings.Take(3))
            ImGui.TextDisabled(warning);
        if (dirty)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.97f, 0.84f, 0.31f, 1f));
            ImGui.TextUnformatted("Unsaved treasure route edits");
            ImGui.PopStyleColor();
        }

        DrawToolbar(context.TerritoryTypeId, visibleRoutes);
        ImGui.Spacing();
        DrawLiveSummary(selectedRoute, playerPosition);
        ImGui.Spacing();
        DrawRouteTable(selectedRoute, playerPosition);
    }

    private void EnsureDraftLoaded()
    {
        if (draftLoaded)
            return;

        RefreshDraft("Loaded treasure routes.");
    }

    private void RefreshDraft(string status)
    {
        TreasureDungeonData.Reload();
        draft = TreasureDungeonData.CreateEditableCopy();
        draftLoaded = true;
        dirty = false;
        editorStatus = $"{status} {TreasureDungeonData.LastLoadStatus}";
        if (selectedTerritoryId != 0 && draft.Routes.All(x => x.TerritoryTypeId != selectedTerritoryId))
            selectedTerritoryId = 0;
    }

    private void DrawToolbar(uint currentTerritoryId, IReadOnlyList<TreasureRouteDefinition> visibleRoutes)
    {
        ImGui.TextUnformatted($"Current territory: {(currentTerritoryId == 0 ? "-" : currentTerritoryId.ToString())}");
        ImGui.SameLine();
        ImGui.Checkbox("Current territory route", ref filterCurrentTerritory);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(320f);
        var comboLabel = GetRouteLabel(draft.Routes.FirstOrDefault(x => x.TerritoryTypeId == selectedTerritoryId));
        if (ImGui.BeginCombo("##ADSTreasureRouteSelect", comboLabel))
        {
            foreach (var route in visibleRoutes)
            {
                var selected = route.TerritoryTypeId == selectedTerritoryId;
                if (ImGui.Selectable(GetRouteLabel(route), selected))
                    selectedTerritoryId = route.TerritoryTypeId;
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        using (new ImGuiDisabledBlock(!dirty))
        {
            if (ImGui.Button("Save"))
                SaveDraft();
        }

        ImGui.SameLine();
        if (ImGui.Button("Reload"))
            RefreshDraft("Reloaded treasure routes.");

        ImGui.SameLine();
        using (new ImGuiDisabledBlock(plugin.RemoteJsonUpdateService.IsUpdateRunning))
        {
            if (ImGui.Button("Update"))
            {
                plugin.ForceRemoteJsonUpdate();
                editorStatus = plugin.RemoteJsonUpdateService.LastUpdateStatus;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Open JSON"))
        {
            EnsureJsonFileForOpen();
            plugin.OpenPath(TreasureDungeonData.ConfigPath);
        }

        ImGui.SameLine();
        using (new ImGuiDisabledBlock(selectedTerritoryId == 0 || !TreasureDungeonData.GetBuiltInRouteTerritoryIds().Contains(selectedTerritoryId)))
        {
            if (ImGui.Button("Reset Route"))
                ResetSelectedRoute();
        }

        ImGui.TextWrapped(editorStatus);
        ImGui.TextDisabled(plugin.RemoteJsonUpdateService.LastUpdateStatus);
    }

    private void DrawLiveSummary(TreasureRouteDefinition? route, Vector3? playerPosition)
    {
        if (!playerPosition.HasValue)
        {
            ImGui.TextUnformatted("Player XYZ: unavailable");
            return;
        }

        var player = playerPosition.Value;
        ImGui.TextUnformatted($"Player XYZ: {FormatVector(player)}");

        var nearestStatic = route is null
            ? null
            : EnumerateRoutePoints(route)
                .Select(point => new
                {
                    Point = point,
                    Position = GetPosition(point),
                    Distance = Vector3.Distance(player, GetPosition(point)),
                })
                .OrderBy(x => x.Distance)
                .FirstOrDefault();
        if (nearestStatic is not null)
            ImGui.TextUnformatted($"Nearest static: {nearestStatic.Point.Label} | {FormatDelta(player, nearestStatic.Position)} | {nearestStatic.Distance:0.00}y");
        else
            ImGui.TextUnformatted("Nearest static: none");

        var nearestLiveDoor = plugin.ObservationMemoryService.Current.LiveInteractables
            .Where(x => x.Classification == InteractableClass.TreasureDoor)
            .Select(x => new
            {
                Door = x,
                Distance = Vector3.Distance(player, x.Position),
            })
            .OrderBy(x => x.Distance)
            .FirstOrDefault();
        if (nearestLiveDoor is not null)
        {
            ImGui.TextUnformatted(
                $"Nearest live door: {nearestLiveDoor.Door.Name} | {FormatVector(nearestLiveDoor.Door.Position)} | {FormatDelta(player, nearestLiveDoor.Door.Position)} | {nearestLiveDoor.Distance:0.00}y");
        }
        else
        {
            ImGui.TextUnformatted("Nearest live door: none");
        }
    }

    private void DrawRouteTable(TreasureRouteDefinition? route, Vector3? playerPosition)
    {
        if (route is null)
        {
            ImGui.TextUnformatted("No treasure route selected.");
            return;
        }

        ImGui.TextUnformatted(GetRouteLabel(route));
        const ImGuiTableFlags tableFlags =
            ImGuiTableFlags.Borders
            | ImGuiTableFlags.RowBg
            | ImGuiTableFlags.ScrollY
            | ImGuiTableFlags.Resizable
            | ImGuiTableFlags.SizingFixedFit;

        if (!ImGui.BeginTable("ADSTreasureRouteTable", 11, tableFlags, new Vector2(-1f, -1f)))
            return;

        ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, 220f);
        ImGui.TableSetupColumn("Room", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("X", ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn("Y", ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn("Z", ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn("dX", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("dY", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("dZ", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("Dist", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableHeadersRow();

        if (route.EntryPoint is not null)
            DrawPointRow("Entry", route.EntryPoint, playerPosition);

        for (var i = 0; i < route.Doors.Count; i++)
        {
            ImGui.PushID(i);
            DrawPointRow("Door", route.Doors[i], playerPosition);
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawPointRow(string kind, TreasureRoutePointDefinition point, Vector3? playerPosition)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(kind);
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(point.Label);
        ImGui.TableSetColumnIndex(2);
        ImGui.TextUnformatted(point.Room.ToString());
        ImGui.TableSetColumnIndex(3);
        ImGui.TextUnformatted(point.Slot.ToString());

        ImGui.TableSetColumnIndex(4);
        if (EditFloatCell("##X", point.X, out var x))
        {
            point.X = x;
            dirty = true;
        }

        ImGui.TableSetColumnIndex(5);
        if (EditFloatCell("##Y", point.Y, out var y))
        {
            point.Y = y;
            dirty = true;
        }

        ImGui.TableSetColumnIndex(6);
        if (EditFloatCell("##Z", point.Z, out var z))
        {
            point.Z = z;
            dirty = true;
        }

        var position = GetPosition(point);
        var delta = playerPosition.HasValue ? position - playerPosition.Value : Vector3.Zero;
        ImGui.TableSetColumnIndex(7);
        ImGui.TextUnformatted(playerPosition.HasValue ? delta.X.ToString("0.00") : "-");
        ImGui.TableSetColumnIndex(8);
        ImGui.TextUnformatted(playerPosition.HasValue ? delta.Y.ToString("0.00") : "-");
        ImGui.TableSetColumnIndex(9);
        ImGui.TextUnformatted(playerPosition.HasValue ? delta.Z.ToString("0.00") : "-");
        ImGui.TableSetColumnIndex(10);
        ImGui.TextUnformatted(playerPosition.HasValue ? Vector3.Distance(position, playerPosition.Value).ToString("0.00") : "-");
    }

    private void SaveDraft()
    {
        draft.Routes = draft.Routes
            .OrderBy(x => x.TerritoryTypeId)
            .ToList();
        if (TreasureDungeonData.SaveManifest(draft))
        {
            RefreshDraft("Saved treasure routes.");
            return;
        }

        editorStatus = TreasureDungeonData.LastLoadStatus;
    }

    private void EnsureJsonFileForOpen()
    {
        if (File.Exists(TreasureDungeonData.ConfigPath))
            return;

        TreasureDungeonData.SaveManifest(draft);
        RefreshDraft("Created treasure route JSON.");
    }

    private void ResetSelectedRoute()
    {
        if (!TreasureDungeonData.TryCreateBuiltInRouteCopy(selectedTerritoryId, out var builtInRoute))
            return;

        var index = draft.Routes.FindIndex(x => x.TerritoryTypeId == selectedTerritoryId);
        if (index >= 0)
            draft.Routes[index] = builtInRoute;
        else
            draft.Routes.Add(builtInRoute);

        dirty = true;
        editorStatus = $"Reset territory {selectedTerritoryId} from built-in route geometry. Save to write JSON.";
    }

    private IEnumerable<TreasureRouteDefinition> BuildVisibleRoutes(uint currentTerritoryId)
    {
        var routeTerritories = draft.Routes
            .Select(x => x.TerritoryTypeId)
            .ToHashSet();
        if (filterCurrentTerritory && currentTerritoryId != 0 && routeTerritories.Contains(currentTerritoryId))
            return draft.Routes.Where(x => x.TerritoryTypeId == currentTerritoryId);

        return draft.Routes
            .Where(x => x.Enabled)
            .OrderBy(x => x.TerritoryTypeId)
            .ThenBy(x => x.DutyName, StringComparer.OrdinalIgnoreCase);
    }

    private void EnsureSelectedRoute(IReadOnlyList<TreasureRouteDefinition> visibleRoutes)
    {
        if (selectedTerritoryId != 0 && visibleRoutes.Any(x => x.TerritoryTypeId == selectedTerritoryId))
            return;

        selectedTerritoryId = visibleRoutes.FirstOrDefault()?.TerritoryTypeId ?? 0;
    }

    private static IEnumerable<TreasureRoutePointDefinition> EnumerateRoutePoints(TreasureRouteDefinition route)
    {
        if (route.EntryPoint is not null)
            yield return route.EntryPoint;

        foreach (var door in route.Doors)
            yield return door;
    }

    private static Vector3 GetPosition(TreasureRoutePointDefinition point)
        => new(point.X ?? 0f, point.Y ?? 0f, point.Z ?? 0f);

    private static string GetRouteLabel(TreasureRouteDefinition? route)
        => route is null
            ? "(no route)"
            : $"{route.TerritoryTypeId} - {(string.IsNullOrWhiteSpace(route.DutyName) ? "Treasure route" : route.DutyName)}";

    private static string FormatVector(Vector3 value)
        => $"{value.X:0.00}, {value.Y:0.00}, {value.Z:0.00}";

    private static string FormatDelta(Vector3 from, Vector3 to)
    {
        var delta = to - from;
        return $"dXYZ {delta.X:0.00}, {delta.Y:0.00}, {delta.Z:0.00}";
    }

    private static bool EditFloatCell(string id, float? value, out float editedValue)
    {
        ImGui.SetNextItemWidth(-1f);
        var local = value ?? 0f;
        editedValue = local;
        if (!ImGui.InputFloat(id, ref local, 0f, 0f, "%.3f"))
            return false;

        editedValue = local;
        return true;
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
