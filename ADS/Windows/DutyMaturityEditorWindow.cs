using System.Numerics;
using ADS.Models;
using ADS.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class DutyMaturityEditorWindow : PositionedWindow, IDisposable
{
    private static readonly DutyClearanceStatus[] ClearanceValues = Enum.GetValues<DutyClearanceStatus>();
    private static readonly string[] ClearanceLabels = ClearanceValues.Select(GetClearanceLabel).ToArray();
    private static readonly DutySupportLevel[] SupportValues = Enum.GetValues<DutySupportLevel>();
    private static readonly string[] SupportLabels = SupportValues.Select(GetSupportLevelLabel).ToArray();

    private readonly Plugin plugin;
    private string search = string.Empty;
    private string? selectedKey;
    private bool showOverridesOnly;
    private bool dirty;
    private string editorStatus = "Duty maturity editor ready.";

    public DutyMaturityEditorWindow(Plugin plugin)
        : base("ADS Duty Maturity Editor###ADSDutyMaturityEditor")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(880f, 520f),
            MaximumSize = new Vector2(3200f, 2200f),
        };
        Size = new Vector2(1240f, 820f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();

        DrawToolbar();
        ImGui.Spacing();

        var visibleEntries = plugin.DutyCatalogService.Entries
            .Where(EntryMatchesFilters)
            .ToList();

        ImGui.TextDisabled($"Rows shown: {visibleEntries.Count} / {plugin.DutyCatalogService.Entries.Count} | {(dirty ? "unsaved changes" : "saved")}");
        ImGui.TextWrapped(editorStatus);

        if (visibleEntries.Count == 0)
        {
            ImGui.TextWrapped("No duties match the current filters.");
            return;
        }

        var selectedEntry = ResolveSelectedEntry(visibleEntries);
        var availableWidth = ImGui.GetContentRegionAvail().X;
        if (availableWidth >= 1120f)
        {
            if (!ImGui.BeginTable("ADSDutyMaturityEditorLayout", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
                return;

            ImGui.TableSetupColumn("Duties", ImGuiTableColumnFlags.WidthStretch, 1.7f);
            ImGui.TableSetupColumn("Notes", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            DrawDutyTable(visibleEntries, 520f);
            ImGui.TableSetColumnIndex(1);
            DrawDetails(selectedEntry);
            ImGui.EndTable();
            return;
        }

        DrawDutyTable(visibleEntries, 320f);
        ImGui.Spacing();
        DrawDetails(selectedEntry);
    }

    private void DrawToolbar()
    {
        ImGui.SetNextItemWidth(360f);
        ImGui.InputTextWithHint(
            "##ADSDutyMaturitySearch",
            "search duty, family, note, territory ID, or CFC ID",
            ref search,
            160);

        ImGui.SameLine();
        ImGui.Checkbox("Overrides only", ref showOverridesOnly);

        ImGui.SameLine();
        using (new ImGuiDisabledBlock(!dirty))
        {
            if (ImGui.Button("Save"))
            {
                if (plugin.DutyCatalogService.SaveMaturityOverrides())
                    dirty = false;

                editorStatus = plugin.DutyCatalogService.LastMaturityLoadStatus;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Reload"))
        {
            plugin.DutyCatalogService.ReloadMaturity();
            dirty = false;
            editorStatus = plugin.DutyCatalogService.LastMaturityLoadStatus;
        }

        ImGui.SameLine();
        if (ImGui.Button("Open JSON"))
            plugin.OpenPath(plugin.DutyCatalogService.MaturityConfigPath);
    }

    private DutyCatalogEntry ResolveSelectedEntry(IReadOnlyList<DutyCatalogEntry> visibleEntries)
    {
        var selectedEntry = visibleEntries.FirstOrDefault(entry => BuildDutyCatalogKey(entry) == selectedKey);
        if (selectedEntry is not null)
            return selectedEntry;

        var context = plugin.DutyContextService.Current;
        selectedEntry = visibleEntries.FirstOrDefault(entry => DutyMatchesCurrentContext(entry, context))
                        ?? visibleEntries[0];
        selectedKey = BuildDutyCatalogKey(selectedEntry);
        return selectedEntry;
    }

    private void DrawDutyTable(IReadOnlyList<DutyCatalogEntry> entries, float height)
    {
        if (!ImGui.BeginTable(
                "ADSDutyMaturityRows",
                6,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
                new Vector2(-1f, height)))
        {
            return;
        }

        ImGui.TableSetupColumn("Duty");
        ImGui.TableSetupColumn("Family", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("IDs", ImGuiTableColumnFlags.WidthFixed, 116f);
        ImGui.TableSetupColumn("Clearance", ImGuiTableColumnFlags.WidthFixed, 174f);
        ImGui.TableSetupColumn("Support", ImGuiTableColumnFlags.WidthFixed, 128f);
        ImGui.TableSetupColumn("Test", ImGuiTableColumnFlags.WidthFixed, 52f);
        ImGui.TableHeadersRow();

        foreach (var entry in entries)
        {
            var key = BuildDutyCatalogKey(entry);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            if (ImGui.Selectable($"{entry.EnglishName}##DutyMaturity{key}", key == selectedKey))
                selectedKey = key;

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(DutyCategoryDisplayCatalog.Get(entry.Category).FilterLabel);

            ImGui.TableSetColumnIndex(2);
            var cfcLabel = entry.ContentFinderConditionId == 0
                ? "-"
                : entry.ContentFinderConditionId.ToString();
            ImGui.TextUnformatted($"T{entry.TerritoryTypeId} / C{cfcLabel}");

            ImGui.TableSetColumnIndex(3);
            DrawClearanceCombo(entry, $"##Clearance{key}");

            ImGui.TableSetColumnIndex(4);
            DrawSupportCombo(entry, $"##Support{key}");

            ImGui.TableSetColumnIndex(5);
            var planned = entry.IsPlannedTest;
            if (ImGui.Checkbox($"##Planned{key}", ref planned))
            {
                entry.IsPlannedTest = planned;
                selectedKey = key;
                MarkDirty();
            }
        }

        ImGui.EndTable();
    }

    private void DrawDetails(DutyCatalogEntry entry)
    {
        var cfcLabel = entry.ContentFinderConditionId == 0
            ? "-"
            : entry.ContentFinderConditionId.ToString();
        ImGui.TextColored(GetClearanceColor(entry.ClearanceStatus), entry.EnglishName);
        ImGui.TextUnformatted($"{DutyCategoryDisplayCatalog.Get(entry.Category).FilterLabel} | {entry.ExpansionName} | level {entry.LevelRequired} | party {entry.PartySize}");
        ImGui.TextUnformatted($"Territory {entry.TerritoryTypeId} | CFC {cfcLabel} | {entry.ContentTypeName}");
        ImGui.Spacing();

        DrawClearanceCombo(entry, "Clearance");
        DrawSupportCombo(entry, "Support");

        var planned = entry.IsPlannedTest;
        if (ImGui.Checkbox("Planned test", ref planned))
        {
            entry.IsPlannedTest = planned;
            MarkDirty();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Support Note");
        var note = entry.SupportNote;
        if (ImGui.InputTextMultiline("##ADSSupportNote", ref note, 1024, new Vector2(-1f, 180f)))
        {
            entry.SupportNote = note;
            MarkDirty();
        }

        if (ImGui.Button("Reset Row"))
        {
            entry.ClearanceStatus = DutyClearanceStatus.NotCleared;
            entry.SupportLevel = DutySupportLevel.PassiveOnly;
            entry.IsPlannedTest = false;
            entry.SupportNote = DutyCatalogService.DefaultSupportNote;
            MarkDirty();
        }
    }

    private void DrawClearanceCombo(DutyCatalogEntry entry, string label)
    {
        var clearanceIndex = Math.Max(0, Array.IndexOf(ClearanceValues, entry.ClearanceStatus));
        if (ImGui.Combo(label, ref clearanceIndex, ClearanceLabels, ClearanceLabels.Length))
        {
            entry.ClearanceStatus = ClearanceValues[Math.Clamp(clearanceIndex, 0, ClearanceValues.Length - 1)];
            selectedKey = BuildDutyCatalogKey(entry);
            MarkDirty();
        }
    }

    private void DrawSupportCombo(DutyCatalogEntry entry, string label)
    {
        var supportIndex = Math.Max(0, Array.IndexOf(SupportValues, entry.SupportLevel));
        if (ImGui.Combo(label, ref supportIndex, SupportLabels, SupportLabels.Length))
        {
            entry.SupportLevel = SupportValues[Math.Clamp(supportIndex, 0, SupportValues.Length - 1)];
            selectedKey = BuildDutyCatalogKey(entry);
            MarkDirty();
        }
    }

    private bool EntryMatchesFilters(DutyCatalogEntry entry)
    {
        if (showOverridesOnly && DutyCatalogService.IsDefaultMaturityEntry(entry))
            return false;

        var filter = search.Trim();
        if (filter.Length == 0)
            return true;

        var family = DutyCategoryDisplayCatalog.Get(entry.Category).FilterLabel;
        return entry.EnglishName.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || family.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || entry.ExpansionName.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || entry.SupportNote.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || entry.ClearanceStatus.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)
               || entry.SupportLevel.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)
               || entry.TerritoryTypeId.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)
               || entry.ContentFinderConditionId.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void MarkDirty()
    {
        dirty = true;
        editorStatus = "Unsaved duty maturity changes.";
    }

    private static string BuildDutyCatalogKey(DutyCatalogEntry entry)
        => entry.ContentFinderConditionId != 0
            ? $"cfc:{entry.ContentFinderConditionId}"
            : $"terr:{entry.TerritoryTypeId}";

    private static bool DutyMatchesCurrentContext(DutyCatalogEntry entry, DutyContextSnapshot context)
    {
        if (entry.ContentFinderConditionId != 0 && entry.ContentFinderConditionId == context.ContentFinderConditionId)
            return true;

        return entry.ContentFinderConditionId == 0
               && entry.TerritoryTypeId != 0
               && entry.TerritoryTypeId == context.TerritoryTypeId;
    }

    private static string GetClearanceLabel(DutyClearanceStatus status)
        => status switch
        {
            DutyClearanceStatus.OnePlayerUnsyncCleared => "1P Unsync Cleared",
            DutyClearanceStatus.OnePlayerDutySupport => "1P Duty Support",
            DutyClearanceStatus.FourPlayerSyncCleared => "Synced Party Cleared",
            _ => "Not Cleared",
        };

    private static Vector4 GetClearanceColor(DutyClearanceStatus status)
        => status switch
        {
            DutyClearanceStatus.OnePlayerUnsyncCleared => new Vector4(0.35f, 0.62f, 1.0f, 1f),
            DutyClearanceStatus.OnePlayerDutySupport => new Vector4(1.0f, 0.86f, 0.24f, 1f),
            DutyClearanceStatus.FourPlayerSyncCleared => new Vector4(0.42f, 0.94f, 0.64f, 1f),
            _ => new Vector4(1.0f, 0.36f, 0.32f, 1f),
        };

    private static string GetSupportLevelLabel(DutySupportLevel supportLevel)
        => supportLevel switch
        {
            DutySupportLevel.ActiveSupported => "Pilot active",
            DutySupportLevel.PassiveOnly => "Catalog test lane",
            _ => "Metadata only",
        };

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
