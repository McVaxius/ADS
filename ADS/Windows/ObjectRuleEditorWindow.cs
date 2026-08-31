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
    private static readonly string[] AllianceValues = ["A", "B", "C", "D", "E", "F", "G"];

    internal static readonly string[] ClassificationLabels = RuleSemanticsCatalog.ClassificationLabels;
    internal static readonly string[] ClassificationValues = RuleSemanticsCatalog.ClassificationValues;

    private static readonly string[] ObjectKindLabels = BuildObjectKindLabels();
    private static readonly string[] FilterModeLabels =
    [
        "All",
        "Global only",
        "Current area only",
        "Global + current area",
        "Effective current label",
    ];
    private static readonly string[] PartialImportModeLabels =
    [
        "Complete duties",
        "Delta rows",
        "Current filter",
    ];

    private const int FilterModeGlobalOnly = 1;
    private const int FilterModeCurrentAreaOnly = 2;
    private const int FilterModeGlobalAndCurrentArea = 3;
    private const int FilterModeEffectiveCurrentLabel = 4;
    private static readonly TimeSpan PresetFilePollInterval = TimeSpan.FromSeconds(1);

    private readonly Plugin plugin;
    private readonly HashSet<ObjectPriorityRule> unsavedNewRules = [];
    private readonly HashSet<ObjectPriorityRule> selectedRules = [];
    private readonly Dictionary<uint, IReadOnlyList<string>> knownLayerSelectorsByTerritory = [];
    private ObjectPriorityRuleManifest draft = new();
    private readonly OneStepRuleManifestUndo undoState = new();
    private ManifestImportPreview? importPreview;
    private bool draftLoaded;
    private bool dirty;
    private bool draftStructureChangedThisDraw;
    private bool sortByDutyName = true;
    private bool openImportPreview;
    private bool openPresetSwitchConfirmation;
    private PresetFileState? loadedPresetFileState;
    private PresetFileState? lastObservedPresetFileState;
    private DateTime nextPresetFilePollUtc;
    private string selectedPresetName = ObjectPriorityRuleService.DefaultPresetName;
    private string pendingPresetName = string.Empty;
    private string pendingPresetSwitchName = string.Empty;
    private string diskTransferPath = string.Empty;
    private DutyCatalogEntry? dutyFilter;
    private DutyRuleIdentity? dutyFilterIdentity;
    private bool dutyFilterTerritoryUnique;
    private bool dutyFilterNameUnique;
    private string dutySearch = string.Empty;
    private string ruleTextFilter = string.Empty;
    private string presetFileConflictStatus = string.Empty;
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

    public void OpenForDuty(DutyCatalogEntry duty)
    {
        EnsureDraftLoaded();
        dutyFilter = duty;
        dutyFilterIdentity = DutyRuleIdentity.From(duty);
        dutyFilterTerritoryUnique = plugin.DutyCatalogService.Entries.Count(entry => entry.TerritoryTypeId == duty.TerritoryTypeId) == 1;
        dutyFilterNameUnique = plugin.DutyCatalogService.Entries.Count(entry =>
            string.Equals(
                DutyRuleCoverageHelper.NormalizeDutyLookupName(entry.EnglishName),
                dutyFilterIdentity.Value.NormalizedEnglishName,
                StringComparison.OrdinalIgnoreCase)) == 1;
        IsOpen = true;
        editorStatus = $"Showing rules associated with {duty.EnglishName}. Redundant scope mismatches remain visible for correction.";
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();
        EnsureDraftLoaded();
        draftStructureChangedThisDraw = false;
        PollSelectedPresetFile();

        ImGui.TextWrapped("Quick start: Object Explorer -> RULE -> choose Class -> fill relevant colored fields -> save DEFAULT -> retest.");
        ImGui.TextWrapped("Field cues: red required (bright red means missing), amber recommended, normal optional, dim ignored. Cues never clear ignored stored values.");
        ImGui.TextWrapped($"Preset: {selectedPresetName} -> {plugin.ObjectPriorityRuleService.GetPresetPath(selectedPresetName)}");
        DrawMatureProposalsBanner();
        DrawCurrentScopeBanner();
        DrawDutyFilterBanner();
        var activeDraftRule = DrawActiveRuleBanner();
        if (!plugin.ObjectPriorityRuleService.IsDefaultPreset(selectedPresetName))
            ImGui.TextWrapped("Runtime ADS still reads DEFAULT/live rules. Non-default presets are parked datasets until you import or copy them back into DEFAULT.");
        ImGui.TextWrapped(editorStatus);
        if (!string.IsNullOrWhiteSpace(presetFileConflictStatus))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.97f, 0.58f, 0.31f, 1f));
            ImGui.TextWrapped(presetFileConflictStatus);
            ImGui.PopStyleColor();
        }

        if (dirty)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.97f, 0.84f, 0.31f, 1f));
            ImGui.TextUnformatted("Unsaved rule edits");
            ImGui.PopStyleColor();
        }

        var visibleRuleIndices = BuildVisibleRuleIndices();
        DrawToolbar(visibleRuleIndices);
        ImGui.Spacing();
        if (!draftStructureChangedThisDraw)
            DrawRulesTable(visibleRuleIndices, activeDraftRule);
    }

    private void DrawToolbar(IReadOnlyList<int> visibleRuleIndices)
    {
        if (ImGui.Button("[GUIDE]"))
            plugin.OpenRuleGuideUi();
        ImGui.SameLine();
        DrawPresetToolbar();
        if (draftStructureChangedThisDraw)
            return;

        ImGui.SameLine();
        if (ImGui.Button("+ Row"))
        {
            AddDraftRule(CreateNewDraftRule(out var status), status);
        }
        if (draftStructureChangedThisDraw)
            return;

        ImGui.SameLine();
        var newRowsUseCurrentArea = plugin.Configuration.RuleEditorNewRowCurrentArea;
        if (ImGui.Checkbox("New rows use current area", ref newRowsUseCurrentArea))
        {
            plugin.Configuration.RuleEditorNewRowCurrentArea = newRowsUseCurrentArea;
            plugin.SaveConfiguration();
        }

        ImGui.SameLine();
        using (new ImGuiDisabledBlock(!plugin.Configuration.RuleEditorNewRowCurrentArea))
        {
            var newRowsUseCurrentLabel = plugin.Configuration.RuleEditorNewRowCurrentLabel;
            if (ImGui.Checkbox("also current label", ref newRowsUseCurrentLabel))
            {
                plugin.Configuration.RuleEditorNewRowCurrentLabel = newRowsUseCurrentLabel;
                plugin.SaveConfiguration();
            }
        }

        ImGui.SameLine();
        using (new ImGuiDisabledBlock(!dirty && string.IsNullOrWhiteSpace(presetFileConflictStatus)))
        {
            if (ImGui.Button("Save"))
                RequestSaveDraft();
        }
        DrawSaveConflictConfirmation();
        if (draftStructureChangedThisDraw)
            return;

        ImGui.SameLine();
        if (ImGui.Button("Reload From Disk"))
        {
            if (dirty)
                ImGui.OpenPopup("ADSConfirmReloadRuleDraft");
            else
                RefreshDraft($"Reloaded preset {selectedPresetName} from disk.");
        }
        DrawReloadDraftConfirmation();
        if (draftStructureChangedThisDraw)
            return;

        ImGui.SameLine();
        if (ImGui.Button("Open JSON"))
            plugin.OpenPath(plugin.ObjectPriorityRuleService.GetPresetPath(selectedPresetName));

        var filterMode = Math.Clamp(plugin.Configuration.RuleEditorFilterMode, 0, FilterModeLabels.Length - 1);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(190f);
        if (ImGui.Combo("Rows", ref filterMode, FilterModeLabels, FilterModeLabels.Length))
        {
            plugin.Configuration.RuleEditorFilterMode = filterMode;
            plugin.SaveConfiguration();
        }

        ImGui.SameLine();
        ImGui.Checkbox("Sort By Duty", ref sortByDutyName);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(260f);
        ImGui.InputTextWithHint("##ADSRuleTextFilter", "filter duty/name/class/layer/notes", ref ruleTextFilter, 128);

        ImGui.TextUnformatted($"Rows shown: {visibleRuleIndices.Count} / {draft.Rules.Count}");
        DrawSelectionToolbar(visibleRuleIndices);
    }

    private void DrawReloadDraftConfirmation()
    {
        if (!ImGui.BeginPopup("ADSConfirmReloadRuleDraft"))
            return;

        ImGui.TextWrapped($"Reload {selectedPresetName} from disk and discard the unsaved in-memory draft?");
        ImGui.TextWrapped("If the disk file is missing or invalid, ADS will keep the current draft unchanged.");
        if (ImGui.Button("Reload and discard"))
        {
            RefreshDraft($"Reloaded preset {selectedPresetName} from disk.");
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private void RequestSaveDraft()
    {
        if (!TryCaptureSelectedPresetFileState(out var currentState, out var stateStatus))
        {
            presetFileConflictStatus = $"Disk conflict: {stateStatus} The draft was kept; saving requires explicit overwrite confirmation.";
            ImGui.OpenPopup("ADSConfirmSaveRuleDraftConflict");
            return;
        }

        lastObservedPresetFileState = currentState;
        nextPresetFilePollUtc = DateTime.UtcNow + PresetFilePollInterval;
        if (!loadedPresetFileState.HasValue || currentState != loadedPresetFileState.Value)
        {
            presetFileConflictStatus = currentState.Exists
                ? $"Disk conflict: preset {selectedPresetName} changed on disk after this draft was loaded. The in-memory draft was kept; saving requires explicit overwrite confirmation."
                : $"Disk conflict: preset {selectedPresetName} is missing on disk. The in-memory draft was kept; saving would recreate it and requires explicit confirmation.";
            ImGui.OpenPopup("ADSConfirmSaveRuleDraftConflict");
            return;
        }

        presetFileConflictStatus = string.Empty;
        SaveDraft();
    }

    private void DrawSaveConflictConfirmation()
    {
        if (!ImGui.BeginPopup("ADSConfirmSaveRuleDraftConflict"))
            return;

        ImGui.TextWrapped(presetFileConflictStatus);
        ImGui.TextWrapped($"Overwrite the current {selectedPresetName} file with this in-memory draft?");
        if (ImGui.Button("Save and overwrite"))
        {
            SaveDraft();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private void SaveDraft()
    {
        if (!plugin.ObjectPriorityRuleService.SaveManifest(selectedPresetName, draft))
        {
            editorStatus = plugin.ObjectPriorityRuleService.LastLoadStatus;
            return;
        }

        dirty = false;
        unsavedNewRules.Clear();
        undoState.MarkRestoreDirty();
        presetFileConflictStatus = string.Empty;
        editorStatus = $"Saved preset {selectedPresetName}.";
        RememberSelectedPresetFileBaseline();
    }

    private void DrawMatureProposalsBanner()
    {
        var ruleService = plugin.ObjectPriorityRuleService;
        if (!ruleService.IsMatureProposalsPreset(selectedPresetName))
            return;

        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.82f, 1f, 1f));
        ImGui.TextWrapped("MATURE-PROPOSALS is an editable non-default preset. Every successful Save restarts its 24-hour remote-refresh countdown; copy it to another preset for unrestricted editing.");
        ImGui.PopStyleColor();
        var refreshDecision = plugin.RemoteJsonUpdateService.GetMatureProposalRefreshDecision();
        ImGui.TextWrapped(refreshDecision.Status);
        if (refreshDecision.NextRefreshUtc.HasValue)
        {
            var remaining = refreshDecision.NextRefreshUtc.Value - DateTime.UtcNow;
            ImGui.TextUnformatted(
                remaining > TimeSpan.Zero
                    ? $"Refresh countdown: {FormatCountdown(remaining)} (until {refreshDecision.NextRefreshUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss})"
                    : "Refresh countdown: ready now");
        }

        if (ImGui.SmallButton("Refresh Now"))
            ImGui.OpenPopup("ADSConfirmMatureProposalReset");
        ImGui.SameLine();
        using (new ImGuiDisabledBlock(dirty || !string.IsNullOrWhiteSpace(presetFileConflictStatus)))
        {
            if (ImGui.SmallButton("Validate & Reveal JSON"))
            {
                if (ruleService.TryLoadManifest(selectedPresetName, out _, out var status))
                {
                    plugin.RevealPathInExplorer(ruleService.MatureProposalsPresetPath);
                    editorStatus = $"Validated and revealed the clean saved proposal JSON. {status}";
                }
                else
                {
                    editorStatus = status;
                }
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy JSON Path"))
            {
                if (ruleService.TryLoadManifest(selectedPresetName, out _, out var status))
                {
                    ImGui.SetClipboardText(ruleService.MatureProposalsPresetPath);
                    editorStatus = $"Validated the clean saved proposal JSON and copied its exact path. {status}";
                }
                else
                {
                    editorStatus = status;
                }
            }
        }

        DrawMatureProposalResetConfirmation();
    }

    private void DrawMatureProposalResetConfirmation()
    {
        if (!ImGui.BeginPopup("ADSConfirmMatureProposalReset"))
            return;

        ImGui.TextWrapped("Refresh MATURE-PROPOSALS from the remote candidate reviewed from community/sheet sources, bypassing the 24-hour countdown?");
        if (dirty)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.97f, 0.58f, 0.31f, 1f));
            ImGui.TextWrapped("The disk preset will refresh while this unsaved in-memory draft is retained. Save will overwrite the refreshed disk file; Reload From Disk will discard this draft.");
            ImGui.PopStyleColor();
        }

        if (ImGui.Button("Refresh Now"))
        {
            plugin.ForceMatureProposalRefresh();
            editorStatus = plugin.RemoteJsonUpdateService.LastUpdateStatus;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private static string FormatCountdown(TimeSpan remaining)
    {
        var totalHours = Math.Max(0, (int)remaining.TotalHours);
        return $"{totalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    private void DrawSelectionToolbar(IReadOnlyList<int> visibleRuleIndices)
    {
        if (ImGui.Button("Select Visible"))
        {
            foreach (var ruleIndex in visibleRuleIndices)
                selectedRules.Add(draft.Rules[ruleIndex]);
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear Selection"))
            selectedRules.Clear();

        ImGui.SameLine();
        using (new ImGuiDisabledBlock(selectedRules.Count == 0))
        {
            if (ImGui.Button("Delete Selected"))
                ImGui.OpenPopup("ADSConfirmDeleteSelectedRules");
        }

        ImGui.SameLine();
        using (new ImGuiDisabledBlock(selectedRules.Count == 0))
        {
            if (ImGui.SmallButton("Export Duties"))
            {
                var groupKeys = selectedRules.Select(rule => ResolveImportGroup(rule).Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                ExportPartialManifest(
                    draft.Rules.Where(rule => groupKeys.Contains(ResolveImportGroup(rule).Key)),
                    "complete selected duty groups");
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Export Delta"))
                ExportPartialManifest(draft.Rules.Where(selectedRules.Contains), "selected delta rows");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Export Filter"))
            ExportPartialManifest(visibleRuleIndices.Select(index => draft.Rules[index]), "current filtered rows");

        ImGui.SameLine();
        using (new ImGuiDisabledBlock(!undoState.CanUndo))
        {
            var label = undoState.CanUndo ? $"Undo {undoState.Label}" : "Undo";
            if (ImGui.Button(label))
            {
                RestoreUndoState();
                return;
            }
        }

        var visibleSelected = visibleRuleIndices.Count(index => selectedRules.Contains(draft.Rules[index]));
        var hiddenSelected = Math.Max(0, selectedRules.Count - visibleSelected);
        ImGui.SameLine();
        ImGui.TextUnformatted($"Selected: {selectedRules.Count} ({hiddenSelected} hidden)");
        DrawDeleteSelectedConfirmation(visibleRuleIndices);
    }

    private void ExportPartialManifest(IEnumerable<ObjectPriorityRule> rules, string label)
    {
        var export = new ObjectPriorityRuleManifest
        {
            SchemaVersion = draft.SchemaVersion,
            Description = $"Partial ADS rule export: {label}.",
            Rules = rules.Select(CloneRule).ToList(),
        };
        ImGui.SetClipboardText(JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true }));
        editorStatus = $"Copied {export.Rules.Count} {label} rule(s) to the clipboard.";
    }

    private void DrawDeleteSelectedConfirmation(IReadOnlyList<int> visibleRuleIndices)
    {
        if (!ImGui.BeginPopup("ADSConfirmDeleteSelectedRules"))
            return;

        var visibleSelected = visibleRuleIndices.Count(index => selectedRules.Contains(draft.Rules[index]));
        var hiddenSelected = Math.Max(0, selectedRules.Count - visibleSelected);
        var impact = BuildSelectionImpact(selectedRules);
        ImGui.TextWrapped($"Delete exactly {selectedRules.Count} selected rule(s)? {hiddenSelected} selected rule(s) are hidden by the current filters.");
        ImGui.TextWrapped($"Affected duties ({impact.Duties.Count}): {(impact.Duties.Count == 0 ? "none" : string.Join(", ", impact.Duties))}");
        ImGui.TextWrapped($"Global rows: {impact.GlobalRows}; unresolved duty rows: {impact.UnresolvedRows}.");
        ImGui.TextWrapped("This can be undone once, until the next draft mutation, reload, or preset switch. Filters and selection do not invalidate it.");

        if (ImGui.Button("Delete"))
        {
            DeleteSelectedRules();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private SelectionImpact BuildSelectionImpact(IEnumerable<ObjectPriorityRule> rules)
    {
        var duties = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var globals = 0;
        var unresolved = 0;
        foreach (var rule in rules)
        {
            if (DutyRuleCoverageHelper.IsGlobalRule(rule))
            {
                globals++;
                continue;
            }

            var matches = plugin.DutyCatalogService.Entries
                .Where(entry => DutyRuleCoverageHelper.RuleAssociatesWithDuty(rule, entry))
                .Take(2)
                .ToList();
            if (matches.Count != 1)
                unresolved++;
            else
                duties.Add(matches[0].EnglishName);
        }

        return new SelectionImpact(duties.ToList(), globals, unresolved);
    }

    private void DrawCurrentScopeBanner()
    {
        var context = plugin.DutyContextService.Current;
        var duty = context.CurrentDuty?.EnglishName ?? (context.InInstancedDuty ? "(uncataloged duty)" : "GLOBAL / outside duty");
        var activeLabel = context.InInstancedDuty
            ? plugin.ObjectPriorityRuleService.GetActiveLayerName(context) ?? "(unknown)"
            : "(none)";
        ImGui.TextWrapped($"Current scope: duty {duty} | Terr {context.TerritoryTypeId} | CFC {context.ContentFinderConditionId} | Label {activeLabel}");
    }

    private void DrawDutyFilterBanner()
    {
        if (dutyFilter is null)
            return;

        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.82f, 1f, 1f));
        ImGui.TextWrapped($"Duty filter: {dutyFilter.EnglishName} (CFC {dutyFilter.ContentFinderConditionId}, Terr {dutyFilter.TerritoryTypeId}). Diagnostic association is used, so conflicting redundant scope fields remain visible.");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear Duty Filter"))
        {
            dutyFilter = null;
            dutyFilterIdentity = null;
        }
    }

    private ObjectPriorityRule? DrawActiveRuleBanner()
    {
        var activeRule = plugin.ObjectivePlannerService.Current.ActiveRule;
        if (activeRule is null)
        {
            ImGui.TextDisabled("No authored rule drives the current objective.");
            return null;
        }

        var classification = string.IsNullOrWhiteSpace(activeRule.Classification)
            ? "(unclassified)"
            : activeRule.Classification.Trim();
        ImGui.TextWrapped($"Active rule: {classification} — {GetActiveRuleTarget(activeRule)} — priority {activeRule.Priority}");

        var activeDraftRule = TryGetAlignedActiveDraftRule(activeRule, out var unavailableReason);
        using (new ImGuiDisabledBlock(activeDraftRule is null))
        {
            if (ImGui.SmallButton("Go to active row") && activeDraftRule is not null)
            {
                if (FindVisibleDisplayIndex(BuildVisibleRuleIndices(), activeDraftRule) < 0)
                {
                    dutyFilter = null;
                    dutyFilterIdentity = null;
                    dutyFilterTerritoryUnique = false;
                    dutyFilterNameUnique = false;
                    ruleTextFilter = string.Empty;
                    if (plugin.Configuration.RuleEditorFilterMode != 0)
                    {
                        plugin.Configuration.RuleEditorFilterMode = 0;
                        plugin.SaveConfiguration();
                    }

                    editorStatus = "Cleared the duty and text filters, selected All, and queued the exact active DEFAULT row.";
                }

                pendingScrollRule = activeDraftRule;
            }
        }

        if (!string.IsNullOrWhiteSpace(unavailableReason))
            ImGui.TextDisabled(unavailableReason);

        return activeDraftRule;
    }

    private ObjectPriorityRule? TryGetAlignedActiveDraftRule(ObjectPriorityRule activeRule, out string reason)
    {
        if (!plugin.ObjectPriorityRuleService.IsDefaultPreset(selectedPresetName))
        {
            reason = $"Exact highlighting unavailable while viewing preset {selectedPresetName}; the runtime rule comes from DEFAULT.";
            return null;
        }

        if (dirty)
        {
            reason = "Exact highlighting unavailable while the DEFAULT draft has unsaved edits.";
            return null;
        }

        var runtimeRules = plugin.ObjectPriorityRuleService.Current.Rules;
        var runtimeIndex = runtimeRules.FindIndex(rule => ReferenceEquals(rule, activeRule));
        if (runtimeIndex < 0)
        {
            reason = "Exact highlighting unavailable because the planner snapshot and current DEFAULT runtime rules are no longer aligned.";
            return null;
        }

        if (!RulesAlignByIndex(runtimeRules, draft.Rules))
        {
            reason = "Exact highlighting unavailable because the clean DEFAULT draft and current runtime rows are misaligned.";
            return null;
        }

        reason = string.Empty;
        return draft.Rules[runtimeIndex];
    }

    private static bool RulesAlignByIndex(IReadOnlyList<ObjectPriorityRule> runtimeRules, IReadOnlyList<ObjectPriorityRule> draftRules)
    {
        if (runtimeRules.Count != draftRules.Count)
            return false;

        for (var index = 0; index < runtimeRules.Count; index++)
        {
            if (!RuleValuesEqual(runtimeRules[index], draftRules[index]))
                return false;
        }

        return true;
    }

    private static bool RuleValuesEqual(ObjectPriorityRule left, ObjectPriorityRule right)
        => left.Enabled == right.Enabled
           && left.TerritoryTypeId == right.TerritoryTypeId
           && left.ContentFinderConditionId == right.ContentFinderConditionId
           && string.Equals(left.DutyEnglishName, right.DutyEnglishName, StringComparison.Ordinal)
           && string.Equals(left.Alliance, right.Alliance, StringComparison.Ordinal)
           && string.Equals(left.ObjectKind, right.ObjectKind, StringComparison.Ordinal)
           && left.BaseId == right.BaseId
           && string.Equals(left.ObjectName, right.ObjectName, StringComparison.Ordinal)
           && string.Equals(left.NameMatchMode, right.NameMatchMode, StringComparison.Ordinal)
           && string.Equals(left.Classification, right.Classification, StringComparison.Ordinal)
           && string.Equals(left.DestinationType, right.DestinationType, StringComparison.Ordinal)
           && string.Equals(left.Layer, right.Layer, StringComparison.Ordinal)
           && string.Equals(left.MapCoordinates, right.MapCoordinates, StringComparison.Ordinal)
           && string.Equals(left.WorldCoordinates, right.WorldCoordinates, StringComparison.Ordinal)
           && string.Equals(left.ObjectMapCoordinates, right.ObjectMapCoordinates, StringComparison.Ordinal)
           && string.Equals(left.ObjectWorldCoordinates, right.ObjectWorldCoordinates, StringComparison.Ordinal)
           && left.ObjectMatchRadius == right.ObjectMatchRadius
           && left.Priority == right.Priority
           && left.PriorityVerticalRadius == right.PriorityVerticalRadius
           && left.MaxDistance == right.MaxDistance
           && left.WaitAtDestinationSeconds == right.WaitAtDestinationSeconds
           && left.WaitAfterInteractSeconds == right.WaitAfterInteractSeconds
           && string.Equals(left.Notes, right.Notes, StringComparison.Ordinal);

    private static string GetActiveRuleTarget(ObjectPriorityRule rule)
    {
        var name = NormalizeEditorText(rule.ObjectName);
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        var coordinates = NormalizeEditorText(GetUnifiedCoordinatesValue(rule));
        return string.IsNullOrWhiteSpace(coordinates) ? "(unnamed)" : coordinates;
    }

    private ObjectPriorityRule CreateNewDraftRule(out string status)
    {
        var rule = plugin.ObjectPriorityRuleService.CreateBlankRule();
        if (!plugin.Configuration.RuleEditorNewRowCurrentArea)
        {
            status = "Added a new global rule row.";
            return rule;
        }

        var context = plugin.DutyContextService.Current;
        if (!TrySeedCurrentArea(rule, context, out var areaStatus))
        {
            status = $"Added a new global rule row. {areaStatus}";
            return rule;
        }

        if (!plugin.Configuration.RuleEditorNewRowCurrentLabel)
        {
            status = $"Added a new rule row scoped to current area. {areaStatus}";
            return rule;
        }

        var activeLayer = plugin.ObjectPriorityRuleService.GetActiveLayerName(context);
        if (string.IsNullOrWhiteSpace(activeLayer))
        {
            status = $"Added a new rule row scoped to current area. {areaStatus} Active label unavailable; Layer left blank.";
            return rule;
        }

        rule.Layer = activeLayer;
        status = $"Added a new rule row scoped to current area and label '{activeLayer}'. {areaStatus}";
        return rule;
    }

    private static bool TrySeedCurrentArea(ObjectPriorityRule rule, DutyContextSnapshot context, out string status)
    {
        if (!context.InInstancedDuty)
        {
            status = "Current area unavailable outside duty.";
            return false;
        }

        if (context.TerritoryTypeId == 0
            && context.ContentFinderConditionId == 0
            && context.CurrentDuty is null)
        {
            status = "Current area context is missing.";
            return false;
        }

        rule.DutyEnglishName = context.CurrentDuty?.EnglishName ?? string.Empty;
        rule.TerritoryTypeId = context.TerritoryTypeId;
        rule.ContentFinderConditionId = context.ContentFinderConditionId;
        var duty = string.IsNullOrWhiteSpace(rule.DutyEnglishName) ? "uncataloged duty" : rule.DutyEnglishName;
        status = $"Scope: {duty}, Terr {rule.TerritoryTypeId}, CFC {rule.ContentFinderConditionId}.";
        return true;
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

                RequestPresetSwitch(presetName);
            }

            ImGui.EndCombo();
        }

        if (openPresetSwitchConfirmation)
        {
            openPresetSwitchConfirmation = false;
            ImGui.OpenPopup("ADSConfirmRulePresetSwitch");
        }

        DrawPresetSwitchConfirmation();
        if (draftStructureChangedThisDraw)
            return;

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
        using (new ImGuiDisabledBlock(
                   plugin.ObjectPriorityRuleService.IsDefaultPreset(selectedPresetName)
                   || plugin.ObjectPriorityRuleService.IsMatureProposalsPreset(selectedPresetName)))
        {
            if (ImGui.SmallButton("-"))
                DeleteCurrentPreset();
        }

        if (plugin.ObjectPriorityRuleService.IsDefaultPreset(selectedPresetName))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("@"))
                ResetDefaultDraftFromCache();
        }

        if (openImportPreview && importPreview is not null)
        {
            openImportPreview = false;
            ImGui.OpenPopup("ADSManifestImportPreview");
        }

        DrawManifestImportPreviewPopup();
    }

    private void EnsureDraftLoaded()
    {
        if (draftLoaded)
            return;

        RefreshDraft($"Loaded preset {selectedPresetName}.");
    }

    private void RefreshDraft(string status)
    {
        if (!plugin.ObjectPriorityRuleService.TryLoadManifest(selectedPresetName, out var loadedDraft, out var loadStatus))
        {
            draftLoaded = true;
            editorStatus = $"Preset {selectedPresetName} was not loaded; the current in-memory draft was kept unchanged. {loadStatus}";
            RememberObservedSelectedPresetFileState();
            presetFileConflictStatus = $"Disk conflict: preset {selectedPresetName} could not be loaded. The current in-memory draft was kept. {loadStatus}";
            SyncDiskTransferPath();
            return;
        }

        ApplyLoadedDraft(loadedDraft, $"{status} {loadStatus}");
    }

    private void ApplyLoadedDraft(ObjectPriorityRuleManifest loadedDraft, string status)
    {
        draft = loadedDraft;
        draftLoaded = true;
        dirty = false;
        dutySearch = string.Empty;
        dutySearchRow = -1;
        unsavedNewRules.Clear();
        selectedRules.Clear();
        pendingScrollRule = null;
        undoState.Invalidate();
        importPreview = null;
        openImportPreview = false;
        pendingPresetSwitchName = string.Empty;
        openPresetSwitchConfirmation = false;
        knownLayerSelectorsByTerritory.Clear();
        draftStructureChangedThisDraw = true;
        editorStatus = status;
        SyncDiskTransferPath();
        RememberSelectedPresetFileBaseline();
    }

    private void PollSelectedPresetFile()
    {
        var now = DateTime.UtcNow;
        if (!draftLoaded || now < nextPresetFilePollUtc)
            return;

        nextPresetFilePollUtc = now + PresetFilePollInterval;
        if (!TryCaptureSelectedPresetFileState(out var currentState, out var stateStatus))
        {
            presetFileConflictStatus = $"Disk conflict: {stateStatus} The current in-memory draft was kept.";
            return;
        }

        if (lastObservedPresetFileState.HasValue && currentState == lastObservedPresetFileState.Value)
            return;

        lastObservedPresetFileState = currentState;
        if (loadedPresetFileState.HasValue && currentState == loadedPresetFileState.Value)
        {
            presetFileConflictStatus = string.Empty;
            editorStatus = $"Preset {selectedPresetName} on disk again matches the version loaded by the editor.";
            return;
        }

        if (!currentState.Exists)
        {
            presetFileConflictStatus = $"Disk conflict: preset {selectedPresetName} is missing on disk. The current in-memory draft was kept.";
            return;
        }

        if (!plugin.ObjectPriorityRuleService.TryLoadManifest(selectedPresetName, out var loadedDraft, out var loadStatus))
        {
            presetFileConflictStatus = $"Disk conflict: preset {selectedPresetName} changed on disk but could not be loaded. The current in-memory draft was kept. {loadStatus}";
            return;
        }

        if (dirty)
        {
            presetFileConflictStatus = $"Disk conflict: preset {selectedPresetName} changed on disk after this draft was loaded. Unsaved edits were kept. Save overwrites the disk refresh; Reload From Disk discards this draft.";
            return;
        }

        ApplyLoadedDraft(
            loadedDraft,
            $"Preset {selectedPresetName} changed on disk and the clean editor draft reloaded automatically. {loadStatus}");
    }

    private bool TryCaptureSelectedPresetFileState(out PresetFileState state, out string status)
    {
        var path = plugin.ObjectPriorityRuleService.GetPresetPath(selectedPresetName);
        try
        {
            var exists = File.Exists(path);
            state = new PresetFileState(exists, exists ? File.GetLastWriteTimeUtc(path) : null);
            status = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            state = default;
            status = $"Could not inspect preset {selectedPresetName} at {path}: {ex.Message}";
            return false;
        }
    }

    private void RememberSelectedPresetFileBaseline()
    {
        nextPresetFilePollUtc = DateTime.UtcNow + PresetFilePollInterval;
        if (!TryCaptureSelectedPresetFileState(out var state, out var status))
        {
            loadedPresetFileState = null;
            lastObservedPresetFileState = null;
            presetFileConflictStatus = $"Disk conflict: {status} Future saves require explicit overwrite confirmation.";
            return;
        }

        loadedPresetFileState = state;
        lastObservedPresetFileState = state;
        presetFileConflictStatus = string.Empty;
    }

    private void RememberObservedSelectedPresetFileState()
    {
        nextPresetFilePollUtc = DateTime.UtcNow + PresetFilePollInterval;
        if (TryCaptureSelectedPresetFileState(out var state, out _))
            lastObservedPresetFileState = state;
    }

    public void CreateRuleFromExplorer(ObjectPriorityRule seededRule)
    {
        EnsureDraftLoaded();
        AddDraftRule(seededRule, $"Seeded a new rule from Object Explorer into preset {selectedPresetName}.");
        IsOpen = true;
    }

    private void DrawRulesTable(IReadOnlyList<int> visibleRuleIndices, ObjectPriorityRule? activeDraftRule)
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

        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 100f);
        ImGui.TableSetupColumn("Duty", ImGuiTableColumnFlags.WidthFixed, 260f);
        ImGui.TableSetupColumn("Terr", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("CFC", ImGuiTableColumnFlags.WidthFixed, 48f);
        ImGui.TableSetupColumn("Alliance", ImGuiTableColumnFlags.WidthFixed, 76f);
        ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 110f);
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
        ImGui.TableSetupColumn("Select", ImGuiTableColumnFlags.WidthFixed, 52f);
        ImGui.TableSetupScrollFreeze(1, 1);
        DrawHeaderRow();

        var pendingDisplayIndex = FindVisibleDisplayIndex(visibleRuleIndices, pendingScrollRule);
        var clipper = ImGui.ImGuiListClipper();
        try
        {
            clipper.Begin(visibleRuleIndices.Count);
            if (pendingDisplayIndex >= 0)
                clipper.ForceDisplayRangeByIndices(pendingDisplayIndex, pendingDisplayIndex + 1);

            while (clipper.Step())
            {
                for (var displayIndex = clipper.DisplayStart; displayIndex < clipper.DisplayEnd; displayIndex++)
                {
                    var ruleIndex = visibleRuleIndices[displayIndex];
                    var rule = draft.Rules[ruleIndex];
                    var isActiveRule = ReferenceEquals(rule, activeDraftRule);
                    var rowChanged = false;
                    var semantics = RuleSemanticsCatalog.Find(rule.Classification ?? string.Empty);
                    var missingRequiredFields = RuleSemanticsCatalog.GetMissingRequiredFields(rule).ToHashSet(StringComparer.Ordinal);
                    ImGui.PushID(ruleIndex);
                    ImGui.TableNextRow();
                    if (isActiveRule)
                    {
                        var rowHighlight = ImGui.ColorConvertFloat4ToU32(new Vector4(0.03f, 0.29f, 0.35f, 0.78f));
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, rowHighlight);
                    }
                    else if (unsavedNewRules.Contains(rule))
                    {
                        var rowHighlight = ImGui.ColorConvertFloat4ToU32(new Vector4(0.20f, 0.34f, 0.18f, 0.65f));
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, rowHighlight);
                    }

                    ImGui.TableSetColumnIndex(0);
                    DrawFieldCue(semantics, nameof(ObjectPriorityRule.Enabled), missingRequiredFields);
                    var enabled = rule.Enabled;
                    if (ImGui.Checkbox("##Enabled", ref enabled))
                    {
                        rule.Enabled = enabled;
                        rowChanged = true;
                    }
                    if (isActiveRule)
                    {
                        ImGui.SameLine(0f, 4f);
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.20f, 0.92f, 1f, 1f));
                        ImGui.TextUnformatted("ACTIVE");
                        ImGui.PopStyleColor();
                    }
                    if (ReferenceEquals(rule, pendingScrollRule))
                    {
                        ImGui.SetScrollHereY(0.25f);
                        pendingScrollRule = null;
                    }

                    ImGui.TableSetColumnIndex(1);
                    DrawFieldCue(semantics, nameof(ObjectPriorityRule.DutyEnglishName), missingRequiredFields);
                    if (DrawDutyCell(ruleIndex, rule))
                        rowChanged = true;

                    ImGui.TableSetColumnIndex(2);
                    DrawFieldCue(semantics, nameof(ObjectPriorityRule.TerritoryTypeId), missingRequiredFields);
                    if (EditUintCell("##TerritoryTypeId", rule.TerritoryTypeId, out var territoryTypeId))
                    {
                        rule.TerritoryTypeId = territoryTypeId;
                        rowChanged = true;
                    }

                    ImGui.TableSetColumnIndex(3);
                    DrawFieldCue(semantics, nameof(ObjectPriorityRule.ContentFinderConditionId), missingRequiredFields);
                    if (EditUintCell("##ContentFinderConditionId", rule.ContentFinderConditionId, out var contentFinderConditionId))
                    {
                        rule.ContentFinderConditionId = contentFinderConditionId;
                        rowChanged = true;
                    }

                    ImGui.TableSetColumnIndex(4);
                    DrawFieldCue(semantics, nameof(ObjectPriorityRule.Alliance), missingRequiredFields);
                    if (DrawAllianceCell(rule, ruleIndex))
                        rowChanged = true;

                    ImGui.TableSetColumnIndex(5);
                    DrawFieldCue(semantics, nameof(ObjectPriorityRule.ObjectKind), missingRequiredFields);
                    if (DrawObjectKindCell(rule, ruleIndex))
                        rowChanged = true;

                    ImGui.TableSetColumnIndex(6);
                    DrawFieldCue(semantics, nameof(ObjectPriorityRule.ObjectName), missingRequiredFields);
                    if (EditTextCell("##ObjectName", rule.ObjectName, 128, out var objectName))
                    {
                        rule.ObjectName = objectName;
                        rowChanged = true;
                    }

                    ImGui.TableSetColumnIndex(7);
                    DrawFieldCue(semantics, nameof(ObjectPriorityRule.NameMatchMode), missingRequiredFields);
                    var matchModeIndex = Math.Max(0, Array.IndexOf(NameMatchModes, string.IsNullOrWhiteSpace(rule.NameMatchMode) ? "Exact" : rule.NameMatchMode));
                    if (ImGui.Combo("##NameMatchMode", ref matchModeIndex, NameMatchModes, NameMatchModes.Length))
                    {
                        rule.NameMatchMode = NameMatchModes[matchModeIndex];
                        rowChanged = true;
                    }

                    ImGui.TableSetColumnIndex(8);
                    DrawFieldCue(semantics, nameof(ObjectPriorityRule.Classification), missingRequiredFields);
                    var classificationIndex = Math.Max(0, Array.IndexOf(ClassificationValues, rule.Classification ?? string.Empty));
                    ImGui.SetNextItemWidth(-30f);
                    if (ImGui.Combo("##Classification", ref classificationIndex, ClassificationLabels, ClassificationLabels.Length))
                    {
                        rule.Classification = ClassificationValues[classificationIndex];
                        rowChanged = true;
                    }
                    var classificationHovered = ImGui.IsItemHovered();
                    var classificationSemantics = RuleSemanticsCatalog.Find(rule.Classification ?? string.Empty);
                    ImGui.SameLine();
                    if (ImGui.SmallButton("?"))
                        ImGui.OpenPopup("ADSClassHelp");
                    DrawClassHelpPopup(classificationSemantics);
                    if (classificationHovered && classificationSemantics is not null)
                    {
                        ImGui.BeginTooltip();
                        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35f);
                        ImGui.TextUnformatted(classificationSemantics.Behavior);
                        ImGui.Separator();
                        ImGui.TextWrapped($"Relevant visible fields: {string.Join(", ", RuleSemanticsCatalog.GetRelevantEditorFieldLabels(classificationSemantics))}");
                        ImGui.PopTextWrapPos();
                        ImGui.EndTooltip();
                    }

                    ImGui.TableSetColumnIndex(9);
                    DrawFieldCue(semantics, nameof(ObjectPriorityRule.Layer), missingRequiredFields);
                    if (DrawLayerCell(rule, ruleIndex))
                    {
                        rowChanged = true;
                    }

                    ImGui.TableSetColumnIndex(10);
                    DrawFieldCue(semantics, GetCoordinateSemanticsField(rule), missingRequiredFields);
                    var unifiedCoordinates = GetUnifiedCoordinatesValue(rule);
                    if (EditTextCell("##Coords", unifiedCoordinates, 48, out var editedCoordinates))
                    {
                        SetUnifiedCoordinatesValue(rule, editedCoordinates);
                        rowChanged = true;
                    }

                    ImGui.TableSetColumnIndex(11);
                    DrawFieldCue(semantics, nameof(ObjectPriorityRule.ObjectMatchRadius), missingRequiredFields);
                    using (new ImGuiDisabledBlock(IsManualDestinationRule(rule) || IsCardinalHoldRule(rule)))
                    {
                        if (EditNullableFloatCell("##ObjectMatchRadius", rule.ObjectMatchRadius, out var objectMatchRadius))
                        {
                            rule.ObjectMatchRadius = objectMatchRadius;
                            rowChanged = true;
                        }
                    }

                    ImGui.TableSetColumnIndex(12);
                    DrawFieldCue(semantics, nameof(ObjectPriorityRule.Priority), missingRequiredFields);
                    if (EditIntCell("##Priority", rule.Priority, out var priority))
                    {
                        rule.Priority = priority;
                        rowChanged = true;
                    }

                    ImGui.TableSetColumnIndex(13);
                    DrawFieldCue(semantics, nameof(ObjectPriorityRule.PriorityVerticalRadius), missingRequiredFields);
                    if (EditFloatCell("##PriorityVerticalRadius", rule.PriorityVerticalRadius, out var priorityVerticalRadius))
                    {
                        rule.PriorityVerticalRadius = priorityVerticalRadius;
                        rowChanged = true;
                    }

                    ImGui.TableSetColumnIndex(14);
                    DrawFieldCue(semantics, nameof(ObjectPriorityRule.MaxDistance), missingRequiredFields);
                    if (EditNullableFloatCell("##MaxDistance", rule.MaxDistance, out var maxDistance))
                    {
                        rule.MaxDistance = maxDistance;
                        rowChanged = true;
                    }
                    if (ImGui.IsItemHovered())
                        DrawDistancePreviewTooltip(rule);

                    ImGui.TableSetColumnIndex(15);
                    DrawFieldCue(semantics, nameof(ObjectPriorityRule.WaitAtDestinationSeconds), missingRequiredFields);
                    if (EditFloatCell("##WaitAtDestinationSeconds", rule.WaitAtDestinationSeconds, out var waitAtDestinationSeconds))
                    {
                        rule.WaitAtDestinationSeconds = waitAtDestinationSeconds;
                        rowChanged = true;
                    }

                    ImGui.TableSetColumnIndex(16);
                    DrawFieldCue(semantics, nameof(ObjectPriorityRule.WaitAfterInteractSeconds), missingRequiredFields);
                    if (EditFloatCell("##WaitAfterInteractSeconds", rule.WaitAfterInteractSeconds, out var waitAfterInteractSeconds))
                    {
                        rule.WaitAfterInteractSeconds = waitAfterInteractSeconds;
                        rowChanged = true;
                    }

                    ImGui.TableSetColumnIndex(17);
                    DrawFieldCue(semantics, nameof(ObjectPriorityRule.Notes), missingRequiredFields);
                    if (EditTextCell("##Notes", rule.Notes, 512, out var notes))
                    {
                        rule.Notes = notes;
                        rowChanged = true;
                    }

                    ImGui.TableSetColumnIndex(18);
                    if (ImGui.SmallButton("B64"))
                        ExportRuleAsBase64(rule);

                    ImGui.TableSetColumnIndex(19);
                    if (ImGui.SmallButton("Paste") && ImportRuleFromClipboard(ruleIndex))
                    {
                        rule = draft.Rules[ruleIndex];
                        rowChanged = true;
                    }

                    ImGui.TableSetColumnIndex(20);
                    var selected = selectedRules.Contains(rule);
                    if (ImGui.Checkbox("##Selected", ref selected))
                    {
                        if (selected)
                            selectedRules.Add(rule);
                        else
                            selectedRules.Remove(rule);
                    }

                    ImGui.PopID();
                    if (rowChanged)
                        MarkOrdinaryEdit();
                }
            }
        }
        finally
        {
            clipper.Destroy();
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
        DrawHeaderCell(4, "Alliance", "Optional alliance-party scope. (Any) is wildcard; A-G fails closed when the live alliance cannot be resolved.");
        DrawHeaderCell(5, "Kind", "Live ObjectKind match. Use blank for wildcard. This is the game object category, not a unique instance id.");
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
        DrawHeaderCell(20, "Select", "Select this row for bulk actions. Selection persists when filters hide the row.");
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

    private static bool IsCardinalHoldRule(ObjectPriorityRule rule)
        => CardinalHoldPolicy.TryParseDirection(rule.Classification, out _);

    private static string GetCoordinateSemanticsField(ObjectPriorityRule rule)
    {
        if (IsCardinalHoldRule(rule)
            || string.Equals(rule.Classification, nameof(InteractableClass.XYZ), StringComparison.OrdinalIgnoreCase)
            || string.Equals(rule.Classification, nameof(InteractableClass.XYZForceMarch), StringComparison.OrdinalIgnoreCase)
            || string.Equals(rule.DestinationType, "XYZ", StringComparison.OrdinalIgnoreCase))
        {
            return nameof(ObjectPriorityRule.WorldCoordinates);
        }

        if (string.Equals(rule.Classification, nameof(InteractableClass.MapXzDestination), StringComparison.OrdinalIgnoreCase)
            || string.Equals(rule.Classification, nameof(InteractableClass.MapXzForceMarch), StringComparison.OrdinalIgnoreCase)
            || string.Equals(rule.DestinationType, "MapXZ", StringComparison.OrdinalIgnoreCase))
        {
            return nameof(ObjectPriorityRule.MapCoordinates);
        }

        return !string.IsNullOrWhiteSpace(rule.ObjectWorldCoordinates)
            ? nameof(ObjectPriorityRule.ObjectWorldCoordinates)
            : nameof(ObjectPriorityRule.ObjectMapCoordinates);
    }

    private static string GetUnifiedCoordinatesValue(ObjectPriorityRule rule)
    {
        if (IsCardinalHoldRule(rule))
            return rule.WorldCoordinates;

        if (IsManualDestinationRule(rule))
            return !string.IsNullOrWhiteSpace(rule.WorldCoordinates) ? rule.WorldCoordinates : rule.MapCoordinates;

        return !string.IsNullOrWhiteSpace(rule.ObjectWorldCoordinates) ? rule.ObjectWorldCoordinates : rule.ObjectMapCoordinates;
    }

    private static void SetUnifiedCoordinatesValue(ObjectPriorityRule rule, string value)
    {
        var normalized = NormalizeCoordinateText(value);
        var partCount = CountCoordinateParts(normalized);
        var isWorldCoordinates = partCount == 3;

        if (IsCardinalHoldRule(rule))
        {
            rule.WorldCoordinates = normalized;
            rule.MapCoordinates = string.Empty;
            rule.ObjectMapCoordinates = string.Empty;
            rule.ObjectWorldCoordinates = string.Empty;
            return;
        }

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

    private static void DrawFieldCue(
        RuleClassificationSemantics? semantics,
        string field,
        IReadOnlySet<string> missingRequiredFields)
    {
        var use = semantics?.Fields.GetValueOrDefault(field, RuleFieldUse.Ignored) ?? RuleFieldUse.Ignored;
        var color = use switch
        {
            RuleFieldUse.Required when missingRequiredFields.Contains(field) => new Vector4(0.72f, 0.12f, 0.12f, 0.70f),
            RuleFieldUse.Required => new Vector4(0.40f, 0.10f, 0.10f, 0.52f),
            RuleFieldUse.Recommended => new Vector4(0.45f, 0.31f, 0.05f, 0.50f),
            RuleFieldUse.Ignored => new Vector4(0.10f, 0.10f, 0.10f, 0.62f),
            _ => Vector4.Zero,
        };

        if (color.W > 0)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.ColorConvertFloat4ToU32(color));
    }

    private void DrawDistancePreviewTooltip(ObjectPriorityRule rule)
    {
        try
        {
            var localPlayer = Plugin.ObjectTable.LocalPlayer;
            if (localPlayer is null)
                return;

            var context = plugin.DutyContextService.Current;
            if (!plugin.ObjectPriorityRuleService.MatchesCurrentDutyScopeForEditor(rule, context))
                return;

            var liveObjects = Plugin.ObjectTable
                .Where(x => x is not null && x.GameObjectId != localPlayer.GameObjectId)
                .Select(x => new RuleDistancePreviewObject(
                    x.ObjectKind,
                    x.BaseId,
                    x.Name.TextValue.Trim(),
                    x.Position,
                    context.MapId))
                .ToList();
            var preview = RuleDistancePreviewResolver.Resolve(
                rule,
                localPlayer.Position,
                (coordinates, playerY) => plugin.ObjectPriorityRuleService.ResolveEditorDistancePreviewMapCoordinates(
                    context,
                    coordinates,
                    playerY),
                liveObjects,
                (candidateRule, liveObject) => plugin.ObjectPriorityRuleService.MatchesEditorDistancePreviewObject(
                    candidateRule,
                    context,
                    liveObject));
            if (preview is null)
                return;

            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 42f);
            ImGui.TextUnformatted(preview.ExactDistance.HasValue
                ? $"{preview.ExactDistanceLabel}: {preview.ExactDistance.Value:0.00}y"
                : $"{preview.ExactDistanceLabel}: unavailable; no matching live object.");
            DrawDistanceComparison(preview);

            if (preview.Kind == RuleDistancePreviewKind.OrdinaryObject)
            {
                ImGui.TextUnformatted($"Live matches: {preview.LiveMatchCount}");
                if (preview.LiveMatchCount > 1)
                {
                    ImGui.TextUnformatted(
                        $"Live distance range: {preview.LiveMatchNearestDistance!.Value:0.00}y - {preview.LiveMatchFarthestDistance!.Value:0.00}y");
                }

                ImGui.Separator();
                ImGui.TextUnformatted($"Planning aid; not ordinary-row Dist gate: {preview.PlanningAidDistance!.Value:0.00}y");
            }

            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
        catch
        {
            // Hover preview is best effort and must never break the editor draw.
        }
    }

    private static void DrawDistanceComparison(RuleDistancePreview preview)
    {
        if (!preview.ConfiguredDistance.HasValue)
        {
            ImGui.TextUnformatted("Configured Dist: blank (no distance cap).");
            return;
        }

        var result = preview.PassesConfiguredDistance switch
        {
            true => "PASS",
            false => "FAIL",
            null => "unavailable",
        };
        ImGui.TextUnformatted($"Configured Dist: {preview.ConfiguredDistance.Value:0.00}y -> {result}");
    }

    private static void DrawClassHelpPopup(RuleClassificationSemantics? semantics)
    {
        if (!ImGui.BeginPopup("ADSClassHelp"))
            return;

        if (semantics is null)
        {
            ImGui.TextWrapped("Unknown class. Choose a listed class before authoring this row.");
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted(semantics.Label);
        ImGui.TextWrapped(semantics.Goal);
        ImGui.TextWrapped(semantics.Behavior);
        ImGui.Separator();
        DrawClassHelpFieldLine(semantics, RuleFieldUse.Required, "Required");
        DrawClassHelpFieldLine(semantics, RuleFieldUse.Recommended, "Recommended");
        DrawClassHelpFieldLine(semantics, RuleFieldUse.Optional, "Optional");
        ImGui.TextWrapped("Ignored fields stay stored; class selection and cues never clear them.");
        ImGui.EndPopup();
    }

    private static void DrawClassHelpFieldLine(
        RuleClassificationSemantics semantics,
        RuleFieldUse use,
        string label)
    {
        var fields = semantics.Fields
            .Where(x => x.Value == use)
            .Select(x => x.Key)
            .ToList();
        ImGui.TextWrapped($"{label}: {(fields.Count == 0 ? "(none)" : string.Join(", ", fields))}");
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

    private static bool DrawAllianceCell(ObjectPriorityRule rule, int ruleIndex)
    {
        var currentLabel = string.IsNullOrWhiteSpace(rule.Alliance)
            ? "(Any)"
            : AllianceScopeParser.IsValidScope(rule.Alliance)
                ? rule.Alliance.Trim().ToUpperInvariant()
                : $"[Invalid] {rule.Alliance.Trim()}";
        ImGui.SetNextItemWidth(-1f);
        if (!ImGui.BeginCombo($"##Alliance{ruleIndex}", currentLabel))
            return false;

        var changed = false;
        if (ImGui.Selectable("(Any)", string.IsNullOrWhiteSpace(rule.Alliance)))
        {
            rule.Alliance = null;
            changed = true;
        }

        foreach (var alliance in AllianceValues)
        {
            var isSelected = string.Equals(rule.Alliance?.Trim(), alliance, StringComparison.OrdinalIgnoreCase);
            if (!ImGui.Selectable(alliance, isSelected))
                continue;

            rule.Alliance = alliance;
            changed = true;
        }

        ImGui.EndCombo();
        return changed;
    }

    private bool DrawLayerCell(ObjectPriorityRule rule, int ruleIndex)
    {
        var territoryTypeId = rule.TerritoryTypeId != 0
            ? rule.TerritoryTypeId
            : plugin.DutyContextService.Current.TerritoryTypeId;
        if (!knownLayerSelectorsByTerritory.TryGetValue(territoryTypeId, out var knownLayers))
        {
            knownLayers = plugin.ObjectPriorityRuleService.GetKnownLayerSelectors(territoryTypeId);
            knownLayerSelectorsByTerritory[territoryTypeId] = knownLayers;
        }
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

    private int FindVisibleDisplayIndex(IReadOnlyList<int> visibleRuleIndices, ObjectPriorityRule? rule)
    {
        if (rule is null)
            return -1;

        for (var displayIndex = 0; displayIndex < visibleRuleIndices.Count; displayIndex++)
        {
            if (ReferenceEquals(draft.Rules[visibleRuleIndices[displayIndex]], rule))
                return displayIndex;
        }

        return -1;
    }

    private IReadOnlyList<int> BuildVisibleRuleIndices()
    {
        var context = plugin.DutyContextService.Current;
        var filterMode = Math.Clamp(plugin.Configuration.RuleEditorFilterMode, 0, FilterModeLabels.Length - 1);
        IEnumerable<int> indices = Enumerable.Range(0, draft.Rules.Count);
        indices = dutyFilter is null
            ? indices.Where(index => MatchesScopeFilter(draft.Rules[index], context, filterMode))
            : indices.Where(index => MatchesDutyDeepLinkFilter(draft.Rules[index]));

        if (!string.IsNullOrWhiteSpace(ruleTextFilter))
            indices = indices.Where(index => MatchesRuleTextFilter(draft.Rules[index]));

        if (!sortByDutyName)
            return indices.ToList();

        return indices
            .OrderBy(index => GetDutySortLabel(draft.Rules[index]), StringComparer.OrdinalIgnoreCase)
            .ThenBy(index => draft.Rules[index].ContentFinderConditionId)
            .ThenBy(index => draft.Rules[index].TerritoryTypeId)
            .ThenBy(index => NormalizeEditorText(draft.Rules[index].Alliance), StringComparer.OrdinalIgnoreCase)
            .ThenBy(index => draft.Rules[index].ObjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(index => draft.Rules[index].Priority)
            .ToList();
    }

    private bool MatchesDutyDeepLinkFilter(ObjectPriorityRule rule)
    {
        if (!dutyFilterIdentity.HasValue || !DutyRuleCoverageHelper.IsExplicitDutyRule(rule))
            return false;

        var identity = dutyFilterIdentity.Value;
        if (rule.ContentFinderConditionId != 0)
            return rule.ContentFinderConditionId == identity.ContentFinderConditionId;
        if (rule.TerritoryTypeId != 0)
            return dutyFilterTerritoryUnique && rule.TerritoryTypeId == identity.TerritoryTypeId;
        return dutyFilterNameUnique
               && string.Equals(
                   DutyRuleCoverageHelper.NormalizeDutyLookupName(rule.DutyEnglishName),
                   identity.NormalizedEnglishName,
                   StringComparison.OrdinalIgnoreCase);
    }

    private string BuildFilterSignature()
        => string.Join(
            '|',
            Math.Clamp(plugin.Configuration.RuleEditorFilterMode, 0, FilterModeLabels.Length - 1),
            NormalizeEditorText(ruleTextFilter).ToLowerInvariant(),
            dutyFilter?.ContentFinderConditionId ?? 0,
            dutyFilter?.TerritoryTypeId ?? 0,
            dutyFilter is null ? string.Empty : DutyRuleCoverageHelper.NormalizeDutyLookupName(dutyFilter.EnglishName).ToLowerInvariant());

    private bool MatchesScopeFilter(ObjectPriorityRule rule, DutyContextSnapshot context, int filterMode)
    {
        var isGlobal = IsGlobalAreaRule(rule);
        var matchesCurrentArea = plugin.ObjectPriorityRuleService.MatchesCurrentDutyScopeForEditor(rule, context);
        return filterMode switch
        {
            FilterModeGlobalOnly => isGlobal,
            FilterModeCurrentAreaOnly => !isGlobal && matchesCurrentArea,
            FilterModeGlobalAndCurrentArea => matchesCurrentArea,
            FilterModeEffectiveCurrentLabel => isGlobal || (matchesCurrentArea && MatchesCurrentLabelFilter(rule, context)),
            _ => true,
        };
    }

    private bool MatchesCurrentLabelFilter(ObjectPriorityRule rule, DutyContextSnapshot context)
    {
        var selector = GetLayerSelectorForEditor(rule);
        if (string.IsNullOrWhiteSpace(selector))
            return true;

        if (context.MapId == 0)
            return false;

        if (uint.TryParse(selector, out var mapId))
            return mapId == context.MapId;

        var activeLabel = plugin.ObjectPriorityRuleService.GetActiveLayerName(context);
        return !string.IsNullOrWhiteSpace(activeLabel)
               && string.Equals(selector, activeLabel, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesRuleTextFilter(ObjectPriorityRule rule)
    {
        var filter = ruleTextFilter.Trim();
        return ContainsFilterText(GetDutySelectionLabel(rule), filter)
               || ContainsFilterText(rule.Alliance ?? string.Empty, filter)
               || ContainsFilterText(rule.ObjectName, filter)
               || ContainsFilterText(rule.Classification, filter)
               || ContainsFilterText(GetLayerSelectorForEditor(rule), filter)
               || ContainsFilterText(rule.Notes, filter);
    }

    internal static bool IsGlobalAreaRule(ObjectPriorityRule rule)
        => string.IsNullOrWhiteSpace(rule.DutyEnglishName)
           && rule.TerritoryTypeId == 0
           && rule.ContentFinderConditionId == 0
           && string.IsNullOrWhiteSpace(rule.Alliance);

    private static bool ContainsFilterText(string value, string filter)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static string GetLayerSelectorForEditor(ObjectPriorityRule rule)
    {
        var explicitLayer = NormalizeEditorText(rule.Layer);
        if (!string.IsNullOrWhiteSpace(explicitLayer))
            return explicitLayer;

        var legacyLayer = NormalizeEditorText(rule.DestinationType);
        return string.Equals(legacyLayer, "MapXZ", StringComparison.OrdinalIgnoreCase)
               || string.Equals(legacyLayer, "XYZ", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : legacyLayer;
    }

    private static string NormalizeEditorText(string? value)
        => string.Join(' ', (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string GetDutySelectionLabel(ObjectPriorityRule rule)
        => string.IsNullOrWhiteSpace(rule.DutyEnglishName)
            ? "GLOBAL"
            : rule.DutyEnglishName;

    private static string GetDutySortLabel(ObjectPriorityRule rule)
        => string.IsNullOrWhiteSpace(rule.DutyEnglishName)
            ? "0000 GLOBAL"
            : rule.DutyEnglishName;

    private void RequestPresetSwitch(string presetName)
    {
        if (string.Equals(presetName, selectedPresetName, StringComparison.OrdinalIgnoreCase))
            return;

        if (!dirty)
        {
            SwitchPreset(presetName);
            return;
        }

        pendingPresetSwitchName = presetName;
        openPresetSwitchConfirmation = true;
    }

    private void DrawPresetSwitchConfirmation()
    {
        if (!ImGui.BeginPopup("ADSConfirmRulePresetSwitch"))
            return;

        ImGui.TextWrapped($"Switch from {selectedPresetName} to {pendingPresetSwitchName} and discard the unsaved in-memory draft?");
        ImGui.TextWrapped("If the target preset is missing or invalid, ADS will keep the current preset and draft unchanged.");
        if (ImGui.Button("Switch and discard"))
        {
            var targetPresetName = pendingPresetSwitchName;
            pendingPresetSwitchName = string.Empty;
            SwitchPreset(targetPresetName);
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            pendingPresetSwitchName = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void SwitchPreset(string presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName)
            || string.Equals(presetName, selectedPresetName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var previousPreset = selectedPresetName;
        var discardedDirtyDraft = dirty;
        if (!plugin.ObjectPriorityRuleService.TryLoadManifest(presetName, out var loadedDraft, out var loadStatus))
        {
            editorStatus = $"Could not switch from {previousPreset} to {presetName}; the current preset and in-memory draft were kept unchanged. {loadStatus}";
            return;
        }

        selectedPresetName = presetName;
        ApplyLoadedDraft(
            loadedDraft,
            discardedDirtyDraft
                ? $"Switched from {previousPreset} to {selectedPresetName}; confirmed unsaved edits in the previous draft were discarded. {loadStatus}"
                : $"Switched from {previousPreset} to {selectedPresetName}. {loadStatus}");
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
                InvalidateUndoState();
                ClearDraftReferenceState();
                importPreview = null;
                openImportPreview = false;
                draftStructureChangedThisDraw = true;
                editorStatus = $"Created preset {sanitizedName} from the current draft.";
                SyncDiskTransferPath();
                RememberSelectedPresetFileBaseline();
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

        ImGui.TextUnformatted("Manifest disk import preview / export");
        ImGui.SetNextItemWidth(540f);
        ImGui.InputTextWithHint("##PresetDiskPath", "path to .json file", ref diskTransferPath, 512);

        if (ImGui.Button("Import file"))
        {
            if (plugin.ObjectPriorityRuleService.TryImportManifestFromPath(diskTransferPath, out var manifest, out var status))
            {
                PrepareManifestImportPreview(manifest, "disk", status);
                ImGui.CloseCurrentPopup();
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

    private void ResetDefaultDraftFromCache()
    {
        if (plugin.ObjectPriorityRuleService.TryLoadDefaultCacheManifest(out var cacheManifest, out var status))
        {
            InvalidateUndoState();
            draft = CloneManifest(cacheManifest);
            dirty = true;
            ClearDraftReferenceState();
            importPreview = null;
            openImportPreview = false;
            draftStructureChangedThisDraw = true;
            editorStatus = $"Loaded the current DEFAULT cache rules into the draft. {status} Press Save to write them live.";
            RememberSelectedPresetFileBaseline();
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
            PrepareManifestImportPreview(manifest, "clipboard", status);
            return;
        }

        editorStatus = status;
    }

    private void PrepareManifestImportPreview(ObjectPriorityRuleManifest manifest, string sourceLabel, string sourceStatus)
    {
        var previewManifest = CloneManifest(manifest);
        var groups = BuildImportGroups(previewManifest);
        importPreview = new ManifestImportPreview(
            previewManifest,
            groups,
            sourceLabel,
            sourceStatus,
            BuildVisibleRuleIndices(),
            BuildFilterSignature());
        openImportPreview = true;
        var unresolvedCount = groups.Count(group => group.Kind == ImportGroupKind.Unresolved);
        editorStatus = $"Prepared an in-memory {sourceLabel} import preview with {previewManifest.Rules.Count} rule(s) in {groups.Count} group(s), including {unresolvedCount} unresolved group(s). The draft is unchanged.";
    }

    private void DrawManifestImportPreviewPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(820f, 720f), ImGuiCond.FirstUseEver);
        if (!ImGui.BeginPopup("ADSManifestImportPreview"))
            return;

        var preview = importPreview;
        if (preview is null)
        {
            ImGui.TextUnformatted("No manifest import preview is available.");
            if (ImGui.Button("Close"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted($"Manifest import preview: {preview.SourceLabel}");
        ImGui.TextWrapped(preview.SourceStatus);
        ImGui.TextWrapped($"Incoming rules: {preview.Manifest.Rules.Count}. Canonical duty and unresolved scope groups are opt-in and begin unselected.");

        var replaceAll = preview.ReplaceAll;
        if (ImGui.Checkbox("Replace All (full draft replacement)", ref replaceAll))
            preview.ReplaceAll = replaceAll;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Includes every incoming rule and manifest field. This invalidates any existing one-level undo.");

        using (new ImGuiDisabledBlock(preview.ReplaceAll))
        {
            var modeIndex = (int)preview.Mode;
            ImGui.SetNextItemWidth(230f);
            if (ImGui.Combo("Partial mode", ref modeIndex, PartialImportModeLabels, PartialImportModeLabels.Length))
                preview.Mode = (RulePartialImportMode)Math.Clamp(modeIndex, 0, PartialImportModeLabels.Length - 1);
            ImGui.TextWrapped(preview.Mode switch
            {
                RulePartialImportMode.CompleteDuties => "Complete duties replace each selected duty's complete existing group.",
                RulePartialImportMode.Delta => "Delta appends exactly the selected incoming rows without deduplication.",
                _ => "Current filter replaces the exact draft row indices frozen when this preview was created.",
            });

            var includeGlobals = preview.IncludeGlobals;
            if (ImGui.Checkbox("Include incoming globals", ref includeGlobals))
                preview.IncludeGlobals = includeGlobals;
            ImGui.SameLine();
            ImGui.TextDisabled("Globals require this separate opt-in.");
        }

        var filterPreviewValid = preview.Mode != RulePartialImportMode.CurrentFilter
                                 || string.Equals(preview.FrozenFilterSignature, BuildFilterSignature(), StringComparison.Ordinal);
        if (!filterPreviewValid)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.52f, 0.3f, 1f));
            ImGui.TextWrapped("Current-filter preview invalid: filters changed. Cancel and create a new preview.");
            ImGui.PopStyleColor();
        }

        ImGui.Separator();
        if (ImGui.BeginChild("ADSManifestImportGroups", new Vector2(-1f, 500f), true))
        {
            foreach (var group in preview.Groups)
            {
                ImGui.PushID(group.Key);
                if (group.Kind == ImportGroupKind.NoDuty)
                {
                    ImGui.TextColored(new Vector4(0.97f, 0.75f, 0.31f, 1f), $"{group.DisplayLabel} — {group.Rules.Count} rule(s)");
                    ImGui.TextDisabled(preview.ReplaceAll || preview.IncludeGlobals
                        ? "Included by explicit full/global selection."
                        : "Protected; excluded until Include incoming globals is selected.");
                }
                else
                {
                    using (new ImGuiDisabledBlock(preview.ReplaceAll))
                    {
                        var selected = group.Selected;
                        if (ImGui.Checkbox($"{group.DisplayLabel} — {group.Rules.Count} rule(s)", ref selected))
                            group.Selected = selected;
                    }

                    if (group.Kind == ImportGroupKind.Unresolved)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.56f, 0.34f, 1f));
                        ImGui.TextWrapped($"Unresolved catalog scope: {group.Detail}");
                        ImGui.PopStyleColor();
                    }
                    else
                    {
                        ImGui.TextDisabled(group.Detail);
                    }
                }

                ImGui.Separator();
                ImGui.PopID();
            }
        }

        ImGui.EndChild();

        var selectedGroups = preview.Groups.Count(group => group.Selected && group.Kind != ImportGroupKind.NoDuty);
        var partialPlan = preview.ReplaceAll ? null : BuildPartialImportPlan(preview);
        if (partialPlan is not null)
        {
            ImGui.TextWrapped(
                $"Preview: {draft.Rules.Count} old -> {partialPlan.Rules.Count} new; " +
                $"remove {partialPlan.RemovedCount}, add {partialPlan.AddedCount}.");
        }

        var canApply = preview.ReplaceAll
                       || ((selectedGroups > 0 || preview.IncludeGlobals)
                           && filterPreviewValid
                           && partialPlan is not null);
        using (new ImGuiDisabledBlock(!canApply))
        {
            var applyLabel = preview.ReplaceAll
                ? "Replace All"
                : $"Apply Partial Import ({selectedGroups} groups)";
            if (ImGui.Button(applyLabel))
            {
                if (preview.ReplaceAll)
                    ApplyFullManifestImport(preview);
                else
                    ApplyPartialManifestImport(preview);

                importPreview = null;
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            importPreview = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private List<ManifestImportGroup> BuildImportGroups(ObjectPriorityRuleManifest manifest)
    {
        var groupsByKey = new Dictionary<string, ManifestImportGroup>(StringComparer.OrdinalIgnoreCase);
        for (var ruleIndex = 0; ruleIndex < manifest.Rules.Count; ruleIndex++)
        {
            var rule = manifest.Rules[ruleIndex];
            var descriptor = ResolveImportGroup(rule);
            if (!groupsByKey.TryGetValue(descriptor.Key, out var group))
            {
                group = new ManifestImportGroup(
                    descriptor.Key,
                    descriptor.DisplayLabel,
                    descriptor.Detail,
                    descriptor.Kind,
                    ruleIndex);
                groupsByKey.Add(descriptor.Key, group);
            }

            group.Rules.Add(rule);
        }

        return groupsByKey.Values
            .OrderBy(group => group.Kind)
            .ThenBy(group => group.DisplayLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private ImportGroupDescriptor ResolveImportGroup(ObjectPriorityRule rule)
    {
        var dutyName = NormalizeEditorText(rule.DutyEnglishName);
        if (string.IsNullOrWhiteSpace(dutyName)
            && rule.ContentFinderConditionId == 0
            && rule.TerritoryTypeId == 0)
        {
            return new ImportGroupDescriptor(
                "no-duty/global",
                "[No duty / global]",
                "No duty name, CFC, or territory scope.",
                ImportGroupKind.NoDuty);
        }

        var matches = plugin.DutyCatalogService.Entries
            .Where(entry => DutyRuleCoverageHelper.RuleAssociatesWithDuty(rule, entry))
            .Take(2)
            .ToList();
        if (matches.Count == 1)
        {
            var duty = matches[0];
            var canonicalKey = duty.ContentFinderConditionId != 0
                ? $"duty:cfc:{duty.ContentFinderConditionId}"
                : $"duty:terr:{duty.TerritoryTypeId}";
            return new ImportGroupDescriptor(
                canonicalKey,
                duty.EnglishName,
                $"Canonical duty: CFC {duty.ContentFinderConditionId}, Terr {duty.TerritoryTypeId}.",
                ImportGroupKind.CanonicalDuty);
        }

        var rawScope = BuildRawImportScopeLabel(rule, dutyName);
        var unresolvedKey = $"unresolved:cfc:{rule.ContentFinderConditionId}:terr:{rule.TerritoryTypeId}:name:{dutyName}";
        var reason = matches.Count == 0
            ? "no catalog duty resolves by CFC, unique territory, or normalized English name"
            : "multiple catalog duties resolve from the supplied fallback scope";
        return new ImportGroupDescriptor(
            unresolvedKey,
            $"[Unresolved] {rawScope}",
            $"{rawScope}; {reason}.",
            ImportGroupKind.Unresolved);
    }

    private static string NormalizeDutyImportName(string? value)
    {
        var normalized = NormalizeEditorText(value);
        return normalized.StartsWith("the ", StringComparison.OrdinalIgnoreCase)
            ? normalized[4..]
            : normalized;
    }

    private static string BuildRawImportScopeLabel(ObjectPriorityRule rule, string dutyName)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(dutyName))
            parts.Add(dutyName);
        if (rule.ContentFinderConditionId != 0)
            parts.Add($"CFC {rule.ContentFinderConditionId}");
        if (rule.TerritoryTypeId != 0)
            parts.Add($"Terr {rule.TerritoryTypeId}");
        return parts.Count == 0 ? "blank scope" : string.Join(" / ", parts);
    }

    private void ApplyFullManifestImport(ManifestImportPreview preview)
    {
        InvalidateUndoState();
        draft = CloneManifest(preview.Manifest);
        dirty = true;
        ClearDraftReferenceState();
        draftStructureChangedThisDraw = true;
        editorStatus = $"Replaced the full {selectedPresetName} draft with {draft.Rules.Count} rule(s) from the {preview.SourceLabel} preview. Press Save to persist it.";
    }

    private void ApplyPartialManifestImport(ManifestImportPreview preview)
    {
        var plan = BuildPartialImportPlan(preview);
        if (plan is null)
            return;

        CaptureUndoState($"partial {preview.Mode} import");
        draft.Rules = plan.Rules;
        dirty = true;
        PruneDraftReferenceState();
        pendingScrollRule = null;
        dutySearch = string.Empty;
        dutySearchRow = -1;
        draftStructureChangedThisDraw = true;
        editorStatus = $"Applied {preview.Mode} import from {preview.SourceLabel}: removed {plan.RemovedCount}, added {plan.AddedCount}, now {plan.Rules.Count} rules. Press Save to persist it.";
    }

    private RulePartialImportPlan? BuildPartialImportPlan(ManifestImportPreview preview)
    {
        if (preview.Mode == RulePartialImportMode.CurrentFilter
            && !string.Equals(preview.FrozenFilterSignature, BuildFilterSignature(), StringComparison.Ordinal))
            return null;

        var groups = preview.Groups
            .OrderBy(group => group.FirstRuleIndex)
            .Select(group => new RulePartialImportGroup(group.Key, group.Kind == ImportGroupKind.NoDuty, group.Rules))
            .ToList();
        var selectedKeys = preview.Groups
            .Where(group => group.Selected && group.Kind != ImportGroupKind.NoDuty)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return PartialRuleImportPlanner.Build(
            draft.Rules,
            groups,
            selectedKeys,
            preview.IncludeGlobals,
            preview.Mode,
            preview.FrozenFilterRuleIndices,
            rule => ResolveImportGroup(rule).Key,
            DutyRuleCoverageHelper.IsGlobalRule);
    }

    private void AddDraftRule(ObjectPriorityRule rule, string status)
    {
        InvalidateUndoState();
        InvalidateImportPreview();
        draft.Rules.Add(rule);
        unsavedNewRules.Add(rule);
        pendingScrollRule = rule;
        dirty = true;
        draftStructureChangedThisDraw = true;
        editorStatus = status;
    }

    private void MarkOrdinaryEdit()
    {
        dirty = true;
        InvalidateUndoState();
        InvalidateImportPreview();
    }

    private void DeleteSelectedRules()
    {
        var rulesToDelete = draft.Rules.Where(selectedRules.Contains).ToHashSet();
        if (rulesToDelete.Count == 0)
        {
            selectedRules.Clear();
            return;
        }

        CaptureUndoState($"delete ({rulesToDelete.Count} rule(s))");
        InvalidateImportPreview();
        draft.Rules.RemoveAll(rulesToDelete.Contains);
        unsavedNewRules.RemoveWhere(rulesToDelete.Contains);
        selectedRules.Clear();
        pendingScrollRule = null;
        dutySearch = string.Empty;
        dutySearchRow = -1;
        dirty = true;
        draftStructureChangedThisDraw = true;
        editorStatus = $"Deleted {rulesToDelete.Count} selected rule(s). Undo remains available until the next invalidating action.";
    }

    private void CaptureUndoState(string label)
    {
        var unsavedRuleIndices = Enumerable.Range(0, draft.Rules.Count)
            .Where(index => unsavedNewRules.Contains(draft.Rules[index]))
            .ToList();
        undoState.Capture(draft, dirty, unsavedRuleIndices, label);
    }

    private void RestoreUndoState()
    {
        if (!undoState.TryTake(out var state))
            return;

        draft = CloneManifest(state.Manifest);
        dirty = state.WasDirty;
        unsavedNewRules.Clear();
        foreach (var ruleIndex in state.UnsavedRuleIndices.Where(index => index >= 0 && index < draft.Rules.Count))
            unsavedNewRules.Add(draft.Rules[ruleIndex]);
        selectedRules.Clear();
        pendingScrollRule = null;
        dutySearch = string.Empty;
        dutySearchRow = -1;
        knownLayerSelectorsByTerritory.Clear();
        draftStructureChangedThisDraw = true;
        editorStatus = $"Undid {state.Label}.";
    }

    private void InvalidateUndoState()
        => undoState.Invalidate();

    private void InvalidateImportPreview()
    {
        importPreview = null;
        openImportPreview = false;
    }

    private void ClearDraftReferenceState()
    {
        unsavedNewRules.Clear();
        selectedRules.Clear();
        pendingScrollRule = null;
        dutySearch = string.Empty;
        dutySearchRow = -1;
        knownLayerSelectorsByTerritory.Clear();
    }

    private void PruneDraftReferenceState()
    {
        var liveRules = draft.Rules.ToHashSet();
        unsavedNewRules.RemoveWhere(rule => !liveRules.Contains(rule));
        selectedRules.RemoveWhere(rule => !liveRules.Contains(rule));
    }

    private void SyncDiskTransferPath()
        => diskTransferPath = plugin.ObjectPriorityRuleService.GetPresetPath(selectedPresetName);

    internal static ObjectPriorityRuleManifest CloneManifest(ObjectPriorityRuleManifest manifest)
        => new()
        {
            SchemaVersion = manifest.SchemaVersion,
            Description = manifest.Description,
            Rules = manifest.Rules.Select(CloneRule).ToList(),
        };

    private static ObjectPriorityRule CloneRule(ObjectPriorityRule rule)
        => new()
        {
            Enabled = rule.Enabled,
            TerritoryTypeId = rule.TerritoryTypeId,
            ContentFinderConditionId = rule.ContentFinderConditionId,
            DutyEnglishName = rule.DutyEnglishName,
            Alliance = rule.Alliance,
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

            var replacedRule = draft.Rules[ruleIndex];
            var wasSelected = selectedRules.Remove(replacedRule);
            var wasUnsavedNew = unsavedNewRules.Remove(replacedRule);
            draft.Rules[ruleIndex] = importedRule;
            if (wasSelected)
                selectedRules.Add(importedRule);
            if (wasUnsavedNew)
                unsavedNewRules.Add(importedRule);
            if (ReferenceEquals(pendingScrollRule, replacedRule))
                pendingScrollRule = importedRule;
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

    private sealed class ManifestImportPreview(
        ObjectPriorityRuleManifest manifest,
        IReadOnlyList<ManifestImportGroup> groups,
        string sourceLabel,
        string sourceStatus,
        IReadOnlyList<int> frozenFilterRuleIndices,
        string frozenFilterSignature)
    {
        public ObjectPriorityRuleManifest Manifest { get; } = manifest;

        public IReadOnlyList<ManifestImportGroup> Groups { get; } = groups;

        public string SourceLabel { get; } = sourceLabel;

        public string SourceStatus { get; } = sourceStatus;

        public IReadOnlyList<int> FrozenFilterRuleIndices { get; } = frozenFilterRuleIndices;

        public string FrozenFilterSignature { get; } = frozenFilterSignature;

        public bool ReplaceAll { get; set; }

        public RulePartialImportMode Mode { get; set; }

        public bool IncludeGlobals { get; set; }
    }

    private sealed class ManifestImportGroup(
        string key,
        string displayLabel,
        string detail,
        ImportGroupKind kind,
        int firstRuleIndex)
    {
        public string Key { get; } = key;

        public string DisplayLabel { get; } = displayLabel;

        public string Detail { get; } = detail;

        public ImportGroupKind Kind { get; } = kind;

        public int FirstRuleIndex { get; } = firstRuleIndex;

        public List<ObjectPriorityRule> Rules { get; } = [];

        public bool Selected { get; set; }
    }

    private readonly record struct ImportGroupDescriptor(
        string Key,
        string DisplayLabel,
        string Detail,
        ImportGroupKind Kind);

    private readonly record struct PresetFileState(
        bool Exists,
        DateTime? LastWriteUtc);

    private readonly record struct SelectionImpact(
        IReadOnlyList<string> Duties,
        int GlobalRows,
        int UnresolvedRows);

    private enum ImportGroupKind
    {
        NoDuty,
        CanonicalDuty,
        Unresolved,
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
