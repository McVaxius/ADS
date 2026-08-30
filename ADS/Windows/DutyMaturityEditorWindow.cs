using System.Numerics;
using ADS.Models;
using ADS.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class DutyMaturityEditorWindow : PositionedWindow, IDisposable
{
    private static readonly string[] RuleCoverageLabels = ["All rules", "No rules", "Has rules", "Dense rules"];
    private static readonly string[] WaypointCoverageLabels = ["All waypoints", "No waypoints", "Has waypoints"];

    private readonly Plugin plugin;
    private readonly DutyMaturityFilterState filters = new();
    private readonly List<DutyMaturityDraftRow> draftRows = [];
    private readonly HashSet<string> selectedKeys = new(StringComparer.Ordinal);
    private string? focusedKey;
    private int bulkMaturityIndex;
    private string editorStatus = "Duty Manager ready.";

    public DutyMaturityEditorWindow(Plugin plugin)
        : base("ADS Duty Manager###ADSDutyMaturityEditor")
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

    public void OpenMissingDutyWork()
    {
        filters.RuleCoverage = DutyRuleCoverageFilter.NoExplicitRules;
        filters.SelectedOnly = false;
        IsOpen = true;
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();
        EnsureDraftRowsLoaded();

        var currentContext = plugin.DutyContextService.Current;
        var coverage = DutyRuleCoverageHelper.BuildSnapshot(
            plugin.DutyCatalogService.Entries,
            plugin.ObjectPriorityRuleService.Current.Rules);
        var hasDraftChanges = draftRows.Any(row => row.IsChanged);
        var visibleRows = draftRows
            .Where(row => DutyMaturityFilterHelper.Matches(
                row,
                filters,
                currentContext,
                coverage.Get(row),
                selectedKeys.Contains(DutyMaturityCatalog.BuildDutyCatalogKey(row))))
            .ToList();

        DrawToolbar(hasDraftChanges);
        DrawFilters();
        DrawBulkActions(visibleRows);
        ImGui.TextDisabled(
            $"Rows: {visibleRows.Count}/{draftRows.Count} | Selected: {selectedKeys.Count} | " +
            $"Global rules: {coverage.GlobalRuleCount} | Unresolved rules: {coverage.UnresolvedRuleCount} | " +
            (hasDraftChanges ? "unsaved changes" : "saved"));
        ImGui.TextWrapped(editorStatus);

        if (visibleRows.Count == 0)
        {
            ImGui.TextWrapped("No duties match the current filters.");
            return;
        }

        var focusedRow = ResolveFocusedRow(visibleRows, currentContext);
        var focusedCoverage = coverage.Get(focusedRow);
        if (ImGui.GetContentRegionAvail().X >= 1180f)
        {
            if (!ImGui.BeginTable("ADSDutyManagerLayout", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
                return;
            ImGui.TableSetupColumn("Duties", ImGuiTableColumnFlags.WidthStretch, 2.2f);
            ImGui.TableSetupColumn("Details", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            DrawDutyTable(visibleRows, coverage, 520f);
            ImGui.TableSetColumnIndex(1);
            DrawDetails(focusedRow, focusedCoverage);
            ImGui.EndTable();
            return;
        }

        DrawDutyTable(visibleRows, coverage, 320f);
        ImGui.Spacing();
        DrawDetails(focusedRow, focusedCoverage);
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

    private void DrawToolbar(bool hasDraftChanges)
    {
        ImGui.TextUnformatted("Duty Manager");
        if (!ImGui.BeginTable("ADSDutyManagerToolbar", 2, ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("Search", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 205f);
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.SetNextItemWidth(-1f);
        var search = filters.Search;
        if (ImGui.InputTextWithHint("##ADSDutyManagerSearch", "search duty, family, expansion, note, territory, or CFC", ref search, 160))
            filters.Search = search;

        ImGui.TableSetColumnIndex(1);
        using (new ImGuiDisabledBlock(!hasDraftChanges))
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
        ImGui.EndTable();
    }

    private void DrawFilters()
    {
        if (!ImGui.CollapsingHeader("Filters", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawFamilyFilters();
        ImGui.TextUnformatted("Maturity");
        if (ImGui.SmallButton("All##DutyMaturityAll"))
            filters.SetAllClearanceStatuses(true);
        ImGui.SameLine();
        if (ImGui.SmallButton("None##DutyMaturityNone"))
            filters.SetAllClearanceStatuses(false);
        foreach (var status in DutyMaturityDisplayCatalog.ClearanceValues)
        {
            ImGui.SameLine();
            var enabled = filters.ClearanceStatuses.Contains(status);
            ImGui.PushStyleColor(ImGuiCol.Text, DutyMaturityDisplayCatalog.GetClearanceColor(status));
            if (ImGui.Checkbox($"{DutyMaturityDisplayCatalog.GetClearanceLabel(status)}##DutyMaturity{status}", ref enabled))
                SetMembership(filters.ClearanceStatuses, status, enabled);
            ImGui.PopStyleColor();
        }

        var expansions = draftRows
            .Select(row => (row.ExVersion, row.ExpansionName))
            .Distinct()
            .OrderBy(value => value.ExVersion)
            .ToList();
        var expansionIndex = filters.ExpansionId.HasValue
            ? expansions.FindIndex(value => value.ExVersion == filters.ExpansionId.Value) + 1
            : 0;
        var expansionLabels = new[] { "All expansions" }.Concat(expansions.Select(value => value.ExpansionName)).ToArray();
        ImGui.SetNextItemWidth(170f);
        if (ImGui.Combo("Expansion", ref expansionIndex, expansionLabels, expansionLabels.Length))
            filters.ExpansionId = expansionIndex == 0 ? null : expansions[expansionIndex - 1].ExVersion;

        ImGui.SameLine();
        var ruleCoverage = (int)filters.RuleCoverage;
        ImGui.SetNextItemWidth(145f);
        if (ImGui.Combo("Rules", ref ruleCoverage, RuleCoverageLabels, RuleCoverageLabels.Length))
            filters.RuleCoverage = (DutyRuleCoverageFilter)ruleCoverage;
        ImGui.SameLine();
        var waypointCoverage = (int)filters.WaypointCoverage;
        ImGui.SetNextItemWidth(155f);
        if (ImGui.Combo("Waypoints", ref waypointCoverage, WaypointCoverageLabels, WaypointCoverageLabels.Length))
            filters.WaypointCoverage = (DutyWaypointCoverageFilter)waypointCoverage;

        DrawFilterToggle("Dawntrail", filters.DawntrailOnly, value => filters.DawntrailOnly = value);
        DrawFilterToggle("Current duty", filters.CurrentDutyOnly, value => filters.CurrentDutyOnly = value);
        DrawFilterToggle("Changed", filters.ChangedOnly, value => filters.ChangedOnly = value);
        DrawFilterToggle("Selected", filters.SelectedOnly, value => filters.SelectedOnly = value);
        DrawFilterToggle("MSQ", filters.MainScenarioOnly, value => filters.MainScenarioOnly = value);
        DrawFilterToggle("Planned", filters.PlannedOnly, value => filters.PlannedOnly = value);
        DrawFilterToggle("Override", filters.OverridesOnly, value => filters.OverridesOnly = value);
        DrawFilterToggle("Has note", filters.HasNoteOnly, value => filters.HasNoteOnly = value);
    }

    private void DrawFamilyFilters()
    {
        ImGui.TextUnformatted("Families");
        if (ImGui.SmallButton("All##DutyFamilyAll"))
            filters.SetAllFamilies(true);
        ImGui.SameLine();
        if (ImGui.SmallButton("None##DutyFamilyNone"))
            filters.SetAllFamilies(false);
        foreach (var entry in DutyCategoryDisplayCatalog.Entries)
        {
            ImGui.SameLine();
            var enabled = filters.Families.Contains(entry.Category);
            ImGui.PushStyleColor(ImGuiCol.Text, entry.Accent);
            if (ImGui.Checkbox($"{entry.FilterLabel}##DutyFamily{entry.Category}", ref enabled))
                SetMembership(filters.Families, entry.Category, enabled);
            ImGui.PopStyleColor();
        }
    }

    private static void DrawFilterToggle(string label, bool current, Action<bool> set)
    {
        ImGui.SameLine();
        var value = current;
        if (ImGui.Checkbox(label, ref value))
            set(value);
    }

    private void DrawBulkActions(IReadOnlyList<DutyMaturityDraftRow> visibleRows)
    {
        if (!ImGui.CollapsingHeader("Bulk changes"))
            return;
        if (ImGui.SmallButton("Select Visible"))
        {
            foreach (var row in visibleRows)
                selectedKeys.Add(DutyMaturityCatalog.BuildDutyCatalogKey(row));
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear Selection"))
            selectedKeys.Clear();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f);
        ImGui.Combo("##BulkMaturity", ref bulkMaturityIndex, DutyMaturityDisplayCatalog.ClearanceLabels, DutyMaturityDisplayCatalog.ClearanceLabels.Length);
        ImGui.SameLine();
        using (new ImGuiDisabledBlock(selectedKeys.Count == 0))
        {
            if (ImGui.SmallButton("Set Maturity"))
                ApplyToSelected(row => row.ClearanceStatus = DutyMaturityDisplayCatalog.ClearanceValues[bulkMaturityIndex]);
            ImGui.SameLine();
            if (ImGui.SmallButton("Planned On"))
                ApplyToSelected(row => row.IsPlannedTest = true);
            ImGui.SameLine();
            if (ImGui.SmallButton("Planned Off"))
                ApplyToSelected(row => row.IsPlannedTest = false);
            ImGui.SameLine();
            if (ImGui.SmallButton("MSQ On"))
                ApplyToSelected(row => row.IsMainScenario = true);
            ImGui.SameLine();
            if (ImGui.SmallButton("MSQ Off"))
                ApplyToSelected(row => row.IsMainScenario = false);
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset Selected"))
                ApplyToSelected(row => row.ResetToDefaults());
        }
    }

    private DutyMaturityDraftRow ResolveFocusedRow(IReadOnlyList<DutyMaturityDraftRow> visibleRows, DutyContextSnapshot currentContext)
    {
        var row = visibleRows.FirstOrDefault(value => DutyMaturityCatalog.BuildDutyCatalogKey(value) == focusedKey)
                  ?? visibleRows.FirstOrDefault(value => DutyMaturityCatalog.DutyMatchesCurrentContext(value, currentContext))
                  ?? visibleRows[0];
        focusedKey = DutyMaturityCatalog.BuildDutyCatalogKey(row);
        return row;
    }

    private void DrawDutyTable(IReadOnlyList<DutyMaturityDraftRow> rows, DutyRuleCoverageSnapshot snapshot, float height)
    {
        if (!ImGui.BeginTable(
                "ADSDutyManagerRows",
                9,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
                new Vector2(-1f, height)))
            return;
        ImGui.TableSetupColumn("Sel", ImGuiTableColumnFlags.WidthFixed, 34f);
        ImGui.TableSetupColumn("Duty");
        ImGui.TableSetupColumn("Family", ImGuiTableColumnFlags.WidthFixed, 88f);
        ImGui.TableSetupColumn("Level / Expansion", ImGuiTableColumnFlags.WidthFixed, 130f);
        ImGui.TableSetupColumn("Maturity", ImGuiTableColumnFlags.WidthFixed, 62f);
        ImGui.TableSetupColumn("Enabled / Total", ImGuiTableColumnFlags.WidthFixed, 96f);
        ImGui.TableSetupColumn("Waypoints", ImGuiTableColumnFlags.WidthFixed, 76f);
        ImGui.TableSetupColumn("Scope", ImGuiTableColumnFlags.WidthFixed, 58f);
        ImGui.TableSetupColumn("Note", ImGuiTableColumnFlags.WidthFixed, 54f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();
        var clipper = new ImGuiListClipper();
        clipper.Begin(rows.Count);
        while (clipper.Step())
        {
            for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                DrawDutyTableRow(rows[index], snapshot.Get(rows[index]));
        }
        clipper.End();
        ImGui.EndTable();
    }

    private void DrawDutyTableRow(DutyMaturityDraftRow row, DutyRuleCoverage coverage)
    {
        var key = DutyMaturityCatalog.BuildDutyCatalogKey(row);
        ImGui.PushID(key);
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        var selected = selectedKeys.Contains(key);
        if (ImGui.Checkbox("##Selected", ref selected))
            SetMembership(selectedKeys, key, selected);
        ImGui.TableSetColumnIndex(1);
        ImGui.PushStyleColor(ImGuiCol.Text, row.IsChanged ? new Vector4(1f, .86f, .24f, 1f) : DutyMaturityDisplayCatalog.GetClearanceColor(row.ClearanceStatus));
        if (ImGui.Selectable($"{row.EnglishName}##Focus", focusedKey == key))
            focusedKey = key;
        ImGui.PopStyleColor();
        ImGui.TableSetColumnIndex(2);
        var family = DutyCategoryDisplayCatalog.Get(row.Category);
        ImGui.TextColored(family.Accent, family.FilterLabel);
        ImGui.TableSetColumnIndex(3);
        ImGui.TextUnformatted($"Lv {row.LevelRequired} / {row.ExpansionName}");
        ImGui.TableSetColumnIndex(4);
        ImGui.TextColored(DutyMaturityDisplayCatalog.GetClearanceColor(row.ClearanceStatus), DutyMaturityDisplayCatalog.GetClearanceLabel(row.ClearanceStatus));
        ImGui.TableSetColumnIndex(5);
        ImGui.TextUnformatted($"{coverage.EnabledRuleCount} / {coverage.AssociatedRuleCount}");
        ImGui.TableSetColumnIndex(6);
        ImGui.TextUnformatted(coverage.EnabledValidWaypointCount.ToString());
        ImGui.TableSetColumnIndex(7);
        if (coverage.RedundantScopeMismatchCount > 0)
            ImGui.TextColored(new Vector4(1f, .55f, .3f, 1f), $"! {coverage.RedundantScopeMismatchCount}");
        else
            ImGui.TextDisabled("-");
        ImGui.TableSetColumnIndex(8);
        ImGui.TextUnformatted(DutyMaturityCatalog.HasCustomSupportNote(row.SupportNote) ? "YES" : "-");
        ImGui.PopID();
    }

    private void DrawDetails(DutyMaturityDraftRow row, DutyRuleCoverage coverage)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f);
        if (ImGui.BeginChild("ADSDutyManagerDetail", new Vector2(-1f, -1f), true))
        {
            ImGui.TextColored(DutyMaturityDisplayCatalog.GetClearanceColor(row.ClearanceStatus), row.EnglishName);
            ImGui.TextColored(DutyCategoryDisplayCatalog.Get(row.Category).Accent, DutyCategoryDisplayCatalog.Get(row.Category).FilterLabel);
            if (ImGui.BeginTable("ADSDutyManagerDetailFacts", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
            {
                ImGui.TableNextRow();
                DrawFact(0, "Level / Expansion", $"{row.LevelRequired} / {row.ExpansionName}");
                DrawFact(1, "CFC / Territory", $"{row.ContentFinderConditionId} / {row.TerritoryTypeId}");
                ImGui.TableNextRow();
                DrawFact(0, "Enabled / Total Rules", $"{coverage.EnabledRuleCount} / {coverage.AssociatedRuleCount}");
                DrawFact(1, "Valid Waypoints", coverage.EnabledValidWaypointCount.ToString());
                ImGui.TableNextRow();
                DrawFact(0, "Scope Warnings", coverage.RedundantScopeMismatchCount.ToString());
                DrawFact(1, "Unsaved Change", row.IsChanged ? "YES" : "NO");
                ImGui.EndTable();
            }
            ImGui.Spacing();
            DrawMaturityCombo(row, "Maturity");
            var msq = row.IsMainScenario;
            if (ImGui.Checkbox("MSQ", ref msq))
            {
                row.IsMainScenario = msq;
                MarkDirty(row);
            }
            ImGui.SameLine();
            var planned = row.IsPlannedTest;
            if (ImGui.Checkbox("Planned", ref planned))
            {
                row.IsPlannedTest = planned;
                MarkDirty(row);
            }
            ImGui.TextUnformatted("Note");
            var note = row.SupportNote;
            if (ImGui.InputTextMultiline("##ADSDutyNote", ref note, 2048, new Vector2(-1f, 180f)))
            {
                row.SupportNote = note;
                MarkDirty(row);
            }
            if (ImGui.Button("Reset"))
            {
                row.ResetToDefaults();
                MarkDirty(row);
            }
            ImGui.SameLine();
            if (ImGui.Button("Manage Rules"))
                plugin.OpenRuleEditorUi(ResolveCatalogEntry(row));
            ImGui.SameLine();
            if (ImGui.Button("Open Rules"))
                plugin.OpenRuleEditorUi();
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
    }

    private DutyCatalogEntry ResolveCatalogEntry(DutyMaturityDraftRow row)
        => plugin.DutyCatalogService.Entries.First(entry =>
            DutyMaturityCatalog.BuildDutyCatalogKey(entry) == DutyMaturityCatalog.BuildDutyCatalogKey(row));

    private void DrawMaturityCombo(DutyMaturityDraftRow row, string label)
    {
        var index = Math.Max(0, Array.IndexOf(DutyMaturityDisplayCatalog.ClearanceValues, row.ClearanceStatus));
        if (!ImGui.Combo(label, ref index, DutyMaturityDisplayCatalog.ClearanceLabels, DutyMaturityDisplayCatalog.ClearanceLabels.Length))
            return;
        row.ClearanceStatus = DutyMaturityDisplayCatalog.ClearanceValues[index];
        MarkDirty(row);
    }

    private static void DrawFact(int column, string label, string value)
    {
        ImGui.TableSetColumnIndex(column);
        ImGui.TextDisabled(label.ToUpperInvariant());
        ImGui.TextWrapped(value);
    }

    private void ApplyToSelected(Action<DutyMaturityDraftRow> action)
    {
        var count = 0;
        foreach (var row in draftRows.Where(row => selectedKeys.Contains(DutyMaturityCatalog.BuildDutyCatalogKey(row))))
        {
            action(row);
            count++;
        }
        editorStatus = count == 0 ? "No selected duties to update." : $"Updated {count} selected duties. Save to persist them.";
    }

    private void MarkDirty(DutyMaturityDraftRow row)
    {
        focusedKey = DutyMaturityCatalog.BuildDutyCatalogKey(row);
        editorStatus = $"{row.EnglishName}: unsaved draft change.";
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
