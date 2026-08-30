using ADS.Models;
using ADS.Services;

namespace ADS.Tests;

public sealed class PartialRuleImportPlannerTests
{
    [Fact]
    public void CompleteDutyModeReplacesTheWholeSelectedGroup()
    {
        var current = new[] { Rule("A", "old-1"), Rule("A", "old-2"), Rule("B", "keep"), Rule("", "global") };
        var groups = new[] { Group("A", false, Rule("A", "new")) };

        var plan = PartialRuleImportPlanner.Build(current, groups, new HashSet<string> { "A" }, false, RulePartialImportMode.CompleteDuties, [], Key, IsGlobal);

        Assert.NotNull(plan);
        Assert.Equal(2, plan.RemovedCount);
        Assert.Equal(1, plan.AddedCount);
        Assert.Equal(new[] { "new", "keep", "global" }, plan.Rules.Select(rule => rule.ObjectName));
    }

    [Fact]
    public void DeltaModeAppendsExactRowsWithoutDeduplication()
    {
        var current = new[] { Rule("A", "same") };
        var groups = new[] { Group("A", false, Rule("A", "same"), Rule("A", "same")) };

        var plan = PartialRuleImportPlanner.Build(current, groups, new HashSet<string> { "A" }, false, RulePartialImportMode.Delta, [], Key, IsGlobal);

        Assert.NotNull(plan);
        Assert.Equal(3, plan.Rules.Count);
        Assert.All(plan.Rules, rule => Assert.Equal("same", rule.ObjectName));
    }

    [Fact]
    public void CurrentFilterModeReplacesOnlyTheFrozenIndices()
    {
        var current = new[] { Rule("A", "zero"), Rule("A", "one"), Rule("B", "two"), Rule("B", "three") };
        var groups = new[] { Group("A", false, Rule("A", "replacement")) };

        var plan = PartialRuleImportPlanner.Build(current, groups, new HashSet<string> { "A" }, false, RulePartialImportMode.CurrentFilter, [1, 2], Key, IsGlobal);

        Assert.NotNull(plan);
        Assert.Equal(2, plan.RemovedCount);
        Assert.Equal(new[] { "zero", "replacement", "three" }, plan.Rules.Select(rule => rule.ObjectName));
    }

    [Fact]
    public void GlobalsRequireSeparateOptIn()
    {
        var current = new[] { Rule("A", "keep") };
        var groups = new[] { Group("global", true, Rule("", "incoming-global")) };

        Assert.Null(PartialRuleImportPlanner.Build(current, groups, new HashSet<string>(), false, RulePartialImportMode.Delta, [], Key, IsGlobal));
        var included = PartialRuleImportPlanner.Build(current, groups, new HashSet<string>(), true, RulePartialImportMode.Delta, [], Key, IsGlobal);
        Assert.NotNull(included);
        Assert.Equal(new[] { "keep", "incoming-global" }, included.Rules.Select(rule => rule.ObjectName));
    }

    [Fact]
    public void CurrentFilterModeDoesNotRemoveExistingGlobalsWithoutOptIn()
    {
        var current = new[] { Rule("A", "duty"), Rule("", "global") };
        var groups = new[] { Group("A", false, Rule("A", "replacement")) };

        var protectedPlan = PartialRuleImportPlanner.Build(current, groups, new HashSet<string> { "A" }, false, RulePartialImportMode.CurrentFilter, [0, 1], Key, IsGlobal);
        var optedInPlan = PartialRuleImportPlanner.Build(current, groups, new HashSet<string> { "A" }, true, RulePartialImportMode.CurrentFilter, [0, 1], Key, IsGlobal);

        Assert.NotNull(protectedPlan);
        Assert.Equal(new[] { "replacement", "global" }, protectedPlan.Rules.Select(rule => rule.ObjectName));
        Assert.NotNull(optedInPlan);
        Assert.Equal(new[] { "replacement" }, optedInPlan.Rules.Select(rule => rule.ObjectName));
    }

    [Fact]
    public void OneStepUndoRestoresOnceAndInvalidatesExplicitly()
    {
        var undo = new OneStepRuleManifestUndo();
        var manifest = new ObjectPriorityRuleManifest { Rules = [Rule("A", "before")] };
        undo.Capture(manifest, wasDirty: false, [0], "delete");
        manifest.Rules[0].ObjectName = "after";

        Assert.True(undo.TryTake(out var restored));
        Assert.Equal("before", restored.Manifest.Rules[0].ObjectName);
        Assert.False(undo.TryTake(out _));

        undo.Capture(manifest, wasDirty: true, [], "import");
        undo.Invalidate();
        Assert.False(undo.CanUndo);
    }

    [Fact]
    public void UndoAfterSaveRestoresAsDirty()
    {
        var undo = new OneStepRuleManifestUndo();
        var manifest = new ObjectPriorityRuleManifest { Rules = [Rule("A", "before")] };
        undo.Capture(manifest, wasDirty: false, [], "delete");

        undo.MarkRestoreDirty();

        Assert.True(undo.TryTake(out var restored));
        Assert.True(restored.WasDirty);
    }

    private static RulePartialImportGroup Group(string key, bool global, params ObjectPriorityRule[] rules)
        => new(key, global, rules);

    private static ObjectPriorityRule Rule(string duty, string name)
        => new() { DutyEnglishName = duty, ObjectName = name };

    private static string Key(ObjectPriorityRule rule)
        => string.IsNullOrWhiteSpace(rule.DutyEnglishName) ? "global" : rule.DutyEnglishName;

    private static bool IsGlobal(ObjectPriorityRule rule)
        => string.IsNullOrWhiteSpace(rule.DutyEnglishName);
}
