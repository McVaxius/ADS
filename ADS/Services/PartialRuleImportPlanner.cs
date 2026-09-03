using ADS.Models;

namespace ADS.Services;

internal enum RulePartialImportMode
{
    CompleteDuties,
    Delta,
    CurrentFilter,
}

internal readonly record struct RulePartialImportGroup(
    string Key,
    bool IsGlobal,
    IReadOnlyList<ObjectPriorityRule> Rules);

internal sealed record RulePartialImportPlan(
    List<ObjectPriorityRule> Rules,
    int RemovedCount,
    int AddedCount);

internal static class PartialRuleImportPlanner
{
    public static RulePartialImportPlan? Build(
        IReadOnlyList<ObjectPriorityRule> currentRules,
        IReadOnlyList<RulePartialImportGroup> incomingGroups,
        IReadOnlySet<string> selectedGroupKeys,
        bool includeGlobals,
        RulePartialImportMode mode,
        IReadOnlyCollection<int> frozenFilterIndices,
        Func<ObjectPriorityRule, string> groupKeySelector,
        Func<ObjectPriorityRule, bool> isGlobalSelector)
    {
        var selectedGroups = incomingGroups
            .Where(group => (!group.IsGlobal && selectedGroupKeys.Contains(group.Key))
                            || (group.IsGlobal && includeGlobals))
            .ToList();
        if (selectedGroups.Count == 0)
            return null;

        var incoming = selectedGroups.SelectMany(group => group.Rules).Select(CloneRule).ToList();
        if (mode == RulePartialImportMode.Delta)
        {
            var appended = new List<ObjectPriorityRule>(currentRules.Count + incoming.Count);
            appended.AddRange(currentRules);
            appended.AddRange(incoming);
            return new RulePartialImportPlan(appended, 0, incoming.Count);
        }

        if (mode == RulePartialImportMode.CurrentFilter)
        {
            var frozen = frozenFilterIndices
                .Where(index => index >= 0 && index < currentRules.Count)
                .Where(index => includeGlobals || !isGlobalSelector(currentRules[index]))
                .ToHashSet();
            var insertAt = frozen.Count == 0 ? currentRules.Count : frozen.Min();
            var next = new List<ObjectPriorityRule>(currentRules.Count - frozen.Count + incoming.Count);
            for (var index = 0; index <= currentRules.Count; index++)
            {
                if (index == insertAt)
                    next.AddRange(incoming);
                if (index < currentRules.Count && !frozen.Contains(index))
                    next.Add(currentRules[index]);
            }
            return new RulePartialImportPlan(next, frozen.Count, incoming.Count);
        }

        var replacements = selectedGroups.ToDictionary(group => group.Key, StringComparer.OrdinalIgnoreCase);
        var inserted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var complete = new List<ObjectPriorityRule>(currentRules.Count);
        var removed = 0;
        foreach (var existing in currentRules)
        {
            var key = groupKeySelector(existing);
            if (!replacements.TryGetValue(key, out var replacement))
            {
                complete.Add(existing);
                continue;
            }

            removed++;
            if (inserted.Add(key))
                complete.AddRange(replacement.Rules.Select(CloneRule));
        }

        foreach (var replacement in selectedGroups)
        {
            if (inserted.Add(replacement.Key))
                complete.AddRange(replacement.Rules.Select(CloneRule));
        }

        return new RulePartialImportPlan(complete, removed, incoming.Count);
    }

    internal static ObjectPriorityRule CloneRule(ObjectPriorityRule rule)
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
            DebugCommand = rule.DebugCommand,
        };
}

internal sealed record RuleManifestUndoSnapshot(
    ObjectPriorityRuleManifest Manifest,
    bool WasDirty,
    IReadOnlyList<int> UnsavedRuleIndices,
    string Label);

internal sealed class OneStepRuleManifestUndo
{
    private RuleManifestUndoSnapshot? snapshot;

    public bool CanUndo => snapshot is not null;

    public string Label => snapshot?.Label ?? string.Empty;

    public void Capture(ObjectPriorityRuleManifest manifest, bool wasDirty, IReadOnlyList<int> unsavedRuleIndices, string label)
        => snapshot = new RuleManifestUndoSnapshot(
            new ObjectPriorityRuleManifest
            {
                SchemaVersion = manifest.SchemaVersion,
                Description = manifest.Description,
                Rules = manifest.Rules.Select(PartialRuleImportPlanner.CloneRule).ToList(),
            },
            wasDirty,
            unsavedRuleIndices.ToList(),
            label);

    public bool TryTake(out RuleManifestUndoSnapshot value)
    {
        if (snapshot is null)
        {
            value = null!;
            return false;
        }

        value = snapshot;
        snapshot = null;
        return true;
    }

    public void MarkRestoreDirty()
    {
        if (snapshot is not null)
            snapshot = snapshot with { WasDirty = true };
    }

    public void Invalidate()
        => snapshot = null;
}
