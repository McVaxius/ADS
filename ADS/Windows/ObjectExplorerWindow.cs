using AethertekUI;
using ADS.Localization;
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
        DrawExportControls();
        MaterialText.TextWrapped(Ui.T("Action status: {0}", Ui.Display(plugin.ObjectExplorerStatus)));

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var context = plugin.DutyContextService.Current;
        if (!compact)
            MaterialText.Text(Ui.T("Territory / Map / CFC: {0} / {1} / {2}", context.TerritoryTypeId, context.MapId, context.ContentFinderConditionId));
        MaterialText.Text(Ui.T("Current map ID: {0}", (localPlayer is null ? "Unavailable" : context.CurrentMapId?.ToString() ?? "Unavailable")));
        if (localPlayer is null)
        {
            rulePopupObjectId = null;
            if (!compact)
            {
                MaterialText.TextWrapped(Ui.T("Flag status: {0}", Ui.Display(plugin.ObjectExplorerMapFlagStatus)));
                MaterialText.Text(Ui.T("No local player is available."));
            }
            return;
        }

        var activeLayer = plugin.ObjectPriorityRuleService.GetActiveLayerName(context) ?? "Unknown";
        var nearestFrontierLabel = plugin.DungeonFrontierService.CurrentLabelMarkers
            .OrderBy(x => Vector3.Distance(localPlayer.Position, x.WorldPosition))
            .FirstOrDefault();

        if (!compact)
        {
            MaterialText.Text(Ui.T("Live Loaded Objects"));
            MaterialText.TextWrapped(Ui.T("Operator-first object table. Rules column shows all rule hits before live layer filtering. Same-map-only is best-effort: ADS hides rows that only match off-layer scoped rules and keeps rows with no map-layer evidence."));
            MaterialText.Text(Ui.T("Layer / Sub-area: {0}", activeLayer));
            MaterialText.Text(Ui.T("Nearest frontier label: {0}", (nearestFrontierLabel is null ? "None" : $"{nearestFrontierLabel.Name} ({Vector3.Distance(localPlayer.Position, nearestFrontierLabel.WorldPosition):0.0}y)")));
            MaterialText.TextWrapped(Ui.T("Frontier target: {0}", plugin.DungeonFrontierService.CurrentTarget?.Name ?? "None"));
            MaterialText.TextWrapped(Ui.T("Flag status: {0}", Ui.Display(plugin.ObjectExplorerMapFlagStatus)));
        }

        MaterialText.Text(Ui.T("Search"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        WindowLayout.InputTextWithHint("##ADSObjectTextFilter", Ui.T("name / base id / object kind; | for OR"), ref textFilter, 128);
        MaterialText.Text(Ui.T("Kind"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180f);
        WindowLayout.Combo("##ADSObjectKindFilter", ref objectKindFilterIndex, objectKindFilters.Select(Ui.Display).ToArray(), objectKindFilters.Length);
        ImGui.SameLine();
        if (WindowLayout.Button(Ui.L("Clear filters")))
            ClearFilters();

        if (WindowLayout.Checkbox("Filter by Lv.", ref levelFilterEnabled) && levelFilterEnabled && levelFilter <= 0)
            levelFilter = localPlayer.Level;

        ImGui.SameLine();
        ImGui.BeginDisabled(!levelFilterEnabled);
        ImGui.SetNextItemWidth(72f);
        WindowLayout.InputInt(WindowLayout.InputLabel("Lv.##ADSObjectLevelFilter"), ref levelFilter, 0, 0);
        if (levelFilterEnabled)
            levelFilter = Math.Max(1, levelFilter);
        ImGui.SameLine();
        WindowLayout.RadioButton(Ui.L("Exact##ADSObjectLevelFilter"), ref levelFilterMode, 0);
        ImGui.SameLine();
        WindowLayout.RadioButton(Ui.L("<=##ADSObjectLevelFilter"), ref levelFilterMode, 1);
        ImGui.SameLine();
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

        var rows = BuildRows(context, localPlayer)
            .Where(MatchesFilter)
            .ToList();

        if (!compact)
            MaterialText.Text(Ui.T("Objects shown: {0}", rows.Count));
        var whitelistAvailable = plugin.DhogNavWhitelistAvailable;
        if (!ImGui.BeginTable("ADSObjectExplorerTable", 14, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Resizable | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortMulti, new Vector2(-1f, -1f)))
            return;

        ImGui.TableSetupColumn(Ui.L("Name"));
        ImGui.TableSetupColumn(Ui.L("Kind"));
        ImGui.TableSetupColumn(Ui.L("Lv."));
        ImGui.TableSetupColumn(Ui.L("f.Lv"));
        ImGui.TableSetupColumn(Ui.L("Element"));
        ImGui.TableSetupColumn(Ui.L("Dist"), ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortAscending);
        ImGui.TableSetupColumn(Ui.L("Y"));
        ImGui.TableSetupColumn(Ui.L("Rules"));
        ImGui.TableSetupColumn(Ui.L("moveto"), ImGuiTableColumnFlags.NoSort);
        ImGui.TableSetupColumn(Ui.L("flyto"), ImGuiTableColumnFlags.NoSort);
        ImGui.TableSetupColumn(Ui.L("FLAG"), ImGuiTableColumnFlags.NoSort);
        ImGui.TableSetupColumn(Ui.L("RULE"), ImGuiTableColumnFlags.NoSort);
        ImGui.TableSetupColumn(Ui.L("Copy XYZ"), ImGuiTableColumnFlags.NoSort);
        ImGui.TableSetupColumn(Ui.L("DhogNav"), ImGuiTableColumnFlags.NoSort | (whitelistAvailable ? ImGuiTableColumnFlags.None : ImGuiTableColumnFlags.Disabled));
        ImGui.TableSetupScrollFreeze(1, 1);
        DrawHeaderRow();
        SortTableRows(rows);

        foreach (var row in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            MaterialText.Text(row.Name);
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(1);
            MaterialText.Text(row.ObjectKind);
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(2);
            MaterialText.Text(row.Level?.ToString() ?? "—");
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(3);
            MaterialText.Text(row.ForayLevel?.ToString() ?? "—");
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(4);
            MaterialText.Text(FormatForayElement(row.ForayElement));
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(5);
            MaterialText.Text(row.Distance.ToString("0.00"));
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(6);
            MaterialText.Text(row.VerticalDelta.ToString("0.00"));
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(7);
            MaterialText.Text(row.MatchingRules.Count.ToString());
            DrawRuleTooltip(row);

            ImGui.TableSetColumnIndex(8);
            if (WindowLayout.SmallButton(Ui.L("moveto##ADSObjectMove{0}", row.GameObjectId)))
                plugin.TryExplorerNavigation(row.Position, useFly: false);

            ImGui.TableSetColumnIndex(9);
            if (WindowLayout.SmallButton(Ui.L("flyto##ADSObjectFly{0}", row.GameObjectId)))
                plugin.TryExplorerNavigation(row.Position, useFly: true);

            ImGui.TableSetColumnIndex(10);
            if (WindowLayout.SmallButton(Ui.L("FLAG##ADSObjectFlag{0}", row.GameObjectId)))
                plugin.TryPlaceObjectFlag(row.Name, row.Position);

            ImGui.TableSetColumnIndex(11);
            if (WindowLayout.SmallButton(Ui.L("RULE##ADSObjectRule{0}", row.GameObjectId)))
            {
                rulePopupObjectId = row.GameObjectId;
                ImGui.OpenPopup($"ADSObjectRulePopup##{row.GameObjectId}");
            }

            ImGui.TableSetColumnIndex(12);
            if (WindowLayout.SmallButton(Ui.L("XYZ##ADSObjectCopy{0}", row.GameObjectId)))
                ImGui.SetClipboardText($"{row.Position.X:0.00}, {row.Position.Y:0.00}, {row.Position.Z:0.00}");

            if (whitelistAvailable && row.ObjectKind == nameof(ObjectKind.Pc))
            {
                ImGui.TableSetColumnIndex(13);
                if (WindowLayout.SmallButton(Ui.L("Whitelist##ADSObjectWhitelist{0}", row.GameObjectId)))
                    plugin.TryWhitelistExplorerPlayer(row.GameObjectId);
                if (ImGui.IsItemHovered())
                    MaterialText.SetTooltip(Ui.T("Add this player and their home server to DhogNav's whitelist. DhogNav must be loaded; parasite mode may be off. Finish or cancel any whitelist edit first."));
            }
        }

        if (rulePopupObjectId is { } objectId)
            DrawRulePopup(objectId, rows.Find(row => row.GameObjectId == objectId));

        ImGui.EndTable();
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
        DrawHeaderCell(8, "moveto", "Move to the object's position.", actionHelp);
        DrawHeaderCell(9, "flyto", "Fly to the object's position.", actionHelp);
        DrawHeaderCell(10, "FLAG", "Place a map flag at the object.", actionHelp);
        DrawHeaderCell(11, "RULE", "Seed an object rule; choose its class.", actionHelp);
        DrawHeaderCell(12, "Copy XYZ", "Copy the object's XYZ coordinates.", actionHelp);
        DrawHeaderCell(13, "DhogNav", "Add the player to DhogNav's whitelist.", actionHelp);
    }

    private static void DrawHeaderCell(int column, string label, string purpose, string interaction)
    {
        if (!ImGui.TableSetColumnIndex(column))
            return;

        WindowLayout.TableHeader(Ui.L(label));
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        try {
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28f);
        try {
        MaterialText.Text(Ui.T("{0}\n{1}", purpose, interaction));

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

        if (!compact)
            MaterialText.TextWrapped(Ui.T("Export status: {0}", Ui.Display(plugin.ExplorerSnapshotExportService.Status)));
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
        IReadOnlyList<ObjectPriorityRule> MatchingRules);
}
