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
        DrawLiveSummary(playerPosition);
        ImGui.Spacing();
        DrawRouteTable(selectedRoute);
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

    private void DrawLiveSummary(Vector3? playerPosition)
    {
        if (!playerPosition.HasValue)
        {
            ImGui.TextUnformatted("Player XYZ: unavailable");
            return;
        }

        var player = playerPosition.Value;
        ImGui.TextUnformatted($"Player XYZ: {FormatVector(player)}");

        var nearestLiveDoor = plugin.ObservationMemoryService.Current.LiveInteractables
            .Where(x => x.Classification == InteractableClass.TreasureDoor)
            .OrderBy(x =>
            {
                var delta = x.Position - player;
                return Vector3.Dot(delta, delta);
            })
            .FirstOrDefault();
        if (nearestLiveDoor is not null)
        {
            ImGui.TextUnformatted(
                $"Nearest live door: {nearestLiveDoor.Name} | {FormatVector(nearestLiveDoor.Position)}");
        }
        else
        {
            ImGui.TextUnformatted("Nearest live door: none");
        }
    }

    private void DrawRouteTable(TreasureRouteDefinition? route)
    {
        if (route is null)
        {
            ImGui.TextUnformatted("No treasure route selected.");
            return;
        }

        ImGui.TextUnformatted(GetRouteLabel(route));
        DrawEntrySection(route);
        ImGui.Spacing();
        DrawRoomRows(route);
    }

    private void DrawEntrySection(TreasureRouteDefinition route)
    {
        ImGui.TextUnformatted("Entry XYZ");
        if (route.EntryPoint is null)
        {
            ImGui.TextUnformatted("Entry point missing.");
            return;
        }

        const ImGuiTableFlags tableFlags =
            ImGuiTableFlags.Borders
            | ImGuiTableFlags.RowBg
            | ImGuiTableFlags.SizingFixedFit;

        if (!ImGui.BeginTable("ADSTreasureRouteEntry", 3, tableFlags))
            return;

        ImGui.TableSetupColumn("X", ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn("Y", ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn("Z", ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableHeadersRow();

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        if (EditFloatCell("##EntryX", route.EntryPoint.X, out var x))
        {
            route.EntryPoint.X = x;
            dirty = true;
        }

        ImGui.TableSetColumnIndex(1);
        if (EditFloatCell("##EntryY", route.EntryPoint.Y, out var y))
        {
            route.EntryPoint.Y = y;
            dirty = true;
        }

        ImGui.TableSetColumnIndex(2);
        if (EditFloatCell("##EntryZ", route.EntryPoint.Z, out var z))
        {
            route.EntryPoint.Z = z;
            dirty = true;
        }

        ImGui.EndTable();
    }

    private void DrawRoomRows(TreasureRouteDefinition route)
    {
        var isThief = route.RouteKind == TreasureRouteKind.Thief;
        var columnCount = isThief ? 4 : 3;
        const ImGuiTableFlags tableFlags =
            ImGuiTableFlags.Borders
            | ImGuiTableFlags.RowBg
            | ImGuiTableFlags.ScrollY
            | ImGuiTableFlags.Resizable
            | ImGuiTableFlags.SizingFixedFit;

        if (!ImGui.BeginTable("ADSTreasureRouteRooms", columnCount, tableFlags, new Vector2(-1f, -1f)))
            return;

        ImGui.TableSetupColumn("Room", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Left XYZ", ImGuiTableColumnFlags.WidthFixed, 300f);
        if (isThief)
            ImGui.TableSetupColumn("Middle XYZ", ImGuiTableColumnFlags.WidthFixed, 300f);
        ImGui.TableSetupColumn("Right XYZ", ImGuiTableColumnFlags.WidthFixed, 300f);
        ImGui.TableHeadersRow();

        foreach (var room in route.Rooms.OrderBy(x => x.Room))
        {
            ImGui.PushID(room.Room);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(room.Room.ToString());

            ImGui.TableSetColumnIndex(1);
            DrawCoordinateCell("Left", room.Left);

            var rightColumn = 2;
            if (isThief)
            {
                ImGui.TableSetColumnIndex(2);
                DrawCoordinateCell("Middle", room.Middle);
                rightColumn = 3;
            }

            ImGui.TableSetColumnIndex(rightColumn);
            DrawCoordinateCell("Right", room.Right);
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawCoordinateCell(string id, TreasureRouteCoordinate? coordinate)
    {
        if (coordinate is null)
        {
            ImGui.TextDisabled("-");
            return;
        }

        const float componentWidth = 88f;
        ImGui.PushID(id);
        if (EditFloatCell("##X", coordinate.X, out var x, componentWidth))
        {
            coordinate.X = x;
            dirty = true;
        }

        ImGui.SameLine();
        if (EditFloatCell("##Y", coordinate.Y, out var y, componentWidth))
        {
            coordinate.Y = y;
            dirty = true;
        }

        ImGui.SameLine();
        if (EditFloatCell("##Z", coordinate.Z, out var z, componentWidth))
        {
            coordinate.Z = z;
            dirty = true;
        }

        ImGui.PopID();
    }

    private void SaveDraft()
    {
        foreach (var route in draft.Routes)
            route.Rooms = route.Rooms
                .OrderBy(x => x.Room)
                .ToList();

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

    private static string GetRouteLabel(TreasureRouteDefinition? route)
        => route is null
            ? "(no route)"
            : $"{route.TerritoryTypeId} - {(string.IsNullOrWhiteSpace(route.DutyName) ? "Treasure route" : route.DutyName)}";

    private static string FormatVector(Vector3 value)
        => $"{value.X:0.00}, {value.Y:0.00}, {value.Z:0.00}";

    private static bool EditFloatCell(string id, float? value, out float editedValue, float width = -1f)
    {
        ImGui.SetNextItemWidth(width);
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
