using System.Numerics;
using ADS.Models;
using ADS.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class DutyMaturityEditorWindow : PositionedWindow, IDisposable
{
    private static readonly string[] RuleCoverageLabels =
    [
        "All",
        "No explicit rules",
        "Has rules",
        "Dense rules",
    ];

    private readonly Plugin plugin;
    private readonly DutyMaturityFilterState filters = new();
    private readonly List<DutyMaturityDraftRow> draftRows = [];
    private readonly HashSet<string> selectedKeys = new(StringComparer.Ordinal);
    private string? focusedKey;
    private int bulkClearanceIndex;
    private int bulkSupportIndex;
    private string editorStatus = "Duty maturity editor ready.";

    public DutyMaturityEditorWindow(Plugin plugin)
        : base("ADS Duty Maturity Editor###ADSDutyMaturityEditor")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(960f, 560f),
            MaximumSize = new Vector2(3200f, 2200f),
        };
        Size = new Vector2(1440f, 860f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();
        EnsureDraftRowsLoaded();

        var ruleCounts = DutyRuleCoverageHelper.BuildExplicitRuleCountsByDuty(
            plugin.DutyCatalogService.Entries,
            plugin.ObjectPriorityRuleService);
        var currentContext = plugin.DutyContextService.Current;

        DrawToolbar();
        ImGui.Spacing();

        var visibleRows = draftRows
            .Where(row => DutyMaturityFilterHelper.Matches(
                row,
                filters,
                currentContext,
                ruleCounts.GetValueOrDefault(DutyMaturityCatalog.BuildDutyCatalogKey(row))))
            .ToList();
        PruneStaleSelection();
        DrawBulkActions(visibleRows);

        ImGui.Spacing();
        ImGui.TextDisabled($"Rows shown: {visibleRows.Count} / {draftRows.Count} | Selected: {selectedKeys.Count} | {(HasDraftChanges() ? "unsaved changes" : "saved")}");
        ImGui.TextWrapped(editorStatus);

        if (visibleRows.Count == 0)
        {
            ImGui.TextWrapped("No duties match the current filters.");
            return;
        }

        var focusedRow = ResolveFocusedRow(visibleRows, currentContext);
        var availableWidth = ImGui.GetContentRegionAvail().X;
        if (availableWidth >= 1180f)
        {
            if (!ImGui.BeginTable("ADSDutyMaturityEditorLayout", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
                return;

            ImGui.TableSetupColumn("Duties", ImGuiTableColumnFlags.WidthStretch, 2.1f);
            ImGui.TableSetupColumn("Details", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            DrawDutyTable(visibleRows, ruleCounts, 520f);
            ImGui.TableSetColumnIndex(1);
            DrawDetails(focusedRow, ruleCounts.GetValueOrDefault(DutyMaturityCatalog.BuildDutyCatalogKey(focusedRow)));
            ImGui.EndTable();
            return;
        }

        DrawDutyTable(visibleRows, ruleCounts, 320f);
        ImGui.Spacing();
        DrawDetails(focusedRow, ruleCounts.GetValueOrDefault(DutyMaturityCatalog.BuildDutyCatalogKey(focusedRow)));
    }

    private void EnsureDraftRowsLoaded()
    {
        if (draftRows.Count == plugin.DutyCatalogService.Entries.Count && draftRows.Count != 0)
            return;

        LoadDraftRows(clearSelection: true);
    }

    private void LoadDraftRows(bool clearSelection)
    {
        draftRows.Clear();
        draftRows.AddRange(plugin.DutyCatalogService.Entries.Select(DutyMaturityDraftRow.FromEntry));
        if (clearSelection)
            selectedKeys.Clear();

        focusedKey = null;
    }

    private void DrawToolbar()
    {
        ImGui.SetNextItemWidth(360f);
        var search = filters.Search;
        if (ImGui.InputTextWithHint(
            "##ADSDutyMaturitySearch",
            "search duty, family, note, status, territory ID, or CFC ID",
            ref search,
            160))
        {
            filters.Search = search;
        }

        ImGui.SameLine();
        using (new ImGuiDisabledBlock(!HasDraftChanges()))
        {
            if (ImGui.Button("Save"))
            {
                if (plugin.DutyCatalogService.SaveMaturityOverrides(draftRows))
                {
                    foreach (var row in draftRows)
                        row.AcceptChanges();
                    selectedKeys.Clear();
                    focusedKey = null;
                }

                editorStatus = plugin.DutyCatalogService.LastMaturityLoadStatus;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Reload"))
        {
            plugin.DutyCatalogService.ReloadMaturity();
            LoadDraftRows(clearSelection: true);
            editorStatus = plugin.DutyCatalogService.LastMaturityLoadStatus;
        }

        ImGui.SameLine();
        if (ImGui.Button("Open JSON"))
            plugin.OpenPath(plugin.DutyCatalogService.MaturityConfigPath);

        DrawFamilyFilters();
        DrawEnumFilters();
        DrawToggleFilters();
    }

    private void DrawFamilyFilters()
    {
        ImGui.TextUnformatted("Families");
        if (ImGui.SmallButton("All##DutyFamilyAll"))
            filters.SetAllFamilies(enabled: true);

        ImGui.SameLine();
        if (ImGui.SmallButton("None##DutyFamilyNone"))
            filters.SetAllFamilies(enabled: false);

        var availableWidth = ImGui.GetContentRegionAvail().X;
        var columnCount = availableWidth >= 1120f ? 4 : availableWidth >= 640f ? 2 : 1;
        if (!ImGui.BeginTable("ADSDutyMaturityFamilyFilters", columnCount, ImGuiTableFlags.SizingStretchSame))
            return;

        for (var index = 0; index < DutyCategoryDisplayCatalog.Entries.Count; index++)
        {
            if (index % columnCount == 0)
                ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(index % columnCount);
            var entry = DutyCategoryDisplayCatalog.Entries[index];
            var enabled = filters.Families.Contains(entry.Category);
            ImGui.PushStyleColor(ImGuiCol.Text, entry.Accent);
            if (ImGui.Checkbox($"{entry.FilterLabel}##DutyMaturityFamily{entry.Category}", ref enabled))
                SetMembership(filters.Families, entry.Category, enabled);
            ImGui.PopStyleColor();
        }

        ImGui.EndTable();
    }

    private void DrawEnumFilters()
    {
        if (!ImGui.BeginTable("ADSDutyMaturityEnumFilters", 2, ImGuiTableFlags.SizingStretchSame))
            return;

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        DrawClearanceFilters();
        ImGui.TableSetColumnIndex(1);
        DrawSupportFilters();
        ImGui.EndTable();
    }

    private void DrawClearanceFilters()
    {
        ImGui.TextUnformatted("Clearance");
        if (ImGui.SmallButton("All##DutyClearanceAll"))
            filters.SetAllClearanceStatuses(enabled: true);
        ImGui.SameLine();
        if (ImGui.SmallButton("None##DutyClearanceNone"))
            filters.SetAllClearanceStatuses(enabled: false);

        foreach (var status in DutyMaturityDisplayCatalog.ClearanceValues)
        {
            var enabled = filters.ClearanceStatuses.Contains(status);
            ImGui.PushStyleColor(ImGuiCol.Text, DutyMaturityDisplayCatalog.GetClearanceColor(status));
            if (ImGui.Checkbox($"{DutyMaturityDisplayCatalog.GetClearanceLabel(status)}##DutyClearanceFilter{status}", ref enabled))
                SetMembership(filters.ClearanceStatuses, status, enabled);
            ImGui.PopStyleColor();
        }
    }

    private void DrawSupportFilters()
    {
        ImGui.TextUnformatted("Support");
        if (ImGui.SmallButton("All##DutySupportAll"))
            filters.SetAllSupportLevels(enabled: true);
        ImGui.SameLine();
        if (ImGui.SmallButton("None##DutySupportNone"))
            filters.SetAllSupportLevels(enabled: false);

        foreach (var support in DutyMaturityDisplayCatalog.SupportValues)
        {
            var enabled = filters.SupportLevels.Contains(support);
            ImGui.PushStyleColor(ImGuiCol.Text, DutyMaturityDisplayCatalog.GetSupportLevelColor(support));
            if (ImGui.Checkbox($"{DutyMaturityDisplayCatalog.GetSupportLevelLabel(support)}##DutySupportFilter{support}", ref enabled))
                SetMembership(filters.SupportLevels, support, enabled);
            ImGui.PopStyleColor();
        }
    }

    private void DrawToggleFilters()
    {
        var mainScenarioOnly = filters.MainScenarioOnly;
        if (ImGui.Checkbox("MSQ only", ref mainScenarioOnly))
            filters.MainScenarioOnly = mainScenarioOnly;
        ImGui.SameLine();
        var plannedOnly = filters.PlannedOnly;
        if (ImGui.Checkbox("Planned only", ref plannedOnly))
            filters.PlannedOnly = plannedOnly;
        ImGui.SameLine();
        var overridesOnly = filters.OverridesOnly;
        if (ImGui.Checkbox("Overrides only", ref overridesOnly))
            filters.OverridesOnly = overridesOnly;
        ImGui.SameLine();
        var changedOnly = filters.ChangedOnly;
        if (ImGui.Checkbox("Changed only", ref changedOnly))
            filters.ChangedOnly = changedOnly;
        ImGui.SameLine();
        var currentDutyOnly = filters.CurrentDutyOnly;
        if (ImGui.Checkbox("Current duty", ref currentDutyOnly))
            filters.CurrentDutyOnly = currentDutyOnly;
        ImGui.SameLine();
        var hasNoteOnly = filters.HasNoteOnly;
        if (ImGui.Checkbox("Has note", ref hasNoteOnly))
            filters.HasNoteOnly = hasNoteOnly;

        ImGui.SameLine();
        ImGui.SetNextItemWidth(170f);
        var coverageIndex = (int)filters.RuleCoverage;
        if (ImGui.Combo("Rule coverage", ref coverageIndex, RuleCoverageLabels, RuleCoverageLabels.Length))
            filters.RuleCoverage = (DutyRuleCoverageFilter)Math.Clamp(coverageIndex, 0, RuleCoverageLabels.Length - 1);
    }

    private void DrawBulkActions(IReadOnlyList<DutyMaturityDraftRow> visibleRows)
    {
        if (ImGui.Button("Select Visible"))
        {
            foreach (var row in visibleRows)
                selectedKeys.Add(DutyMaturityCatalog.BuildDutyCatalogKey(row));
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear Selection"))
            selectedKeys.Clear();

        ImGui.SameLine();
        using (new ImGuiDisabledBlock(selectedKeys.Count == 0))
        {
            ImGui.SetNextItemWidth(160f);
            ImGui.Combo("##BulkClearance", ref bulkClearanceIndex, DutyMaturityDisplayCatalog.ClearanceLabels, DutyMaturityDisplayCatalog.ClearanceLabels.Length);
            ImGui.SameLine();
            if (ImGui.Button("Set Clearance"))
                ApplyToSelected(row => row.ClearanceStatus = DutyMaturityDisplayCatalog.ClearanceValues[Math.Clamp(bulkClearanceIndex, 0, DutyMaturityDisplayCatalog.ClearanceValues.Length - 1)]);

            ImGui.SameLine();
            ImGui.SetNextItemWidth(150f);
            ImGui.Combo("##BulkSupport", ref bulkSupportIndex, DutyMaturityDisplayCatalog.SupportLabels, DutyMaturityDisplayCatalog.SupportLabels.Length);
            ImGui.SameLine();
            if (ImGui.Button("Set Support"))
                ApplyToSelected(row => row.SupportLevel = DutyMaturityDisplayCatalog.SupportValues[Math.Clamp(bulkSupportIndex, 0, DutyMaturityDisplayCatalog.SupportValues.Length - 1)]);

            ImGui.SameLine();
            if (ImGui.Button("Planned On"))
                ApplyToSelected(row => row.IsPlannedTest = true);
            ImGui.SameLine();
            if (ImGui.Button("Planned Off"))
                ApplyToSelected(row => row.IsPlannedTest = false);
            ImGui.SameLine();
            if (ImGui.Button("MSQ On"))
                ApplyToSelected(row => row.IsMainScenario = true);
            ImGui.SameLine();
            if (ImGui.Button("MSQ Off"))
                ApplyToSelected(row => row.IsMainScenario = false);
            ImGui.SameLine();
            if (ImGui.Button("Reset Selected"))
                ApplyToSelected(row => row.ResetToDefaults());
        }
    }

    private DutyMaturityDraftRow ResolveFocusedRow(IReadOnlyList<DutyMaturityDraftRow> visibleRows, DutyContextSnapshot currentContext)
    {
        var focusedRow = visibleRows.FirstOrDefault(row => DutyMaturityCatalog.BuildDutyCatalogKey(row) == focusedKey);
        if (focusedRow is not null)
            return focusedRow;

        focusedRow = visibleRows.FirstOrDefault(row => DutyMaturityCatalog.DutyMatchesCurrentContext(row, currentContext))
                     ?? visibleRows[0];
        focusedKey = DutyMaturityCatalog.BuildDutyCatalogKey(focusedRow);
        return focusedRow;
    }

    private void DrawDutyTable(
        IReadOnlyList<DutyMaturityDraftRow> rows,
        IReadOnlyDictionary<string, int> ruleCounts,
        float height)
    {
        if (!ImGui.BeginTable(
                "ADSDutyMaturityRows",
                10,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
                new Vector2(-1f, height)))
        {
            return;
        }

        ImGui.TableSetupColumn("Sel", ImGuiTableColumnFlags.WidthFixed, 34f);
        ImGui.TableSetupColumn("Duty");
        ImGui.TableSetupColumn("Family", ImGuiTableColumnFlags.WidthFixed, 92f);
        ImGui.TableSetupColumn("Level / Expansion", ImGuiTableColumnFlags.WidthFixed, 116f);
        ImGui.TableSetupColumn("Clearance", ImGuiTableColumnFlags.WidthFixed, 174f);
        ImGui.TableSetupColumn("Support", ImGuiTableColumnFlags.WidthFixed, 146f);
        ImGui.TableSetupColumn("MSQ", ImGuiTableColumnFlags.WidthFixed, 48f);
        ImGui.TableSetupColumn("Planned", ImGuiTableColumnFlags.WidthFixed, 66f);
        ImGui.TableSetupColumn("Rules", ImGuiTableColumnFlags.WidthFixed, 52f);
        ImGui.TableSetupColumn("Note", ImGuiTableColumnFlags.WidthFixed, 170f);
        ImGui.TableHeadersRow();

        foreach (var row in rows)
        {
            var key = DutyMaturityCatalog.BuildDutyCatalogKey(row);
            var ruleCount = ruleCounts.GetValueOrDefault(key);
            ImGui.PushID(key);
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            var selected = selectedKeys.Contains(key);
            if (ImGui.Checkbox("##Selected", ref selected))
                SetMembership(selectedKeys, key, selected);

            ImGui.TableSetColumnIndex(1);
            var highlight = row.IsChanged
                ? new Vector4(1.0f, 0.86f, 0.24f, 1f)
                : DutyMaturityDisplayCatalog.GetClearanceColor(row.ClearanceStatus);
            ImGui.PushStyleColor(ImGuiCol.Text, highlight);
            if (ImGui.Selectable($"{row.EnglishName}##Focus", key == focusedKey))
                focusedKey = key;
            ImGui.PopStyleColor();

            ImGui.TableSetColumnIndex(2);
            var family = DutyCategoryDisplayCatalog.Get(row.Category);
            ImGui.PushStyleColor(ImGuiCol.Text, family.Accent);
            ImGui.TextUnformatted(family.FilterLabel);
            ImGui.PopStyleColor();

            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted($"Lv {row.LevelRequired} / {row.ExpansionName}");

            ImGui.TableSetColumnIndex(4);
            DrawClearanceCombo(row, "##Clearance");

            ImGui.TableSetColumnIndex(5);
            DrawSupportCombo(row, "##Support");

            ImGui.TableSetColumnIndex(6);
            var msq = row.IsMainScenario;
            if (ImGui.Checkbox("##MSQ", ref msq))
            {
                row.IsMainScenario = msq;
                focusedKey = key;
                MarkDirty(row);
            }

            ImGui.TableSetColumnIndex(7);
            var planned = row.IsPlannedTest;
            if (ImGui.Checkbox("##Planned", ref planned))
            {
                row.IsPlannedTest = planned;
                focusedKey = key;
                MarkDirty(row);
            }

            ImGui.TableSetColumnIndex(8);
            DrawRuleCount(row, ruleCount);

            ImGui.TableSetColumnIndex(9);
            DrawNoteSnippet(row);
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawDetails(DutyMaturityDraftRow row, int ruleCount)
    {
        var cfcLabel = row.ContentFinderConditionId == 0
            ? "-"
            : row.ContentFinderConditionId.ToString();
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f);
        if (ImGui.BeginChild("ADSDutyMaturityDetail", new Vector2(-1f, -1f), true))
        {
            ImGui.TextColored(DutyMaturityDisplayCatalog.GetClearanceColor(row.ClearanceStatus), row.EnglishName);
            ImGui.SameLine();
            ImGui.TextColored(DutyMaturityDisplayCatalog.GetMsqColor(row.IsMainScenario), row.IsMainScenario ? "MSQ" : "non-MSQ");
            ImGui.TextColored(DutyCategoryDisplayCatalog.Get(row.Category).Accent, DutyCategoryDisplayCatalog.Get(row.Category).FilterLabel);
            ImGui.Spacing();

            if (ImGui.BeginTable("ADSDutyMaturityDetailFacts", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
            {
                ImGui.TableNextRow();
                DrawDutyDetailFact(0, "Level", row.LevelRequired.ToString());
                DrawDutyDetailFact(1, "Expansion", row.ExpansionName);
                ImGui.TableNextRow();
                DrawDutyDetailFact(0, "Party Size", row.PartySize.ToString());
                DrawDutyDetailFact(1, "Content Type", row.ContentTypeName);
                ImGui.TableNextRow();
                DrawDutyDetailFact(0, "Territory ID", row.TerritoryTypeId.ToString());
                DrawDutyDetailFact(1, "CFC ID", cfcLabel);
                ImGui.TableNextRow();
                DrawDutyDetailFact(0, "Explicit Rules", ruleCount.ToString());
                DrawDutyDetailFact(1, "Unsaved Change", row.IsChanged ? "YES" : "NO");
                ImGui.EndTable();
            }

            ImGui.Spacing();
            DrawClearanceCombo(row, "Clearance");
            DrawSupportCombo(row, "Support");

            var msq = row.IsMainScenario;
            if (ImGui.Checkbox("MSQ", ref msq))
            {
                row.IsMainScenario = msq;
                MarkDirty(row);
            }

            ImGui.SameLine();
            var planned = row.IsPlannedTest;
            if (ImGui.Checkbox("Planned test", ref planned))
            {
                row.IsPlannedTest = planned;
                MarkDirty(row);
            }

            ImGui.Spacing();
            ImGui.TextUnformatted("Support Note");
            var note = row.SupportNote;
            if (ImGui.InputTextMultiline("##ADSSupportNote", ref note, 2048, new Vector2(-1f, 220f)))
            {
                row.SupportNote = note;
                MarkDirty(row);
            }

            if (ImGui.Button("Reset Row"))
            {
                row.ResetToDefaults();
                MarkDirty(row);
            }
        }

        ImGui.EndChild();
        ImGui.PopStyleVar();
    }

    private void DrawClearanceCombo(DutyMaturityDraftRow row, string label)
    {
        var clearanceIndex = Math.Max(0, Array.IndexOf(DutyMaturityDisplayCatalog.ClearanceValues, row.ClearanceStatus));
        if (ImGui.Combo(label, ref clearanceIndex, DutyMaturityDisplayCatalog.ClearanceLabels, DutyMaturityDisplayCatalog.ClearanceLabels.Length))
        {
            row.ClearanceStatus = DutyMaturityDisplayCatalog.ClearanceValues[Math.Clamp(clearanceIndex, 0, DutyMaturityDisplayCatalog.ClearanceValues.Length - 1)];
            focusedKey = DutyMaturityCatalog.BuildDutyCatalogKey(row);
            MarkDirty(row);
        }
    }

    private void DrawSupportCombo(DutyMaturityDraftRow row, string label)
    {
        var supportIndex = Math.Max(0, Array.IndexOf(DutyMaturityDisplayCatalog.SupportValues, row.SupportLevel));
        if (ImGui.Combo(label, ref supportIndex, DutyMaturityDisplayCatalog.SupportLabels, DutyMaturityDisplayCatalog.SupportLabels.Length))
        {
            row.SupportLevel = DutyMaturityDisplayCatalog.SupportValues[Math.Clamp(supportIndex, 0, DutyMaturityDisplayCatalog.SupportValues.Length - 1)];
            focusedKey = DutyMaturityCatalog.BuildDutyCatalogKey(row);
            MarkDirty(row);
        }
    }

    private static void DrawDutyDetailFact(int column, string label, string value)
    {
        ImGui.TableSetColumnIndex(column);
        ImGui.TextDisabled(label.ToUpperInvariant());
        ImGui.TextWrapped(value);
    }

    private static void DrawRuleCount(IDutyMaturityCatalogRow row, int ruleCount)
    {
        if (!DutyMaturityCatalog.IsDefaultMaturityEntry(row) && ruleCount == 0)
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.43f, 0.35f, 1f));
        else if (!DutyMaturityCatalog.IsDefaultMaturityEntry(row) && ruleCount > DutyMaturityCatalog.DenseRuleThreshold)
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.86f, 0.24f, 1f));

        ImGui.TextUnformatted(ruleCount.ToString());
        if (!DutyMaturityCatalog.IsDefaultMaturityEntry(row)
            && (ruleCount == 0 || ruleCount > DutyMaturityCatalog.DenseRuleThreshold))
        {
            ImGui.PopStyleColor();
        }
    }

    private static void DrawNoteSnippet(IDutyMaturityCatalogRow row)
    {
        if (!DutyMaturityCatalog.HasCustomSupportNote(row.SupportNote))
        {
            ImGui.TextDisabled("-");
            return;
        }

        ImGui.TextUnformatted(BuildNoteSnippet(row.SupportNote));
    }

    private void ApplyToSelected(Action<DutyMaturityDraftRow> action)
    {
        var count = 0;
        foreach (var row in draftRows)
        {
            if (!selectedKeys.Contains(DutyMaturityCatalog.BuildDutyCatalogKey(row)))
                continue;

            action(row);
            count++;
        }

        editorStatus = count == 0
            ? "No selected duty rows to update."
            : $"Updated {count} selected duty row(s). Save to write duty-maturity.json.";
    }

    private void MarkDirty(DutyMaturityDraftRow row)
    {
        focusedKey = DutyMaturityCatalog.BuildDutyCatalogKey(row);
        editorStatus = $"{row.EnglishName}: unsaved draft change.";
    }

    private bool HasDraftChanges()
        => draftRows.Any(row => row.IsChanged);

    private void PruneStaleSelection()
    {
        var validKeys = draftRows
            .Select(DutyMaturityCatalog.BuildDutyCatalogKey)
            .ToHashSet(StringComparer.Ordinal);
        selectedKeys.RemoveWhere(key => !validKeys.Contains(key));
    }

    private static string BuildNoteSnippet(string note)
    {
        var normalized = DutyMaturityCatalog.NormalizeText(note);
        return normalized.Length <= 32
            ? normalized
            : $"{normalized[..29]}...";
    }

    private static void SetMembership<T>(ISet<T> values, T value, bool enabled)
    {
        if (enabled)
            values.Add(value);
        else
            values.Remove(value);
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
