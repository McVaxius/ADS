using AethertekUI;
using ADS.Localization;
using System.Globalization;
using System.Numerics;
using ADS.Models;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace ADS.Windows;

public sealed class ObjectExplorerWindow : PositionedWindow, IDisposable
{
    private readonly Plugin plugin;
    private readonly string[] objectKindFilters = ["All", .. Enum.GetNames<ObjectKind>()
        .Select(kind => kind == nameof(ObjectKind.Pc) ? "Player" : kind).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)];
    private readonly string[] ruleClassificationOptions = ["Auto", .. Enum.GetNames<InteractableClass>()];
    private string textFilter = string.Empty;
    private int objectKindFilterIndex;
    private bool levelFilterEnabled;
    private int levelFilter;
    private int levelFilterMode;
    private bool targetableOnly;
    private bool sameMapOnly;
    private bool compact;
    private bool showTeleport;
    private bool showMap = true;
    private bool showDiagnostics;
    private readonly bool[] visibleColumns = Enumerable.Repeat(true, 8).ToArray();
    private static readonly string[] columnLabels = ["Name", "Kind", "Lv.", "f.Lv", "Element", "Dist", "Y", "Rules"];
    private ObjectExplorerRow? selectedObject;
    private bool scrollToSelection;
    private bool centerSelection;
    private uint viewedTerritory;
    private uint viewedMap;
    private float mapZoom = 1;
    private Vector2 mapCenter = new(.5f);
    private float? preferredMapWidth;
    private float mapResizeStartWidth;
    private bool flagPlacementArmed;
    internal MaterialTitlebarStatus TitlebarStatus
    {
        get
        {
            var count = Ui.T("{0} objects", Plugin.ObjectTable.Count());
            var summary = selectedObject is { } selected ? count + " · " + Ui.T("Selected: {0}", selected.Name) : count;
            return new(plugin.Configuration.UiDetailedTitlebarStatus ? summary : count, count, summary);
        }
    }
    private string selectionStatus = "Click a marker or row to pin it.";
    private ulong? rulePopupObjectId;

    public ObjectExplorerWindow(Plugin plugin)
        : base("ADS Object Explorer###ADSObjectExplorer")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720f, 420f),
            MaximumSize = new Vector2(3200f, 2200f),
        };
        Size = plugin.Configuration.UiCompact ? new Vector2(980f, 620f) : new Vector2(1320f, 920f);
    }

    public void Dispose()
    {
        flagPlacementArmed = false;
    }

    public override void OnClose() => flagPlacementArmed = false;

    public override unsafe void PostDraw()
    {
        base.PostDraw();
        var window = ImGuiP.FindWindowByName(WindowName);
        if (window.Handle != null && window.Collapsed) flagPlacementArmed = false;
    }

    private void ClearFilters()
    {
        textFilter = string.Empty;
        objectKindFilterIndex = 0;
        levelFilterEnabled = false;
        levelFilter = 0;
        levelFilterMode = 0;
        targetableOnly = false;
        sameMapOnly = false;
    }

    public void OpenPlayers()
    {
        ClearFilters();
        objectKindFilterIndex = Array.IndexOf(objectKindFilters, "Player");
        IsOpen = true;
    }

    public override void Draw()
    {
        windowMotion.DrawChrome();
        FinalizePendingWindowPlacement();
        using var spacing = new MaterialStyleScope();
        spacing.Style(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 4 * MaterialTheme.Metrics.Scale));
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var context = plugin.DutyContextService.Current;
        if (context.IsUnsafeTransition || ImGui.IsKeyPressed(ImGuiKey.Escape) || ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            flagPlacementArmed = false;
        var mapId = context.CurrentMapId ?? 0;
        if (viewedTerritory != context.TerritoryTypeId || viewedMap != mapId || localPlayer is null)
        {
            flagPlacementArmed = false;
            selectedObject = null;
            scrollToSelection = false;
            centerSelection = false;
            rulePopupObjectId = null;
            selectionStatus = "Click a marker or row to pin it.";
            mapCenter = new(.5f);
            mapZoom = 1;
            viewedTerritory = context.TerritoryTypeId;
            viewedMap = mapId;
        }
        DrawFilters(localPlayer);
        if (!showMap) flagPlacementArmed = false;
        ImGui.SameLine();
        DrawExportControls();
        ImGui.SameLine();
        if (WindowLayout.SmallButton(Ui.L("Diagnostics"))) showDiagnostics = !showDiagnostics;
        if (showDiagnostics) DrawDiagnostics(context, localPlayer);
        MaterialText.Text(Ui.T("Map {0} · {1}", mapId == 0 ? Ui.T("Unavailable") : mapId.ToString(),
            Ui.Display(plugin.ObjectPriorityRuleService.GetActiveLayerName(context) ?? "Unknown")));
        if (localPlayer is null)
        {
            MaterialText.Text(Ui.T("No local player is available."));
            return;
        }
        DrawFlagCoordinates();

        var rows = BuildRows(context, localPlayer).Where(MatchesFilter).ToList();
        if (selectedObject is { } selected)
        {
            selectedObject = rows.Find(row => SameObject(row, selected));
            if (selectedObject is null)
            {
                selectionStatus = "The selected object disappeared or was filtered out.";
                scrollToSelection = false;
            }
        }
        var available = ImGui.GetContentRegionAvail();
        var footerText = (selectedObject is { } selection
            ? Ui.T("Selected: {0} · {1:0.0}y", selection.Name, selection.Distance)
            : Ui.T(selectionStatus)) + "   " + Ui.T("Objects shown: {0}", rows.Count);
        var actionStatus = Ui.T("Action status: {0}", Ui.Display(plugin.ObjectExplorerStatus));
        var footerTextWidth = Math.Max(1, available.X - (selectedObject is null ? 0 : ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X));
        var footerHeight = Math.Max(ImGui.GetFrameHeight(), MaterialText.Measure(footerText, wrapWidth: footerTextWidth).Y)
            + MaterialText.Measure(actionStatus, wrapWidth: Math.Max(1, available.X)).Y + ImGui.GetStyle().ItemSpacing.Y * 2;
        var bodyHeight = Math.Max(160 * MaterialTheme.Metrics.Scale, available.Y - footerHeight);
        // The original table stays in its window's ID scope; only the new map owns a child.
        var mapVisible = showMap && available.X >= 1120 * MaterialTheme.Metrics.Scale;
        if (!mapVisible) flagPlacementArmed = false;
        if (mapVisible)
        {
            var scale = MaterialTheme.Metrics.Scale;
            var dividerWidth = 6 * scale;
            var gap = ImGui.GetStyle().ItemSpacing.X;
            var origin = ImGui.GetCursorScreenPos();
            var mapWidth = MapPaneWidth(available.X, scale, dividerWidth + gap * 2, preferredMapWidth);
            ImGui.SetCursorScreenPos(origin + new Vector2(mapWidth + gap, 0));
            ImGui.InvisibleButton("ADSExplorerMapDivider", new Vector2(dividerWidth, bodyHeight));
            var hovered = ImGui.IsItemHovered();
            var active = ImGui.IsItemActive();
            if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) preferredMapWidth = null;
            if (ImGui.IsItemActivated())
                mapResizeStartWidth = MapPaneWidth(available.X, scale, dividerWidth + gap * 2, preferredMapWidth) / scale;
            if (active && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                preferredMapWidth = Math.Max(240, mapResizeStartWidth + ImGui.GetMouseDragDelta(ImGuiMouseButton.Left).X / scale);
            if (hovered || active) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
            if (hovered) MaterialText.SetTooltip(Ui.T("Drag the divider to resize the map; double-click to reset."));
            mapWidth = MapPaneWidth(available.X, scale, dividerWidth + gap * 2, preferredMapWidth);
            var divider = origin + new Vector2(mapWidth + gap + dividerWidth * .5f, 0);
            ImGui.GetWindowDrawList().AddLine(divider, divider + new Vector2(0, bodyHeight),
                ImGui.GetColorU32(active ? ImGuiCol.SeparatorActive : hovered ? ImGuiCol.SeparatorHovered : ImGuiCol.Separator), scale);
            ImGui.SetCursorScreenPos(origin);
            DrawMap(context, localPlayer, rows, new Vector2(mapWidth, bodyHeight));
            ImGui.SameLine(0, dividerWidth + gap * 2);
        }
        DrawObjectTable(context, rows, new Vector2(Math.Max(1, ImGui.GetContentRegionAvail().X), bodyHeight));
        if (selectedObject is not null)
        {
            if (IconAction(Ui.L("Clear selection"), MaterialIcon.Close, "Clear selection"))
            {
                selectedObject = null;
                selectionStatus = "Click a marker or row to pin it.";
            }
            ImGui.SameLine();
        }
        MaterialText.TextWrapped(footerText);
        MaterialText.TextWrapped(actionStatus);
    }

    private void DrawFilters(IGameObject? localPlayer)
    {
        using var controls = MaterialControls.Push(MaterialControlContext.Dense);
        var scale = MaterialTheme.Metrics.Scale;
        var kindOptions = objectKindFilters.Select(Ui.Display).ToArray();
        var kindWidth = MaterialText.Measure(Ui.T("Kind")).X;
        var kindComboWidth = Math.Max(120 * scale, WindowLayout.ComboMinimum(kindOptions));
        var mapWidth = MaterialText.Measure(Ui.T("Map")).X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
        var reserved = kindWidth + kindComboWidth + WindowLayout.ButtonMinimum(Ui.L("Clear filters"))
            + mapWidth + ImGui.GetFrameHeight() + 5 * ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetNextItemWidth(Math.Max(120 * scale, ImGui.GetContentRegionAvail().X - reserved));
        WindowLayout.InputTextWithHint("##ADSObjectTextFilter", Ui.T("name / base id / object kind; | for OR"), ref textFilter, 128);
        ImGui.SameLine();
        MaterialLayout.FitNextItemWidth(0, kindWidth + ImGui.GetStyle().ItemSpacing.X + kindComboWidth);
        MaterialText.Text(Ui.T("Kind"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(kindComboWidth);
        WindowLayout.Combo("##ADSObjectKindFilter", ref objectKindFilterIndex, kindOptions, kindOptions.Length);
        ImGui.SameLine();
        if (WindowLayout.Button(Ui.L("Clear filters")))
            ClearFilters();
        ImGui.SameLine();
        WindowLayout.Checkbox("Map", ref showMap);
        if (ImGui.IsItemHovered()) MaterialText.SetTooltip(Ui.T("The map hides at narrow widths. Drag to pan; scroll to zoom. Selection never starts an action."));
        ImGui.SameLine();
        MaterialLayout.FitNextItemWidth(0, ImGui.GetFrameHeight());
        if (IconAction(Ui.L("Columns"), MaterialIcon.More, "Columns")) ImGui.OpenPopup("ADSExplorerColumns");
        var originalIdRoot = ImGui.GetID(string.Empty);
        if (ImGui.BeginPopup("ADSExplorerColumns"))
        {
            try
            {
                for (var index = 0; index < visibleColumns.Length; index++)
                {
                    if (index == 0) ImGui.BeginDisabled();
                    WindowLayout.Checkbox(columnLabels[index], ref visibleColumns[index]);
                    if (index == 0) ImGui.EndDisabled();
                }
                ImGuiP.PushOverrideID(originalIdRoot);
                try
                {
                    ImGui.BeginDisabled(!plugin.DhogNavTeleportAvailable);
                    try { WindowLayout.Checkbox("Show DNav teleport", ref showTeleport); }
                    finally { ImGui.EndDisabled(); }
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        MaterialText.SetTooltip(Ui.T("Manual teleport only. DNav readiness, territory, filters and shared cooldown still apply."));
                }
                finally { ImGui.PopID(); }
            }
            finally { ImGui.EndPopup(); }
        }
        if (WindowLayout.Checkbox("Filter by Lv.", ref levelFilterEnabled) && levelFilterEnabled && levelFilter <= 0)
            levelFilter = localPlayer is ICharacter character ? character.Level : 1;

        ImGui.SameLine();
        ImGui.BeginDisabled(!levelFilterEnabled);
        ImGui.SetNextItemWidth(72f);
        WindowLayout.InputInt(WindowLayout.InputLabel("Lv.##ADSObjectLevelFilter"), ref levelFilter, 0, 0);
        if (levelFilterEnabled)
            levelFilter = Math.Max(1, levelFilter);
        ImGui.SameLine();
        MaterialLayout.FitNextItemWidth(0, MaterialText.Measure(Ui.T("Exact")).X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X);
        WindowLayout.RadioButton(Ui.L("Exact##ADSObjectLevelFilter"), ref levelFilterMode, 0);
        ImGui.SameLine();
        MaterialLayout.FitNextItemWidth(0, MaterialText.Measure(Ui.T("<=")).X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X);
        WindowLayout.RadioButton(Ui.L("<=##ADSObjectLevelFilter"), ref levelFilterMode, 1);
        ImGui.SameLine();
        MaterialLayout.FitNextItemWidth(0, MaterialText.Measure(Ui.T(">=")).X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X);
        WindowLayout.RadioButton(Ui.L(">=##ADSObjectLevelFilter"), ref levelFilterMode, 2);
        ImGui.EndDisabled();
        ImGui.SameLine();
        WindowLayout.Checkbox("Targetable only", ref targetableOnly);
        ImGui.SameLine();
        WindowLayout.Checkbox("Same-map-only", ref sameMapOnly);
        if (ImGui.IsItemHovered())
        {
            MaterialText.SetTooltip(Ui.T("Best-effort layer filter. Rows with only off-layer scoped rule hits are hidden; rows with no layer evidence stay visible."));
        }

        ImGui.SameLine();
        MaterialText.Text(Ui.T("|"));
        ImGui.SameLine();
        var seedObjectPosition = plugin.Configuration.RuleEditorSeedObjectPosition;
        if (WindowLayout.Checkbox("Pin rule to XYZ", ref seedObjectPosition))
        {
            plugin.Configuration.RuleEditorSeedObjectPosition = seedObjectPosition;
            plugin.SaveConfiguration();
        }

        if (ImGui.IsItemHovered())
            MaterialText.SetTooltip(Ui.T("When enabled, RULE seeds object XYZ coordinates plus a 6y radius. BaseId remains 0; observed BaseId is kept in Notes."));
    }

    private void DrawObjectTable(DutyContextSnapshot context, List<ObjectExplorerRow> rows, Vector2 size)
    {
        var teleportAvailable = plugin.DhogNavTeleportAvailable;
        var whitelistAvailable = plugin.DhogNavWhitelistAvailable;
        using var controls = MaterialTable.PushControls(context: compact ? MaterialControlContext.Dense : MaterialControlContext.Toolbar);
        using var tightRows = MaterialTable.PushTightRows();
        if (!ImGui.BeginTable("ADSObjectExplorerTable", 15, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Resizable | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortMulti, size))
            return;
        try
        {
        for (var index = 0; index < columnLabels.Length; index++)
        {
            var flags = !visibleColumns[index] ? ImGuiTableColumnFlags.Disabled : ImGuiTableColumnFlags.None;
            if (index == 5) flags |= ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortAscending;
            ImGui.TableSetupColumn(Ui.L(columnLabels[index]), flags, index == 0 ? 170 * MaterialTheme.Metrics.Scale : 0);
        }
        var actionWidth = ImGui.GetFrameHeight() + ImGui.GetStyle().CellPadding.X * 2;
        const ImGuiTableColumnFlags actionFlags = ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.NoHeaderWidth;
        ImGui.TableSetupColumn(Ui.L("moveto"), actionFlags, actionWidth);
        ImGui.TableSetupColumn(Ui.L("flyto"), actionFlags, actionWidth);
        ImGui.TableSetupColumn(Ui.L("FLAG"), actionFlags, actionWidth);
        ImGui.TableSetupColumn(Ui.L("RULE"), actionFlags, actionWidth);
        ImGui.TableSetupColumn(Ui.L("Copy XYZ"), actionFlags, actionWidth);
        ImGui.TableSetupColumn(Ui.L("DhogNav"), actionFlags | (whitelistAvailable ? ImGuiTableColumnFlags.None : ImGuiTableColumnFlags.Disabled), actionWidth);
        ImGui.TableSetupColumn(Ui.L("Teleport"), actionFlags | (teleportAvailable && showTeleport ? ImGuiTableColumnFlags.None : ImGuiTableColumnFlags.Disabled), actionWidth);
        ImGui.TableSetupScrollFreeze(1, 1);
        DrawHeaderRow();
        SortTableRows(rows);
        if (selectedObject is { } pinnedSelection)
        {
            var selectedIndex = rows.FindIndex(row => SameObject(row, pinnedSelection));
            if (selectedIndex > 0)
            {
                var pinned = rows[selectedIndex];
                rows.RemoveAt(selectedIndex);
                rows.Insert(0, pinned);
            }
        }
        if (scrollToSelection && selectedObject is not null)
        {
            // The selection is pinned first. Request the table's top even when
            // its previously scrolled rows clip the selected name this frame.
            ImGui.SetScrollY(0);
            scrollToSelection = false;
        }

        foreach (var row in rows)
        {
            ImGui.TableNextRow();
            if (ImGui.TableSetColumnIndex(0))
            {
            var selected = selectedObject is { } pin && SameObject(row, pin);
            if (selected) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(ImGuiCol.Header));
            if (MaterialText.Selectable(row.Name + "###ADSExplorerSelect" + row.GameObjectId, selected,
                    ImGuiSelectableFlags.None, new Vector2(0, ImGui.GetFrameHeight()))) SelectObject(row, centerMap: true);
            DrawRowTooltip(row);
            }
            if (ImGui.TableSetColumnIndex(1))
            {
            MaterialText.Text(row.ObjectKind);
            DrawRowTooltip(row);
            }
            if (ImGui.TableSetColumnIndex(2))
            {
            MaterialText.Text(row.Level?.ToString() ?? "—");
            DrawRowTooltip(row);
            }
            if (ImGui.TableSetColumnIndex(3))
            {
            MaterialText.Text(row.ForayLevel?.ToString() ?? "—");
            DrawRowTooltip(row);
            }
            if (ImGui.TableSetColumnIndex(4))
            {
            MaterialText.Text(FormatForayElement(row.ForayElement));
            DrawRowTooltip(row);
            }
            if (ImGui.TableSetColumnIndex(5))
            {
            MaterialText.Text(row.Distance.ToString("0.00"));
            DrawRowTooltip(row);
            }
            if (ImGui.TableSetColumnIndex(6))
            {
            MaterialText.Text(row.VerticalDelta.ToString("0.00"));
            DrawRowTooltip(row);
            }
            if (ImGui.TableSetColumnIndex(7))
            {
            MaterialText.Text(row.MatchingRules.Count.ToString());
            DrawRuleTooltip(row);
            }

            if (ImGui.TableSetColumnIndex(8) && IconAction(Ui.L("moveto##ADSObjectMove{0}", row.GameObjectId), MaterialIcon.Person, "Move to the object's position."))
                plugin.TryExplorerNavigation(row.Position, useFly: false);

            if (ImGui.TableSetColumnIndex(9) && IconAction(Ui.L("flyto##ADSObjectFly{0}", row.GameObjectId), MaterialIcon.Send, "Fly to the object's position."))
                plugin.TryExplorerNavigation(row.Position, useFly: true);

            if (ImGui.TableSetColumnIndex(10) && IconAction(Ui.L("FLAG##ADSObjectFlag{0}", row.GameObjectId), MaterialIcon.MapPin, "Place a map flag at the object."))
                plugin.TryPlaceObjectFlag(row.Name, row.Position);

            if (ImGui.TableSetColumnIndex(11) && IconAction(Ui.L("RULE##ADSObjectRule{0}", row.GameObjectId), MaterialIcon.Document, "Seed an object rule; choose its class."))
            {
                rulePopupObjectId = row.GameObjectId;
                ImGui.OpenPopup($"ADSObjectRulePopup##{row.GameObjectId}");
            }

            if (ImGui.TableSetColumnIndex(12) && IconAction(Ui.L("XYZ##ADSObjectCopy{0}", row.GameObjectId), MaterialIcon.Copy, "Copy the object's XYZ coordinates."))
                ImGui.SetClipboardText($"{row.Position.X:0.00}, {row.Position.Y:0.00}, {row.Position.Z:0.00}");

            if (whitelistAvailable && row.ObjectKind == nameof(ObjectKind.Pc))
            {
                if (ImGui.TableSetColumnIndex(13) && IconAction(Ui.L("Whitelist##ADSObjectWhitelist{0}", row.GameObjectId), MaterialIcon.Star,
                        "Add this player and their home server to DhogNav's whitelist. DhogNav must be loaded; parasite mode may be off. Finish or cancel any whitelist edit first."))
                    plugin.TryWhitelistExplorerPlayer(row.GameObjectId);
            }

            if (teleportAvailable && showTeleport && ImGui.TableSetColumnIndex(14))
            {
                var territory = context.TerritoryTypeId;
                var reason = plugin.GetExplorerTeleportBlockReason(row, territory);
                ImGui.BeginDisabled(reason != null);
                if (IconAction(Ui.L("Teleport##ADSObjectTeleport{0}", row.GameObjectId), MaterialIcon.Location, "Teleport to this object through DNav.", tooltipEnabled: false))
                    plugin.TryTeleportExplorerObject(row, territory);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    MaterialText.SetTooltip(reason == null
                        ? Ui.T("Teleport to this object through DNav.")
                        : string.Join("; ", reason.Split("; ").Select(Ui.Display)));
            }
        }

        if (rulePopupObjectId is { } objectId)
            DrawRulePopup(objectId, rows.Find(row => row.GameObjectId == objectId));

        }
        finally { ImGui.EndTable(); }
    }

    private void DrawDiagnostics(DutyContextSnapshot context, IGameObject? localPlayer)
    {
        MaterialText.Text(Ui.T("Territory / Map / CFC: {0} / {1} / {2}", context.TerritoryTypeId, context.MapId, context.ContentFinderConditionId));
        MaterialText.Text(Ui.T("Current map ID: {0}", localPlayer is null ? Ui.T("Unavailable") : context.CurrentMapId?.ToString() ?? Ui.T("Unavailable")));
        MaterialText.Text(Ui.T("Live Loaded Objects"));
        MaterialText.TextWrapped(Ui.T("Operator-first object table. Rules column shows all rule hits before live layer filtering. Same-map-only is best-effort: ADS hides rows that only match off-layer scoped rules and keeps rows with no map-layer evidence."));
        MaterialText.Text(Ui.T("Layer / Sub-area: {0}", Ui.Display(plugin.ObjectPriorityRuleService.GetActiveLayerName(context) ?? "Unknown")));
        var nearest = localPlayer is null ? null : plugin.DungeonFrontierService.CurrentLabelMarkers
            .OrderBy(marker => Vector3.Distance(localPlayer.Position, marker.WorldPosition)).FirstOrDefault();
        MaterialText.Text(Ui.T("Nearest frontier label: {0}", nearest is null ? Ui.T("None")
            : $"{nearest.Name} ({Vector3.Distance(localPlayer!.Position, nearest.WorldPosition):0.0}y)"));
        MaterialText.TextWrapped(Ui.T("Frontier target: {0}", plugin.DungeonFrontierService.CurrentTarget?.Name ?? Ui.T("None")));
        MaterialText.TextWrapped(Ui.T("Flag status: {0}", Ui.Display(plugin.ObjectExplorerMapFlagStatus)));
        MaterialText.TextWrapped(Ui.T("Export status: {0}", Ui.Display(plugin.ExplorerSnapshotExportService.Status)));
    }

    // Native button identity remains the original translated wrapper's ### suffix.
    private static bool IconAction(string nativeLabel, MaterialIcon icon, string tooltip, bool tooltipEnabled = true)
    {
        var idStart = nativeLabel.IndexOf("###", StringComparison.Ordinal);
        var id = idStart >= 0 ? nativeLabel[idStart..] : nativeLabel;
        var height = ImGui.GetFrameHeight();
        var clicked = ImGui.Button(id, new Vector2(height));
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var iconSize = Math.Min(18 * MaterialTheme.Metrics.Scale, height - 2 * MaterialTheme.Metrics.Scale);
        if (icon == MaterialIcon.None)
            ImGui.GetWindowDrawList().AddLine(min + new Vector2(height * .25f, height * .5f), min + new Vector2(height * .75f, height * .5f),
                ImGui.GetColorU32(ImGuiCol.Text), Math.Max(1, MaterialTheme.Metrics.Scale));
        else MaterialIcons.Draw(icon, min + (max - min - new Vector2(iconSize)) * .5f, iconSize,
                ImGui.GetStyle().Colors[(int)ImGuiCol.Text], ImGui.GetStyle().Alpha);
        if (tooltipEnabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            MaterialText.SetTooltip(Ui.T(tooltip));
        return clicked;
    }

    private void SelectObject(ObjectExplorerRow row, bool centerMap = false)
    {
        selectedObject = row;
        scrollToSelection = true;
        centerSelection = centerMap;
        selectionStatus = "Click a marker or row to pin it.";
    }

    internal static bool SameObject(ObjectExplorerRow left, ObjectExplorerRow right)
        => left.GameObjectId == right.GameObjectId && left.GameObjectAddress == right.GameObjectAddress
            && left.BaseId == right.BaseId && left.ObjectKind == right.ObjectKind;

    internal static bool TryMapUv(Vector3 position, ushort sizeFactor, short offsetX, short offsetY, out Vector2 uv)
    {
        uv = default;
        if (sizeFactor == 0 || !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) return false;
        // Sheet offsets are world units; the map is a square 2048-unit canvas.
        uv = new Vector2(.5f) + new Vector2(position.X + offsetX, position.Z + offsetY) * (sizeFactor / 100f / 2048f);
        return float.IsFinite(uv.X) && float.IsFinite(uv.Y);
    }

    internal static bool TryMapWorldPosition(Vector2 point, Vector2 imageMin, float imageSide,
        ushort sizeFactor, short offsetX, short offsetY, out Vector3 position)
    {
        position = default;
        if (sizeFactor == 0 || !float.IsFinite(imageSide) || imageSide <= 0) return false;
        var uv = (point - imageMin) / imageSide;
        if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y) || uv.X < 0 || uv.Y < 0 || uv.X > 1 || uv.Y > 1) return false;
        var world = (uv - new Vector2(.5f)) * (2048f * 100f / sizeFactor) - new Vector2(offsetX, offsetY);
        position = new(world.X, 0, world.Y);
        return float.IsFinite(position.X) && float.IsFinite(position.Z);
    }

    private void DrawMapFlagButton(bool available)
    {
        var height = ImGui.GetFrameHeight();
        ImGui.BeginDisabled(!available);
        if (ImGui.Button("###ADSExplorerPlaceMapFlag", new Vector2(height))) flagPlacementArmed = !flagPlacementArmed;
        ImGui.EndDisabled();
        var origin = ImGui.GetItemRectMin();
        var list = ImGui.GetWindowDrawList();
        var ink = ImGui.GetColorU32(MaterialTheme.Current.Colors.Error);
        list.AddLine(origin + new Vector2(.3f, .18f) * height, origin + new Vector2(.3f, .84f) * height,
            ink, Math.Max(1, MaterialTheme.Metrics.Scale));
        list.AddTriangleFilled(origin + new Vector2(.3f, .18f) * height, origin + new Vector2(.78f, .33f) * height,
            origin + new Vector2(.3f, .48f) * height, ink);
        if (flagPlacementArmed) list.AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), ink);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            MaterialText.SetTooltip(Ui.T(flagPlacementArmed
                ? "Click the map once to place a flag. Escape or right-click cancels."
                : "Place a map flag with one map click."));
    }

    internal static ObjectExplorerRow? PickMapMarker(IReadOnlyList<(ObjectExplorerRow Row, Vector2 Point)> markers,
        Vector2 mouse, float radius, ObjectExplorerRow? selected)
    {
        // Stable order lets repeated clicks cycle all overlapping markers.
        var candidates = markers.Where(marker => Vector2.DistanceSquared(marker.Point, mouse) <= radius * radius)
            .OrderBy(marker => Vector2.DistanceSquared(marker.Point, mouse)).ThenBy(marker => marker.Row.GameObjectId).ToList();
        if (candidates.Count == 0) return null;
        var current = selected is null ? -1 : candidates.FindIndex(marker => SameObject(marker.Row, selected));
        return candidates[(current + 1) % candidates.Count].Row;
    }

    internal static float MapPaneWidth(float availableWidth, float scale, float dividerSpace, float? preferredLogicalWidth)
        => Math.Clamp(preferredLogicalWidth.HasValue ? preferredLogicalWidth.Value * scale : Math.Min(420 * scale, availableWidth * .32f),
            240 * scale, Math.Max(240 * scale, availableWidth - 700 * scale - dividerSpace));

    private void DrawMap(DutyContextSnapshot context, IGameObject player, IReadOnlyList<ObjectExplorerRow> rows, Vector2 size)
    {
        var wasArmed = flagPlacementArmed;
        var visible = ImGui.BeginChild("ADSExplorerMap", size, true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        try
        {
            if (!visible) { flagPlacementArmed = false; return; }
            Map map;
            Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap? texture;
            try
            {
                if (context.CurrentMapId is not { } id || !Plugin.DataManager.GetExcelSheet<Map>(Ui.SheetLanguage).TryGetRow(id, out map)
                    || map.SizeFactor == 0)
                {
                    flagPlacementArmed = false;
                    MaterialText.TextWrapped(Ui.T("Map unavailable."));
                    return;
                }
                var signature = map.Id.ToString();
                if (string.IsNullOrWhiteSpace(signature))
                {
                    flagPlacementArmed = false;
                    MaterialText.TextWrapped(Ui.T("Map unavailable."));
                    return;
                }
                texture = Plugin.TextureProvider.GetFromGame($"ui/map/{signature}/{signature.Replace("/", string.Empty, StringComparison.Ordinal)}_m.tex").GetWrapOrDefault();
            }
            catch
            {
                flagPlacementArmed = false;
                MaterialText.TextWrapped(Ui.T("Map unavailable."));
                return;
            }
            var title = map.PlaceName.ValueNullable?.Name.ToString() ?? Ui.T("Map");
            var inlineControls = MaterialText.Measure(title).X + 4 * (ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X) <= ImGui.GetContentRegionAvail().X;
            MaterialText.TextWrapped(title);
            if (inlineControls) ImGui.SameLine();
            if (IconAction(Ui.L("Zoom in"), MaterialIcon.Plus, "Zoom in")) mapZoom = Math.Min(8, mapZoom * 1.25f);
            ImGui.SameLine();
            if (IconAction(Ui.L("Zoom out"), MaterialIcon.None, "Zoom out")) mapZoom = Math.Max(1, mapZoom / 1.25f);
            ImGui.SameLine();
            if (IconAction(Ui.L("Center on player"), MaterialIcon.Target, "Center on player")
                && TryMapUv(player.Position, map.SizeFactor, map.OffsetX, map.OffsetY, out var playerUv))
                mapCenter = Vector2.Clamp(playerUv, Vector2.Zero, Vector2.One);
            ImGui.SameLine();
            DrawMapFlagButton(texture != null && !context.IsUnsafeTransition);

            if (texture is null)
            {
                flagPlacementArmed = false;
                MaterialText.TextWrapped(Ui.T("Map texture is loading or unavailable."));
                return;
            }
            var legend = Ui.T("Character · Object · Treasure · You");
            var legendHeight = MaterialText.Measure(legend, wrapWidth: Math.Max(1, ImGui.GetContentRegionAvail().X)).Y + ImGui.GetStyle().ItemSpacing.Y;
            var canvasSize = new Vector2(Math.Max(1, ImGui.GetContentRegionAvail().X), Math.Max(1, ImGui.GetContentRegionAvail().Y - legendHeight));
            var min = ImGui.GetCursorScreenPos();
            var max = min + canvasSize;
            var center = min + canvasSize * .5f;
            var canvasClicked = ImGui.InvisibleButton("ADSExplorerMapCanvas", canvasSize);
            var hovered = ImGui.IsItemHovered();
            var mouse = ImGui.GetMousePos();
            var baseSide = Math.Min(canvasSize.X, canvasSize.Y);
            if (hovered && ImGui.GetIO().MouseWheel != 0)
            {
                var next = Math.Clamp(mapZoom * MathF.Pow(1.2f, ImGui.GetIO().MouseWheel), 1, 8);
                mapCenter += (mouse - center) * (1 / (baseSide * mapZoom) - 1 / (baseSide * next));
                mapZoom = next;
            }
            var side = baseSide * mapZoom;
            if (centerSelection && selectedObject is { } selectedRow
                && TryMapUv(selectedRow.Position, map.SizeFactor, map.OffsetX, map.OffsetY, out var selectedUv))
            {
                mapCenter = selectedUv;
                centerSelection = false;
            }
            if (!flagPlacementArmed && ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left)) mapCenter -= ImGui.GetIO().MouseDelta / side;
            mapCenter = Vector2.Clamp(mapCenter, Vector2.Zero, Vector2.One);
            var imageMin = center - mapCenter * side;
            var drawing = ImGui.GetWindowDrawList();
            var markers = new List<(ObjectExplorerRow Row, Vector2 Point)>();
            drawing.PushClipRect(min, max, true);
            try
            {
                drawing.AddImage(texture.Handle, imageMin, imageMin + new Vector2(side), Vector2.Zero, Vector2.One, ImGui.GetColorU32(Vector4.One));
                foreach (var row in rows)
                {
                    if (!TryMapUv(row.Position, map.SizeFactor, map.OffsetX, map.OffsetY, out var uv) || uv.X < 0 || uv.Y < 0 || uv.X > 1 || uv.Y > 1) continue;
                    var point = imageMin + uv * side;
                    if (point.X < min.X || point.Y < min.Y || point.X > max.X || point.Y > max.Y) continue;
                    markers.Add((row, point));
                    var color = row.ObjectKind == nameof(ObjectKind.Treasure) ? ImGui.GetColorU32(ImGuiCol.PlotHistogram)
                        : row.ObjectKind is nameof(ObjectKind.BattleNpc) or nameof(ObjectKind.Pc) ? ImGui.GetColorU32(ImGuiCol.CheckMark)
                        : ImGui.GetColorU32(ImGuiCol.TextDisabled);
                    drawing.AddCircleFilled(point, 5 * MaterialTheme.Metrics.Scale, color, 12);
                    drawing.AddCircle(point, 5 * MaterialTheme.Metrics.Scale, ImGui.GetColorU32(ImGuiCol.WindowBg), 12, MaterialTheme.Metrics.Scale);
                    if (!row.MatchesCurrentLayer) drawing.AddCircle(point, 7 * MaterialTheme.Metrics.Scale, ImGui.GetColorU32(ImGuiCol.TextDisabled), 12, MaterialTheme.Metrics.Scale);
                    if (selectedObject is { } selected && SameObject(row, selected))
                        drawing.AddCircle(point, 9 * MaterialTheme.Metrics.Scale, ImGui.GetColorU32(ImGuiCol.PlotHistogram), 20, 2 * MaterialTheme.Metrics.Scale);
                }
                if (TryMapUv(player.Position, map.SizeFactor, map.OffsetX, map.OffsetY, out var uvPlayer) && float.IsFinite(player.Rotation))
                {
                    var point = imageMin + uvPlayer * side;
                    var direction = new Vector2(MathF.Sin(player.Rotation), MathF.Cos(player.Rotation));
                    var normal = new Vector2(-direction.Y, direction.X);
                    var radius = 8 * MaterialTheme.Metrics.Scale;
                    drawing.AddTriangleFilled(point + direction * radius, point - direction * radius * .7f + normal * radius * .6f,
                        point - direction * radius * .7f - normal * radius * .6f, ImGui.GetColorU32(ImGuiCol.Text));
                }
            }
            finally { drawing.PopClipRect(); }
            if (hovered)
            {
                var hits = markers.Where(marker => Vector2.DistanceSquared(marker.Point, mouse) <= MathF.Pow(10 * MaterialTheme.Metrics.Scale, 2)).ToList();
                if (canvasClicked && ImGui.GetMouseDragDelta(ImGuiMouseButton.Left).LengthSquared() < 16)
                {
                    if (flagPlacementArmed)
                    {
                        flagPlacementArmed = false; // Consume before dispatch, including a refused placement.
                        if (!context.IsUnsafeTransition && TryMapWorldPosition(mouse, imageMin, side,
                            map.SizeFactor, map.OffsetX, map.OffsetY, out var position))
                            plugin.TryPlaceObjectFlag(Ui.T("Map"), position, map.RowId);
                    }
                    else if (PickMapMarker(markers, mouse, 10 * MaterialTheme.Metrics.Scale, selectedObject) is { } picked)
                        SelectObject(picked);
                }
                if (hits.Count > 0)
                {
                    ImGui.BeginTooltip();
                    try
                    {
                        foreach (var hit in hits.Take(8))
                        {
                            MaterialText.Text(Ui.T("{0} · {1:0.0}y · ΔY {2:0.0}", hit.Row.Name, hit.Row.Distance, hit.Row.Position.Y - player.Position.Y));
                            if (!hit.Row.MatchesCurrentLayer) MaterialText.Text(Ui.T("Matches current layer: {0}", Ui.T("NO")));
                        }
                        if (hits.Count > 1) MaterialText.Text(Ui.T("Click again to cycle overlapping markers."));
                    }
                    finally { ImGui.EndTooltip(); }
                }
            }
            if (wasArmed && flagPlacementArmed && ImGui.IsMouseReleased(ImGuiMouseButton.Left) && !hovered)
                flagPlacementArmed = false;
            MaterialText.TextWrapped(legend);
        }
        finally { ImGui.EndChild(); }
    }

    private void DrawFlagCoordinates()
    {
        if (!plugin.TryGetExplorerFlagCoordinates(out var worldXZ, out var worldXYZ))
            return;

        MaterialText.Text(Ui.T("Flag XYZ"));
        ImGui.SameLine();
        var coordinates = worldXYZ is { } xyz
            ? string.Create(CultureInfo.InvariantCulture, $"{xyz.X:R}, {xyz.Y:R}, {xyz.Z:R}")
            : string.Create(CultureInfo.InvariantCulture, $"{worldXZ.X:R}, ?, {worldXZ.Y:R}");
        ImGui.BeginDisabled(!worldXYZ.HasValue);
        if (MaterialText.Selectable(coordinates + "##ADSFlagXYZ", false, ImGuiSelectableFlags.None,
                new Vector2(MaterialText.Measure(coordinates).X + ImGui.GetStyle().FramePadding.X * 2, 0)))
            ImGui.SetClipboardText(coordinates);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            MaterialText.SetTooltip(Ui.T(worldXYZ.HasValue ? "Click to copy flag XYZ." : "Flag height is not resolved."));

        ImGui.SameLine();
        if (WindowLayout.SmallButton(Ui.L("Copy X/Z##ADSFlagXZ")))
            ImGui.SetClipboardText(string.Create(CultureInfo.InvariantCulture, $"{worldXZ.X:R}, {worldXZ.Y:R}"));
        if (ImGui.IsItemHovered())
            MaterialText.SetTooltip(Ui.T("Copy flag X/Z only; no height is included."));
    }

    private static void DrawHeaderRow()
    {
        const string sortHelp = "Click: sort; Shift+click: combine.";
        const string actionHelp = "Use the row button.";
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers, WindowLayout.HeaderRowHeight());
        DrawHeaderCell(0, "Name", "Object name.", sortHelp);
        DrawHeaderCell(1, "Kind", "Object kind.", sortHelp);
        DrawHeaderCell(2, "Lv.", "Character level, when available.", sortHelp);
        DrawHeaderCell(3, "f.Lv", "Foray level, when available.", sortHelp);
        DrawHeaderCell(4, "Element", "Foray element, when available.", sortHelp);
        DrawHeaderCell(5, "Dist", "3D distance from you, in yalms.", sortHelp);
        DrawHeaderCell(6, "Y", "Absolute height difference, in yalms.", sortHelp);
        DrawHeaderCell(7, "Rules", "Rule matches before layer filtering.", sortHelp);
        DrawHeaderCell(8, "moveto", "Move to the object's position.", actionHelp, MaterialIcon.Person);
        DrawHeaderCell(9, "flyto", "Fly to the object's position.", actionHelp, MaterialIcon.Send);
        DrawHeaderCell(10, "FLAG", "Place a map flag at the object.", actionHelp, MaterialIcon.MapPin);
        DrawHeaderCell(11, "RULE", "Seed an object rule; choose its class.", actionHelp, MaterialIcon.Document);
        DrawHeaderCell(12, "Copy XYZ", "Copy the object's XYZ coordinates.", actionHelp, MaterialIcon.Copy);
        DrawHeaderCell(13, "DhogNav", "Add the player to DhogNav's whitelist.", actionHelp, MaterialIcon.Star);
        DrawHeaderCell(14, "Teleport", "Teleport to this object through DNav.", actionHelp, MaterialIcon.Location);
    }

    private static void DrawHeaderCell(int column, string label, string purpose, string interaction, MaterialIcon icon = MaterialIcon.None)
    {
        if (!ImGui.TableSetColumnIndex(column))
            return;

        if (icon != MaterialIcon.None) ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        try { WindowLayout.TableHeader(Ui.L(label)); }
        finally { if (icon != MaterialIcon.None) ImGui.PopStyleColor(); }
        if (icon != MaterialIcon.None)
        {
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var size = Math.Min(18 * MaterialTheme.Metrics.Scale, max.Y - min.Y);
            MaterialIcons.Draw(icon, min + (max - min - new Vector2(size)) * .5f, size,
                ImGui.GetStyle().Colors[(int)ImGuiCol.Text], ImGui.GetStyle().Alpha);
        }
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        try {
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28f);
        try {
        MaterialText.Text(Ui.T("{0}\n{1}", Ui.T(purpose), Ui.T(interaction)));

        } finally { ImGui.PopTextWrapPos(); }

        } finally { ImGui.EndTooltip(); }
    }

    private static unsafe void SortTableRows(List<ObjectExplorerRow> rows)
    {
        var specs = ImGui.TableGetSortSpecs();
        var criteria = new SortCriterion[specs.SpecsCount];
        for (var index = 0; index < criteria.Length; index++)
        {
            var spec = specs.Specs[index];
            criteria[spec.SortOrder] = new SortCriterion((SortColumn)spec.ColumnIndex, spec.SortDirection == ImGuiSortDirection.Descending);
        }

        // Rows are a fresh snapshot: reapply priorities even without a header click.
        SortRows(rows, criteria);
        specs.SpecsDirty = false;
    }

    internal static void SortRows(List<ObjectExplorerRow> rows, IReadOnlyList<SortCriterion> criteria)
        => rows.Sort((left, right) => CompareRows(left, right, criteria));

    internal static int CompareRows(ObjectExplorerRow left, ObjectExplorerRow right, IReadOnlyList<SortCriterion> criteria)
    {
        for (var index = 0; index < criteria.Count; index++)
        {
            var criterion = criteria[index];
            var leftMissing = IsMissingSortValue(left, criterion.Column);
            var rightMissing = IsMissingSortValue(right, criterion.Column);
            if (leftMissing != rightMissing)
                return leftMissing ? 1 : -1;

            var result = criterion.Column switch
            {
                SortColumn.Name => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name),
                SortColumn.Kind => StringComparer.OrdinalIgnoreCase.Compare(left.ObjectKind, right.ObjectKind),
                SortColumn.Level => Nullable.Compare(left.Level, right.Level),
                SortColumn.ForayLevel => Nullable.Compare(left.ForayLevel, right.ForayLevel),
                SortColumn.Element => StringComparer.OrdinalIgnoreCase.Compare(FormatForayElement(left.ForayElement), FormatForayElement(right.ForayElement)),
                SortColumn.Distance => left.Distance.CompareTo(right.Distance),
                SortColumn.VerticalDelta => left.VerticalDelta.CompareTo(right.VerticalDelta),
                SortColumn.Rules => left.MatchingRules.Count.CompareTo(right.MatchingRules.Count),
                _ => 0,
            };
            if (result != 0)
                return criterion.Descending ? -result : result;
        }

        var distance = left.Distance.CompareTo(right.Distance);
        if (distance != 0)
            return distance;

        var name = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        return name != 0 ? name : left.GameObjectId.CompareTo(right.GameObjectId);
    }

    private static bool IsMissingSortValue(ObjectExplorerRow row, SortColumn column)
        => column switch
        {
            SortColumn.Level => row.Level is null,
            SortColumn.ForayLevel => row.ForayLevel is null,
            SortColumn.Element => row.ForayElement is null,
            _ => false,
        };

    // Match the eight sortable table column indexes; action columns follow them.
    internal enum SortColumn
    {
        Name,
        Kind,
        Level,
        ForayLevel,
        Element,
        Distance,
        VerticalDelta,
        Rules,
    }

    internal readonly record struct SortCriterion(SortColumn Column, bool Descending = false);

    private void DrawExportControls()
    {
        if (WindowLayout.SmallButton(Ui.L("Export All JSON")))
            plugin.ExportExplorerSnapshot();

        if (ImGui.IsItemHovered())
            MaterialText.SetTooltip(Ui.T("Exports all buffered server events and every loaded object-table entry, independent of viewer filters."));

        ImGui.SameLine();
        if (WindowLayout.SmallButton(Ui.L("Open Export Folder")))
        {
            Directory.CreateDirectory(plugin.ExplorerSnapshotExportService.ExportDirectory);
            plugin.OpenPath(plugin.ExplorerSnapshotExportService.ExportDirectory);
        }

        ImGui.SameLine();
        WindowLayout.Checkbox("Compact", ref compact);

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
                MatchingRules: matchingRules,
                GameObjectAddress: gameObject.Address);
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
        if (selectedKind == "Player") selectedKind = nameof(ObjectKind.Pc);
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
        => Ui.T(element switch
        {
            null => "—",
            1 => "Fire",
            2 => "Ice",
            3 => "Wind",
            4 => "Earth",
            5 => "Lightning",
            6 => "Water",
            _ => element.Value.ToString(),
        });

    private void DrawRulePopup(ulong objectId, ObjectExplorerRow? row)
    {
        if (!ImGui.BeginPopup($"ADSObjectRulePopup##{objectId}"))
        {
            rulePopupObjectId = null;
            return;
        }

        // Keep the popup tied to its object, and retire it if that row disappears.
        if (row is null)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            rulePopupObjectId = null;
            return;
        }

        MaterialText.Text(Ui.T("Seed rule with"));
        ImGui.Separator();
        for (var optionIndex = 0; optionIndex < ruleClassificationOptions.Length; optionIndex++)
        {
            var option = ruleClassificationOptions[optionIndex];
            if (MaterialText.Selectable(Ui.L(option)))
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
        try {
        MaterialText.Text(Ui.T(row.MatchingRules.Count == 0 ? "No matching rules." : "Matching rules"));
        if (row.MatchingRules.Count > 0)
        {
            foreach (var rule in row.MatchingRules.Take(8))
            {
                var type = string.IsNullOrWhiteSpace(rule.Classification) ? "(blank)" : rule.Classification;
                var scope = plugin.ObjectPriorityRuleService.DescribeRuleScope(rule);
                MaterialText.Text(Ui.T("{0} | pri {1} | {2}", type, rule.Priority, scope));
            }

            if (row.MatchingRules.Count > 8)
                MaterialText.Text(Ui.T("... {0} more", row.MatchingRules.Count - 8));
        }


        } finally { ImGui.EndTooltip(); }
    }

    private static void DrawRowTooltip(ObjectExplorerRow row)
    {
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        try {
        MaterialText.Text(row.Name);
        MaterialText.Text(Ui.T("ObjectKind: {0}", row.ObjectKind));
        MaterialText.Text(Ui.T("Level: {0}", row.Level?.ToString() ?? "—"));
        MaterialText.Text(Ui.T("Foray level: {0}", row.ForayLevel?.ToString() ?? "—"));
        MaterialText.Text(Ui.T("Foray element: {0}", FormatForayElement(row.ForayElement)));
        MaterialText.Text(Ui.T("Distance: {0:0.00}", row.Distance));
        MaterialText.Text(Ui.T("Y delta: {0:0.00}", row.VerticalDelta));
        MaterialText.Text(Ui.T("BaseId: {0}", row.BaseId));
        MaterialText.Text(Ui.T("GameObjectId: {0}", row.GameObjectId));
        MaterialText.Text(Ui.T("Targetable: {0}", Ui.T(row.IsTargetable ? "YES" : "NO")));
        MaterialText.Text(Ui.T("Matches current layer: {0}", Ui.T(row.MatchesCurrentLayer ? "YES" : "NO")));
        MaterialText.Text(Ui.T("Position: {0:0.00}, {1:0.00}, {2:0.00}", row.Position.X, row.Position.Y, row.Position.Z));

        } finally { ImGui.EndTooltip(); }
    }

    internal sealed record ObjectExplorerRow(
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
        IReadOnlyList<ObjectPriorityRule> MatchingRules,
        nint GameObjectAddress = 0);
}
