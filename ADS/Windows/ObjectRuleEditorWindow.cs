using System.Numerics;
using System.Text;
using System.Text.Json;
using ADS.Models;
using ADS.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class ObjectRuleEditorWindow : PositionedWindow, IDisposable
{
    private static readonly string[] NameMatchModes =
    [
        "Exact",
        "Contains",
    ];

    private static readonly string[] ClassificationLabels =
    [
        "(none)",
        "Ignored",
        "Follow",
        "BossFight",
        "Required",
        "Optional",
        "Expendable",
        "CombatFriendly",
        "TreasureCoffer",
        "TreasureDoor",
        "MapXzDestination",
        "XYZ",
        "MapXzForceMarch",
        "XYZForceMarch",
    ];

    private static readonly string[] ClassificationValues =
    [
        "",
        "Ignored",
        "Follow",
        "BossFight",
        "Required",
        "Optional",
        "Expendable",
        "CombatFriendly",
        "TreasureCoffer",
        "TreasureDoor",
        "MapXzDestination",
        "XYZ",
        "MapXzForceMarch",
        "XYZForceMarch",
    ];

    private static readonly string[] ObjectKindLabels = BuildObjectKindLabels();

    private readonly Plugin plugin;
    private readonly HashSet<ObjectPriorityRule> unsavedNewRules = [];
    private ObjectPriorityRuleManifest draft = new();
    private bool draftLoaded;
    private bool dirty;
    private bool filterCurrentAreaAndGlobal;
    private bool sortByDutyName = true;
    private string selectedPresetName = ObjectPriorityRuleService.DefaultPresetName;
    private string pendingPresetName = string.Empty;
    private string diskTransferPath = string.Empty;
    private string dutySearch = string.Empty;
    private int dutySearchRow = -1;
    private ObjectPriorityRule? pendingScrollRule;
    private string editorStatus = "Rules not loaded.";

    public ObjectRuleEditorWindow(Plugin plugin)
        : base("ADS Rules Editor###ADSRulesEditor")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(1680f, 560f),
            MaximumSize = new Vector2(3600f, 2400f),
        };
        Size = new Vector2(3000f, 1040f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();
        EnsureDraftLoaded();

        ImGui.TextWrapped("Spreadsheet-style editor for duty-object-rules.json. Use the duty dropdown for catalog duties, leave it on GLOBAL for wildcard rows, and use row base64 export/import for quick duplication or sharing.");
        ImGui.TextWrapped("Coords is now the single coordinate field. Enter `a,b` for map X,Z and `a,b,c` for world X,Y,Z. On manual destination rows, Coords drives MapXzDestination / MapXzForceMarch versus XYZ / XYZForceMarch. ForceMarch rows are authored inside-duty bypass waypoints that can stay committed through incidental combat; they are not Praetorium-mounted-only rows. On ordinary rows, Coords is the positional selector and R is its optional radius. BaseId is the stable sheet/base object id, not the per-instance GameObjectId.");
        ImGui.TextWrapped("Wait-before holds after ADS arrives in interact range and before the first interact send. Wait-after holds after a successful interact send before ADS retries or moves on.");
        ImGui.TextWrapped($"Preset: {selectedPresetName} -> {plugin.ObjectPriorityRuleService.GetPresetPath(selectedPresetName)}");
        if (!plugin.ObjectPriorityRuleService.IsDefaultPreset(selectedPresetName))
            ImGui.TextWrapped("Runtime ADS still reads DEFAULT/live rules. Non-default presets are parked datasets until you import or copy them back into DEFAULT.");
        ImGui.TextWrapped(editorStatus);
        if (dirty)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.97f, 0.84f, 0.31f, 1f));
            ImGui.TextUnformatted("Unsaved rule edits");
            ImGui.PopStyleColor();
        }

        DrawToolbar();
        ImGui.Spacing();
        DrawRulesTable();
    }

    private void DrawToolbar()
    {
        DrawPresetToolbar();

        ImGui.SameLine();
        if (ImGui.Button("+ Row"))
        {
            AddDraftRule(plugin.ObjectPriorityRuleService.CreateBlankRule(), "Added a new blank rule row.");
        }

        ImGui.SameLine();
        using (new ImGuiDisabledBlock(!dirty))
        {
            if (ImGui.Button("Save"))
            {
                if (plugin.ObjectPriorityRuleService.SaveManifest(selectedPresetName, draft))
                {
                    RefreshDraft($"Saved preset {selectedPresetName}.");
                }
                else
                {
                    editorStatus = plugin.ObjectPriorityRuleService.LastLoadStatus;
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Reload From Disk"))
        {
            RefreshDraft($"Reloaded preset {selectedPresetName} from disk.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Open JSON"))
            plugin.OpenPath(plugin.ObjectPriorityRuleService.GetPresetPath(selectedPresetName));

        ImGui.SameLine();
        ImGui.Checkbox("Current Area + Global", ref filterCurrentAreaAndGlobal);

        ImGui.SameLine();
        ImGui.Checkbox("Sort By Duty", ref sortByDutyName);

        var rowsShown = BuildVisibleRuleIndices().Count;
        ImGui.TextUnformatted($"Rows shown: {rowsShown} / {draft.Rules.Count}");
    }

    private void DrawPresetToolbar()
    {
        ImGui.TextUnformatted("Preset");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220f);
        if (ImGui.BeginCombo("##RulePreset", selectedPresetName))
        {
            foreach (var presetName in plugin.ObjectPriorityRuleService.GetPresetNames())
            {
                var isSelected = string.Equals(presetName, selectedPresetName, StringComparison.OrdinalIgnoreCase);
                if (!ImGui.Selectable(presetName, isSelected))
                    continue;

                SwitchPreset(presetName);
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Export"))
            ExportManifestToClipboard();

        ImGui.SameLine();
        if (ImGui.SmallButton("Import"))
            ImportManifestFromClipboard();

        ImGui.SameLine();
        if (ImGui.SmallButton("Disk+"))
        {
            SyncDiskTransferPath();
            ImGui.OpenPopup("ADSPresetDiskTransfer");
        }

        DrawDiskTransferPopup();

        ImGui.SameLine();
        if (ImGui.SmallButton("+"))
        {
            pendingPresetName = plugin.ObjectPriorityRuleService.IsDefaultPreset(selectedPresetName)
                ? "Preset"
                : plugin.ObjectPriorityRuleService.SanitizePresetName(selectedPresetName);
            ImGui.OpenPopup("ADSCreatePreset");
        }

        DrawCreatePresetPopup();

        ImGui.SameLine();
        using (new ImGuiDisabledBlock(plugin.ObjectPriorityRuleService.IsDefaultPreset(selectedPresetName)))
        {
            if (ImGui.SmallButton("-"))
                DeleteCurrentPreset();
        }

        if (plugin.ObjectPriorityRuleService.IsDefaultPreset(selectedPresetName))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("@"))
                ResetDefaultDraftFromBundled();
        }
    }

    private void EnsureDraftLoaded()
    {
        if (draftLoaded)
            return;

        RefreshDraft($"Loaded preset {selectedPresetName}.");
    }

    private void RefreshDraft(string status)
    {
        if (!plugin.ObjectPriorityRuleService.TryLoadManifest(selectedPresetName, out draft, out var loadStatus))
        {
            draft = new ObjectPriorityRuleManifest();
            editorStatus = loadStatus;
            draftLoaded = true;
            dirty = false;
            unsavedNewRules.Clear();
            pendingScrollRule = null;
            SyncDiskTransferPath();
            return;
        }

        draftLoaded = true;
        dirty = false;
        dutySearch = string.Empty;
        dutySearchRow = -1;
        unsavedNewRules.Clear();
        pendingScrollRule = null;
        editorStatus = $"{status} {loadStatus}";
        SyncDiskTransferPath();
    }

    public void CreateRuleFromExplorer(ObjectPriorityRule seededRule)
    {
        EnsureDraftLoaded();
        AddDraftRule(seededRule, $"Seeded a new rule from Object Explorer into preset {selectedPresetName}.");
        IsOpen = true;
    }

    private void DrawRulesTable()
    {
        const ImGuiTableFlags tableFlags =
            ImGuiTableFlags.Borders
            | ImGuiTableFlags.RowBg
            | ImGuiTableFlags.ScrollX
            | ImGuiTableFlags.ScrollY
            | ImGuiTableFlags.Resizable
            | ImGuiTableFlags.SizingFixedFit;

        if (!ImGui.BeginTable("ADSRulesEditorTable", 21, tableFlags, new Vector2(-1f, -1f)))
            return;

        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 40f);
        ImGui.TableSetupColumn("Duty", ImGuiTableColumnFlags.WidthFixed, 260f);
        ImGui.TableSetupColumn("Terr", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("CFC", ImGuiTableColumnFlags.WidthFixed, 48f);
        ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 110f);
        ImGui.TableSetupColumn("BaseId", ImGuiTableColumnFlags.WidthFixed, 88f);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, 260f);
        ImGui.TableSetupColumn("Match", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("Class", ImGuiTableColumnFlags.WidthFixed, 170f);
        ImGui.TableSetupColumn("Layer", ImGuiTableColumnFlags.WidthFixed, 130f);
        ImGui.TableSetupColumn("Coords", ImGuiTableColumnFlags.WidthFixed, 190f);
        ImGui.TableSetupColumn("R", ImGuiTableColumnFlags.WidthFixed, 76f);
        ImGui.TableSetupColumn("Pri", ImGuiTableColumnFlags.WidthFixed, 88f);
        ImGui.TableSetupColumn("Y", ImGuiTableColumnFlags.WidthFixed, 88f);
        ImGui.TableSetupColumn("Dist", ImGuiTableColumnFlags.WidthFixed, 88f);
        ImGui.TableSetupColumn("Wait-before", ImGuiTableColumnFlags.WidthFixed, 92f);
        ImGui.TableSetupColumn("Wait-after", ImGuiTableColumnFlags.WidthFixed, 92f);
        ImGui.TableSetupColumn("Notes", ImGuiTableColumnFlags.WidthStretch, 420f);
        ImGui.TableSetupColumn("Copy", ImGuiTableColumnFlags.WidthFixed, 52f);
        ImGui.TableSetupColumn("Paste", ImGuiTableColumnFlags.WidthFixed, 56f);
        ImGui.TableSetupColumn("-", ImGuiTableColumnFlags.WidthFixed, 30f);
        ImGui.TableSetupScrollFreeze(0, 1);
        DrawHeaderRow();

        var rowToRemove = -1;
        foreach (var ruleIndex in BuildVisibleRuleIndices())
        {
            var rule = draft.Rules[ruleIndex];
            ImGui.PushID(ruleIndex);
            ImGui.TableNextRow();
            if (unsavedNewRules.Contains(rule))
            {
                var rowHighlight = ImGui.ColorConvertFloat4ToU32(new Vector4(0.20f, 0.34f, 0.18f, 0.65f));
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, rowHighlight);
            }

            ImGui.TableSetColumnIndex(0);
            var enabled = rule.Enabled;
            if (ImGui.Checkbox("##Enabled", ref enabled))
            {
                rule.Enabled = enabled;
                dirty = true;
            }
            if (ReferenceEquals(rule, pendingScrollRule))
            {
                ImGui.SetScrollHereY(0.25f);
                pendingScrollRule = null;
            }

            ImGui.TableSetColumnIndex(1);
            if (DrawDutyCell(ruleIndex, rule))
                dirty = true;

            ImGui.TableSetColumnIndex(2);
            if (EditUintCell("##TerritoryTypeId", rule.TerritoryTypeId, out var territoryTypeId))
            {
                rule.TerritoryTypeId = territoryTypeId;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(3);
            if (EditUintCell("##ContentFinderConditionId", rule.ContentFinderConditionId, out var contentFinderConditionId))
            {
                rule.ContentFinderConditionId = contentFinderConditionId;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(4);
            if (DrawObjectKindCell(rule, ruleIndex))
                dirty = true;

            ImGui.TableSetColumnIndex(5);
            if (EditUintCell("##BaseId", rule.BaseId, out var baseId))
            {
                rule.BaseId = baseId;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(6);
            if (EditTextCell("##ObjectName", rule.ObjectName, 128, out var objectName))
            {
                rule.ObjectName = objectName;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(7);
            var matchModeIndex = Math.Max(0, Array.IndexOf(NameMatchModes, string.IsNullOrWhiteSpace(rule.NameMatchMode) ? "Exact" : rule.NameMatchMode));
            if (ImGui.Combo("##NameMatchMode", ref matchModeIndex, NameMatchModes, NameMatchModes.Length))
            {
                rule.NameMatchMode = NameMatchModes[matchModeIndex];
                dirty = true;
            }

            ImGui.TableSetColumnIndex(8);
            var classificationIndex = Math.Max(0, Array.IndexOf(ClassificationValues, rule.Classification ?? string.Empty));
            if (ImGui.Combo("##Classification", ref classificationIndex, ClassificationLabels, ClassificationLabels.Length))
            {
                rule.Classification = ClassificationValues[classificationIndex];
                dirty = true;
            }

            ImGui.TableSetColumnIndex(9);
            if (DrawLayerCell(rule, ruleIndex))
            {
                dirty = true;
            }

            ImGui.TableSetColumnIndex(10);
            var unifiedCoordinates = GetUnifiedCoordinatesValue(rule);
            if (EditTextCell("##Coords", unifiedCoordinates, 48, out var editedCoordinates))
            {
                SetUnifiedCoordinatesValue(rule, editedCoordinates);
                dirty = true;
            }

            ImGui.TableSetColumnIndex(11);
            using (new ImGuiDisabledBlock(IsManualDestinationRule(rule)))
            {
                if (EditNullableFloatCell("##ObjectMatchRadius", rule.ObjectMatchRadius, out var objectMatchRadius))
                {
                    rule.ObjectMatchRadius = objectMatchRadius;
                    dirty = true;
                }
            }

            ImGui.TableSetColumnIndex(12);
            if (EditIntCell("##Priority", rule.Priority, out var priority))
            {
                rule.Priority = priority;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(13);
            if (EditFloatCell("##PriorityVerticalRadius", rule.PriorityVerticalRadius, out var priorityVerticalRadius))
            {
                rule.PriorityVerticalRadius = priorityVerticalRadius;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(14);
            if (EditNullableFloatCell("##MaxDistance", rule.MaxDistance, out var maxDistance))
            {
                rule.MaxDistance = maxDistance;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(15);
            if (EditFloatCell("##WaitAtDestinationSeconds", rule.WaitAtDestinationSeconds, out var waitAtDestinationSeconds))
            {
                rule.WaitAtDestinationSeconds = waitAtDestinationSeconds;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(16);
            if (EditFloatCell("##WaitAfterInteractSeconds", rule.WaitAfterInteractSeconds, out var waitAfterInteractSeconds))
            {
                rule.WaitAfterInteractSeconds = waitAfterInteractSeconds;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(17);
            if (EditTextCell("##Notes", rule.Notes, 512, out var notes))
            {
                rule.Notes = notes;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(18);
            if (ImGui.SmallButton("B64"))
                ExportRuleAsBase64(rule);

            ImGui.TableSetColumnIndex(19);
            if (ImGui.SmallButton("Paste") && ImportRuleFromClipboard(ruleIndex))
                dirty = true;

            ImGui.TableSetColumnIndex(20);
            if (ImGui.SmallButton("-"))
                rowToRemove = ruleIndex;

            ImGui.PopID();
        }

        if (rowToRemove >= 0)
        {
            unsavedNewRules.Remove(draft.Rules[rowToRemove]);
            draft.Rules.RemoveAt(rowToRemove);
            dutySearch = string.Empty;
            dutySearchRow = -1;
            dirty = true;
        }

        ImGui.EndTable();
    }

    private void DrawHeaderRow()
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        DrawHeaderCell(0, "On", "Enable or disable this row without deleting it.");
        DrawHeaderCell(1, "Duty", "Catalog duty selector. GLOBAL leaves Duty/Terr/CFC wild so the row can match any duty.");
        DrawHeaderCell(2, "Terr", "TerritoryTypeId scope. Auto-filled from the duty dropdown. Zero means wildcard.");
        DrawHeaderCell(3, "CFC", "ContentFinderConditionId scope. Auto-filled from the duty dropdown. Zero means wildcard.");
        DrawHeaderCell(4, "Kind", "Live ObjectKind match. Use blank for wildcard. This is the game object category, not a unique instance id.");
        DrawHeaderCell(5, "BaseId", "Stable base sheet/object id. Useful when names collide, but not unique per live spawn. This is not GameObjectId.");
        DrawHeaderCell(6, "Name", "Object name text to match. Leave blank for any object name inside the rest of this rule scope.");
        DrawHeaderCell(7, "Match", "Exact or substring name matching.");
        DrawHeaderCell(8, "Class", "Planner/execution behavior override such as Required, CombatFriendly, TreasureDoor, BossFight, MapXzDestination, MapXzForceMarch, XYZ, or XYZForceMarch. ForceMarch rows are generic authored bypass destinations, not mounted-only rows.");
        DrawHeaderCell(9, "Layer", "Live map/sub-area filter. If set, this rule only applies on that active layer. Use a live map name like Forecastle or a map row id.");
        DrawHeaderCell(10, "Coords", "Single coordinate field. Enter `a,b` for map X,Z and `a,b,c` for world X,Y,Z. On manual destination rows this is the destination point. On ordinary rows this is the physical object selector.");
        DrawHeaderCell(11, "R", "Optional positional-match radius for ordinary rows only. Blank/0 means no explicit radius and falls back to 6y when Coords is populated. Manual destination rows ignore this field.");
        DrawHeaderCell(12, "Pri", "Lower wins. Manual destinations can intentionally beat worse live progression interactables if you give them the better priority.");
        DrawHeaderCell(13, "Y", "Priority vertical radius gate. Zero means no Y gate.");
        DrawHeaderCell(14, "Dist", "Optional max distance gate. Zero/blank means no distance cap.");
        DrawHeaderCell(15, "Wait-before", "Seconds to hold after ADS arrives in interact range and before it sends the first direct interact for this commitment.");
        DrawHeaderCell(16, "Wait-after", "Seconds to hold after a successful direct interact send before ADS retries the same target or moves on to new planner truth.");
        DrawHeaderCell(17, "Notes", "Human notes only. Safe place for why this rule exists or what was tested.");
        DrawHeaderCell(18, "Copy", "Copy this row as base64-wrapped JSON to the clipboard.");
        DrawHeaderCell(19, "Paste", "Replace this row from a base64 row payload currently on the clipboard.");
        DrawHeaderCell(20, "-", "Delete this row.");
    }

    private static bool IsManualDestinationRule(ObjectPriorityRule rule)
        => string.Equals(rule.Classification, "MapXzDestination", StringComparison.OrdinalIgnoreCase)
           || string.Equals(rule.Classification, "MapXzForceMarch", StringComparison.OrdinalIgnoreCase)
           || string.Equals(rule.Classification, "XYZ", StringComparison.OrdinalIgnoreCase)
           || string.Equals(rule.Classification, "XYZForceMarch", StringComparison.OrdinalIgnoreCase)
           || string.Equals(rule.DestinationType, "MapXZ", StringComparison.OrdinalIgnoreCase)
           || string.Equals(rule.DestinationType, "XYZ", StringComparison.OrdinalIgnoreCase);

    private static bool IsForceMarchManualDestinationRule(ObjectPriorityRule rule)
        => string.Equals(rule.Classification, "MapXzForceMarch", StringComparison.OrdinalIgnoreCase)
           || string.Equals(rule.Classification, "XYZForceMarch", StringComparison.OrdinalIgnoreCase);

    private static string GetUnifiedCoordinatesValue(ObjectPriorityRule rule)
    {
        if (IsManualDestinationRule(rule))
            return !string.IsNullOrWhiteSpace(rule.WorldCoordinates) ? rule.WorldCoordinates : rule.MapCoordinates;

        return !string.IsNullOrWhiteSpace(rule.ObjectWorldCoordinates) ? rule.ObjectWorldCoordinates : rule.ObjectMapCoordinates;
    }

    private static void SetUnifiedCoordinatesValue(ObjectPriorityRule rule, string value)
    {
        var normalized = NormalizeCoordinateText(value);
        var partCount = CountCoordinateParts(normalized);
        var isWorldCoordinates = partCount == 3;

        if (IsManualDestinationRule(rule))
        {
            rule.MapCoordinates = string.Empty;
            rule.WorldCoordinates = string.Empty;
            rule.DestinationType = string.Empty;
            var useForceMarchClassification = IsForceMarchManualDestinationRule(rule);

            if (!string.IsNullOrWhiteSpace(normalized))
            {
                if (isWorldCoordinates)
                {
                    rule.WorldCoordinates = normalized;
                    rule.Classification = useForceMarchClassification ? "XYZForceMarch" : "XYZ";
                }
                else
                {
                    rule.MapCoordinates = normalized;
                    rule.Classification = useForceMarchClassification ? "MapXzForceMarch" : "MapXzDestination";
                }
            }

            return;
        }

        rule.ObjectMapCoordinates = string.Empty;
        rule.ObjectWorldCoordinates = string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        if (isWorldCoordinates)
            rule.ObjectWorldCoordinates = normalized;
        else
            rule.ObjectMapCoordinates = normalized;
    }

    private static int CountCoordinateParts(string value)
        => string.IsNullOrWhiteSpace(value)
            ? 0
            : value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    private static string NormalizeCoordinateText(string value)
        => string.Join(',', value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private void DrawHeaderCell(int columnIndex, string label, string tooltip)
    {
        ImGui.TableSetColumnIndex(columnIndex);
        ImGui.TableHeader(label);
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35f);
        ImGui.TextUnformatted(tooltip);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private bool DrawDutyCell(int ruleIndex, ObjectPriorityRule rule)
    {
        var currentLabel = GetDutySelectionLabel(rule);
        ImGui.SetNextItemWidth(-1f);
        if (!ImGui.BeginCombo("##DutyEnglishName", currentLabel))
            return false;

        if (dutySearchRow != ruleIndex)
        {
            dutySearchRow = ruleIndex;
            dutySearch = string.Empty;
        }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##DutySearch", "search duties", ref dutySearch, 128);
        ImGui.Separator();

        var changed = false;
        if (DrawDutyChoice("GLOBAL", string.IsNullOrWhiteSpace(rule.DutyEnglishName)))
        {
            rule.DutyEnglishName = string.Empty;
            rule.TerritoryTypeId = 0;
            rule.ContentFinderConditionId = 0;
            changed = true;
        }

        var currentDuty = plugin.DutyCatalogService.Entries
            .FirstOrDefault(x => x.EnglishName.Equals(rule.DutyEnglishName, StringComparison.OrdinalIgnoreCase));
        if (currentDuty is null && !string.IsNullOrWhiteSpace(rule.DutyEnglishName) && MatchesDutySearch(rule.DutyEnglishName))
        {
            if (DrawDutyChoice($"[Custom] {rule.DutyEnglishName}", false))
            {
                changed = false;
            }
        }

        foreach (var entry in plugin.DutyCatalogService.Entries
                     .OrderBy(x => x.EnglishName, StringComparer.OrdinalIgnoreCase)
                     .Where(x => MatchesDutySearch(x.EnglishName)))
        {
            var isSelected = entry.EnglishName.Equals(rule.DutyEnglishName, StringComparison.OrdinalIgnoreCase);
            if (!DrawDutyChoice(entry.EnglishName, isSelected))
                continue;

            rule.DutyEnglishName = entry.EnglishName;
            rule.TerritoryTypeId = entry.TerritoryTypeId;
            rule.ContentFinderConditionId = entry.ContentFinderConditionId;
            changed = true;
        }

        ImGui.EndCombo();
        return changed;
    }

    private static bool DrawDutyChoice(string label, bool selected)
        => ImGui.Selectable(label, selected);

    private bool MatchesDutySearch(string label)
        => string.IsNullOrWhiteSpace(dutySearch)
           || label.Contains(dutySearch, StringComparison.OrdinalIgnoreCase);

    private bool DrawLayerCell(ObjectPriorityRule rule, int ruleIndex)
    {
        var territoryTypeId = rule.TerritoryTypeId != 0
            ? rule.TerritoryTypeId
            : plugin.DutyContextService.Current.TerritoryTypeId;
        var knownLayers = plugin.ObjectPriorityRuleService.GetKnownLayerSelectors(territoryTypeId);
        if (knownLayers.Count == 0)
        {
            if (!EditTextCell("##Layer", rule.Layer, 48, out var layer))
                return false;

            rule.Layer = layer;
            return true;
        }

        var currentLabel = string.IsNullOrWhiteSpace(rule.Layer) ? "(blank)" : rule.Layer;
        ImGui.SetNextItemWidth(-1f);
        if (!ImGui.BeginCombo($"##Layer{ruleIndex}", currentLabel))
            return false;

        var changed = false;
        if (ImGui.Selectable("(blank)", string.IsNullOrWhiteSpace(rule.Layer)))
        {
            rule.Layer = string.Empty;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(rule.Layer)
            && knownLayers.All(x => !x.Equals(rule.Layer, StringComparison.OrdinalIgnoreCase)))
        {
            if (ImGui.Selectable($"[Custom] {rule.Layer}", true))
                changed = false;

            ImGui.Separator();
        }

        foreach (var layer in knownLayers)
        {
            var isSelected = string.Equals(rule.Layer, layer, StringComparison.OrdinalIgnoreCase);
            if (!ImGui.Selectable(layer, isSelected))
                continue;

            rule.Layer = layer;
            changed = true;
        }

        ImGui.EndCombo();
        return changed;
    }

    private bool DrawObjectKindCell(ObjectPriorityRule rule, int ruleIndex)
    {
        var currentLabel = string.IsNullOrWhiteSpace(rule.ObjectKind) ? "(any)" : rule.ObjectKind;
        ImGui.SetNextItemWidth(-1f);
        var currentIndex = Math.Max(0, Array.IndexOf(ObjectKindLabels, currentLabel));
        if (!ImGui.Combo($"##ObjectKind{ruleIndex}", ref currentIndex, ObjectKindLabels, ObjectKindLabels.Length))
            return false;

        rule.ObjectKind = currentIndex == 0 ? string.Empty : ObjectKindLabels[currentIndex];
        return true;
    }

    private IReadOnlyList<int> BuildVisibleRuleIndices()
    {
        IEnumerable<int> indices = Enumerable.Range(0, draft.Rules.Count);
        if (filterCurrentAreaAndGlobal)
        {
            var context = plugin.DutyContextService.Current;
            indices = indices.Where(index => plugin.ObjectPriorityRuleService.MatchesCurrentDutyScopeForEditor(draft.Rules[index], context));
        }

        if (!sortByDutyName)
            return indices.ToList();

        return indices
            .OrderBy(index => GetDutySortLabel(draft.Rules[index]), StringComparer.OrdinalIgnoreCase)
            .ThenBy(index => draft.Rules[index].ContentFinderConditionId)
            .ThenBy(index => draft.Rules[index].TerritoryTypeId)
            .ThenBy(index => draft.Rules[index].ObjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(index => draft.Rules[index].Priority)
            .ToList();
    }

    private static string GetDutySelectionLabel(ObjectPriorityRule rule)
        => string.IsNullOrWhiteSpace(rule.DutyEnglishName)
            ? "GLOBAL"
            : rule.DutyEnglishName;

    private static string GetDutySortLabel(ObjectPriorityRule rule)
        => string.IsNullOrWhiteSpace(rule.DutyEnglishName)
            ? "0000 GLOBAL"
            : rule.DutyEnglishName;

    private void SwitchPreset(string presetName)
    {
        if (string.Equals(presetName, selectedPresetName, StringComparison.OrdinalIgnoreCase))
            return;

        var previousPreset = selectedPresetName;
        var discardedDirtyDraft = dirty;
        selectedPresetName = presetName;
        RefreshDraft(
            discardedDirtyDraft
                ? $"Switched from {previousPreset} to {selectedPresetName}; unsaved edits in the previous draft were discarded."
                : $"Switched from {previousPreset} to {selectedPresetName}.");
    }

    private void DrawCreatePresetPopup()
    {
        if (!ImGui.BeginPopup("ADSCreatePreset"))
            return;

        ImGui.TextUnformatted("Create preset from the current draft");
        ImGui.SetNextItemWidth(260f);
        ImGui.InputTextWithHint("##NewPresetName", "preset name", ref pendingPresetName, 64);

        if (ImGui.Button("Create"))
        {
            var sanitizedName = plugin.ObjectPriorityRuleService.SanitizePresetName(pendingPresetName);
            if (plugin.ObjectPriorityRuleService.IsDefaultPreset(sanitizedName))
            {
                editorStatus = "DEFAULT is reserved; choose a different preset name.";
            }
            else if (plugin.ObjectPriorityRuleService.SaveManifest(sanitizedName, draft))
            {
                selectedPresetName = sanitizedName;
                draft = CloneManifest(draft);
                dirty = false;
                unsavedNewRules.Clear();
                pendingScrollRule = null;
                editorStatus = $"Created preset {sanitizedName} from the current draft.";
                SyncDiskTransferPath();
                ImGui.CloseCurrentPopup();
            }
            else
            {
                editorStatus = plugin.ObjectPriorityRuleService.LastLoadStatus;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private void DrawDiskTransferPopup()
    {
        if (!ImGui.BeginPopup("ADSPresetDiskTransfer"))
            return;

        ImGui.TextUnformatted("Full-manifest disk import/export");
        ImGui.SetNextItemWidth(540f);
        ImGui.InputTextWithHint("##PresetDiskPath", "path to .json file", ref diskTransferPath, 512);

        if (ImGui.Button("Import file"))
        {
            if (plugin.ObjectPriorityRuleService.TryImportManifestFromPath(diskTransferPath, out var manifest, out var status))
            {
                draft = manifest;
                dirty = true;
                unsavedNewRules.Clear();
                pendingScrollRule = null;
                editorStatus = $"Imported full manifest from disk into preset {selectedPresetName} draft. {status}";
            }
            else
            {
                editorStatus = status;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Export file"))
        {
            if (plugin.ObjectPriorityRuleService.TryExportManifestToPath(diskTransferPath, draft, out var status))
                editorStatus = status;
            else
                editorStatus = status;
        }

        ImGui.SameLine();
        if (ImGui.Button("Use preset path"))
            SyncDiskTransferPath();

        ImGui.SameLine();
        if (ImGui.Button("Open preset dir"))
            plugin.OpenPath(plugin.ObjectPriorityRuleService.PresetDirectoryPath);

        ImGui.EndPopup();
    }

    private void DeleteCurrentPreset()
    {
        if (plugin.ObjectPriorityRuleService.TryDeletePreset(selectedPresetName, out var status))
        {
            selectedPresetName = ObjectPriorityRuleService.DefaultPresetName;
            RefreshDraft($"{status} Switched back to DEFAULT.");
            return;
        }

        editorStatus = status;
    }

    private void ResetDefaultDraftFromBundled()
    {
        if (plugin.ObjectPriorityRuleService.TryLoadBundledManifest(out var bundledManifest, out var status))
        {
            draft = bundledManifest;
            dirty = true;
            unsavedNewRules.Clear();
            pendingScrollRule = null;
            editorStatus = $"Loaded the packaged DEFAULT rules into the current draft. {status} Press Save to write them live.";
            return;
        }

        editorStatus = status;
    }

    private void ExportManifestToClipboard()
    {
        try
        {
            var json = JsonSerializer.Serialize(draft, new JsonSerializerOptions { WriteIndented = true });
            ImGui.SetClipboardText(json);
            editorStatus = $"Copied full preset {selectedPresetName} manifest JSON to the clipboard.";
        }
        catch (Exception ex)
        {
            editorStatus = $"Failed to export preset {selectedPresetName}: {ex.Message}";
        }
    }

    private void ImportManifestFromClipboard()
    {
        if (plugin.ObjectPriorityRuleService.TryImportManifestText(ImGui.GetClipboardText() ?? string.Empty, out var manifest, out var status))
        {
            draft = manifest;
            dirty = true;
            unsavedNewRules.Clear();
            pendingScrollRule = null;
            editorStatus = $"Imported full manifest from clipboard into preset {selectedPresetName} draft. {status}";
            return;
        }

        editorStatus = status;
    }

    private void AddDraftRule(ObjectPriorityRule rule, string status)
    {
        draft.Rules.Add(rule);
        unsavedNewRules.Add(rule);
        pendingScrollRule = rule;
        dirty = true;
        editorStatus = status;
    }

    private void SyncDiskTransferPath()
        => diskTransferPath = plugin.ObjectPriorityRuleService.GetPresetPath(selectedPresetName);

    private static ObjectPriorityRuleManifest CloneManifest(ObjectPriorityRuleManifest manifest)
        => new()
        {
            SchemaVersion = manifest.SchemaVersion,
            Description = manifest.Description,
            Rules = manifest.Rules.Select(
                    rule => new ObjectPriorityRule
                    {
                        Enabled = rule.Enabled,
                        TerritoryTypeId = rule.TerritoryTypeId,
                        ContentFinderConditionId = rule.ContentFinderConditionId,
                        DutyEnglishName = rule.DutyEnglishName,
                        ObjectKind = rule.ObjectKind,
                        BaseId = rule.BaseId,
                        ObjectName = rule.ObjectName,
                        NameMatchMode = rule.NameMatchMode,
                        Classification = rule.Classification,
                        DestinationType = rule.DestinationType,
                        Layer = rule.Layer,
                        MapCoordinates = rule.MapCoordinates,
                        WorldCoordinates = rule.WorldCoordinates,
                        ObjectMapCoordinates = rule.ObjectMapCoordinates,
                        ObjectWorldCoordinates = rule.ObjectWorldCoordinates,
                        ObjectMatchRadius = rule.ObjectMatchRadius,
                        Priority = rule.Priority,
                        PriorityVerticalRadius = rule.PriorityVerticalRadius,
                        MaxDistance = rule.MaxDistance,
                        WaitAtDestinationSeconds = rule.WaitAtDestinationSeconds,
                        WaitAfterInteractSeconds = rule.WaitAfterInteractSeconds,
                        Notes = rule.Notes,
                    })
                .ToList(),
        };

    private void ExportRuleAsBase64(ObjectPriorityRule rule)
    {
        try
        {
            var json = JsonSerializer.Serialize(rule);
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            ImGui.SetClipboardText(payload);
            editorStatus = $"Copied row for {GetDutySelectionLabel(rule)} / {rule.ObjectName} as base64.";
        }
        catch (Exception ex)
        {
            editorStatus = $"Failed to base64-export row: {ex.Message}";
        }
    }

    private bool ImportRuleFromClipboard(int ruleIndex)
    {
        try
        {
            var clipboard = ImGui.GetClipboardText()?.Trim();
            if (string.IsNullOrWhiteSpace(clipboard))
            {
                editorStatus = "Clipboard was empty; no row import performed.";
                return false;
            }

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(clipboard));
            var importedRule = JsonSerializer.Deserialize<ObjectPriorityRule>(json);
            if (importedRule is null)
            {
                editorStatus = "Clipboard base64 did not decode into a rule row.";
                return false;
            }

            draft.Rules[ruleIndex] = importedRule;
            editorStatus = $"Imported base64 row into visible row {ruleIndex}.";
            return true;
        }
        catch (Exception ex)
        {
            editorStatus = $"Failed to base64-import row: {ex.Message}";
            return false;
        }
    }

    private static bool EditTextCell(string id, string value, int maxLength, out string editedValue)
    {
        ImGui.SetNextItemWidth(-1f);
        var local = value;
        editedValue = local;
        if (!ImGui.InputText(id, ref local, maxLength))
            return false;

        editedValue = local;
        return true;
    }

    private static bool EditUintCell(string id, uint value, out uint editedValue)
    {
        ImGui.SetNextItemWidth(-1f);
        var local = value > int.MaxValue ? int.MaxValue : (int)value;
        editedValue = value;
        if (!ImGui.InputInt(id, ref local))
            return false;

        editedValue = local <= 0 ? 0u : (uint)local;
        return true;
    }

    private static bool EditIntCell(string id, int value, out int editedValue)
    {
        ImGui.SetNextItemWidth(-1f);
        var local = value;
        editedValue = value;
        if (!ImGui.InputInt(id, ref local))
            return false;

        editedValue = local;
        return true;
    }

    private static bool EditFloatCell(string id, float value, out float editedValue)
    {
        ImGui.SetNextItemWidth(-1f);
        var local = value;
        editedValue = value;
        if (!ImGui.InputFloat(id, ref local, 0f, 0f, "%.1f"))
            return false;

        editedValue = local < 0f ? 0f : local;
        return true;
    }

    private static bool EditNullableFloatCell(string id, float? value, out float? editedValue)
    {
        ImGui.SetNextItemWidth(-1f);
        var local = value ?? 0f;
        editedValue = value;
        if (!ImGui.InputFloat(id, ref local, 0f, 0f, "%.1f"))
            return false;

        editedValue = local <= 0f ? null : local;
        return true;
    }

    private static string[] BuildObjectKindLabels()
    {
        var labels = new List<string> { "(any)" };
        labels.AddRange(Enum.GetNames<ObjectKind>().OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        return labels.ToArray();
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
