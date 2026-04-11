using System.Numerics;
using System.Text;
using System.Text.Json;
using ADS.Models;
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
        "MapXzDestination",
        "XYZ",
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
        "MapXzDestination",
        "XYZ",
    ];

    private static readonly string[] ObjectKindLabels = BuildObjectKindLabels();

    private readonly Plugin plugin;
    private ObjectPriorityRuleManifest draft = new();
    private bool draftLoaded;
    private bool dirty;
    private bool filterCurrentAreaAndGlobal;
    private bool sortByDutyName = true;
    private string dutySearch = string.Empty;
    private int dutySearchRow = -1;
    private string editorStatus = "Rules not loaded.";

    public ObjectRuleEditorWindow(Plugin plugin)
        : base("ADS Rules Editor###ADSRulesEditor")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(1400f, 560f),
            MaximumSize = new Vector2(3600f, 2400f),
        };
        Size = new Vector2(2600f, 1040f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();
        EnsureDraftLoaded();

        ImGui.TextWrapped("Spreadsheet-style editor for duty-object-rules.json. Use the duty dropdown for catalog duties, leave it on GLOBAL for wildcard rows, and use row base64 export/import for quick duplication or sharing.");
        ImGui.TextWrapped("Layer now scopes any rule to the current live sub-area only. For Map XZ and XYZ rows, Layer still means the active map/sub-area selector. BaseId is the stable sheet/base object id, not the per-instance GameObjectId.");
        ImGui.TextWrapped(plugin.ObjectPriorityRuleService.ConfigPath);
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
        if (ImGui.Button("+ Row"))
        {
            draft.Rules.Add(plugin.ObjectPriorityRuleService.CreateBlankRule());
            dirty = true;
        }

        ImGui.SameLine();
        using (new ImGuiDisabledBlock(!dirty))
        {
            if (ImGui.Button("Save"))
            {
                if (plugin.ObjectPriorityRuleService.SaveManifest(draft))
                {
                    RefreshDraft("Rules saved and reloaded.");
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
            plugin.ObjectPriorityRuleService.Reload();
            RefreshDraft("Draft reloaded from disk.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Open JSON"))
            plugin.OpenPath(plugin.ObjectPriorityRuleService.ConfigPath);

        ImGui.SameLine();
        ImGui.Checkbox("Current Area + Global", ref filterCurrentAreaAndGlobal);

        ImGui.SameLine();
        ImGui.Checkbox("Sort By Duty", ref sortByDutyName);

        var rowsShown = BuildVisibleRuleIndices().Count;
        ImGui.TextUnformatted($"Rows shown: {rowsShown} / {draft.Rules.Count}");
    }

    private void EnsureDraftLoaded()
    {
        if (draftLoaded)
            return;

        RefreshDraft("Rules loaded from disk.");
    }

    private void RefreshDraft(string status)
    {
        draft = plugin.ObjectPriorityRuleService.CreateEditableCopy();
        draftLoaded = true;
        dirty = false;
        dutySearch = string.Empty;
        dutySearchRow = -1;
        editorStatus = status;
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

        if (!ImGui.BeginTable("ADSRulesEditorTable", 20, tableFlags, new Vector2(-1f, -1f)))
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
        ImGui.TableSetupColumn("Map XZ", ImGuiTableColumnFlags.WidthFixed, 130f);
        ImGui.TableSetupColumn("World XYZ", ImGuiTableColumnFlags.WidthFixed, 180f);
        ImGui.TableSetupColumn("Pri", ImGuiTableColumnFlags.WidthFixed, 88f);
        ImGui.TableSetupColumn("Y", ImGuiTableColumnFlags.WidthFixed, 88f);
        ImGui.TableSetupColumn("Dist", ImGuiTableColumnFlags.WidthFixed, 88f);
        ImGui.TableSetupColumn("Wait", ImGuiTableColumnFlags.WidthFixed, 76f);
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

            ImGui.TableSetColumnIndex(0);
            var enabled = rule.Enabled;
            if (ImGui.Checkbox("##Enabled", ref enabled))
            {
                rule.Enabled = enabled;
                dirty = true;
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
            if (EditTextCell("##Layer", rule.Layer, 48, out var layer))
            {
                rule.Layer = layer;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(10);
            if (EditTextCell("##MapCoordinates", rule.MapCoordinates, 32, out var mapCoordinates))
            {
                rule.MapCoordinates = mapCoordinates;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(11);
            if (EditTextCell("##WorldCoordinates", rule.WorldCoordinates, 48, out var worldCoordinates))
            {
                rule.WorldCoordinates = worldCoordinates;
                dirty = true;
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
            if (EditTextCell("##Notes", rule.Notes, 512, out var notes))
            {
                rule.Notes = notes;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(17);
            if (ImGui.SmallButton("B64"))
                ExportRuleAsBase64(rule);

            ImGui.TableSetColumnIndex(18);
            if (ImGui.SmallButton("Paste") && ImportRuleFromClipboard(ruleIndex))
                dirty = true;

            ImGui.TableSetColumnIndex(19);
            if (ImGui.SmallButton("-"))
                rowToRemove = ruleIndex;

            ImGui.PopID();
        }

        if (rowToRemove >= 0)
        {
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
        DrawHeaderCell(8, "Class", "Planner/execution behavior override such as Required, CombatFriendly, BossFight, MapXzDestination, or XYZ.");
        DrawHeaderCell(9, "Layer", "Live map/sub-area filter. If set, this rule only applies on that active layer. Use a live map name like Forecastle or a map row id.");
        DrawHeaderCell(10, "Map XZ", "Player-facing map coordinates for manual staging / Map XZ rows, like 11.3,10.4.");
        DrawHeaderCell(11, "World XYZ", "World-space X,Y,Z coordinates for precise XYZ rows, like 154.1,101.9,-34.2.");
        DrawHeaderCell(12, "Pri", "Lower wins. Manual destinations can intentionally beat worse live progression interactables if you give them the better priority.");
        DrawHeaderCell(13, "Y", "Priority vertical radius gate. Zero means no Y gate.");
        DrawHeaderCell(14, "Dist", "Optional max distance gate. Zero/blank means no distance cap.");
        DrawHeaderCell(15, "Wait", "Reserved wait-at-destination seconds seam for future execution timing.");
        DrawHeaderCell(16, "Notes", "Human notes only. Safe place for why this rule exists or what was tested.");
        DrawHeaderCell(17, "Copy", "Copy this row as base64-wrapped JSON to the clipboard.");
        DrawHeaderCell(18, "Paste", "Replace this row from a base64 row payload currently on the clipboard.");
        DrawHeaderCell(19, "-", "Delete this row.");
    }

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
